using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.NetCode
{
    /// <summary>
    /// Postprocess all the game objects present in a subscene witch present a GhostAuthoringComponent by adding to the primary
    /// entities the following components:
    /// - A PrespawnId component: contains a unique identifier (per subscene) that is guaranteed to be determistic
    /// - A SubSceneGhostComponentHash shared component: used to deterministically group the ghost instances
    /// </summary>
    [UpdateInGroup(typeof(GameObjectAfterConversionGroup))]
    [UpdateAfter(typeof(GhostAuthoringConversion))]
    [ConverterVersion("cmarastoni", 8)]
    class PreSpawnedGhostsConversion : GameObjectConversionSystem
    {
        protected override void OnUpdate()
        {
            var hashToEntity = new NativeHashMap<ulong, Entity>(128, Allocator.TempJob);
            // TODO: Check that the GhostAuthoringComponent is interpolated, as we don't support predicted atm
            Entities.ForEach((GhostAuthoringComponent ghostAuthoring) =>
            {
                var entity = GetPrimaryEntity(ghostAuthoring);
                var isInSubscene = DstEntityManager.HasComponent<SceneSection>(entity);
                bool isPrefab = !ghostAuthoring.gameObject.scene.IsValid() || ghostAuthoring.ForcePrefabConversion;
                var activeInScene = ghostAuthoring.gameObject.activeInHierarchy;
                if (!isPrefab && isInSubscene && activeInScene)
                {
                    var hashData = new NativeList<ulong>(Allocator.TempJob);
                    var componentTypes = DstEntityManager.GetComponentTypes(entity, Allocator.TempJob);
                    // Hash all the data on the entity (first each component data then all together)
                    //Add the scene hahs
                    for (int i = 0; i < componentTypes.Length; ++i)
                    {
                        // Do not include components which might be included or not included depending on if you are a
                        // client or a server
                        // TODO: Check the interpolated/predicted/server bools instead
                        //       Only iterate ghostAuthoring.Components
                        //       Should skip PhysicsCollider, WorldRenderBounds, XXXSnapshotData, PredictedGhostComponent
                        if (componentTypes[i] == typeof(Translation) || componentTypes[i] == typeof(Rotation))
                        {
                            var componentDataHash = ComponentDataToHash(entity, componentTypes[i]);
                            hashData.Add(componentDataHash);
                        }
                    }

                    hashData.Sort();

                    //Add the scene guid at the very end
                    var sceneSection = DstEntityManager.GetSharedComponentData<SceneSection>(entity);
                    hashData.Add(sceneSection.SceneGUID.Value[0]);
                    hashData.Add(sceneSection.SceneGUID.Value[1]);
                    hashData.Add(sceneSection.SceneGUID.Value[2]);
                    hashData.Add(sceneSection.SceneGUID.Value[3]);
                    ulong combinedComponentHash;
                    unsafe
                    {
                        combinedComponentHash = Unity.Core.XXHash.Hash64((byte*) hashData.GetUnsafeReadOnlyPtr(),
                            hashData.Length * sizeof(ulong));
                    }

                    // When duplicating a scene object it will have the same position/rotation as the original, so until that
                    // changes there will always be a duplicate hash until it's moved to it's own location
                    if (!hashToEntity.ContainsKey(combinedComponentHash))
                        hashToEntity.Add(combinedComponentHash, entity);

                    componentTypes.Dispose();
                    hashData.Dispose();
                }
            });

            if (hashToEntity.Count() > 0)
            {
                //Add the components in batch
                var values = hashToEntity.GetValueArray(Allocator.TempJob);
                DstEntityManager.AddComponent(values, typeof(PreSpawnedGhostIndex));
                DstEntityManager.AddComponent(values, typeof(PrespawnGhostBaseline));

                var keys = hashToEntity.GetKeyArray(Allocator.TempJob);
                keys.Sort();

                // Assign ghost IDs to the pre-spawned entities sorted by component data hash
                for (int i = 0; i < keys.Length; ++i)
                {
                    DstEntityManager.SetComponentData(hashToEntity[keys[i]], new PreSpawnedGhostIndex {Value = i});
                    //We need to pre-assign the ghostType to -1 so that that the ghost is actually identified as prespawn
                    //befor
                    DstEntityManager.SetComponentData(hashToEntity[keys[i]], new GhostComponent
                    {
                        ghostId = 0,
                        // GhostType -1 is a special case for prespawned ghosts which is converted to a proper ghost id in the send / receive systems
                        // once the ghost ids are known
                        ghostType = -1,
                        spawnTick = 0
                    });
                    //Disable the entity so the ghost cannot be retrieved before the prespawn baseline are calculated
                    DstEntityManager.AddComponent<Disabled>(hashToEntity[keys[i]]);
                }

                // Save the final subscene hash with all the pre-spawned ghosts
                ulong hash;
                unsafe
                {
                    hash = Unity.Core.XXHash.Hash64((byte*) keys.GetUnsafeReadOnlyPtr(),
                        keys.Length * sizeof(ulong));
                }

                for (int i = 0; i < keys.Length; ++i)
                {
                    // Track the subscene which is the parent of this entity
                    DstEntityManager.AddSharedComponentData(hashToEntity[keys[i]], new SubSceneGhostComponentHash {Value = hash});
                }

                //Add the SubSceneWithPrespawnGhosts to the scene entity
                //FIXME: current limitation: we are expecting all the prespawn entities belonging to same section
                var sectionEntity = GetSceneSectionEntity(hashToEntity[keys[0]]);
                if (sectionEntity != Entity.Null)
                {
                    DstEntityManager.AddComponentData(sectionEntity, new SubSceneWithPrespawnGhosts
                    {
                        SubSceneHash = hash,
                        PrespawnCount = keys.Length
                    });
                }
                //We can add more here. Ideally the serialization. A way would be to use a sort of offset re-mapping
                values.Dispose();
                keys.Dispose();
            }

            hashToEntity.Dispose();
        }

        ulong ComponentDataToHash(Entity entity, ComponentType componentType)
        {
            var untypedType = DstEntityManager.GetDynamicComponentTypeHandle(componentType);
            var chunk = DstEntityManager.GetChunk(entity);
            var sizeInChunk = TypeManager.GetTypeInfo(componentType.TypeIndex).SizeInChunk;
            var data = chunk.GetDynamicComponentDataArrayReinterpret<byte>(untypedType, sizeInChunk);

            var entityType = GetEntityTypeHandle();
            var entities = chunk.GetNativeArray(entityType);
            int index = -1;
            for (int j = 0; j < entities.Length; ++j)
            {
                if (entities[j] == entity)
                {
                    index = j;
                    break;
                }
            }

            ulong hash = 0;
            if (index != -1)
            {
                unsafe
                {
                    hash = Unity.Core.XXHash.Hash64((byte*) data.GetUnsafeReadOnlyPtr() + (index * sizeInChunk),
                        sizeInChunk);
                }
            }

            return hash;
        }
    }
}
