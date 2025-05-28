using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// The <see cref="EntityCommandBufferSystem"/> at the beginning of the <see cref="PredictedSimulationSystemGroup"/>.
    /// This command buffer can update mulitple time per frame on the client, based on the network condition and the frequency of server packet received by the client.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This system update may not be called on the client if there are no predicted ghost entities. Pending commands may be stale and processed
    /// later, when new predicted ghost entities are spawned. <br/>
    ///
    /// Pending commands are executed in the same frame they are added to the buffer only in these cases:<br/>
    /// - Commands are queued before the <see cref="PredictedSimulationSystemGroup"/> update.<br/>
    /// - Commands are queued by a system or job executed inside the <see cref="PredictedSimulationSystemGroup"/> and the group still has to run at least another tick, either partial or full. <br/>
    /// For the latter case, notice that for application that run at fixed tick rate (i.e the server or the client when v-synced) that is never the case and all commands
    /// are always delayed by one tick.
    /// </para>
    /// <para>
    /// In general, prefer using the <see cref="EndPredictedSimulationEntityCommandBufferSystem"/> to queue operation that are expected be executed
    /// by the end of the prediction group update or in general in the current frame. For example:
    /// <list type="bullet">
    /// <item>spawning entities (predicted spawning) on the client or the server</item>
    /// <item>removing / adding components</item>
    /// </list>
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class BeginPredictedSimulationEntityCommandBufferSystem : EntityCommandBufferSystem
    {
        /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton"/>
        public unsafe struct Singleton : IComponentData, IECBSingleton
        {
            internal UnsafeList<EntityCommandBuffer>* pendingBuffers;
            internal AllocatorManager.AllocatorHandle allocator;

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.CreateCommandBuffer"/>
            public EntityCommandBuffer CreateCommandBuffer(WorldUnmanaged world)
            {
                return EntityCommandBufferSystem.CreateCommandBuffer(ref *pendingBuffers, allocator, world);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetPendingBufferList"/>
            public void SetPendingBufferList(ref UnsafeList<EntityCommandBuffer> buffers)
            {
                pendingBuffers = (UnsafeList<EntityCommandBuffer>*)UnsafeUtility.AddressOf(ref buffers);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(Allocator allocatorIn)
            {
                allocator = allocatorIn;
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(AllocatorManager.AllocatorHandle allocatorIn)
            {
                allocator = allocatorIn;
            }
        }
        /// <inheritdoc cref="EntityCommandBufferSystem.OnCreate"/>
        protected override void OnCreate()
        {
            base.OnCreate();
            this.RegisterSingleton<Singleton>(ref PendingBuffers, World.Unmanaged);
        }
    }

    /// <summary>
    /// <para>
    /// The <see cref="EntityCommandBufferSystem"/> at the end of the <see cref="PredictedSimulationSystemGroup"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer using this system to queue spawning operations that involved predicted ghosts, especially on the Client (predicted spawning).
    /// This guarantee that they are initialized with the correct state at the tick they are spawned (no partial tick) if the usual
    /// rules for spawning (NetworkTime.IsFirstTimePredictedTick true) are followed.
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    public partial class EndPredictedSimulationEntityCommandBufferSystem : EntityCommandBufferSystem
    {
        /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton"/>
        public unsafe struct Singleton : IComponentData, IECBSingleton
        {
            internal UnsafeList<EntityCommandBuffer>* pendingBuffers;
            internal AllocatorManager.AllocatorHandle allocator;

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.CreateCommandBuffer"/>
            public EntityCommandBuffer CreateCommandBuffer(WorldUnmanaged world)
            {
                return EntityCommandBufferSystem.CreateCommandBuffer(ref *pendingBuffers, allocator, world);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetPendingBufferList"/>
            public void SetPendingBufferList(ref UnsafeList<EntityCommandBuffer> buffers)
            {
                pendingBuffers = (UnsafeList<EntityCommandBuffer>*)UnsafeUtility.AddressOf(ref buffers);
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(Allocator allocatorIn)
            {
                allocator = allocatorIn;
            }

            /// <inheritdoc cref="BeginInitializationEntityCommandBufferSystem.Singleton.SetAllocator"/>
            public void SetAllocator(AllocatorManager.AllocatorHandle allocatorIn)
            {
                allocator = allocatorIn;
            }
        }
        /// <inheritdoc cref="EntityCommandBufferSystem.OnCreate"/>
        protected override void OnCreate()
        {
            base.OnCreate();
            this.RegisterSingleton<Singleton>(ref PendingBuffers, World.Unmanaged);
        }
    }
}
