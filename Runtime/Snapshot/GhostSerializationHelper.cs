using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.LowLevel.Unsafe;

namespace Unity.NetCode
{
    namespace LowLevel.Unsafe
    {
        // Internal serializer helper. Hold a bunch of serializer related data together
        unsafe struct GhostSerializeHelper
        {
            public byte* snapshotPtr;
            public byte* snapshotDynamicPtr;
            public int snapshotOffset;
            public int dynamicSnapshotDataOffset;
            public int snapshotCapacity;
            public int dynamicSnapshotCapacity;
            public DynamicComponentTypeHandle typeHandle;

            //Constant data
            [ReadOnly] public DynamicComponentTypeHandle* ghostChunkComponentTypesPtr;
            [ReadOnly] public DynamicBuffer<GhostCollectionComponentIndex> GhostComponentIndex;
            [ReadOnly] public DynamicBuffer<GhostComponentSerializer.State> GhostComponentCollection;
            [ReadOnly] public StorageInfoFromEntity childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            public int ghostChunkComponentTypesPtrLen;
            public GhostSerializerState serializerState;

            public enum ClearOption
            {
                Clear = 0,
                DontClear
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckValidComponentIndex(int compIdx)
            {
                if (compIdx >= ghostChunkComponentTypesPtrLen)
                    throw new InvalidOperationException($"Component index out of range");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckValidDynamicSnapshotOffset(in GhostComponentSerializer.State serializer, int maskSize, int bufferLen)
            {
                if ((dynamicSnapshotDataOffset + serializer.SnapshotSize * bufferLen) > dynamicSnapshotCapacity)
                    throw new InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void CheckValidSnapshotOffset(int compSnapshotSize)
            {
                if ((snapshotOffset + compSnapshotSize) > snapshotCapacity)
                    throw new InvalidOperationException("Overflow writing data to dynamic snapshot memory buffer");
            }

            [BurstCompatible]
            [BurstCompile]
            private void CopyComponentToSnapshot(ArchetypeChunk chunk, int ent, in GhostComponentSerializer.State serializer)
            {
                var compSize = serializer.ComponentSize;
                var compData = (byte*) chunk.GetDynamicComponentDataArrayReinterpret<byte>(typeHandle, compSize).GetUnsafeReadOnlyPtr();
                CheckValidSnapshotOffset(serializer.SnapshotSize);
                serializer.CopyToSnapshot.Ptr.Invoke((IntPtr) UnsafeUtility.AddressOf(ref serializerState),
                    (IntPtr) snapshotPtr, snapshotOffset, snapshotCapacity, (IntPtr) (compData + ent * compSize), compSize, 1);
            }

            [BurstCompatible]
            [BurstCompile]
            private void CopyBufferToSnapshot(ArchetypeChunk chunk, int ent, in GhostComponentSerializer.State serializer)
            {
                var compSize = serializer.ComponentSize;
                var bufData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                // Collect the buffer data to serialize by storing pointers, offset and size.
                var bufferPointer = (IntPtr) bufData.GetUnsafeReadOnlyPtrAndLength(ent, out var bufferLen);
                var snapshotData = (uint*) (snapshotPtr + snapshotOffset);
                snapshotData[0] = (uint) bufferLen;
                snapshotData[1] = (uint) dynamicSnapshotDataOffset;
                //Serialize the buffer contents
                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(serializer.ChangeMaskBits, bufferLen);
                CheckValidDynamicSnapshotOffset(serializer, maskSize, bufferLen);
                serializer.CopyToSnapshot.Ptr.Invoke(
                    (IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                    (IntPtr)(snapshotDynamicPtr + maskSize), dynamicSnapshotDataOffset, serializer.SnapshotSize,
                    bufferPointer, compSize, bufferLen);
                dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + serializer.SnapshotSize * bufferLen);
            }

            [BurstCompatible]
            [BurstCompile]
            public void CopyEntityToSnapshot(ArchetypeChunk chunk, int ent, in GhostCollectionPrefabSerializer typeData, ClearOption option = ClearOption.Clear)
            {
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    CheckValidComponentIndex(compIdx);
                    typeHandle = ghostChunkComponentTypesPtr[compIdx];
                    var sizeInSnapshot = GhostComponentSerializer.SizeInSnapshot(GhostComponentCollection[serializerIdx]);
                    if (chunk.Has(typeHandle))
                    {
                        if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            CopyBufferToSnapshot(chunk, ent, GhostComponentCollection[serializerIdx]);
                        }
                        else
                        {
                            CopyComponentToSnapshot(chunk, ent, GhostComponentCollection[serializerIdx]);
                        }
                    }
                    else if(option == ClearOption.Clear)
                    {
                        if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            *(uint*)(snapshotPtr + snapshotOffset) = (uint)0;
                            *(uint*)(snapshotPtr + snapshotOffset + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                        }
                        else
                        {
                            for (int i = 0; i < GhostComponentCollection[serializerIdx].SnapshotSize / 4; ++i)
                            {
                                ((uint*) (snapshotPtr + snapshotOffset))[i] = 0;
                            }
                        }
                    }
                    snapshotOffset += sizeInSnapshot;
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        CheckValidComponentIndex(compIdx);
                        typeHandle = ghostChunkComponentTypesPtr[compIdx];
                        var sizeInSnapshot = GhostComponentSerializer.SizeInSnapshot(GhostComponentCollection[serializerIdx]);
                        var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                        if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(typeHandle))
                        {
                            if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
                                CopyBufferToSnapshot(childChunk.Chunk, childChunk.IndexInChunk, GhostComponentCollection[serializerIdx]);
                            }
                            else
                            {
                                CopyComponentToSnapshot(childChunk.Chunk,childChunk.IndexInChunk, GhostComponentCollection[serializerIdx]);
                            }
                        }
                        else if(option == ClearOption.Clear)
                        {
                            if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            {
                                *(uint*)(snapshotPtr + snapshotOffset) = (uint)0;
                                *(uint*)(snapshotPtr + snapshotOffset + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                            }
                            else
                            {
                                for (int i = 0; i < GhostComponentCollection[serializerIdx].SnapshotSize / 4; ++i)
                                {
                                    ((uint*) (snapshotPtr + snapshotOffset))[i] = 0;
                                }
                            }
                        }
                        snapshotOffset += sizeInSnapshot;
                    }
                }
                //Update the dynamic data total size
                if(typeData.NumBuffers > 0)
                    ((uint*)snapshotDynamicPtr)[0] = (uint)(dynamicSnapshotDataOffset - GhostCollectionSystem.SnapshotSizeAligned(sizeof(uint)));
            }

            [BurstCompatible]
            [BurstCompile]
            public void CopyChunkToSnapshot(ArchetypeChunk chunk, in GhostCollectionPrefabSerializer typeData)
            {
                // Loop through all components and call the serialize method which will write the snapshot data and serialize the entities to the temporary stream
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    CheckValidComponentIndex(compIdx);
                    var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                    //Don't access the data but always increment the offset by the component SnapshotSize.
                    //Otherwise, the next serialized component would technically copy the data in the wrong memory slot
                    //It might still work in some cases but if this snapshot is then part of the history and used for
                    //interpolated data we might get incorrect results
                    if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        {
                            var dynamicDataSize = GhostComponentCollection[serializerIdx].SnapshotSize;
                            var bufData = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                            for (int ent = 0; ent < chunk.Count; ++ent)
                            {
                                var compData = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(ent, out var len);
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, len);
                                //Set the elements count and the buffer content offset inside the dynamic data history buffer
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity) = (uint)len;
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                                GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                    (IntPtr)snapshotDynamicPtr, dynamicSnapshotDataOffset + maskSize, dynamicDataSize, (IntPtr)compData, compSize, len);

                                dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
                            }
                        }
                        else
                        {
                            for (int ent = 0; ent < chunk.Count; ++ent)
                            {
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity) = (uint)0;
                                *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                            }
                        }

                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                    }
                    else
                    {
                        if (chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        {
                            var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                            GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                (IntPtr)snapshotPtr, snapshotOffset, snapshotCapacity, (IntPtr)compData, compSize, chunk.Count);
                        }
                        else
                        {
                            for (int ent = 0; ent < chunk.Count; ++ent)
                                UnsafeUtility.MemClear(snapshotPtr + snapshotOffset + ent*snapshotCapacity, GhostComponentCollection[serializerIdx].SnapshotSize);
                        }

                        snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                    }
                }
                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        CheckValidComponentIndex(compIdx);
                        var compSize = GhostComponentCollection[serializerIdx].ComponentSize;
                        if(GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                        {
                            var dynamicDataSize = GhostComponentCollection[serializerIdx].SnapshotSize;
                            for (int ent = 0; ent < chunk.Count; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var bufData = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                    var compData = (byte*)bufData.GetUnsafeReadOnlyPtrAndLength(childChunk.IndexInChunk, out var len);

                                    var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, len);
                                    //Set the elements count and the buffer content offset inside the dynamic data history buffer
                                    *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity) = (uint)len;
                                    *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                                    GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                        (IntPtr)snapshotDynamicPtr, dynamicSnapshotDataOffset + maskSize, dynamicDataSize, (IntPtr)compData, compSize, len);

                                    dynamicSnapshotDataOffset += GhostCollectionSystem.SnapshotSizeAligned(maskSize + dynamicDataSize * len);
                                }
                                else
                                {
                                    *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity) = (uint)0;
                                    *(uint*)(snapshotPtr + snapshotOffset + ent * snapshotCapacity + sizeof(int)) = (uint)(dynamicSnapshotDataOffset);
                                }
                            }

                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostSystemConstants.DynamicBufferComponentSnapshotSize);
                        }
                        else
                        {
                            for (int ent = 0; ent < chunk.Count; ++ent)
                            {
                                var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                                var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                                //We can skip here, becase the memory buffer offset is computed using the start-end entity indices
                                if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                                {
                                    var compData = (byte*)childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[compIdx], compSize).GetUnsafeReadOnlyPtr();
                                    compData += childChunk.IndexInChunk * compSize;
                                    // TODO: would batching be faster?
                                    GhostComponentCollection[serializerIdx].CopyToSnapshot.Ptr.Invoke((IntPtr)UnsafeUtility.AddressOf(ref serializerState),
                                        (IntPtr)snapshotPtr + ent*snapshotCapacity, snapshotOffset, snapshotCapacity, (IntPtr)compData, compSize, 1);
                                }
                                else
                                {
                                    UnsafeUtility.MemClear(snapshotPtr + snapshotOffset + ent*snapshotCapacity, GhostComponentCollection[serializerIdx].SnapshotSize);
                                }
                            }

                            snapshotOffset += GhostCollectionSystem.SnapshotSizeAligned(GhostComponentCollection[serializerIdx].SnapshotSize);
                        }
                    }
                }
            }

            [BurstCompatible]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GatherBufferSize(ArchetypeChunk chunk, int startIndex, GhostCollectionPrefabSerializer typeData)
            {
                var emptyArray = new NativeArray<int>();
                return GatherBufferSize(chunk, startIndex, typeData, ref emptyArray);
            }

            [BurstCompatible]
            [BurstCompile]
            public int GatherBufferSize(ArchetypeChunk chunk, int startIndex, GhostCollectionPrefabSerializer typeData, ref NativeArray<int> buffersSize)
            {
                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int totalSize = 0;
                for (int comp = 0; comp < numBaseComponents; ++comp)
                {
                    int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                    if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer || !chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                        continue;

                    for (int ent = startIndex; ent < chunk.Count; ++ent)
                    {
                        var bufferAccessor = chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                        var bufferLen = bufferAccessor.GetBufferLength(ent);
                        var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, bufferLen);
                        var size = GhostCollectionSystem.SnapshotSizeAligned(maskSize + bufferLen * GhostComponentCollection[serializerIdx].SnapshotSize);
                        if(buffersSize.IsCreated)
                            buffersSize[ent] += size;
                        totalSize += size;
                    }
                }

                if (typeData.NumChildComponents > 0)
                {
                    var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                    for (int comp = numBaseComponents; comp < typeData.NumComponents; ++comp)
                    {
                        int compIdx = GhostComponentIndex[typeData.FirstComponent + comp].ComponentIndex;
                        int serializerIdx = GhostComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                        CheckValidComponentIndex(compIdx);
                        if (!GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                            continue;

                        for (int ent = startIndex; ent < chunk.Count; ++ent)
                        {
                            var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                            var childEnt = linkedEntityGroup[GhostComponentIndex[typeData.FirstComponent + comp].EntityIndex].Value;
                            if (childEntityLookup.TryGetValue(childEnt, out var childChunk) && childChunk.Chunk.Has(ghostChunkComponentTypesPtr[compIdx]))
                            {
                                var bufferAccessor = childChunk.Chunk.GetUntypedBufferAccessor(ref ghostChunkComponentTypesPtr[compIdx]);
                                var bufferLen = bufferAccessor.GetBufferLength(childChunk.IndexInChunk);
                                var maskSize = SnapshotDynamicBuffersHelper.GetDynamicDataChangeMaskSize(GhostComponentCollection[serializerIdx].ChangeMaskBits, bufferLen);
                                var size = GhostCollectionSystem.SnapshotSizeAligned(maskSize + bufferLen * GhostComponentCollection[serializerIdx].SnapshotSize);
                                if(buffersSize.IsCreated)
                                    buffersSize[ent] += size;
                                totalSize += size;
                            }
                        }
                    }
                }
                return totalSize;
            }
        }
    }
}