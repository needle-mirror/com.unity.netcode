using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [TestFixture]
    public class PerPrefabOverridesTests
    {
        public class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager,
                GameObjectConversionSystem conversionSystem)
            {
                if(gameObject.transform.parent == null)
                    dstManager.AddComponentData(entity, new GhostOwnerComponent { NetworkId = -1});
                dstManager.AddComponentData(entity, new GhostGen_IntStruct());
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
                authoring.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                authoring.SupportedGhostModes = GhostAuthoringComponent.GhostModeMask.All;
                collection[i] = ghostGameObject;
            }

            return collection;
        }

        //Check that the component prefab serializer and indexes are initialized as expected
        void CheckCollection(World world, int serializerIndex, int entityIndex)
        {
            var collection = world.EntityManager.CreateEntityQuery(typeof(GhostCollection)).GetSingletonEntity();
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
                    Assert.AreEqual(2, ghostComponentIndex.AsNativeArray()
                        .GetSubArray(ghostSerializerCollection[i].FirstComponent, 5)
                        .Count(t => t.SerializerIndex == serializerIndex));
                }
                //The (none) variant should have 4
                else if (ghostSerializerCollection[i].NumComponents == 4)
                {
                    Assert.AreEqual(entityIndex==0?1:0, ghostSerializerCollection[i].NumChildComponents);
                    Assert.AreEqual(1, ghostComponentIndex.AsNativeArray()
                        .GetSubArray(ghostSerializerCollection[i].FirstComponent, 4)
                        .Count(t => t.SerializerIndex == serializerIndex));
                }
                else
                {
                    Assert.Fail("Invalid number of componenent");
                }
            }
        }

        [Test]
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
                    var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                    authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                    {
                        new GhostAuthoringComponent.ComponentOverride
                        {
                            fullTypeName = typeof(GhostGen_IntStruct).FullName,
                            gameObject = collection[i],
                            PrefabType = (int)prefabTypes[i],
                            OwnerPredictedSendType = (int)GhostSendType.All,
                            ComponentVariant = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

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
                    var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                    authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                    {
                        new GhostAuthoringComponent.ComponentOverride
                        {
                            fullTypeName = typeof(GhostGen_IntStruct).FullName,
                            gameObject = collection[i].transform.GetChild(0).gameObject,
                            PrefabType = (int)prefabTypes[i],
                            OwnerPredictedSendType = (int)GhostSendType.All,
                            ComponentVariant = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

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
                    var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                    authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                    {
                        new GhostAuthoringComponent.ComponentOverride
                        {
                            fullTypeName = typeof(GhostGen_IntStruct).FullName,
                            gameObject = collection[i].transform.GetChild(0).GetChild(0).gameObject,
                            PrefabType = (int)prefabTypes[i],
                            OwnerPredictedSendType = (int)GhostSendType.All,
                            ComponentVariant = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

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
                var sendTypes = new[] {GhostSendType.All, GhostSendType.Interpolated, GhostSendType.Predicted, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                    authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                    {
                        new GhostAuthoringComponent.ComponentOverride
                        {
                            fullTypeName = typeof(GhostGen_IntStruct).FullName,
                            gameObject = collection[i],
                            PrefabType = (int)GhostPrefabType.All,
                            OwnerPredictedSendType = (int)sendTypes[i],
                            SendForChild = GhostAuthoringComponent.ComponentOverride.UseDefaultValue,
                            ComponentVariant = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

                //In order to get the collection setup I need to enter in game
                testWorld.Connect(1.0f / 60f, 16);
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

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
                var sendTypes = new[] {GhostSendType.All, GhostSendType.Interpolated, GhostSendType.Predicted, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                    authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                    {
                        new GhostAuthoringComponent.ComponentOverride
                        {
                            fullTypeName = typeof(GhostGen_IntStruct).FullName,
                            gameObject = collection[i].transform.GetChild(0).gameObject,
                            PrefabType = (int)GhostPrefabType.All,
                            OwnerPredictedSendType = (int)sendTypes[i],
                            SendForChild = GhostAuthoringComponent.ComponentOverride.UseDefaultValue,
                            ComponentVariant = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

                //In order to get the collection setup I need to enter in game
                testWorld.Connect(1.0f / 60f, 16);
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

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
                var sendTypes = new[] {GhostSendType.All, GhostSendType.Interpolated, GhostSendType.Predicted, (GhostSendType)0};
                var collection = CreatePrefabs(names);
                for (int i = 0; i < sendTypes.Length; ++i)
                {
                    var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                    authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                    {
                        new GhostAuthoringComponent.ComponentOverride
                        {
                            fullTypeName = typeof(GhostGen_IntStruct).FullName,
                            gameObject = collection[i].transform.GetChild(0).GetChild(0).gameObject,
                            PrefabType = (int)GhostPrefabType.All,
                            OwnerPredictedSendType = (int)sendTypes[i],
                            SendForChild = GhostAuthoringComponent.ComponentOverride.UseDefaultValue,
                            ComponentVariant = 0
                        }
                    };
                }

                Assert.IsTrue(testWorld.CreateGhostCollection(collection));
                testWorld.CreateWorlds(true, 1);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

                //In order to get the collection setup I need to enter in game
                testWorld.Connect(1.0f / 60f, 16);
                testWorld.GoInGame();

                for (int i = 0; i < collection.Length; ++i)
                    testWorld.SpawnOnServer(collection[i]);

                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

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

        //Does nothing special (same serialiation code) but just provide a variant we can assign
        [GhostComponentVariation(typeof(Transforms.Translation), "TranslationVariantTest")]
        [GhostComponent(PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.All, SendDataForChildEntity = false)]
        public struct TranslationVariantTest
        {
            [GhostField(Quantization=1000, Smoothing=SmoothingAction.Interpolate, SubType=0)] public float3 Value;
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
                authoring.DefaultGhostMode = GhostAuthoringComponent.GhostMode.Interpolated;
                authoring.SupportedGhostModes = GhostAuthoringComponent.GhostModeMask.All;

                //Setup a variant for both root and child entity and check that the runtime serializer use this one to serialize data
                var attrType = typeof(TranslationVariantTest).GetCustomAttribute<GhostComponentVariationAttribute>();
                ulong hash = 0;
                foreach (var serializer in testWorld.ServerWorld.GetExistingSystem<GhostComponentSerializerCollectionSystemGroup>().GhostComponentCollection)
                {
                    if (serializer.ComponentType == attrType.ComponentType && GhostComponentSerializer.VariantTypes[serializer.VariantTypeIndex] == typeof(TranslationVariantTest))
                        hash = serializer.VariantHash;
                }
                Assert.AreNotEqual(0, hash);
                authoring.ComponentOverrides = new List<GhostAuthoringComponent.ComponentOverride>
                {
                    new GhostAuthoringComponent.ComponentOverride
                    {
                        fullTypeName = typeof(Transforms.Translation).FullName,
                        gameObject = ghostGameObject,
                        PrefabType = (int)GhostPrefabType.All,
                        OwnerPredictedSendType = (int)GhostSendType.All,
                        ComponentVariant = hash
                    },
                    new GhostAuthoringComponent.ComponentOverride
                    {
                        fullTypeName = typeof(Transforms.Translation).FullName,
                        gameObject = childGhost,
                        PrefabType = (int)GhostPrefabType.All,
                        OwnerPredictedSendType = (int)GhostSendType.All,
                        ComponentVariant = hash
                    },
                    new GhostAuthoringComponent.ComponentOverride
                    {
                        fullTypeName = typeof(Transforms.Translation).FullName,
                        gameObject = nestedChildGhost,
                        PrefabType = (int)GhostPrefabType.All,
                        OwnerPredictedSendType = (int)GhostSendType.All,
                        ComponentVariant = hash
                    }
                };

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject), "Cannot create ghost collection");
                testWorld.ConvertGhostCollection(testWorld.ServerWorld);
                testWorld.ConvertGhostCollection(testWorld.ClientWorlds[0]);

                //Register serializers and setup all the system
                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

                //In order to get the collection setup I need to enter in game
                testWorld.Connect(1.0f / 60f, 16);
                testWorld.GoInGame();
                testWorld.SpawnOnServer(ghostGameObject);

                for(int i=0;i<16;++i)
                    testWorld.Tick(1.0f/60.0f);

                var typeIndex = TypeManager.GetTypeIndex<Transforms.Translation>();
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

        [Test]
        public void AddPrefabOverride_NotFoundInstanceID_Throws()
        {
            var notFoundGameObject = new GameObject("Not in the hierarchy");
            var argumentException = Assert.Throws<ArgumentException>(() =>
            {
                AddPrefabOverride_ComputesGameObjectReference((collection, i) => notFoundGameObject);
            });
            Assert.AreEqual(argumentException.Message, $"{notFoundGameObject.GetInstanceID()}: didn't match any game object in All");
        }

        private void AddPrefabOverride_ComputesGameObjectReference(Func<GameObject[], int, GameObject> testTransform)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var names = new[] { "All", "Interpolated", "Predicted", "None" };
            var sendTypes = new[]
                { GhostSendType.All, GhostSendType.Interpolated, GhostSendType.Predicted, GhostSendType.None };
            var collection = CreatePrefabs(names);
            for (int i = 0; i < sendTypes.Length; ++i)
            {
                var authoring = collection[i].GetComponent<GhostAuthoringComponent>();
                var entityGuid = new EntityGuid
                {
                    a = (ulong)testTransform(collection, i).GetInstanceID(),
                    b = 0,
                };
                var componentOverride = authoring.AddPrefabOverride(typeof(GhostGen_IntStruct).FullName, entityGuid);
                Assert.AreEqual(componentOverride.gameObject.GetInstanceID(), entityGuid.OriginatingId,
                    $"{entityGuid.OriginatingId}: did not match game object set on {componentOverride.gameObject.name}");
            }
        }
    }
}
