using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    static class PrespawnHelper
    {
        public const uint PrespawnGhostIdBase = 0x80000000;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public int MakePrespawGhostId(int ghostId)
        {
            return (int) (PrespawnGhostIdBase | ghostId);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public bool IsPrespawGhostId(int ghostId)
        {
            return (ghostId & PrespawnGhostIdBase) != 0;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public bool IsRuntimeSpawnedGhost(int ghostId)
        {
            return (ghostId & PrespawnGhostIdBase) == 0;
        }

        //return a valid index if a reserved range that contains the ghost id is present, otherwise 0
        static public int GhostIdRangeIndex(ref this DynamicBuffer<PrespawnGhostIdRange> ranges , long ghostId)
        {
            ghostId &= ~PrespawnHelper.PrespawnGhostIdBase;
            for (int i = 0; i < ranges.Length; ++i)
            {
                if(ranges[i].Reserved != 0 &&
                   ghostId >= ranges[i].FirstGhostId &&
                   ghostId < ranges[i].FirstGhostId + ranges[i].Count)
                    return i;
            }
            return -1;
        }

        static public Entity CreatePrespawnSceneListGhostPrefab(World world, EntityManager entityManager)
        {
            var e = entityManager.CreateEntity();
            entityManager.AddComponent<Prefab>(e);
            entityManager.AddComponent<GhostComponent>(e);
            entityManager.AddComponent<GhostTypeComponent>(e);
            entityManager.AddBuffer<PrespawnSceneLoaded>(e);
            var linkedEntities = entityManager.AddBuffer<LinkedEntityGroup>(e);
            linkedEntities.Add(e);

            //I need an unique identifier and should not clash with any loaded prefab.
            var ghostType = new GhostTypeComponent
            {
                guid0 = 0,
                guid1 = 0,
                guid2 = 101,
                guid3 = 0,
            };
            entityManager.SetComponentData(e, ghostType);

            if (world.GetExistingSystem<ServerSimulationSystemGroup>() != null)
            {
                entityManager.AddSharedComponentData(e, new SharedGhostTypeComponent
                {
                    SharedValue = ghostType
                });
            }
            else
            {
                entityManager.AddComponent<SnapshotData>(e);
                entityManager.AddComponent<SnapshotDataBuffer>(e);
                entityManager.AddComponent<SnapshotDynamicDataBuffer>(e);
            }

            using (var builder = new BlobBuilder(Allocator.Temp))
            {
                ref var root = ref builder.ConstructRoot<GhostPrefabMetaData>();
                root.Importance = 1;
                root.SupportedModes = GhostPrefabMetaData.GhostMode.Both;
                root.DefaultMode = GhostPrefabMetaData.GhostMode.Interpolated;
                root.StaticOptimization = false;
                var numServerComponents = builder.Allocate(ref root.NumServerComponentsPerEntity, 1);
                var blobServerComponents = builder.Allocate(ref root.ServerComponentList, 1);
                numServerComponents[0] = 1;
                blobServerComponents[0].StableHash = TypeManager.GetTypeInfo<PrespawnSceneLoaded>().StableTypeHash;
                blobServerComponents[0].Variant = 0;
                blobServerComponents[0].SendMaskOverride = -1;
                blobServerComponents[0].SendToChildEntityOverride = -1;
                builder.Allocate(ref root.RemoveOnServer, 0);
                builder.Allocate(ref root.RemoveOnClient, 0);
                builder.Allocate(ref root.DisableOnInterpolatedClient, 0);
                builder.Allocate(ref root.DisableOnPredictedClient, 0);
                var blobReference = builder.CreateBlobAssetReference<GhostPrefabMetaData>(Allocator.Persistent);

                entityManager.AddComponentData(e, new GhostPrefabMetaDataComponent
                {
                    Value = blobReference
                });
            }

            return e;
        }

        static public void DisposeSceneListPrefab(Entity prefabEntity, EntityManager entityManager)
        {
            if(!entityManager.HasComponent<PrespawnSceneLoaded>(prefabEntity))
                return;
            if(!entityManager.HasComponent<GhostPrefabMetaDataComponent>(prefabEntity))
                return;
            var ghostMetadataRef = entityManager.GetComponentData<GhostPrefabMetaDataComponent>(prefabEntity);
            ghostMetadataRef.Value.Dispose();
            entityManager.DestroyEntity(prefabEntity);
        }

        public struct GhostIdInterval: IComparable<GhostIdInterval>
        {
            public int Begin;
            public int End;

            public GhostIdInterval(int begin, int end)
            {
                Begin = begin;
                End = end;
            }
            //Simplified sorting for non overlapping intervals
            public int CompareTo(GhostIdInterval other)
            {
                return Begin.CompareTo(other.Begin);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static public void PopulateSceneHashLookupTable(EntityQuery query, EntityManager entityManager, NativeHashMap<int, ulong> hashMap)
        {
            var chunks = query.CreateArchetypeChunkArray(Allocator.Temp);
            var sharedComponentType = entityManager.GetSharedComponentTypeHandle<SubSceneGhostComponentHash>();
            hashMap.Clear();
            for(int i=0;i<chunks.Length;++i)
            {
                var sharedComponentIndex = chunks[i].GetSharedComponentIndex(sharedComponentType);
                var sharedComponentValue = entityManager.GetSharedComponentData<SubSceneGhostComponentHash>(sharedComponentIndex);
                hashMap.TryAdd(sharedComponentIndex, sharedComponentValue.Value);
            };
        }


        static public void UpdatePrespawnAckSceneMap(ref ConnectionStateData connectionState,
            Entity PrespawnSceneLoadedEntity,
            in BufferFromEntity<PrespawnSectionAck> prespawnAckFromEntity,
            in BufferFromEntity<PrespawnSceneLoaded> prespawnSceneLoadedFromEntity)
        {
            var connectionEntity = connectionState.Entity;
            var clientPrespawnSceneMap = connectionState.AckedPrespawnSceneMap;
            var prespawnSceneLoaded = prespawnSceneLoadedFromEntity[PrespawnSceneLoadedEntity];
            ref var newLoadedRanges = ref connectionState.NewLoadedPrespawnRanges;
            newLoadedRanges.Clear();
            if (!prespawnAckFromEntity.HasComponent(connectionEntity))
            {
                clientPrespawnSceneMap.Clear();
                return;
            }
            var prespawnAck = prespawnAckFromEntity[connectionEntity];
            var newMap = new NativeHashMap<ulong, int>(prespawnAck.Length, Allocator.Temp);
            for (int i = 0; i < prespawnAck.Length; ++i)
            {
                if(!clientPrespawnSceneMap.ContainsKey(prespawnAck[i].SceneHash))
                    newMap.Add(prespawnAck[i].SceneHash, 1);
                else
                    newMap.Add(prespawnAck[i].SceneHash, 0);
            }
            clientPrespawnSceneMap.Clear();
            for (int i = 0; i < prespawnSceneLoaded.Length; ++i)
            {
                if (newMap.TryGetValue(prespawnSceneLoaded[i].SubSceneHash, out var present))
                {
                    clientPrespawnSceneMap.TryAdd(prespawnSceneLoaded[i].SubSceneHash, 1);
                    //Brand new
                    if(present == 1)
                    {
                        newLoadedRanges.Add(new GhostIdInterval(
                            PrespawnHelper.MakePrespawGhostId(prespawnSceneLoaded[i].FirstGhostId),
                            PrespawnHelper.MakePrespawGhostId(prespawnSceneLoaded[i].FirstGhostId + prespawnSceneLoaded[i].PrespawnCount - 1)));
                    }
                }
            }
            newLoadedRanges.Sort();
        }
    }

    public static class PrespawnSubsceneElementExtensions
    {
        internal static int IndexOf(this DynamicBuffer<PrespawnSceneLoaded> subsceneElements, ulong hash)
        {
            for (int i = 0; i < subsceneElements.Length; ++i)
            {
                if (subsceneElements[i].SubSceneHash == hash)
                    return i;
            }

            return -1;
        }

        internal static int IndexOf(this DynamicBuffer<PrespawnSectionAck> subsceneElements, ulong hash)
        {
            for (int i = 0; i < subsceneElements.Length; ++i)
            {
                if (subsceneElements[i].SceneHash == hash)
                    return i;
            }

            return -1;
        }
        internal static bool RemoveScene(this DynamicBuffer<PrespawnSectionAck> subsceneElements, ulong hash)
        {
            for (int i = 0; i < subsceneElements.Length; ++i)
            {
                if (subsceneElements[i].SceneHash == hash)
                {
                    subsceneElements.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }
    }

}
