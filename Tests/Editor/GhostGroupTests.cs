#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class GhostGroupGhostConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            // Dependency on the name
            baker.DependsOn(gameObject);
            if (gameObject.name == "ParentGhost")
            {
                baker.AddBuffer<GhostGroup>(entity);
                baker.AddComponent(entity, default(GhostGroupRoot));
            }
            else
                baker.AddComponent(entity, default(GhostChildEntity));
        }
    }
    internal class LargeDataSizeGroupGhostConverter : TestNetCodeAuthoring.IConverter
    {
        //we need different archetype for the children
        public void Bake(GameObject gameObject, IBaker baker)
        {

            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            // Dependency on the name
            baker.DependsOn(gameObject);
            if (gameObject.name == "ParentGhost")
            {
                baker.AddBuffer<GhostGroup>(entity);
                baker.AddComponent(entity, default(GhostGroupRoot));
                var buffer = baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                buffer.Length = 300;
                for (int i = 0; i < buffer.Length; ++i)
                    buffer[i] = new GhostGenBuffer_ByteBuffer { Value = (byte)i };
            }
            else
            {
                var sub = gameObject.name.Substring(5, gameObject.name.Length - 5);
                int index = int.Parse(sub);
                baker.AddComponent(entity, default(GhostChildEntity));
                if ((index == 0))
                {
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_0));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_1));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_2));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_3));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_4));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_5));
                }
                else if ((index == 1))
                {
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_0));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_1));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_2));
                }
                else
                {
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_0));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_1));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_2));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_3));
                    baker.AddComponent(entity, default(Unity.NetCode.Tests.EnableableComponent_4));
                }
                var buffer = baker.AddBuffer<GhostGenBuffer_ByteBuffer>(entity);
                buffer.Length = 200;
                for (int i = 0; i < buffer.Length; ++i)
                    buffer[i] = new GhostGenBuffer_ByteBuffer { Value = (byte)i };
            }
        }
    }
    internal struct GhostGroupRoot : IComponentData
    {}
    internal class GhostGroupTests
    {
        [Test]
        public void EntityMarkedAsChildIsNotSent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]);
                var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
                Assert.AreEqual(Entity.Null, clientChildEnt);
            }
        }
        [Test]
        public void EntityMarkedAsChildIsSentAsPartOfGroup()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});
                testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]);
                var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);
                Assert.AreNotEqual(Entity.Null, clientChildEnt);
                Assert.AreEqual(43, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientChildEnt).NetworkId);
            }
        }
        [Test]
        public void CanHaveManyGhostGroupGhostTypes()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObjects = new GameObject[64];

                for (int i = 0; i < 32; ++i)
                {
                    var ghostGameObject = new GameObject();
                    ghostGameObject.name = "ParentGhost";
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                    var childGhostGameObject = new GameObject();
                    childGhostGameObject.name = "ChildGhost";
                    childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                    ghostGameObjects[i] = ghostGameObject;
                    ghostGameObjects[i+32] = childGhostGameObject;
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjects));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < 32; ++i)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObjects[i]);
                    var serverChildEnt = testWorld.SpawnOnServer(ghostGameObjects[i+32]);

                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                }

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Check that the client world has the right thing and value
                var ghostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                Assert.AreEqual(64, ghostQuery.CalculateEntityCount());
                Assert.AreEqual(32, groupQuery.CalculateEntityCount());
            }
        }
        [Test]
        public void CanHaveManyGhostGroupsOfSameType()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObjects = new GameObject[2];

                for (int i = 0; i < 1; ++i)
                {
                    var ghostGameObject = new GameObject();
                    ghostGameObject.name = "ParentGhost";
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                    var childGhostGameObject = new GameObject();
                    childGhostGameObject.name = "ChildGhost";
                    childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                    ghostGameObjects[i] = ghostGameObject;
                    ghostGameObjects[i+1] = childGhostGameObject;
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjects));

                testWorld.CreateWorlds(true, 1);

                for (int i = 0; i < 32; ++i)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObjects[0]);
                    var serverChildEnt = testWorld.SpawnOnServer(ghostGameObjects[1]);

                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 43});
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                }

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Check that the client world has the right thing and value
                var ghostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostOwner));
                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                Assert.AreEqual(64, ghostQuery.CalculateEntityCount());
                Assert.AreEqual(32, groupQuery.CalculateEntityCount());
            }
        }

        [Test]
        [NUnit.Framework.Description("Test an edge case of ghost serialization, where we are unable to serializea group," +
                                     " therefore we reset the state and try again. The test is only meant to verify that exceptions aren't throwns and that data are serialized." +
                                     " We are not currently testing another issue that arise with large ghost, that is handled somewhat correctly, but that has not nice user error reported.")]
        public void GroupLargerThan1MTU_WorkCorrectly()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.LogLevel = NetDebug.LogLevelType.Debug; // PERFORMANCE warnings need this.
                testWorld.Bootstrap(true);

                var ghostGameObjects = new GameObject[4];
                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                //The LargeDataSizeGroupGhostConverter will create different archetypes for each child.
                //This would exercize correctly the group serialization rollback (using the same archetype would test anything).
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LargeDataSizeGroupGhostConverter();
                ghostGameObjects[0] = ghostGameObject;
                for (int i = 0; i < 3; ++i)
                {
                    var childGhostGameObject = new GameObject();
                    childGhostGameObject.name = $"Child{i}";
                    childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LargeDataSizeGroupGhostConverter();
                    ghostGameObjects[i+1] = childGhostGameObject;
                }

                //we need a large data size to fail this. We can actually hack it around a bit by forcing the
                //max snapshot size for sake of convenience.
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObjects));

                testWorld.CreateWorlds(true, 1);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObjects[0]);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});
                for (int i = 0; i < 3; ++i)
                {
                    var serverChildEnt = testWorld.SpawnOnServer(ghostGameObjects[i+1]);
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = i});
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                }

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                {
                    ValidateComponentStatsLessThanTypeStats(testWorld);
                    testWorld.Tick();
                }

                // Check that the client world has the right thing and value
                var ghostQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostChildEntity));
                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                Assert.AreEqual(3, ghostQuery.CalculateEntityCount());
                Assert.AreEqual(1, groupQuery.CalculateEntityCount());
                var rootBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(groupQuery.GetSingletonEntity());
                for (int i = 0; i < rootBuffer.Length; ++i)
                    Assert.AreEqual((byte)i, rootBuffer[i].Value);
                var entities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < entities.Length; ++i)
                {
                    var owner = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(entities[i]);
                    Assert.AreEqual(i, owner.NetworkId);
                    var childBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(entities[i]);
                    for (int j = 0; j < rootBuffer.Length; ++j)
                        Assert.AreEqual((byte)j, rootBuffer[j].Value);
                }

#if NETCODE_DEBUG
                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
#endif
}
        }

        [Test]
        [NUnit.Framework.Description("Test an edge case of ghost serialization, where we are unable to serializea group that does not have" +
                                     " children because the bitstream is already full. Therefore we reset the state and try again and again.")]
        [TestCase(0)]
        [TestCase(1)]
        public void GroupWith0Children_WorkCorrectly(int failingEntity)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                var authoringComponent = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoringComponent.GhostGroup = true;
                authoringComponent.SupportedGhostModes = GhostModeMask.Predicted;
                ghostGameObject.AddComponent<GhostByteBufferAuthoringComponent>();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                // Connect and make sure the connection could be established
                testWorld.Connect();
                // Go in-game
                testWorld.GoInGame();

                for(int i=0;i<32;++i)
                    testWorld.Tick();

                var systemData = testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld);
                // Force a very small packet size to trigger a HasFailedWrites within the ghost group serialization logic:
                // Tweak the test by iteratively changing one the encoded size:
                // Expected test SETUP result:
                //  * fail on the first entity: expected: -> larger size requested (twice the size)
                //  * fail on the second entity: only the first transmitted, then second time other entity
                int baseSize;
                int inc;
                if (failingEntity == 0)
                {
                    baseSize = 101;
                    inc = 0;
                    systemData.ValueRW.DefaultSnapshotPacketSize = 122;
                }
                else
                {
                    baseSize = 55;
                    inc = 3;
                    systemData.ValueRW.DefaultSnapshotPacketSize = 146;
                }

                for (int ent = 0; ent < 2; ++ent)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    //this will fail serialize the first entity, therefore we will retry with fragmented pipeline
                    var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(serverEnt);
                    //TODO: this is really a trick. The correct would be unit test the ChunkSerializer. And we can
                    //but it get a little annoying
                    buffer.Resize(baseSize + inc*ent, NativeArrayOptions.UninitializedMemory);
                    for (int i = 0; i < buffer.Length; ++i)
                        buffer.ElementAt(i).Value = 7;
                }

                var groupQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostGroup));
                for(int i=0;i<3;++i)
                    testWorld.Tick();
                //third tick: we should receive the first entity and second entity (case 0)
                //third tick: we should receive the first entity only (case 1)
                if(failingEntity == 0)
                    Assert.AreEqual(2, groupQuery.CalculateEntityCount());
                else
                    Assert.AreEqual(1, groupQuery.CalculateEntityCount());
                var clientEntities = groupQuery.ToEntityArray(Allocator.Temp);
                for (int ent = 0; ent < clientEntities.Length; ++ent)
                {
                    var rootBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEntities[ent]);
                    for (int i = 0; i < rootBuffer.Length; ++i)
                        Assert.AreEqual(7, rootBuffer[i].Value);
                }
                //sub-sequent tick we should have both
                testWorld.Tick();
                Assert.AreEqual(2, groupQuery.CalculateEntityCount());
                for (int ent = 0; ent < clientEntities.Length; ++ent)
                {
                    var rootBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostGenBuffer_ByteBuffer>(clientEntities[ent]);
                    for (int i = 0; i < rootBuffer.Length; ++i)
                        Assert.AreEqual(7, rootBuffer[i].Value);
                }
            }
        }

        static unsafe void ValidateComponentStatsLessThanTypeStats(NetCodeTestWorld testWorld)
        {
            var stats = testWorld.GetSingleton<GhostStatsSnapshotSingleton>(testWorld.ServerWorld);

            // TODO validate total size of packet sent is bigger than total stats size

            var perTypeStatsList = stats.UnsafeMainStatsRead.PerGhostTypeStatsListRO;
            for (int i = 0; i < perTypeStatsList.Length; i++)
            {
                uint totalSize = perTypeStatsList[i].SizeInBits;
                uint componentSizesSum = 0;
                var perComponentStatsList = perTypeStatsList[i].PerComponentStatsList;
                for (int j = 0; j < perComponentStatsList.Length; j++)
                {
                    componentSizesSum  += perComponentStatsList[j].SizeInSnapshotInBits;
                }
                Assert.IsTrue(totalSize >= componentSizesSum, $"Sum of all component stats {componentSizesSum} is larger than actual total size {totalSize}, something went wrong in stats calculations. Normally, we can expect some metadata in total size that's not accounted for by component size. But component size should always remain smaller than total size.");
            }
        }

        [Test]
        public void GhostGroup_WorksWithRelevancy_AndStaticOptimization([Values]NetCodeTestLatencyProfile latencyProfile, [Values]GhostOptimizationMode rootMode, [Values]GhostOptimizationMode childMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.SetTestLatencyProfile(latencyProfile);

                var ghostGameObject = new GameObject();
                ghostGameObject.name = "ParentGhost";
                var ghostAuthoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostAuthoring.DefaultGhostMode = GhostMode.Interpolated;
                ghostAuthoring.OptimizationMode = rootMode;
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();
                var childGhostGameObject = new GameObject();
                childGhostGameObject.name = "ChildGhost";
                var childGhostAuthoring = childGhostGameObject.AddComponent<GhostAuthoringComponent>();
                childGhostAuthoring.DefaultGhostMode = GhostMode.Predicted;
                childGhostAuthoring.OptimizationMode = childMode;
                childGhostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGroupGhostConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, childGhostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);
                testWorld.SpawnOnServer(childGhostGameObject);
                var serverEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ServerWorld);
                var serverChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 1});
                testWorld.ServerWorld.EntityManager.SetComponentData(serverChildEnt, new GhostOwner{NetworkId = 1});
                testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEnt).Add(new GhostGroup{Value = serverChildEnt});
                testWorld.Connect(maxSteps:16);
                testWorld.GoInGame();

                // Important quirk: GhostGroup children;
                // - Ignore their own relevancy value.
                // - Follow the root's relevancy value (i.e. become relevant when the root does).
                // - EXCEPT when the root becomes irrelevant (i.e. they don't despawn along with
                // a now-irrelevant GhostGroup root, but they do stop receiving updates - they become "stranded").
                // - When a GhostGroup child leaves the group, it can now again follow its own relevancy rules.
                // TODO: Test coverage of relevancy while a ghost enters & leaves a GhostGroup.
                const bool ghostGroupChildNuance = true;

                Assert.AreEqual(GhostRelevancyMode.Disabled, testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode);
                ExpectExist(testWorld, true, true, "relevancy is disabled by default, expect relevant");

                // Make them irrelevant:
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                ExpectExist(testWorld, false, ghostGroupChildNuance, "forced irrelevant 1st");

                // Make them relevant again:
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsIrrelevant;
                ExpectExist(testWorld, true, ghostGroupChildNuance, "forced relevant 1st");

                // Again irrelevant:
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                ExpectExist(testWorld, false, ghostGroupChildNuance, "forced irrelevant 2nd");

                // Only the root:
                var serverEntGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Clear();
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(1, serverEntGhostId), 1);
                ExpectExist(testWorld, true, ghostGroupChildNuance, "only root relevant (child not)");

                // Only the child:
                var serverChildEntGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverChildEnt).ghostId;
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Clear();
                testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW.GhostRelevancySet.Add(new RelevantGhostForConnection(1, serverChildEntGhostId), 1);
                ExpectExist(testWorld, false, ghostGroupChildNuance, "only child relevant (root not)");
            }
        }

        private static void ExpectExist(NetCodeTestWorld testWorld, bool expectRoot, bool expectChild, string context)
        {
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
            var clientEnt = testWorld.TryGetSingletonEntity<GhostGroupRoot>(testWorld.ClientWorlds[0]) != Entity.Null;
            var clientChildEnt = testWorld.TryGetSingletonEntity<GhostChildEntity>(testWorld.ClientWorlds[0]) != Entity.Null;
            Assert.AreEqual(expectRoot, clientEnt, "root failed:" + context);
            Assert.AreEqual(expectChild, clientChildEnt, "child failed:" + context);

            var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
            int expectedCount = (expectRoot ? 1 : 0) + (expectChild ? 1 : 0);
            var msg = ghostCount.ToString();
            Assert.AreEqual(expectedCount, ghostCount.GhostCountInstantiatedOnClient, msg);
            Assert.AreEqual(expectedCount, ghostCount.GhostCountReceivedOnClient, msg);
            Assert.AreEqual(expectedCount, ghostCount.GhostCountOnServer, msg);
        }
    }
}
