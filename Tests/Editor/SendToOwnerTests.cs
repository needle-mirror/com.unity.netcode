using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Tests
{
    class SendToOwnerTests
    {
        internal class TestComponentConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent<GhostOwner>(entity);
                baker.AddComponent<GhostPredictedOnly>(entity);
                baker.AddComponent<GhostInterpolatedOnly>(entity);
                baker.AddComponent<GhostGen_IntStruct>(entity);
                baker.AddComponent<GhostTypeIndex>(entity);
                baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                baker.AddBuffer<GhostGenTest_Buffer>(entity);
            }
        }

        void ChangeSendToOwnerOption(World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(typeof(GhostCollection));
            var entity = query.GetSingletonEntity();
            var collection = world.EntityManager.GetBuffer<GhostComponentSerializer.State>(entity);
            for (int i = 0; i < collection.Length; ++i)
            {
                var c = collection[i];
                if (c.ComponentType.GetManagedType() == typeof(GhostGen_IntStruct))
                {
                    c.SendToOwner = SendToOwnerType.SendToOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostTypeIndex))
                {
                    c.SendToOwner = SendToOwnerType.SendToNonOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostPredictedOnly))
                {
                    c.SendToOwner = SendToOwnerType.SendToOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostInterpolatedOnly))
                {
                    c.SendToOwner = SendToOwnerType.SendToNonOwner;
                    collection[i] = c;
                }
                else if (c.ComponentType.GetManagedType() == typeof(GhostGenTest_Buffer))
                {
                    c.SendToOwner = SendToOwnerType.SendToNonOwner;
                    collection[i] = c;
                }
            }
        }

        [Test]
        [TestCase(GhostModeMask.All, GhostMode.OwnerPredicted)]
        [TestCase(GhostModeMask.All, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.All, GhostMode.Predicted)]
        [TestCase(GhostModeMask.Interpolated, GhostMode.Interpolated)]
        [TestCase(GhostModeMask.Predicted, GhostMode.Predicted)]
        public void SendToOwner_Clients_ReceiveTheCorrectData(GhostModeMask modeMask, GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestComponentConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;
                //Some context about where owner make sense:
                //interpolated ghost: does even make sense that a ghost has an owner? Yes, it does and it is usually the server.
                //                    Can be a player ??? Yes it can. In that case, the player can still control the ghost via command but it will not predict the
                //                    ghost movement. Only the server will compute the correct position. The client will always see a delayed and interpolated replica.
                //Predicted ghost: owner make absolutely sense.
                //OwnerPredicted: by definition
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 2);
                //Here I do a trick: I will wait until the CollectionSystem is run and the component collection built.
                //Then I will change the serializer flags a little to make them behave the way I want.
                //This is a temporary hack, can be remove whe override per prefab will be available.
                using var queryServer = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient0 = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient1 = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                while (true)
                {
                    testWorld.Tick();
                    if (queryServer.IsEmptyIgnoreFilter || queryClient0.IsEmptyIgnoreFilter || queryClient1.IsEmptyIgnoreFilter)
                        continue;
                    if (testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0)
                        continue;
                    //intStruct -> to owner
                    //GhostTypeIndex -> to non-owner
                    //GhostPredictedOnly -> to owner
                    //GhostInterpolatedOnly -> to non owner
                    //GhostGenBuffer_ByteBuffer -> to non owner
                    ChangeSendToOwnerOption(testWorld.ServerWorld);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[0]);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[1]);
                    break;
                }

                testWorld.Connect();
                testWorld.GoInGame();
                var serverEntities = new NativeArray<Entity>(10, Allocator.Temp);

                for (int ent = 0; ent < 10; ++ent)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    serverEntities[ent] = serverEnt;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostGen_IntStruct {IntValue = 10000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostTypeIndex {Value = 20000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostPredictedOnly {Value = 30000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostInterpolatedOnly {Value = 40000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = ent/5 + 1});
                    var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                    serverBuffer1.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer1.Add(new GhostGenBuffer_ByteBuffer{Value = (byte)(10 + i)});
                    var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEnt);
                    serverBuffer2.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer2.Add(new GhostGenTest_Buffer());
                }

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                for (int i = 0; i < 2; ++i)
                {
                    var spawnMap = testWorld.GetSingletonRW<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                    for (int ent = 0; ent < 10; ++ent)
                    {
                        var serverEnt = serverEntities[ent];
                        var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                        var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEnt);
                        var serverComp1 = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGen_IntStruct>(serverEnt);
                        var serverComp2 = testWorld.ServerWorld.EntityManager.GetComponentData<GhostTypeIndex>(serverEnt);
                        var predictedOnly = testWorld.ServerWorld.EntityManager.GetComponentData<GhostPredictedOnly>(serverEnt);
                        var interpOnly = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInterpolatedOnly>(serverEnt);


                        var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt);
                        spawnMap.ValueRW.Value.TryGetValue(new SpawnedGhost(ghost.ghostId,ghost.spawnTick), out var clientEnt);
                        var clientComp1_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostGen_IntStruct>(clientEnt);
                        var clientComp2_NonOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostTypeIndex>(clientEnt);
                        var clientPredOnly_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostPredictedOnly>(clientEnt);
                        var clientInterpOnly_ToNonOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostInterpolatedOnly>(clientEnt);

                        var clientBuffer1 = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEnt);
                        var clientBuffer2_ToNonOwner = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenTest_Buffer>(clientEnt);

                        Assert.AreEqual(ent/5==i,serverComp1.IntValue == clientComp1_ToOwner.IntValue,$"Client {i}");
                        Assert.AreEqual(ent/5!=i,serverComp2.Value == clientComp2_NonOwner.Value,$"Client {i}");

                        //The component are sent to all the clients and only the SendToOwner matter
                        if (mode == GhostMode.Predicted)
                        {
                            Assert.AreEqual(ent/5==i,predictedOnly.Value == clientPredOnly_ToOwner.Value, $"Client {i}");
                            Assert.AreEqual(false,interpOnly.Value == clientInterpOnly_ToNonOwner.Value,  $"Client {i}");
                        }
                        else if (mode == GhostMode.Interpolated)
                        {
                            Assert.AreEqual(false,predictedOnly.Value == clientPredOnly_ToOwner.Value, $"Client {i}");
                            Assert.AreEqual(ent/5!=i,interpOnly.Value == clientInterpOnly_ToNonOwner.Value, $"Client {i}");
                        }
                        else if(mode == GhostMode.OwnerPredicted)
                        {
                            Assert.AreEqual(ent/5==i,predictedOnly.Value == clientPredOnly_ToOwner.Value,$"Client {i}");
                            Assert.AreEqual(ent/5!=i,interpOnly.Value == clientInterpOnly_ToNonOwner.Value,$"Client {i}");
                        }
                        Assert.AreEqual(true, 10 ==clientBuffer1.Length);
                        Assert.AreEqual(ent/5!=i,10 ==clientBuffer2_ToNonOwner.Length);
                        Assert.AreEqual(ent/5==i,0 ==clientBuffer2_ToNonOwner.Length);
                        for (int k = 0; k < clientBuffer1.Length; ++k)
                            Assert.AreEqual(serverBuffer1[k].Value, clientBuffer1[k].Value,$"Client {i}");
                        for (int k = 0; k < clientBuffer2_ToNonOwner.Length; ++k)
                            Assert.AreEqual(serverBuffer2[k].IntValue, clientBuffer2_ToNonOwner[k].IntValue,$"Client {i}");
                    }
                }
            }
        }

        [Test]
        [TestCase(GhostModeMask.All, GhostMode.OwnerPredicted)]
        [TestCase(GhostModeMask.All, GhostMode.Predicted)]
        [TestCase(GhostModeMask.Predicted, GhostMode.Predicted)]
        public void HistoryBackup_RespectSendToOwnerSemantic(GhostModeMask modeMask, GhostMode mode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,typeof(InputSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new TestComponentConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.SupportedGhostModes = modeMask;
                ghostConfig.DefaultGhostMode = mode;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 2);
                //Here I do a trick: I will wait until the CollectionSystem is run and the component collection built.
                //Then I will change the serializer flags a little to make them behave the way I want.
                //This is a temporary hack, can be remove whe override per prefab will be available.
                using var queryServer = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient0 = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                using var queryClient1 = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                while (true)
                {
                    testWorld.Tick();
                    if (queryServer.IsEmptyIgnoreFilter || queryClient0.IsEmptyIgnoreFilter || queryClient1.IsEmptyIgnoreFilter)
                        continue;
                    if (testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0 ||
                        testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(queryServer.GetSingletonEntity()).Length == 0)
                        continue;
                    //intStruct -> to owner
                    //GhostTypeIndex -> to non-owner
                    //GhostPredictedOnly -> to owner
                    //GhostInterpolatedOnly -> to non owner
                    //GhostGenBuffer_ByteBuffer -> to non owner
                    ChangeSendToOwnerOption(testWorld.ServerWorld);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[0]);
                    ChangeSendToOwnerOption(testWorld.ClientWorlds[1]);
                    break;
                }

                testWorld.Connect();
                testWorld.GoInGame();
                var serverEntities = new NativeArray<Entity>(10, Allocator.Temp);

                for (int ent = 0; ent < 10; ++ent)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    serverEntities[ent] = serverEnt;
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostGen_IntStruct {IntValue = 10000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostTypeIndex {Value = 20000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostPredictedOnly {Value = 30000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostInterpolatedOnly {Value = 40000});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = ent/5 + 1});
                    var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                    serverBuffer1.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer1.Add(new GhostGenBuffer_ByteBuffer{Value = (byte)(10 + i)});
                    var serverBuffer2 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenTest_Buffer>(serverEnt);
                    serverBuffer2.Capacity = 10;
                    for (int i = 0; i < 10; ++i)
                        serverBuffer2.Add(new GhostGenTest_Buffer());
                }

                //spawn the entities
                for(int i=0;i<8;++i)
                    testWorld.Tick();

                //Run a partial tick here to ensure the last predicted tick was partial and so the successive tick
                //will forcible try to restore from the backup.
                //Otherwise, the because we are modifying values outside the prediction loop, the ghost update system
                //will not try to restore from backup, if the last predicted tick was a full one
                //(assuming nothing changed component values)
                //That may be seen as incorrect and wrong (and indeed is confusing behavior)
                testWorld.Tick((1f/60)/2f);

                //verify we are sync and that the owner flag has been respected (so value for certain components are not
                //overwritten by server authority
                for (int tick = 0; tick < 4; ++tick)
                {
                    //overwrite the values for all the components for partial ticks and verify that:
                    // - replicated data are actually reset to the authoritative value if they match owner / non-owner
                    // - replicated data for owner are reset to the authoritative value
                    for (int i = 0; i < 2; ++i)
                    {
                        var spawnMap = testWorld.GetSingletonRW<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                        for (int ent = 0; ent < 10; ++ent)
                        {
                            var serverEnt = serverEntities[ent];
                            var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt);
                            spawnMap.ValueRW.Value.TryGetValue(new SpawnedGhost(ghost.ghostId, ghost.spawnTick),
                                out var clientEnt);
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(clientEnt, new GhostGen_IntStruct
                            {
                                IntValue = 1 + tick * 1000
                            });
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(clientEnt, new GhostTypeIndex
                            {
                                Value = 1 + tick * 1000
                            });
                            testWorld.ClientWorlds[i].EntityManager.SetComponentData(clientEnt, new GhostPredictedOnly
                            {
                                Value = 1 + tick * 1000
                            });
                        }
                    }
                    //Modify owner and not owner data for component that aren't synced and verify that for partial ticks they
                    //aren't rollback
                    testWorld.Tick((1f/60)/4f);
                    //What are the expectation in this case?
                    //We expect that:
                    //data that should be replicated only for onwers/non-owner, are not backup for the respective objects, thus they are unaffected by the partial tick restored
                    for (int i = 0; i < 2; ++i)
                    {
                        var spawnMap = testWorld.GetSingletonRW<SpawnedGhostEntityMap>(testWorld.ClientWorlds[i]);
                        //entities 0-5 owned by client 1
                        //entities 5-9 owned by client 2
                        for (int ent = i*5; ent < (i+1)*5; ++ent)
                        {
                            var serverEnt = serverEntities[ent];
                            var serverBuffer1 = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                            var serverComp1 = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGen_IntStruct>(serverEnt);
                            var predictedOnly = testWorld.ServerWorld.EntityManager.GetComponentData<GhostPredictedOnly>(serverEnt);

                            var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt);
                            spawnMap.ValueRW.Value.TryGetValue(new SpawnedGhost(ghost.ghostId,ghost.spawnTick), out var clientEnt);
                            var intStruct_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostGen_IntStruct>(clientEnt);
                            var typeIndex_NonOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostTypeIndex>(clientEnt);
                            var clientPredOnly_ToOwner = testWorld.ClientWorlds[i].EntityManager.GetComponentData<GhostPredictedOnly>(clientEnt);

                            var clientBuffer1 = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEnt);
                            var clientBuffer2_ToNonOwner = testWorld.ClientWorlds[i].EntityManager.GetBuffer<GhostGenTest_Buffer>(clientEnt);

                            Assert.AreEqual(serverComp1.IntValue, intStruct_ToOwner.IntValue,$"Client {i}");
                            Assert.AreEqual(predictedOnly.Value, clientPredOnly_ToOwner.Value,$"Client {i}");
                            Assert.AreEqual(1 + tick*1000, typeIndex_NonOwner.Value,$"Client {i}");
                            Assert.AreEqual(true, 10 ==clientBuffer1.Length);
                            for (int k = 0; k < clientBuffer1.Length; ++k)
                                Assert.AreEqual(serverBuffer1[k].Value, clientBuffer1[k].Value,$"Client {i}");
                            Assert.AreEqual(0, clientBuffer2_ToNonOwner.Length);
                        }
                    }
                }
            }
        }
    }
}
