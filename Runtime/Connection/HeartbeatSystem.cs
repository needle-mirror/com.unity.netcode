using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// An RPC sent by both client and server to keep the connection alive when the client connection has not
    /// been set "in-game" (see <see cref="NetworkStreamInGame"/>).
    /// The client is always the initiator and send the heartbeat message every 10s. When the server receive the RPC,
    /// it reply the message back to client.
    /// </summary>
    internal struct HeartbeatComponent : IRpcCommand
    {
    }

    /// <summary>
    /// System that keeps the non in-game connections alive by sending the <see cref="HeartbeatComponent"/> rpc message
    /// at a constant interval (every 10 seconds).
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct HeartbeatSendSystem : ISystem
    {
        private uint m_LastSend;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<NetworkIdComponent>()
                .WithNone<NetworkStreamInGame>();
            state.RequireForUpdate(state.GetEntityQuery(builder));

            m_LastSend = NetworkTimeSystem.TimestampMS;
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        /// <summary>
        /// Send the keep-alive <see cref="HeartbeatComponent"/> RPC to the server,
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            uint now = NetworkTimeSystem.TimestampMS;
            // Send a heartbeat every 10 seconds, but only to connections which are not ingame since ingame connections already has a constant stream of data
            if (now - m_LastSend >= 10000)
            {
                var ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
                var request = ecb.CreateEntity();
                ecb.AddComponent<HeartbeatComponent>(request);
                ecb.AddComponent<SendRpcCommandRequestComponent>(request);
                m_LastSend = now;
            }
        }
    }

    /// <summary>
    /// System present on the server that receive the <see cref="HeartbeatComponent"/> rpc message and reply it back to the sender.
    /// </summary>
    [BurstCompile]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct HeartbeatReplySystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<HeartbeatComponent, ReceiveRpcCommandRequestComponent>();
            state.RequireForUpdate(state.GetEntityQuery(builder));
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        /// <summary>
        /// Receive and reply the <see cref="HeartbeatComponent"/> messages.
        /// </summary>
        /// <remarks>It reuse the entity with HeartbeatComponent for reply</remarks>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var replyJob = new HeartbeatReplyJob()
            {
                ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                    .CreateCommandBuffer(state.WorldUnmanaged)
            };
            state.Dependency = replyJob.Schedule(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(HeartbeatComponent))]
        partial struct HeartbeatReplyJob : IJobEntity
        {
            public EntityCommandBuffer ecb;
            public void Execute(Entity entity,
                ref ReceiveRpcCommandRequestComponent recv)
            {
                ecb.AddComponent(entity,
                    new SendRpcCommandRequestComponent {TargetConnection = recv.SourceConnection});
                ecb.RemoveComponent<ReceiveRpcCommandRequestComponent>(entity);
            }
        }
    }

    /// <summary>
    /// System present on the client that consume the received <see cref="HeartbeatComponent"/> messages.
    /// </summary>
    [BurstCompile]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    public partial struct HeartbeatReceiveSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        /// <summary>
        /// Receive and destroy the <see cref="HeartbeatComponent"/> messages.
        /// </summary>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var deleteJob = new HeartbeatDeleteJob()
            {
                ecb = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>()
                    .CreateCommandBuffer(state.WorldUnmanaged)
            };
            state.Dependency = deleteJob.Schedule(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(HeartbeatComponent))]
        [WithNone(typeof(SendRpcCommandRequestComponent))]
        partial struct HeartbeatDeleteJob : IJobEntity
        {
            public EntityCommandBuffer ecb;
            public void Execute(Entity entity)
            {
                ecb.DestroyEntity(entity);
            }
        }
    }
}
