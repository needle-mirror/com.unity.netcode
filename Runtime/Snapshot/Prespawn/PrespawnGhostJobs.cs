using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// Build the ghost baseline for all the pre-spawned ghosts present in the world.
    /// The job will add to the entities a new buffer, PrespawGhostBaseline, witch will contains the
    /// a pre-serialized snapshot of the entity at the time the job run.
    ///
    /// NOTE: The serialization does not depend on component stripping (it is only dependent on the ghost type archetype
    /// serializer /omponent that is guarantee to be same on both client and server and that is handled by the GhostCollectionSystem)
    /// </summary>
    // baseline snapshot data layout:
    // -------------------------------------------------------------
    // [COMPONENT DATA][SIZE][PADDING (3UINT)][DYNAMIC BUFFER DATA]
    // -------------------------------------------------------------
    [BurstCompatible]
    [BurstCompile]
    public struct PrespawnGhostSerializer : IJobChunk
    {
        [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
        [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
        [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
        [ReadOnly] public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;
        [ReadOnly] public ComponentTypeHandle<GhostTypeComponent> ghostTypeComponentType;
        [ReadOnly] public EntityTypeHandle entityType;
        [ReadOnly] public StorageInfoFromEntity childEntityLookup;
        [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
        [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;
        [ReadOnly] public DynamicTypeList ghostChunkComponentTypes;
        public NativeArray<ulong> baselineHashes;
        [NativeDisableParallelForRestriction]
        public BufferTypeHandle<PrespawnGhostBaseline> prespawnBaseline;
        public Entity GhostCollectionSingleton;

        public unsafe void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var entities = chunk.GetNativeArray(entityType);
            var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
            var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
            var ghostTypeComponent = chunk.GetNativeArray(ghostTypeComponentType)[0];
            int ghostType;
            for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
            {
                if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                    break;
            }
            //the type has not been processed yet. There isn't much we can do about it
            if (ghostType >= GhostCollection.Length || ghostType >= GhostTypeCollection.Length)
            {
                UnityEngine.Debug.LogError("Cannot serialize prespawn ghost baselines. GhostCollection didn't correctly process some prefabs");
                return;
            }

            var buffersSize = new NativeArray<int>(chunk.Count, Allocator.Temp);
            var ghostChunkComponentTypesPtr = ghostChunkComponentTypes.GetData();
            var helper = new GhostSerializeHelper
            {
                serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                childEntityLookup = childEntityLookup,
                linkedEntityGroupType = linkedEntityGroupType,
                ghostChunkComponentTypesPtrLen = ghostChunkComponentTypes.Length
            };

            var typeData = GhostTypeCollection[ghostType];
            //collect the buffers size for each entity (and children)
            if (GhostTypeCollection[ghostType].NumBuffers > 0)
                helper.GatherBufferSize(chunk, 0, typeData, ref buffersSize);

            var snapshotSize = typeData.SnapshotSize;
            int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
            var snapshotBaseOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints * 4);
            var bufferAccessor = chunk.GetBufferAccessor(prespawnBaseline);
            for (int i = 0; i < entities.Length; ++i)
            {
                //Initialize the baseline buffer. This will contains the component data
                var baselineBuffer = bufferAccessor[i];
                //The first 4 bytes are the size of the dynamic data
                var dynamicDataCapacity = GhostCollectionSystem.SnapshotSizeAligned(sizeof(uint)) + buffersSize[i];
                baselineBuffer.ResizeUninitialized(snapshotSize + dynamicDataCapacity);
                var baselinePtr = baselineBuffer.GetUnsafePtr();
                UnsafeUtility.MemClear(baselinePtr, baselineBuffer.Length);

                helper.snapshotOffset = snapshotBaseOffset;
                helper.snapshotPtr = (byte*) baselinePtr;
                helper.snapshotDynamicPtr = (byte*) baselinePtr + snapshotSize;
                helper.dynamicSnapshotDataOffset = GhostCollectionSystem.SnapshotSizeAligned(sizeof(uint));
                helper.snapshotCapacity = snapshotSize;
                helper.dynamicSnapshotCapacity = baselineBuffer.Length - snapshotSize;
                helper.CopyEntityToSnapshot(chunk, i, typeData, GhostSerializeHelper.ClearOption.DontClear);

                // Compute the hash for that baseline
                baselineHashes[firstEntityIndex + i] =  Unity.Core.XXHash.Hash64((byte*) baselineBuffer.GetUnsafeReadOnlyPtr(), baselineBuffer.Length);
            }

            buffersSize.Dispose();
        }
    }

    /// <summary>
    ///  Strip from the prespawned ghost instances all the runtime components marked to be removed or disabled
    /// </summary>
    /// <remarks>
    /// This job is not burst compatbile since it uses TypeManager internal static members, that aren't SharedStatic.
    /// </remarks>
    struct PrespawnGhostStripComponentsJob : IJobChunk
    {
        [ReadOnly]public ComponentTypeHandle<GhostTypeComponent> ghostTypeHandle;
        [ReadOnly]public ComponentDataFromEntity<GhostPrefabMetaDataComponent> metaDataFromEntity;
        [ReadOnly]public BufferTypeHandle<LinkedEntityGroup> linkedEntityTypeHandle;
        [ReadOnly]public NativeHashMap<GhostTypeComponent, Entity> prefabFromType;
        public EntityCommandBuffer.ParallelWriter commandBuffer;
        public NetDebug netDebug;
        public bool server;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var ghostTypes = chunk.GetNativeArray(ghostTypeHandle);
            if (!prefabFromType.TryGetValue(ghostTypes[0], out var ghostPrefabEntity))
            {
                netDebug.LogError("Failed to look up ghost type");
                return;
            }
            // Modfy the entity to its proper version
            if (!metaDataFromEntity.HasComponent(ghostPrefabEntity))
            {
                netDebug.LogWarning($"Could not find a valid ghost prefab for the ghostType");
                return;
            }

            ref var ghostMetaData = ref metaDataFromEntity[ghostPrefabEntity].Value.Value;
            var linkedEntityBufferAccessor = chunk.GetBufferAccessor(linkedEntityTypeHandle);

            for (int index = 0; index < chunk.Count; ++index)
            {
                var linkedEntityGroup = linkedEntityBufferAccessor[index];
                if (server)
                {
                    for (int rm = 0; rm < ghostMetaData.RemoveOnServer.Length; ++rm)
                    {
                        var childIndexCompHashPair = ghostMetaData.RemoveOnServer[rm];
                        var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                        commandBuffer.RemoveComponent(chunkIndex, linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                    }
                }
                else
                {
                    for (int rm = 0; rm < ghostMetaData.RemoveOnClient.Length; ++rm)
                    {
                        var childIndexCompHashPair = ghostMetaData.RemoveOnClient[rm];
                        var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                        commandBuffer.RemoveComponent(chunkIndex,linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                    }
                    // FIXME: should disable instead of removing once we have a way of doing that without structural changes
                    if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Predicted)
                    {
                        for (int rm = 0; rm < ghostMetaData.DisableOnPredictedClient.Length; ++rm)
                        {
                            var childIndexCompHashPair = ghostMetaData.DisableOnPredictedClient[rm];
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                            commandBuffer.RemoveComponent(chunkIndex,linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                        }
                    }
                    else if (ghostMetaData.DefaultMode == GhostPrefabMetaData.GhostMode.Interpolated)
                    {
                        for (int rm = 0; rm < ghostMetaData.DisableOnInterpolatedClient.Length; ++rm)
                        {
                            var childIndexCompHashPair = ghostMetaData.DisableOnInterpolatedClient[rm];
                            var rmCompType = ComponentType.ReadWrite(TypeManager.GetTypeIndexFromStableTypeHash(childIndexCompHashPair.StableHash));
                            commandBuffer.RemoveComponent(chunkIndex,linkedEntityGroup[childIndexCompHashPair.EntityIndex].Value, rmCompType);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Assign to GhostComponent and GhostStateSystemComponent the ghost ids for all the prespawn ghosts.
    /// Also responsible to popule the SpawnedGhostMapping lists with all the spawned ghosts
    /// </summary>
    [BurstCompatible]
    [BurstCompile]
    struct AssignPrespawnGhostIdJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle entityType;
        [ReadOnly] public ComponentTypeHandle<PreSpawnedGhostIndex> prespawnIdType;
        [NativeDisableParallelForRestriction]
        public ComponentTypeHandle<GhostComponent> ghostComponentType;
        [NativeDisableParallelForRestriction]
        public ComponentTypeHandle<GhostSystemStateComponent> ghostStateTypeHandle;
        [NativeDisableParallelForRestriction]
        public NativeArray<SpawnedGhostMapping> spawnedGhosts;
        public int startGhostId;
        public NetDebug netDebug;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            var entities = chunk.GetNativeArray(entityType);
            var preSpawnedIds = chunk.GetNativeArray(prespawnIdType);
            var ghostComponents = chunk.GetNativeArray(ghostComponentType);
            var ghostStates = chunk.GetNativeArray(ghostStateTypeHandle);

            for (int index = 0; index < chunk.Count; ++index)
            {
                var entity = entities[index];
                // Check if this entity has already been handled
                if (ghostComponents[index].ghostId != 0)
                {
                    netDebug.LogWarning($"{entity} already has ghostId={ghostComponents[index].ghostId} prespawn= {preSpawnedIds[index].Value}");
                    continue;
                }
                //Special encoding for prespawnId (sort of "namespace").
                var ghostId = PrespawnHelper.MakePrespawGhostId(preSpawnedIds[index].Value + startGhostId);
                if (ghostStates.IsCreated && ghostStates.Length > 0)
                    ghostStates[index] = new GhostSystemStateComponent {ghostId = ghostId, despawnTick = 0, spawnTick = 0};

                spawnedGhosts[firstEntityIndex + index] = new SpawnedGhostMapping
                {
                    ghost = new SpawnedGhost {ghostId = ghostId, spawnTick = 0}, entity = entity
                };
                // GhostType -1 is a special case for prespawned ghosts which is converted to a proper ghost id in the send / receive systems
                // once the ghost ids are known
                // Pre-spawned uses spawnTick = 0, if there is a reference to a ghost and it has spawnTick 0 the ref is always resolved
                // This works because there despawns are high priority and we never create pre-spawned ghosts after connection
                ghostComponents[index] = new GhostComponent {ghostId = ghostId, ghostType = -1, spawnTick = 0};
            }
        }
    }
}
