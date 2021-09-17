#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderLast = true)]
    [AlwaysUpdateSystem]
    partial class GhostStatsCollectionSystem : SystemBase
    {
        private ServerSimulationSystemGroup m_ServerSimulationSystemGroup;
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private NetworkTimeSystem m_NetworkTimeSystem;

        public bool IsConnected => m_StatIndex>=0;

        public void SetIndex(int index)
        {
            m_StatIndex = index;
            m_CollectionTick = 0;
            m_PacketQueue.Clear();
            m_UsedPacketPoolSize = 0;

            if (m_LastNameAndErrorArray.Length > 0)
            {
                AppendNamePacket();
            }
        }

        private unsafe void AppendNamePacket()
        {
            FixedString64Bytes header = "{\"index\":";
            header.Append(m_StatIndex);
            header.Append(',');
            FixedString32Bytes footer = "}";

            var totalLen = header.Length + m_LastNameAndErrorArray.Length + footer.Length;
            EnsurePoolSize(totalLen);
            fixed (byte* poolData = m_PacketPool)
            {
                var binaryData = poolData + m_UsedPacketPoolSize;
                UnsafeUtility.MemCpy(binaryData, header.GetUnsafePtr(), header.Length);
                UnsafeUtility.MemCpy(binaryData + header.Length, m_LastNameAndErrorArray.GetUnsafePtr(), m_LastNameAndErrorArray.Length);
                UnsafeUtility.MemCpy(binaryData + header.Length + m_LastNameAndErrorArray.Length, footer.GetUnsafePtr(), footer.Length);
            }

            m_PacketQueue.Add(new Packet
            {
                dataSize = totalLen,
                dataOffset = m_UsedPacketPoolSize,
                isString = true
            });
            m_UsedPacketPoolSize += totalLen;
            // Make sure the packet size is big enough for the new snapshot stats
            UpdateMaxPacketSize();
        }

        public void SetSnapshotTick(uint minTick, uint maxTick)
        {
            m_SnapshotTickMin = minTick;
            m_SnapshotTickMax = maxTick;
        }

        private NativeText m_LastNameAndErrorArray;

        public void SetGhostNames(NativeList<FixedString128Bytes> nameList, NativeList<FixedString512Bytes> errorList)
        {
            // Add a pending packet with the new list of names
            m_LastNameAndErrorArray.Clear();
            m_LastNameAndErrorArray.Append("\"name\":\"");
            m_LastNameAndErrorArray.Append(World.Name);
            m_LastNameAndErrorArray.Append("\",\"ghosts\":[\"Destroy\"");
            for (int i = 0; i < nameList.Length; ++i)
            {
                m_LastNameAndErrorArray.Append(',');
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(nameList[i]);
                m_LastNameAndErrorArray.Append('"');
            }

            m_LastNameAndErrorArray.Append("], \"errors\":[");
            if (errorList.Length > 0)
            {
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(errorList[0]);
                m_LastNameAndErrorArray.Append('"');
            }
            for (int i = 1; i < errorList.Length; ++i)
            {
                m_LastNameAndErrorArray.Append(',');
                m_LastNameAndErrorArray.Append('"');
                m_LastNameAndErrorArray.Append(errorList[i]);
                m_LastNameAndErrorArray.Append('"');
            }

            m_LastNameAndErrorArray.Append(']');

            if (m_SnapshotStats.Length != (nameList.Length+1)*3)
            {
                // Reset the snapshot stats
                m_SnapshotStats.Dispose();
                m_SnapshotStats = new NativeArray<uint>((nameList.Length + 1)*3, Allocator.Persistent);
            }

            if (m_PredictionErrors.Length != errorList.Length)
            {
                // Reset the snapshot stats
                m_PredictionErrors.Dispose();
                m_PredictionErrors = new NativeArray<float>(errorList.Length, Allocator.Persistent);
            }

            if (m_StatIndex < 0)
                return;
            AppendNamePacket();
        }

        public void AddSnapshotStats(NativeArray<uint> stats)
        {
            if (stats[0] == 0 || m_SnapshotTicks.Length >= 255 || m_StatIndex < 0)
                return;
            uint currentTick = (m_ClientSimulationSystemGroup!=null) ? m_ClientSimulationSystemGroup.ServerTick : m_ServerSimulationSystemGroup.ServerTick;
            if (currentTick != m_CollectionTick)
                BeginCollection(currentTick);
            for (int i = 1; i < stats.Length; ++i)
                m_SnapshotStats[i-1] += stats[i];
            m_SnapshotTicks.Add(stats[0]);
        }
        public void AddCommandStats(NativeArray<uint> stats)
        {
            if (stats[0] == 0 || m_CommandTicks.Length >= 255 || m_StatIndex < 0)
                return;
            uint currentTick = (m_ClientSimulationSystemGroup!=null) ? m_ClientSimulationSystemGroup.ServerTick : m_ServerSimulationSystemGroup.ServerTick;
            if (currentTick != m_CollectionTick)
                BeginCollection(currentTick);
            m_CommandStats += stats[1];
            if (m_CommandTicks.Length == 0 || m_CommandTicks[m_CommandTicks.Length-1] != stats[0])
                m_CommandTicks.Add(stats[0]);
        }
        public void AddPredictionErrorStats(NativeArray<float> stats)
        {
            if (m_SnapshotTicks.Length >= 255 || m_StatIndex < 0)
                return;
            uint currentTick = (m_ClientSimulationSystemGroup!=null) ? m_ClientSimulationSystemGroup.ServerTick : m_ServerSimulationSystemGroup.ServerTick;
            if (currentTick != m_CollectionTick)
                BeginCollection(currentTick);

            for (int i = 0; i < stats.Length; ++i)
                m_PredictionErrors[i] = math.max(stats[i], m_PredictionErrors[i]);
        }

        public void AddDiscardedPackets(uint stats)
        {
            m_DiscardedPackets += stats;
        }

        protected override void OnCreate()
        {
            m_StatIndex = -1;

            m_ServerSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            if (m_ClientSimulationSystemGroup != null)
                m_NetworkTimeSystem = World.GetOrCreateSystem<NetworkTimeSystem>();
            m_SnapshotStats = new NativeArray<uint>(0, Allocator.Persistent);
            m_SnapshotTicks = new NativeList<uint>(16, Allocator.Persistent);
            m_PredictionErrors = new NativeArray<float>(0, Allocator.Persistent);
            m_TimeSamples = new NativeList<TimeSample>(16, Allocator.Persistent);
            m_CommandTicks = new NativeList<uint>(16, Allocator.Persistent);

            m_PacketQueue = new NativeList<Packet>(16, Allocator.Persistent);
            m_PacketPool = new byte[4096];

            m_LastNameAndErrorArray = new NativeText(4096, Allocator.Persistent);

            UpdateMaxPacketSize();
        }

        protected override void OnDestroy()
        {
            m_LastNameAndErrorArray.Dispose();
            m_PacketQueue.Dispose();
            m_CommandTicks.Dispose();
            m_SnapshotStats.Dispose();
            m_SnapshotTicks.Dispose();
            m_PredictionErrors.Dispose();
            m_TimeSamples.Dispose();
        }
        protected override void OnUpdate()
        {
            if (m_StatIndex < 0 || HasSingleton<ThinClientComponent>())
                return;
            uint currentTick = (m_ClientSimulationSystemGroup!=null) ? m_ClientSimulationSystemGroup.ServerTick : m_ServerSimulationSystemGroup.ServerTick;
            if (currentTick != m_CollectionTick)
                BeginCollection(currentTick);
            if (m_ClientSimulationSystemGroup == null || !HasSingleton<NetworkSnapshotAckComponent>())
                return;
            var ack = GetSingleton<NetworkSnapshotAckComponent>();
            var timeSample = new TimeSample
            {
                sampleFraction = m_ClientSimulationSystemGroup.ServerTickFraction,
                timeScale = m_NetworkTimeSystem.GetAverageTimeScale(),
                interpolationOffset = m_NetworkTimeSystem.currentInterpolationFrames,
                commandAge = ack.ServerCommandAge / 256f,
                rtt = ack.EstimatedRTT,
                jitter = ack.DeviationRTT,
                snapshotAgeMin = m_SnapshotTickMax!=0?(currentTick-m_SnapshotTickMax):0,
                snapshotAgeMax = m_SnapshotTickMin!=0?(currentTick-m_SnapshotTickMin):0,
            };
            if (m_TimeSamples.Length < 255)
                m_TimeSamples.Add(timeSample);
        }

        void BeginCollection(uint currentTick)
        {
            if (m_CollectionTick != 0)
                BuildPacket();
            m_CollectionTick = currentTick;
            m_SnapshotTicks.Clear();
            m_TimeSamples.Clear();
            for (int i = 0; i < m_SnapshotStats.Length; ++i)
            {
                m_SnapshotStats[i] = 0;
            }
            for (int i = 0; i < m_PredictionErrors.Length; ++i)
            {
                m_PredictionErrors[i] = 0;
            }

            m_CommandTicks.Clear();
            m_CommandStats = 0;
            m_DiscardedPackets = 0;
        }
        unsafe void BuildPacket()
        {
            EnsurePoolSize(m_MaxPacketSize);

            int binarySize = 0;
            fixed (byte* poolData = m_PacketPool)
            {
                var binaryData = poolData + m_UsedPacketPoolSize;
                *(uint*) binaryData = m_CollectionTick;
                binarySize += 4;
                binaryData[binarySize++] = (byte) m_StatIndex;
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
                    timeSample[3] = m_TimeSamples[i].commandAge;
                    timeSample[4] = m_TimeSamples[i].rtt;
                    timeSample[5] = m_TimeSamples[i].jitter;
                    timeSample[6] = m_TimeSamples[i].snapshotAgeMin;
                    timeSample[7] = m_TimeSamples[i].snapshotAgeMax;
                    binarySize += 32;
                }
                // Write snapshots
                for (int i = 0; i < m_SnapshotTicks.Length; ++i)
                {
                    *(uint*) (binaryData + binarySize) = m_SnapshotTicks[i];
                    binarySize += 4;
                }
                for (int i = 0; i < m_SnapshotStats.Length; ++i)
                {
                    *(uint*) (binaryData + binarySize) = m_SnapshotStats[i];
                    binarySize += 4;
                }
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
            }

            m_PacketQueue.Add(new Packet
            {
                dataSize = binarySize,
                dataOffset = m_UsedPacketPoolSize
            });
            m_UsedPacketPoolSize += binarySize;
        }

        public void SendPackets(DebugWebSocket socket)
        {
            foreach (var packet in m_PacketQueue)
            {
                if (packet.isString)
                    socket.SendText(m_PacketPool, packet.dataOffset, packet.dataSize);
                else
                    socket.SendBinary(m_PacketPool, packet.dataOffset, packet.dataSize);
            }
            m_PacketQueue.Clear();
            m_UsedPacketPoolSize = 0;
        }

        void EnsurePoolSize(int packetSize)
        {
            if (m_UsedPacketPoolSize + packetSize > m_PacketPool.Length)
            {
                int newLen = m_PacketPool.Length*2;
                while (m_UsedPacketPoolSize + packetSize > newLen)
                    newLen *= 2;
                var newPool = new byte[newLen];
                m_PacketPool.CopyTo(newPool, 0);
                m_PacketPool = newPool;
            }
        }
        void UpdateMaxPacketSize()
        {
            // Calculate a new max packet size
            var packetSize = 8 + 20 * 255 + 4 * m_SnapshotStats.Length + 4 * m_PredictionErrors.Length + 4 * 255;
            if (packetSize == m_MaxPacketSize)
                return;
            m_MaxPacketSize = packetSize;

            // Drop all pending packets not yet in the queue
            m_CollectionTick = 0;
        }

        internal struct Packet
        {
            public int dataSize;
            public int dataOffset;
            public bool isString;
        }
        private NativeList<Packet> m_PacketQueue;
        private byte[] m_PacketPool;
        private int m_UsedPacketPoolSize;
        private int m_MaxPacketSize;

        private uint m_CollectionTick;
        private uint m_SnapshotTickMin;
        private uint m_SnapshotTickMax;
        private NativeList<TimeSample> m_TimeSamples;
        private NativeArray<uint> m_SnapshotStats;
        private NativeList<uint> m_SnapshotTicks;
        private NativeArray<float> m_PredictionErrors;
        private uint m_CommandStats;
        private uint m_DiscardedPackets;
        private NativeList<uint> m_CommandTicks;
        private int m_StatIndex;

        struct TimeSample
        {
            public float sampleFraction;
            public float timeScale;
            public float interpolationOffset;
            public float commandAge;
            public float rtt;
            public float jitter;
            public float snapshotAgeMin;
            public float snapshotAgeMax;
        }
    }
}
#endif
