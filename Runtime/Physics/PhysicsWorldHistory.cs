using System;
using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Jobs;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    [StructLayout(LayoutKind.Sequential)]
    public struct CollisionHistoryBuffer
    {
        public const int Capacity = 16;
        private CollisionWorld CollisionWorld00;
        private CollisionWorld CollisionWorld01;
        private CollisionWorld CollisionWorld02;
        private CollisionWorld CollisionWorld03;
        private CollisionWorld CollisionWorld04;
        private CollisionWorld CollisionWorld05;
        private CollisionWorld CollisionWorld06;
        private CollisionWorld CollisionWorld07;
        private CollisionWorld CollisionWorld08;
        private CollisionWorld CollisionWorld09;
        private CollisionWorld CollisionWorld10;
        private CollisionWorld CollisionWorld11;
        private CollisionWorld CollisionWorld12;
        private CollisionWorld CollisionWorld13;
        private CollisionWorld CollisionWorld14;
        private CollisionWorld CollisionWorld15;

        public int Size => m_size;
        private int m_size;
        internal uint m_lastStoredTick;

        public CollisionHistoryBuffer(int size)
        {
            if (size > Capacity)
                throw new ArgumentOutOfRangeException($"Invalid size {size}. Must be <= {Capacity}");
            m_size = size;
            m_lastStoredTick = 0;
            CollisionWorld00 = CollisionWorld01 = CollisionWorld02 = CollisionWorld03 =
                CollisionWorld04 = CollisionWorld05 = CollisionWorld06 = CollisionWorld07 =
                CollisionWorld08 = CollisionWorld09 = CollisionWorld10 = CollisionWorld11 =
                CollisionWorld12 = CollisionWorld13 = CollisionWorld14 = CollisionWorld15 = default;
        }

        public void GetCollisionWorldFromTick(uint tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
            // Clamp to oldest physics copy when requesting older data than supported
            if (interpolationDelay > m_size-1)
                interpolationDelay = (uint)m_size-1;
            tick -= interpolationDelay;
            if (SequenceHelpers.IsNewer(tick, m_lastStoredTick))
                tick = m_lastStoredTick;
            var index = (int)(tick % m_size);
            GetCollisionWorldFromIndex(index, out collWorld);
        }

        public unsafe void DisposeIndex(int index)
        {
            if (m_size == 1 && index == 0)
                return;
            switch (index)
            {
                case 0 : CollisionWorld00.Dispose(); return;
                case 1 : CollisionWorld01.Dispose(); return;
                case 2 : CollisionWorld02.Dispose(); return;
                case 3 : CollisionWorld03.Dispose(); return;
                case 4 : CollisionWorld04.Dispose(); return;
                case 5 : CollisionWorld05.Dispose(); return;
                case 6 : CollisionWorld06.Dispose(); return;
                case 7 : CollisionWorld07.Dispose(); return;
                case 8 : CollisionWorld08.Dispose(); return;
                case 9 : CollisionWorld09.Dispose(); return;
                case 10 : CollisionWorld10.Dispose(); return;
                case 11 : CollisionWorld11.Dispose(); return;
                case 12 : CollisionWorld12.Dispose(); return;
                case 13 : CollisionWorld13.Dispose(); return;
                case 14 : CollisionWorld14.Dispose(); return;
                case 15 : CollisionWorld15.Dispose(); return;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        unsafe void GetCollisionWorldFromIndex(int index, out CollisionWorld collWorld)
        {
            switch (index)
            {
                case 0 : collWorld = CollisionWorld00;break;
                case 1 : collWorld = CollisionWorld01;break;
                case 2 : collWorld = CollisionWorld02;break;
                case 3 : collWorld = CollisionWorld03;break;
                case 4 : collWorld = CollisionWorld04;break;
                case 5 : collWorld = CollisionWorld05;break;
                case 6 : collWorld = CollisionWorld06;break;
                case 7 : collWorld = CollisionWorld07;break;
                case 8 : collWorld = CollisionWorld08;break;
                case 9 : collWorld = CollisionWorld09;break;
                case 10 : collWorld = CollisionWorld10;break;
                case 11 : collWorld = CollisionWorld11;break;
                case 12 : collWorld = CollisionWorld12;break;
                case 13 : collWorld = CollisionWorld13;break;
                case 14 : collWorld = CollisionWorld14;break;
                case 15 : collWorld = CollisionWorld15;break;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        public unsafe void CloneCollisionWorld(int index, in CollisionWorld collWorld)
        {
            var worldClone = (m_size==1 && index == 0) ? collWorld : collWorld.Clone();
            switch (index)
            {
                case 0 : CollisionWorld00 = (CollisionWorld)worldClone; break;
                case 1 : CollisionWorld01 = (CollisionWorld)worldClone;break;
                case 2 : CollisionWorld02 = (CollisionWorld)worldClone;break;
                case 3 : CollisionWorld03 = (CollisionWorld)worldClone;break;
                case 4 : CollisionWorld04 = (CollisionWorld)worldClone;break;
                case 5 : CollisionWorld05 = (CollisionWorld)worldClone;break;
                case 6 : CollisionWorld06 = (CollisionWorld)worldClone;break;
                case 7 : CollisionWorld07 = (CollisionWorld)worldClone;break;
                case 8 : CollisionWorld08 = (CollisionWorld)worldClone;break;
                case 9 : CollisionWorld09 = (CollisionWorld)worldClone;break;
                case 10 : CollisionWorld10 = (CollisionWorld)worldClone;break;
                case 11 : CollisionWorld11 = (CollisionWorld)worldClone;break;
                case 12 : CollisionWorld12 = (CollisionWorld)worldClone;break;
                case 13 : CollisionWorld13 = (CollisionWorld)worldClone;break;
                case 14 : CollisionWorld14 = (CollisionWorld)worldClone;break;
                case 15 : CollisionWorld15 = (CollisionWorld)worldClone;break;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

    }

    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    [AlwaysUpdateSystem]
    [AlwaysSynchronizeSystem]
    [UpdateBefore(typeof(BuildPhysicsWorld))]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    /// <summary>
    /// A system used to store old state of the physics world for lag compensation.
    /// You can get a CollisionHistoryBuffer from this system and from that you can
    /// get a physics collision world for a previous tick.
    /// When passing the collision history to a job you must set LastPhysicsJobHandle to
    /// the handle for that job.
    /// </summary>
    public class PhysicsWorldHistory : JobComponentSystem
    {
        private bool m_initialized;
        private uint m_lastStoredTick;

        public CollisionHistoryBuffer CollisionHistory
        {
            get { return m_CollisionHistory;  }
        }

        CollisionHistoryBuffer m_CollisionHistory;

        BuildPhysicsWorld m_BuildPhysicsWorld;
        ServerSimulationSystemGroup m_ServerSimulationSystemGroup;
        ClientSimulationSystemGroup m_ClientSimulationSystemGroup;

        public JobHandle LastPhysicsJobHandle;
        protected override void OnCreate()
        {
            m_ServerSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_CollisionHistory = new CollisionHistoryBuffer(m_ServerSimulationSystemGroup!=null ? CollisionHistoryBuffer.Capacity : 1);
        }
        protected override void OnDestroy()
        {
            if (m_initialized)
            {
                for(int i=0; i < CollisionHistoryBuffer.Capacity; ++i)
                    m_CollisionHistory.DisposeIndex(i);
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var serverTick = (m_ServerSimulationSystemGroup != null) ? m_ServerSimulationSystemGroup.ServerTick : m_ClientSimulationSystemGroup.ServerTick;

            if (m_BuildPhysicsWorld == null)
            {
                m_BuildPhysicsWorld = World.GetExistingSystem<BuildPhysicsWorld>();
                if (m_BuildPhysicsWorld == null)
                    return inputDeps;
            }
            m_BuildPhysicsWorld.FinalJobHandle.Complete();

            if (!m_initialized)
            {
                for (int i = 0; i < CollisionHistoryBuffer.Capacity; i++)
                {
                    m_CollisionHistory.CloneCollisionWorld(i, in m_BuildPhysicsWorld.PhysicsWorld.CollisionWorld);
                }

                m_lastStoredTick = serverTick;
                m_initialized = true;
            }
            else
            {
                if (serverTick <= m_lastStoredTick)
                    return inputDeps;

                // Store world for each tick that has not been stored yet (framerate might be lower than tickrate)
                var startStoreTick = (m_lastStoredTick != 0) ? (m_lastStoredTick + 1) : serverTick;
                for (var storeTick = startStoreTick; storeTick <= serverTick; storeTick++)
                {
                    var index = (int)(storeTick % m_CollisionHistory.Size);

                    m_CollisionHistory.DisposeIndex(index);
                    m_CollisionHistory.CloneCollisionWorld(index, in m_BuildPhysicsWorld.PhysicsWorld.CollisionWorld);
                }

                m_lastStoredTick = serverTick;
            }
            m_CollisionHistory.m_lastStoredTick = m_lastStoredTick;

            return inputDeps;
        }
    }
}
