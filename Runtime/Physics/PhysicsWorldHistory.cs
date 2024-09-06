using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Mathematics;

[assembly: InternalsVisibleTo("Unity.NetCode.Physics.EditorTests")]
namespace Unity.NetCode
{

    /// <summary>
    /// A singleton component from which you can get a physics collision world for a previous tick.
    /// </summary>
    public partial struct PhysicsWorldHistorySingleton : IComponentData
    {
        /// <summary>
        /// Get the <see cref="CollisionWorld"/> state for the given tick and interpolation delay.
        /// </summary>
        /// <param name="tick">The server tick we are simulating.</param>
        /// <param name="interpolationDelay">The client interpolation delay, measured in ticks. This is used to look back in time
        ///     and retrieve the state of the collision world at tick - interpolationDelay.
        ///     The interpolation delay is internally clamped to the current collision history size (the number of saved history state).</param>
        /// <param name="physicsWorld">The physics world which is use to get collision worlds for ticks which are not yet in the history buffer.</param>
        /// <param name="collWorld">The <see cref="CollisionWorld"/> state retrieved from the history.</param>
        /// <param name="expectedTick">The tick we should be fetching, after subtracting interpolationDelay.</param>
        /// <param name="returnedTick">The tick index we actually fetched, due to clamping.
        /// I.e. If clamped to the oldest stored tick, you'll see the eldest stored tick returned here.
        /// <br/>Compare this to the expectedTick to determine that a players interpolationDelay is so high that they're hitting the clamp.</param>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, ref PhysicsWorld physicsWorld, out CollisionWorld collWorld, out NetworkTick expectedTick, out NetworkTick returnedTick)
        {
            expectedTick = tick;
            expectedTick.Subtract(interpolationDelay);
            if (!LatestStoredTick.IsValid || expectedTick.IsNewerThan(LatestStoredTick))
            {
                collWorld = physicsWorld.CollisionWorld;
                returnedTick = tick;
                return;
            }
            m_History.GetCollisionWorldFromTick(tick, interpolationDelay, out collWorld, out expectedTick, out returnedTick);
        }

        /// <inheritdoc cref="GetCollisionWorldFromTick(Unity.NetCode.NetworkTick,uint,ref Unity.Physics.PhysicsWorld,out Unity.Physics.CollisionWorld,out Unity.NetCode.NetworkTick,out Unity.NetCode.NetworkTick)"/>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, ref PhysicsWorld physicsWorld, out CollisionWorld collWorld)
        {
            GetCollisionWorldFromTick(tick, interpolationDelay, ref physicsWorld, out collWorld, out _, out _);
        }

        /// <summary>Returns the latest (i.e. newest) tick stored into the <see cref="CollisionHistoryBuffer"/>.</summary>
        public NetworkTick LatestStoredTick => m_History.m_LatestStoredTick;
        internal CollisionHistoryBufferRef m_History;

        /// <summary>
        /// An optional collection specifying a manual whitelist of <see cref="CollisionWorld.Bodies"/> indexes that you want
        /// to opt-into deep copying the collider blob assets of (each specified by its <see cref="CollisionWorld.GetRigidBodyIndex"/> index).
        /// <br/>Must not contain duplicate entries, and must not contain indices of rigid bodies whose colliders will
        /// already be deep copied due to the values chosen for the <see cref="LagCompensationConfig.DeepCopyDynamicColliders"/> and
        /// <see cref="LagCompensationConfig.DeepCopyStaticColliders"/> parameters.
        /// </summary>
        /// <remarks>
        /// If you know exactly which set of ghosts you need lag compensation for, it may be simpler
        /// to pass their indexes here. Use <see cref="CollisionWorld.GetRigidBodyIndex"/> to map an entity to a rigidbody.
        /// </remarks>
        [NativeDisableContainerSafetyRestriction]
        public NativeList<int> DeepCopyRigidBodyCollidersWhitelist;

        /// <summary>
        /// Helper to retrieve debug data from the history buffer.
        /// </summary>
        /// <param name="physicsWorld"></param>
        /// <returns></returns>
        public unsafe string GetHistoryBufferData(ref PhysicsWorld physicsWorld)
        {
            string info = $"[PhysicsWorldHistorySingleton] Size:{m_History.m_Size} History.LastStoredTick:{LatestStoredTick.ToFixedString()}";
            if (!LatestStoredTick.IsValid) return info;

            for (uint interpolDelay = 0; interpolDelay < m_History.m_Size; interpolDelay++)
            {
                GetCollisionWorldFromTick(LatestStoredTick, interpolDelay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);
                info += $"\n[tick:{LatestStoredTick.ToFixedString()}^{interpolDelay}]={returnedTick.ToFixedString()} (expected:{expectedTick.ToFixedString()}) idx:{(returnedTick.IsValid ? returnedTick.TickIndexForValidTick%m_History.m_Size : -1)}";
                info += $"  Bodies:{collWorld.Bodies.Length} (dynamic:{collWorld.DynamicBodies.Length} static:{collWorld.StaticBodies.Length})";
                if (expectedTick.IsNewerThan(LatestStoredTick)) info += "  RETURNING_LIVE_COLWORLD! ";
                if (returnedTick.IsValid && LatestStoredTick.TicksSince(returnedTick) >= m_History.m_Size) info += "  OUT_OF_BOUNDS! ";
                if (!returnedTick.IsValid || expectedTick != returnedTick) info += "  RETURN_DIFF! ";

                for (var i = 0; i < collWorld.Bodies.Length; i++)
                {
                    info += $"\n\t[{i}] ";
                    GetColliderInfo(collWorld.Bodies[i], ref info);
                }
            }

            info += "\n\t--";
            return info;

            static void GetColliderInfo(RigidBody rigidBody, ref string info)
            {
                var coll = rigidBody.Collider;
                info += $"{rigidBody.Entity} Position:{rigidBody.WorldFromBody.pos} Scale:{rigidBody.Scale} CustomTags:{rigidBody.CustomTags} {(coll.IsCreated ? $"Collider:{coll.Value.Type}" : "Collider:null")}";
            }
        }
    }

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

        public NetworkTick world01Tick;
        public NetworkTick world02Tick;
        public NetworkTick world03Tick;
        public NetworkTick world04Tick;
        public NetworkTick world05Tick;
        public NetworkTick world06Tick;
        public NetworkTick world07Tick;
        public NetworkTick world08Tick;
        public NetworkTick world09Tick;
        public NetworkTick world10Tick;
        public NetworkTick world11Tick;
        public NetworkTick world12Tick;
        public NetworkTick world13Tick;
        public NetworkTick world14Tick;
        public NetworkTick world15Tick;
        public NetworkTick world16Tick;
    }
    internal static class RawHistoryBufferExtension
    {
        public static ref CollisionWorld GetWorldAt(ref this RawHistoryBuffer buffer, int index, int size, out NetworkTick tick)
        {
            UnityEngine.Debug.Assert(index < size);
            switch (index)
            {
                case 0: tick = buffer.world01Tick; break;
                case 1: tick = buffer.world02Tick; break;
                case 2: tick = buffer.world03Tick; break;
                case 3: tick = buffer.world04Tick; break;
                case 4: tick = buffer.world05Tick; break;
                case 5: tick = buffer.world06Tick; break;
                case 6: tick = buffer.world07Tick; break;
                case 7: tick = buffer.world08Tick; break;
                case 8: tick = buffer.world09Tick; break;
                case 9: tick = buffer.world10Tick; break;
                case 10: tick = buffer.world11Tick; break;
                case 11: tick = buffer.world12Tick; break;
                case 12: tick = buffer.world13Tick; break;
                case 13: tick = buffer.world14Tick; break;
                case 14: tick = buffer.world15Tick; break;
                case 15: tick = buffer.world16Tick; break;
                default:
                    throw new IndexOutOfRangeException();
            }
            if(tick.IsValid) UnityEngine.Debug.Assert(tick.TickIndexForValidTick % size == index, $"{tick.ToFixedString()} % {size} == {index}");
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

        public static void SetWorldAt(this ref RawHistoryBuffer buffer, int index, NetworkTick tick, int size, in CollisionWorld world)
        {
            UnityEngine.Debug.Assert(index < size);
            if(tick.IsValid) UnityEngine.Debug.Assert(tick.TickIndexForValidTick % size == index);
            switch (index)
            {
                case 0: buffer.world01 = world; buffer.world01Tick = tick; break;
                case 1: buffer.world02 = world; buffer.world02Tick = tick; break;
                case 2: buffer.world03 = world; buffer.world03Tick = tick; break;
                case 3: buffer.world04 = world; buffer.world04Tick = tick; break;
                case 4: buffer.world05 = world; buffer.world05Tick = tick; break;
                case 5: buffer.world06 = world; buffer.world06Tick = tick; break;
                case 6: buffer.world07 = world; buffer.world07Tick = tick; break;
                case 7: buffer.world08 = world; buffer.world08Tick = tick; break;
                case 8: buffer.world09 = world; buffer.world09Tick = tick; break;
                case 9: buffer.world10 = world; buffer.world10Tick = tick; break;
                case 10:buffer.world11 = world; buffer.world11Tick = tick; break;
                case 11:buffer.world12 = world; buffer.world12Tick = tick; break;
                case 12:buffer.world13 = world; buffer.world13Tick = tick; break;
                case 13:buffer.world14 = world; buffer.world14Tick = tick; break;
                case 14:buffer.world15 = world; buffer.world15Tick = tick; break;
                case 15:buffer.world16 = world; buffer.world16Tick = tick; break;
                default:
                    throw new IndexOutOfRangeException();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CollisionHistoryBuffer : IDisposable
    {
        public const int Capacity = RawHistoryBuffer.Capacity;
        public int Size { get; }
        public unsafe bool IsCreated => m_bufferCopyPtr != null;
        public NetworkTick LatestStoredTick { get; private set; }

        private RawHistoryBuffer m_buffer;
        [NativeDisableUnsafePtrRestriction]
        private unsafe void* m_bufferCopyPtr;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        //For job checks
        private AtomicSafetyHandle m_Safety;
        //To avoid accessing the buffer if already disposed
        private static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<CollisionHistoryBuffer>();
#endif

        public CollisionHistoryBuffer(int size)
        {
            if (size > Capacity)
                throw new ArgumentOutOfRangeException($"Invalid size {size}. Must be <= {Capacity}");
            if (size > 0 && !math.ispow2(size))
                throw new ArgumentOutOfRangeException($"Invalid size {size}. Must be 0, 1, or a power of 2! Recommended value:{math.ceilpow2(size)}!");
            Size = size;
            LatestStoredTick = NetworkTick.Invalid;
            var defaultWorld = default(CollisionWorld);
            m_buffer = new RawHistoryBuffer();
            for(int i=0;i<Size;++i)
            {
                m_buffer.SetWorldAt(i, NetworkTick.Invalid, size, defaultWorld);
            }

            unsafe
            {
                m_bufferCopyPtr = UnsafeUtility.Malloc(UnsafeUtility.SizeOf<RawHistoryBuffer>(), 8, Allocator.Persistent);
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = AtomicSafetyHandle.Create();
            CollectionHelper.SetStaticSafetyId<CollisionHistoryBuffer>(ref m_Safety, ref s_staticSafetyId.Data);
#endif
        }

        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            // Clamp to the oldest physics copy when requesting older data than supported
            if (interpolationDelay > Size-1)
                interpolationDelay = (uint)Size-1;
            tick.Subtract(interpolationDelay);
            if (LatestStoredTick.IsValid && tick.IsNewerThan(LatestStoredTick))
                tick = LatestStoredTick;
            var index = (int)(tick.TickIndexForValidTick % Size);
            GetCollisionWorldFromIndex(index, out collWorld);
        }

        public void DisposeIndex(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            m_buffer.GetWorldAt(index, Size, out _).Dispose();
        }

        void GetCollisionWorldFromIndex(int index, out CollisionWorld collWorld)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            collWorld = m_buffer.GetWorldAt(index, Size, out _);
        }

        [Obsolete("Prefer the more explicit CloneCollisionWorld (where args are passed by ref, and PhysicsWorldHistorySingleton is injected).")]
        public void CloneCollisionWorld(int index, in CollisionWorld collWorld, in LagCompensationConfig config = default, NetworkTick tick = default)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            if (index >= Size || index >= Capacity)
                throw new IndexOutOfRangeException($"Index {index} >= Size:{Size} or Capacity:{Capacity}!");

            //Always dispose the current world
            m_buffer.GetWorldAt(index, Size, out _).Dispose();
            m_buffer.SetWorldAt(index, tick, Size, collWorld.Clone(config.DeepCopyDynamicColliders, config.DeepCopyStaticColliders));
            if(tick.IsValid && (!LatestStoredTick.IsValid || tick.IsNewerThan(LatestStoredTick)))
                LatestStoredTick = tick;
        }

        public void CloneCollisionWorld(int index, ref CollisionWorld collWorld, ref LagCompensationConfig config, ref PhysicsWorldHistorySingleton pwhs, NetworkTick tick)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            if (index >= Size || index >= Capacity)
                throw new IndexOutOfRangeException($"Index {index} >= Size:{Size} or Capacity:{Capacity}!");

            //Always dispose the current world
            m_buffer.GetWorldAt(index, Size, out _).Dispose();
            m_buffer.SetWorldAt(index, tick, Size, collWorld.Clone(config.DeepCopyDynamicColliders, config.DeepCopyStaticColliders, pwhs.DeepCopyRigidBodyCollidersWhitelist));
            if(tick.IsValid && (!LatestStoredTick.IsValid || tick.IsNewerThan(LatestStoredTick)))
                LatestStoredTick = tick;
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
                m_Ptr = m_bufferCopyPtr,
                m_LatestStoredTick = LatestStoredTick,
                m_Size = Size,
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
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
#endif
            unsafe
            {
                if (m_bufferCopyPtr != null)
                {
                    UnsafeUtility.Free(m_bufferCopyPtr, Allocator.Persistent);
                    m_bufferCopyPtr = null;
                }
                for (int i = 0; i < Size; ++i)
                {
                    m_buffer.GetWorldAt(i, Size, out _).Dispose();
                }
            }
        }
    }

    /// <summary>
    /// A safe reference to the <see cref="CollisionHistoryBuffer"/>.
    /// Avoid copying the large world history data structure when accessing the buffer, and because of that
    /// can easily passed around in function, jobs or used on the main thread without consuming to much stack space.
    /// </summary>
    internal struct CollisionHistoryBufferRef
    {
        [NativeDisableUnsafePtrRestriction]
        unsafe internal void *m_Ptr;
        internal NetworkTick m_LatestStoredTick;
        internal int m_Size;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;
#endif
        /// <summary>
        /// Get the <see cref="CollisionWorld"/> state for the given tick and interpolation delay.
        /// </summary>
        /// <param name="tick">The server tick we are simulating</param>
        /// <param name="interpolationDelay">The client interpolation delay, measured in ticks. This is used to look back in time
        ///     and retrieve the state of the collision world at tick - interpolationDelay.
        ///     The interpolation delay is internally clamped to the current collision history size (the number of saved history state)</param>
        /// <param name="collWorld">The <see cref="CollisionWorld"/> state retrieved from the history</param>
        /// <param name="expectedTick">The tick we should be fetching, after subtracting interpolationDelay.</param>
        /// <param name="returnedTick">The tick index we actually fetched, due to clamping. I.e. If clamped to the oldest stored tick, you'll see the eldest stored tick returned here.</param>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, out CollisionWorld collWorld, out NetworkTick expectedTick, out NetworkTick returnedTick)
        {
            int ringBufferIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            //The error will be misleading (is going to mention a NativeArray) but at least is more consistent
            //Rely only on CheckReadAndThrow give bad error messages
            AtomicSafetyHandle.CheckExistsAndThrow(this.m_Safety);
            AtomicSafetyHandle.CheckReadAndThrow(this.m_Safety);
#endif
            tick.Subtract(interpolationDelay);
            expectedTick = tick;

            // Clamp to the oldest physics copy when requesting older data than supported
            if (m_LatestStoredTick.IsValid)
            {
                if (tick.IsNewerThan(m_LatestStoredTick))
                {
                    tick = m_LatestStoredTick;
                }
                else if (m_LatestStoredTick.TicksSince(tick) >= m_Size)
                {
                    tick = m_LatestStoredTick;
                    tick.Subtract((uint) (m_Size-1));
                }
            }

            // WARNING: This operation requires m_Size to be Pow2, otherwise we get invalid indexes
            // when the TickIndexForValidTick wraps around uint.MaxValue.
            UnityEngine.Debug.Assert(math.ispow2(m_Size));
            ringBufferIndex = (int)(tick.TickIndexForValidTick % m_Size);
            unsafe
            {
                collWorld = UnsafeUtility.AsRef<RawHistoryBuffer>(m_Ptr).GetWorldAt(ringBufferIndex, m_Size, out returnedTick);
            }
        }

        /// <inheritdoc cref="GetCollisionWorldFromTick(Unity.NetCode.NetworkTick,uint,out Unity.Physics.CollisionWorld,out Unity.NetCode.NetworkTick)"/>
        public void GetCollisionWorldFromTick(NetworkTick tick, uint interpolationDelay, out CollisionWorld collWorld)
        {
            GetCollisionWorldFromTick(tick, interpolationDelay, out collWorld, out _, out _);
        }
    }

    /// <summary>
    /// A system used to store old state of the physics world for lag compensation.
    /// This system creates a PhysicsWorldHistorySingleton and from that you can
    /// get a physics collision world for a previous tick.
    /// </summary>
    /// <remarks>
    /// This clone of the PhysicsWorld occurs shortly after building the physics world,
    /// to ensure that collider BlobAssetReferences are valid and correctly copyable.
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PhysicsSystemGroup), OrderLast = true)]
    [BurstCompile]
    public partial struct PhysicsWorldHistory : ISystem
    {
        /// <summary>
        /// Max quantity of CollisionWorld's that can be stored in the RawHistoryBuffer.
        /// </summary>
        /// <remarks>
        /// Lag compensation queries that attempt to reach further back than the capacity will be clamped to the oldest.
        /// </remarks>
        public const int RawHistoryBufferMaxCapacity = RawHistoryBuffer.Capacity;

        CollisionHistoryBuffer m_CollisionHistory;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<LagCompensationConfig>();
            state.RequireForUpdate<NetworkId>();
            state.EntityManager.CreateEntity(ComponentType.ReadWrite<PhysicsWorldHistorySingleton>());
            SystemAPI.SetSingleton(new PhysicsWorldHistorySingleton
            {
                DeepCopyRigidBodyCollidersWhitelist = new NativeList<int>(0, Allocator.Persistent),
            });
        }
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (m_CollisionHistory.IsCreated)
                m_CollisionHistory.Dispose();

            if (SystemAPI.TryGetSingleton(out PhysicsWorldHistorySingleton pwhs) && pwhs.DeepCopyRigidBodyCollidersWhitelist.IsCreated)
                pwhs.DeepCopyRigidBodyCollidersWhitelist.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var serverTick = networkTime.ServerTick;
            if (!serverTick.IsValid || !networkTime.IsFirstTimeFullyPredictingTick)
                return;

            var config = SystemAPI.GetSingleton<LagCompensationConfig>();
            if (!m_CollisionHistory.IsCreated)
            {
                int historySize;
                if (state.WorldUnmanaged.IsServer())
                    historySize = config.ServerHistorySize!=0 ? config.ServerHistorySize : RawHistoryBuffer.Capacity;
                else
                    historySize = config.ClientHistorySize;
                if (historySize == 0)
                    return;
                if (historySize < 0 || historySize > RawHistoryBuffer.Capacity)
                {
                    SystemAPI.GetSingleton<NetDebug>().LogWarning($"Invalid LagCompensationConfig, history size ({historySize}) must be > 0 <= {RawHistoryBuffer.Capacity}. Clamping hte value to the valid range.");
                    historySize = math.clamp(historySize, 1, RawHistoryBuffer.Capacity);
                }

                m_CollisionHistory = new CollisionHistoryBuffer(historySize);
            }

            state.CompleteDependency();

            //We need to grab the physics world from a different source based on the physics configuration present or not
            var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
            ref var physicsWorldHistorySingleton = ref SystemAPI.GetSingletonRW<PhysicsWorldHistorySingleton>().ValueRW;

            // Copy all ticks before this one to the buffer using the most recent physics world - which will be what that simulation used
            if (!m_CollisionHistory.LatestStoredTick.IsValid)
            {
                var storeTick = serverTick;
                for (int i = 0; i < m_CollisionHistory.Size; i++)
                {
                    var index = (int)(storeTick.TickIndexForValidTick % m_CollisionHistory.Size);
                    m_CollisionHistory.CloneCollisionWorld(index, ref physicsWorld.CollisionWorld, ref config, ref physicsWorldHistorySingleton, storeTick);
                    storeTick.Decrement();
                }
            }
            else
            {
                // Store a CollisionWorld for each tick that has not been stored yet.
                var ticksToStore = serverTick.TicksSince(m_CollisionHistory.LatestStoredTick);
                if (ticksToStore <= 0) return;

                // Copying more than m_CollisionHistory.Size would mean we overwrite a tick we copied this frame,
                // so prevent that:
                var startStoreTick = serverTick;
                startStoreTick.Subtract((uint) math.min(ticksToStore - 1, m_CollisionHistory.Size));

                // Store:
                for (var storeTick = startStoreTick; !storeTick.IsNewerThan(serverTick); storeTick.Increment())
                {
                    var index = (int)(storeTick.TickIndexForValidTick % m_CollisionHistory.Size);
                    m_CollisionHistory.CloneCollisionWorld(index, ref physicsWorld.CollisionWorld, ref config, ref physicsWorldHistorySingleton, storeTick);
                }

                // Note: If using multiple physics sub-steps, we only store the result of the first one in each ServerTick.
            }
            physicsWorldHistorySingleton.m_History = m_CollisionHistory.AsCollisionHistoryBufferRef();
        }
    }
}
