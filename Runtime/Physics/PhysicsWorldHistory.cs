using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Networking.Transport.Utilities;

[assembly: InternalsVisibleTo("Unity.NetCode.Physics.EditorTests")]
namespace Unity.NetCode
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawHistoryBuffer
    {
        public const int Capacity = 16;

        public CollisionWorld world01;
        public CollisionWorld world02;
        public CollisionWorld world03;
        public CollisionWorld world04;
        public CollisionWorld world05;
        public CollisionWorld world06;
        public CollisionWorld world07;
        public CollisionWorld world08;
        public CollisionWorld world09;
        public CollisionWorld world10;
        public CollisionWorld world11;
        public CollisionWorld world12;
        public CollisionWorld world13;
        public CollisionWorld world14;
        public CollisionWorld world15;
        public CollisionWorld world16;
    }
    internal static class RawHistoryBufferExtension
    {
        public static ref CollisionWorld GetWorldAt(this ref RawHistoryBuffer buffer, int index)
        {
            switch (index)
            {
                case 0: return ref buffer.world01;
                case 1: return ref buffer.world02;
                case 2: return ref buffer.world03;
                case 3: return ref buffer.world04;
                case 4: return ref buffer.world05;
                case 5: return ref buffer.world06;
                case 6: return ref buffer.world07;
                case 7: return ref buffer.world08;
                case 8: return ref buffer.world09;
                case 9: return ref buffer.world10;
                case 10: return ref buffer.world11;
                case 11: return ref buffer.world12;
                case 12: return ref buffer.world13;
                case 13: return ref buffer.world14;
                case 14: return ref buffer.world15;
                case 15: return ref buffer.world16;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

        public static void SetWorldAt(this ref RawHistoryBuffer buffer, int index, in CollisionWorld world)
        {
            switch (index)
            {
                case 0: buffer.world01 = world; break;
                case 1: buffer.world02 = world; break;
                case 2: buffer.world03 = world; break;
                case 3: buffer.world04 = world; break;
                case 4: buffer.world05 = world; break;
                case 5: buffer.world06 = world; break;
                case 6: buffer.world07 = world; break;
                case 7: buffer.world08 = world; break;
                case 8: buffer.world09 = world; break;
                case 9: buffer.world10 = world; break;
                case 10:buffer.world11 = world; break;
                case 11:buffer.world12 = world; break;
                case 12:buffer.world13 = world; break;
                case 13:buffer.world14 = world; break;
                case 14:buffer.world15 = world; break;
                case 15:buffer.world16 = world; break;
                default:
                    throw new IndexOutOfRangeException();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CollisionHistoryBuffer : IDisposable
    {
        public const int Capacity = RawHistoryBuffer.Capacity;
        public int Size => m_size;
        private int m_size;
        internal uint m_lastStoredTick;

        private RawHistoryBuffer m_buffer;
        [NativeDisableUnsafePtrRestriction]
        private unsafe void* m_bufferCopyPtr;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        //For job checks
        private AtomicSafetyHandle m_Safety;
        //To avoid accessing the buffer if already disposed
        [NativeSetClassTypeToNullOnSchedule]
        private DisposeSentinel m_DisposeSentinel;
#if UNITY_2020_1_OR_NEWER
        private static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<CollisionHistoryBufferRef>();
        [BurstDiscard]
        private static void CreateStaticSafetyId()
        {
            s_staticSafetyId.Data = AtomicSafetyHandle.NewStaticSafetyId<CollisionHistoryBuffer>();
        }
#endif
#endif

        public CollisionHistoryBuffer(int size)
        {
            if (size > Capacity)
                throw new ArgumentOutOfRangeException($"Invalid size {size}. Must be <= {Capacity}");
            m_size = size;
            m_lastStoredTick = 0;
            var defaultWorld = default(CollisionWorld);
            m_buffer = new RawHistoryBuffer();
            for(int i=0;i<Capacity;++i)
            {
                m_buffer.SetWorldAt(i, defaultWorld);
            }

            unsafe
            {
                m_bufferCopyPtr = UnsafeUtility.Malloc(UnsafeUtility.SizeOf<RawHistoryBuffer>(), 8, Allocator.Persistent);
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Create(out this.m_Safety, out this.m_DisposeSentinel, 10, Allocator.Persistent);
#if UNITY_2020_1_OR_NEWER
            if (s_staticSafetyId.Data == 0)
            {
                CreateStaticSafetyId();
            }
            AtomicSafetyHandle.SetStaticSafetyId(ref m_Safety, s_staticSafetyId.Data);
#endif
#endif
        }

        public void GetCollisionWorldFromTick(uint tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            // Clamp to oldest physics copy when requesting older data than supported
            if (interpolationDelay > m_size-1)
                interpolationDelay = (uint)m_size-1;
            tick -= interpolationDelay;
            if (SequenceHelpers.IsNewer(tick, m_lastStoredTick))
                tick = m_lastStoredTick;
            var index = (int)(tick % m_size);
            GetCollisionWorldFromIndex(index, out collWorld);
        }

        public void DisposeIndex(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            m_buffer.GetWorldAt(index).Dispose();
        }

        void GetCollisionWorldFromIndex(int index, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            collWorld = m_buffer.GetWorldAt(index);
        }

        public void CloneCollisionWorld(int index, in CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            if (index >= Capacity)
            {
                throw new IndexOutOfRangeException();
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (m_buffer.GetWorldAt(index).NumBodies > 0)
            {
                UnityEngine.Debug.LogError("Not disposing CollisionWorld before assign a new one might cause memory leak");
            }
#endif
            m_buffer.SetWorldAt(index, collWorld.Clone());
        }

        public unsafe CollisionHistoryBufferRef AsCollisionHistoryBufferRef()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            //First check the CheckExistAndThrow to avoid bad access and return better error
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            //Then validate the write access right
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            UnsafeUtility.AsRef<RawHistoryBuffer>(m_bufferCopyPtr) = m_buffer;
            var bufferRef = new CollisionHistoryBufferRef
            {
                m_ptr = m_bufferCopyPtr,
                m_lastStoredTick = m_lastStoredTick,
                m_size = m_size,
            };
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            bufferRef.m_Safety = m_Safety;
            AtomicSafetyHandle.UseSecondaryVersion(ref bufferRef.m_Safety);
#endif
            return bufferRef;
        }

        public void Dispose()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Dispose(ref m_Safety, ref m_DisposeSentinel);
#endif
            unsafe
            {
                if (m_bufferCopyPtr != null)
                {
                    UnsafeUtility.Free(m_bufferCopyPtr, Allocator.Persistent);
                    m_bufferCopyPtr = null;
                }
                for (int i = 0; i < Capacity; ++i)
                {
                    m_buffer.GetWorldAt(i).Dispose();
                }
            }
        }
    }

    public struct CollisionHistoryBufferRef
    {
        [NativeDisableUnsafePtrRestriction]
        unsafe internal void *m_ptr;
        internal uint m_lastStoredTick;
        internal int m_size;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;
#endif
        public void GetCollisionWorldFromTick(uint tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            //The error will be misleading (is going to mention a NativeArray) but at least is more consistent
            //Rely only on CheckReadAndThrow give bad error messages
            AtomicSafetyHandle.CheckExistsAndThrow(this.m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(this.m_Safety);
#endif
            // Clamp to oldest physics copy when requesting older data than supported
            if (interpolationDelay > m_size-1)
                interpolationDelay = (uint)m_size-1;
            tick -= interpolationDelay;
            if (SequenceHelpers.IsNewer(tick, m_lastStoredTick))
                tick = m_lastStoredTick;
            var index = (int)(tick % m_size);

            unsafe
            {
                collWorld = UnsafeUtility.AsRef<RawHistoryBuffer>(m_ptr).GetWorldAt(index);
            }
        }
    }

    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    [AlwaysUpdateSystem]
    [AlwaysSynchronizeSystem]
    /// <summary>
    /// A system used to store old state of the physics world for lag compensation.
    /// You can get a CollisionHistoryBuffer from this system and from that you can
    /// get a physics collision world for a previous tick.
    /// When passing the collision history to a job you must set LastPhysicsJobHandle to
    /// the handle for that job.
    /// </summary>
    public class PhysicsWorldHistory : SystemBase
    {
        private bool m_initialized;
        private uint m_lastStoredTick;

        public CollisionHistoryBufferRef CollisionHistory => m_CollisionHistory.AsCollisionHistoryBufferRef();

        CollisionHistoryBuffer m_CollisionHistory;

        BuildPhysicsWorld m_BuildPhysicsWorld;
        EndFramePhysicsSystem m_EndFramePhysicsSystem;
        ServerSimulationSystemGroup m_ServerSimulationSystemGroup;
        ClientSimulationSystemGroup m_ClientSimulationSystemGroup;

        public JobHandle LastPhysicsJobHandle;
        public uint LastStoreTick => m_lastStoredTick;
        public bool IsInitialized => m_initialized;

        protected override void OnCreate()
        {
            m_ServerSimulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_BuildPhysicsWorld = World.GetExistingSystem<BuildPhysicsWorld>();
            m_EndFramePhysicsSystem = World.GetExistingSystem<EndFramePhysicsSystem>();
            m_CollisionHistory = new CollisionHistoryBuffer(m_ServerSimulationSystemGroup!=null ? CollisionHistoryBuffer.Capacity : 1);
        }
        protected override void OnDestroy()
        {
            m_CollisionHistory.Dispose();
        }

        protected override void OnUpdate()
        {
            if (HasSingleton<DisableLagCompensation>())
                return;

            var serverTick = (m_ServerSimulationSystemGroup != null) ? m_ServerSimulationSystemGroup.ServerTick : m_ClientSimulationSystemGroup.ServerTick;
            if (serverTick == 0)
                return;

            m_EndFramePhysicsSystem.GetOutputDependency().Complete();
            LastPhysicsJobHandle.Complete();

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
                if (!SequenceHelpers.IsNewer(serverTick, m_lastStoredTick))
                    return;

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
        }
    }
}
