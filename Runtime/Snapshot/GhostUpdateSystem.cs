using Unity.Entities;
using Unity.Collections;
using Unity.Burst;
using Unity.Networking.Transport.Utilities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public unsafe class GhostUpdateSystem : SystemBase
    {
        private EntityQuery m_ChildEntityQuery;
        private NativeHashMap<Entity, EntityChunkLookup> m_ChildEntityLookup;

        // There will be burst/IL problems with using generic job structs, so we're
        // laying out each job size type here manually
        [BurstCompile]
        struct GhostUpdateJob32 : IJobChunk
        {
            public DynamicTypeList32 List;
            public UpdateJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct GhostUpdateJob64 : IJobChunk
        {
            public DynamicTypeList64 List;
            public UpdateJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }
        [BurstCompile]
        struct GhostUpdateJob128 : IJobChunk
        {
            public DynamicTypeList128 List;
            public UpdateJob Job;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                Job.Execute(chunk, chunkIndex, firstEntityIndex, List.GetData(), List.Length);
            }
        }

        [BurstCompile]
        struct UpdateJob
        {
            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionPrefabSerializer> GhostTypeCollection;
            [NativeDisableContainerSafetyRestriction] private DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;

            [ReadOnly] public NativeHashMap<SpawnedGhost, Entity> GhostMap;
    #if UNITY_EDITOR || DEVELOPMENT_BUILD
            [NativeDisableParallelForRestriction] public NativeArray<uint> minMaxSnapshotTick;
    #endif
    #pragma warning disable 649
            [NativeSetThreadIndex] public int ThreadIndex;
    #pragma warning restore 649
            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostType;
            [ReadOnly] public ComponentTypeHandle<SnapshotData> ghostSnapshotDataType;
            [ReadOnly] public BufferTypeHandle<SnapshotDataBuffer> ghostSnapshotDataBufferType;
            [ReadOnly] public BufferTypeHandle<SnapshotDynamicDataBuffer> ghostSnapshotDynamicDataBufferType;

            public uint interpolatedTargetTick;
            public float interpolatedTargetTickFraction;
            public uint predictedTargetTick;

            [NativeDisableParallelForRestriction] public NativeArray<uint> minPredictedTick;
            public ComponentTypeHandle<PredictedGhostComponent> predictedGhostComponentType;
            public uint lastPredictedTick;
            public uint lastInterpolatedTick;

            [ReadOnly] public NativeHashMap<Entity, EntityChunkLookup> childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            public uint predictionStateBackupTick;
            [ReadOnly] public NativeHashMap<ArchetypeChunk, System.IntPtr> predictionStateBackup;
            [ReadOnly] public EntityTypeHandle entityType;
            public int ghostOwnerId;
            public uint MaxExtrapolationTicks;
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];
                GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];

                bool predicted = chunk.Has(predictedGhostComponentType);
                uint targetTick = predicted ? predictedTargetTick : interpolatedTargetTick;
                float targetTickFraction = predicted ? 1.0f : interpolatedTargetTickFraction;

                var deserializerState = new GhostDeserializerState
                {
                    GhostMap = GhostMap,
                    GhostOwner = ghostOwnerId,
                    SendToOwner = SendToOwnerType.All
                };
                var ghostComponents = chunk.GetNativeArray(ghostType);
                int ghostTypeId = ghostComponents.GetFirstGhostTypeId(out var firstGhost);
                if (ghostTypeId < 0)
                    return;
                var typeData = GhostTypeCollection[ghostTypeId];
                var ghostSnapshotDataArray = chunk.GetNativeArray(ghostSnapshotDataType);
                var ghostSnapshotDataBufferArray = chunk.GetBufferAccessor(ghostSnapshotDataBufferType);
                var ghostSnapshotDynamicBufferArray = chunk.GetBufferAccessor(ghostSnapshotDynamicDataBufferType);

                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                int snapshotDataAtTickSize = UnsafeUtility.SizeOf<SnapshotData.DataAtTick>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var minMaxOffset = ThreadIndex * (JobsUtility.CacheLineSize/4);
#endif
                var dataAtTick = new NativeArray<SnapshotData.DataAtTick>(ghostComponents.Length, Allocator.Temp);
                var entityRange = new NativeList<int2>(ghostComponents.Length, Allocator.Temp);
                int2 nextRange = default;
                var predictedGhostComponentArray = chunk.GetNativeArray(predictedGhostComponentType);
                bool canBeStatic = typeData.StaticOptimization;
                // Find the ranges of entities which have data to apply, store the data to apply in an array while doing so
                for (int ent = firstGhost; ent < ghostComponents.Length; ++ent)
                {
                    // Pre spawned ghosts might not have the ghost type set yet - in that case we need to skip them until the GHostReceiveSystem has assigned the ghost type
                    if (ghostComponents[firstGhost].ghostType != ghostTypeId)
                    {
                        if (nextRange.y != 0)
                            entityRange.Add(nextRange);
                        nextRange = default;
                        continue;
                    }
                    var snapshotDataBuffer = ghostSnapshotDataBufferArray[ent];
                    var ghostSnapshotData = ghostSnapshotDataArray[ent];
                    var latestTick = ghostSnapshotData.GetLatestTick(snapshotDataBuffer);
                    bool isStatic = canBeStatic && ghostSnapshotData.WasLatestTickZeroChange(snapshotDataBuffer, changeMaskUints);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (latestTick != 0 && !isStatic)
                    {
                        if (minMaxSnapshotTick[minMaxOffset] == 0 || SequenceHelpers.IsNewer(minMaxSnapshotTick[minMaxOffset], latestTick))
                            minMaxSnapshotTick[minMaxOffset] = latestTick;
                        if (minMaxSnapshotTick[minMaxOffset + 1] == 0 || SequenceHelpers.IsNewer(latestTick, minMaxSnapshotTick[minMaxOffset + 1]))
                            minMaxSnapshotTick[minMaxOffset + 1] = latestTick;
                    }
#endif
                    if (ghostSnapshotData.GetDataAtTick(targetTick, typeData.PredictionOwnerOffset, targetTickFraction, snapshotDataBuffer, out var data, MaxExtrapolationTicks))
                    {
                        if (predicted)
                        {
                            // TODO: is this the right way to handle this?
                            data.InterpolationFactor = 0;
                            var snapshotTick = *(uint*)data.SnapshotBefore;
                            var predictedData = predictedGhostComponentArray[ent];
                            // We want to contiue prediction from the last full tick we predicted last time
                            var predictionStartTick = predictionStateBackupTick;
                            // If there is no history, try to use the tick where we left off last time, will only be a valid tick if we ended with a full prediction tick as opposed to a fractional one
                            if (predictionStartTick == 0)
                                predictionStartTick = lastPredictedTick;
                            // If we do not have a backup or we got more data since last time we run from the tick we have snapshot data for
                            if (predictionStartTick == 0 || predictedData.AppliedTick != snapshotTick)
                                predictionStartTick = snapshotTick;
                            // If we have newer or equally new data in the
                            else if (!SequenceHelpers.IsNewer(predictionStartTick, snapshotTick))
                                predictionStartTick = snapshotTick;

                            // If we want to continue prediction, and this is not the currently applied prediction state we must restore the state from the backup
                            if (predictionStartTick != snapshotTick && predictionStartTick != lastPredictedTick)
                            {
                                // If we cannot restore the backup and continue prediction we roll back and resimulate
                                if (!RestorePredictionBackup(chunk, ent, typeData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength))
                                    predictionStartTick = snapshotTick;
                            }


                            if (minPredictedTick[ThreadIndex] == 0 || SequenceHelpers.IsNewer(minPredictedTick[ThreadIndex], predictionStartTick))
                                minPredictedTick[ThreadIndex] = predictionStartTick;

                            if (predictionStartTick != snapshotTick)
                            {
                                if (nextRange.y != 0)
                                    entityRange.Add(nextRange);
                                nextRange = default;
                            }
                            else
                            {
                                predictedData.AppliedTick = snapshotTick;
                                if (nextRange.y == 0)
                                    nextRange.x = ent;
                                nextRange.y = ent+1;
                            }
                            predictedData.PredictionStartTick = predictionStartTick;
                            predictedGhostComponentArray[ent] = predictedData;
                        }
                        else
                        {
                            // If this snapshot is static, and the data for the latest tick was applied during last interpolation update, we can just skip copying data
                            if (isStatic && !SequenceHelpers.IsNewer(latestTick, lastInterpolatedTick))
                            {
                                if (nextRange.y != 0)
                                    entityRange.Add(nextRange);
                                nextRange = default;
                            }
                            else
                            {
                                if (nextRange.y == 0)
                                    nextRange.x = ent;
                                nextRange.y = ent+1;
                            }
                        }
                        dataAtTick[ent] = data;
                    }
                    else if (nextRange.y != 0)
                    {
                        entityRange.Add(nextRange);
                        nextRange = default;
                        if (predicted)
                        {
                            var predictionStartTick = predictionStateBackupTick;
                            if (predictionStateBackupTick != lastPredictedTick)
                            {
                                // Try to back up the thing
                                if (!RestorePredictionBackup(chunk, ent, typeData, ghostChunkComponentTypesPtr, ghostChunkComponentTypesLength))
                                    predictionStartTick = 0;
                            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            if (predictionStartTick == 0)
                                throw new System.InvalidOperationException("Trying to predict a ghost without having a state to roll back to");
#endif
                        }
                    }
                }
                if (nextRange.y != 0)
                    entityRange.Add(nextRange);

                var requiredSendMask = predicted ? GhostComponentSerializer.SendMask.Predicted : GhostComponentSerializer.SendMask.Interpolated;
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;

                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    var snapshotSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                        ? GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize)
                        : GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                    if (!chunk.Has(ghostChunkComponentTypesPtr[compIdx]) || (GhostComponentIndex[typeData.FirstComponent + comp].SendMask&requiredSendMask) == 0)
                    {
                        snapshotOffset += snapshotSize;
                        continue;
                    }
                    var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                        deserializerState.SendToOwner = GhostComponentCollection[serializerIdx].SendToOwner;
                        for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                        {
                            var range = entityRange[rangeIdx];
                            var snapshotData = (byte*)dataAtTick.GetUnsafeReadOnlyPtr();
                            snapshotData += snapshotDataAtTickSize * range.x;
                            GhostComponentCollection[serializerIdx].CopyFromSnapshot.Ptr.Invoke((System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr)snapshotData, snapshotOffset, snapshotDataAtTickSize, (System.IntPtr)(compData + range.x*compSize), compSize, range.y-range.x);
                        }
                        snapshotOffset += snapshotSize;
                    }
                    else
                    {
                        var bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        var dynamicDataSize = GhostComponentCollection[serializerIdx].SnapshotSize;
                        var maskBits = GhostComponentCollection[serializerIdx].ChangeMaskBits;
                        deserializerState.SendToOwner = GhostComponentCollection[serializerIdx].SendToOwner;
                        for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                        {
                            var range = entityRange[rangeIdx];
                            for (int ent = range.x; ent < range.y; ++ent)
                            {
                                //Compute the required owner mask for the buffers and skip the copyfromsnapshot. The check must be done
                                //for each entity.
                                if (dataAtTick[ent].GhostOwner > 0)
                                {
                                    var requiredOwnerMask = dataAtTick[ent].GhostOwner == deserializerState.GhostOwner
                                        ? SendToOwnerType.SendToOwner
                                        : SendToOwnerType.SendToNonOwner;
                                    if ((GhostComponentCollection[serializerIdx].SendToOwner & requiredOwnerMask) == 0)
                                        continue;
                                }

                                var dynamicDataBuffer = ghostSnapshotDynamicBufferArray[ent];
                                var dynamicDataAtTick = SetupDynamicDataAtTick(dataAtTick[ent], snapshotOffset, dynamicDataSize, maskBits, dynamicDataBuffer, out var bufLen);
                                bufferAccessor.ResizeUninitialized(ent, bufLen);
                                var componentData = (System.IntPtr)bufferAccessor.GetUnsafePtr(ent);
                                GhostComponentCollection[serializerIdx].CopyFromSnapshot.Ptr.Invoke(
                                    (System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState),
                                    (System.IntPtr) UnsafeUtility.AddressOf(ref dynamicDataAtTick), 0, dynamicDataSize,
                                    componentData, compSize, bufLen);
                            }
                        }
                        snapshotOffset += snapshotSize;
                    }
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif

                        var snapshotSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize)
                            : GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        if ((GhostComponentIndex[typeData.FirstComponent + comp].SendMask & requiredSendMask) == 0)
                        {
                            snapshotOffset += snapshotSize;
                            continue;
                        }
                        var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            deserializerState.SendToOwner = GhostComponentCollection[serializerIdx].SendToOwner;
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                for (int ent = range.x; ent < range.y; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    if (!childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value, out var childChunk))
                                        continue;
                                    if (!childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                        continue;
                                    var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                    var snapshotData = (byte*)dataAtTick.GetUnsafeReadOnlyPtr();
                                    snapshotData += snapshotDataAtTickSize * ent;
                                    GhostComponentCollection[serializerIdx].CopyFromSnapshot.Ptr.Invoke((System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState), (System.IntPtr)snapshotData, snapshotOffset, snapshotDataAtTickSize, (System.IntPtr)(compData + childChunk.index*compSize), compSize, 1);
                                }
                            }
                            snapshotOffset += snapshotSize;
                        }
                        else
                        {
                            var dynamicDataSize = GhostComponentCollection[serializerIdx].SnapshotSize;
                            var maskBits = GhostComponentCollection[serializerIdx].ChangeMaskBits;
                            deserializerState.SendToOwner = GhostComponentCollection[serializerIdx].SendToOwner;
                            for (var rangeIdx = 0; rangeIdx < entityRange.Length; ++rangeIdx)
                            {
                                var range = entityRange[rangeIdx];
                                for (int ent = range.x; ent < range.y; ++ent)
                                {
                                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                    if (!childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value, out var childChunk))
                                        continue;
                                    if (!childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                        continue;

                                    //Compute the required owner mask for the buffers and skip the copyfromsnapshot. The check must be done
                                    if (dataAtTick[ent].GhostOwner > 0)
                                    {
                                        var requiredOwnerMask = dataAtTick[ent].GhostOwner == deserializerState.GhostOwner
                                            ? SendToOwnerType.SendToOwner
                                            : SendToOwnerType.SendToNonOwner;
                                        if ((deserializerState.SendToOwner & requiredOwnerMask) == 0)
                                            continue;
                                    }

                                    var dynamicDataBuffer = ghostSnapshotDynamicBufferArray[ent];
                                    var dynamicDataAtTick = SetupDynamicDataAtTick(dataAtTick[ent], snapshotOffset, dynamicDataSize, maskBits, dynamicDataBuffer, out var bufLen);
                                    var bufferAccessor = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                    bufferAccessor.ResizeUninitialized(childChunk.index, bufLen);
                                    var componentData = (System.IntPtr)bufferAccessor.GetUnsafePtr(childChunk.index);
                                    GhostComponentCollection[serializerIdx].CopyFromSnapshot.Ptr.Invoke(
                                        (System.IntPtr)UnsafeUtility.AddressOf(ref deserializerState),
                                        (System.IntPtr) UnsafeUtility.AddressOf(ref dynamicDataAtTick), 0, dynamicDataSize,
                                        componentData, compSize, bufLen);
                                }
                            }
                            snapshotOffset += snapshotSize;
                        }
                    }
                }
            }

            SnapshotData.DataAtTick SetupDynamicDataAtTick(in SnapshotData.DataAtTick dataAtTick,
                int snapshotOffset, int snapshotSize, int maskBits, in DynamicBuffer<SnapshotDynamicDataBuffer> ghostSnapshotDynamicBuffer, out int buffernLen)
            {
                // Retrieve from the snapshot the buffer information and
                var snapshotData = (int*)(dataAtTick.SnapshotBefore + snapshotOffset);
                var bufLen = snapshotData[0];
                var dynamicDataOffset = snapshotData[1];
                //The dynamic snapshot data is associated with the root entity not the children
                var dynamicSnapshotDataBeforePtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr((byte*)ghostSnapshotDynamicBuffer.GetUnsafeReadOnlyPtr(),
                    dataAtTick.BeforeIdx, ghostSnapshotDynamicBuffer.Length);
                //var dynamicSnapshotDataCapacity = SnapshotDynamicBuffersHelper.GetDynamicDataCapacity(SnapshotDynamicBuffersHelper.GetHeaderSize(),ghostSnapshotDynamicBuffer.Length);
                var dynamicMaskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(maskBits, bufLen);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if ((dynamicDataOffset + bufLen*snapshotSize) > ghostSnapshotDynamicBuffer.Length)
                    throw new System.InvalidOperationException("Overflow reading data from dynamic snapshot memory buffer");
#endif
                //Copy into the buffer the snapshot data. Use a temporary DataTick to pass some information to the serializer function.
                //No need to use a DataAtTick per element (would be overkill)
                buffernLen = bufLen;
                return new SnapshotData.DataAtTick
                {
                    SnapshotBefore = (System.IntPtr)(dynamicSnapshotDataBeforePtr + dynamicDataOffset + dynamicMaskSize),
                    SnapshotAfter = (System.IntPtr)(dynamicSnapshotDataBeforePtr + dynamicDataOffset + dynamicMaskSize),
                    //No interpolation factor is necessary
                    InterpolationFactor = 0.0f,
                    Tick = dataAtTick.Tick
                };
            }
            bool RestorePredictionBackup(ArchetypeChunk chunk, int ent, in GhostCollectionPrefabSerializer typeData, DynamicComponentTypeHandle* ghostChunkComponentTypesPtr, int ghostChunkComponentTypesLength)
            {
                // Try to get the backup state
                if (!predictionStateBackup.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                    return false;

                // Verify that the backup is for the correct entity
                Entity* entities = PredictionBackupState.GetEntities(state);
                var entity = chunk.GetNativeArray(entityType)[ent];
                if (entity != entities[ent])
                    return false;

                int baseOffset = typeData.FirstComponent;
                const GhostComponentSerializer.SendMask requiredSendMask = GhostComponentSerializer.SendMask.Predicted;

                byte* dataPtr = PredictionBackupState.GetData(state);
                //bufferBackupDataPtr is null in case there are no buffer for that ghost type
                byte* bufferBackupDataPtr = PredictionBackupState.GetBufferDataPtr(state);
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                        continue;

                    var compSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                        ? GhostSystemConstants.DynamicBufferComponentSnapshotSize
                        : GhostComponentCollection[serializerIdx].ComponentSize;
                    if (!chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                    {
                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                        continue;
                    }

                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                        GhostComponentCollection[serializerIdx].RestoreFromBackup.Ptr.Invoke((System.IntPtr)(compData + ent * compSize), (System.IntPtr)(dataPtr + ent * compSize));
                    }
                    else
                    {
                        var backupData = (int*)(dataPtr + ent * compSize);
                        var bufLen = backupData[0];
                        var bufOffset = backupData[1];
                        var elemSize = GhostComponentCollection[serializerIdx].ComponentSize;
                        var bufferDataPtr = bufferBackupDataPtr + bufOffset;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if ((bufOffset + bufLen*elemSize) > PredictionBackupState.GetBufferDataCapacity(state))
                            throw new System.InvalidOperationException("Overflow reading data from dynamic snapshot memory buffer");
#endif
                        var bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);

                        //IMPORTANT NOTE: The RestoreFromBackup restore only the serialized fields for a given struct.
                        //Differently from the component counterpart, when the dynamic snapshot buffer get resized the memory is not
                        //cleared (for performance reason) and some portion of the data could be left "uninitialized" with random values
                        //in case some of the element fields does not have a [GhostField] annotation.
                        //For such a reason we enforced a rule: BufferElementData MUST have all fields annotated with the GhostFieldAttribute.
                        //This solve the problem and we might relax that condition later.

                        bufferAccessor.ResizeUninitialized(ent, bufLen);
                        var bufferPointer = (byte*)bufferAccessor.GetUnsafePtr(ent);

                        for(int i=0;i<bufLen;++i)
                        {
                            GhostComponentCollection[serializerIdx].RestoreFromBackup.Ptr.Invoke((System.IntPtr)(bufferPointer), (System.IntPtr)(bufferDataPtr));
                            bufferPointer += elemSize;
                            bufferDataPtr += elemSize;
                        }
                    }
                    dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[baseOffset + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[baseOffset + comp].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        if (compIdx >= ghostChunkComponentTypesLength)
                            throw new System.InvalidOperationException("Component index out of range");
#endif
                        if ((GhostComponentIndex[baseOffset + comp].SendMask&requiredSendMask) == 0)
                            continue;

                        var compSize = GhostComponentCollection[serializerIdx].ComponentType.IsBuffer
                            ? GhostSystemConstants.DynamicBufferComponentSnapshotSize
                            : GhostComponentCollection[serializerIdx].ComponentSize;

                        var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                        if (childEntityLookup.TryGetValue(linkedEntityGroup[GhostComponentIndex[baseOffset + comp].EntityIndex].Value, out var childChunk) &&
                            childChunk.chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        {
                            if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
                                var compData = (byte*)childChunk.chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                GhostComponentCollection[serializerIdx].RestoreFromBackup.Ptr.Invoke((System.IntPtr)(compData + childChunk.index * compSize), (System.IntPtr)(dataPtr + ent * compSize));
                            }
                            else
                            {
                                var backupData = (int*)(dataPtr + ent * compSize);
                                var bufLen = backupData[0];
                                var bufOffset = backupData[1];
                                var elemSize = GhostComponentCollection[serializerIdx].ComponentSize;
                                var bufferDataPtr = bufferBackupDataPtr + bufOffset;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                if ((bufOffset + bufLen*elemSize) > PredictionBackupState.GetBufferDataCapacity(state))
                                    throw new System.InvalidOperationException("Overflow reading data from dynamic snapshot memory buffer");
#endif
                                var bufferAccessor = childChunk.chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                bufferAccessor.ResizeUninitialized(childChunk.index, bufLen);
                                var bufferPointer = (byte*)bufferAccessor.GetUnsafePtr(childChunk.index);
                                for(int i=0;i<bufLen;++i)
                                {
                                    GhostComponentCollection[serializerIdx].RestoreFromBackup.Ptr.Invoke((System.IntPtr)(bufferPointer), (System.IntPtr)(bufferDataPtr));
                                    bufferPointer += elemSize;
                                    bufferDataPtr += elemSize;
                                }
                            }
                        }
                        dataPtr = PredictionBackupState.GetNextData(dataPtr, compSize, chunk.Capacity);
                    }
                }

                return true;
            }
        }
        private ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        private GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        private EntityQuery m_ghostQuery;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private NativeArray<uint> m_ghostSnapshotTickMinMax;
        private GhostStatsCollectionSystem m_GhostStatsCollectionSystem;
#endif
        private GhostReceiveSystem m_GhostReceiveSystem;
        private uint m_LastPredictedTick;
        private uint m_LastInterpolatedTick;

        private GhostPredictionHistorySystem m_GhostPredictionHistorySystem;
        protected override void OnCreate()
        {
            m_GhostPredictionHistorySystem = World.GetOrCreateSystem<GhostPredictionHistorySystem>();

            m_GhostReceiveSystem = World.GetOrCreateSystem<GhostReceiveSystem>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_ghostSnapshotTickMinMax = new NativeArray<uint>(JobsUtility.MaxJobThreadCount * JobsUtility.CacheLineSize/4, Allocator.Persistent);
            m_GhostStatsCollectionSystem = World.GetOrCreateSystem<GhostStatsCollectionSystem>();
#endif
            m_ClientSimulationSystemGroup = World.GetOrCreateSystem<ClientSimulationSystemGroup>();
            m_GhostPredictionSystemGroup = World.GetOrCreateSystem<GhostPredictionSystemGroup>();
            m_ghostQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new []{
                    ComponentType.ReadWrite<SnapshotDataBuffer>(),
                    ComponentType.ReadOnly<SnapshotData>(),
                    ComponentType.ReadOnly<GhostComponent>(),
                },
            });

            m_ChildEntityQuery = GetEntityQuery(ComponentType.ReadOnly<GhostChildEntityComponent>());
            m_ChildEntityLookup = new NativeHashMap<Entity, EntityChunkLookup>(1024, Allocator.Persistent);

            RequireSingletonForUpdate<NetworkStreamInGame>();
            RequireSingletonForUpdate<GhostCollection>();
        }
        protected override void OnDestroy()
        {
            m_ChildEntityLookup.Dispose();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            m_ghostSnapshotTickMinMax.Dispose();
#endif
        }
        protected override void OnUpdate()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Gather the min/max age stats
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            for (int i = 1; i < JobsUtility.MaxJobThreadCount; ++i)
            {
                if (m_ghostSnapshotTickMinMax[intsPerCacheLine*i] != 0 &&
                    (m_ghostSnapshotTickMinMax[0] == 0 ||
                    SequenceHelpers.IsNewer(m_ghostSnapshotTickMinMax[0], m_ghostSnapshotTickMinMax[intsPerCacheLine*i])))
                    m_ghostSnapshotTickMinMax[0] = m_ghostSnapshotTickMinMax[intsPerCacheLine*i];
                if (m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1] != 0 &&
                    (m_ghostSnapshotTickMinMax[1] == 0 ||
                    SequenceHelpers.IsNewer(m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1], m_ghostSnapshotTickMinMax[1])))
                    m_ghostSnapshotTickMinMax[1] = m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1];
                m_ghostSnapshotTickMinMax[intsPerCacheLine*i] = 0;
                m_ghostSnapshotTickMinMax[intsPerCacheLine*i+1] = 0;
            }
            // Pass the min and max to stats collection
            m_GhostStatsCollectionSystem.SetSnapshotTick(m_ghostSnapshotTickMinMax[0], m_ghostSnapshotTickMinMax[1]);
            m_ghostSnapshotTickMinMax[0] = 0;
            m_ghostSnapshotTickMinMax[1] = 0;
#endif
            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            if (HasSingleton<ClientTickRate>())
                clientTickRate = GetSingleton<ClientTickRate>();

            m_ChildEntityLookup.Clear();
            var childCount = m_ChildEntityQuery.CalculateEntityCountWithoutFiltering();
            if (childCount > m_ChildEntityLookup.Capacity)
                m_ChildEntityLookup.Capacity = childCount;
            var buildChildJob = new BuildChildEntityLookupJob
            {
                entityType = GetEntityTypeHandle(),
                childEntityLookup = m_ChildEntityLookup.AsParallelWriter()
            };
            Dependency = buildChildJob.ScheduleParallel(m_ChildEntityQuery, Dependency);
            var updateJob = new UpdateJob
            {
                GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),

                GhostMap = m_GhostReceiveSystem.SpawnedGhostEntityMap,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                minMaxSnapshotTick = m_ghostSnapshotTickMinMax,
#endif

                interpolatedTargetTick = m_ClientSimulationSystemGroup.InterpolationTick,
                interpolatedTargetTickFraction = m_ClientSimulationSystemGroup.InterpolationTickFraction,

                predictedTargetTick = m_ClientSimulationSystemGroup.ServerTick,
                minPredictedTick = m_GhostPredictionSystemGroup.OldestPredictedTick,
                predictedGhostComponentType = GetComponentTypeHandle<PredictedGhostComponent>(),
                lastPredictedTick = m_LastPredictedTick,
                lastInterpolatedTick = m_LastInterpolatedTick,

                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                ghostSnapshotDataType = GetComponentTypeHandle<SnapshotData>(true),
                ghostSnapshotDataBufferType = GetBufferTypeHandle<SnapshotDataBuffer>(true),
                ghostSnapshotDynamicDataBufferType = GetBufferTypeHandle<SnapshotDynamicDataBuffer>(true),
                childEntityLookup = m_ChildEntityLookup,
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(true),

                predictionStateBackupTick = m_GhostPredictionHistorySystem.LastBackupTick,
                predictionStateBackup = m_GhostPredictionHistorySystem.PredictionState,
                entityType = GetEntityTypeHandle(),
                ghostOwnerId = GetSingleton<NetworkIdComponent>().Value,
                MaxExtrapolationTicks = clientTickRate.MaxExtrapolationTimeSimTicks
            };
            Dependency = JobHandle.CombineDependencies(Dependency, m_GhostPredictionHistorySystem.PredictionStateWriteJobHandle, m_GhostReceiveSystem.LastGhostMapWriter);
            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(updateJob.GhostCollectionSingleton);
            var listLength = ghostComponentCollection.Length;
            if (listLength <= 32)
            {
                var dynamicListJob = new GhostUpdateJob32 {Job = updateJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, false, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_ghostQuery, Dependency);
            }
            else if (listLength <= 64)
            {
                var dynamicListJob = new GhostUpdateJob64 {Job = updateJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, false, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_ghostQuery, Dependency);
            }
            else if (listLength <= 128)
            {
                var dynamicListJob = new GhostUpdateJob128 {Job = updateJob};
                DynamicTypeList.PopulateList(this, ghostComponentCollection, false, ref dynamicListJob.List);
                Dependency = dynamicListJob.ScheduleParallel(m_ghostQuery, Dependency);
            }
            else
                throw new System.InvalidOperationException(
                    $"Too many ghost component types present in project, limit is {DynamicTypeList.MaxCapacity} types. This is any struct which has a field marked with GhostField attribute.");
            m_LastPredictedTick = m_ClientSimulationSystemGroup.ServerTick;
            if (m_ClientSimulationSystemGroup.ServerTickFraction < 1)
                m_LastPredictedTick = 0;

            // If the interpolation target for this frame was received we can update which the latest fully applied interpolation tick is
            var ack = GetSingleton<NetworkSnapshotAckComponent>();
            if (!SequenceHelpers.IsNewer(m_ClientSimulationSystemGroup.InterpolationTick, ack.LastReceivedSnapshotByLocal))
            {
                m_LastInterpolatedTick = m_ClientSimulationSystemGroup.InterpolationTick;
                // Make sure it is the last full interpolated tick. It is only used to see if a static ghost already has the latest state applied
                if (m_ClientSimulationSystemGroup.InterpolationTickFraction < 1)
                    --m_LastInterpolatedTick;
            }

            m_GhostReceiveSystem.LastGhostMapWriter = Dependency;
            m_GhostPredictionSystemGroup.AddPredictedTickWriter(Dependency);
            m_GhostPredictionHistorySystem.AddPredictionStateReader(Dependency);
        }
    }
}
