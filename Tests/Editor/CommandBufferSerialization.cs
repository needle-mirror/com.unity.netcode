using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Transforms;

namespace Unity.NetCode.Tests
{
    [GhostComponent(PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.Predicted,
        OwnerSendType = SendToOwnerType.SendToNonOwner)]
    public struct TestInput : ICommandData
    {
        [GhostField] public uint Tick { get; set; }
        [GhostField]
        public int Value;
    }

    public class TestInputConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
            dstManager.AddComponent<GhostGen_IntStruct>(entity);
            dstManager.AddBuffer<TestInput>(entity);
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    public class PredictionSystem : SystemBase
    {
        private GhostPredictionSystemGroup predictionGroup;
        protected override void OnCreate()
        {
            base.OnCreate();
            predictionGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
            RequireSingletonForUpdate<NetworkStreamInGame>();
        }
        protected override void OnUpdate()
        {
            var tick = predictionGroup.PredictingTick;
            Entities
                .ForEach((Entity entity, ref Translation translation, in DynamicBuffer<TestInput> inputBuffer, in PredictedGhostComponent
                    prediciton) =>
                {
                    if (!GhostPredictionSystemGroup.ShouldPredict(tick, prediciton))
                        return;

                    if (!inputBuffer.GetDataAtTick(tick, out var input))
                        return;

                    translation.Value.y += 1.0f * input.Value;
                }).Run();
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    public class InputSystem : SystemBase
    {
        private ClientSimulationSystemGroup clientSim;
        protected override void OnCreate()
        {
            base.OnCreate();
            clientSim = World.GetExistingSystem<ClientSimulationSystemGroup>();
            RequireSingletonForUpdate<NetworkStreamInGame>();
            RequireSingletonForUpdate<GhostOwnerComponent>();
        }
        protected override void OnUpdate()
        {
            var connection = GetSingletonEntity<NetworkStreamInGame>();
            var commandTarget = EntityManager.GetComponentData<CommandTargetComponent>(connection);
            if (commandTarget.targetEntity == Entity.Null)
                return;
            var inputBuffer = EntityManager.GetBuffer<TestInput>(commandTarget.targetEntity);
            inputBuffer.AddCommandData(new TestInput
            {
                Tick = clientSim.ServerTick,
                Value = 1
            });
        }
    }
    public class CommandBufferTests
    {
        private const float deltaTime = 1.0f / 60.0f;

        [Test]
        [TestCase(GhostAuthoringComponent.GhostModeMask.All, GhostAuthoringComponent.GhostMode.OwnerPredicted)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.All, GhostAuthoringComponent.GhostMode.Interpolated)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.All, GhostAuthoringComponent.GhostMode.Predicted)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.Interpolated, GhostAuthoringComponent.GhostMode.Interpolated)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.Predicted, GhostAuthoringComponent.GhostMode.Predicted)]
        public void CommandDataBuffer_GhostOwner_WillNotReceiveTheBuffer(GhostAuthoringComponent.GhostModeMask modeMask,
            GhostAuthoringComponent.GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                Assert.IsTrue(testWorld.Connect(deltaTime, 64));
                testWorld.GoInGame();

                var serverEnt = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var clientEnt = WaitEntitySpawnedOnClientsAndAssignOwner(testWorld, 1, 0);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestInput>(clientEnt[0]);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                //Server as always 3 more inputs)
                Assert.GreaterOrEqual(serverBuffer.Length, clientBuffer.Length + 1);
                //Because of the redundancy the server always has more imputs
                for (int i = 0; i < serverBuffer.Length - clientBuffer.Length + 1; ++i)
                    Assert.AreEqual(0, serverBuffer[i].Value);
                for (int i = serverBuffer.Length - clientBuffer.Length + 1; i < serverBuffer.Length; ++i)
                    Assert.AreEqual(1, serverBuffer[i].Value);
                for (int i = 0; i < clientBuffer.Length; ++i)
                    Assert.AreEqual(1, clientBuffer[i].Value);
                //now rewrite the server buffer and confirm that is not changing on the client side
                serverBuffer.Length = 4;
                for (int i = 0; i < serverBuffer.Length; ++i)
                    serverBuffer[i] = new TestInput {Tick = serverBuffer[i].Tick, Value = 2};

                for (int i = 0; i < 10; ++i)
                    testWorld.Tick(deltaTime);

                clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestInput>(clientEnt[0]);
                Assert.Less(serverBuffer.Length, clientBuffer.Length);
                for (int i = 0; i < clientBuffer.Length; ++i)
                    Assert.AreEqual(1, clientBuffer[i].Value);

            }
        }

        [Test]
        [TestCase(GhostAuthoringComponent.GhostModeMask.All, GhostAuthoringComponent.GhostMode.Predicted)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.Predicted, GhostAuthoringComponent.GhostMode.Predicted)]
        public void CommandDataBuffer_NonOwner_WillReceiveTheBuffer(GhostAuthoringComponent.GhostModeMask modeMask,
            GhostAuthoringComponent.GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 2);
                Assert.IsTrue(testWorld.Connect(deltaTime, 64));
                testWorld.GoInGame();

                var serverEnt = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var clientEnt = WaitEntitySpawnedOnClientsAndAssignOwner(testWorld, 2, 0);

                //Run a series of full ticks and check that the buffers are replicated to the non owner
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var clientBuffer0 = testWorld.ClientWorlds[0].EntityManager.GetBuffer<TestInput>(clientEnt[0]);
                var clientBuffer1 = testWorld.ClientWorlds[1].EntityManager.GetBuffer<TestInput>(clientEnt[1]);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                Assert.AreEqual(serverBuffer.Length, clientBuffer1.Length);
                Assert.Greater(serverBuffer.Length, clientBuffer0.Length);
                for (int i = 4; i < serverBuffer.Length; ++i)
                    Assert.AreEqual(serverBuffer[i].Value, clientBuffer0[i-4].Value);
                var bufferCopy = new TestInput[serverBuffer.Length];
                serverBuffer.AsNativeArray().CopyTo(bufferCopy);
                //run some partials tick and check that the buffer is preserved correctly
                for (int i = 0; i < 3; ++i)
                {
                    testWorld.Tick((1.0f / 60.0f) / 4.0f);
                    clientBuffer1 = testWorld.ClientWorlds[1].EntityManager.GetBuffer<TestInput>(clientEnt[1]);
                    serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                    Assert.AreEqual(serverBuffer.Length, clientBuffer1.Length);
                    for (int k = 0; k < serverBuffer.Length; ++k)
                        Assert.AreEqual(bufferCopy[k].Value, clientBuffer1[k].Value);
                }
                //Do last partial tick and check the buffer are again in sync
                testWorld.Tick((1.0f / 60.0f) / 4.0f);
                Assert.AreEqual(serverBuffer.Length, clientBuffer1.Length);
                Assert.Greater(clientBuffer1.Length, bufferCopy.Length);
            }
        }


        [Test]
        [TestCase(GhostAuthoringComponent.GhostModeMask.All, GhostAuthoringComponent.GhostMode.OwnerPredicted)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.All, GhostAuthoringComponent.GhostMode.Interpolated)]
        [TestCase(GhostAuthoringComponent.GhostModeMask.Interpolated, GhostAuthoringComponent.GhostMode.Interpolated)]
        public void CommandDataBuffer_NonOwner_ShouldNotReceiveTheBuffer(GhostAuthoringComponent.GhostModeMask modeMask,
            GhostAuthoringComponent.GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                int numClients = 2;

                testWorld.CreateWorlds(true, numClients);
                Assert.IsTrue(testWorld.Connect(deltaTime, 64));
                testWorld.GoInGame();

                var serverEnt = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var clientEnt = WaitEntitySpawnedOnClientsAndAssignOwner(testWorld, numClients, 0);

                //Run a series of full ticks and check that the buffers are replicated to the non owner
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<TestInput>(serverEnt);
                for (int i = 0; i < numClients; ++i)
                {
                    var clientBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<TestInput>(clientEnt[i]);
                    if (i != 0)
                    {
                        Assert.AreNotEqual(serverBuffer.Length, clientBuffer.Length);
                        Assert.AreEqual(0, clientBuffer.Length);
                    }
                }
            }
        }

        //A extended version of the previous test, with an entity for each active client and one "spectator"
        [Test]
        public void CommandDataBuffer_OwnerPredicted_InterpolatedClientes_ShouldNotReceiveTheBuffer()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestInputConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = GhostAuthoringComponent.GhostModeMask.All;
                ghostConfig.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                int numClients = 3;

                testWorld.CreateWorlds(true, numClients);
                Assert.IsTrue(testWorld.Connect(deltaTime, 64));
                testWorld.GoInGame();

                var serverEnt1 = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 0);
                var serverEnt2 = SpawnEntityAndAssignOwnerOnServer(testWorld, ghostGameObject, 1);
                var clientEnt = new Entity[2];
                //Tick a little and wait all entities spawns
                for(int i=0;i<16;++i)
                    testWorld.Tick(deltaTime);
                //Assign the owner on the respective clients. Client3 is  passive (no entity)
                for(int i=0;i<2;++i)
                {
                    var query = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(GhostOwnerComponent));
                    var entities = query.ToEntityArray(Allocator.Temp);
                    var owners = query.ToComponentDataArray<GhostOwnerComponent>(Allocator.Temp);
                    var connQuery = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(
                        typeof(NetworkIdComponent));
                    var conn = connQuery.GetSingletonEntity();
                    var networkId = connQuery.GetSingleton<NetworkIdComponent>();
                    for(int e=0;e<entities.Length;++e)
                    {
                        if (owners[e].NetworkId == networkId.Value)
                        {
                            clientEnt[i] = entities[e];
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(conn, new CommandTargetComponent {targetEntity = entities[e]});
                        }
                    }
                }
                //Run a series of full ticks and check that the buffers are not replicated to the interpolated clients ghost
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(deltaTime);

                for (int i = 0; i < 3; ++i)
                {
                    var query = testWorld.ClientWorlds[i].EntityManager.CreateEntityQuery(typeof(GhostOwnerComponent));
                    var entities = query.ToEntityArray(Allocator.Temp);
                    for (int e = 0; e < entities.Length; ++e)
                    {
                        var clientBuffer = testWorld.ClientWorlds[i].EntityManager.GetBuffer<TestInput>(entities[e]);
                        if(i == 2 || entities[e] != clientEnt[i])
                        {
                            Assert.AreEqual(0, clientBuffer.Length, $"Client {i} entity {e}");
                        }
                        else
                        {
                            Assert.AreNotEqual(0, clientBuffer.Length, $"Client {i} entity {e}");
                        }

                    }
                }
            }
        }

        private static Entity[] WaitEntitySpawnedOnClientsAndAssignOwner(NetCodeTestWorld testWorld, int numClients, int owner)
        {
            bool entitiesAreNotSpawned;
            var clientEnt = new Entity[numClients];
            do
            {
                testWorld.Tick(deltaTime);
                entitiesAreNotSpawned = false;
                for (int i = 0; i < numClients; ++i)
                {
                    clientEnt[i] = testWorld.TryGetSingletonEntity<TestInput>(testWorld.ClientWorlds[i]);
                    entitiesAreNotSpawned |= clientEnt[i] == Entity.Null;
                }
            } while (entitiesAreNotSpawned);

            var clientConn = testWorld.TryGetSingletonEntity<NetworkStreamInGame>(testWorld.ClientWorlds[owner]);
            testWorld.ClientWorlds[owner].EntityManager.SetComponentData(clientConn, new CommandTargetComponent {targetEntity = clientEnt[owner]});
            return clientEnt;
        }

        private static Entity SpawnEntityAndAssignOwnerOnServer(NetCodeTestWorld testWorld, GameObject ghostGameObject, int clientOwner)
        {
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            var net1 = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ClientWorlds[clientOwner]);
            var netId1 = testWorld.ClientWorlds[clientOwner].EntityManager.GetComponentData<NetworkIdComponent>(net1);

            var entities = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkIdComponent>())
                .ToEntityArray(Allocator.Temp);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwnerComponent {NetworkId = netId1.Value});
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostGen_IntStruct {IntValue = 1000});
            for (int i = 0; i < entities.Length; ++i)
            {
                var netId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(entities[i]);
                if (netId.Value == netId1.Value)
                    testWorld.ServerWorld.EntityManager.SetComponentData(entities[i], new CommandTargetComponent {targetEntity = serverEnt});
            }

            return serverEnt;
        }
    }
}
