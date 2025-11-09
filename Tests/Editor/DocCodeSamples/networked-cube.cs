using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace DocumentationCodeSamples
{
    partial class networked_cube
    {
        #region EstablishConnection
        // Create a custom bootstrap, which enables auto-connect.
        // The bootstrap can also be used to configure other settings as well as to
        // manually decide which worlds (client and server) to create based on user input
        [UnityEngine.Scripting.Preserve]
        public class GameBootstrap : ClientServerBootstrap
        {
            public override bool Initialize(string defaultWorldName)
            {
                AutoConnectPort = 7979; // Enabled auto connect
                return base.Initialize(defaultWorldName); // Use the regular bootstrap
            }
        }
        #endregion

        #region GoInGame
        /// <summary>
        /// This allows sending RPCs between a stand alone build and the editor for testing purposes in the event when you finish this example
        /// you want to connect a server-client stand alone build to a client configured editor instance.
        /// </summary>
        [BurstCompile]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
        [UpdateInGroup(typeof(InitializationSystemGroup))]
        [CreateAfter(typeof(RpcSystem))]
        public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
                state.Enabled = false;
            }
        }

        // RPC request from client to server for game to go "in game" and send snapshots / inputs
        public struct GoInGameRequest : IRpcCommand
        {
        }

        // When client has a connection with network id, go in game and tell server to also go in game
        [BurstCompile]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
        public partial struct GoInGameClientSystem : ISystem
        {
            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<NetworkId>()
                    .WithNone<NetworkStreamInGame>();
                state.RequireForUpdate(state.GetEntityQuery(builder));
            }

            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess().WithNone<NetworkStreamInGame>())
                {
                    commandBuffer.AddComponent<NetworkStreamInGame>(entity);
                    var req = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent<GoInGameRequest>(req);
                    commandBuffer.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
                }
                commandBuffer.Playback(state.EntityManager);
            }
        }

        // When server receives go in game request, go in game and delete request
        [BurstCompile]
        [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        public partial struct GoInGameServerSystem : ISystem
        {
            private ComponentLookup<NetworkId> networkIdFromEntity;

            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<GoInGameRequest>()
                    .WithAll<ReceiveRpcCommandRequest>();
                state.RequireForUpdate(state.GetEntityQuery(builder));
                networkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
            }

            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                var worldName = state.WorldUnmanaged.Name;

                var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                networkIdFromEntity.Update(ref state);

                foreach (var (reqSrc, reqEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>().WithAll<GoInGameRequest>().WithEntityAccess())
                {
                    commandBuffer.AddComponent<NetworkStreamInGame>(reqSrc.ValueRO.SourceConnection);
                    var networkId = networkIdFromEntity[reqSrc.ValueRO.SourceConnection];

                    Debug.Log($"'{worldName}' setting connection '{networkId.Value}' to in game");

                    commandBuffer.DestroyEntity(reqEntity);
                }
                commandBuffer.Playback(state.EntityManager);
            }
        }
        #endregion

        #region CubeAuthoring
        public struct Cube : IComponentData
        {
        }

        [DisallowMultipleComponent]
        public class CubeAuthoring : MonoBehaviour
        {
            class CubeBaker : Baker<CubeAuthoring>
            {
                public override void Bake(CubeAuthoring authoring)
                {
                    var entity = GetEntity(TransformUsageFlags.Dynamic);
                    AddComponent<Cube>(entity);
                }
            }
        }
        #endregion

        #region CubeSpawner
        public struct CubeSpawner : IComponentData
        {
            public Entity Cube;
        }

        [DisallowMultipleComponent]
        public class CubeSpawnerAuthoring : MonoBehaviour
        {
            public GameObject Cube;

            class Baker : Baker<CubeSpawnerAuthoring>
            {
                public override void Bake(CubeSpawnerAuthoring authoring)
                {
                    CubeSpawner component = default(CubeSpawner);
                    component.Cube = GetEntity(authoring.Cube, TransformUsageFlags.Dynamic);
                    var entity = GetEntity(TransformUsageFlags.Dynamic);
                    AddComponent(entity, component);
                }
            }
        }
        #endregion

        public partial struct RequireSpawnerExampleSystem : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                #region RequireSpawner
                state.RequireForUpdate<CubeSpawner>();
                #endregion
            }
        }

        [BurstCompile]
        public partial struct ModifiedGoInGameClientCreateExampleSystem : ISystem
        {
            #region ModifiedGoInGameClientCreate
            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                // Run only on entities with a CubeSpawner component data
                state.RequireForUpdate<CubeSpawner>();

                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<NetworkId>()
                    .WithNone<NetworkStreamInGame>();
                state.RequireForUpdate(state.GetEntityQuery(builder));
            }
            #endregion
        }

        [BurstCompile]
        public partial struct ModifiedGoInGameServerExampleSystem : ISystem
        {
            private ComponentLookup<NetworkId> networkIdFromEntity;

            #region ModifiedGoInGameServerCreate
            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<CubeSpawner>();

                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<GoInGameRequest>()
                    .WithAll<ReceiveRpcCommandRequest>();
                state.RequireForUpdate(state.GetEntityQuery(builder));
                networkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
            }
            #endregion

            #region ModifiedGoInGameServerUpdate
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                // Get the prefab to instantiate
                var prefab = SystemAPI.GetSingleton<CubeSpawner>().Cube;

                // Ge the name of the prefab being instantiated
                state.EntityManager.GetName(prefab, out var prefabName);
                var worldName = new FixedString32Bytes(state.WorldUnmanaged.Name);

                var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                networkIdFromEntity.Update(ref state);

                foreach (var (reqSrc, reqEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>().WithAll<GoInGameRequest>().WithEntityAccess())
                {
                    commandBuffer.AddComponent<NetworkStreamInGame>(reqSrc.ValueRO.SourceConnection);
                    // Get the NetworkId for the requesting client
                    var networkId = networkIdFromEntity[reqSrc.ValueRO.SourceConnection];

                    // Log information about the connection request that includes the client's assigned NetworkId and the name of the prefab spawned.
                    UnityEngine.Debug.Log($"'{worldName}' setting connection '{networkId.Value}' to in game, spawning a Ghost '{prefabName}' for them!");

                    // Instantiate the prefab
                    var player = commandBuffer.Instantiate(prefab);
                    // Associate the instantiated prefab with the connected client's assigned NetworkId
                    commandBuffer.SetComponent(player, new GhostOwner { NetworkId = networkId.Value});

                    // Add the player to the linked entity group so it is destroyed automatically on disconnect
                    commandBuffer.AppendToBuffer(reqSrc.ValueRO.SourceConnection, new LinkedEntityGroup{Value = player});
                    commandBuffer.DestroyEntity(reqEntity);
                }
                commandBuffer.Playback(state.EntityManager);
            }
            #endregion
        }

        public partial class Wrapper
        {
            #region ModifiedGoInGameAll
            /// <summary>
            /// This allows sending RPCs between a stand alone build and the editor for testing purposes in the event when you finish this example
            /// you want to connect a server-client stand alone build to a client configured editor instance.
            /// </summary>
            [BurstCompile]
            [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
            [UpdateInGroup(typeof(InitializationSystemGroup))]
            [CreateAfter(typeof(RpcSystem))]
            public partial struct SetRpcSystemDynamicAssemblyListSystem : ISystem
            {
                public void OnCreate(ref SystemState state)
                {
                    SystemAPI.GetSingletonRW<RpcCollection>().ValueRW.DynamicAssemblyList = true;
                    state.Enabled = false;
                }
            }

            // RPC request from client to server for game to go "in game" and send snapshots / inputs
            public struct GoInGameRequest : IRpcCommand
            {
            }

            // When client has a connection with network id, go in game and tell server to also go in game
            [BurstCompile]
            [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
            public partial struct GoInGameClientSystem : ISystem
            {
                [BurstCompile]
                public void OnCreate(ref SystemState state)
                {
                    // Run only on entities with a CubeSpawner component data
                    state.RequireForUpdate<CubeSpawner>();

                    var builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<NetworkId>()
                        .WithNone<NetworkStreamInGame>();
                    state.RequireForUpdate(state.GetEntityQuery(builder));
                }

                [BurstCompile]
                public void OnUpdate(ref SystemState state)
                {
                    var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                    foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess().WithNone<NetworkStreamInGame>())
                    {
                        commandBuffer.AddComponent<NetworkStreamInGame>(entity);
                        var req = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent<GoInGameRequest>(req);
                        commandBuffer.AddComponent(req, new SendRpcCommandRequest { TargetConnection = entity });
                    }
                    commandBuffer.Playback(state.EntityManager);
                }
            }

            // When server receives go in game request, go in game and delete request
            [BurstCompile]
            [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
            public partial struct GoInGameServerSystem : ISystem
            {
                private ComponentLookup<NetworkId> networkIdFromEntity;

                [BurstCompile]
                public void OnCreate(ref SystemState state)
                {
                    state.RequireForUpdate<CubeSpawner>();

                    var builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<GoInGameRequest>()
                        .WithAll<ReceiveRpcCommandRequest>();
                    state.RequireForUpdate(state.GetEntityQuery(builder));
                    networkIdFromEntity = state.GetComponentLookup<NetworkId>(true);
                }

                [BurstCompile]
                public void OnUpdate(ref SystemState state)
                {
                    // Get the prefab to instantiate
                    var prefab = SystemAPI.GetSingleton<CubeSpawner>().Cube;

                    // Ge the name of the prefab being instantiated
                    state.EntityManager.GetName(prefab, out var prefabName);
                    var worldName = new FixedString32Bytes(state.WorldUnmanaged.Name);

                    var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                    networkIdFromEntity.Update(ref state);

                    foreach (var (reqSrc, reqEntity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>().WithAll<GoInGameRequest>().WithEntityAccess())
                    {
                        commandBuffer.AddComponent<NetworkStreamInGame>(reqSrc.ValueRO.SourceConnection);
                        // Get the NetworkId for the requesting client
                        var networkId = networkIdFromEntity[reqSrc.ValueRO.SourceConnection];

                        // Log information about the connection request that includes the client's assigned NetworkId and the name of the prefab spawned.
                        UnityEngine.Debug.Log($"'{worldName}' setting connection '{networkId.Value}' to in game, spawning a Ghost '{prefabName}' for them!");

                        // Instantiate the prefab
                        var player = commandBuffer.Instantiate(prefab);
                        // Associate the instantiated prefab with the connected client's assigned NetworkId
                        commandBuffer.SetComponent(player, new GhostOwner { NetworkId = networkId.Value});

                        // Add the player to the linked entity group so it is destroyed automatically on disconnect
                        commandBuffer.AppendToBuffer(reqSrc.ValueRO.SourceConnection, new LinkedEntityGroup{Value = player});
                        commandBuffer.DestroyEntity(reqEntity);
                    }
                    commandBuffer.Playback(state.EntityManager);
                }
            }
            #endregion
        }

        #region MovingCube
        public struct CubeInput : IInputComponentData
        {
            public int Horizontal;
            public int Vertical;
        }

        [DisallowMultipleComponent]
        public class CubeInputAuthoring : MonoBehaviour
        {
            class CubeInputBaking : Unity.Entities.Baker<CubeInputAuthoring>
            {
                public override void Bake(CubeInputAuthoring authoring)
                {
                    var entity = GetEntity(TransformUsageFlags.Dynamic);
                    AddComponent<CubeInput>(entity);
                }
            }
        }

        [UpdateInGroup(typeof(GhostInputSystemGroup))]
        public partial struct SampleCubeInput : ISystem
        {
            public void OnCreate(ref SystemState state)
            {
                state.RequireForUpdate<NetworkStreamInGame>();
                state.RequireForUpdate<CubeSpawner>();
            }

            public void OnUpdate(ref SystemState state)
            {
                foreach (var playerInput in SystemAPI.Query<RefRW<CubeInput>>().WithAll<GhostOwnerIsLocal>())
                {
                    playerInput.ValueRW = default;
                    if (Input.GetKey("left"))
                        playerInput.ValueRW.Horizontal -= 1;
                    if (Input.GetKey("right"))
                        playerInput.ValueRW.Horizontal += 1;
                    if (Input.GetKey("down"))
                        playerInput.ValueRW.Vertical -= 1;
                    if (Input.GetKey("up"))
                        playerInput.ValueRW.Vertical += 1;
                }
            }
        }
        #endregion

        #region CubeMovementSystem
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        [BurstCompile]
        public partial struct CubeMovementSystem : ISystem
        {
            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                var speed = SystemAPI.Time.DeltaTime * 4;
                foreach (var (input, trans) in SystemAPI.Query<RefRO<CubeInput>, RefRW<LocalTransform>>().WithAll<Simulate>())
                {
                    var moveInput = new float2(input.ValueRO.Horizontal, input.ValueRO.Vertical);
                    moveInput = math.normalizesafe(moveInput) * speed;
                    trans.ValueRW.Position += new float3(moveInput.x, 0, moveInput.y);
                }
            }
        }
        #endregion
    }
}
