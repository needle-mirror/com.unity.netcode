#if UNITY_EDITOR || NETCODE_DEBUG
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Profiling;

// TODO have this available in release builds for DGS

namespace Unity.NetCode
{
    internal struct GhostStats : IComponentData
    {
        public bool IsConnected;
    }
    internal struct GhostStatsCollectionCommand : IComponentData
    {
        public NativeArray<uint> Value;
    }

    // "unsafe" since this can't be a NativeContainer (it's embedded in NativeList in GhostStatsSnapshotSingleton for example)
    // For now, assuming the read buffer containing a raw unsafe instance will only be called from the main thread. To use in a safe way, we could create a wrapper GhostStatsSnapshotReader to read those stats from jobs. Not an API we expose to users for now, so skipping for now.
    // This is written to by Netcode and shouldn't be written to manually.
    // There's still safety in here. It's useful in cases where the reader is used in a job, but the mainthread is trying to write to the reader base unsafe data (for updating the double buffer reader for example)
    // Also useful for dispose checks
    internal unsafe struct UnsafeGhostStatsSnapshot : IDisposable
    {
        public struct PerGhostTypeStats : IDisposable
        {
            public uint EntityCount; // old statType * 3 + 4
            public uint SizeInBits; // old statType * 3 + 5
            public uint UncompressedCount; // old statType * 3 + 6
            // TODO there's some more data sent than what's counted here. They are some ints and bytes used netcode side to keep track of things. We should have a generic "metadata" section in the profiler that just takes the packet size and subtracts all the per component sizes to get the "netcode overhead + UTP overhead".
            // TODO in the original code, this was stored at the same place as UncompressedCount (3rd index). Did I misunderstand something or is this just reusing the same spot in memory? Was there some form of optim there for this? There shouldn't be that many prefabs that we need to save on amount of memory used no? Like for 500 ghost types that's just 2KB. Even for web debugger packets, I'm still reusing the old format with only 3*uint per ghost type so this should still be good?
            public uint ChunkCount;
            internal NativeList<PerComponentStats> PerComponentStatsList;

            internal PerGhostTypeStats(Allocator allocator)
            {
                PerComponentStatsList = new(10, allocator);
                EntityCount = 0;
                SizeInBits = 0;
                UncompressedCount = 0;
                ChunkCount = 0;
            }

            internal void IncrementWith(in PerGhostTypeStats other)
            {
                EntityCount += other.EntityCount;
                SizeInBits += other.SizeInBits;
                UncompressedCount += other.UncompressedCount;
                ChunkCount += other.ChunkCount;
                for (int i = 0; i < other.PerComponentStatsList.Length; i++)
                {
                    if (i >= PerComponentStatsList.Length)
                        PerComponentStatsList.Add(other.PerComponentStatsList[i]);
                    else
                        PerComponentStatsList.ElementAt(i).IncrementWith(other.PerComponentStatsList[i]);
                }
            }

            public void Dispose()
            {
                PerComponentStatsList.Dispose();
            }

            internal void ResetToDefault()
            {
                EntityCount = 0;
                SizeInBits = 0;
                UncompressedCount = 0;
                ChunkCount = 0;
                PerComponentStatsList.ResetToDefault();
            }

            public int GetBlittableSizeBytes()
            {
                var toReturn = 0;
                toReturn += UnsafeUtility.SizeOf<uint>(); // EntityCount
                toReturn += UnsafeUtility.SizeOf<uint>(); // SizeInBits
                toReturn += UnsafeUtility.SizeOf<uint>(); // UncompressedCount
                toReturn += UnsafeUtility.SizeOf<uint>(); // ChunkCount
                toReturn += UnsafeUtility.SizeOf<int>(); // list length
                for (int i = 0; i < PerComponentStatsList.Length; i++)
                {
                    toReturn += PerComponentStatsList[i].GetBlittableSizeBytes();
                }
                return toReturn;
            }

            public void ToBlittableData(ref DataStreamWriter writer)
            {
                writer.WriteUInt(EntityCount);
                writer.WriteUInt(SizeInBits);
                writer.WriteUInt(UncompressedCount);
                writer.WriteUInt(ChunkCount);
                writer.WriteInt(PerComponentStatsList.Length);
                for (int i = 0; i < PerComponentStatsList.Length; i++)
                {
                    PerComponentStatsList[i].ToBlittableData(ref writer);
                }
            }

            public static PerGhostTypeStats FromBlittableData(ref DataStreamReader reader, Allocator allocator)
            {
                var toReturn = new PerGhostTypeStats(allocator);
                toReturn.EntityCount = reader.ReadUInt();
                toReturn.SizeInBits = reader.ReadUInt();
                toReturn.UncompressedCount = reader.ReadUInt();
                toReturn.ChunkCount = reader.ReadUInt();
                var listLength = reader.ReadInt();
                toReturn.PerComponentStatsList.Resize(listLength, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < listLength; i++)
                {
                    toReturn.PerComponentStatsList[i] = PerComponentStats.FromBlittableData(ref reader);
                }
                return toReturn;
            }
        }

        [DebuggerDisplay("Size (bits): {SizeInSnapshotInBits}")]
        public struct PerComponentStats
        {
            public uint SizeInSnapshotInBits;

            public void IncrementWith(in PerComponentStats otherPerComponentStats)
            {
                SizeInSnapshotInBits += otherPerComponentStats.SizeInSnapshotInBits;
            }

            public void ResetToDefault()
            {
                SizeInSnapshotInBits = 0;
            }

            public int GetBlittableSizeBytes()
            {
                var toReturn = 0;
                toReturn += UnsafeUtility.SizeOf<int>(); // SizeInSnapshotInBits
                return toReturn;
            }

            public void ToBlittableData(ref DataStreamWriter writer)
            {
                writer.WriteUInt(SizeInSnapshotInBits);
            }

            public static PerComponentStats FromBlittableData(ref DataStreamReader reader)
            {
                var toReturn = new PerComponentStats();
                toReturn.SizeInSnapshotInBits = reader.ReadUInt();
                return toReturn;
            }
        }

        // This data used to be stored in a single uint array, with striding x3 to set individual counters. References to "old index" reference that that old way of storing things for reference purposes
        internal NetworkTick Tick; // old index 0 // client side this is the received snapshot's tick (not current prediction tick). Server side this is the server tick when the send system executed
        internal uint DespawnCount; // old index 1
        internal uint DestroySizeInBits; // old index 2
        internal uint PacketsCount;
        internal uint SnapshotTotalSizeInBits; // includes headers
        public bool Initialized;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;
#endif
        Allocator m_Allocator;

        // indexed by ghostType index. TODO wrap ghost type index inside a struct?
        internal UnsafeList<PerGhostTypeStats> m_PerGhostTypeStatsList;

        public ref UnsafeList<PerGhostTypeStats> PerGhostTypeStatsListRefRW
        {
            get
            {
                CheckWrite();
                return ref UnsafeUtility.AsRef<UnsafeList<PerGhostTypeStats>>(UnsafeUtility.AddressOf(ref m_PerGhostTypeStatsList));
            }
        }
        public readonly UnsafeList<PerGhostTypeStats> PerGhostTypeStatsListRO
        {
            get
            {
                CheckRead();
                return m_PerGhostTypeStatsList;
            }
        }

        public UnsafeGhostStatsSnapshot(int numLoadedPrefab, Allocator allocator)
        {
            Tick = default;
            DespawnCount = 0;
            DestroySizeInBits = 0;
            PacketsCount = 0;
            SnapshotTotalSizeInBits = 0;
            m_Allocator = allocator;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = CollectionHelper.CreateSafetyHandle(m_Allocator);
#endif
            Initialized = true;

            m_PerGhostTypeStatsList = new(numLoadedPrefab, m_Allocator);
            for (int i = 0; i < numLoadedPrefab; i++)
            {
                PerGhostTypeStatsListRefRW.Add(new(m_Allocator));
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal void CheckWrite()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal readonly void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        public void IncrementWith(in UnsafeGhostStatsSnapshot other)
        {
            CheckWrite();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(other.m_Safety);
#endif
            DespawnCount += other.DespawnCount;
            DestroySizeInBits += other.DestroySizeInBits;
            PacketsCount += other.PacketsCount;
            SnapshotTotalSizeInBits += other.SnapshotTotalSizeInBits;
            for (int i = 0; i < other.PerGhostTypeStatsListRefRW.Length; i++)
            {
                PerGhostTypeStatsListRefRW.ElementAt(i).IncrementWith((other.PerGhostTypeStatsListRO)[i]);
            }
        }

        // sets all data to default
        public void ResetToDefault()
        {
            CheckWrite();
            Tick = default;
            DespawnCount = 0;
            DestroySizeInBits = 0;
            PacketsCount = 0;
            SnapshotTotalSizeInBits = 0;
            for (int i = 0; i < PerGhostTypeStatsListRefRW.Length; i++)
            {
                PerGhostTypeStatsListRefRW.ElementAt(i).ResetToDefault();
            }
        }

        public void Reset(int numLoadedPrefab)
        {
            CheckWrite();
            Tick = default;
            DespawnCount = 0;
            DestroySizeInBits = 0;
            PacketsCount = 0;
            SnapshotTotalSizeInBits = 0;
            if (numLoadedPrefab < PerGhostTypeStatsListRefRW.Length)
            {
                for (int i = numLoadedPrefab; i < PerGhostTypeStatsListRefRW.Length; i++)
                {
                    PerGhostTypeStatsListRefRW.ElementAt(i).Dispose();
                }
            }
            var previousLength = PerGhostTypeStatsListRefRW.Length;
            PerGhostTypeStatsListRefRW.Resize(numLoadedPrefab, NativeArrayOptions.UninitializedMemory);
            if (previousLength < PerGhostTypeStatsListRefRW.Length)
            {
                for (int i = previousLength; i < PerGhostTypeStatsListRefRW.Length; i++)
                {
                    (PerGhostTypeStatsListRefRW)[i] = new PerGhostTypeStats(m_Allocator);
                }
            }
            ResetToDefault();
        }

        public void Dispose()
        {
            foreach (var perGhostStat in PerGhostTypeStatsListRefRW)
            {
                perGhostStat.Dispose();
            }
            PerGhostTypeStatsListRefRW.Dispose();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            CollectionHelper.DisposeSafetyHandle(ref m_Safety);
#endif
        }

        public NativeArray<byte> ToBlittableData(Allocator allocator)
        {
            var toReturn = new NativeArray<byte>(GetBlittableSizeBytes(), allocator);

            var writer = new DataStreamWriter(toReturn);
            writer.WriteUInt(this.Tick.SerializedData);
            writer.WriteUInt(this.DespawnCount);
            writer.WriteUInt(this.DestroySizeInBits);
            writer.WriteUInt(this.PacketsCount);
            writer.WriteUInt(this.SnapshotTotalSizeInBits);
            var statsList = this.PerGhostTypeStatsListRO;
            writer.WriteInt(statsList.Length);
            for (int i = 0; i < statsList.Length; i++)
            {
                statsList[i].ToBlittableData(ref writer);
            }

            return toReturn;
        }

        int GetBlittableSizeBytes()
        {
            int toReturn = 0;

            toReturn += UnsafeUtility.SizeOf<uint>(); // Tick
            toReturn += UnsafeUtility.SizeOf<uint>(); // DespawnCount
            toReturn += UnsafeUtility.SizeOf<uint>(); // DestroySizeInBits
            toReturn += UnsafeUtility.SizeOf<uint>(); // NewPacketsCountSent
            toReturn += UnsafeUtility.SizeOf<uint>(); // SnapshotTotalSizeInBits

            var statsList = this.PerGhostTypeStatsListRO;
            toReturn += UnsafeUtility.SizeOf<int>(); // length of stats list
            for (int i = 0; i < statsList.Length; i++)
            {
                toReturn += statsList[i].GetBlittableSizeBytes();
            }

            return toReturn;
        }

        public static UnsafeGhostStatsSnapshot FromBlittableData(Allocator allocator, NativeArray<byte> data)
        {
            var toReturn = new UnsafeGhostStatsSnapshot(1, allocator);

            var reader = new DataStreamReader(data);
            var tick = new NetworkTick();
            tick.SerializedData = reader.ReadUInt();
            toReturn.Tick = tick;
            toReturn.DespawnCount = reader.ReadUInt();
            toReturn.DestroySizeInBits = reader.ReadUInt();
            toReturn.PacketsCount = reader.ReadUInt();
            toReturn.SnapshotTotalSizeInBits = reader.ReadUInt();
            var listLength = reader.ReadInt();
            toReturn.PerGhostTypeStatsListRefRW.Resize(listLength, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < listLength; i++)
            {
                (toReturn.PerGhostTypeStatsListRefRW)[i] = PerGhostTypeStats.FromBlittableData(ref reader, allocator);
            }

            return toReturn;
        }

        #region for old format compatibility
        public readonly int ByteOldSize()
        {
            CheckRead();
            return UIntOldSize() * UnsafeUtility.SizeOf<uint>();
        }

        public readonly int UIntOldSize()
        {
            CheckRead();
            return 1 + 1 + 1 + (PerGhostTypeStatsListRO.Length * 3); // despawn count + destroy size + unknown + per ghost types
            // return 1 + 1 + 1 + 1 + (PerGhostTypeStats.Length * 3); // tick + despawn count + destroy size + unknown + per ghost types
        }

        // using the old format to update the web page. Kept for flow compatibility reasons. We should remove this method once we remove the web page for profiler stats
        public NativeArray<uint> ToOldBinary(Allocator allocator, bool useReceivedStats)
        {
            CheckRead();
            // should return the original format that's expected by the webpage
            var requiredSize = UIntOldSize();
            var toReturn = new NativeArray<uint>(requiredSize, allocator);
            // toReturn[0] = Tick.Value.SerializedData;
            // TODO we don't send the tick?
            toReturn[0] = DespawnCount;
            toReturn[1] = DestroySizeInBits;
            toReturn[2] = 0; // this seems like it was never used and always set to 0. potentially a reserved spot for future use?
            for (int i = 0; i < PerGhostTypeStatsListRefRW.Length; i++)
            {
                toReturn[i * 3 + 3] = PerGhostTypeStatsListRefRW.ElementAt(i).EntityCount;
                toReturn[i * 3 + 4] = PerGhostTypeStatsListRefRW.ElementAt(i).SizeInBits;
                if (useReceivedStats)
                    toReturn[i * 3 + 5] = PerGhostTypeStatsListRefRW.ElementAt(i).UncompressedCount;
                else
                    toReturn[i * 3 + 5] = PerGhostTypeStatsListRefRW.ElementAt(i).ChunkCount;
            }
            return toReturn;
        }

        #endregion
    }

    // main point of access to snapshot stats
    // The flow goes like this: n worker threads gather stats in parallel from GhostSendSystem's job. Then next frame n worker stats are merged into the first "main" one. Then  those stats are copied in the read stats to be read by metrics and the web page.
    // client side, since there's a single thread, GhostReceiveSystem only takes the first writer stat, no need for n writers.
    // Uses NativeList for parallel write access safety. Everything underneath is unsafe.
    internal struct GhostStatsSnapshotSingleton : IComponentData, IDisposable
    {
        internal NativeList<UnsafeGhostStatsSnapshot> allGhostStatsParallelWrites;

        internal ref UnsafeGhostStatsSnapshot MainStatsWrite => ref allGhostStatsParallelWrites.ElementAt(0); // access to write list should be safe because of NativeList, but accessing the internal elements won't be. Make sure each instance is accessed on the same thread

        internal UnsafeGhostStatsSnapshot UnsafeMainStatsRead; // should only be accessed on main thread

        static int MaxThreadCount
        {
            get
            {
#if UNITY_2022_2_14F1_OR_NEWER
                int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
                int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
                return maxThreadCount;
            }
        }

        public GhostStatsSnapshotSingleton(int initializeStatsSize, Allocator allocator)
        {
            allGhostStatsParallelWrites = new(MaxThreadCount, allocator);
            UnsafeMainStatsRead = new (initializeStatsSize, allocator);
        }


        // Main point of access for users. Use this to get a safe read copy of the stats. This should replace GhostMetrics access.
        // This is a copy of the main stats being written to by jobs. This can be accessed from anywhere
        // public unsafe GhostStatsSnapshotReader GetAsyncStatsReader()
        // {
        //     UnsafeMainStatsRead.CheckRead();
        //     return new GhostStatsSnapshotReader((UnsafeGhostStatsSnapshot*)UnsafeUtility.AddressOf(ref this.UnsafeMainStatsRead));
        // }

        public unsafe UnsafeGhostStatsSnapshot GetAsyncStatsReader()
        {
            UnsafeMainStatsRead.CheckRead();
            return UnsafeMainStatsRead;
        }

#if UNITY_EDITOR || NETCODE_DEBUG
        internal unsafe void ResetWriter(int numLoadedPrefabs)
        {
            allGhostStatsParallelWrites.Resize(MaxThreadCount, NativeArrayOptions.ClearMemory);

            for (int i = 0; i < MaxThreadCount; i++)
            {
                ref var statsSnapshotWriter = ref allGhostStatsParallelWrites.ElementAt(i);
                if (!statsSnapshotWriter.Initialized)
                    allGhostStatsParallelWrites[i] = new UnsafeGhostStatsSnapshot(numLoadedPrefabs, Allocator.Persistent);
                else
                    statsSnapshotWriter.Reset(numLoadedPrefabs);
            }
            MainStatsWrite.Tick = NetworkTick.Invalid;
        }
#endif

        /// <summary>
        /// Append to the collection the snapshost prefab stats  or the given tick. Used and populated by the <see cref="GhostSendSystem"/>
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        internal unsafe void UpdateDoubleBufferReadStats(in GhostStatsCollectionData collectionData, int snapshotCount, bool hasMonitor)
        {
            var statsTick = MainStatsWrite.Tick;
            if (!statsTick.IsValid || UnsafeMainStatsRead.PerGhostTypeStatsListRO.Length < MainStatsWrite.PerGhostTypeStatsListRO.Length-1 || snapshotCount >= 255 || (!hasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;

            // TODO just swap pointers? no need for copy
            UnsafeMainStatsRead.Tick = MainStatsWrite.Tick;
            UnsafeMainStatsRead.IncrementWith(MainStatsWrite); // since this can be called multiple times with no new stats, we don't want to override it, we need to just increment it
        }

        public unsafe void Dispose()
        {
            foreach (var statsCollectionSnapshot in allGhostStatsParallelWrites)
            {
                statsCollectionSnapshot.Dispose();
            }

            allGhostStatsParallelWrites.Dispose();
            UnsafeMainStatsRead.Dispose();
        }
    }

    internal struct GhostStatsCollectionPredictionError : IComponentData
    {
        public NativeList<float> Data;
    }
    internal struct GhostStatsCollectionMinMaxTick : IComponentData
    {
        public NativeArray<NetworkTick> Value;
    }

    /// <summary>
    /// The GhostStatsCollectionSystem is responsible to hold all sent and received snapshot statitics on both client
    /// and server.
    /// The collected stats are then sent to the Network Debugger tools for visualization (when the debugger is connected attached) by
    /// the <see cref="GhostStatsConnection"/> at the end of the frame.
    /// </summary>
    // This is updating first in the receive system group to make sure this system is the first stats collection
    // running any given frame since this system sets up the current tick for the stats
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ServerSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation)]
    [BurstCompile]
    unsafe internal partial struct GhostStatsCollectionSystem : ISystem
    {
        private static GhostStatsConnection _sGhostStatsConnection;
        private uint m_UpdateId;
        private bool m_HasMonitor;

        /// <summary>
        /// Append to the collection the send/recv commands stats for the given tick. Used by the <see cref="NetworkStreamReceiveSystem"/>
        /// and <see cref="CommandSendPacketSystem"/>.
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        private void AddCommandStats(NativeArray<uint> stats, in GhostStatsCollectionData collectionData)
        {
            var statsTick = new NetworkTick{SerializedData = stats[0]};
            if (!statsTick.IsValid || m_CommandTicks.Length >= 255 || (!m_HasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;
            m_CommandStats += stats[1];
            if (m_CommandTicks.Length == 0 || m_CommandTicks[m_CommandTicks.Length-1] != stats[0])
                m_CommandTicks.Add(statsTick.TickIndexForValidTick);
        }
        /// <summary>
        /// Append to the collection the prediction error calculatd by <see cref="GhostPredictionDebugSystem"/> for the given tick
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        private void AddPredictionErrorStats(NativeArray<float> stats, in GhostStatsCollectionData collectionData)
        {
            if (m_SnapshotTicks.Length >= 255 || (!m_HasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;
            for (int i = 0; i < stats.Length; ++i)
                m_PredictionErrors[i] = math.max(stats[i], m_PredictionErrors[i]);
        }

        /// <summary>
        /// Append to the collection the number of discarded snapshots/commmands (respectively received by client and server)
        /// </summary>
        /// <param name="stats"></param>
        /// <param name="collectionData"></param>
        private void AddDiscardedPackets(uint stats, in GhostStatsCollectionData collectionData)
        {
            if (m_SnapshotTicks.Length >= 255 || (!m_HasMonitor && collectionData.m_StatIndex < 0) || !collectionData.m_CollectionTick.IsValid)
                return;

            m_DiscardedPackets += stats;
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_SnapshotTicks = new NativeList<NetworkTick>(16, Allocator.Persistent);
            m_PredictionErrors = new NativeList<float>(0, Allocator.Persistent);
            m_TimeSamples = new NativeList<TimeSample>(16, Allocator.Persistent);
            m_CommandTicks = new NativeList<uint>(16, Allocator.Persistent);

            m_PacketQueue = new NativeList<Packet>(16, Allocator.Persistent);
            m_PacketPool = new NativeList<byte>(4096, Allocator.Persistent);
            m_PacketPool.Resize(m_PacketPool.Capacity, NativeArrayOptions.UninitializedMemory);

            m_LastNameAndErrorArray = new NativeText(4096, Allocator.Persistent);

            m_CommandStatsData = new NativeArray<uint>(3, Allocator.Persistent);
            var typeList = new NativeArray<ComponentType>(6, Allocator.Temp);
            typeList[0] = ComponentType.ReadWrite<GhostStats>();
            typeList[1] = ComponentType.ReadWrite<GhostStatsCollectionCommand>();
            typeList[2] = ComponentType.ReadWrite<GhostStatsSnapshotSingleton>();
            typeList[3] = ComponentType.ReadWrite<GhostStatsCollectionPredictionError>();
            typeList[4] = ComponentType.ReadWrite<GhostStatsCollectionMinMaxTick>();
            typeList[5] = ComponentType.ReadWrite<GhostStatsCollectionData>();
            var statEnt = state.EntityManager.CreateEntity(state.EntityManager.CreateArchetype(typeList));
            FixedString64Bytes singletonName = "GhostStatsCollectionSingleton";
            state.EntityManager.SetName(statEnt, singletonName);

            SystemAPI.SetSingleton(new GhostStatsCollectionCommand{Value = m_CommandStatsData});

            const int initialStatsSize = 128;
            SystemAPI.SetSingleton(new GhostStatsSnapshotSingleton(initialStatsSize, Allocator.Persistent));
            m_PredictionErrorStatsData = new NativeList<float>(initialStatsSize, Allocator.Persistent);
            SystemAPI.SetSingleton(new GhostStatsCollectionPredictionError{Data = m_PredictionErrorStatsData});

#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
            m_MinMaxTickStatsData = new NativeArray<NetworkTick>(maxThreadCount * JobsUtility.CacheLineSize/4, Allocator.Persistent);
            SystemAPI.SetSingleton(new GhostStatsCollectionMinMaxTick{Value = m_MinMaxTickStatsData});

            var ghostcollectionData = new GhostStatsCollectionData
            {
                m_PacketPool = m_PacketPool,
                m_PacketQueue = m_PacketQueue,
                m_LastNameAndErrorArray = m_LastNameAndErrorArray,
                m_PredictionErrors = m_PredictionErrors,
                m_StatIndex = -1,
                m_UsedPacketPoolSize = 0
            };
            ghostcollectionData.UpdateMaxPacketSize(initialStatsSize, m_PredictionErrors.Length);
            SystemAPI.SetSingleton(ghostcollectionData);

            m_Recorders = new NativeList<ProfilerRecorder>(Allocator.Persistent);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            m_LastNameAndErrorArray.Dispose();
            m_PacketQueue.Dispose();
            m_CommandTicks.Dispose();
            m_SnapshotTicks.Dispose();
            m_TimeSamples.Dispose();
            m_CommandStatsData.Dispose();
            SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().Dispose();
            m_PredictionErrorStatsData.Dispose();
            m_MinMaxTickStatsData.Dispose();
            m_PacketPool.Dispose();
            m_PredictionErrors.Dispose();
            if (m_Recorders.IsCreated)
            {
                foreach (var recorder in m_Recorders)
                {
                    recorder.Dispose();
                }
                m_Recorders.Dispose();
            }
        }

        void UpdateSnapshotPacketCount(ref SystemState state, ref GhostStatsSnapshotSingleton snapshotStatsSingleton)
        {
            if (SystemAPI.TryGetSingleton(out NetworkStreamDriver networkStreamDriver))
            {
                snapshotStatsSingleton.UnsafeMainStatsRead.PacketsCount = 0;

                var totalSnapshotSize = 0f;
                foreach (var stat in snapshotStatsSingleton.UnsafeMainStatsRead.PerGhostTypeStatsListRO)
                {
                    totalSnapshotSize += stat.SizeInBits / 8f;
                }

                if (totalSnapshotSize == 0) return;

                foreach (var networkStreamConnection in SystemAPI.Query<RefRO<NetworkStreamConnection>>())
                {
                    ref var driverStore = ref networkStreamDriver.DriverStore;
                    var connection = networkStreamConnection.ValueRO.Value;
                    for (int i = driverStore.FirstDriver; i < driverStore.LastDriver; i++)
                    {
                        var networkDriver = driverStore.GetDriverRO(i); // each driver should be configured with the same pipelines
                        var pipeline = driverStore.GetDriverInstanceRO(i).unreliablePipeline;

                        var headerSize = networkDriver.MaxHeaderSize(pipeline);
                        if (networkDriver.GetMaxSupportedMessageSize(connection) < 0)
                        {
                            // most likely this is IPC, skipping
                            continue;
                        }
                        // trying to get header from non-fragmented pipeline first
                        var payloadMaxSize = networkDriver.GetMaxSupportedMessageSize(connection) - headerSize;
                        if (totalSnapshotSize > payloadMaxSize)
                        {
                            // we used the fragmented pipeline, getting that header there
                            headerSize = networkDriver.MaxHeaderSize(driverStore.GetDriverInstanceRO(i).unreliableFragmentedPipeline);
                            payloadMaxSize = networkDriver.GetMaxSupportedMessageSize(connection) - headerSize;
                        }
                        snapshotStatsSingleton.UnsafeMainStatsRead.PacketsCount += (uint)math.ceil(totalSnapshotSize / payloadMaxSize);

                        break; // TODO we currently don't take into account snapshot size per connection, only globally. We assume the payload max size is the same for each connections and each non-IPC driver (which might not be the case in real life) currently, but would need to adapt to a per connection stats eventually. Breaking for now.
                    }

                    break;
                }
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_HasMonitor = SystemAPI.TryGetSingleton<GhostMetricsMonitor>(out var monitorComponent);

            ref var collectionData = ref SystemAPI.GetSingletonRW<GhostStatsCollectionData>().ValueRW;

            SystemAPI.SetSingleton(new GhostStats{IsConnected = collectionData.m_StatIndex >= 0});

            if ((!m_HasMonitor && collectionData.m_StatIndex < 0) || state.WorldUnmanaged.IsThinClient())
                return;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var currentTick = networkTime.ServerTick;
            if (currentTick != collectionData.m_CollectionTick)
            {
                UpdateMetrics(ref state, currentTick);
                BeginCollection(ref state, currentTick, ref collectionData);
            }

            state.CompleteDependency(); // We complete the dependency. This is needed because NetworkSnapshotAck is written by a job in NetworkStreamReceiveSystem
            AddCommandStats(m_CommandStatsData, collectionData);
            AddDiscardedPackets(m_CommandStatsData[2], collectionData);
            m_CommandStatsData[0] = 0;
            m_CommandStatsData[1] = 0;
            m_CommandStatsData[2] = 0;

            // merge stats for current frame if there's any from different threads
            ref var snapshotStatsSingleton = ref SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW;
            if (snapshotStatsSingleton.allGhostStatsParallelWrites.Length > 0 && snapshotStatsSingleton.MainStatsWrite.Tick.SerializedData != 0)
            {
                ref var mainStats = ref snapshotStatsSingleton.MainStatsWrite;
                // increment main writer with worker writer stats
                for (int worker = 1; worker < snapshotStatsSingleton.allGhostStatsParallelWrites.Length; worker++)
                {
                    ref var currentWorkerWriteStats = ref snapshotStatsSingleton.allGhostStatsParallelWrites.ElementAt(worker);
                    mainStats.IncrementWith(currentWorkerWriteStats);
                    currentWorkerWriteStats.ResetToDefault();
                }

                // swap to read stats
                snapshotStatsSingleton.UpdateDoubleBufferReadStats(collectionData, m_SnapshotTicks.Length, m_HasMonitor);
                m_SnapshotTicks.Add(snapshotStatsSingleton.MainStatsWrite.Tick);
                // reset main writer, those stats are saved in the reader now
                snapshotStatsSingleton.MainStatsWrite.ResetToDefault();
            }
            UpdateSnapshotPacketCount(ref state, ref snapshotStatsSingleton);

            if (m_PredictionErrorStatsData.Length > 0)
            {
                AddPredictionErrorStats(m_PredictionErrorStatsData.AsArray(), collectionData);
                m_PredictionErrorStatsData.Clear();
            }

            m_SnapshotTickMin = m_MinMaxTickStatsData[0];
            m_SnapshotTickMax = m_MinMaxTickStatsData[1];
            m_MinMaxTickStatsData[0] = NetworkTick.Invalid;
            m_MinMaxTickStatsData[1] = NetworkTick.Invalid;

            // Gather the min/max age stats
#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            for (int i = 1; i < maxThreadCount; ++i)
            {
                if (m_MinMaxTickStatsData[intsPerCacheLine*i].IsValid &&
                    (!m_SnapshotTickMin.IsValid ||
                    m_SnapshotTickMin.IsNewerThan(m_MinMaxTickStatsData[intsPerCacheLine*i])))
                    m_SnapshotTickMin = m_MinMaxTickStatsData[intsPerCacheLine*i];
                if (m_MinMaxTickStatsData[intsPerCacheLine*i+1].IsValid &&
                    (!m_SnapshotTickMax.IsValid ||
                    m_MinMaxTickStatsData[intsPerCacheLine*i+1].IsNewerThan(m_SnapshotTickMax)))
                    m_SnapshotTickMax = m_MinMaxTickStatsData[intsPerCacheLine*i+1];
                m_MinMaxTickStatsData[intsPerCacheLine*i] = NetworkTick.Invalid;
                m_MinMaxTickStatsData[intsPerCacheLine*i+1] = NetworkTick.Invalid;
            }

            if (!setupRecorders && SystemAPI.TryGetSingletonEntity<GhostMetricsMonitor>(out var entity))
            {
                if (state.EntityManager.HasComponent<GhostNames>(entity) &&
                    state.EntityManager.HasComponent<GhostSerializationMetrics>(entity))
                {
                    var ghostNames = SystemAPI.GetSingletonBuffer<GhostNames>();
                    if (ghostNames.Length > 0)
                    {
                        var job = new ProfilerRecorderJob
                        {
                            names = ghostNames,
                            recorders = m_Recorders
                        };
                        state.Dependency = job.Schedule(state.Dependency);
                        setupRecorders = true;
                    }
                }
            }

            if (!SystemAPI.HasSingleton<UnscaledClientTime>() || !SystemAPI.HasSingleton<NetworkSnapshotAck>())
                return;

            var ack = SystemAPI.GetSingleton<NetworkSnapshotAck>();
            var networkTimeSystemStats = SystemAPI.GetSingleton<NetworkTimeSystemStats>();
            int minAge = m_SnapshotTickMax.IsValid?currentTick.TicksSince(m_SnapshotTickMax):0;
            int maxAge = m_SnapshotTickMin.IsValid?currentTick.TicksSince(m_SnapshotTickMin):0;
            var timeSample = new TimeSample
            {
                sampleFraction = networkTime.ServerTickFraction,
                timeScale = networkTimeSystemStats.GetAverageTimeScale(),
                interpolationScale = networkTimeSystemStats.GetAverageIterpTimeScale(),
                interpolationOffset = networkTimeSystemStats.currentInterpolationFrames,
                commandAge = ack.ServerCommandAge / 256f,
                rtt = ack.EstimatedRTT,
                jitter = ack.DeviationRTT,
                snapshotAgeMin = minAge,
                snapshotAgeMax = maxAge,
            };
            if (m_TimeSamples.Length < 255)
                m_TimeSamples.Add(timeSample);
        }

        void BeginCollection(ref SystemState state, NetworkTick currentTick, ref GhostStatsCollectionData collectionData)
        {
            if (collectionData.m_StatIndex >= 0 && collectionData.m_CollectionTick.IsValid)
                BuildPacket(ref state, ref collectionData);

            collectionData.m_CollectionTick = currentTick;
            m_SnapshotTicks.Clear();
            m_TimeSamples.Clear();
            SystemAPI.GetSingletonRW<GhostStatsSnapshotSingleton>().ValueRW.UnsafeMainStatsRead.ResetToDefault();
            for (int i = 0; i < m_PredictionErrors.Length; ++i)
            {
                m_PredictionErrors[i] = 0;
            }

            m_CommandTicks.Clear();
            m_CommandStats = 0;
            m_DiscardedPackets = 0;
        }

        void BuildPacket(ref SystemState state, ref GhostStatsCollectionData statsData)
        {
            statsData.EnsurePoolSize(statsData.m_MaxPacketSize);
            int binarySize = 0;
            var binaryData = ((byte*)statsData.m_PacketPool.GetUnsafePtr()) + statsData.m_UsedPacketPoolSize;
            *(uint*) binaryData = statsData.m_CollectionTick.TickIndexForValidTick;
            binarySize += 4;
            binaryData[binarySize++] = (byte) statsData.m_StatIndex;
            binaryData[binarySize++] = (byte) m_TimeSamples.Length;
            binaryData[binarySize++] = (byte) m_SnapshotTicks.Length;
            binaryData[binarySize++] = (byte) m_CommandTicks.Length;
            binaryData[binarySize++] = 0; // rpcs
            binaryData[binarySize++] = (byte)m_DiscardedPackets;
            binaryData[binarySize++] = 0; // unused
            binaryData[binarySize++] = 0; // unused

            for (int i = 0; i < m_TimeSamples.Length; ++i)
            {
                float* timeSample = (float*) (binaryData + binarySize);
                timeSample[0] = m_TimeSamples[i].sampleFraction;
                timeSample[1] = m_TimeSamples[i].timeScale;
                timeSample[2] = m_TimeSamples[i].interpolationOffset;
                timeSample[3] = m_TimeSamples[i].interpolationScale;
                timeSample[4] = m_TimeSamples[i].commandAge;
                timeSample[5] = m_TimeSamples[i].rtt;
                timeSample[6] = m_TimeSamples[i].jitter;
                timeSample[7] = m_TimeSamples[i].snapshotAgeMin;
                timeSample[8] = m_TimeSamples[i].snapshotAgeMax;
                binarySize += 36;
            }
            // Write snapshots
            for (int i = 0; i < m_SnapshotTicks.Length; ++i)
            {
                *(uint*) (binaryData + binarySize) = m_SnapshotTicks[i].TickIndexForValidTick;
                binarySize += 4;
            }

            var statsSingleton = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>();

            using var bytes = statsSingleton.UnsafeMainStatsRead.ToOldBinary(Allocator.Temp, state.WorldUnmanaged.IsClient()).Reinterpret<byte>(UnsafeUtility.SizeOf<uint>());
            UnsafeUtility.MemCpy(binaryData + binarySize, bytes.GetUnsafePtr(), bytes.Length);
            binarySize += bytes.Length;
            // Write prediction errors
            for (int i = 0; i < m_PredictionErrors.Length; ++i)
            {
                *(float*) (binaryData + binarySize) = m_PredictionErrors[i];
                binarySize += 4;
            }
            // Write commands
            for (int i = 0; i < m_CommandTicks.Length; ++i)
            {
                *(uint*) (binaryData + binarySize) = m_CommandTicks[i];
                binarySize += 4;
            }
            *(uint*) (binaryData + binarySize) = m_CommandStats;
            binarySize += 4;

            statsData.m_PacketQueue.Add(new Packet
            {
                dataSize = binarySize,
                dataOffset = statsData.m_UsedPacketPoolSize
            });
            statsData.m_UsedPacketPoolSize += binarySize;
        }


        internal struct Packet
        {
            public int dataSize;
            public int dataOffset;
            public bool isString;
        }

        private bool setupRecorders;

        private NativeList<Packet> m_PacketQueue;
        private NativeList<byte> m_PacketPool;

        private NativeList<ProfilerRecorder> m_Recorders;
        private NetworkTick m_SnapshotTickMin;
        private NetworkTick m_SnapshotTickMax;
        private NativeList<TimeSample> m_TimeSamples;
        private NativeList<NetworkTick> m_SnapshotTicks; // TODO does the following make sense? --> These are the ticks of the individual snapshots we got in the past few frames before consuming them. Since receives runs at frame rate, but a server tick runs at tick rate, we can have multiple frames, each receiving different snapshots
        private NativeList<float> m_PredictionErrors;
        private uint m_CommandStats;
        private uint m_DiscardedPackets;
        private NativeList<uint> m_CommandTicks;

        private NativeText m_LastNameAndErrorArray;
        private NativeArray<uint> m_CommandStatsData;
        private NativeList<float> m_PredictionErrorStatsData;
        private NativeArray<NetworkTick> m_MinMaxTickStatsData;

        struct TimeSample
        {
            public float sampleFraction;
            public float timeScale;
            public float interpolationOffset;
            public float interpolationScale;
            public float commandAge;
            public float rtt;
            public float jitter;
            public float snapshotAgeMin;
            public float snapshotAgeMax;
        }
        // TODO move this to GhostMetrics
        // updates GhostMetrics with read buffer from last frame
        void UpdateMetrics(ref SystemState state, NetworkTick currentTick)
        {
            var hasTimeSamples = m_TimeSamples.Length > 0;
            var hasSnapshotSamples = m_SnapshotTicks.Length > 0;
            var readStats = SystemAPI.GetSingleton<GhostStatsSnapshotSingleton>().GetAsyncStatsReader();
            var ghostTypeStats = readStats.PerGhostTypeStatsListRO;
            var hasSnapshotStats = ghostTypeStats.IsCreated && ghostTypeStats.Length > 0;
            var hasPredictionErrors = m_PredictionErrors.Length > 0;

            uint totalSize = 0;
            uint totalCount = 0;

            if (SystemAPI.TryGetSingletonEntity<GhostMetricsMonitor>(out var entity))
            {
                ref var simulationMetrics = ref SystemAPI.GetSingletonRW<GhostMetricsMonitor>().ValueRW;
                simulationMetrics.CapturedTick = currentTick;

                if (hasTimeSamples && state.EntityManager.HasComponent<NetworkMetrics>(entity))
                {
                    ref var networkMetrics = ref SystemAPI.GetSingletonRW<NetworkMetrics>().ValueRW;

                    networkMetrics.SampleFraction = m_TimeSamples[0].sampleFraction;
                    networkMetrics.TimeScale = m_TimeSamples[0].timeScale;
                    networkMetrics.InterpolationOffset = m_TimeSamples[0].interpolationOffset;
                    networkMetrics.InterpolationScale = m_TimeSamples[0].interpolationScale;
                    networkMetrics.CommandAge = m_TimeSamples[0].commandAge;
                    networkMetrics.Rtt = m_TimeSamples[0].rtt;
                    networkMetrics.Jitter = m_TimeSamples[0].jitter;
                    networkMetrics.SnapshotAgeMin = m_TimeSamples[0].snapshotAgeMin;
                    networkMetrics.SnapshotAgeMax = m_TimeSamples[0].snapshotAgeMax;
                }
                if (hasPredictionErrors && state.EntityManager.HasComponent<PredictionErrorMetrics>(entity))
                {
                    if (SystemAPI.TryGetSingletonBuffer<PredictionErrorMetrics>(out var predictionErrorMetrics))
                    {
                        predictionErrorMetrics.Clear();
                        var count = m_PredictionErrors.Length;

                        for (int i = 0; i < count; i++)
                        {
                            predictionErrorMetrics.Add(new PredictionErrorMetrics
                            {
                                Value = m_PredictionErrors[i]
                            });
                        }
                    }
                }

                if (hasSnapshotStats && state.EntityManager.HasComponent<GhostMetrics>(entity))
                {
                    if (SystemAPI.TryGetSingletonBuffer<GhostMetrics>(out var ghostMetrics))
                    {
                        ghostMetrics.Clear();
                        var perGhostTypeStats = ghostTypeStats;
                        for (int ghostTypeIndex = 0; ghostTypeIndex < perGhostTypeStats.Length; ghostTypeIndex++)
                        {
                            var perTypeStat = perGhostTypeStats[ghostTypeIndex];
                            ghostMetrics.Add(new GhostMetrics
                            {
                                InstanceCount = perTypeStat.EntityCount,
                                SizeInBits = perTypeStat.SizeInBits,
                                ChunkCount = perTypeStat.ChunkCount,
                                Uncompressed = perTypeStat.UncompressedCount,
                            });
                            totalSize += perTypeStat.SizeInBits;
                            totalCount += perTypeStat.EntityCount;
                            }
                    }
                }
                if (hasSnapshotSamples && state.EntityManager.HasComponent<SnapshotMetrics>(entity))
                {
                    ref var snapshotMetrics = ref SystemAPI.GetSingletonRW<SnapshotMetrics>().ValueRW;

                    snapshotMetrics.SnapshotTick = readStats.Tick.SerializedData;
                    snapshotMetrics.TotalSizeInBits = totalSize;
                    snapshotMetrics.TotalGhostCount = totalCount;
                    snapshotMetrics.DestroyInstanceCount = hasSnapshotStats ? readStats.DespawnCount : 0;
                    snapshotMetrics.DestroySizeInBits = hasSnapshotStats ? readStats.DestroySizeInBits : 0;
                }
            }

            if (m_Recorders.IsCreated && SystemAPI.TryGetSingletonBuffer<GhostSerializationMetrics>(out var serializationMetrics))
            {
                serializationMetrics.Clear();
                var count = m_Recorders.Length;

                for (int i = 0; i < count; i++)
                {
                    serializationMetrics.Add(new GhostSerializationMetrics
                    {
                        LastRecordedValue = m_Recorders[i].LastValue
                    });
                }
            }
        }

        struct ProfilerRecorderJob : IJob
        {
            public DynamicBuffer<GhostNames> names;
            public NativeList<ProfilerRecorder> recorders;
            public void Execute()
            {
                for (int i = 0; i < names.Length; i++)
                {
                    recorders.Add(ProfilerRecorder.StartNew(new ProfilerCategory("GhostSendSystem"),
                        names[i].Name.Value));
                }
            }
        }
    }

    // collects data in binary packets to be sent to the web page
    internal struct GhostStatsCollectionData : IComponentData
    {
        public NativeList<byte> m_PacketPool; // websocket packets we send to the profiler webpage
        public NativeList<GhostStatsCollectionSystem.Packet> m_PacketQueue;
        public NativeText m_LastNameAndErrorArray;
        public NativeList<float> m_PredictionErrors;
        public int m_StatIndex;
        public int m_UsedPacketPoolSize;
        public int m_MaxPacketSize;
        public NetworkTick m_CollectionTick;

        public void EnsurePoolSize(int packetSize)
        {
            if (m_UsedPacketPoolSize + packetSize > m_PacketPool.Length)
            {
                int newLen = m_PacketPool.Length*2;
                while (m_UsedPacketPoolSize + packetSize > newLen)
                    newLen *= 2;
                m_PacketPool.Resize(newLen, NativeArrayOptions.UninitializedMemory);
            }
        }

        public void UpdateMaxPacketSize(int snapshotStatsLength, int predictionErrorsLength)
        {
            // Calculate a new max packet size
            var packetSize = 8 + 20 * 255 + 4 * snapshotStatsLength + 4 * predictionErrorsLength + 4 * 255;
            if (packetSize == m_MaxPacketSize)
                return;
            m_MaxPacketSize = packetSize;

            // Drop all pending packets not yet in the queue
            m_CollectionTick = NetworkTick.Invalid;
        }

        /// <summary>
        /// Setup the ghosts prefabs and error names (used by the NetworkDebugger tool). Called after the prefab collection has been
        /// processed by the <see cref="GhostCollectionSystem"/>
        /// </summary>
        /// <param name="nameList"></param>
        /// <param name="errorList"></param>
        /// <param name="worldName"></param>
        public void SetGhostNames(in FixedString128Bytes worldName,
            NativeList<FixedString64Bytes> nameList, NativeList<PredictionErrorNames> errorList,
            int predictedErrorCount, ref GhostStatsSnapshotSingleton snapshotStatsSingleton)
        {
            // Add a pending packet with the new list of names
            m_LastNameAndErrorArray.Clear();
            m_LastNameAndErrorArray.Append((FixedString32Bytes)"\"name\":\"");
            m_LastNameAndErrorArray.Append(worldName);
            m_LastNameAndErrorArray.Append((FixedString32Bytes)"\",\"ghosts\":[\"Destroy\"");
            for (int i = 0; i < nameList.Length; ++i)
            {
                m_LastNameAndErrorArray.Append(',');
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(nameList[i]);
                m_LastNameAndErrorArray.Append('"');
            }

            m_LastNameAndErrorArray.Append((FixedString32Bytes)"], \"errors\":[");
            if (errorList.Length > 0)
            {
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(errorList[0].Name);
                m_LastNameAndErrorArray.Append('"');
            }
            for (int i = 1; i < errorList.Length; ++i)
            {
                m_LastNameAndErrorArray.Append(',');
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(errorList[i].Name);
                m_LastNameAndErrorArray.Append('"');
            }

            m_LastNameAndErrorArray.Append(']');

            // This is called when the ghost collection is updated, so we then know if there's new ghost types or removed ghost types and we can adjust the relevant stats lists sizes.
            snapshotStatsSingleton.UnsafeMainStatsRead.Reset(nameList.Length);

            //we are clearing before resizing, because the resize will otherwise memcpy the old values
            if (m_PredictionErrors.Length != predictedErrorCount)
            {
                m_PredictionErrors.Clear();
                m_PredictionErrors.ResizeUninitialized(predictedErrorCount);
            }

            if (m_StatIndex < 0)
                return;

            AppendNamePacket(snapshotStatsSingleton);
        }
        public unsafe void AppendNamePacket(in GhostStatsSnapshotSingleton snapshotStatsSingleton)
        {
            FixedString64Bytes header = "{\"index\":";
            header.Append(m_StatIndex);
            header.Append(',');
            FixedString32Bytes footer = "}";

            var totalLen = header.Length + m_LastNameAndErrorArray.Length + footer.Length;
            EnsurePoolSize(totalLen);

            var binaryData = ((byte*)m_PacketPool.GetUnsafePtr()) + m_UsedPacketPoolSize;
            UnsafeUtility.MemCpy(binaryData, header.GetUnsafePtr(), header.Length);
            UnsafeUtility.MemCpy(binaryData + header.Length, m_LastNameAndErrorArray.GetUnsafePtr(), m_LastNameAndErrorArray.Length);
            UnsafeUtility.MemCpy(binaryData + header.Length + m_LastNameAndErrorArray.Length, footer.GetUnsafePtr(), footer.Length);

            m_PacketQueue.Add(new GhostStatsCollectionSystem.Packet
            {
                dataSize = totalLen,
                dataOffset = m_UsedPacketPoolSize,
                isString = true
            });
            m_UsedPacketPoolSize += totalLen;
            // Make sure the packet size is big enough for the new snapshot stats
            UpdateMaxPacketSize(snapshotStatsSingleton.UnsafeMainStatsRead.ByteOldSize(), m_PredictionErrors.Length);
        }
    }
}
#endif
