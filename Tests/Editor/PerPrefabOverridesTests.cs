using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// Registers <see cref="ClientOnlyVariant"/> as the default rule for <see cref="GhostGen_IntStruct"/> so
    /// the server prefab is stripped of the component while the client prefab keeps it.
    /// </summary>
    [DisableAutoCreation]
    sealed partial class GhostGenIntStruct_ClientOnlyVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(typeof(GhostGen_IntStruct), Rule.ForAll(typeof(ClientOnlyVariant)));
        }
    }

    internal struct InternalTargetComponent : IComponentData
    {
        public float someValue;
    }

    /// <summary>An internal variant targeting an internal component. Both are supported.</summary>
    [GhostComponentVariation(typeof(InternalTargetComponent), nameof(InternalComponentVariantTest))]
    internal struct InternalComponentVariantTest
    {
        [GhostField]
        public float someValue;
    }

    [TestFixture]
    internal class PerPrefabOverridesTests
    {
        internal class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var transform = baker.GetComponent<Transform>();
                baker.DependsOn(transform.parent);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                if(transform.parent == null)
                    baker.AddComponent(entity, new GhostOwner { NetworkId = -1});
                baker.AddComponent(entity, new GhostGen_IntStruct());
            }
        }

        GameObject[] CreatePrefabs(string[] names)
        {
            var collection = new GameObject[names.Length];
            for (int i = 0; i < names.Length; ++i)
            {
                var ghostGameObject = new GameObject(names[i]);
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                var childGhost = new GameObject("Child");
                childGhost.transform.parent = ghostGameObject.transform;
                childGhost.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                var nestedChildGhost = new GameObject("NestedChild");
                nestedChildGhost.transform.parent = childGhost.transform;
                nestedChildGhost.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
                authoring.SupportedGhostModes = GhostModeMask.All;
                collection[i] = ghostGameObject;
            }

            return collection;
        }

        //Check that the component prefab serializer and indexes are initialized as expected
        void CheckCollection(World world, int serializerIndex, int entityIndex)
        {
            using var collectionQuery = world.EntityManager.CreateEntityQuery(typeof(GhostCollection));
            var collection = collectionQuery.GetSingletonEntity();
            var ghostSerializerCollection = world.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(collection);
            var ghostComponentIndex = world.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collection);
            Assert.AreEqual(4, ghostSerializerCollection.Length);
            //First 3 (all, predicted, interpolated) should have the component (also the GhostGen_IntStruct)
            for (int i = 0; i < ghostSerializerCollection.Length; ++i)
            {
                if(serializerIndex != ghostComponentIndex[ghostSerializerCollection[i].FirstComponent].SerializerIndex)
                    continue;
                if (ghostSerializerCollection[i].NumComponents == 5)
                {
                    Assert.AreEqual(1, ghostSerializerCollection[i].NumChildComponents);
                    var subArray = ghostComponentIndex.AsNativeArray()
                        .GetSubArray(ghostSerializerCollection[i].FirstComponent, 5);

                    int count = 0;
                    foreach (var t in subArray)
                    {
                        if (t.SerializerIndex == serializerIndex)
                            count++;
                    }

                    Assert.AreEqual(2, count);
                }
                //The (none) variant should have 4
                else if (ghostSerializerCollection[i].NumComponents == 4)
                {
                    Assert.AreEqual(entityIndex==0?1:0, ghostSerializerCollection[i].NumChildComponents);
                    var subArray = ghostComponentIndex.AsNativeArray()
                        .GetSubArray(ghostSerializerCollection[i].FirstComponent, 4);

                    int count = 0;
                    foreach (var t in subArray)
                    {
                        if (t.SerializerIndex == serializerIndex)
                        {
                            count++;
                        }
                    }

                    Assert.AreEqual(1, count);
                }
                else
                {
                    Assert.Fail("Invalid number of componenent");
                }
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        public void OverrideComponentPrefabType_RootEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"ServerOnly", "ClientOnly", "PredictedOnly", "InterpolatedOnly"};
                var prefabTypes = new[] {GhostPrefabType.Server, GhostPrefabType.Client, GhostPrefabType.InterpolatedClient, GhostPrefabType.PredictedClient};
                var collection = CreatePrefabs(names);
                //overrides the component prefab types in different prefabs
                for (int i = 0; i < prefabTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var inspection = gameObject.AddComponent<GhostAuthoringInspectionComponent>();
                    inspection.ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = prefabTypes[i],
                            SendTypeOptimization = GhostSendType.AllClients,
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //Then check the expected results
                var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefabList = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    if ((prefabTypes[i] & GhostPrefabType.Server) != 0)
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    else
                        Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                }

                ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
                prefabList = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    if ((prefabTypes[i] & GhostPrefabType.Client) != 0)
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    else
                        Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                }
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        public void OverrideComponentPrefabType_ChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"ServerOnly", "ClientOnly", "PredictedOnly", "InterpolatedOnly"};
                var prefabTypes = new[] {GhostPrefabType.Server, GhostPrefabType.Client, GhostPrefabType.InterpolatedClient, GhostPrefabType.PredictedClient};
                var collection = CreatePrefabs(names);
                //Only modify child behaviors
                for (int i = 0; i < prefabTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var child = gameObject.transform.GetChild(0);
                    child.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = prefabTypes[i],
                            SendTypeOptimization = GhostSendType.AllClients,
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //Then check the expected results
                //Server
                var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefabList = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    if ((prefabTypes[i] & GhostPrefabType.Server) != 0)
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                    else
                        Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value), "{0} should not have ChildComponent", names[i]);
                }
                //Client
                ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
                prefabList = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    var linkedGroupBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    if ((prefabTypes[i] & GhostPrefabType.Client) != 0)
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                    else
                        Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[1].Value));
                }
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        public void OverrideComponentPrefabType_NestedChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"ServerOnly", "ClientOnly", "PredictedOnly", "InterpolatedOnly"};
                var prefabTypes = new[] {GhostPrefabType.Server, GhostPrefabType.Client, GhostPrefabType.InterpolatedClient, GhostPrefabType.PredictedClient};
                var collection = CreatePrefabs(names);
                // Only modify nested child behaviors
                for (int i = 0; i < prefabTypes.Length; ++i)
                {
                    var gameObject = collection[i];

                    var child = gameObject.transform.GetChild(0);
                    var nestedChild = child.GetChild(0);
                    nestedChild.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = prefabTypes[i],
                            SendTypeOptimization = GhostSendType.AllClients,
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //Then check the expected results
                //Server
                var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefabList = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    var linkedGroupBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    if ((prefabTypes[i] & GhostPrefabType.Server) != 0)
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value));
                    else
                        Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value), "{0} should not have ChildComponent", names[i]);
                }
                //Client
                ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
                prefabList = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);
                Assert.AreEqual(4, prefabList.Length);
                for (int i = 0; i < prefabList.Length; ++i)
                {
                    var linkedGroupBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(prefabList[i].Value);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(prefabList[i].Value));
                    if ((prefabTypes[i] & GhostPrefabType.Client) != 0)
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value));
                    else
                        Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(linkedGroupBuffer[2].Value));
                }
            }
        }

        [Test]
        public void OverrideComponentSendType_RootEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"All", "Interpolated", "Predicted", "None"};
                var sendTypes = new[] {GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var inspection = collection[i].AddComponent<GhostAuthoringInspectionComponent>();
                    inspection.ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = GhostPrefabType.All,
                            SendTypeOptimization = sendTypes[i],
                            VariantHash = 0,
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //In order to get the collection setup I need to enter in game
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //Then check the expected results
                var collectionEntity = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionEntity);
                var ghostComponentCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);

                var type = TypeManager.GetTypeIndex(typeof(GhostGen_IntStruct));
                var index = 0;
                while (index < ghostCollection.Length && ghostComponentCollection[ghostCollection[index].ComponentIndex].Type.TypeIndex != type) ++index;
                var serializerIndex = ghostCollection[index].SerializerIndex;


                CheckCollection(testWorld.ServerWorld, serializerIndex, 0);
                CheckCollection(testWorld.ClientWorlds[0], serializerIndex, 0);
            }
        }

        [Test]
        public void OverrideComponentSendType_ChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"All", "Interpolated", "Predicted", "None"};
                var sendTypes = new[] {GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var child = gameObject.transform.GetChild(0);
                    child.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = GhostPrefabType.All,
                            SendTypeOptimization = sendTypes[i],
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //In order to get the collection setup I need to enter in game
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //Then check the expected results
                var collectionEntity = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionEntity);
                var ghostComponentCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);

                var type = TypeManager.GetTypeIndex(typeof(GhostGen_IntStruct));
                var index = 0;
                while (index < ghostCollection.Length && ghostComponentCollection[ghostCollection[index].ComponentIndex].Type.TypeIndex != type)
                    ++index;
                var serializerIndex = ghostCollection[index].SerializerIndex;

                CheckCollection(testWorld.ServerWorld, serializerIndex, 1);
                CheckCollection(testWorld.ClientWorlds[0], serializerIndex, 1);
            }
        }

        [Test]
        public void OverrideComponentSendType_NestedChildEntity()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var names = new[] {"All", "Interpolated", "Predicted", "None"};
                var sendTypes = new[] {GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var gameObject = collection[i];
                    var child = gameObject.transform.GetChild(0);
                    var nestedChild = child.GetChild(0);
                    nestedChild.gameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new []
                    {
                        new GhostAuthoringInspectionComponent.ComponentOverride
                        {
                            FullTypeName = typeof(GhostGen_IntStruct).FullName,
                            PrefabType = GhostPrefabType.All,
                            SendTypeOptimization = sendTypes[i],
                            VariantHash = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //In order to get the collection setup I need to enter in game
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //Then check the expected results
                var collectionEntity = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collectionEntity);
                var ghostComponentCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentType>(collectionEntity);

                var type = TypeManager.GetTypeIndex(typeof(GhostGen_IntStruct));
                var index = 0;
                while (index < ghostCollection.Length && ghostComponentCollection[ghostCollection[index].ComponentIndex].Type.TypeIndex != type)
                {
                    ++index;
                }
                var serializerIndex = ghostCollection[index].SerializerIndex;

                CheckCollection(testWorld.ServerWorld, serializerIndex, 2);
                CheckCollection(testWorld.ClientWorlds[0], serializerIndex, 2);
            }
        }

        internal class InternalComponentConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new InternalTargetComponent());
            }
        }

        [Test(Description = "Both variants and the components they target can be internal. Full flow test: makes sure such a variant is found by the runtime variant logic, can be assigned as a prefab override and is used for serialization. The internal component has no ghost fields itself, so its value can only be replicated through the variant")]
        public void SerializationVariant_InternalVariantTargetingInternalComponent_IsUsedForSerialization()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);

                //Prefab creation: bake a ghost with the internal component and override its serialization with the internal variant
                var ghostGameObject = new GameObject("InternalVariantGhost");
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new InternalComponentConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.Interpolated;
                authoring.SupportedGhostModes = GhostModeMask.All;

                var hash = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(InternalComponentVariantTest).FullName, typeof(InternalTargetComponent).FullName);
                Assert.AreNotEqual(0, hash);
                ghostGameObject.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(InternalTargetComponent).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    },
                };

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject), "Cannot create ghost collection");
                testWorld.BakeGhostCollection(testWorld.ServerWorld);
                testWorld.BakeGhostCollection(testWorld.ClientWorlds[0]);

                testWorld.Connect();
                testWorld.GoInGame();

                //The internal variant is found by the runtime variant logic
                using var collectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostComponentSerializerCollectionData>());
                var collectionData = collectionQuery.GetSingleton<GhostComponentSerializerCollectionData>();
                using var strategies = collectionData.GetAllAvailableSerializationStrategiesForType(ComponentType.ReadWrite<InternalTargetComponent>(), 0, isRoot: true);
                var foundVariant = false;
                for (int i = 0; i < strategies.Length; ++i)
                    foundVariant |= strategies[i].Hash == hash;
                Assert.IsTrue(foundVariant, $"Expected the {nameof(InternalComponentVariantTest)} variant (hash {hash}) targeting the internal component {nameof(InternalTargetComponent)} to be found among the {strategies.Length} available serialization strategies");

                // set initial value. normally this component doesn't have ghost fields so shouldn't be replicated, but it works because of the variant.
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                var expectedValue = 123f;
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new InternalTargetComponent { someValue = expectedValue });

                testWorld.TickUntilClientsHaveAllGhosts();
                var clientEnt = testWorld.TryGetSingletonEntity<InternalTargetComponent>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt, "The ghost was never spawned client side");

                //Prefab override check: the prefab serializer for the internal component must point to the internal variant
                var collection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostSerializerCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(collection);
                var componentIndex = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collection);
                var ghostPrefabCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(collection);
                var typeIndex = TypeManager.GetTypeIndex<InternalTargetComponent>();
                var serializerFound = false;
                for (int i = 0; i < ghostPrefabCollection[0].NumComponents; ++i)
                {
                    var idx = componentIndex[ghostPrefabCollection[0].FirstComponent + i];
                    if (ghostSerializerCollection[idx.SerializerIndex].ComponentType.TypeIndex == typeIndex)
                    {
                        serializerFound = true;
                        Assert.AreEqual(hash, ghostSerializerCollection[idx.SerializerIndex].VariantHash, "The prefab serializer for the internal component is not using the internal variant");
                        break;
                    }
                }
                Assert.IsTrue(serializerFound, $"No serializer found for {nameof(InternalTargetComponent)} in the ghost prefab. The variant override was not applied");

                var clientValue = testWorld.ClientWorlds[0].EntityManager.GetComponentData<InternalTargetComponent>(clientEnt).someValue;
                Assert.AreEqual(expectedValue, clientValue, "the internal component's value wasn't replicated when it should have been because of variants");
            }
        }

        /// <summary>A client only variant we can assign.</summary>
        [GhostComponentVariation(typeof(Transforms.LocalTransform), nameof(TransformVariantTest))]
        [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
        internal struct TransformVariantTest
        {
            [GhostField(Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
            public float3 Position;

            [GhostField(Quantization=100, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
            public float Scale;

            [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
            public quaternion Rotation;
        }

        [Test]
        public void SerializationVariant_AreAppliedToBothRootAndChildEntities()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                var ghostGameObject = new GameObject("Root");
                var childGhost = new GameObject("Child");
                childGhost.transform.parent = ghostGameObject.transform;
                var nestedChildGhost = new GameObject("NestedChild");
                nestedChildGhost.transform.parent = childGhost.transform;
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                var inspection = ghostGameObject.AddComponent<GhostAuthoringInspectionComponent>();
                authoring.DefaultGhostMode = GhostMode.Interpolated;
                authoring.SupportedGhostModes = GhostModeMask.All;

                //Setup a variant for both root and child entity and check that the runtime serializer use this one to serialize data
                ulong hash = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformVariantTest).FullName, typeof(LocalTransform).FullName);

                using var collectionQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostComponentSerializerCollectionData>());

                Assert.AreNotEqual(0, hash);
                inspection.ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(Transforms.LocalTransform).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    },
                };
                childGhost.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
                childGhost.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(Transforms.LocalTransform).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    },
                };
                nestedChildGhost.AddComponent<NetcodeTransformUsageFlagsTestAuthoring>();
                nestedChildGhost.AddComponent<GhostAuthoringInspectionComponent>().ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = typeof(Transforms.LocalTransform).FullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = hash
                    }
                };

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject), "Cannot create ghost collection");
                testWorld.BakeGhostCollection(testWorld.ServerWorld);
                testWorld.BakeGhostCollection(testWorld.ClientWorlds[0]);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick();

                //In order to get the collection setup I need to enter in game
                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.SpawnOnServer(ghostGameObject);

                for(int i=0;i<16;++i)
                    testWorld.Tick();

                var typeIndex = TypeManager.GetTypeIndex<Transforms.LocalTransform>();

                //Then check the expected results
                var collection = testWorld.TryGetSingletonEntity<GhostCollection>(testWorld.ServerWorld);
                var ghostSerializerCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostComponentSerializer.State>(collection);
                //Check that the variant has been registered
                bool variantIsPresent = false;
                foreach (var t in ghostSerializerCollection)
                    variantIsPresent |= t.VariantHash == hash;
                Assert.IsTrue(variantIsPresent);

                var componentIndex = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionComponentIndex>(collection);
                var ghostPrefabCollection = testWorld.ServerWorld.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(collection);
                //And verify that the component associated with the ghost for the transform point to this index
                for (int i = 0; i < ghostPrefabCollection[0].NumComponents;++i)
                {
                    var idx = componentIndex[ghostPrefabCollection[0].FirstComponent + i];
                    if (ghostSerializerCollection[idx.SerializerIndex].ComponentType.TypeIndex == typeIndex)
                    {
                        Assert.IsTrue(ghostSerializerCollection[idx.SerializerIndex].VariantHash == hash);
                    }
                }
            }
        }

        [Test]
        public void AddPrefabOverride_InRoot_ComputesGameObjectReference()
        {
            AddPrefabOverride_ComputesGameObjectReference((collection, i) => collection[i]);
        }

        [Test]
        public void AddPrefabOverride_InChild_ComputesGameObjectReference()
        {
            AddPrefabOverride_ComputesGameObjectReference((collection, i) => collection[i].transform.GetChild(0).gameObject);
        }

        [Test]
        public void AddPrefabOverride_InNestedChild_ComputesGameObjectReference()
        {
            AddPrefabOverride_ComputesGameObjectReference((collection, i) => collection[i].transform.GetChild(0).GetChild(0).gameObject);
        }

        private void AddPrefabOverride_ComputesGameObjectReference(Func<GameObject[], int, GameObject> testTransform)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var names = new[] { "All", "Interpolated", "Predicted", "None" };
            var sendTypes = new[]
                { GhostSendType.AllClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.OnlyPredictedClients, GhostSendType.DontSend };
            var collection = CreatePrefabs(names);
            for (int i = 0; i < sendTypes.Length; ++i)
            {
                var goFromFunc = testTransform(collection, i);
                const int exampleEntityIndex = 66;
                var inspection = goFromFunc.GetComponent<GhostAuthoringInspectionComponent>() ?? goFromFunc.AddComponent<GhostAuthoringInspectionComponent>();

                var entityGuid = new EntityGuid(goFromFunc.GetEntityId(), EntityId.None, 0, exampleEntityIndex);

                var componentOverride = inspection.GetOrAddPrefabOverride(typeof(GhostGen_IntStruct), entityGuid, (GhostPrefabType) GhostAuthoringInspectionComponent.ComponentOverride.NoOverride);

                var ghostAuthoringComponent = collection[i].GetComponent<GhostAuthoringComponent>();
                Assert.IsNotNull(ghostAuthoringComponent);
                var allComponentOverrides = GhostAuthoringInspectionComponent.CollectAllComponentOverridesInInspectionComponents(ghostAuthoringComponent, false);
                (GameObject, GhostAuthoringInspectionComponent.ComponentOverride) foundInspection = default;
                foreach (var x in allComponentOverrides)
                {
                    if (x.Item1 == goFromFunc)
                    {
                        foundInspection = x;
                    }
                }
                Assert.AreEqual(foundInspection.Item1.GetEntityId(), entityGuid.OriginatingEntityId, $"entityGuid.OriginatingEntityId '{entityGuid.OriginatingEntityId}' did not match game object set '{goFromFunc}'");
                Assert.AreEqual(foundInspection.Item2.EntityIndex, exampleEntityIndex, "EntityIndex should have been set!");
            }
        }

        [Test, Description("A ComponentOverride targeting a renamed/deleted type should be removed, so the UI doesn't enter a bad state.")]
        public void LogErrorIfComponentOverrideIsInvalid_RemovesUnknownTypeOverride()
        {
            var go = new GameObject(nameof(LogErrorIfComponentOverrideIsInvalid_RemovesUnknownTypeOverride));
            try
            {
                var inspection = go.AddComponent<GhostAuthoringInspectionComponent>();
                inspection.ComponentOverrides = new[]
                {
                    new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = "Unity.NetCode.Tests.NonExistentType_RenamedAway",
                        EntityIndex = 0,
                        PrefabType = GhostPrefabType.Server,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = 12345UL,
                    },
                };

                GhostAuthoringInspectionComponent.forceSave = false;
                GhostAuthoringInspectionComponent.forceBake = false;

                LogAssert.Expect(LogType.Error, new Regex("invalid 'Component Override'"));
                inspection.LogErrorIfComponentOverrideIsInvalid();

                Assert.AreEqual(0, inspection.ComponentOverrides.Length, "Override targeting an unknown type should be auto-removed.");
                Assert.IsTrue(GhostAuthoringInspectionComponent.forceSave, "Removing an invalid override should request a save.");
                Assert.IsTrue(GhostAuthoringInspectionComponent.forceBake, "Removing an invalid override should request a re-bake.");
            }
            finally
            {
                GhostAuthoringInspectionComponent.forceSave = false;
                GhostAuthoringInspectionComponent.forceBake = false;
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test, Description("Verifies that a component - whose default rule is ClientOnlyVariant - is stripped from the baked server prefab (so the server never allocates or processes it) while remaining present on every client-side prefab variant.")]
        [DisableSingleWorldHostTest]
        public void DefaultVariant_ClientOnlyVariant_StripsComponentFromServerPrefab()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(GhostGenIntStruct_ClientOnlyVariantSystem));
            var collection = CreatePrefabs(new[] { "ClientOnlyGhost" });
            Assert.IsTrue(testWorld.CreateGhostCollection(collection));
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var serverGhostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            using var serverPrefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(serverGhostCollection).ToNativeArray(Allocator.Temp);
            Assert.Greater(serverPrefabs.Length, 0, "Expected at least one baked server prefab.");
            for (int i = 0; i < serverPrefabs.Length; ++i)
            {
                Assert.IsFalse(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(serverPrefabs[i].Value),
                    "ClientOnlyVariant must strip the component from the server-side root prefab.");
            }

            var clientGhostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
            using var clientPrefabs = testWorld.ClientWorlds[0].EntityManager.GetBuffer<NetCodeTestPrefab>(clientGhostCollection).ToNativeArray(Allocator.Temp);
            Assert.Greater(clientPrefabs.Length, 0, "Expected at least one baked client prefab.");
            for (int i = 0; i < clientPrefabs.Length; ++i)
            {
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(clientPrefabs[i].Value),
                    "ClientOnlyVariant must NOT strip the component from the client-side root prefab.");
            }
        }
    }
}
