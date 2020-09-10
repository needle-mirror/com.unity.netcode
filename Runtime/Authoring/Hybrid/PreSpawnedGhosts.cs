using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

[UpdateInGroup(typeof(GameObjectAfterConversionGroup))]
[UpdateAfter(typeof(GhostAuthoringConversion))]
class PreSpawnedGhosts : GameObjectConversionSystem
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

                ulong combinedComponentHash;
                unsafe
                {
                    combinedComponentHash = Unity.Core.XXHash.Hash64((byte*)hashData.GetUnsafeReadOnlyPtr(), hashData.Length * sizeof(ulong));
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
            var keys = hashToEntity.GetKeyArray(Allocator.TempJob);
            keys.Sort();

            // Assign ghost IDs to the pre-spawned entities sorted by component data hash
            for (int i = 0; i < keys.Length; ++i)
            {
                DstEntityManager.AddComponentData(hashToEntity[keys[i]], new PreSpawnedGhostId {Value = i + 1});
            }

            // Save the final subscene hash with all the pre-spawned ghosts
            unsafe
            {
                var hash = Unity.Core.XXHash.Hash64((byte*) keys.GetUnsafeReadOnlyPtr(), keys.Length * sizeof(ulong));

                for (int i = 0; i < keys.Length; ++i)
                {
                    // Track the subscene which is the parent of this entity
                    DstEntityManager.AddSharedComponentData(hashToEntity[keys[i]], new SubSceneGhostComponentHash {Value = hash});
                }
            }
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
                hash = Unity.Core.XXHash.Hash64((byte*)data.GetUnsafeReadOnlyPtr() + (index*sizeInChunk), sizeInChunk);
            }
        }

        return hash;
    }
}