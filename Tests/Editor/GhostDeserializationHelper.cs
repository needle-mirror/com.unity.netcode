using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Unity.NetCode.LowLevel.Unsafe
{
    //TODO: require some further generalization but then we can expose and use it to collect all the deserialization
    //logics and helpers here, so they are not sparse anymore as a first refactor step.
    [BurstCompile]
    unsafe struct GhostDeserializeHelper
    {
        public byte* snapshotPtr;
        public byte* snapshotDynamicPtr;
        public int snapshotOffset;
        public int snapshotSize;
        public int dynamicSnapshotCapacity;
        public GhostCollectionPrefabSerializer ghostPrefabSerializer;
        //Constant data
        [ReadOnly] public DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
        [ReadOnly] public DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
        [ReadOnly] public EntityStorageInfoLookup childEntityLookup;
        [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
        public DynamicTypeList ghostChunkComponentTypes;
        public GhostDeserializerState deserializerState;

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckValidDynamicSnapshotOffset(in GhostComponentSerializer.State serializer, int offset, int bufferLen)
        {
            if ((offset + serializer.SnapshotSize * bufferLen) > dynamicSnapshotCapacity)
                throw new InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
        }
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckValidSnapshotOffset(int compSnapshotSize)
        {
            if ((snapshotOffset + compSnapshotSize) > snapshotSize)
                throw new InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
        }

        public GhostDeserializeHelper(ref SystemState state, Entity ghostCollection,
            Entity ghostEntity, int ghostType)
        {
            var ghostCollectionIndex = state.EntityManager.GetBuffer<GhostCollectionComponentIndex>(ghostCollection);
            var ghostSerializers = state.EntityManager.GetBuffer<GhostComponentSerializer.State>(ghostCollection);
            var ghostPrefabs = state.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollection);
            var typeData = ghostPrefabs[ghostType];

            var dynamicBuffer = state.EntityManager.GetBuffer<SnapshotDynamicDataBuffer>(ghostEntity);
            var snapshot = (byte*)state.EntityManager.GetBuffer<SnapshotDataBuffer>(ghostEntity).GetUnsafePtr();
            var slotCapacity = (int)SnapshotDynamicBuffersHelper.GetDynamicDataCapacity(SnapshotDynamicBuffersHelper.GetHeaderSize(),
                dynamicBuffer.Length);
            var maskSize = GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
            maskSize += GhostComponentSerializer.ChangeMaskArraySizeInUInts(typeData.EnableableBits);

            ghostPrefabSerializer = typeData;
            snapshotPtr = snapshot;
            snapshotDynamicPtr = SnapshotDynamicBuffersHelper.GetDynamicDataPtr((byte*)dynamicBuffer.GetUnsafePtr(), 0, dynamicBuffer.Length);
            snapshotOffset = GhostComponentSerializer.SnapshotSizeAligned(sizeof(uint) + maskSize * sizeof(uint));
            snapshotSize = typeData.SnapshotSize;
            dynamicSnapshotCapacity = slotCapacity;
            GhostComponentIndex = ghostCollectionIndex;
            GhostComponentCollection = ghostSerializers;
            childEntityLookup = state.GetEntityStorageInfoLookup();
            linkedEntityGroupType = state.EntityManager.GetBufferTypeHandle<LinkedEntityGroup>(true);
            deserializerState = new GhostDeserializerState
            {
                SendToOwner = SendToOwnerType.All
            };
            ghostChunkComponentTypes = default;
        }

        [BurstCompile]
        public void CopySnapshotToEntity(in EntityStorageInfo storageInfo, int slot = 0,
            float interpolationFactor = 0f, SendToOwnerType ownerSendMask = SendToOwnerType.All)
        {
            int numBaseComponents = ghostPrefabSerializer.NumComponents - ghostPrefabSerializer.NumChildComponents;
            var ghostChunkComponentTypesPtr = ghostChunkComponentTypes.GetData();
            SnapshotData.DataAtTick dataAtTick = new SnapshotData.DataAtTick();
            dataAtTick.SnapshotAfter = (IntPtr)(this.snapshotPtr + snapshotSize * slot);
            dataAtTick.SnapshotBefore = (IntPtr)(this.snapshotPtr + snapshotSize * slot);
            dataAtTick.InterpolationFactor = interpolationFactor;
            dataAtTick.RequiredOwnerSendMask = ownerSendMask;
            for (int comp = 0; comp < numBaseComponents; ++comp)
            {
                int compIdx = GhostComponentIndex[ghostPrefabSerializer.FirstComponent + comp].ComponentIndex;
                int serializerIdx = GhostComponentIndex[ghostPrefabSerializer.FirstComponent + comp].SerializerIndex;
                var typeHandle = ghostChunkComponentTypesPtr[compIdx];
                ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                var sizeInSnapshot = GhostComponentSerializer.SizeInSnapshot(ghostSerializer);
                if (storageInfo.Chunk.Has(ref typeHandle))
                {
                    deserializerState.SendToOwner = ghostSerializer.SendToOwner;
                    if (ghostSerializer.ComponentType.IsBuffer)
                    {
                        CopyBufferFromSnapshot(storageInfo.Chunk, storageInfo.IndexInChunk, ref typeHandle, ghostSerializer, ref dataAtTick);
                    }
                    else
                    {
                        CopyComponentFromSnapshot(storageInfo.Chunk, storageInfo.IndexInChunk, ref typeHandle, ghostSerializer, ref dataAtTick);
                    }
                }
                snapshotOffset += sizeInSnapshot;
            }
            if (ghostPrefabSerializer.NumChildComponents > 0)
            {
                var linkedEntityGroupAccessor = storageInfo.Chunk.GetBufferAccessor(ref linkedEntityGroupType);
                var linkedEntityGroup = linkedEntityGroupAccessor[storageInfo.IndexInChunk];
                for (int comp = numBaseComponents; comp < ghostPrefabSerializer.NumComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[ghostPrefabSerializer.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[ghostPrefabSerializer.FirstComponent + comp].SerializerIndex;
                    var typeHandle = ghostChunkComponentTypesPtr[compIdx];
                    ref readonly var ghostSerializer = ref GhostComponentCollection.ElementAtRO(serializerIdx);
                    var sizeInSnapshot = GhostComponentSerializer.SizeInSnapshot(ghostSerializer);
                    var childEnt = linkedEntityGroup[GhostComponentIndex[ghostPrefabSerializer.FirstComponent + comp].EntityIndex].Value;
                    if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ref typeHandle))
                    {
                        if (ghostSerializer.ComponentType.IsBuffer)
                        {
                            CopyBufferFromSnapshot(childChunk.Chunk, childChunk.IndexInChunk, ref typeHandle, ghostSerializer, ref dataAtTick);
                        }
                        else
                        {
                            CopyComponentFromSnapshot(childChunk.Chunk,childChunk.IndexInChunk, ref typeHandle, ghostSerializer, ref dataAtTick);
                        }
                    }
                    snapshotOffset += sizeInSnapshot;
                }
            }
        }

        private void CopyBufferFromSnapshot(ArchetypeChunk chunk, int ent,
            ref DynamicComponentTypeHandle typeHandle,
            in GhostComponentSerializer.State serializer, ref SnapshotData.DataAtTick dataAtTick)
        {
            var compSize = serializer.ComponentSize;
            var bufData = chunk.GetUntypedBufferAccessor(ref typeHandle);
            var snapshotData = (int*) (snapshotPtr + snapshotOffset);
            var bufferLen = snapshotData[0];
            var offset = snapshotData[1];
            bufData.ResizeUninitialized(ent, bufferLen);
            var bufferPointer = bufData.GetUnsafePtr(ent);
            var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(serializer.ChangeMaskBits, bufferLen);
            CheckValidDynamicSnapshotOffset(serializer, offset + maskSize, bufferLen);
            var bufferAtTick = dataAtTick;
            bufferAtTick.SnapshotBefore = (IntPtr)snapshotDynamicPtr + maskSize;
            bufferAtTick.SnapshotAfter = (IntPtr)snapshotDynamicPtr + maskSize;
            bufferAtTick.InterpolationFactor = 0f;
            serializer.CopyFromSnapshot.Invoke(
                (IntPtr)UnsafeUtility.AddressOf(ref deserializerState),
                GhostComponentSerializer.IntPtrCast(ref bufferAtTick), offset, serializer.SnapshotSize,
                (IntPtr)bufferPointer, compSize, bufferLen);
        }

        private void CopyComponentFromSnapshot(ArchetypeChunk chunk, int ent,
            ref DynamicComponentTypeHandle typeHandle,
            in GhostComponentSerializer.State serializer, ref SnapshotData.DataAtTick dataAtTick)
        {
            if(!serializer.HasGhostFields) return;
            var compSize = serializer.ComponentSize;
            var compData = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize).GetUnsafeReadOnlyPtr();
            CheckValidSnapshotOffset(serializer.SnapshotSize);
            serializer.CopyFromSnapshot.Invoke((IntPtr) UnsafeUtility.AddressOf(ref deserializerState),
                GhostComponentSerializer.IntPtrCast(ref dataAtTick), snapshotOffset, snapshotSize, (IntPtr) (compData + ent * compSize), compSize, 1);
        }
    }
}
