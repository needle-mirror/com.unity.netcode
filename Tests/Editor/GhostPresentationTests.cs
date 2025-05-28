#if! UNITY_DISABLE_MANAGED_COMPONENTS
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.Hybrid;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    [WriteGroup(typeof(LocalToWorld))]
    struct TestWriteGroupComponent : IComponentData
    {
    }

    [DisableAutoCreation]
    [WriteGroup(typeof(LocalToWorld))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    partial struct CustomPresetationSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach(var (ltw, lt) in SystemAPI.Query<RefRW<LocalToWorld>, RefRO<LocalTransform>>()
                        .WithAll<TestWriteGroupComponent>())
            {
                ltw.ValueRW.Value = float4x4.TRS(new float3(1, 2, 3), quaternion.identity, lt.ValueRO.Scale);
            }
        }
    }
    [DisableAutoCreation]
    [WriteGroup(typeof(LocalToWorld))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    partial struct UpdateTransformSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            foreach(var lt in SystemAPI.Query<RefRW<LocalTransform>>()
                        .WithAll<TestWriteGroupComponent>())
            {
                lt.ValueRW.Position.x += 5f*deltaTime;
                lt.ValueRW.Position.y += 5f*deltaTime;
                lt.ValueRW.Position.z += 5f*deltaTime;
                lt.ValueRW = lt.ValueRW.RotateY(1f * deltaTime);
            }
        }
    }
    internal class GhostPresentationTests
    {
        [Test]
        public void SpawningEntityWithGhostPresentationCreateGameObject(
            [Values]bool presentOnClient,
            [Values]bool presentOnServer)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Hybrid");
                testWorld.Bootstrap(true);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var go = new GameObject("GhostPresentation");
                var presentationGameObjectAuthoring = go.AddComponent<GhostPresentationGameObjectAuthoring>();
                presentationGameObjectAuthoring.ClientPrefab = presentOnClient ? cube : null;
                presentationGameObjectAuthoring.ServerPrefab = presentOnServer ? cube : null;
                var ghostAuthoringComponent = go.AddComponent<GhostAuthoringComponent>();

                testWorld.CreateGhostCollection(go);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0; i<16; ++i)
                    testWorld.Tick();

                //we should not have any object yet attached to the ghost. But the ghost must have some component data here.
                var entity = testWorld.SpawnOnServer(0);
                if (presentOnServer)
                {
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostPresentationGameObjectPrefabReference>(entity));
                    var prefabReference = testWorld.ServerWorld.EntityManager.GetComponentData<GhostPresentationGameObjectPrefabReference>(entity);
                    Assert.AreNotEqual(Entity.Null, prefabReference.Prefab);
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostPresentationGameObjectPrefab>(prefabReference.Prefab));
                    Assert.AreEqual(cube, testWorld.ServerWorld.EntityManager.GetComponentObject<GhostPresentationGameObjectPrefab>(prefabReference.Prefab).Server);
                    testWorld.Tick();
                    //the server should now add the cleanup to track the object and a gameobject should have been spawned
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostPresentationGameObjectState>(entity));
                    Assert.AreEqual(0, testWorld.ServerWorld.EntityManager.GetComponentData<GhostPresentationGameObjectState>(entity).GameObjectIndex);
                    var serverGameObject = testWorld.ServerWorld.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().GetGameObjectForEntity(testWorld.ServerWorld.EntityManager, entity);
                    Assert.IsNotNull(serverGameObject);
                }

                //spawn on client side
                for(int i=0; i<8; ++i)
                    testWorld.Tick();

                var ghosts = testWorld.ClientWorlds[0].EntityManager
                    .CreateEntityQuery(new ComponentType(typeof(GhostInstance)));
                var clientEntity = ghosts.GetSingletonEntity();
                if (presentOnClient)
                {
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostPresentationGameObjectPrefabReference>(clientEntity));
                    var prefabReference = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostPresentationGameObjectPrefabReference>(clientEntity);
                    Assert.AreNotEqual(Entity.Null, prefabReference.Prefab);
                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostPresentationGameObjectPrefab>(prefabReference.Prefab));
                    Assert.AreEqual(cube, testWorld.ClientWorlds[0].EntityManager.GetComponentObject<GhostPresentationGameObjectPrefab>(prefabReference.Prefab).Client);
                    var clientGameObject = testWorld.ClientWorlds[0].GetExistingSystemManaged<GhostPresentationGameObjectSystem>().GetGameObjectForEntity(testWorld.ClientWorlds[0].EntityManager, clientEntity);
                    Assert.IsNotNull(clientGameObject);
                }
            }
        }

        [Test]
        public void PresentationGameObjectTransformInSyncWithLTW()
        {
            void VerifyPositionEqualsLTW(World world, Entity entity)
            {
                var lt = world.EntityManager.GetComponentData<LocalTransform>(entity);
                var ltw = world.EntityManager.GetComponentData<LocalToWorld>(entity);
                var go = world.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().GetGameObjectForEntity(world.EntityManager,entity);
                //the LT and LTW must be different
                Assert.AreNotEqual(0f, math.distance(ltw.Position, lt.Position));
                Assert.AreNotEqual(0f, math.angle(ltw.Rotation, lt.Rotation));
                var scale = ltw.Value.Scale();
                Assert.AreEqual(0f, math.distance(scale, lt.Scale));
                //and transform should be identical to LTW
                Assert.AreEqual(0f, math.distance(go.transform.localPosition, ltw.Position));
                Assert.AreEqual(0f, math.angle(go.transform.localRotation, ltw.Rotation));
                Assert.AreNotEqual(0f, math.distance(go.transform.localScale, scale));
                Assert.AreEqual(0f, math.distance(go.transform.localScale, new float3(1f,1f,1f)));
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Hybrid");
                //We use a custom transform system here that guarantee we are doing something custom for transform so we can
                //easily test.
                //Another possible test is to use either prediction switching or physics with interpolation.
                testWorld.Bootstrap(true, typeof(CustomPresetationSystem), typeof(UpdateTransformSystem));
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var go = new GameObject("GhostPresentation");
                var presentationGameObjectAuthoring = go.AddComponent<GhostPresentationGameObjectAuthoring>();
                presentationGameObjectAuthoring.ClientPrefab = cube;
                presentationGameObjectAuthoring.ServerPrefab = cube;
                var ghostAuthoringComponent = go.AddComponent<GhostAuthoringComponent>();

                testWorld.CreateGhostCollection(go);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0; i<16; ++i)
                    testWorld.Tick();

                testWorld.ServerWorld.EntityManager.AddComponent<TestWriteGroupComponent>(
                    testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ServerWorld)[0].GhostPrefab);
                testWorld.ClientWorlds[0].EntityManager.AddComponent<TestWriteGroupComponent>(
                    testWorld.GetSingletonBuffer<GhostCollectionPrefab>(testWorld.ClientWorlds[0])[0].GhostPrefab);

                var serverEntity = testWorld.SpawnOnServer(0);
                //we don't sync scale, nor the PostfixMatrix is supported for PresentationGameObject.
                //that may make sense or depending on the context.
                //But we are definitively assert in the test that the scaling can be different
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, LocalTransform.FromPositionRotationScale(new float3(10f, 10f, 10f), quaternion.identity, 5f));
                testWorld.Tick();
                //TODO: this is the wrong patter an we should change this (we should not call other systems)
                //TODO: seems to complicate retrieve the GameObject (we should just have an UnityObjectRef in a unmanaged component).
                var serverGo = testWorld.ServerWorld.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().GetGameObjectForEntity(testWorld.ServerWorld.EntityManager, serverEntity);

                VerifyPositionEqualsLTW(testWorld.ServerWorld, serverEntity);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
                var clientEntity = testWorld.TryGetSingletonEntity<GhostInstance>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEntity);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                    VerifyPositionEqualsLTW(testWorld.ServerWorld, serverEntity);
                    VerifyPositionEqualsLTW(testWorld.ClientWorlds[0], clientEntity);
                }
            }
        }

        [Test]
        public void DestroyEntitiesWithGhostPresentationDestroyGameObjects()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Hybrid");
                testWorld.Bootstrap(true);
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var go = new GameObject("GhostPresentation");
                var presentationGameObjectAuthoring = go.AddComponent<GhostPresentationGameObjectAuthoring>();
                presentationGameObjectAuthoring.ClientPrefab = cube;
                presentationGameObjectAuthoring.ServerPrefab = cube;
                var ghostAuthoringComponent = go.AddComponent<GhostAuthoringComponent>();

                testWorld.CreateGhostCollection(go);
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();
                for(int i=0; i<16; ++i)
                    testWorld.Tick();

                //spawn a bunch
                var spawnedEntities = new NativeArray<Entity>(31, Allocator.Temp);
                for (int i = 0; i < 31; ++i)
                    spawnedEntities[i] = testWorld.SpawnOnServer(0);

                //sync all
                for(int i=0; i<16; ++i)
                    testWorld.Tick();

                var ghosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
                Assert.AreEqual(31, ghosts.CalculateEntityCount());

                //we need to test one specific edge case that it despawn the last object in the list
                var lastEntity = testWorld.ServerWorld.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().m_Entities[^1];
                var serverEntities = testWorld.ServerWorld.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().m_Entities.ToArray(Allocator.Temp);
                var serverGameObjects = testWorld.ServerWorld.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().m_GameObjects.ToArray();
                var clientEntities = testWorld.ClientWorlds[0].GetExistingSystemManaged<GhostPresentationGameObjectSystem>().m_Entities.ToArray(Allocator.Temp);
                var clientGameObjects = testWorld.ClientWorlds[0].GetExistingSystemManaged<GhostPresentationGameObjectSystem>().m_GameObjects.ToArray();
                //building a ghost-id to gameobject mapping here for
                var ghostIdToGameObject = new Dictionary<int, GameObject>();
                var serverGhostIds = new NativeArray<int>(serverEntities.Length, Allocator.Temp);
                for (var index = 0; index < serverGhostIds.Length; index++)
                    serverGhostIds[index] = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntities[index]).ghostId;
                for (var index = 0; index < clientEntities.Length; index++)
                    ghostIdToGameObject[testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(clientEntities[index]).ghostId] = clientGameObjects[index];
                testWorld.ServerWorld.EntityManager.DestroyEntity(lastEntity);
                //everthing should be still fine
                for(int i=0; i<8; ++i)
                    testWorld.Tick();
                //there should be no gameobject associated with this entity anymore
                Assert.IsFalse(serverGameObjects[^1]);
                Assert.AreEqual(30, ghosts.CalculateEntityCount());
                //and not more gameobject for it too.
                for (int i = 0; i < 15; ++i)
                    testWorld.ServerWorld.EntityManager.DestroyEntity(serverEntities[i*2]);
                for(int i=0; i<16; ++i)
                    testWorld.Tick();
                Assert.AreEqual(15, ghosts.CalculateEntityCount());
                //No more of these gameobjects present for the server and client
                for (int i = 0; i < 15; ++i)
                {
                    Assert.IsFalse(serverGameObjects[i * 2]);
                    Assert.IsFalse(ghostIdToGameObject[serverGhostIds[i * 2]]);
                }
                //despawn everything at once
                var remainingEntities = testWorld.ServerWorld.GetExistingSystemManaged<GhostPresentationGameObjectSystem>().m_Entities;
                for (int i = 0; i < remainingEntities.Length; ++i)
                    testWorld.ServerWorld.EntityManager.DestroyEntity(remainingEntities[i]);
                for(int i=0; i<16; ++i)
                    testWorld.Tick();
                //all gameobjects should be gone at this point
                for (int i = 0; i < serverGameObjects.Length; ++i)
                {
                    Assert.IsFalse(serverGameObjects[i]);
                }
                for (int i = 0; i < clientGameObjects.Length; ++i)
                {
                    Assert.IsFalse(clientGameObjects[i]);
                }
            }
        }
    }
}
#endif
