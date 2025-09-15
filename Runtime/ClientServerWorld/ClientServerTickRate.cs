using System;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.NetCode
{
    /// <summary>
    /// The ClientServerTickRate singleton is used to configure the client and server simulation time step,
    /// server packet send rate and other related settings.
    /// The singleton entity is automatically created for the clients in the <see cref="Unity.NetCode.NetworkStreamReceiveSystem"/>
    /// first update if not present.
    /// On the server, by contrast, the entity is never automatically created and it is up to the user to create the singletong instance if
    /// they need to.
    /// This behaviour is asymmetric because the client need to have this singleton data synced with the server one. It is like
    /// this for compatibility reason and It may be changed in the future.
    /// In order to configure these settings you can either:
    /// <list type="bullet">
    /// <item> Create the entity in a custom Unity.NetCode.ClientServerBootstrap after the worlds has been created.</item>
    /// <item> On a system, in either the OnCreate or OnUpdate.</item>
    /// </list>
    /// It is not mandatory to set all the fields to a proper value when creating the singleton. It is sufficient to change only the relevant setting, and call the <see cref="ResolveDefaults"/> method to
    /// configure the fields that does not have a value set.
    /// The <see cref="ClientServerTickRate"/> settings are synced as part of the of the initial client connection handshake.
    /// (<see cref="Unity.NetCode.ClientServerTickRateRefreshRequest"/> data).
    /// The ClientServerTickRate should also be used to customise other server only timing settings, such as
    /// <list type="bullet">
    /// <item>the maximum number of tick per frame</item>
    /// <item>the maximum number of tick per frame</item>
    /// <item>tick batching (&lt;`MaxSimulationStepBatchSize`) and others.</item>
    /// </list>
    /// See the individual fields documentation for more information.
    /// </summary>
    /// <example>
    /// <code>
    /// class MyCustomClientServerBootstrap : ClientServerBootstrap
    /// {
    ///    override public void Initialize(string defaultWorld)
    ///    {
    ///        base.Initialise(defaultWorld);
    ///        var customTickRate = new ClientServerTickRate();
    ///        //run at 30hz
    ///        customTickRate.simulationTickRate = 30;
    ///        customTickRate.ResolveDefault();
    ///        foreach(var world in World.All)
    ///        {
    ///            if(world.IsServer())
    ///            {
    ///               //In this case we only create on the server, but we can do the same also for the client world
    ///               var tickRateEntity = world.EntityManager.CreateSingleton(new ClientServerTickRate
    ///               {
    ///                   SimulationTickRate = 30;
    ///               });
    ///            }
    ///        }
    ///    }
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// Once the client is connected, changes to the ClientServerTickRate are not replicated. If you change the settings are runtime, the same change must
    /// be done on both client and server.
    /// </item>
    /// <item>
    /// The ClientServerTickRate should never be added to sub-scene with a baker. In case you want to setup the ClientServerTickRate
    /// based on some scene settings, we suggest to implement your own component and change the ClientServerTickRate inside a system in
    /// your game.
    /// </item>
    /// </list>
    /// </remarks>
    [Serializable]
    public struct ClientServerTickRate : IComponentData
    {
        /// <summary>
        /// Enum to control how the simulation should deal with running at a higher frame rate than simulation rate.
        /// </summary>
        public enum FrameRateMode
        {
            /// <summary>
            /// Use `Sleep` if running in a server-only build, otherwise use `BusyWait`.
            /// </summary>
            Auto,
            /// <summary>
            /// Let the game loop run at full frequency and skip simulation updates if the accumulated delta time is less than the simulation frequency.
            /// </summary>
            BusyWait,
            /// <summary>
            /// Use `Application.TargetFrameRate` to limit the game loop frequency to the simulation frequency.
            /// </summary>
            Sleep
        }

        /// <summary>
        /// The fixed simulation frequency on the server and prediction loop.
        /// The client can render at a higher or lower rate than this.
        /// Default: 60Hz.
        /// </summary>
        /// <remarks>
        /// Note: Clients are not locked to this refresh rate (see Partial Ticks documentation).
        /// Higher values increase gameplay quality, but incur higher CPU and bandwidth costs.
        /// Higher values are particularly expensive on the client, as prediction cost increases.
        /// </remarks>
        [Tooltip("The fixed simulation frequency of the Netcode gameplay simulation. Higher values incur higher CPU costs on both the client and server, especially during client prediction.")]
        [Min(1)]
        public int SimulationTickRate;

        /// <summary>
        /// Multiplier used to calculate the tick rate (i.e. frequency) for the <see cref="PredictedFixedStepSimulationSystemGroup"/>.
        /// The group rate must be an integer multiple of the <see cref="SimulationTickRate"/>.
        /// The default value is 1, meaning that the <see cref="PredictedFixedStepSimulationSystemGroup"/> run at the same frequency
        /// as the prediction loop.
        /// The calculated delta is 1.0/(SimulationTickRate*PredictedFixedStepSimulationTickRatio).
        /// </summary>
        [Tooltip("Multiplier used to calculate the tick rate (i.e. frequency) for the PredictedFixedStepSimulationSystemGroup.\n\nThe default (and recommendation) is 0 (which becomes 1 i.e. one fixed step per tick), where higher values allow physics to tick more frequently (i.e. at smaller intervals).")]
        [Range(0, 8)]
        public int PredictedFixedStepSimulationTickRatio;

        /// <summary>1f / <see cref="SimulationTickRate"/>. Think of this as the netcode version of `fixedDeltaTime`.</summary>
        public float SimulationFixedTimeStep => 1f / SimulationTickRate;

        /// <summary>
        /// The fixed time used to run the physics simulation. Is always an integer multiple of the SimulationFixedTimeStep. <br/>
        /// The value is equal to 1f / (<see cref="SimulationTickRate"/> * <see cref="PredictedFixedStepSimulationTickRatio"/>).
        /// </summary>
        public float PredictedFixedStepSimulationTimeStep => 1f / (PredictedFixedStepSimulationTickRatio*SimulationTickRate);

        /// <summary>
        /// The rate at which the server creates (and sends) a snapshots to each client.
        /// This can be lower than than the simulation frequency, which means the server only sends new snapshots to the clients
        /// every N frames.
        /// Defaults to the <see cref="SimulationTickRate"/>.
        /// </summary>
        /// <remarks>
        /// The CPU work performed to build and send snapshots (via <see cref="GhostSendSystem"/>) is often the most significant
        /// CPU cost in a multiplayer game. Thus, reducing this send-rate can lead to significant CPU savings, but at
        /// the expense of gameplay quality (especially when packets are lost to the network).
        /// Note that the server can still send data on every simulation tick, but to different subsets of clients. This is to distribute CPU
        /// load over multiple simulation ticks (to avoid CPU spikes). For example, with a NetworkTickRate of 30 and a SimulationTickRate of 60,
        /// the server will send snapshots to half of the clients for one tick, and the other half, the next tick. So each client still end up with
        /// a packet every 2 simulation ticks, while the server is distributing the CPU load over each tick (via a 'round robin' strategy).
        /// </remarks>
        [Tooltip("The rate at which the server creates (and sends) a snapshot to each client.\n\nIf zero (the default), this value will be set to the <b>SimulationTickRate</b>, but half (or one third) is often good enough.\n\nThe CPU work performed to build and send snapshots is often the most significant CPU cost in a multiplayer game. Thus, reducing this send-rate can lead to significant CPU savings, but at the expense of gameplay quality (especially when packets are lost to the network).")]
        [Min(0)]
        public int NetworkTickRate;
        /// <summary>
        /// If the server cannot keep up with the passing of realtime (i.e. the server is ticking at too low a rate to
        /// match the <see cref="SimulationTickRate"/>), it will perform multiple ticks in a single frame (in an attempt to 'catch up').
        /// This setting puts a limit on how many such updates it can perform in a single frame.
        /// Once this limit is reached, the simulation time will update slower than real time.
        /// The default value is 1.
        /// </summary>
        /// <remarks>
        /// The network tick rate only applies to snapshots, the frequency commands and RPCs is not
        /// affected by this setting.
        /// </remarks>
        [Tooltip("Denotes how many fixed-step ticks can be performed on any given Unity frame, when 'catching up', when running too slowly.\n\nDefault value is 0 (which becomes 1).")]
        [Range(0, 16)]
        public int MaxSimulationStepsPerFrame;
        /// <summary>
        /// If the server cannot keep up with the simulation frequency with running `MaxSimulationStepsPerFrame`
        /// ticks, it is possible to allow each tick to run with a longer delta time in order to keep the game
        /// time updating correctly. This means that instead of running two ticks with delta time N each, the
        /// system will run a single tick with delta time 2*N. It is a less expensive but more inaccurate way
        /// of dealing with server performance spikes, it also requires the game logic to be able to handle it.
        /// </summary>
        [Tooltip("Denotes how many individual ticks will be batched together (into a single tick) when recovering from a severe slowdown.\n\nDefault value is 0 (which becomes 4).\n\n<b>Warning: You lose accuracy when batching ticks, and gameplay code must account for it.</b>")]
        [Range(0, 16)]
        public int MaxSimulationStepBatchSize;
        /// <summary>
        /// If the server is capable of updating more often than the simulation tick rate, it can either
        /// skip the simulation tick for some updates (`BusyWait`), or limit the updates using
        /// `Application.TargetFrameRate` (`Sleep`). `Auto` makes it use `Sleep` for dedicated server
        /// builds and `BusyWait` for client and server builds (as well as the editor).
        /// </summary>
        [Tooltip("Denotes how the server should sleep, when determining when it should next tick.\n\nDefaults to <b>Auto</b>, which will use <b>Sleep</b> for dedicated server builds, and <b>BusyWait</b> for client and server builds (as well as the editor).")]
        public FrameRateMode TargetFrameRateMode;
        /// <summary>
        /// If the server has to run multiple simulation ticks in the same frame, the server can either
        /// send snapshots for all those ticks (true), or just the last one (false).
        /// </summary>
        public bool SendSnapshotsForCatchUpTicks
        {
            get { return m_SendSnapshotsForCatchUpTicks; }
            set { m_SendSnapshotsForCatchUpTicks = value; }
        }

        [Tooltip("When the server has to run multiple simulation ticks in the same frame (to catch-up), this flag denotes whether or not the server will send snapshots for all catch-up ticks, or just the last one. Default is <b>false</b> (only the last).")]
        [SerializeField]
        private bool m_SendSnapshotsForCatchUpTicks;

        /// <summary>
        ///     Netcode needs to store a history of snapshot acknowledgements ("acks") on the server - one per connection.
        ///     This denotes the size of said history buffer, in bits, and is exposed only to allow further patching of an esoteric
        ///     issue (see remarks). Default value is 4096 bits (0.5KB), which should prevent this issue in the common case.
        ///     Previous hardcoded default was 256 bits.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Due to <see cref="GhostSendSystem" /> priority queue mechanics, increasing this value may fix errors where:
        ///         <list type="bullet">
        ///             <item>Static ghosts never stop resending.</item>
        ///             <item>
        ///                 Static and dynamic ghosts do not correctly find their 'baselines' (i.e. previously send and acked
        ///                 values), when attempting delta-compression.
        ///             </item>
        ///         </list>
        ///     </para>
        ///     <para>
        ///         Per connection, per chunk, netcode stores up to 32 previous snapshots (and thus baselines, and their
        ///         acks) in a circular/ring buffer (<see cref="LowLevel.Unsafe.GhostChunkSerializationState" /> and
        ///         <see cref="GhostSystemConstants.SnapshotHistorySize" />). This ring-buffer appends an entry
        ///         every time the chunk is successfully serialized into a snapshot writer.
        ///     </para>
        ///     <para>
        ///         The problem is: When you have tens of thousands of relevant ghosts for a single connection
        ///         (a case we strongly advise against), the priority queue will only "bubble up" a chunk to be resent
        ///         after many tens of seconds. You can very loosely approximate the lower bound of this via
        ///         <c>(((numGhosts/avgNumGhostsPerChunk)*averageSizeOfChunkInBytes)/transportMTU)/NetworkTickRate</c>
        ///         E.g. 100k well optimized ghosts, sent at 30Hz (Simulation 60Hz), is <c>(((100000/40)*1200)/1400)/30 = ~72s</c>
        ///         to replicate them all once. I.e. ~4285 simulation ticks will have occurred since the client
        ///         was sent the previously sent snapshot.
        ///     </para>
        ///     <para>
        ///         Thus, when we check the ack buffer ~72 seconds later, the ack has long since been bit-shifted off
        ///         the end of the 256 tick history buffer. The simplest solution (implemented here) is to store
        ///         an ack buffer that is considerably larger. It is now 4096 entries by default (i.e. ~1.1 minutes at 60Hz),
        ///         and 1024 entries at a minimum (~17s at 60Hz), whereas the previous default was 256 (i.e. ~4.26s at 60Hz).
        ///         <b>This field configures said capacity.</b>
        ///     </para>
        ///     <para>
        ///         Because we are now able to find acks for snapshots sent over 4.26s ago, this fixed a
        ///         regression in delta-compression performance (as, previously, the baseline was found,
        ///         but treated as un-acked, thus unable to be used).
        ///     </para>
        ///     <para>
        ///         We also previously failed to mark this chunk as having 'no changes' (via <c>isZeroChange</c>),
        ///         as a ghost having 'no change' relies on its current value being compared to any of its acked
        ///         baseline values. This means we previously could not early out via <c>CanUseStaticOptimization</c>
        ///         (which looks for zero change). As a result, we frequently saw resending of previously acked
        ///         static ghosts in these circumstances (at least until the server so happens to try to resend
        ///         the same chunk within <see cref="SnapshotAckMaskCapacity" /> ticks of a previous ack).
        ///     </para>
        ///     <para>
        ///         Similarly, if you implemented configuration options like <see cref="GhostSendSystemData.MinSendImportance" />,
        ///         we would delay processing of a chunk artificially. If this delay happened to exceed capacity,
        ///         the chunk (and its ghosts) can never possibly ack. Thankfully, <c>SnapshotAckMaskCapacity</c>
        ///         is now far higher than we'd ever recommend setting <c>MinSendImportance</c>.
        ///     </para>
        /// </remarks>
        [Tooltip("Denotes how many entries the snapshot ack history BitArray stores. Default value: 4096 bits. Min: 1024 bits.\n\nSolves an emergent problem when replicating tens of thousands of relevant static ghosts to a single connection - a case we strongly advise against. See XML doc.")]
        public uint SnapshotAckMaskCapacity;

        /// <summary>
        /// On the client, Netcode attempts to align its own fixed step with the render refresh rate, with the goal of
        /// reducing Partial ticks, and increasing stability. This setting denotes the window (in %) to snap and align.
        /// Defaults to 5 (5%), which is applied each way: I.e. If you're within 5% of the last full tick, or if you're
        /// within 5% of the next full tick, we'll clamp.
        /// -1 is 'turn clamping off', 0 is 'use default'.
        /// Max value is 50 (i.e. 50% each way, leading to full clamping, as it's applied in both directions).
        /// </summary>
        /// <remarks>High values will lead to more aggressive alignment, which may be perceivable (as we'll need to shift time further).</remarks>
        public int ClampPartialTicksThreshold
        {
            readonly get => m_ClampPartialTicksThreshold;
            set => m_ClampPartialTicksThreshold = value;
        }
        [Tooltip("On the client, Netcode attempts to align its own fixed step with the render refresh rate, with the goal of reducing Partial ticks, and increasing stability.\n\nThis setting denotes the window (in %) to snap and align.\n\nDefaults to 5 (5%), which is applied each way.\nI.e. If you're within 5% of the last full tick, or if you're within 5% of the next full tick, we'll clamp. 50 (50%) to always clamp.")]
        [SerializeField]
        [Range(-1, 50)]
        private int m_ClampPartialTicksThreshold;

        /// <summary>
        /// The timeout for the connection handshake and approval procedure.
        /// Note: This is one counter for both states. In other words: The client must complete both Handshake
        /// and Approval before this timeout expires - it's not reset upon entering Approval.
        /// <br/>As soon as the client is accepted on the server, the timer will start.
        /// Timeout will occur if the server has not handshaked and approved the connection
        /// within the given duration. The default is 5000ms.
        /// </summary>
        /// <remarks>
        /// The overall timeout sequence when a client is connecting is:
        /// <br/>   1. The client goes through the transport-level connection timeout first (max connect attempt * connect timeout).
        /// <br/>   2. Then, once the UTP connection succeeds, netcode begins the handshake process, where protocol
        /// version RPCs are automatically exchanged.
        /// <br/>   3. If the client protocol is valid, the server will move the client to either the connected state,
        ///  or to the approval state (if approval is enabled via <see cref="NetworkStreamDriver.RequireConnectionApproval"/>).
        /// <br/>This timeout applies to both the Handshake and Approval elapsed durations. It's a single timer for both.
        /// </remarks>
        [Tooltip("The timeout for the connection handshake and approval procedure. Both must succeed within the allotted time!\n\nDefaults to 0ms (which becomes 5s).")]
        [Range(0, 120_000)]
        public uint HandshakeApprovalTimeoutMS;

        internal const int DefaultTickRate = 60;
        internal const int DefaultMaxSimulationStepsPerFrame = 1;
        internal const int DefaultMaxSimulationStepBatchSize = 4;
        internal const int DefaultPredictedFixedStepSimulationTickRatio = 1;
        internal const int DefaultHandshakeApprovalTimeoutMS = 5_000;

        /// <summary>
        /// Set all the properties that haven't been changed by the user (or that have invalid ranges) to a proper default value.
        /// In particular, this guarantees that both <see cref="NetworkTickRate"/> and <see cref="SimulationTickRate"/> are never 0.
        /// </summary>
        public void ResolveDefaults()
        {
            if (SimulationTickRate <= 0)
                SimulationTickRate = DefaultTickRate;
            if (PredictedFixedStepSimulationTickRatio <= 0)
                PredictedFixedStepSimulationTickRatio = DefaultPredictedFixedStepSimulationTickRatio;
            if (NetworkTickRate <= 0)
                NetworkTickRate = SimulationTickRate;
            if (NetworkTickRate > SimulationTickRate)
                NetworkTickRate = SimulationTickRate;
            if (MaxSimulationStepsPerFrame <= 0)
                MaxSimulationStepsPerFrame = DefaultMaxSimulationStepsPerFrame;
            if (MaxSimulationStepBatchSize <= 0)
                MaxSimulationStepBatchSize = DefaultMaxSimulationStepBatchSize;
            if (SnapshotAckMaskCapacity == 0)
                SnapshotAckMaskCapacity = 4096;
            if (ClampPartialTicksThreshold == 0)
                ClampPartialTicksThreshold = 5;
            if (HandshakeApprovalTimeoutMS == 0)
                HandshakeApprovalTimeoutMS = DefaultHandshakeApprovalTimeoutMS;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal readonly void Validate()
        {
            FixedList4096Bytes<FixedString64Bytes> errors = default;
            ValidateAll(ref errors);
            if (errors.Length > 0)
                throw new ArgumentException(errors[0].ToString());
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal readonly void ValidateAll(ref FixedList4096Bytes<FixedString64Bytes> errors)
        {
            // ReSharper is technically correct here, some of these are impossible when you use the NetCodeConfig, thanks to attribute validation.
            // But users can modify these values directly in C#, so we must validate them.
            // ReSharper disable ConditionIsAlwaysTrueOrFalse
            if (SimulationTickRate <= 0)
                errors.Add($"{nameof(SimulationTickRate)} must always be > 0");
            if (PredictedFixedStepSimulationTickRatio <= 0)
                errors.Add($"{nameof(PredictedFixedStepSimulationTickRatio)} must always be > 0");
            if (NetworkTickRate <= 0)
                errors.Add($"{nameof(NetworkTickRate)} must always be > 0");
            if (NetworkTickRate > SimulationTickRate)
                errors.Add($"{nameof(NetworkTickRate)} must always be <= {nameof(SimulationTickRate)}");
            if (MaxSimulationStepsPerFrame <= 0)
                errors.Add($"{nameof(MaxSimulationStepsPerFrame)} must always be > 0");
            if (MaxSimulationStepBatchSize <= 0)
                errors.Add($"{nameof(MaxSimulationStepBatchSize)} must always be > 0");
            if (SnapshotAckMaskCapacity < 1024)
                errors.Add($"{nameof(SnapshotAckMaskCapacity)} has a minimum size of 1024");
            if (ClampPartialTicksThreshold > 50)
                errors.Add($"{nameof(ClampPartialTicksThreshold)} must always within be [-1, 50]");
            if(HandshakeApprovalTimeoutMS < 1000)
                errors.Add($"{nameof(HandshakeApprovalTimeoutMS)} must be >= 1000ms");
            // ReSharper restore ConditionIsAlwaysTrueOrFalse
        }

        /// <summary>
        /// Helper:
        /// Returns 1 when NetworkTickRate is equal to (or close enough - via rounding - to) SimulationTickRate.
        /// Returns 2 when half, 3 when 1/3rd etc.
        /// </summary>
        /// <returns>The snapshot send interval.</returns>
        public int CalculateNetworkSendRateInterval() => (SimulationTickRate + NetworkTickRate - 1) / NetworkTickRate;

        /// <summary>
        /// Returns the <see cref="MaxSendRate"/> as a <see cref="SimulationTickRate"/> interval UNTIL you can resend this chunk.
        /// </summary>
        /// <param name="MaxSendRate">From the GhostAuthoring.</param>
        /// <returns>The interval i.e. every nth <see cref="SimulationTickRate"/> tick.</returns>
        public byte CalculateNetworkSendIntervalOfGhostInTicks(ushort MaxSendRate)
        {
            if (MaxSendRate == 0)
                return 1; // Every SimulationTickRate tick.
            var maxSendRateMs = 1000f / MaxSendRate; // E.g. 9hz 111ms
            var networkTickRateDelayMS = 1000f / NetworkTickRate; // 60hz 16ms
            return (byte)math.ceil((maxSendRateMs - 0.001f) / (networkTickRateDelayMS)); // = 111/16 = 6.9375 = 7
                                                                                          // = You send on every 7th tick
                                                                                          // i.e. you wait 6 ticks.
        }
    }

    /// <summary>
    /// RPC sent as part of the initial handshake from server to client to match the simulation tick rate properties
    /// on the client with those present on the server.
    /// </summary>
    internal struct ClientServerTickRateRefreshRequest : IComponentData
    {
        /// <inheritdoc cref="ClientServerTickRate.SimulationTickRate"/>
        public int SimulationTickRate;
        /// <inheritdoc cref="ClientServerTickRate.PredictedFixedStepSimulationTickRatio"/>
        public int PredictedFixedStepSimulationTickRatio;
        /// <inheritdoc cref="ClientServerTickRate.NetworkTickRate"/>
        public int NetworkTickRate;
        /// <inheritdoc cref="ClientServerTickRate.MaxSimulationStepsPerFrame"/>
        public int MaxSimulationStepsPerFrame;
        /// <inheritdoc cref="ClientServerTickRate.MaxSimulationStepBatchSize"/>
        public int MaxSimulationStepBatchSize;
        /// <inheritdoc cref="ClientServerTickRate.HandshakeApprovalTimeoutMS"/>
        public uint HandshakeApprovalTimeoutMS;

        internal readonly void Serialize(ref DataStreamWriter writer, in StreamCompressionModel compressionModel)
        {
            writer.WritePackedUIntDelta((uint) SimulationTickRate, ClientServerTickRate.DefaultTickRate, compressionModel);
            writer.WritePackedUIntDelta((uint) NetworkTickRate, ClientServerTickRate.DefaultTickRate, compressionModel);
            writer.WritePackedUIntDelta((uint) MaxSimulationStepBatchSize, ClientServerTickRate.DefaultMaxSimulationStepBatchSize, compressionModel);
            writer.WritePackedUIntDelta((uint) MaxSimulationStepsPerFrame, ClientServerTickRate.DefaultMaxSimulationStepsPerFrame, compressionModel);
            writer.WritePackedUIntDelta((uint) PredictedFixedStepSimulationTickRatio, ClientServerTickRate.DefaultPredictedFixedStepSimulationTickRatio, compressionModel);
            writer.WritePackedUIntDelta((uint) HandshakeApprovalTimeoutMS, ClientServerTickRate.DefaultHandshakeApprovalTimeoutMS, compressionModel);
        }

        internal void Deserialize(ref DataStreamReader reader, in StreamCompressionModel compressionModel)
        {
            SimulationTickRate = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultTickRate, compressionModel);
            NetworkTickRate = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultTickRate, compressionModel);
            MaxSimulationStepBatchSize = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultMaxSimulationStepBatchSize, compressionModel);
            MaxSimulationStepsPerFrame = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultMaxSimulationStepsPerFrame, compressionModel);
            PredictedFixedStepSimulationTickRatio = (int) reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultPredictedFixedStepSimulationTickRatio, compressionModel);
            HandshakeApprovalTimeoutMS = reader.ReadPackedUIntDelta(ClientServerTickRate.DefaultHandshakeApprovalTimeoutMS, compressionModel);
        }

        public void ApplyTo(ref ClientServerTickRate tickRate)
        {
            tickRate.MaxSimulationStepsPerFrame = MaxSimulationStepsPerFrame;
            tickRate.NetworkTickRate = NetworkTickRate;
            tickRate.SimulationTickRate = SimulationTickRate;
            tickRate.MaxSimulationStepBatchSize = MaxSimulationStepBatchSize;
            tickRate.PredictedFixedStepSimulationTickRatio = PredictedFixedStepSimulationTickRatio;
            tickRate.HandshakeApprovalTimeoutMS = HandshakeApprovalTimeoutMS;
        }

        public void ReadFrom(in ClientServerTickRate tickRate)
        {
            NetworkTickRate = tickRate.NetworkTickRate;
            MaxSimulationStepsPerFrame = tickRate.MaxSimulationStepsPerFrame;
            MaxSimulationStepBatchSize = tickRate.MaxSimulationStepBatchSize;
            SimulationTickRate = tickRate.SimulationTickRate;
            PredictedFixedStepSimulationTickRatio = tickRate.PredictedFixedStepSimulationTickRatio;
            HandshakeApprovalTimeoutMS = tickRate.HandshakeApprovalTimeoutMS;
        }
    }

    /// <summary>
    /// Configure when the prediction loop should run on the client.
    /// </summary>
    public enum PredictionLoopUpdateMode
    {
        /// <summary>
        /// The prediction loop will run the prediction systems only if there is at least one predicted ghost spawned on the client.
        /// </summary>
        RequirePredictedGhost,
        /// <summary>
        /// The prediction loop will always run, regardless of whether or not any predicted ghosts are spawned on the client.
        /// </summary>
        AlwaysRun
    }

    /// <summary>
    /// Create a ClientTickRate singleton in the client world (either at runtime or by loading it from sub-scene)
    /// to configure all the network time synchronization, interpolation delay, prediction batching and other setting for the client.
    /// See the individual fields for more information about the individual properties.
    /// </summary>
    [Serializable]
    public struct ClientTickRate : IComponentData
    {
        /// <summary>
        /// The number of network ticks to use as an interpolation buffer for interpolated ghosts.
        /// </summary>
        [Tooltip("If not zero, denotes the number of network ticks to use as an interpolation buffer for interpolated ghosts.\n\nDefaults to 2.\n\n<b>Warning: Ignored when InterpolationTimeMS is set.</b>")]
        [Min(0)]
        public uint InterpolationTimeNetTicks;
        /// <summary>
        /// The time in ms to use as an interpolation buffer for interpolated ghosts, this will take precedence and override the
        /// interpolation time in ticks if specified.
        /// </summary>
        [Tooltip("If not zero, denotes the number of milliseconds to use as an interpolation buffer for interpolated ghosts.\n\nDefaults to 0 (OFF).\n\n<b>Warning: Is used instead of InterpolationTimeNetTicks, if set.</b>")]
        [Min(0)]
        public uint InterpolationTimeMS;
        /// <summary>
        /// The maximum time (in simulation ticks) which the client can extrapolate ahead, when data is missing.
        /// </summary>
        [Tooltip("The maximum time (in simulation ticks) which the client can extrapolate ahead, when data is missing.\n\nDefaults to 20.")]
        [Min(0)]
        public uint MaxExtrapolationTimeSimTicks;
        /// <summary>
        /// Force the client input to be delayed by this many SimulationTickRate ticks before even being played back
        /// locally (via client prediction).
        /// <b>Significantly</b> reduces the number of ticks your client needs to rollback and re-simulate on average,
        /// but at the <b>considerable</b> "game feel" expense of increased perceived input latency.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>WARNING 1:</b> This value should only be set to a non-zero value for games and platforms
        /// which can support slower-paced play. E.g. Mobile games, games without mouse aim etc.
        /// </para>
        /// <para>
        /// <b>WARNING 2:</b> It is possible for this value to be configured so high that it pushes <see cref="NetworkTime.ServerTick"/>
        /// back far enough to fall behind the <see cref="NetworkTime.InterpolationTick"/>. If this is about to happen,
        /// Netcode for Entities will increase the interpolated window interval instead, pushing the interpolated timeline
        /// further back. Therefore, it will always run at least one prediction loop (assuming <see cref="PredictionLoopUpdateMode"/>
        /// conditions are met).
        /// </para>
        /// <para>
        /// As we don’t store inputs for partial ticks, even a value of 1 here will result in Netcode for Entities clamping
        /// input playback to full tick values, which may result in a perceptible loss of smoothness for continuous inputs
        /// (like controller d-pads/sticks).
        /// </para>
        /// </remarks>
        /// <seealso cref="NetworkTime.InputTargetTick"/>
        [Tooltip("Force the client input to be delayed by this many SimulationTickRate ticks before even being played back locally (via client prediction).\n\nI.e. Reduces the quantity of ticks your client needs to predict, but at the <b>considerable</b> expense of local input latency.\n\nDefaults to 0 (OFF).\n\n<i><b>WARNING: This value should only be greater than zero for games and platforms which can support slower-paced play. E.g. Mobile.</b></i>")]
        public byte ForcedInputLatencyTicks;
        /// <summary>
        /// This is the maximum accepted ping. RTT will be clamped to this value when calculating the server tick on the client,
        /// which means if ping is higher than this, your client will begin to incur input latency (as if you'd enabled
        /// <see cref="ForcedInputLatencyTicks"/>).
        /// Increasing this value makes the client able to deal with higher ping, but higher-ping clients will then need
        /// to run more prediction steps, which incurs more CPU overhead.
        /// </summary>
        [Tooltip("This is the maximum accepted ping. RTT will be clamped to this value when calculating the server tick on the client, which means if ping is higher than this, the client will begin to incur input latency.\n\nIncreasing this makes the client able to deal with higher ping, but higher-ping clients will then need to run more prediction steps, which incurs more CPU time.")]
        [Range(0, 500)]
        public uint MaxPredictAheadTimeMS;

        /// <summary>
        ///     <para>
        ///         The netcode package will automatically destroy client predicted spawns if they are not classified by the
        ///         time the <see cref="NetworkTime.InterpolationTick" /> passes the <b>spawnTick</b> of the client predicted spawn
        ///         (see <see cref="PredictedGhostSpawnSystem.CleanupPredictedSpawns" />).
        ///     </para>
        ///     <para>
        ///         However, this default behaviour can be too aggressive in its destruction of predicted ghosts, as:
        ///         <list type="bullet">
        ///             <item>
        ///                 Having a large number of relevant ghosts (for a given connection) can cause new ghost spawn replication delays,
        ///                 even without the presence of packet loss, due to the fact that snapshot capacity and send rate are constrained
        ///                 (via GhostSendSystemData.DefaultSnapshotPacketSize and ClientServerTickRate.NetworkTickRate respectively).
        ///             </item>
        ///             <item>
        ///                 All forms of packet loss can add delays to the replication and acknowledgement of new ghost spawns, as the snapshots
        ///                 can themselves be dropped, as can the ack mask sent by the client via the CommandSendSystem. Note: High jitter
        ///                 also leads to packet loss.
        ///             </item>
        ///             <item>
        ///                 If your InterpolationTimeMS (or InterpolationTimeNetTicks) buffer window is configured
        ///                 to be very short (the default), the netcode package has fewer opportunities to replicate this new
        ///                 spawn.
        ///                 If this is a frequent occurance, you may want to consider increasing this interpolation buffer window,
        ///                 before tweaking this.
        ///             </item>
        ///         </list>
        ///     </para>
        ///     <para>
        ///         This value denotes the additional number of client prediction ticks to keep all client predicted ghosts alive for,
        ///         increasing the likelihood of them being successfully classified against their late-arriving, server-authoritative counterparts.
        ///     </para>
        /// </summary>
        /// <remarks>
        ///     Note that increasing this value will also cause <b>mis-predicted</b> client predicted spawns to live longer.
        ///     <br />
        ///     Also note: If you frequently encounter client predicted ghosts which fail to classify within the <b>Interpolation
        ///     Window</b>, this may indicate that said window is too short. Consider increasing it.
        /// </remarks>
        [Tooltip("Denotes how many additional <b>SimulationTickRate</b> ticks that all client predicted spawns will live for, increasing the likelihood of them being successfully classified against the real ghost sent by the authoritative server.\n\nDefaults to 0 (OFF).")]
        public ushort NumAdditionalClientPredictedGhostLifetimeTicks;
        /// <summary>
        /// Denotes the plus and minus range (in <see cref="ClientServerTickRate.SimulationTickRate"/> ServerTick's) discrepancy
        /// that we allow client predicted ghosts to be automatically classified within. Defaults to ±5 ticks.
        /// <br />
        /// In other words: If no user-code classification system is written for a predicted ghost type, and a new predicted ghost spawn
        /// is detected on the client, we will check to see whether or not the new server ghost spawned within this many ticks (e.g. ±5) of
        /// the client predicted spawn (inclusive). If it has, we will assume they are the same ghost, and therefore classification will succeed.
        /// </summary>
        /// <remarks>
        /// Increase this value if you observe frequent classification failures due to large spawnTick discrepancies
        /// (common when encountering Server Tick Batching, for example).
        /// <br />
        /// Decrease this value if you observe frequent mis-classification of predicted spawned ghosts
        /// (particularly when many are spawned within a few ticks of each other).
        /// <br />
        /// Prefer to write your own classificiation systems, where possible, which allow you to use project-specific per-instance
        /// <see cref="GhostFieldAttribute"/> data to more accurately classify new spawns.
        /// </remarks>
        [Tooltip(@"Denotes the plus and minus range (in ServerTick's) discrepancy that we allow client predicted ghosts to be automatically classified within. Defaults to ±5 ticks.

In other words: If no user-code classification system is written for a predicted ghost type, and a new predicted ghost spawn is detected on the client, we will check to see whether or not the new server ghost spawned within this many ticks of the client spawn. If it has, we will assume they are the same ghost, and therefore classification will succeed.

 - Increase this value if you observe frequent classification failures due to large spawnTick discrepancies (common when encountering Server Tick Batching, for example).

 - Decrease this value if you observe frequent mis-classification of predicted spawned ghosts.")]
        [Min(1)]
        public ushort DefaultClassificationAllowableTickPeriod;
        /// <summary>
        /// Specifies the number of simulation ticks that the client tries to stay ahead of the server, to try to make sure the commands are received by the server
        /// before they are actually consumed.
        /// </summary>
        /// <remarks>Higher values increase command arrival reliability, at the cost of a longer client prediction window (which can itself degrade gameplay performance).</remarks>
        [Tooltip("Specifies the number of simulation ticks that the client tries to stay ahead of the server, to try to make sure the commands are received by the server before they are actually consumed.\n\nDefaults to 2.\n\nHigher values increase command arrival reliability, at the cost of a longer client prediction window (which can itself degrade gameplay performance). This contributes to the overall RTT, including frame time, target command slack, etc.")]
        [Range(0, 16)]
        public uint TargetCommandSlack;
        /// <summary>
        /// The `CommandSendSystem` will send <see cref="TargetCommandSlack"/> + <see cref="NumAdditionalCommandsToSend"/>
        /// commands in each input packet (default of 2 and 2, thus 4), as a packet loss recovery mechanism (hard max: 32, see <see cref="CommandSendSystemGroup.k_MaxInputBufferSendSize"/>).
        /// This option defines how many <b>additional</b> packets to send (on top of `TargetCommandSlack`).
        /// Min value is 1, as sending zero additional inputs can cause input loss, even on connections with zero packet loss.
        /// Default value is 2.
        /// Higher values incur more server ingress bandwidth consumption, but can be useful when dealing with unstable connections.
        /// However, you may just be re-sending commands that are already too old to be used.
        /// </summary>
        /// <remarks>
        /// Debug command arrival rate (and statistics) via the Packet Dump Utility and/or the <see cref="NetworkSnapshotAck.CommandArrivalStatistics"/>.
        /// </remarks>
        [Tooltip("The `CommandSendSystem` will send `TargetCommandSlack` + `NumAdditionalCommandsToSend` commands in each input packet (default of 2 and 2, thus 4), as a packet loss recovery mechanism.\n\nThis option defines how many additional packets to send (on top of `TargetCommandSlack`).\n\nMin value is 1, default value is 2.\n\nHigher values incur more server ingress bandwidth consumption, but can be useful when dealing with unstable connections.\n\nDebug command arrival rate (and statistics) via the Packet Dump Utility and/or the `NetworkSnapshotAck.CommandArrivalStats`.")]
        [Range(1, 32)]
        public uint NumAdditionalCommandsToSend;
        /// <summary>
        /// The client can batch simulation steps in the prediction loop. This setting controls
        /// how many simulation steps the simulation can batch, for ticks which have previously
        /// been predicted.
        /// Setting this to a value larger than 1 will save performance, but the gameplay systems
        /// must account for it.
        /// </summary>
        [Tooltip("The client can batch simulation steps in the prediction loop. This setting controls how many simulation steps the simulation can batch, <b>for ticks which have previously been predicted</b>.\n\nWhen 0, defaults to 1 at runtime.\n\nSetting this to a value larger than 1 will save performance at the cost of simulation accuracy. Gameplay systems need to account for it.")]
        [Range(0, 16)]
        public int MaxPredictionStepBatchSizeRepeatedTick;
        /// <summary>
        /// The client can batch simulation steps in the prediction loop. This setting controls
        /// how many simulation steps the simulation can batch, for ticks which are being predicted
        /// for the first time.
        /// Setting this to a value larger than 1 will save performance, but the gameplay systems
        /// needs to be adapted.
        /// </summary>
        [Tooltip("The client can batch simulation steps in the prediction loop. This setting controls how many simulation steps the simulation can batch, <b>for ticks which are being predicted for the first time</b>.\n\nWhen 0, defaults to 1 at runtime.\n\nSetting this to a value larger than 1 will save performance at the cost of simulation accuracy. Gameplay systems needs to be adapted.")]
        [Range(0, 16)]
        public int MaxPredictionStepBatchSizeFirstTimeTick;
        /// <summary>
        /// Configure how the client should run the prediction loop systems. By default, the client runs the systems inside the <see cref="PredictedSimulationSystemGroup"/> (and consequently also the ones in <see cref="PredictedFixedStepSimulationSystemGroup"/>)
        /// only if there are predicted ghosts in the world. This is a good behaviour in general, as it saves some CPU cycles. However, it can be unintuitive, as there are situations where you would like to have these systems always run. For example:
        /// <list type="bullet">>
        /// <item>You would like to ray cast against the physics world, even in cases where there are only interpolated ghosts and/or static geometry present. I.e. In order to spawn a predicted ghost in first place, you need to raycast against the static geometry.</item>
        /// <item>You want some systems to act on both interpolated and predicted ghosts (and run in the same group, with certain caveats, of course). An example could be a "dead-reckoned" static, interpolated ghost that rarely updates (i.e. it has very low importance).</item>
        /// </list>
        /// It is important to understand the implications of selecting the alternative mode, <see cref="PredictionLoopUpdateMode.AlwaysRun"/>, especially from a CPU cost perspective. In that case, because the systems will run all the time,
        /// it is fundamental to prevent doing work when said work is un-necessary. Example: Scheduling jobs with empty queries. While it is, in general, already the case that most of the idiomatic foreach and jobs etc are going to be a no-op,
        /// you may still incur some extra CPU overhead, just because of the systems update. Best practice is to use RequireForUpdate (or similar) checks, as preconditions for the system to run.
        /// </summary>
        [Tooltip("Denotes if the client should run the prediction loop systems, even if no predicted ghosts are present in the client world. By default, the client doesn't run the systems inside the PredictedSimulationSystemGroup (and consequently, nor the ones in PredictedFixedStepSimulationSystemGroup) if there are no predicted ghosts.\n\nThis is a good behaviour in general, that saves some CPU cycles. However, it may be unintuitive, as there are situations where you would like to have these systems always run. For example:\n\n - You would like to ray cast against the physics world, even in cases where there are only interpolated ghosts and/or static geometry present. I.e. In order to spawn a predicted ghost in first place, you need to raycast against the static geometry.\n\n - You want some systems to act on both interpolated and predicted ghosts (and run in the same group, with certain caveats, of course). An example could be a \"dead-reckoned\" static, interpolated ghost that rarely updates (i.e. it has very low importance).")]
        public PredictionLoopUpdateMode PredictionLoopUpdateMode;
        /// <summary>
        /// Multiplier used to compensate received snapshot rate jitter when calculating the Interpolation Delay.
        /// Default Value: 1.25.
        /// </summary>
        [Tooltip("Multiplier used to compensate received snapshot rate jitter when calculating the Interpolation Delay.\n\nDefaults to 1.25.")]
        [Min(0.001f)]
        public float InterpolationDelayJitterScale;
        /// <summary>
        /// Used to limit the maximum InterpolationDelay changes in one frame, as percentage of the frame deltaTicks.
        /// Default value: 10% of the frame delta ticks. Smaller values will result in slow adaptation to the network state (loss and jitter)
        /// but would result in smooth delay changes. Larger values would make the InterpolationDelay change quickly adapt but
        /// may cause sudden jump in the interpolated values.
        /// Good ranges: [0.10 - 0.3]
        /// </summary>
        [Tooltip("Used to limit the maximum InterpolationDelay changes in one frame, as percentage of the frame deltaTicks.\n\nDefaults to 10% of the frame delta ticks. Recommended range is [0.10 - 0.3].\n\n - Smaller values will result in slow adaptation to the network state (loss and jitter) but would result in smooth delay changes.\n - Larger values would make the InterpolationDelay change quickly adapt but may cause sudden jump in the interpolated values.")]
        [Range(0.01f, 0.5f)]
        public float InterpolationDelayMaxDeltaTicksFraction;
        /// <summary>
        /// <para>The percentage of the error in the interpolation delay that can be corrected in one frame. Used to control InterpolationTickTimeScale.
        /// Must be in range (0, 1).</para>
        /// <code>
        ///              ________ Max
        ///            /
        ///           /
        /// Min _____/____________
        ///                         InterpolationDelayDelta
        /// </code>
        /// <para>DefaultValue: 10% of the delta in between the current and next desired interpolation tick.
        /// Good ranges: [0.075 - 0.2]</para>
        /// </summary>
        [Tooltip("The percentage of the error in the interpolation delay that can be corrected in one frame. Used to control InterpolationTickTimeScale.\n\nRecommended range is [0.075 - 0.2].")]
        [Range(0f, 1f)]
        public float InterpolationDelayCorrectionFraction;
        /// <summary>
        /// The minimum value for the InterpolateTimeScale. Must be in range (0, 1) Default: 0.85.
        /// </summary>
        [Tooltip("The minimum value for the InterpolateTimeScale.\n\nDefaults to 0.85.")]
        [Range(0f, 1f)]
        public float InterpolationTimeScaleMin;
        /// <summary>
        /// The maximum value for the InterpolateTimeScale. Must be greater that 1.0. Default: 1.1.
        /// </summary>
        [Tooltip("The maximum value for the InterpolateTimeScale.\n\nDefaults to 1.1.")]
        [Min(1f)]
        public float InterpolationTimeScaleMax;
        /// <summary>
        /// <para>The percentage of the error in the predicted server tick that can be corrected each frame. Used to control the client deltaTime scaling, used to
        /// slow-down/speed-up the server tick estimate.
        /// Must be in (0, 1) range.</para>
        /// <code>
        ///
        ///              ________ Max
        ///             /
        ///            /
        /// Min ______/__________
        ///                      CommandAge
        /// </code>
        /// <para>DefaultValue: 10% of the error.
        /// The two major causes affecting the command age are:
        ///  - Network condition (Latency and Jitter)
        ///  - Server performance (running below the target frame rate)
        ///
        /// Small time scale values allow for smooth adjustments of the prediction tick but slower reaction to changes in both network and server frame rate.
        /// By using larger values, is faster to recovery to desync situation (caused by bad network and condition or/and slow server performance) but the
        /// predicted ticks delta are larger.
        /// Good ranges: [0.075 - 0.2]</para>
        /// </summary>
        [Tooltip("The percentage of the error in the predicted server tick that can be corrected each frame. Used to control the client deltaTime scaling, used to slow-down/speed-up the server tick estimate.\n\nDefaults to 10% of the error. Recommended range is [0.075 - 0.2].\n\n - Small time scale values allow for smooth adjustments of the prediction tick, but slower reaction to changes in both network and server frame rate.\n - Larger values causes recovery to be faster in desync situations, but the predicted ticks delta are larger.")]
        [Range(0f, 1f)]
        public float CommandAgeCorrectionFraction;
        /// <summary>
        /// PredictionTick time scale min value, max be less then 1.0f. Default: 0.9f.
        /// Note: it is not mandatory to have the min-max symmetric.
        /// Good Range: (0.8 - 0.95)
        /// </summary>
        [Tooltip("The PredictionTick time scale min value.\n\nDefaults to 0.9. Recommended range is (0.8 - 0.95).\n\nNote: It is not mandatory to have the min and max values symmetric.")]
        [Range(0f, 1f)]
        public float PredictionTimeScaleMin;
        /// <summary>
        /// PredictionTick time scale max value, max be greater then 1.0f. Default: 1.1f
        /// Note: it is not mandatory to have the min-max symmetric.
        /// Good Range: (1.05 - 1.2)
        /// </summary>
        [Tooltip("PredictionTick time scale max value.\n\nDefaults to 1.1. Recommended range is (1.05 - 1.2).\n\nNote: It is not mandatory to have the min and max values symmetric.")]
        [Range(1f, 2f)]
        public float PredictionTimeScaleMax;

        /// <summary>The size of the interpolation window.</summary>
        /// <param name="tickRate">The current struct value.</param>
        /// <returns>Value in <see cref="ClientServerTickRate.SimulationTickRate"/> Ticks.</returns>
        public int CalculateInterpolationBufferTimeInTicks(in ClientServerTickRate tickRate)
        {
            if (InterpolationTimeMS != 0)
                return (int)((InterpolationTimeMS * tickRate.NetworkTickRate + 999) / 1000);
            return (int) InterpolationTimeNetTicks;
        }
        /// <summary>The size of the interpolation window.</summary>
        /// <param name="tickRate">The current struct value.</param>
        /// <returns>Value in milliseconds.</returns>
        public float CalculateInterpolationBufferTimeInMs(in ClientServerTickRate tickRate) => CalculateInterpolationBufferTimeInTicks(in tickRate) * tickRate.SimulationFixedTimeStep * 1000;
    }
}
