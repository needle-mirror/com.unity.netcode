#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using UnityEngine;


namespace Unity.NetCode
{
    internal struct GhostCleanup : ICleanupComponentData
    {
        public int ghostId;
        public NetworkTick spawnTick;
        public NetworkTick despawnTick;
    }

    /// <summary>
    /// For internal use only, struct used to pass some data to the code-generate ghost serializer.
    /// </summary>
    public struct GhostSerializerState
    {
        /// <summary>
        /// A readonly accessor to retrieve the <see cref="GhostInstance"/> from an entity reference. Used to
        /// serialize a ghost entity reference.
        /// </summary>
        public ComponentLookup<GhostInstance> GhostFromEntity;
    }

    internal struct GhostSystemConstants
    {
        /// <summary>
        ///     The number of ghost snapshots stored internally by the server in the <see cref="GhostChunkSerializationState" />,
        ///     and by the client in the <see cref="SnapshotDataBuffer" /> ring buffer.
        ///     Reducing the SnapshotHistorySize would reduce the cost of storage on both server and client, but will
        ///     affect the server's ability to effectively delta-compress data.
        ///     This is because; based on the client latency, by the time the server receives the snapshot acks (inside the
        ///     client command stream), the slot in which the acked data was stored could have been overwritten.
        ///     The default of 32 is designed to work with a round trip time of about 500ms at a 60hz NetworkTickRate,
        ///     where the server is sending a single connection the same dynamic ghost every tick.
        /// </summary>
        /// <remarks>
        ///     32 (the default) is designed to work with a round trip time of about 500ms at a 60hz NetworkTickRate,
        ///     where the server is sending a single connection the same dynamic ghost every tick.
        ///     <br />
        ///     <c>NETCODE_SNAPSHOT_HISTORY_SIZE_16</c> is a good middle-ground between size-reduction (for static ghosts)
        ///     and ack availability (for dynamic ghosts). Recommended for projects where the highest <see cref="GhostPrefabCreation.Config.MaxSendRate" />
        ///     is 30Hz, or where the <see cref="ClientServerTickRate.NetworkTickRate" /> is 30.
        ///     <br />
        ///     <c>NETCODE_SNAPSHOT_HISTORY_SIZE_6</c> is best suited for larger scale projects (i.e. hundreds of dynamic
        ///     ghosts, thousands of static ghosts, and where the player character controller is already sent at a
        ///     significantly lower frequency due to congestion or <see cref="GhostPrefabCreation.Config.MaxSendRate" />).
        /// </remarks>
        public const int SnapshotHistorySize =
#if NETCODE_SNAPSHOT_HISTORY_SIZE_6
            6;
#elif NETCODE_SNAPSHOT_HISTORY_SIZE_16
            16;
#else
            32;
#endif
        /// <summary>At most, around half the snapshot can consist of new prefabs to use.</summary>
        public const uint MaxNewPrefabsPerSnapshot = 32u;
        /// <summary>
        /// Prepend to all serialized ghosts in the snapshot their compressed size. This can be used by the client
        /// to recover from error condition and to skip ghost data in some situation, for example transitory condition
        /// while streaming in/out scenes.
        /// </summary>
        public const bool SnapshotHasCompressedGhostSize = true;
        /// <summary>
        /// The maximum age of a baseline. If a baseline is older than this limit it will not be used
        /// for delta compression.
        /// </summary>
        /// <remarks>
        /// The index part of a network tick is 31 bits, at most 30 bits can be used without producing negative
        /// values in TicksSince due to wrap around. This adds a margin of 2 bits to that limit.
        /// </remarks>
        public const uint MaxBaselineAge = 1u<<28;

        /// <summary>Maximum number of snapshot send attempts, which kicks in after we fail to fit even a single ghost into the snapshot.</summary>
        /// <remarks>After each attempt, we double the packet size, meaning our last attempt is a snapshot <c>2^(8-1) i.e. 128x</c> larger than configured.</remarks>
        public const int MaxSnapshotSendAttempts = 8;

        /// Minimum value for <see cref="GhostSendSystemData.DefaultSnapshotPacketSize"/>, if configured.
        internal const int MinSnapshotPacketSize = 100;
        /// Minimum value for <see cref="GhostSendSystemData.PercentReservedForDespawnMessages"/>.
        internal const float MinPercentReservedForDespawnMessages = .2f;
        /// Maximum value for <see cref="GhostSendSystemData.PercentReservedForDespawnMessages"/>.
        internal const float MaxPercentReservedForDespawnMessages = .8f;
    }

#if UNITY_EDITOR
    internal struct GhostSendSystemAnalyticsData : IComponentData
    {
        public NativeArray<uint> UpdateLenSums;
        public NativeArray<uint> NumberOfUpdates;
    }
#endif


    /// <summary>
    /// Singleton entity that contains all the tweakable settings for the <see cref="GhostSendSystem"/>.
    /// </summary>
    [Serializable]
    public struct GhostSendSystemData : IComponentData
    {
        /// <summary>
        /// Non-zero values for <see cref="MinSendImportance"/> can cause both:
        /// a) 'unchanged chunks that are "new" to a new-joiner' and b) 'newly spawned chunks'
        /// to be ignored by the replication priority system for multiple seconds.
        /// If this behaviour is undesirable, set this to be above <see cref="MinSendImportance"/>.
        /// This multiplies the importance value used on those "new" (to the player or to the world) ghost chunks.
        /// Note: This does not guarantee delivery of all "new" chunks,
        /// it only guarantees that every ghost chunk will get serialized and sent at least once per connection,
        /// as quickly as possible (e.g. assuming you have the bandwidth for it).
        /// </summary>
        public uint FirstSendImportanceMultiplier
        {
            get => m_FirstSendImportanceMultiplier;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(FirstSendImportanceMultiplier));
#endif
                m_FirstSendImportanceMultiplier = value;
            }
        }

        /// <summary>
        /// If not 0, denotes the desired size of an individual snapshot (unless the per-connection <see cref="NetworkStreamSnapshotTargetSize"/> component is present).
        /// If zero, <see cref="NetworkParameterConstants.MTU"/> is used (minus headers).
        /// Minimum value is <see cref="GhostSystemConstants.MinSnapshotPacketSize"/>.
        /// </summary>
        [Tooltip("- If zero (the default), <b>NetworkParameterConstants.MTU</b> is used (minus headers).\n\n - Otherwise, denotes the desired size of an individual snapshot (unless the per-connection <b>NetworkStreamSnapshotTargetSize</b> component is present).")]
        [Min(0)]
        public int DefaultSnapshotPacketSize;

        /// <summary>
        /// Denotes the maximum percentage of the snapshot's capacity that can be used for despawn messages.
        /// The default is 33% (i.e. one third of a snapshot), though we recommend around 75% for large scale games.
        /// </summary>
        /// <remarks>
        /// Delta-compression of despawn <see cref="GhostInstance.ghostId"/>'s improves with despawn count.
        /// Note that - due to importance scaling - it can take many ticks for the <see cref="GhostSendSystem"/> to
        /// circle around to any given chunk, to recognise that it needs to despawn ghosts within said chunk.
        /// Therefore, increasing <see cref="MaxIterateChunks"/> may help despawns register faster.
        /// </remarks>
        [Tooltip("Denotes the maximum percentage of the snapshot's capacity that can be used for despawn messages.\n\nThe default is 33% (i.e. one third of a snapshot), though we recommend up to 75% for large scale games.")]
        [Range(GhostSystemConstants.MinPercentReservedForDespawnMessages, GhostSystemConstants.MaxPercentReservedForDespawnMessages)]
        public float PercentReservedForDespawnMessages;

        /// <summary>
        /// The minimum importance considered for inclusion in a snapshot. Any ghost chunk with an importance value lower
        /// than this value will not be added to the snapshot, even if there is enough space in the packet.
        /// </summary>
        /// <remarks>
        /// As of 1.4, prefer <see cref="GhostPrefabCreation.Config.MaxSendRate"/>, which you can author via the `GhostAuthoringComponent`.
        /// Counted on a per-connection, per-chunk basis, where importance increases by the Importance value every tick, until sent (NOT confirmed delivered).
        /// E.g. <c>MinSendImportance=60, SimulationTickRate=60, GhostAuthoringComponent.Importance=1</c> implies a ghost will be replicated roughly once per second.
        /// </remarks>
        [Tooltip("The minimum importance considered for inclusion in a snapshot. The Defaults to 0 (disabled).\n\nAny ghost chunk with an importance value lower than this value will not be added to the snapshot, even if there is enough space in the packet. Use to reduce send-rate for low-importance ghosts.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        public int MinSendImportance;

        /// <summary>
        /// The minimum importance considered for inclusion in a snapshot after applying distance based
        /// priority scaling to the ghost chunk. Any ghost chunk with a downscaled importance value lower
        /// than this will not be added to the snapshot, even if there is enough space in the packet.
        /// </summary>
        [Tooltip("The minimum importance considered for inclusion in a snapshot after applying distance based priority scaling to the ghost chunk. Any ghost chunk with a downscaled importance value lower than this will not be added to the snapshot, even if there is enough space in the packet.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        public int MinDistanceScaledSendImportance;

        /// <summary>
        ///     Denotes the maximum number of chunks the <see cref="GhostSendSystem" /> will iterate over in a single
        ///     tick, for a given connection, within a single <see cref="ClientServerTickRate.NetworkTickRate" />
        ///     snapshot send interval. It's an optimization in use-cases where you have many thousands of static ghosts
        ///     (and thus hundreds of static chunks which are iterated over unneccessarily to find ones containing possible changes).
        /// </summary>
        /// <remarks>
        ///     A positive value will clamp the maximum number of chunks we iterate over (but cannot be less than
        ///     <see cref="MaxSendChunks" />, thus clamped automatically to it).
        ///     Use 0 (the default) to denote that you want to use the <see cref="MaxSendChunks" /> value as the
        ///     <see cref="MaxIterateChunks"/> value (but note that this can lead to snapshot packets being less full than expected).
        ///     Use -1 to denote that you want to iterate until the packet is filled (or send rules like <see cref="MaxSendChunks" /> are encountered).
        ///     <br/>
        ///     <b>1st Warning:</b> If netcode cannot fill the packet within <see cref="MaxIterateChunks"/> chunks (for
        ///     any reason), any ghost chunks after this index will not be processed (even if there is still space in
        ///     the packet). Therefore, if you're encountering less-than-full packets in cases where you expect the packet
        ///     to be full, increase this!
        ///     <br/>
        ///     <b>2nd Warning:</b> <see cref="MaxIterateChunks"/> limits the number of chunks we process, and this filtering
        ///     is applied BEFORE we check if ghosts are irrelevant. Therefore, if <see cref="MaxIterateChunks"/> is 4 (for example),
        ///     and the 4 highest importance chunks ONLY contain irrelevant ghosts, we will NOT send ANY ghosts in this snapshot.
        ///     Therefore, we recommend setting <see cref="MaxIterateChunks"/> to a value at least 2x higher than <see cref="MaxSendChunks"/>.
        /// </remarks>
        [Tooltip("Denotes the maximum number of chunks the <b>GhostSendSystem</b> will iterate over in a single tick, for a given connection, within a single <b>NetworkTickRate</b> snapshot send interval.\n\nIt's an optimization in use-cases where you have many thousands of static ghosts (and thus hundreds of static chunks which are iterated over unneccessarily to find ones containing possible changes).\n\nDefaults to 0 (i.e. use <b>MinSendImportance</b>)\nRecommendation: ~10\n\n - A positive value will clamp the maximum number of chunks we iterate over (but cannot be less than <b>MaxSendChunks</b>, thus clamped automatically to it).\n - Use 0 to denote that <b>MaxIterateChunks</b> should use <b>MaxSendChunks</b>.\n\n - Use -1 to denote that you want to iterate until the packet is filled - or send rules (like <b>MaxSendChunks</b>) are encountered.")]
        [Min(0)]
        public int MaxIterateChunks;

        /// <summary>
        /// The maximum number of chunks the <see cref="GhostSendSystem"/> will add to the snapshot for any given connection,
        /// within a single <see cref="ClientServerTickRate.NetworkTickRate"/> snapshot send interval. Only incremented
        /// when at least one ghost is added to the snapshot for a chunk.
        /// <br/>
        /// <b>Warning</b>: <see cref="MaxSendChunks"/> may lead to unnecessarily empty snapshot packets, in cases where
        /// adding this many chunks to the snapshot does not completely fill it. See <see cref="MaxIterateChunks"/> for resolution.
        /// </summary>
        [Tooltip("The maximum number of chunks the GhostSendSystem will add to the snapshot for any given connection, within a single NetworkTickRate snapshot send interval. Only incremented when at least one ghost is added to the snapshot for a chunk. Warning: <b>MaxSendChunks</b> may lead to unnecessarily empty snapshot packets, in cases where adding this many chunks to the snapshot does not completely fill it. See <b>MaxIterateChunks</b> for resolution.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        public int MaxSendChunks;

        /// <summary>
        /// The maximum number of entities the <see cref="GhostSendSystem"/> will add to the snapshot for any given connection,
        /// within a single <see cref="ClientServerTickRate.NetworkTickRate"/> snapshot send interval.
        /// Ignores irrelevant ghosts and cancelled sends (e.g. zero change static optimized chunks).
        /// This can be used to reduce / control CPU time on the server.
        /// <b>Warning</b>: <see cref="MaxSendChunks"/> may lead to unnecessarily empty snapshot packets, in cases where
        /// adding this many entities to the snapshot does not completely fill it.
        /// Prefer <see cref="MaxSendChunks"/> and <see cref="MaxIterateChunks"/>.
        /// </summary>
        /// <remarks>
        ///     An implementation detail to be aware of here is that we can currently only check this value
        ///     after a chunk has been written (partially or in full) to the snapshot. Therefore, in practice, a value of 1
        ///     is equivalent to <c>MaxSendChunks = 1;</c>.
        /// </remarks>
        [Tooltip("<b>Obsolete: No longer functional!</b>\n\nThe maximum number of entities the <b>GhostSendSystem</b> will add to the snapshot for any given connection, within a single <b>NetworkTickRate</b> snapshot send interval. Ignores irrelevant ghosts and cancelled sends (e.g. zero change static optimized chunks). This can be used to reduce / control CPU time on the server.\n\n<b>Warning</b>: <b>MaxSendChunks</b> may lead to unnecessarily empty snapshot packets, in cases where adding this many entities to the snapshot does not completely fill it. Prefer <b>MaxSendChunks</b> and <b>MaxIterateChunks</b>.\n\nDefaults to 0 (OFF).")]
        [Min(0)]
        [ReadOnly]
        [Obsolete("No longer functional! Prefer MaxSendChunks and MaxIterateChunks to tweak GhostSendSystem CPU characteristics. (RemovedAfter 1.x)", false)]
        public int MaxSendEntities;

        /// <summary>
        /// Value used to scale down the importance of chunks where all entities were irrelevant last time it was sent.
        /// The importance is divided by this value. It can be used together with MinSendImportance to make sure
        /// relevancy is not updated every frame for things with low importance.
        /// </summary>
        public int IrrelevantImportanceDownScale
        {
            get => m_IrrelevantImportanceDownScale;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(IrrelevantImportanceDownScale));
#endif
                m_IrrelevantImportanceDownScale = value;
            }
        }

        /// <summary>
        /// We multiply every chunks priority by this value (default: 1k) just before passing said chunks to ghost importance
        /// scaling function pointers, to allow said scaling functions to play with -- and therefore return -- better,
        /// more fine-grained values.
        /// </summary>
        public ushort ImportanceScalingMultiplier
        {
            get { return m_ImportanceScalingMultiplier; }
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(IrrelevantImportanceDownScale));
#endif
                m_ImportanceScalingMultiplier = value;
            }
        }
        [Tooltip("We multiply every chunks priority by this value (default: 1k) just before passing said chunks to ghost importance scaling function pointers, to allow said scaling functions to play with -- and therefore return -- better, more fine-grained values.")]
        [Min(1)]
        internal ushort m_ImportanceScalingMultiplier;

        /// <summary>
        /// Force all ghosts to use a single snapshot delta-compression value prediction baseline. This will reduce CPU
        /// usage at the expense of increased bandwidth usage. This is mostly meant as a way of measuring which ghosts
        /// should use static optimization instead of dynamic. If the bits / ghost does not significantly increase when
        /// enabling this the ghost can use static optimization to save CPU.
        /// </summary>
        public bool ForceSingleBaseline
        {
            get { return m_ForceSingleBaseline; }
            set { m_ForceSingleBaseline = value; }
        }
        [Tooltip("Force all ghosts to use a single snapshot delta-compression value prediction baseline. This will reduce CPU usage at the expense of increased bandwidth usage.\n\nDefaults to false (no).\n\nThis is mostly meant as a way of measuring which ghosts should use static optimization instead of dynamic. If the bits / ghost does not significantly increase when enabling this the ghost can use static optimization to save CPU.")]
        [SerializeField]
        internal bool m_ForceSingleBaseline;

        /// <summary>
        /// Debug Feature: Force all ghosts to use pre-serialization. This means part of the serialization will be done once for
        /// all connection, instead of once per-connection. This can increase CPU time for simple ghosts and ghosts
        /// which are rarely sent. This switch is meant as a DEBUG feature, providing a way of measuring which ghosts
        /// would benefit from using pre-serialization.
        /// </summary>
        /// <remarks>Should not be enabled in Production!</remarks>
        public bool ForcePreSerialize
        {
            get { return m_ForcePreSerialize; }
            set { m_ForcePreSerialize = value; }
        }
        [Tooltip("DEBUG FEATURE: Force all ghosts to use pre-serialization. This means part of the serialization will be done once for all connection, instead of once per-connection.\n\nDefaults to false (don't).\n\nThis can increase CPU time for simple ghosts and ghosts which are rarely sent. This switch is meant as a way of measuring which ghosts would benefit from using pre-serialization.\n\n<b>Should not be enabled in Production builds!</b>")]
        [SerializeField]
        internal bool m_ForcePreSerialize;

        /// <summary>
        /// Try to keep the snapshot history buffer for an entity when there is a structural change.
        /// Doing this will require a lookup and copy of data whenever a ghost has a structural change,
        /// which will add additional CPU cost on the server.
        /// Keeping the snapshot history will not always be possible, so, this flag does no give a 100% guarantee,
        /// and you are expected to measure CPU and bandwidth when changing this.
        /// </summary>
        public bool KeepSnapshotHistoryOnStructuralChange
        {
            get { return m_KeepSnapshotHistoryOnStructuralChange; }
            set { m_KeepSnapshotHistoryOnStructuralChange = value; }
        }
        [Tooltip("Try to keep the snapshot history buffer for an entity when there is a structural change. Doing this will require a lookup and copy of data whenever a ghost has a structural change, which will add additional CPU cost on the server.\n\nDefaults to true (do).\n\nKeeping the snapshot history will not always be possible, so, this flag does no give a 100% guarantee, and you are expected to measure CPU and bandwidth when changing this.")]
        [SerializeField]
        internal bool m_KeepSnapshotHistoryOnStructuralChange;

        /// <summary>
        /// Enable profiling scopes for each component in a ghost.
        /// This can help track down why a ghost is expensive to serialize - but it comes with a performance cost, so is not enabled by default.
        /// </summary>
        public bool EnablePerComponentProfiling
        {
            get { return m_EnablePerComponentProfiling; }
            set { m_EnablePerComponentProfiling = value; }
        }

        [Tooltip("Enable profiling scopes for each component in a ghost. This can help track down why a ghost is expensive to serialize - but it comes with a performance cost, so is not enabled by default.")]
        [SerializeField]
        internal bool m_EnablePerComponentProfiling;

        /// <summary>
        /// The number of connections to cleanup unused serialization data for, in a single tick.
        /// Setting this higher can recover memory faster, but uses more CPU time.
        /// </summary>
        [Tooltip("The number of connections to cleanup unused serialization data for, in a single tick. Setting this higher can recover memory faster, but uses more CPU time.\n\nDefaults to 1.")]
        [Min(1)]
        public int CleanupConnectionStatePerTick;

        [Tooltip("This multiplies the importance value used on new (new to the player, or new to the world) ghost chunks.\n\nDefaults to 1 (OFF).\n\nNon-zero values for MinSendImportance can cause both: a) 'unchanged chunks that are new to a new-joiner' and b) 'newly spawned chunks' to be ignored by the replication priority system for multiple seconds. If this behaviour is undesirable, set this to be above MinSendImportance.\n\nNote: This does not guarantee delivery of all new chunks, it only guarantees that every ghost chunk will get serialized and sent at least once per connection, as quickly as possible (e.g. assuming you have the bandwidth for it).")]
        [Min(1)]
        [SerializeField]
        uint m_FirstSendImportanceMultiplier;
        [Tooltip("Value used to scale down the importance of chunks where all entities were irrelevant last time it was sent. The importance is divided by this value.\n\nDefaults to 1 (OFF).\n\nIt can be used together with MinSendImportance to make sure relevancy is not updated every frame, for ghosts with low importance.")]
        [Min(1)]
        [SerializeField]
        int m_IrrelevantImportanceDownScale;

        /// <summary>
        /// Value used to set the initial size of the internal temporary stream in which
        /// ghost data is serialized. Using a small size will incur in extra serialization costs (because
        /// of multiple round of serialization), while using a larger size provide better performance (overall).
        /// The minimum size of this buffer is forced to be the initial capacity of the outgoing data
        /// stream (usually MaxMessageSize or larger for fragmented payloads).
        /// The suggested default (8kb), while extremely large in respect to the packet size, would allow the <see cref="GhostSendSystem"/>
        /// to be able to to write a large range of mid/small ghost entities types, with varying size (up to hundreds of bytes
        /// each), without incurring in extra serialization overhead.
        /// </summary>
        public int TempStreamInitialSize
        {
            get => m_TempStreamSize;
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(m_TempStreamSize));
#endif
                m_TempStreamSize = value;
            }
        }

        /// <summary>
        /// When set, enables support for using any registered <see cref="GhostPrefabCustomSerializer"/> to serialize ghost chunks.
        /// </summary>
        public int UseCustomSerializer
        {
            get => m_UseCustomSerializer ? 1 : 0;
            set => m_UseCustomSerializer = value > 0;
        }
        [Tooltip("Value used to set the initial size of the internal temporary stream in which ghost data is serialized.\n - Smaller sizes will incur in extra serialization costs (as it may need to be resized mid-serialization, causing multiple round of serialization).\n - Larger sizes provide better performance (overall).\n\nThe minimum size of this buffer is forced to be the initial capacity of the outgoing data stream (usually MaxMessageSize or larger for fragmented payloads).\n\nThe suggested default (8kb), while extremely large in respect to the packet size, would allow the GhostSendSystem to be able to to write a large range of mid/small ghost entities types, with varying size (up to hundreds of bytes each), without incurring in extra serialization overhead.")]
        [Range(2 * 1024, 10 * 1024)]
        [SerializeField]
        internal int m_TempStreamSize;
        [Tooltip("When set, enables support for using any registered GhostPrefabCustomSerializer to serialize ghost chunks.")]
        [SerializeField]
        internal bool m_UseCustomSerializer;

        internal void Initialize()
        {
            MinSendImportance = 0;
            MinDistanceScaledSendImportance = 0;
            PercentReservedForDespawnMessages = .33f;
            MaxSendChunks = 0;
            MaxIterateChunks = 0;
            ForceSingleBaseline = false;
            ForcePreSerialize = false;
            KeepSnapshotHistoryOnStructuralChange = true;
            EnablePerComponentProfiling = false;
            CleanupConnectionStatePerTick = 1;
            m_FirstSendImportanceMultiplier = 1;
            m_IrrelevantImportanceDownScale = 1;
            m_TempStreamSize = 8 * 1024;
            m_ImportanceScalingMultiplier = 1000;
        }
    }

    /// <summary>
    /// <para>
    /// System present only for servers worlds, and responsible to replicate ghost entities to the clients.
    /// The <see cref="GhostSendSystem"/> is one of the most complex system of the whole package and heavily rely on multi-thread jobs to dispatch ghosts to all connection as much as possible in parallel.
    /// </para>
    /// <para>
    /// Ghosts entities are replicated by sending a 'snapshot' of their state to the clients, at <see cref="ClientServerTickRate.NetworkTickRate"/> frequency.
    /// Snaphosts are streamed to the client when their connection is tagged with a <see cref="NetworkStreamInGame"/> component (we usually refere a connection with that tag as "in-game"),
    /// and transmitted using an unrealiable channel. To save bandwith, snapshosts are delta-compressed against the latest reported ones received by the client.
    /// By default, up to 3 baseline are used to delta-compress the data, by using a predictive compression scheme (see <see cref="GhostDeltaPredictor"/>). It is possible
    /// to reduce the number of baseline used (and CPU cycles) using the <see cref="GhostSendSystemData"/> settings.
    /// </para>
    /// <para>
    /// The GhostSendSystem is designed to send to each connection <b>one single packet per network update</b>. By default, the system will try to
    /// replicate to the clients all the existing ghost present in the world. When all ghosts cannot be serialized into the same packet,
    /// the enties are prioritized by their importance.
    /// </para>
    /// <para>
    /// The base ghost importance can be set at authoring time on the prefab (<see cref="Unity.NetCode.GhostAuthoringComponent"/>);
    /// At runtime the ghost importance is scaled based on:
    /// </para>
    /// <para>- age (the last time the entities has been sent)</para>
    /// <para>- scaled by distance, (see <see cref="GhostConnectionPosition"/>, <see cref="GhostDistanceImportance"/></para>
    /// <para>- scaled by custom scaling (see <see cref="GhostImportance"/></para>
    /// <para>
    /// Ghost entities are replicated on "per-chunk" basis; all ghosts for the same chunk, are replicated
    /// together. The importance, as well as the importance scaling, apply to whole chunk.
    /// </para>
    /// <para>
    /// The send system can also be configured to send multiple ghost packets per frame and to to use snaphost larger than the MaxMessageSize.
    /// In that case, the snapshot packet is sent using another unreliable channel, setup with a <see cref="FragmentationPipelineStage"/>.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    [BurstCompile]
    public partial struct GhostSendSystem : ISystem
    {
        NativeParallelHashMap<RelevantGhostForConnection, int> m_GhostRelevancySet;

        EntityQuery ghostQuery;
        EntityQuery ghostSpawnQuery;
        EntityQuery ghostDespawnQuery;
        EntityQuery prespawnSharedComponents;

        EntityQueryMask internalGlobalRelevantQueryMask;
        EntityQueryMask netcodeEmptyQuery;

        EntityQuery connectionQuery;

        NativeQueue<int> m_FreeGhostIds;
        NativeArray<int> m_AllocatedGhostIds;
        NativeList<int> m_DestroyedPrespawns;
        NativeQueue<int> m_DestroyedPrespawnsQueue;
        NativeReference<NetworkTick> m_OldestPendingDespawnTickByAll;
#if UNITY_EDITOR
        NativeArray<uint> m_UpdateLen;
        NativeArray<uint> m_UpdateCounts;
#endif

        NativeList<ConnectionStateData> m_ConnectionStates;
        JobHandle m_ConnectionStatesJobHandle;

        /// <summary>
        /// Internal api used by PlayModeTool's ImportanceDrawerSystem.
        /// Returns the connectionStateData for a given connectionEntity.
        /// </summary>
        /// <param name="connectionEntity">The connectionEntity for which to get the connectionStateData.</param>
        /// <returns>
        /// A jobHandle used to access the ConnectionStateData.
        /// The connectionStateData for the given entity.
        /// </returns>
        /// <exception cref="ArgumentException">Throws an ArgumentException when the connectionEntity is not present in the GhostSendSystem's connection state array.</exception>
        internal (JobHandle, ConnectionStateData) GetConnectionStateData(Entity connectionEntity) =>
            (m_ConnectionStatesJobHandle, m_ConnectionStates[m_ConnectionStateLookup[connectionEntity]]);

        NativeParallelHashMap<Entity, int> m_ConnectionStateLookup;
        StreamCompressionModel m_CompressionModel;
        NativeParallelHashMap<int, ulong> m_SceneSectionHashLookup;

        NativeList<ConnectionStateData> m_ConnectionsToProcess;
#if NETCODE_DEBUG
        EntityQuery m_PacketLogEnableQuery;
        ComponentLookup<PrefabDebugName> m_PrefabDebugNameFromEntity;
        FixedString512Bytes m_LogFolder;
#endif

        NativeParallelHashMap<SpawnedGhost, Entity> m_GhostMap;
        NativeQueue<SpawnedGhost> m_FreeSpawnedGhostQueue;

        static readonly Profiling.ProfilerMarker s_PrioritizeChunksMarker = new Profiling.ProfilerMarker("PrioritizeChunks");
        internal static readonly Profiling.ProfilerMarker s_GhostGroupMarker = new Profiling.ProfilerMarker("GhostGroup");
        internal static readonly Profiling.ProfilerMarker s_CanUseStaticOptimizationMarker = new Profiling.ProfilerMarker("CanUseStaticOptimization");
        internal static readonly Profiling.ProfilerMarker s_RelevancyMarker = new Profiling.ProfilerMarker("Relevancy");
        internal static readonly Profiling.ProfilerMarker s_GhostGroupRelevancyMarker = new Profiling.ProfilerMarker("GhostGroupRelevancy");
        static readonly Profiling.ProfilerMarker k_Scheduling = new Profiling.ProfilerMarker("GhostSendSystem_Scheduling");
        static readonly Profiling.ProfilerMarker s_TryGetChunkStateOrNewMarker = new Profiling.ProfilerMarker("TryGetChunkStateOrNew");

        GhostPreSerializer m_GhostPreSerializer;
        ComponentLookup<NetworkId> m_NetworkIdFromEntity;
        ComponentLookup<NetworkSnapshotAck> m_SnapshotAckFromEntity;
        ComponentLookup<GhostType> m_GhostTypeFromEntity;
        ComponentLookup<NetworkStreamConnection> m_ConnectionFromEntity;
        ComponentLookup<GhostInstance> m_GhostFromEntity;
        ComponentLookup<NetworkStreamSnapshotTargetSize> m_SnapshotTargetFromEntity;
        ComponentLookup<EnablePacketLogging> m_EnablePacketLoggingFromEntity;
        ComponentLookup<OverrideGhostData> m_GhostOverrideFromEntity;

        ComponentTypeHandle<GhostCleanup> m_GhostSystemStateType;
        ComponentTypeHandle<PreSerializedGhost> m_PreSerializedGhostType;
        ComponentTypeHandle<GhostInstance> m_GhostComponentType;
        ComponentTypeHandle<GhostOwner> m_GhostOwnerComponentType;
        ComponentTypeHandle<GhostChildEntity> m_GhostChildEntityComponentType;
        ComponentTypeHandle<PreSpawnedGhostIndex> m_PrespawnedGhostIdType;
        ComponentTypeHandle<GhostType> m_GhostTypeComponentType;

        EntityTypeHandle m_EntityType;
        BufferTypeHandle<GhostGroup> m_GhostGroupType;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupType;
        BufferTypeHandle<PrespawnGhostBaseline> m_PrespawnGhostBaselineType;
        SharedComponentTypeHandle<SubSceneGhostComponentHash> m_SubsceneGhostComponentType;

        BufferLookup<PrespawnGhostIdRange> m_PrespawnGhostIdRangeFromEntity;
        BufferLookup<GhostCollectionPrefabSerializer> m_GhostTypeCollectionFromEntity;
        BufferLookup<GhostCollectionPrefab> m_GhostCollectionFromEntity;
        BufferLookup<GhostComponentSerializer.State> m_GhostComponentCollectionFromEntity;
        BufferLookup<GhostCollectionComponentIndex> m_GhostComponentIndexFromEntity;
        BufferLookup<PrespawnSectionAck> m_PrespawnAckFromEntity;
        BufferLookup<PrespawnSceneLoaded> m_PrespawnSceneLoadedFromEntity;

        int m_CurrentCleanupConnectionState;
        uint m_SentSnapshots;
        ComponentTypeHandle<GhostImportance> m_GhostImportanceType;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
#if NETCODE_DEBUG
            m_LogFolder = NetDebug.LogFolderForPlatform();
            NetDebugInterop.Initialize();
#endif
            ghostQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostCleanup>());
            EntityQueryDesc filterSpawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostInstance)},
                None = new ComponentType[] {typeof(GhostCleanup), typeof(PreSpawnedGhostIndex)}
            };
            //TODO if we had a different tag like GhostNeedsInitialization that'd be there on all ghost prefabs, we could then have the Ghostcleanup comp already on the ghost when spawning, allowing the serialize job to detect the ghost on the same frame as it is spawned, avoiding an extra 16ms of delay between server spawn and it actually being sent. Could have high impact for missiles and other spawned time sensitive objects like this.
            EntityQueryDesc filterDespawn = new EntityQueryDesc
            {
                All = new ComponentType[] {typeof(GhostCleanup)},
                None = new ComponentType[] {typeof(GhostInstance)}
            };
            ghostSpawnQuery = state.GetEntityQuery(filterSpawn);
            ghostDespawnQuery = state.GetEntityQuery(filterDespawn);
            prespawnSharedComponents = state.GetEntityQuery(ComponentType.ReadOnly<SubSceneGhostComponentHash>());
            internalGlobalRelevantQueryMask = state.GetEntityQuery(ComponentType.ReadOnly<PrespawnSceneLoaded>()).GetEntityQueryMask();
            netcodeEmptyQuery = state.GetEntityQuery(new EntityQueryDesc { None = new ComponentType[] { typeof(GhostInstance) } }).GetEntityQueryMask(); // "default" just matches everything so we need to specify None to have a real "no query is set"

            m_FreeGhostIds = new NativeQueue<int>(Allocator.Persistent);
            m_AllocatedGhostIds = new NativeArray<int>(2, Allocator.Persistent);
            m_AllocatedGhostIds[0] = 1; // To make sure 0 is invalid
            m_AllocatedGhostIds[1] = 1; // To make sure 0 is invalid

            m_DestroyedPrespawns = new NativeList<int>(Allocator.Persistent);
            m_DestroyedPrespawnsQueue = new NativeQueue<int>(Allocator.Persistent);
            m_OldestPendingDespawnTickByAll = new NativeReference<NetworkTick>(Allocator.Persistent);
#if UNITY_EDITOR
#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
            m_UpdateLen = new NativeArray<uint>(maxThreadCount, Allocator.Persistent);
            m_UpdateCounts = new NativeArray<uint>(maxThreadCount, Allocator.Persistent);
#endif

            connectionQuery = state.GetEntityQuery(
                ComponentType.ReadWrite<NetworkStreamConnection>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());

            m_ConnectionStates = new NativeList<ConnectionStateData>(256, Allocator.Persistent);
            m_ConnectionStateLookup = new NativeParallelHashMap<Entity, int>(256, Allocator.Persistent);
            m_CompressionModel = StreamCompressionModel.Default;
            m_SceneSectionHashLookup = new NativeParallelHashMap<int, ulong>(256, Allocator.Persistent);

            state.RequireForUpdate<GhostCollection>();

            m_GhostRelevancySet = new NativeParallelHashMap<RelevantGhostForConnection, int>(1024, Allocator.Persistent);
            m_ConnectionsToProcess = new NativeList<ConnectionStateData>(16, Allocator.Persistent);
            var relevancySingleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostRelevancy>());
            state.EntityManager.SetName(relevancySingleton, "GhostRelevancy-Singleton");
            SystemAPI.SetSingleton(new GhostRelevancy(m_GhostRelevancySet));

            m_GhostMap = new NativeParallelHashMap<SpawnedGhost, Entity>(1024, Allocator.Persistent);
            m_FreeSpawnedGhostQueue = new NativeQueue<SpawnedGhost>(Allocator.Persistent);

            var spawnedGhostMap = state.EntityManager.CreateEntity(ComponentType.ReadWrite<SpawnedGhostEntityMap>());
            state.EntityManager.SetName(spawnedGhostMap, "SpawnedGhostEntityMapSingleton");

            SystemAPI.SetSingleton(new SpawnedGhostEntityMap{Value = m_GhostMap.AsReadOnly(), SpawnedGhostMapRW = m_GhostMap, ServerDestroyedPrespawns = m_DestroyedPrespawns, m_ServerAllocatedGhostIds = m_AllocatedGhostIds, m_ServerFreeGhostIds = m_FreeGhostIds });

#if NETCODE_DEBUG
            m_PacketLogEnableQuery = state.GetEntityQuery(ComponentType.ReadOnly<EnablePacketLogging>());
#endif

            m_GhostPreSerializer = new GhostPreSerializer(state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostType>(), ComponentType.ReadOnly<PreSerializedGhost>()));

            var dataSingleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostSendSystemData>());
            state.EntityManager.SetName(dataSingleton, "GhostSystemData-Singleton");
            var data = new GhostSendSystemData();
            data.Initialize();
            SystemAPI.SetSingleton(data);

#if UNITY_EDITOR
            SetupAnalyticsSingleton(state.EntityManager);
#endif

            m_NetworkIdFromEntity = state.GetComponentLookup<NetworkId>();
            m_SnapshotAckFromEntity = state.GetComponentLookup<NetworkSnapshotAck>(false);
            m_GhostTypeFromEntity = state.GetComponentLookup<GhostType>(true);
#if NETCODE_DEBUG
            m_PrefabDebugNameFromEntity = state.GetComponentLookup<PrefabDebugName>(true);
#endif
            m_ConnectionFromEntity = state.GetComponentLookup<NetworkStreamConnection>(true);
            m_GhostFromEntity = state.GetComponentLookup<GhostInstance>(true);
            m_SnapshotTargetFromEntity = state.GetComponentLookup<NetworkStreamSnapshotTargetSize>(true);
            m_EnablePacketLoggingFromEntity = state.GetComponentLookup<EnablePacketLogging>(false);
            m_GhostOverrideFromEntity = state.GetComponentLookup<OverrideGhostData>(true);

            m_GhostSystemStateType = state.GetComponentTypeHandle<GhostCleanup>(true);
            m_PreSerializedGhostType = state.GetComponentTypeHandle<PreSerializedGhost>(true);
            m_GhostComponentType = state.GetComponentTypeHandle<GhostInstance>();
            m_GhostOwnerComponentType = state.GetComponentTypeHandle<GhostOwner>(true);
            m_GhostChildEntityComponentType = state.GetComponentTypeHandle<GhostChildEntity>(true);
            m_PrespawnedGhostIdType = state.GetComponentTypeHandle<PreSpawnedGhostIndex>(true);
            m_GhostTypeComponentType = state.GetComponentTypeHandle<GhostType>(true);
            m_GhostImportanceType = state.GetComponentTypeHandle<GhostImportance>();

            m_EntityType = state.GetEntityTypeHandle();
            m_GhostGroupType = state.GetBufferTypeHandle<GhostGroup>(true);
            m_LinkedEntityGroupType = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            m_PrespawnGhostBaselineType = state.GetBufferTypeHandle<PrespawnGhostBaseline>(true);
            m_SubsceneGhostComponentType = state.GetSharedComponentTypeHandle<SubSceneGhostComponentHash>();

            m_PrespawnGhostIdRangeFromEntity = state.GetBufferLookup<PrespawnGhostIdRange>();
            m_GhostTypeCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefabSerializer>(true);
            m_GhostCollectionFromEntity = state.GetBufferLookup<GhostCollectionPrefab>(true);
            m_GhostComponentCollectionFromEntity = state.GetBufferLookup<GhostComponentSerializer.State>(true);
            m_GhostComponentIndexFromEntity = state.GetBufferLookup<GhostCollectionComponentIndex>(true);
            m_PrespawnAckFromEntity = state.GetBufferLookup<PrespawnSectionAck>(true);
            m_PrespawnSceneLoadedFromEntity = state.GetBufferLookup<PrespawnSceneLoaded>(true);
        }

#if UNITY_EDITOR
        void SetupAnalyticsSingleton(EntityManager entityManager)
        {
            var analyticsSingleton = entityManager.CreateEntity(ComponentType.ReadWrite<GhostSendSystemAnalyticsData>());
            entityManager.SetName(analyticsSingleton, "GhostSystemAnalyticsData-Singleton");
            var analyticsData = new GhostSendSystemAnalyticsData
            {
                UpdateLenSums = m_UpdateLen,
                NumberOfUpdates = m_UpdateCounts,
            };
            SystemAPI.SetSingleton(analyticsData);
        }
#endif

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_GhostPreSerializer.Dispose();
            m_AllocatedGhostIds.Dispose();
            m_FreeGhostIds.Dispose();

            m_DestroyedPrespawns.Dispose();
            m_DestroyedPrespawnsQueue.Dispose();
            m_OldestPendingDespawnTickByAll.Dispose();
            foreach (var connectionState in m_ConnectionStates)
            {
                connectionState.Dispose();
            }
            m_ConnectionStates.Dispose();

            m_ConnectionStateLookup.Dispose();

            m_GhostRelevancySet.Dispose();
            m_ConnectionsToProcess.Dispose();

            state.Dependency.Complete(); // for ghost map access
            m_GhostMap.Dispose();
            m_FreeSpawnedGhostQueue.Dispose();
            m_SceneSectionHashLookup.Dispose();
#if UNITY_EDITOR
            m_UpdateLen.Dispose();
            m_UpdateCounts.Dispose();
#endif
        }

        [BurstCompile]
        struct SpawnGhostJob : IJob
        {
            [ReadOnly] public NativeArray<ConnectionStateData> connectionState;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [ReadOnly] public NativeList<ArchetypeChunk> spawnChunks;
            [ReadOnly] public EntityTypeHandle entityType;
            public ComponentTypeHandle<GhostInstance> ghostComponentType;
            public NativeQueue<int> freeGhostIds;
            public NativeArray<int> allocatedGhostIds;
            public EntityCommandBuffer commandBuffer;
            public NativeParallelHashMap<SpawnedGhost, Entity> ghostMap;

            [ReadOnly] public ComponentLookup<GhostType> ghostTypeFromEntity;
            [ReadOnly] public ComponentLookup<OverrideGhostData> ghostOverrideFromEntity;
            public NetworkTick serverTick;
            public byte forcePreSerialize;
            public NetDebug netDebug;
#if NETCODE_DEBUG
            [ReadOnly] public ComponentLookup<PrefabDebugName> prefabNames;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            [ReadOnly] public ComponentTypeHandle<GhostOwner> ghostOwnerComponentType;
#endif
            public void Execute()
            {
                // Some of the code in GhostSendSystem could be useful for offline single world hosts as well.
                // However there are assumptions that things are reset if you're not NetworkStreamInGame anymore. ghost collection gets reset with no more connections.
                // For example during a scene switch. Which was nice, we'd reset everything between "in-game" and make sure there's no rogue prefab entries.
                // Users could do a world migration which could be cleaner, but it'd be lots of maintenance cost, not great.
                // We could potentially split "enable replication" and "reset ghost collection because there was a scene switch"
                // The assumption was that Ghost IDs should only be there for mapping ghosts between client and server. if there's no connection, there's no need for a GhostID.
                // However, if you want to access a ghost' GhostType (which is stored in GhostInstance), you need GhostInstance to be initialized. For example the BackupSystem uses GhostType for its own serialization purpose.
                // Plus it's more consistent to just always have GhostIDs all the time as soon as a ghost is spawned, not just when you have a connection. The perf gains you'd gain by not doing the work now would
                // be offset by the burst of work to do with the first client connected. Plus, assigning ghost IDs shouldn't be the thing that takes the most time, user simulation logic should be bigger.
                // TODO are there other assumptions around NetworkStreamInGame and other work that should always be done only if you have a client connection? Other perf impact?
                // TODO Once we do this, look at what happens if you go in game, then stop in game (clearing ghost collection), switch scene and rebuild collection once you go in game again? Are ghost types coherent then for your GhostInstance? Does this work for a DGS where you're not in-game when you start the process?

                // The below check is useless, as this job is only triggered when there's a GhostCollection present and it's "network stream in game" is true. This check was added a long time ago by Tim when fixing a LagCompensation test? https://github.com/Unity-Technologies/netcode/commit/07560a4e66da43ecc88dea0d0dd81123bccf8982#diff-ecc6fdb6e44e3dc05cff13a9e5aa56ba02b1faa082c1adb80031105d79b23793
                // if (connectionState.Length == 0)
                //     return;

                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                for (int chunk = 0; chunk < spawnChunks.Length; ++chunk)
                {
                    var entities = spawnChunks[chunk].GetNativeArray(entityType);
                    var ghostTypeComponent = ghostTypeFromEntity[entities[0]];
                    int ghostType;
                    for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                    {
                        if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                            break;
                    }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (ghostType >= GhostCollection.Length)
                        throw new InvalidOperationException($"Could not find ghost type in the collection. GhostCollection length is {GhostCollection.Length}, was trying to find ghost type index {ghostType}");
#endif
                    if (ghostType >= GhostTypeCollection.Length)
                        continue; // serialization data has not been loaded yet
                    var ghosts = spawnChunks[chunk].GetNativeArray(ref ghostComponentType);
                    for (var ent = 0; ent < entities.Length; ++ent)
                    {
                        var newEntitySpawnTick = serverTick;
                        var newEntityGhostId = 0;

                        if (ghostOverrideFromEntity.HasComponent(entities[ent]))
                        {
                            var overrideComponent = ghostOverrideFromEntity[entities[ent]];
                            newEntityGhostId = overrideComponent.GhostId;
                            newEntitySpawnTick = overrideComponent.SpawnTick;
                            commandBuffer.RemoveComponent<OverrideGhostData>(entities[ent]);
                        }
                        else
                        {
                            if (!freeGhostIds.TryDequeue(out newEntityGhostId))
                            {
                                newEntityGhostId = allocatedGhostIds[0];
                                allocatedGhostIds[0] = newEntityGhostId + 1;
                            }
                        }

                        if ( newEntityGhostId == 0 )
                        {
                            netDebug.LogError($"Assigning a GhostId of 0 to a Ghost. This should never happen. Has GhostId override = {ghostOverrideFromEntity.HasComponent(entities[ent])}");
                        }

                        // TODO-release this won't execute on a single world host if it has no connection. Backup system assumes GhostInstance is initialized on each entity. Wouldn't that be the case for user systems as well? A user server system running would assume a spawned ghost has a GhostInstance that's initialized, even if there's not client connected, no? This is mostly an issue for single world host if we support offline mode. In binary world mode, a host can't really be "offline", its client world wouldn't update anymore.
                        ghosts[ent] = new GhostInstance {ghostId = newEntityGhostId, ghostType = ghostType, spawnTick = newEntitySpawnTick };

                        var spawnedGhost = new SpawnedGhost
                        {
                            ghostId = newEntityGhostId,
                            spawnTick = newEntitySpawnTick
                        };
                        if (!ghostMap.TryAdd(spawnedGhost, entities[ent]))
                        {
                            netDebug.LogError(FixedString.Format("GhostID {0} already present in the ghost entity map", newEntityGhostId));
                            ghostMap[spawnedGhost] = entities[ent];
                        }

                        var ghostState = new GhostCleanup
                        {
                            ghostId = newEntityGhostId, despawnTick = NetworkTick.Invalid, spawnTick = newEntitySpawnTick
                        };
                        commandBuffer.AddComponent(entities[ent], ghostState);
                        if (forcePreSerialize == 1)
                            commandBuffer.AddComponent<PreSerializedGhost>(entities[ent]);
#if NETCODE_DEBUG
                        if (netDebug.LogLevel <= NetDebug.LogLevelType.Debug)
                        {
                            FixedString64Bytes prefabNameString = default;
                            if (prefabNames.HasComponent(GhostCollection[ghostType].GhostPrefab))
                                prefabNameString.CopyFromTruncated(prefabNames[GhostCollection[ghostType].GhostPrefab].PrefabName);
                            netDebug.DebugLog(FixedString.Format("[Spawn] GID:{0} Prefab:{1} TypeID:{2} spawnTick:{3}", newEntityGhostId, prefabNameString, ghostType, newEntitySpawnTick.ToFixedString()));
                        }
#endif
                    }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (GhostTypeCollection[ghostType].PredictionOwnerOffset != 0)
                    {
                        if (!spawnChunks[chunk].Has(ref ghostOwnerComponentType))
                        {
                            netDebug.LogError(FixedString.Format("Ghost type is owner predicted but does not have a GhostOwner {0}, {1}", ghostType, ghostTypeComponent.guid0));
                            continue;
                        }
                        if (GhostTypeCollection[ghostType].OwnerPredicted != 0)
                        {
                            // Validate that the entity has a GhostOwner and that the value in the GhostOwner has been initialized
                            var ghostOwners = spawnChunks[chunk].GetNativeArray(ref ghostOwnerComponentType);
                            for (int ent = 0; ent < ghostOwners.Length; ++ent)
                            {
                               if (ghostOwners[ent].NetworkId == 0)
                               {
                                   netDebug.LogError("Trying to spawn an owner predicted ghost which does not have a valid owner set. When using owner prediction you must set GhostOwner.NetworkId when spawning the ghost. If the ghost is not owned by a player you can set NetworkId to -1.");
                               }
                            }
                        }
                    }
#endif
                }
            }
        }

        [BurstCompile]
        struct SerializeJob : IJobParallelForDefer
        {
            public DynamicTypeList DynamicGhostCollectionComponentTypeList;
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferLookup<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [ReadOnly] public BufferLookup<GhostCollectionPrefab> GhostCollectionFromEntity;
            [NativeDisableContainerSafetyRestriction] DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
            public ConcurrentDriverStore concurrentDriverStore;
            [ReadOnly] public NativeList<ArchetypeChunk> despawnChunks;
            [ReadOnly] public NativeList<ArchetypeChunk> ghostChunks;

            [ReadOnly] public NativeArray<ConnectionStateData> connectionState;
            [NativeDisableParallelForRestriction] public ComponentLookup<NetworkSnapshotAck> ackFromEntity;
            [ReadOnly] public ComponentLookup<NetworkStreamConnection> connectionFromEntity;
            [ReadOnly] public ComponentLookup<NetworkId> networkIdFromEntity;

            [ReadOnly] public EntityTypeHandle entityType;
            [ReadOnly] public ComponentTypeHandle<GhostInstance> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostCleanup> ghostSystemStateType;
            [ReadOnly] public ComponentTypeHandle<PreSerializedGhost> preSerializedGhostType;
            [ReadOnly] public BufferTypeHandle<GhostGroup> ghostGroupType;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntity> ghostChildEntityComponentType;
            [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnGhostIdType;
            [ReadOnly] public SharedComponentTypeHandle<SubSceneGhostComponentHash> subsceneHashSharedTypeHandle;

            public GhostRelevancyMode relevancyMode;
            [ReadOnly] public NativeParallelHashMap<RelevantGhostForConnection, int> relevantGhostForConnection;
            [ReadOnly] public EntityQueryMask userGlobalRelevantMask;
            [ReadOnly] public EntityQueryMask internalGlobalRelevantMask;

#if UNITY_EDITOR || NETCODE_DEBUG
            public NativeArray<UnsafeGhostStatsSnapshot> NetStatsSnapshotPerThread;
            [NativeSetThreadIndex] public int ThreadIndex;
#endif
            [ReadOnly] public StreamCompressionModel compressionModel;

            [ReadOnly] public ComponentLookup<GhostInstance> ghostFromEntity;

            public NetworkTick currentTick;
            public uint localTime;
            public float simulationTickRateIntervalMs;
            public int networkTickRateIntervalTicks;

            public PortableFunctionPointer<GhostImportance.BatchScaleImportanceDelegate> BatchScaleImportance;
            public PortableFunctionPointer<GhostImportance.ScaleImportanceDelegate> ScaleGhostImportance;

            [ReadOnly] public DynamicSharedComponentTypeHandle ghostImportancePerChunkTypeHandle;
            [NativeDisableUnsafePtrRestriction] [ReadOnly] public IntPtr ghostImportanceDataIntPtr;
            [ReadOnly] public DynamicComponentTypeHandle ghostConnectionDataTypeHandle;
            public int ghostConnectionDataTypeSize;
            [ReadOnly] public ComponentLookup<NetworkStreamSnapshotTargetSize> snapshotTargetSizeFromEntity;
            [ReadOnly] public ComponentLookup<GhostType> ghostTypeFromEntity;
            [ReadOnly] public NativeArray<int> allocatedGhostIds;
            [ReadOnly] public NativeList<int> prespawnDespawns;

            [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaselineTypeHandle;
            [ReadOnly] public NativeParallelHashMap<int, ulong> SubSceneHashSharedIndexMap;
            public uint CurrentSystemVersion;
            public NetDebug netDebug;
#if NETCODE_DEBUG
            public PacketDumpLogger netDebugPacket;
            [ReadOnly] public ComponentLookup<PrefabDebugName> prefabNamesFromEntity;
            [NativeDisableContainerSafetyRestriction] public ComponentLookup<EnablePacketLogging> enableLoggingFromEntity;
            public FixedString128Bytes timestamp;
#endif

            public Entity prespawnSceneLoadedEntity;
            [ReadOnly] public BufferLookup<PrespawnSectionAck> prespawnAckFromEntity;
            [ReadOnly] public BufferLookup<PrespawnSceneLoaded> prespawnSceneLoadedFromEntity;

            Entity connectionEntity;
            ConnectionStateData.GhostStateList ghostStateData;
            int connectionIdx;

            public GhostSendSystemData systemData;

            [ReadOnly] public NativeParallelHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotPreSerializeData;
#if UNITY_EDITOR
            [NativeDisableParallelForRestriction] public NativeArray<uint> UpdateLen;
            [NativeDisableParallelForRestriction] public NativeArray<uint> UpdateCounts;
#endif

            public unsafe void Execute(int idx)
            {
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicGhostCollectionComponentTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicGhostCollectionComponentTypeList.Length;
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                connectionIdx = idx;
                var curConnectionState = connectionState[connectionIdx];
                connectionEntity = curConnectionState.Entity;

                curConnectionState.EnsureGhostStateCapacity(allocatedGhostIds[0], allocatedGhostIds[1]);
                ghostStateData = curConnectionState.GhostStateData;
#if NETCODE_DEBUG
                netDebugPacket = curConnectionState.NetDebugPacket;
                EnablePacketLogging.InitAndFetch(connectionEntity, enableLoggingFromEntity, curConnectionState.NetDebugPacket);
#endif
                var connectionId = connectionFromEntity[connectionEntity].Value;
                var concurrent = concurrentDriverStore.GetConcurrentDriver(connectionFromEntity[connectionEntity].DriverId);
                var driver = concurrent.driver;
                var unreliablePipeline = concurrent.unreliablePipeline;
                var unreliableFragmentedPipeline = concurrent.unreliableFragmentedPipeline;
                if (driver.GetConnectionState(connectionId) != NetworkConnection.State.Connected)
                    return;

#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\n███ [GSS:SJ][{timestamp}] Connection {connectionId.ToFixedString()} on ServerTick:{currentTick.ToFixedString()}, {networkIdFromEntity[connectionEntity].ToFixedString()}\n");
#endif

                // Gather Ghost Chunks:
                s_PrioritizeChunksMarker.Begin();
                GatherGhostChunksBatch(out GhostChunksContext ctx);
                s_PrioritizeChunksMarker.End();

                // MaxIterateChunks is how many we process (i.e. query i.e. how many we ATTEMPT to send),
                // MaxSendChunks is how many we ALLOW to send.
                ctx.MaxChunksToIterate = ctx.SerialChunks->Length;
                if (systemData.MaxIterateChunks == 0 && systemData.MaxSendChunks > 0)
                    systemData.MaxIterateChunks = systemData.MaxSendChunks;
                if(systemData.MaxIterateChunks > 0)
                    ctx.MaxChunksToIterate = math.min(systemData.MaxIterateChunks, ctx.SerialChunks->Length);

#if NETCODE_DEBUG
                if (Hint.Unlikely(netDebugPacket.IsCreated))
                    netDebugPacket.Log((FixedString512Bytes) $"\tGatherGhostChunks gathered and sorted {ctx.SerialChunks->Length} of {ghostChunks.Length} chunks for ServerTick:{currentTick.ToFixedString()}! MSC:{systemData.MaxSendChunks}, MIC:{systemData.MaxIterateChunks} means iterating {ctx.MaxChunksToIterate} w/ RlvntGhosts:{ctx.TotalRelevantGhosts} (RMode:{(int) relevancyMode}), numZC:{ctx.NumZeroChangeChunks} {(int) ((float) ctx.NumZeroChangeChunks / ctx.SerialChunks->Length) * 100}%");
#endif

                // Serialize Entities:
                var maxMessageSize = driver.m_DriverSender.m_SendQueue.PayloadCapacity;
                int maxSnapshotSizeWithoutFragmentation = maxMessageSize - driver.MaxHeaderSize(unreliablePipeline);

                int targetSnapshotSize = maxSnapshotSizeWithoutFragmentation;
                if (snapshotTargetSizeFromEntity.TryGetComponent(connectionEntity, out var perConnectionTargetSnapshotSize))
                {
                    targetSnapshotSize = math.max(GhostSystemConstants.MinSnapshotPacketSize, perConnectionTargetSnapshotSize.Value);
                }
                else if (systemData.DefaultSnapshotPacketSize > 0)
                {
                    targetSnapshotSize = math.max(GhostSystemConstants.MinSnapshotPacketSize, systemData.DefaultSnapshotPacketSize);
                }

                if (prespawnSceneLoadedEntity != Entity.Null)
                {
                    PrespawnHelper.UpdatePrespawnAckSceneMap(ref curConnectionState,
                        prespawnSceneLoadedEntity, prespawnAckFromEntity, prespawnSceneLoadedFromEntity);
                }

                int attempt = 1;
                var serializeResult = default(SerializeEnitiesResult);
                while (serializeResult != SerializeEnitiesResult.Abort &&
                       serializeResult != SerializeEnitiesResult.Ok)
                {
                    // If the requested packet size if larger than MaxMessageSize we have to use the fragmentation pipeline
                    var pipelineToUse = (targetSnapshotSize <= maxSnapshotSizeWithoutFragmentation) ? unreliablePipeline : unreliableFragmentedPipeline;
                    var result = driver.BeginSend(pipelineToUse, connectionId, out var dataStream, targetSnapshotSize);
                    if ((int)Networking.Transport.Error.StatusCode.Success == result)
                    {
                        serializeResult = SerializeEnitiesResult.Unknown;
                        try
                        {
                            ref var snapshotAck = ref ackFromEntity.GetRefRW(connectionEntity).ValueRW;
                            serializeResult = sendEntities(ref dataStream, snapshotAck, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength, in ctx);
                            if (serializeResult == SerializeEnitiesResult.Ok)
                            {
                                if ((result = driver.EndSend(dataStream)) >= (int) Networking.Transport.Error.StatusCode.Success)
                                {
#if UNITY_EDITOR || NETCODE_DEBUG
                                    ref var netStatsSnapshots = ref NetStatsSnapshotPerThread.AsSpan()[ThreadIndex];
                                    netStatsSnapshots.SnapshotTotalSizeInBits += (uint)dataStream.LengthInBits;
#endif
                                    snapshotAck.CurrentSnapshotSequenceId++;
                                    snapshotAck.SnapshotPacketLoss.NumPacketsReceived++;
                                }
                                else
                                {
                                    netDebug.LogWarning($"Failed to send a snapshot to a client with EndSend error: {result}!");
                                }
                            }
                            else
                            {
                                driver.AbortSend(dataStream); // TODO reset snapshot stats here
                            }
                        }
                        finally
                        {

                            //Finally is always called for non butsted code because there is a try-catch in outer caller (worldunmanged)
                            //regardless of the exception thrown (even invalidprogramexception).
                            //For bursted code, the try-finally has some limitation but it is still unwinding the blocks in the correct order
                            //(not in all cases, but it the one used here everything work fine).
                            //In general, the unhandled error and exceptions are all cought first by the outermost try-catch (world unmanged)
                            //and then the try-finally are called in reverse order (stack unwiding).
                            //There are two exeption handling in the ghost send system:
                            //- the one here, that is responsible to abort the data stream.
                            //- one inside the sendEntities method itself, that try to revert some internal state (i.e: the despawn ghost)
                            //
                            //The innermost finally is called first and do not abort the streams.
                            if (serializeResult == SerializeEnitiesResult.Unknown)
                                driver.AbortSend(dataStream); // TODO reset snapshot stats here
                        }
                    }
                    else
                    {
                        netDebug.LogError($"Failed to send a snapshot to a client with BeginSend error: {result}, attempt:{attempt}!");
                        if (result == (int)Networking.Transport.Error.StatusCode.NetworkPacketOverflow)
                        {
                            serializeResult = SerializeEnitiesResult.Abort;
                        }
                    }

                    if (serializeResult == SerializeEnitiesResult.Failed)
                    {
                        if (Hint.Likely(attempt < GhostSystemConstants.MaxSnapshotSendAttempts))
                        {
                            // TODO - This is still wasteful as it re-serializes everything.
                            // I.e. If the current dataStream can't fit a single ghost, we should NOT throw away all the writes,
                            // we just need to allocate a larger writer and copy the existing data into the new writer.
                            if (Hint.Unlikely(netDebug.LogLevel == NetDebug.LogLevelType.Debug))
                                netDebug.LogWarning($"PERFORMANCE: Could not fit snapshot content into `targetSnapshotSize`:{targetSnapshotSize} in attempt:{attempt} for {ctx.NetworkId.ToFixedString()}, increasing size to {targetSnapshotSize * 2} and trying again! Your configured `MaxMessageSize`:{maxMessageSize} and/or `DefaultSnapshotPacketSize`:{systemData.DefaultSnapshotPacketSize}, and/or `NetworkStreamSnapshotTargetSize`:{perConnectionTargetSnapshotSize.Value} is too small to fit even one ghost.");

                            UnityEngine.Debug.Assert(targetSnapshotSize > 0);
                            targetSnapshotSize += targetSnapshotSize;
#if NETCODE_DEBUG
                            if (Hint.Unlikely(netDebugPacket.IsCreated))
                                netDebugPacket.Log($"Send attempt {attempt} failed with targetSnapshotSize:{targetSnapshotSize}, retrying!\n");
#endif
                        }
                        else
                        {
#if NETCODE_DEBUG
                            if (Hint.Unlikely(netDebugPacket.IsCreated))
                                netDebugPacket.Log($"FATAL: Could not fit snapshot content into `targetSnapshotSize`:{targetSnapshotSize} after MaxSnapshotSendAttempts:{attempt} for {ctx.NetworkId.ToFixedString()}, aborting!\n");
#endif
                            netDebug.LogError($"FATAL: Could not fit snapshot content into `targetSnapshotSize`:{targetSnapshotSize} after MaxSnapshotSendAttempts:{attempt} for {ctx.NetworkId.ToFixedString()}, aborting!");
                            serializeResult = SerializeEnitiesResult.Abort;
                        }
                    }
                    attempt++;
                }
            }

            unsafe struct GhostChunksContext
            {
                public NetworkId NetworkId;
                public UnsafeList<PrioChunk>* SerialChunks;
                public int MaxChunksToIterate;
                public int MaxGhostsPerChunk;
                /// <summary>
                /// Approximates the total number of relevant ghosts.
                /// <br/>
                /// Note: Does not count ghost chunks that aren't passed into this job yet (e.g. ones without the <see cref="GhostCleanup"/>).
                /// And, when relevancy is enabled, this does not count ghost chunks that have not yet run through the
                /// <see cref="GhostChunkSerializer.UpdateGhostRelevancy"/> step.
                /// </summary>
                public int TotalRelevantGhosts;
                public int NumZeroChangeChunks;
            }

            unsafe SerializeEnitiesResult sendEntities(ref DataStreamWriter dataStream, NetworkSnapshotAck snapshotAckCopy,
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength, in GhostChunksContext ctx)
            {
                var serializerState = new GhostSerializerState
                {
                    GhostFromEntity = ghostFromEntity
                };
                var ackTick = snapshotAckCopy.LastReceivedSnapshotByRemote;

                // Snapshot header:
                dataStream.WriteByte((byte) NetworkStreamProtocol.Snapshot);

                dataStream.WriteUInt(localTime);
                uint returnTime = snapshotAckCopy.CalculateReturnTime(localTime);
                dataStream.WriteUInt(returnTime);
                dataStream.WriteInt(snapshotAckCopy.ServerCommandAge);
                dataStream.WriteByte(snapshotAckCopy.CurrentSnapshotSequenceId);
                dataStream.WriteUInt(currentTick.SerializedData);

                // Write the list of ghost snapshots the client has not acked yet
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                uint numLoadedPrefabs = snapshotAckCopy.NumLoadedPrefabs;
                if (numLoadedPrefabs > (uint)GhostCollection.Length)
                {
                    // The received ghosts by remote might not have been updated yet
                    numLoadedPrefabs = 0;
                    // Override the copy of the snapshot ack so the GhostChunkSerializer can skip this check
                    snapshotAckCopy.NumLoadedPrefabs = 0;
                }
                uint numNewPrefabs = math.min((uint)GhostCollection.Length - numLoadedPrefabs, GhostSystemConstants.MaxNewPrefabsPerSnapshot);
                dataStream.WritePackedUInt(numNewPrefabs, compressionModel);

#if NETCODE_DEBUG
                FixedString512Bytes debugLog = default;
                if (netDebugPacket.IsCreated)
                {
                    debugLog = $"\n\t[SendEntities] Protocol:{(byte) NetworkStreamProtocol.Snapshot} LocalTime:{localTime} ReturnTime:{returnTime} CommandAge:{snapshotAckCopy.ServerCommandAge} | ";
                    debugLog.Append((FixedString512Bytes)$"Tick:{currentTick.ToFixedString()} SSId:{snapshotAckCopy.CurrentSnapshotSequenceId} | NewPrefabs:{numNewPrefabs}, LoadedPrefabs:{numLoadedPrefabs}\n");
                }
#endif

                if (numNewPrefabs > 0)
                {
                    dataStream.WritePackedUInt(numLoadedPrefabs, compressionModel);
                    int prefabNum = (int)numLoadedPrefabs;
                    for (var i = 0; i < numNewPrefabs; ++i)
                    {
                        var ghostPrefab = GhostCollection[prefabNum];
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid0);
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid1);
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid2);
                        dataStream.WriteUInt(ghostPrefab.GhostType.guid3);
                        dataStream.WriteULong(ghostPrefab.Hash);
#if NETCODE_DEBUG
                        if (netDebugPacket.IsCreated)
                        {
                            debugLog.Append(FixedString.Format("\t NewPrefab:{0}-{1}-{2}-{3}",
                                ghostPrefab.GhostType.guid0, ghostPrefab.GhostType.guid1,
                                ghostPrefab.GhostType.guid2,
                                ghostPrefab.GhostType.guid3));
                            debugLog.Append(FixedString.Format(" Hash:{0} '{1}'\n", ghostPrefab.Hash, prefabNamesFromEntity[ghostPrefab.GhostPrefab].PrefabName));
                        }
#endif
                        ++prefabNum;
                    }
                }

                dataStream.WritePackedUInt((uint)ctx.TotalRelevantGhosts, compressionModel);
                var lenWriter = dataStream;
                dataStream.WriteUShort(0); // space for despawnLen.
                dataStream.WriteUShort(0); // space for totalSentEntities.

                // Write a list of all ghosts which have been despawned after the last acked packet. Return the number of ghost ids written
#if UNITY_EDITOR || NETCODE_DEBUG
                int startPos = dataStream.LengthInBits;
#endif
                var pendingGhostDespawns = connectionState[connectionIdx].PendingDespawns;
                uint despawnLen = PendingGhostDespawn.WriteDespawns(currentTick, ref *pendingGhostDespawns, ref ghostStateData,
                    despawnChunks, ref snapshotAckCopy, ghostSystemStateType, ref dataStream, ref compressionModel,
                    ref connectionState[connectionIdx].NewLoadedPrespawnRanges, ref prespawnDespawns,
                    ref systemData
#if NETCODE_DEBUG
                    , ref netDebugPacket
#endif
                    );

                if (dataStream.HasFailedWrites)
                {
                    RevertDespawnGhostState();
#if NETCODE_DEBUG
                    if(netDebugPacket.IsCreated)
                        netDebugPacket.Log((FixedString128Bytes)"█ >> Failed! HasFailedWrites before even serializing chunks!\n");
#endif
                    return SerializeEnitiesResult.Failed;
                }
#if UNITY_EDITOR || NETCODE_DEBUG

                ref var netStatsSnapshots = ref NetStatsSnapshotPerThread.AsSpan()[ThreadIndex];
                netStatsSnapshots.DespawnCount += despawnLen;
                netStatsSnapshots.DestroySizeInBits += (uint) (dataStream.LengthInBits - startPos);
                startPos = dataStream.LengthInBits;
#endif

                uint totalSentEntities = 0;
                uint totalSentChunks = 0;
                bool didFillPacket = false;
                var serializerData = new GhostChunkSerializer
                {
                    GhostComponentCollection = GhostComponentCollection,
                    GhostTypeCollection = GhostTypeCollection,
                    GhostComponentIndex = GhostComponentIndex,
                    PrespawnIndexType = prespawnGhostIdType,
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    prespawnBaselineTypeHandle = prespawnBaselineTypeHandle,
                    entityType = entityType,
                    ghostComponentType = ghostComponentType,
                    ghostSystemStateType = ghostSystemStateType,
                    preSerializedGhostType = preSerializedGhostType,
                    ghostChildEntityComponentType = ghostChildEntityComponentType,
                    ghostGroupType = ghostGroupType,
                    snapshotAck = snapshotAckCopy,
                    chunkSerializationData = *connectionState[connectionIdx].SerializationState,
                    pendingDespawns = pendingGhostDespawns,
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    ghostChunkComponentTypesLength = ghostChunkComponentTypesLength,
                    currentTick = currentTick,
                    // Add networkTickRateIntervalTicks as we only send a snapshot on this interval, which artificially inflates the expected snapshot RTT.
                    expectedSnapshotRttInSimTicks = networkTickRateIntervalTicks + math.max(Mathf.CeilToInt(snapshotAckCopy.EstimatedRTT / simulationTickRateIntervalMs), networkTickRateIntervalTicks),
                    compressionModel = compressionModel,
                    serializerState = serializerState,
                    NetworkId = ctx.NetworkId.Value,
                    relevantGhostForConnection = relevantGhostForConnection,
                    relevancyMode = relevancyMode,
                    userGlobalRelevantMask = userGlobalRelevantMask,
                    internalGlobalRelevantMask = internalGlobalRelevantMask,
                    ghostStateData = ghostStateData,
                    CurrentSystemVersion = CurrentSystemVersion,

                    netDebug = netDebug,
#if NETCODE_DEBUG
                    netDebugPacket = netDebugPacket,
                    netDebugPacketResult = default,
                    netDebugPacketDebug = default,
#endif
                    systemData = systemData,
                    SnapshotPreSerializeData = SnapshotPreSerializeData,
                };
                //We now use a better initial size for the temp stream. There is one big of a problem with the current
                //serialization logic: multiple full serialization loops in case the chunk does not fit into the current
                //temp stream. That can happen if either:
                //There are big ghosts (large components or buffers)
                //Lots of small/mid size ghosts (so > 30/40 per chunks) and because of the serialized size
                //(all components temp data are aligned to 32 bits) we can end up in the situation we are consuming up to 2/3x the size
                //of the temp stream.
                //When that happen, we re-fetch and all data (again and again, also for child) and we retry again.
                //This is EXTREMELY SLOW. By allocating at least 8/16kb (instead of 1MTU) we ensure that does not happen (or at least quite rarely)
                //gaining already a 2/3 perf out of the box in many cases. I choose a 8 kb buffer, that is a little large, but
                //give overall a very good boost in many scenario.
                //The parameter is tunable though via GhostSendSystemData, so you can tailor that to the game as necessary.
                var streamCapacity = systemData.UseCustomSerializer == 0
                    ? math.max(systemData.TempStreamInitialSize, dataStream.Capacity)
                    : dataStream.Capacity;
                serializerData.AllocateTempData(ctx.MaxGhostsPerChunk, streamCapacity);

                int pc = 0;
                for (; pc < ctx.MaxChunksToIterate; ++pc)
                {
                    var chunk = ctx.SerialChunks->ElementAt(pc).chunk;
                    var ghostType = ctx.SerialChunks->ElementAt(pc).ghostType;
#if NETCODE_DEBUG
                    serializerData.componentStats = netStatsSnapshots.PerGhostTypeStatsListRefRW.ElementAt(ghostType).PerComponentStatsList;
                    serializerData.ghostTypeName = default;
                    if (netDebugPacket.IsCreated)
                    {
                        if (prefabNamesFromEntity.HasComponent(GhostCollection[ghostType].GhostPrefab))
                            serializerData.ghostTypeName.Append(
                                prefabNamesFromEntity[GhostCollection[ghostType].GhostPrefab].PrefabName);
                    }
#endif

                    // Do not send entities with a ghost type which the client has not acked yet.
                    // TODO - Can this be pulled out & into the GatherGhostChunksBatched stage?
                    if (ghostType >= numLoadedPrefabs)
                    {
#if NETCODE_DEBUG
                        if(netDebugPacket.IsCreated)
                            netDebugPacket.Log(FixedString.Format(
                                "\t\tSkipping {0} as client has not acked prefab.",
                                serializerData.ghostTypeName));
#endif
                        continue;
                    }

                    var serializeResult = default(SerializeEnitiesResult);
                    uint thisChunkSentEntities;
                    try
                    {
                        serializeResult = serializerData.SerializeChunk(ctx.SerialChunks->ElementAt(pc), ref dataStream,
                            out thisChunkSentEntities, ref didFillPacket);
                    }
                    finally
                    {
                        //If the result is unknown, an exception may have been thrown inside the serializeChunk.
                        if (serializeResult == SerializeEnitiesResult.Unknown)
                        {
                            //Do not abort the stream. It is aborted in the outermost loop.
                            RevertDespawnGhostState();
                        }
                    }

                    if (thisChunkSentEntities > 0)
                    {
                        totalSentChunks++;
                        totalSentEntities += thisChunkSentEntities;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        UnityEngine.Debug.Assert(serializeResult == SerializeEnitiesResult.Ok);
#endif
#if UNITY_EDITOR || NETCODE_DEBUG
                        ref var perGhostTypeStats = ref netStatsSnapshots.PerGhostTypeStatsListRefRW.ElementAt(ghostType);
                        perGhostTypeStats.EntityCount += thisChunkSentEntities;
                        perGhostTypeStats.SizeInBits += (uint)(dataStream.LengthInBits - startPos);
                        perGhostTypeStats.ChunkCount += 1;
                        startPos = dataStream.LengthInBits;
#endif
                    }

#if NETCODE_DEBUG
                    if (netDebugPacket.IsCreated)
                    {
                        serializerData.PacketDumpFlush();
                        netDebugPacket.Log((FixedString512Bytes)$"\n\t[{chunk.SequenceNumber}] {ToFixedString(serializeResult)} | +{thisChunkSentEntities} | pc:{pc}/{ctx.MaxChunksToIterate}/{ctx.SerialChunks->Length} | {serializerData.netDebugPacketResult}\n");
                        serializerData.netDebugPacketResult.Clear();
                    }
#endif

                    // Reasons to stop iterating through chunks:
                    if (serializeResult != SerializeEnitiesResult.Ok || didFillPacket)
                        break;
                    if (thisChunkSentEntities > 0 && systemData.MaxSendChunks > 0 && totalSentChunks >= systemData.MaxSendChunks)
                    {
#if NETCODE_DEBUG
                        if(netDebugPacket.IsCreated)
                            netDebugPacket.Log((FixedString512Bytes)$"\tHit MaxSendChunks!");
#endif
                        break;
                    }
                }

#if NETCODE_DEBUG
                if (systemData.MaxIterateChunks != 0 && pc >= systemData.MaxIterateChunks - 1 && netDebugPacket.IsCreated)
                    netDebugPacket.Log((FixedString64Bytes) $"\tHit MaxIterateChunks:{systemData.MaxIterateChunks}!");
#endif

                if (Hint.Unlikely(dataStream.HasFailedWrites))
                {
                    RevertDespawnGhostState();
                    netDebug.LogError("Size limitation on snapshot did not prevent all errors!");
#if NETCODE_DEBUG
                    if (netDebugPacket.IsCreated)
                        netDebugPacket.Log((FixedString128Bytes) $"█ >> Aborted! Size limitation on snapshot did not prevent all errors!");
#endif
                    return SerializeEnitiesResult.Abort;
                }

                dataStream.Flush();
                lenWriter.WriteUShort((ushort)despawnLen);
                lenWriter.WriteUShort((ushort)totalSentEntities);
#if UNITY_EDITOR
                if (totalSentEntities > 0)
                {
                    UpdateLen[ThreadIndex] += totalSentEntities;
                    UpdateCounts[ThreadIndex] += 1;
                }
#endif
                if (didFillPacket && totalSentEntities == 0)
                {
                    RevertDespawnGhostState();
#if NETCODE_DEBUG
                    if(netDebugPacket.IsCreated)
                        netDebugPacket.Log($"█ >> Failed to even write ONE ghost to the snapshot!");
#endif
                    return SerializeEnitiesResult.Failed;
                }
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"█ >> {dataStream.Length}B on ServerTick:{currentTick.ToFixedString()} SSId:{snapshotAckCopy.CurrentSnapshotSequenceId} | TotalDespawns:{despawnLen} TotalUpdates:{totalSentEntities} via NumChunks:{totalSentChunks}, DidFill:{didFillPacket}, SSId:{snapshotAckCopy.CurrentSnapshotSequenceId}\n\n");
#endif
                return SerializeEnitiesResult.Ok;
            }

            unsafe void RevertDespawnGhostState()
            {
                // TODO - Do we handle this correctly for other stateful data, in general?
                PendingGhostDespawn.RevertSnapshotDespawnWrites(ref *connectionState[connectionIdx].PendingDespawns, currentTick);
            }

            int FindGhostTypeIndex(Entity ent)
            {
                var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                int ghostType;
                var ghostTypeComponent = ghostTypeFromEntity[ent];
                for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                {
                    if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                        break;
                }
                if (ghostType >= GhostCollection.Length)
                {
                    netDebug.LogError("Could not find ghost type in the collection");
                    return -1;
                }
                return ghostType;
            }

            static unsafe IntPtr GetComponentPtrInChunk(
                EntityStorageInfo storageInfo,
                DynamicComponentTypeHandle connectionDataTypeHandle,
                int typeSize)
            {
                var ptr = (byte*)storageInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref connectionDataTypeHandle, typeSize).GetUnsafeReadOnlyPtr();
                ptr += typeSize * storageInfo.IndexInChunk;
                return (IntPtr)ptr;
            }

            unsafe bool TryGetChunkStateOrNew(ArchetypeChunk ghostChunk,
                ref UnsafeHashMap<ArchetypeChunk, GhostChunkSerializationState> chunkStates,
                out GhostChunkSerializationState chunkState)
            {
                using var __ = s_TryGetChunkStateOrNewMarker.Auto();

                if (chunkStates.TryGetValue(ghostChunk, out chunkState))
                {
                    if (chunkState.sequenceNumber == ghostChunk.SequenceNumber)
                    {
                        return true;
                    }

                    chunkState.FreeSnapshotData();
                    chunkStates.Remove(ghostChunk);
                }

                var ghosts = ghostChunk.GetComponentDataPtrRO(ref ghostComponentType);
                if (!TryGetChunkGhostType(ghostChunk, ghosts[0], out var chunkGhostType))
                {
                    return false;
                }

                chunkState.ghostType = chunkGhostType;
                chunkState.sequenceNumber = ghostChunk.SequenceNumber;
                ref readonly var prefabSerializer = ref GhostTypeCollection.ElementAtRO(chunkState.ghostType);
                int serializerDataSize = prefabSerializer.SnapshotSize;
                chunkState.baseImportance = (ushort) math.max(1, prefabSerializer.BaseImportance);
                chunkState.maxSendRateAsSimTickInterval = prefabSerializer.MaxSendRateAsSimTickInterval;
                chunkState.AllocateSnapshotData(serializerDataSize, ghostChunk.Capacity);
                var importanceTick = currentTick;
                // We include MinSendImportance/MaxSendRate because there is no good reason to gate/defer the FIRST SEND of ALL
                // ghost chunks behind this threshold. I.e. It's valid to assume every new ghost wants to be replicated NOW.
                // Therefore, FirstSendImportanceMultiplier is more about HOW MUCH we want to bias the first send of a
                // low importance ghost type (e.g. a tree) ABOVE the resending of a very high importance existing ghost (like the player).
                var maxResendIntervalTicks = math.max((uint) (systemData.MinSendImportance / chunkState.baseImportance), chunkState.maxSendRateAsSimTickInterval);
                importanceTick.Subtract((uint) (systemData.FirstSendImportanceMultiplier * systemData.IrrelevantImportanceDownScale * maxResendIntervalTicks));
                chunkState.SetLastFullUpdate(importanceTick);
                // When relevancy is enabled, our first estimated relevant ghost count has to be 0, because otherwise
                // every new chunk seen by this connection will spike the GhostCount singleton for one tick, even if none are relevant.
                // Unfortunately, working around this requires us to use `math.max(relevantGhostCount, actuallySentGhostCount)`
                // when writing to the stream, and note that this will cause irrelevant downscaling (which is why we account for it above).
                var relevancyEnabled = relevancyMode != GhostRelevancyMode.Disabled;
                var numRelevant = relevancyEnabled ? 0 : ghostChunk.Count;
                chunkState.SetNumRelevant(numRelevant, ghostChunk);

                chunkStates.TryAdd(ghostChunk, chunkState);
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tChunk {ghostChunk.SequenceNumber}, TypeID:{chunkState.ghostType} archetype changed, allocating new one! LastUp:{chunkState.GetLastUpdate().ToFixedString()}, MSR:{chunkState.maxSendRateAsSimTickInterval}!");
#endif
                return true;
            }

            bool TryGetChunkGhostType(ArchetypeChunk ghostChunk, in GhostInstance ghost, out int chunkGhostType)
            {
                chunkGhostType = ghost.ghostType;
                // Pre spawned ghosts might not have a proper ghost type index yet, we calculate it here for pre spawns
                if (chunkGhostType < 0)
                {
                    var ghostEntity = ghostChunk.GetNativeArray(entityType)[0];
                    chunkGhostType = FindGhostTypeIndex(ghostEntity);
                    if (chunkGhostType < 0)
                    {
                        return false;
                    }
                }

                return chunkGhostType < GhostTypeCollection.Length;
            }

            static bool TryGetComponentPtrInChunk(EntityStorageInfo connectionChunkInfo, DynamicComponentTypeHandle typeHandle, int typeSize, out IntPtr componentPtrInChunk)
            {
                var connectionHasType = connectionChunkInfo.Chunk.Has(ref typeHandle);
                componentPtrInChunk = connectionHasType ? GetComponentPtrInChunk(connectionChunkInfo, typeHandle, typeSize) : default;
                return connectionHasType;
            }

            /// <summary>
            /// Collect a list of all chunks which could be serialized and sent. Sort the list so other systems get it in priority order.
            /// Also cleanup any stale ghost state in the map and create new storage buffers for new chunks so all chunks are in a valid state after this has executed.
            /// </summary>
            unsafe void GatherGhostChunksBatch(out GhostChunksContext ctx)
            {
                var prioChunksRef = connectionState[connectionIdx].PrioChunksPtr;
                prioChunksRef->Clear();
                ctx = new GhostChunksContext
                {
                    SerialChunks = prioChunksRef,
                    MaxGhostsPerChunk = 0,
                    TotalRelevantGhosts = 0,
                    NumZeroChangeChunks = 0,
                    NetworkId = networkIdFromEntity[connectionEntity],
                };
                var connectionChunkInfo = childEntityLookup[connectionEntity];
                var connectionHasConnectionData = TryGetComponentPtrInChunk(connectionChunkInfo, ghostConnectionDataTypeHandle, ghostConnectionDataTypeSize, out var connectionDataPtr);
                var chunkStates = connectionState[connectionIdx].SerializationState;

                for (int chunk = 0; chunk < ghostChunks.Length; ++chunk)
                {
                    var ghostChunk = ghostChunks[chunk];
                    if (!TryGetChunkStateOrNew(ghostChunk, ref *chunkStates, out var chunkState))
                    {
                        PacketDumpSkippedNoChunkState(ghostChunk);
                        continue;
                    }

                    chunkState.SetLastValidTick(currentTick);
                    ctx.TotalRelevantGhosts += chunkState.GetNumRelevant();
                    ctx.NumZeroChangeChunks += chunkState.GetFirstZeroChangeVersion() != 0 ? 1 : 0;
                    ctx.MaxGhostsPerChunk = math.max(ctx.MaxGhostsPerChunk, ghostChunk.Count);

                    // Caveat: Entity structural changes completely invalidates both Importance & MaxSendRate.
                    var ticksSinceLastSent = currentTick.TicksSince(chunkState.GetLastUpdate());
                    var allIrrelevant = chunkState.GetAllIrrelevant();
                    var maxSendRate = math.select(chunkState.maxSendRateAsSimTickInterval, chunkState.maxSendRateAsSimTickInterval * systemData.IrrelevantImportanceDownScale, allIrrelevant);
                    if (ticksSinceLastSent < maxSendRate)
                    {
                        PacketDumpSkippedMaxSendRate(ghostChunk, ticksSinceLastSent, maxSendRate);
                        continue;
                    }

                    //Prespawn ghost chunk should be considered only if the subscene wich they belong to as been loaded (acked) by the client.
                    if (ghostChunk.Has(ref prespawnGhostIdType))
                    {
                        var ackedPrespawnSceneMap = connectionState[connectionIdx].AckedPrespawnSceneMap;
                        //Retrieve the subscene hash from the shared component index.
                        var sharedComponentIndex = ghostChunk.GetSharedComponentIndex(subsceneHashSharedTypeHandle);
                        var hash = SubSceneHashSharedIndexMap[sharedComponentIndex];
                        //Skip the chunk if the client hasn't acked/requested streaming that subscene
                        if (!ackedPrespawnSceneMap.ContainsKey(hash))
                        {
                            PacketDumpSkippedPrespawnAndSceneLoadNotYetAcked(ghostChunk, hash);
                            continue;
                        }
                    }

                    if (ghostChunk.Has(ref ghostChildEntityComponentType))
                        continue;

                    var chunkPriority = chunkState.baseImportance * ticksSinceLastSent;
                    if (allIrrelevant)
                        chunkPriority /= systemData.IrrelevantImportanceDownScale;
                    if (chunkPriority < systemData.MinSendImportance)
                    {
                        PacketDumpSkippedMinSendImportance(ghostChunk, chunkPriority);
                        continue;
                    }

                    prioChunksRef->Add(new PrioChunk
                    {
                        chunk = ghostChunk,
                        priority = chunkPriority * systemData.m_ImportanceScalingMultiplier,
                        isRelevant = relevancyMode != GhostRelevancyMode.SetIsRelevant,
                        startIndex = chunkState.GetStartIndex(),
                        ghostType = chunkState.ghostType,
                    });
                }

                // Importance Scaling:
#if NETCODE_DEBUG
                var numChunksCulled = 0;
#endif
                var hasBatched = BatchScaleImportance.Ptr.IsCreated;
                var hasNonBatched = ScaleGhostImportance.Ptr.IsCreated;
                var runImportanceScaling = connectionHasConnectionData && (hasBatched || hasNonBatched);
                if (runImportanceScaling)
                {
                    if (hasBatched)
                    {
                        var func = (delegate *unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, UnsafeList<PrioChunk>*, void>)BatchScaleImportance.Ptr.Value;
                        func(connectionDataPtr, ghostImportanceDataIntPtr,
                            GhostComponentSerializer.IntPtrCast(ref ghostImportancePerChunkTypeHandle),
                            prioChunksRef);
                    }
                    else
                    {
                        for (int i = 0; i < prioChunksRef->Length; ++i)
                        {
                            ref var serialChunk = ref prioChunksRef->ElementAt(i);
                            if (!serialChunk.chunk.Has(ref ghostImportancePerChunkTypeHandle)) continue;
                            IntPtr chunkTile = new IntPtr(serialChunk.chunk.GetDynamicSharedComponentDataAddress(ref ghostImportancePerChunkTypeHandle));
                            var func = (delegate *unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, int>)ScaleGhostImportance.Ptr.Value;
                            serialChunk.priority = func(connectionDataPtr, ghostImportanceDataIntPtr, chunkTile, serialChunk.priority);
                        }
                    }

                    if (systemData.MinDistanceScaledSendImportance > 0)
                    {
                        var chunk = 0;

                        while(chunk < prioChunksRef->Length)
                        {
                            if (prioChunksRef->ElementAt(chunk).priority < systemData.MinDistanceScaledSendImportance)
                            {
                                prioChunksRef->RemoveAtSwapBack(chunk);
#if NETCODE_DEBUG
                                numChunksCulled++;
#endif
                            }
                            else
                            {
                                ++chunk;
                            }
                        }
                    }
                }

                prioChunksRef->Sort();

#if NETCODE_DEBUG
                PacketDumpAddedChunksAndGhostImportance(ctx.SerialChunks, runImportanceScaling, numChunksCulled, connectionHasConnectionData, hasBatched, hasNonBatched);
#endif
            }

#if NETCODE_DEBUG
            private static FixedString32Bytes ToFixedString(SerializeEnitiesResult serializeResult)
            {
                return serializeResult switch
                {
                    SerializeEnitiesResult.Ok => "Ok",
                    SerializeEnitiesResult.Failed => "Fail",
                    SerializeEnitiesResult.Abort => "Abort",
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
#endif
            [Conditional("NETCODE_DEBUG")]
            private unsafe void PacketDumpAddedChunksAndGhostImportance(in UnsafeList<PrioChunk>* serialChunks, bool runImportanceScaling, int numChunksCulled, bool connectionHasConnectionData, bool hasBatched, bool hasNonBatched)
            {
#if NETCODE_DEBUG
                if (netDebugPacket.IsCreated)
                {
                    for (int i = 0; i < serialChunks->Length; ++i)
                    {
                        netDebugPacket.Log($"\tAdded {serialChunks->ElementAt(i).chunk.SequenceNumber} TypeID:{serialChunks->ElementAt(i).ghostType} Priority:{serialChunks->ElementAt(i).priority}");
                    }

                    FixedString64Bytes res = runImportanceScaling ? $"ran & culled {numChunksCulled} chunks!" : "disabled!";
                    netDebugPacket.Log($"\n\tGhostImportance(connHasData:{connectionHasConnectionData}, batched:{hasBatched}, nonBatched:{hasNonBatched}) {res}");
                }
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedMinSendImportance(in ArchetypeChunk ghostChunk, int chunkPriority)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as chunkPriority:{chunkPriority} < minSendImportance:{systemData.MinSendImportance}");
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedPrespawnAndSceneLoadNotYetAcked(in ArchetypeChunk ghostChunk, ulong hash)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as it's a prespawn, and scene {NetDebug.PrintHex(hash)} not yet acked by client");
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedMaxSendRate(in ArchetypeChunk ghostChunk, int ticksSinceLastSent, int maxSendRate)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as {ticksSinceLastSent}<MSR:{maxSendRate}");
#endif
            }
            [Conditional("NETCODE_DEBUG")]
            private void PacketDumpSkippedNoChunkState(in ArchetypeChunk ghostChunk)
            {
#if NETCODE_DEBUG
                if(netDebugPacket.IsCreated)
                    netDebugPacket.Log($"\tSkipping {ghostChunk.SequenceNumber} as no chunkState");
#endif
            }
        }

        /// <inheritdoc/>
        [BurstCompile]
        public unsafe void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (networkTime.NumPredictedTicksExpected == 0)
                // TODO consider striding of sends for off frames (e.g. 120 FPS with 60 ticks/s and 30 (or even 60) sends/s, you'd want to send 1/2 or 1/4 in off frames to smooth the sending over time). round robin would need to be adapted to single world host cases.
                return;
            var systemData = SystemAPI.GetSingleton<GhostSendSystemData>();
#if UNITY_EDITOR || NETCODE_DEBUG
            ref var snapshotStatsSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
            var numLoadedPrefabs = SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs;
            snapshotStatsSingleton.ResetWriter(numLoadedPrefabs);
            snapshotStatsSingleton.MainStatsWrite.Tick = networkTime.ServerTick;
#endif
            // Calculate how many state updates we should send this frame
            SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate);
            tickRate.ResolveDefaults();
            var netTickInterval =
                (tickRate.SimulationTickRate + tickRate.NetworkTickRate - 1) / tickRate.NetworkTickRate;
            var sendThisTick = tickRate.SendSnapshotsForCatchUpTicks || !networkTime.IsCatchUpTick;
            if (sendThisTick)
                ++m_SentSnapshots;

            // Make sure the list of connections and connection state is up to date
            var connections = connectionQuery.ToEntityListAsync(state.WorldUpdateAllocator, out var connectionHandle);

            var relevancySingleton = SystemAPI.GetSingleton<GhostRelevancy>();
            var relevancyMode = relevancySingleton.GhostRelevancyMode;
            EntityQueryMask userGlobalRelevantQueryMask = netcodeEmptyQuery;
            if (relevancySingleton.DefaultRelevancyQuery != default)
                userGlobalRelevantQueryMask = relevancySingleton.DefaultRelevancyQuery.GetEntityQueryMask();

            bool relevancyEnabled = (relevancyMode != GhostRelevancyMode.Disabled);
            // Find the latest tick which has been acknowledged by all clients and cleanup all ghosts destroyed before that
            var currentTick = networkTime.ServerTick;

            // Setup the connections which require cleanup this frame
            // This logic is using length from previous frame, that means we can skip updating connections in some cases
            if (m_ConnectionStates.Length > 0)
                m_CurrentCleanupConnectionState = (m_CurrentCleanupConnectionState + systemData.CleanupConnectionStatePerTick) % m_ConnectionStates.Length;
            else
                m_CurrentCleanupConnectionState = 0;

            // Find the latest tick received by all connections
            m_OldestPendingDespawnTickByAll.Value = currentTick;
            var connectionsToProcess = m_ConnectionsToProcess;
            connectionsToProcess.Clear();
            m_NetworkIdFromEntity.Update(ref state);
            k_Scheduling.Begin();
            state.Dependency = new UpdateConnectionsJob()
            {
                Connections = connections,
                ConnectionStateLookup = m_ConnectionStateLookup,
                ConnectionStates = m_ConnectionStates,
                ConnectionsToProcess = connectionsToProcess,
                OldestPendingDespawnTickByAll = m_OldestPendingDespawnTickByAll,
                NetTickInterval = netTickInterval,
                NetworkIdFromEntity = m_NetworkIdFromEntity,
                SendThisTick = sendThisTick ? (byte)1 : (byte)0,
                SentSnapshots = m_SentSnapshots,
            }.Schedule(JobHandle.CombineDependencies(state.Dependency, connectionHandle));
            k_Scheduling.End();

#if NETCODE_DEBUG
            FixedString128Bytes packetDumpTimestamp = default;
            if (!m_PacketLogEnableQuery.IsEmptyIgnoreFilter)
            {
                state.CompleteDependency();
                NetDebugInterop.GetTimestampWithTick(currentTick, out packetDumpTimestamp);
                FixedString128Bytes worldNameFixed = state.WorldUnmanaged.Name;

                foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>()
                    .WithAll<EnablePacketLogging, NetworkStreamConnection, NetworkStreamInGame>()
                    .WithAll<NetworkStreamInGame>().WithEntityAccess())
                {
                    if (!m_ConnectionStateLookup.ContainsKey(entity))
                        continue;

                    var conState = m_ConnectionStates[m_ConnectionStateLookup[entity]];
                    if (conState.NetDebugPacket.IsCreated)
                        continue;

                    NetDebugInterop.InitDebugPacketIfNotCreated(ref conState.NetDebugPacket, m_LogFolder, worldNameFixed, id.ValueRO.Value);
                    m_ConnectionStates[m_ConnectionStateLookup[entity]] = conState;
                    // Find connection state in the list sent to the serialize job and replace with this updated version
                    for (int i = 0; i < connectionsToProcess.Length; ++i)
                    {
                        if (connectionsToProcess[i].Entity != entity)
                        {
                            continue;
                        }
                        connectionsToProcess[i] = conState;
                        break;
                    }
                }
            }
#endif

            // Prepare a command buffer
            EntityCommandBuffer commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
            var commandBufferConcurrent = commandBuffer.AsParallelWriter();

            // Setup the tick at which ghosts were despawned, cleanup ghosts which have been despawned and acked by al connections
            var freeGhostIds = m_FreeGhostIds.AsParallelWriter();
            var prespawnDespawn = m_DestroyedPrespawnsQueue.AsParallelWriter();
            var freeSpawnedGhosts = m_FreeSpawnedGhostQueue.AsParallelWriter();
            m_PrespawnGhostIdRangeFromEntity.Update(ref state);
            var prespawnIdRanges = m_PrespawnGhostIdRangeFromEntity[SystemAPI.GetSingletonEntity<PrespawnGhostIdRange>()];
            k_Scheduling.Begin();
            state.Dependency = new GhostDespawnParallelJob
            {
                CommandBufferConcurrent = commandBufferConcurrent,
                CurrentTick = currentTick,
                OldestPendingDespawnTickByAll = m_OldestPendingDespawnTickByAll,
                FreeGhostIds = freeGhostIds,
                FreeSpawnedGhosts = freeSpawnedGhosts,
                GhostMap = m_GhostMap,
                PrespawnDespawn = prespawnDespawn,
                PrespawnIdRanges = prespawnIdRanges,
            }.ScheduleParallel(ghostDespawnQuery, state.Dependency);
            k_Scheduling.End();

            // Copy destroyed entities in the parallel write queue populated by ghost cleanup to a single list
            // and free despawned ghosts from map
            k_Scheduling.Begin();
            state.Dependency = new GhostDespawnSingleJob
            {
                DespawnList = m_DestroyedPrespawns,
                DespawnQueue = m_DestroyedPrespawnsQueue,
                FreeSpawnQueue = m_FreeSpawnedGhostQueue,
                GhostMap = m_GhostMap,
            }.Schedule(state.Dependency);
            k_Scheduling.End();

            // If the ghost collection has not been initialized yet the send ystem can not process any ghosts
            if (!SystemAPI.GetSingleton<GhostCollection>().IsInGame)
            {
                return;
            }

            // Extract all newly spawned ghosts and set their ghost ids
            var ghostCollectionSingleton = SystemAPI.GetSingletonEntity<GhostCollection>();
            var spawnChunks = ghostSpawnQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, out var spawnChunkHandle);
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
#if NETCODE_DEBUG
            m_PrefabDebugNameFromEntity.Update(ref state);
#endif
            m_GhostTypeFromEntity.Update(ref state);
            m_GhostComponentType.Update(ref state);
            m_GhostOwnerComponentType.Update(ref state);
            m_EntityType.Update(ref state);
            m_GhostTypeCollectionFromEntity.Update(ref state);
            m_GhostCollectionFromEntity.Update(ref state);
            m_GhostOverrideFromEntity.Update(ref state);
            //The spawnjob assign the ghost id, tick and track the ghost with a cleanup component. If the
            //ghost chunk has a GhostType that has not been processed yet by the GhostCollectionSystem,
            //the chunk is skipped. However, this leave the entities in a limbo state where the data is not setup
            //yet.
            //It is necessary to check always for the cleanup component being added to the chunk in general in the serialization
            //job to ensure the data has been appropriately set.
            var spawnJob = new SpawnGhostJob
            {
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionFromEntity,
                spawnChunks = spawnChunks,
                entityType = m_EntityType,
                ghostComponentType = m_GhostComponentType,
                freeGhostIds = m_FreeGhostIds,
                allocatedGhostIds = m_AllocatedGhostIds,
                commandBuffer = commandBuffer,
                ghostMap = m_GhostMap,
                ghostTypeFromEntity = m_GhostTypeFromEntity,
                ghostOverrideFromEntity = m_GhostOverrideFromEntity,
                serverTick = currentTick,
                forcePreSerialize = (byte) (systemData.ForcePreSerialize ? 1 : 0),
                netDebug = netDebug,
#if NETCODE_DEBUG
                prefabNames = m_PrefabDebugNameFromEntity,
#endif
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                ghostOwnerComponentType = m_GhostOwnerComponentType
#endif
            };
            k_Scheduling.Begin();
            state.Dependency = spawnJob.Schedule(JobHandle.CombineDependencies(state.Dependency, spawnChunkHandle));
            k_Scheduling.End();

            // Create chunk arrays for ghosts and despawned ghosts
            var despawnChunks = ghostDespawnQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, out var despawnChunksHandle);
            var ghostChunks = ghostQuery.ToArchetypeChunkListAsync(state.WorldUpdateAllocator, out var ghostChunksHandle);
            state.Dependency = JobHandle.CombineDependencies(state.Dependency, despawnChunksHandle, ghostChunksHandle);

            SystemAPI.TryGetSingletonEntity<PrespawnSceneLoaded>(out var prespawnSceneLoadedEntity);
            PrespawnHelper.PopulateSceneHashLookupTable(prespawnSharedComponents, state.EntityManager, m_SceneSectionHashLookup);

            ref readonly var networkStreamDriver = ref SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRO;
            // If there are any connections to send data to, serialize the data for them in parallel
            UpdateSerializeJobDependencies(ref state);
            var serializeJob = new SerializeJob
            {
                GhostCollectionSingleton = ghostCollectionSingleton,
                GhostComponentCollectionFromEntity = m_GhostComponentCollectionFromEntity,
                GhostTypeCollectionFromEntity = m_GhostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = m_GhostComponentIndexFromEntity,
                GhostCollectionFromEntity = m_GhostCollectionFromEntity,
                SubSceneHashSharedIndexMap = m_SceneSectionHashLookup,
                concurrentDriverStore = networkStreamDriver.ConcurrentDriverStore,
                despawnChunks = despawnChunks,
                ghostChunks = ghostChunks,
                connectionState = m_ConnectionsToProcess.AsDeferredJobArray(),
                ackFromEntity = m_SnapshotAckFromEntity,
                connectionFromEntity = m_ConnectionFromEntity,
                networkIdFromEntity = m_NetworkIdFromEntity,
                entityType = m_EntityType,
                ghostSystemStateType = m_GhostSystemStateType,
                preSerializedGhostType = m_PreSerializedGhostType,
                prespawnGhostIdType = m_PrespawnedGhostIdType,
                ghostComponentType = m_GhostComponentType,
                ghostGroupType = m_GhostGroupType,
                ghostChildEntityComponentType = m_GhostChildEntityComponentType,
                relevantGhostForConnection = m_GhostRelevancySet,
                userGlobalRelevantMask = userGlobalRelevantQueryMask,
                internalGlobalRelevantMask = internalGlobalRelevantQueryMask,
                relevancyMode = relevancyMode,
#if UNITY_EDITOR || NETCODE_DEBUG
                NetStatsSnapshotPerThread = snapshotStatsSingleton.allGhostStatsParallelWrites.AsArray(),
#endif
                compressionModel = m_CompressionModel,
                ghostFromEntity = m_GhostFromEntity,
                currentTick = currentTick,
                localTime = NetworkTimeSystem.TimestampMS,
                simulationTickRateIntervalMs = (tickRate.SimulationFixedTimeStep * 1000f),
                networkTickRateIntervalTicks = tickRate.CalculateNetworkSendRateInterval(),

                snapshotTargetSizeFromEntity = m_SnapshotTargetFromEntity,
                ghostTypeFromEntity = m_GhostTypeFromEntity,
                allocatedGhostIds = m_AllocatedGhostIds,
                prespawnDespawns = m_DestroyedPrespawns,
                childEntityLookup = state.GetEntityStorageInfoLookup(),
                linkedEntityGroupType = m_LinkedEntityGroupType,
                prespawnBaselineTypeHandle = m_PrespawnGhostBaselineType,
                subsceneHashSharedTypeHandle = m_SubsceneGhostComponentType,
                prespawnSceneLoadedEntity = prespawnSceneLoadedEntity,
                prespawnAckFromEntity = m_PrespawnAckFromEntity,
                prespawnSceneLoadedFromEntity = m_PrespawnSceneLoadedFromEntity,

                CurrentSystemVersion = state.GlobalSystemVersion,
#if NETCODE_DEBUG
                prefabNamesFromEntity = m_PrefabDebugNameFromEntity,
                enableLoggingFromEntity = m_EnablePacketLoggingFromEntity,
                timestamp = packetDumpTimestamp,
#endif
                netDebug = netDebug,
                systemData = systemData,
#if UNITY_EDITOR
                UpdateLen = m_UpdateLen,
                UpdateCounts = m_UpdateCounts,
#endif
            };
            if (!SystemAPI.TryGetSingleton<GhostImportance>(out var importance))
            {
                serializeJob.BatchScaleImportance = default;
                serializeJob.ScaleGhostImportance = default;
            }
            else
            {
                serializeJob.BatchScaleImportance = importance.BatchScaleImportanceFunction;
                serializeJob.ScaleGhostImportance = importance.ScaleImportanceFunctionSuppressedWarning;
            }

            // We don't want to assign default value to type handles as this would lead to a safety error
            if (SystemAPI.TryGetSingletonEntity<GhostImportance>(out var singletonEntity))
            {
                m_GhostImportanceType.Update(ref state);

                var entityStorageInfoLookup = SystemAPI.GetEntityStorageInfoLookup();
                var entityStorageInfo = entityStorageInfoLookup[singletonEntity];

                var ghostImportanceTypeHandle = m_GhostImportanceType;
                GhostImportance config;
                unsafe
                {
                    config = entityStorageInfo.Chunk.GetComponentDataPtrRO(ref ghostImportanceTypeHandle)[entityStorageInfo.IndexInChunk];
                }
                var ghostConnectionDataTypeRO = config.GhostConnectionComponentType;
                var ghostImportancePerChunkDataTypeRO = config.GhostImportancePerChunkDataType;
                var ghostImportanceDataTypeRO = config.GhostImportanceDataType;
                ghostConnectionDataTypeRO.AccessModeType = ComponentType.AccessMode.ReadOnly;
                ghostImportanceDataTypeRO.AccessModeType = ComponentType.AccessMode.ReadOnly;
                ghostImportancePerChunkDataTypeRO.AccessModeType = ComponentType.AccessMode.ReadOnly;
                serializeJob.ghostConnectionDataTypeHandle = state.GetDynamicComponentTypeHandle(ghostConnectionDataTypeRO);
                serializeJob.ghostImportancePerChunkTypeHandle = state.GetDynamicSharedComponentTypeHandle(ghostImportancePerChunkDataTypeRO);
                serializeJob.ghostConnectionDataTypeSize = TypeManager.GetTypeInfo(ghostConnectionDataTypeRO.TypeIndex).TypeSize;

                // Try to get the users importance singleton data from the same "GhostImportance Singleton".
                // If it's not there, don't error, just pass on the null. Thus, treated as optional.
                if (ghostImportanceDataTypeRO.TypeIndex != default && !config.GhostImportanceDataType.IsZeroSized)
                {
                    var ghostImportanceDataTypeSize = TypeManager.GetTypeInfo(ghostImportanceDataTypeRO.TypeIndex).TypeSize;
                    var ghostImportanceDynamicTypeHandle = state.GetDynamicComponentTypeHandle(ghostImportanceDataTypeRO);

                    var hasGhostImportanceTypeInSingletonChunk = entityStorageInfo.Chunk.Has(ref ghostImportanceTypeHandle);
                    unsafe
                    {
                        serializeJob.ghostImportanceDataIntPtr = hasGhostImportanceTypeInSingletonChunk
                            ? (IntPtr) entityStorageInfo.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref ghostImportanceDynamicTypeHandle, ghostImportanceDataTypeSize).GetUnsafeReadOnlyPtr()
                            : IntPtr.Zero;
                    }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (!hasGhostImportanceTypeInSingletonChunk)
                        throw new InvalidOperationException($"You configured your `GhostImportance` singleton to expect that the type '{ghostImportanceDataTypeRO.ToFixedString()}' would also be added to this singleton entity, but the singleton entity does not contain this type. Either remove this requirement, or add this component to the singleton.");
#endif
                }
                else
                {
                    serializeJob.ghostImportanceDataIntPtr = IntPtr.Zero;
                }
            }
            else
            {
                serializeJob.ghostImportancePerChunkTypeHandle = state.GetDynamicSharedComponentTypeHandle(new ComponentType { TypeIndex = TypeIndex.Null, AccessModeType = ComponentType.AccessMode.ReadOnly });
            }

            var ghostComponentCollection = state.EntityManager.GetBuffer<GhostCollectionComponentType>(ghostCollectionSingleton);
            m_GhostTypeComponentType.Update(ref state);

            k_Scheduling.Begin();
            state.Dependency = m_GhostPreSerializer.Schedule(state.Dependency,
                serializeJob.GhostComponentCollectionFromEntity,
                serializeJob.GhostTypeCollectionFromEntity,
                serializeJob.GhostComponentIndexFromEntity,
                serializeJob.GhostCollectionSingleton,
                serializeJob.GhostCollectionFromEntity,
                serializeJob.linkedEntityGroupType,
                serializeJob.childEntityLookup,
                serializeJob.ghostComponentType,
                m_GhostTypeComponentType,
                serializeJob.entityType,
                serializeJob.ghostFromEntity,
                serializeJob.connectionState,
                serializeJob.netDebug,
                currentTick,
                systemData.m_UseCustomSerializer ? 1 : 0,
                ref state,
                ghostComponentCollection);
            k_Scheduling.End();
            serializeJob.SnapshotPreSerializeData = m_GhostPreSerializer.SnapshotData;

            DynamicTypeList.PopulateList(ref state, ghostComponentCollection, true, ref serializeJob.DynamicGhostCollectionComponentTypeList);

            k_Scheduling.Begin();
            var jobHandle = serializeJob.ScheduleByRef(m_ConnectionsToProcess, 1, state.Dependency);
            m_ConnectionStatesJobHandle = jobHandle;
            state.Dependency = jobHandle;
            k_Scheduling.End();

            var serializeHandle = state.Dependency;
            // Schedule a job to clean up connections
            k_Scheduling.Begin();
            var cleanupHandle = new CleanupGhostSerializationStateJob
            {
                CleanupConnectionStatePerTick = systemData.CleanupConnectionStatePerTick,
                CurrentCleanupConnectionState = m_CurrentCleanupConnectionState,
                ConnectionStates = m_ConnectionStates,
                GhostChunks = ghostChunks,
            }.Schedule(state.Dependency);
            var flushHandle = networkStreamDriver.DriverStore.ScheduleFlushSendAllDrivers(serializeHandle);
            k_Scheduling.End();
            state.Dependency = JobHandle.CombineDependencies(flushHandle, cleanupHandle);
#if NETCODE_DEBUG && !USING_UNITY_LOGGING
            state.Dependency = new FlushNetDebugPacket
            {
                EnablePacketLogging = m_EnablePacketLoggingFromEntity,
                ConnectionStates = m_ConnectionsToProcess.AsDeferredJobArray(),
            }.Schedule(m_ConnectionsToProcess, 1, state.Dependency);
#endif
        }

        void UpdateSerializeJobDependencies(ref SystemState state)
        {
#if NETCODE_DEBUG
            m_PrefabDebugNameFromEntity.Update(ref state);
#endif
            m_GhostTypeFromEntity.Update(ref state);
            m_SnapshotTargetFromEntity.Update(ref state);
            m_GhostGroupType.Update(ref state);
            m_GhostComponentType.Update(ref state);
            m_NetworkIdFromEntity.Update(ref state);
            m_GhostTypeCollectionFromEntity.Update(ref state);
            m_GhostCollectionFromEntity.Update(ref state);
            m_SnapshotAckFromEntity.Update(ref state);
            m_ConnectionFromEntity.Update(ref state);
            m_GhostFromEntity.Update(ref state);
            m_SnapshotTargetFromEntity.Update(ref state);
            m_EnablePacketLoggingFromEntity.Update(ref state);
            m_GhostSystemStateType.Update(ref state);
            m_PreSerializedGhostType.Update(ref state);
            m_GhostChildEntityComponentType.Update(ref state);
            m_PrespawnedGhostIdType.Update(ref state);
            m_GhostGroupType.Update(ref state);
            m_EntityType.Update(ref state);
            m_LinkedEntityGroupType.Update(ref state);
            m_PrespawnGhostBaselineType.Update(ref state);
            m_SubsceneGhostComponentType.Update(ref state);
            m_GhostComponentCollectionFromEntity.Update(ref state);
            m_GhostComponentIndexFromEntity.Update(ref state);
            m_PrespawnAckFromEntity.Update(ref state);
            m_PrespawnSceneLoadedFromEntity.Update(ref state);
        }

        [BurstCompile]
        struct UpdateConnectionsJob : IJob
        {
            [ReadOnly] public ComponentLookup<NetworkId> NetworkIdFromEntity;
            public NativeList<Entity> Connections;
            public NativeParallelHashMap<Entity, int> ConnectionStateLookup;
            public NativeList<ConnectionStateData> ConnectionStates;
            public NativeList<ConnectionStateData> ConnectionsToProcess;
            public NativeReference<NetworkTick> OldestPendingDespawnTickByAll;
            public byte SendThisTick;
            public int NetTickInterval;
            public uint SentSnapshots;

            public void Execute()
            {
                var existing = new NativeParallelHashSet<Entity>(Connections.Length, Allocator.Temp);
                int maxConnectionId = 0;
                var oldestPendingByAll = OldestPendingDespawnTickByAll.Value;
                foreach (var connection in Connections)
                {
                    existing.Add(connection);
                    if (!ConnectionStateLookup.TryGetValue(connection, out var stateIndex))
                    {
                        stateIndex = ConnectionStates.Length;
                        ConnectionStates.Add(ConnectionStateData.Create(connection));
                        ConnectionStateLookup.TryAdd(connection, stateIndex);
                    }
                    maxConnectionId = math.max(maxConnectionId, NetworkIdFromEntity[connection].Value);

                    var oldestPendingDespawnTick = ConnectionStates[stateIndex].GhostStateData.OldestPendingDespawnTick;
                    if (!oldestPendingDespawnTick.IsValid)
                        oldestPendingByAll = NetworkTick.Invalid;
                    else if (oldestPendingByAll.IsValid && oldestPendingByAll.IsNewerThan(oldestPendingDespawnTick))
                        oldestPendingByAll = oldestPendingDespawnTick;
                }
                OldestPendingDespawnTickByAll.Value = oldestPendingByAll;

                for (int i = 0; i < ConnectionStates.Length; ++i)
                {
                    if (existing.Contains(ConnectionStates[i].Entity))
                    {
                        continue;
                    }

                    ConnectionStateLookup.Remove(ConnectionStates[i].Entity);
                    ConnectionStates[i].Dispose();
                    if (i != ConnectionStates.Length - 1)
                    {
                        ConnectionStates[i] = ConnectionStates[ConnectionStates.Length - 1];
                        ConnectionStateLookup.Remove(ConnectionStates[i].Entity);
                        ConnectionStateLookup.TryAdd(ConnectionStates[i].Entity, i);
                    }

                    ConnectionStates.RemoveAtSwapBack(ConnectionStates.Length - 1);
                    --i;
                }

                if (SendThisTick == 0)
                    return;
                var sendPerFrame = (ConnectionStates.Length + NetTickInterval - 1) / NetTickInterval;
                var sendStartPos = sendPerFrame * (int) (SentSnapshots % NetTickInterval);

                if (sendStartPos + sendPerFrame > ConnectionStates.Length)
                    sendPerFrame = ConnectionStates.Length - sendStartPos;
                for (int i = 0; i < sendPerFrame; ++i)
                    ConnectionsToProcess.Add(ConnectionStates[sendStartPos + i]);
            }
        }

#if NETCODE_DEBUG && !USING_UNITY_LOGGING
        struct FlushNetDebugPacket : IJobParallelForDefer
        {
            [ReadOnly] public ComponentLookup<EnablePacketLogging> EnablePacketLogging;
            [ReadOnly] public NativeArray<ConnectionStateData> ConnectionStates;
            public void Execute(int index)
            {
                var state = ConnectionStates[index];
                if (EnablePacketLogging.HasComponent(state.Entity))
                {
                    state.NetDebugPacket.Flush();
                }
            }
        }
#endif

        [BurstCompile]
        struct CleanupGhostSerializationStateJob : IJob
        {
            public int CleanupConnectionStatePerTick;
            public int CurrentCleanupConnectionState;
            [ReadOnly] public NativeList<ConnectionStateData> ConnectionStates;
            [ReadOnly] public NativeList<ArchetypeChunk> GhostChunks;

            public unsafe void Execute()
            {
                var conCount = math.min(CleanupConnectionStatePerTick, ConnectionStates.Length);
                var existingChunks = new UnsafeHashMap<ArchetypeChunk, ulong>(GhostChunks.Length, Allocator.Temp);
                foreach (var chunk in GhostChunks)
                {
                    existingChunks.TryAdd(chunk, chunk.SequenceNumber);
                }
                for (int con = 0; con < conCount; ++con)
                {
                    var conIdx = (con + CurrentCleanupConnectionState) % ConnectionStates.Length;
                    var chunkSerializationData = ConnectionStates[conIdx].SerializationState;
                    var oldChunks = chunkSerializationData->GetKeyArray(Allocator.Temp);
                    foreach (var oldChunk in oldChunks)
                    {
                        if (existingChunks.TryGetValue(oldChunk, out var sequence) && sequence == oldChunk.SequenceNumber)
                        {
                            continue;
                        }
                        GhostChunkSerializationState chunkState;
                        chunkSerializationData->TryGetValue(oldChunk, out chunkState);
                        chunkState.FreeSnapshotData();
                        chunkSerializationData->Remove(oldChunk);
                    }
                }
            }
        }

        [BurstCompile]
        struct GhostDespawnSingleJob : IJob
        {
            public NativeQueue<SpawnedGhost> FreeSpawnQueue;
            public NativeQueue<int> DespawnQueue;
            public NativeList<int> DespawnList;
            public NativeParallelHashMap<SpawnedGhost, Entity> GhostMap;

            public void Execute()
            {
                while (DespawnQueue.TryDequeue(out int destroyed))
                {
                    if (!DespawnList.Contains(destroyed))
                    {
                        DespawnList.Add(destroyed);
                    }
                }

                while (FreeSpawnQueue.TryDequeue(out var spawnedGhost))
                {
                    GhostMap.Remove(spawnedGhost);
                }
            }
        }

        [BurstCompile]
        partial struct GhostDespawnParallelJob : IJobEntity
        {
            [ReadOnly] public NativeParallelHashMap<SpawnedGhost, Entity> GhostMap;
            [ReadOnly] public NativeReference<NetworkTick> OldestPendingDespawnTickByAll;
            [ReadOnly] public DynamicBuffer<PrespawnGhostIdRange> PrespawnIdRanges;
            public EntityCommandBuffer.ParallelWriter CommandBufferConcurrent;
            public NativeQueue<int>.ParallelWriter PrespawnDespawn;
            public NativeQueue<int>.ParallelWriter FreeGhostIds;
            public NativeQueue<SpawnedGhost>.ParallelWriter FreeSpawnedGhosts;
            public NetworkTick CurrentTick;

            public void Execute(Entity entity, [EntityIndexInQuery]int entityIndexInQuery, ref GhostCleanup ghost)
            {
                var oldestPendingByAll = OldestPendingDespawnTickByAll.Value;
                if (!ghost.despawnTick.IsValid)
                {
                    ghost.despawnTick = CurrentTick;
                }
                else if (oldestPendingByAll.IsValid && oldestPendingByAll.IsNewerThan(ghost.despawnTick))
                {
                    if (PrespawnHelper.IsRuntimeSpawnedGhost(ghost.ghostId))
                        FreeGhostIds.Enqueue(ghost.ghostId);
                    CommandBufferConcurrent.RemoveComponent<GhostCleanup>(entityIndexInQuery, entity);
                }
                //Remove the ghost from the mapping as soon as possible, regardless of clients acknowledge
                var spawnedGhost = new SpawnedGhost {ghostId = ghost.ghostId, spawnTick = ghost.spawnTick};
                if (!GhostMap.ContainsKey(spawnedGhost))
                {
                    return;
                }
                FreeSpawnedGhosts.Enqueue(spawnedGhost);
                //If there is no allocated range, do not add to the queue. That means the subscene the
                //prespawn belongs to has been unloaded
                if (PrespawnHelper.IsPrespawnGhostId(ghost.ghostId) && PrespawnIdRanges.GhostIdRangeIndex(ghost.ghostId) >= 0)
                    PrespawnDespawn.Enqueue(ghost.ghostId);
            }
        }

    }
}
