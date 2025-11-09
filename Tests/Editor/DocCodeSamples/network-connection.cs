using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;

namespace DocumentationCodeSamples
{
    partial class network_connection
    {
        #region AutoConnectPort
        public class AutoConnectBootstrap : ClientServerBootstrap
        {
            public override bool Initialize(string defaultWorldName)
            {
                // This will enable auto connect.
                AutoConnectPort = 7979;
                // Create the default client and server worlds, depending on build type in a player or the PlayMode Tools in the editor
                CreateDefaultClientServerWorlds();
                return true;
            }
        }
        #endregion

        public partial struct NetworkStreamRequestExampleSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                var clientWorld = ClientServerBootstrap.ClientWorld;
                var serverWorld = ClientServerBootstrap.ServerWorld;
                var serverEndPoint = NetworkEndpoint.LoopbackIpv4;

                #region NetworkStreamRequest
                //On the client world, create a new entity with a NetworkStreamRequestConnect. It will be consumed by NetworkStreamReceiveSystem later.
                var connectRequest = clientWorld.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
                clientWorld.EntityManager.SetComponentData(connectRequest, new NetworkStreamRequestConnect { Endpoint = serverEndPoint });

                //On the server world, create a new entity with a NetworkStreamRequestConnect. It will be consumed by NetworkStreamReceiveSystem later.
                var listenRequest = serverWorld.EntityManager.CreateEntity(typeof(NetworkStreamRequestListen));
                serverWorld.EntityManager.SetComponentData(listenRequest, new NetworkStreamRequestListen { Endpoint = serverEndPoint });
                #endregion
            }
        }

        #region ClientEvents
        // Example System:
        [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
        [BurstCompile]
        public partial struct NetCodeConnectionEventListener : ISystem
        {
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                var connectionEventsForClient = SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;
                foreach (var evt in connectionEventsForClient)
                {
                    UnityEngine.Debug.Log($"[{state.WorldUnmanaged.Name}] {evt.ToFixedString()}!");
                }
            }
        }
        #endregion

        private void ConnectionApprovalExample()
        {
            var isServer = ClientServerBootstrap.ServerWorld != null;
            var server = ClientServerBootstrap.ServerWorld;
            var client = ClientServerBootstrap.ClientWorld;
            var ep = NetworkEndpoint.LoopbackIpv4;

            #region ConnectionApproval
            if (isServer)
            {
                using var drvQuery = server.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
                drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
                drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ep);
            }
            else
            {
                using var drvQuery = client.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
                drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
                drvQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(client.EntityManager, ep);
            }
            #endregion
        }

        #region ConnectionApprovalHandling
        // The approval RPC, here it contains a hypothetical payload the server will validate
        public struct ApprovalFlow : IApprovalRpcCommand
        {
            public FixedString512Bytes Payload;
        }

        // This is used to indicate we've already sent an approval RPC and don't need to do so again
        public struct ApprovalStarted : IComponentData
        {
        }

        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
        public partial struct ClientConnectionApprovalSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<RpcCollection>();
            }

            public void OnUpdate(ref SystemState state)
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                // Check connections which have not yet fully connected and send connection approval message
                foreach (var (connection, entity) in SystemAPI.Query<RefRW<NetworkStreamConnection>>().WithNone<NetworkId>().WithNone<ApprovalStarted>().WithEntityAccess())
                {
                    var sendApprovalMsg = ecb.CreateEntity();
                    ecb.AddComponent(sendApprovalMsg, new ApprovalFlow { Payload = "ABC" });
                    ecb.AddComponent<SendRpcCommandRequest>(sendApprovalMsg);

                    ecb.AddComponent<ApprovalStarted>(entity);
                }
                ecb.Playback(state.EntityManager);
            }
        }

        [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        public partial struct ServerConnectionApprovalSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                var ecb = new EntityCommandBuffer(Allocator.Temp);
                // Check connections which have not yet fully connected and send connection approval message
                foreach (var (receiveRpc, approvalMsg, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>,RefRW<ApprovalFlow>>().WithEntityAccess())
                {
                    var connectionEntity = receiveRpc.ValueRO.SourceConnection;
                    if (approvalMsg.ValueRO.Payload.Equals("ABC"))
                    {
                        ecb.AddComponent<ConnectionApproved>(connectionEntity);

                        // Destroy RPC message
                        ecb.DestroyEntity(entity);
                    }
                    else
                    {
                        // Failed approval messages should be disconnected
                        ecb.AddComponent<NetworkStreamRequestDisconnect>(connectionEntity);
                    }
                }
                ecb.Playback(state.EntityManager);
            }
        }
        #endregion
    }
}
