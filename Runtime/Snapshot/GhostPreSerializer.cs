using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System;
using System.Diagnostics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.NetCode
{

    /// <summary>
    /// Adding this component to a ghost will trigger pre-serialization for that ghost.
    /// Pre-serialization means that part of the serialization process happens before
    /// the regular serialization pass and can be done once for all connections.
    /// This can save some CPU time if the the ghost will generally be sent to more than
    /// one player every frame and it contains complex serialization (serialized data on
    /// child entities or buffers).
    /// </summary>
    public struct PreSerializedGhost : IComponentData
    {}

    internal unsafe struct SnapshotPreSerializeData
    {
        public void* Data;
        public int DynamicSize;
        public int Capacity;
        public int DynamicCapacity;
    }
    internal unsafe struct GhostPreSerializer : IDisposable
    {
        public NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData> SnapshotData;
        private NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData> PreviousSnapshotData;
        private EntityQuery m_Query;

        public GhostPreSerializer(EntityQuery query)
        {
            SnapshotData = new NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData>(1024, Allocator.Persistent);
            PreviousSnapshotData = new NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData>(1024, Allocator.Persistent);
            m_Query = query;
        }
        void CleanupSnapshotData()
        {
            // FIXME: this could be a job too
            var chunks = PreviousSnapshotData.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < chunks.Length; ++i)
            {
                if (!SnapshotData.ContainsKey(chunks[i]))
                {
                    // Free the data stored for this key in PreviousSnapshotData
                    PreviousSnapshotData.TryGetValue(chunks[i], out var snapshot);
                    UnsafeUtility.Free(snapshot.Data, Allocator.Persistent);
                }
            }
            PreviousSnapshotData.Clear();
            var temp = SnapshotData;
            SnapshotData = PreviousSnapshotData;
            PreviousSnapshotData = temp;
        }
        public void Dispose()
        {
            CleanupSnapshotData();
            var snapshots = PreviousSnapshotData.GetValueArray(Allocator.Temp);
            for (int i = 0; i < snapshots.Length; ++i)
                UnsafeUtility.Free(snapshots[i].Data, Allocator.Persistent);
            PreviousSnapshotData.Dispose();
            SnapshotData.Dispose();
        }

        public JobHandle Schedule(JobHandle dependency,
            BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity,
            BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity,
            BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity,
            Entity GhostCollectionSingleton,
            BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity,
            BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType,
            StorageInfoFromEntity childEntityLookup,
            ComponentTypeHandle<GhostComponent> ghostComponentType,
            ComponentTypeHandle<GhostTypeComponent> ghostTypeComponentType,
            EntityTypeHandle entityType,
            ComponentDataFromEntity<GhostComponent> ghostFromEntity,
            NetDebug netDebug,
            uint currentTick,
            SystemBase system,
            DynamicBuffer<GhostCollectionComponentType> ghostCollection)
        {
            CleanupSnapshotData();
            var job = new GhostPreSerializeJob
            {
                SnapshotData = SnapshotData.AsParallelWriter(),
                PreviousSnapshotData = PreviousSnapshotData,
                GhostComponentCollectionFromEntity = GhostComponentCollectionFromEntity,
                GhostTypeCollectionFromEntity = GhostTypeCollectionFromEntity,
                GhostComponentIndexFromEntity = GhostComponentIndexFromEntity,
                GhostCollectionSingleton = GhostCollectionSingleton,
                GhostCollectionFromEntity = GhostCollectionFromEntity,

                linkedEntityGroupType = linkedEntityGroupType,
                childEntityLookup = childEntityLookup,
                ghostComponentType = ghostComponentType,
                ghostTypeComponentType = ghostTypeComponentType,
                entityType = entityType,
                ghostFromEntity = ghostFromEntity,
                netDebug = netDebug,
                currentTick = currentTick
            };
            DynamicTypeList.PopulateList(system, ghostCollection, true, ref job.List);
            return job.ScheduleParallelByRef(m_Query, dependency);
        }

        [BurstCompile]
        struct GhostPreSerializeJob : IJobEntityBatch
        {
            public NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData>.ParallelWriter SnapshotData;
            [ReadOnly] public NativeHashMap<ArchetypeChunk, SnapshotPreSerializeData> PreviousSnapshotData;


            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;

            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefab> GhostCollectionFromEntity;

            public DynamicTypeList List;


            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;
            [ReadOnly] public StorageInfoFromEntity childEntityLookup;

            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostComponentType;
            [ReadOnly] public ComponentTypeHandle<GhostTypeComponent> ghostTypeComponentType;
            [ReadOnly] public EntityTypeHandle entityType;

            [ReadOnly] public ComponentDataFromEntity<GhostComponent> ghostFromEntity;


            public NetDebug netDebug;

            public uint currentTick;

            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = List.GetData();
                var ghosts = chunk.GetNativeArray(ghostComponentType);
                // Find the ghost type for this chunk
                var ghostType = ghosts[0].ghostType;
                // Pre spawned ghosts might not have a proper ghost type index yet, we calculate it here for pre spawns
                if (ghostType < 0)
                {
                    var GhostCollection = GhostCollectionFromEntity[GhostCollectionSingleton];
                    var ghostTypeComponent = chunk.GetNativeArray(ghostTypeComponentType)[0];
                    for (ghostType = 0; ghostType < GhostCollection.Length; ++ghostType)
                    {
                        if (GhostCollection[ghostType].GhostType == ghostTypeComponent)
                            break;
                    }
                }
                if (ghostType < 0 || ghostType >= GhostTypeCollection.Length)
                {
                    netDebug.LogError("Could not find ghost type in the collection");
                    return;
                }
                var typeData = GhostTypeCollection[ghostType];
                int dynamicDataCapacity = 0;
                int dynamicDataHeaderSize = 0;
                int snapshotSize = typeData.SnapshotSize;

                int changeMaskUints = GhostCollectionSystem.ChangeMaskArraySizeInUInts(typeData.ChangeMaskBits);
                int snapshotOffset = GhostCollectionSystem.SnapshotSizeAligned(4 + changeMaskUints*4);
                var helper = new GhostSerializeHelper
                {
                    serializerState = new GhostSerializerState { GhostFromEntity = ghostFromEntity },
                    ghostChunkComponentTypesPtr = ghostChunkComponentTypesPtr,
                    GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton],
                    GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton],
                    childEntityLookup = childEntityLookup,
                    linkedEntityGroupType = linkedEntityGroupType,
                    ghostChunkComponentTypesPtrLen = List.Length,
                };

                if (typeData.NumBuffers != 0)
                {
                    // figure out how much data is required for the buffer dynamic data
                    dynamicDataCapacity = helper.GatherBufferSize(chunk, 0, typeData);
                    dynamicDataHeaderSize = GhostChunkSerializationState.GetDynamicDataHeaderSize(chunk.Capacity);
                }
                int snapshotDataCapacity = typeData.SnapshotSize * chunk.Capacity;
                // Determine the required allocation size
                if (!PreviousSnapshotData.TryGetValue(chunk, out var snapshot) || snapshot.Capacity != snapshotDataCapacity || snapshot.DynamicCapacity < dynamicDataCapacity)
                {
                    // Allocate a new snapshot
                    if (snapshot.Data != null)
                    {
                        UnsafeUtility.Free(snapshot.Data, Allocator.Persistent);
                    }
                    snapshot.Capacity = snapshotDataCapacity;
                    // Round up to an even number of kb
                    snapshot.DynamicCapacity = (dynamicDataCapacity + 1023) & (~1023);
                    snapshot.Data = UnsafeUtility.Malloc(snapshot.Capacity + snapshot.DynamicCapacity, 16, Allocator.Persistent);
                }
                snapshot.DynamicSize = dynamicDataCapacity;
                // Add to the new snapshot data lookup
                if (!SnapshotData.TryAdd(chunk, snapshot))
                {
                    netDebug.LogError("Could not register snapshot data for pre-serialization");
                    UnsafeUtility.Free(snapshot.Data, Allocator.Persistent);
                    return;
                }

                typeData.profilerMarker.Begin();
                // Go through all entities and serialize the data to the snapshot store
                helper.snapshotPtr = (byte*)snapshot.Data;
                helper.snapshotOffset = snapshotOffset;
                helper.snapshotCapacity = snapshotSize;
                if (typeData.NumBuffers != 0)
                {
                    //This require some explanation.
                    // The data layout for the pre-serialized ghost snapshot looks like this:
                    //
                    //   snapshot.Capacity   snapshot.DynamicCapacity
                    // [  SNAPSHOT DATA  ][      DYNAMIC DATA       ]
                    //
                    // The dynamic data will be copied/re-located by the GhostChunkSerializer
                    // inside the chunk DynamicSnapshotBuffer, right after the header.
                    //
                    //   chunk snapshot cap      chunk dynamic capacity
                    // [  SNAPSHOT DATA     ][ HEADER][    DYNAMIC DATA   ]
                    //
                    // Because of that the relative offset that we store in the snapshot data
                    // indicating where the dynamicbuffer contents start from, must be offset by the dynamic header size.
                    //
                    //  [SNAPSHOT DATA]
                    // ..  Buffer ...                Chunk Dynamic Data
                    //   Len, Offset                  [Header][CONTENTS]
                    //    X     |                                 |
                    //          |_________________________________|
                    //
                    // Becausee the pre-serialized data is actually stored right after the snapshot but
                    // the helper will write in memory at address snapshotDynamicPtr + dynamicSnapshotDataOffset,
                    // we are offsetting the start position of the buffer back by the header capacity
                    helper.snapshotDynamicPtr = (byte*)snapshot.Data + snapshot.Capacity - dynamicDataHeaderSize;
                    helper.dynamicSnapshotDataOffset = dynamicDataHeaderSize;
                    //The max capacity should also be larger (like it contains also the header) so all math make sense
                    helper.dynamicSnapshotCapacity = snapshot.DynamicCapacity + dynamicDataHeaderSize;
                }
                helper.CopyChunkToSnapshot(chunk, typeData);
                for (int ent = 0; ent < chunk.Count; ++ent)
                {
                    *(uint*)((byte*)snapshot.Data + snapshotSize * ent) = currentTick;
                }
                typeData.profilerMarker.End();
            }
        }
    }
}