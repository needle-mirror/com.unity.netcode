using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.HostMigration;
using UnityEngine;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [BakingVersion("larus", 2)]
    partial class HostMigrationBakingSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var hashToEntity = new NativeParallelHashMap<ulong, Entity>(128, Allocator.Temp);
            var migrationDataQuery = SystemAPI.QueryBuilder().WithAllRW<IncludeInMigration>().Build();
            var migrationEntities = migrationDataQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < migrationEntities.Length; i++)
            {
                var entity = migrationEntities[i];
                var isInSubscene = EntityManager.HasComponent<SceneSection>(entity);
                if (isInSubscene)
                {
                    var hashData = new NativeList<ulong>(Allocator.Temp);
                    var sceneSection = EntityManager.GetSharedComponent<SceneSection>(entity);
                    hashData.Add(sceneSection.SceneGUID.Value[0]);
                    hashData.Add(sceneSection.SceneGUID.Value[1]);
                    hashData.Add(sceneSection.SceneGUID.Value[2]);
                    hashData.Add(sceneSection.SceneGUID.Value[3]);

                    var transformAuthoring = EntityManager.GetComponentData<TransformAuthoring>(entity);
                    unsafe
                    {
                        var positionData = (byte*)&transformAuthoring.Position;
                        var rotationData = (byte*)&transformAuthoring.Rotation;
                        hashData.Add(Unity.Core.XXHash.Hash64(positionData, 3*sizeof(float)));
                        hashData.Add(Unity.Core.XXHash.Hash64(rotationData, 4*sizeof(float)));
                    }

                    ulong combinedComponentHash;
                    unsafe
                    {
                        combinedComponentHash = Unity.Core.XXHash.Hash64((byte*) hashData.GetUnsafeReadOnlyPtr(),
                            hashData.Length * sizeof(ulong));
                    }

                    if (!hashToEntity.ContainsKey(combinedComponentHash))
                        hashToEntity.Add(combinedComponentHash, entity);
                    else
                        Debug.LogError($"Two entities with host migrated non-ghost components have colliding IDs {EntityManager.GetName(entity)}. Change the transform to fix this (as it's generated from the transform data).");
                }
            }

            foreach (var htoe in hashToEntity)
            {
                EntityManager.AddComponentData(htoe.Value, new SceneEntityMigrationId() { Value = htoe.Key });
            }
        }
    }
}
