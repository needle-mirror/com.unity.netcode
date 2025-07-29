using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode.LowLevel.StateSave;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.HostMigration
{
    /// <summary>
    /// Host migration class used to access the host migration system, like getting the host migration data blob and
    /// deploying migration data to a new world.
    /// </summary>
    public static class HostMigrationData
    {
        internal struct Data
        {
            // Flag for if a ghost type has had the server-only components scanned and added to the below hashmap
            public NativeList<int> ServerOnlyComponentsFlag;
            // Cache for the server-only components which are present in each ghost type
            public NativeHashMap<int, NativeList<ComponentType>> ServerOnlyComponentsPerGhostType;
        }

        /// <summary>
        /// Get the host migration data which has been collected by the host migration system. There
        /// is no limit enforced on the total size of the migration data.
        /// </summary>
        /// <param name="fromWorld">The world where the migration data is stored</param>
        /// <param name="toData">Destination list to copy the data, this will be resized if it is too small to store all the data</param>
        public static void Get(World fromWorld, ref NativeList<byte> toData)
        {
            var hostMigrationDataQuery = fromWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationStorage>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationStorage>();
            var hostData = hostMigrationData.ValueRO.HostDataBlob;
            var ghostData = hostMigrationData.ValueRO.GhostDataBlob;

            var compressedGhostData = CompressGhostDataIfEnabled(fromWorld, ghostData, hostData, out var size);

            if (toData.Capacity < size)
                toData.Resize(size*2, NativeArrayOptions.ClearMemory);

            // Set the size to exactly what will be copied
            toData.Length = size;
            var dataArray = toData.AsArray();
            CopyMigrationData(ref dataArray, hostData, compressedGhostData);
        }

        static unsafe void CopyMigrationData(ref NativeArray<byte> destinationBuffer, NativeList<byte> hostData, NativeList<byte> ghostData)
        {
            // Copy host data size + host data into destination buffer
            var dataPtr = (IntPtr)destinationBuffer.GetUnsafePtr();
            var offset = 0;
            int* header = (int*)dataPtr;
            *header = hostData.Length;
            offset += sizeof(int);
            UnsafeUtility.MemCpy((void*)(dataPtr + offset), hostData.GetUnsafeReadOnlyPtr(), hostData.Length);

            // Copy ghost data size + ghost data into destination buffer behind the host data
            offset += hostData.Length;
            header = (int*)(dataPtr + offset);
            *header = ghostData.Length;
            offset += sizeof(int);
            UnsafeUtility.MemCpy((void*)(dataPtr + offset), ghostData.GetUnsafeReadOnlyPtr(), ghostData.Length);
        }

        static void UpdateStatistics(World world, int updateSize)
        {
            using var statsQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationStats>());
            var stats = statsQuery.GetSingleton<HostMigrationStats>();
            stats.TotalUpdateSize -= stats.UpdateSize;
            stats.UpdateSize = updateSize;
            stats.TotalUpdateSize += stats.UpdateSize;
            world.EntityManager.SetComponentData(statsQuery.GetSingletonEntity(), stats);
        }

        /// <summary>
        /// Compress the ghost data if compression is enabled in the host migration configuration.
        /// Update the migration data statistics data size as they've been recorded earlier uncompressed
        /// </summary>
        static NativeList<byte> CompressGhostDataIfEnabled(World world, NativeList<byte> ghostData, NativeList<byte> hostData, out int size)
        {
            // This is the required size for host/ghost data + headers for each
            size = hostData.Length + ghostData.Length + sizeof(int) + sizeof(int);

            // NOTE: Compression needs to be done here as it's not burst compatible and can't be done in the host migration system
            var compressedGhostData = new NativeList<byte>(ghostData.Length, Allocator.Temp);
            CompressAndEncodeGhostData(ghostData, compressedGhostData);
            size = hostData.Length + compressedGhostData.Length + sizeof(int) + sizeof(int);

            // Statistics need to be updated as the recorded value earlier was uncompressed
            UpdateStatistics(world, size);
            return compressedGhostData;
        }

        /// <summary>
        /// Compress ghost data using Brotli compression and Base64 encode the result
        /// </summary>
        internal static unsafe void CompressAndEncodeGhostData(NativeList<byte> ghostData, NativeList<byte> compressedGhostData)
        {
            using var outputStream = new MemoryStream();
            using var compressor = new BrotliStream(outputStream, System.IO.Compression.CompressionLevel.Fastest);
            compressor.Write(ghostData.AsArray().AsReadOnlySpan());
            compressor.Flush();
            var compressed = Convert.ToBase64String(outputStream.ToArray());
            var stringBytes = Encoding.UTF8.GetBytes(compressed);

            fixed (byte* stringPtr = stringBytes)
            {
                compressedGhostData.AddRange(stringPtr, stringBytes.Length);
            }
            compressedGhostData.Add(0);
        }

        /// <summary>
        /// On the server check if an incoming connection is a known connection reconnecting
        /// and re-add all the components it previously had before the host migration. Note
        /// that it only readds the components but does not restore component data.
        /// </summary>
        internal static bool HandleReconnection(NativeArray<HostConnectionData> hostMigrationConnections, EntityCommandBuffer commandBuffer, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            if (!hostMigrationConnections.IsCreated || hostMigrationConnections.Length == 0)
                return false;
            for (int j = 0; j < hostMigrationConnections.Length; ++j)
            {
                var prevConnectionData = hostMigrationConnections[j];
                if (prevConnectionData.UniqueId == uniqueId.Value)
                {
                    var components = prevConnectionData.Components;
                    if (components.Length == 0)
                        return false;
                    foreach (var component in components)
                    {
                        var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(component.StableHash);
                        commandBuffer.AddComponent(connectionEntity, ComponentType.FromTypeIndex(typeIndex));
                    }
                    return true;
                }
            }
            return false;
        }

        internal static unsafe bool RestoreConnectionComponentData(NativeArray<HostConnectionData> hostMigrationConnections, EntityManager entityManager, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            entityManager.CompleteAllTrackedJobs(); // For the dynamic component data pointer safety
            for (int j = 0; j < hostMigrationConnections.Length; ++j)
            {
                var prevConnectionData = hostMigrationConnections[j];
                if (prevConnectionData.UniqueId == uniqueId.Value)
                {
                    var components = hostMigrationConnections[j].Components;
                    foreach (var component in components)
                    {
                        var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(component.StableHash);
                        var componentType = ComponentType.FromTypeIndex(typeIndex);
                        if (componentType.IsZeroSized)
                            continue;
                        var typeInfo = TypeManager.GetTypeInfo(typeIndex);
                        var chunk = entityManager.GetChunk(connectionEntity);
                        var typeHandle = entityManager.GetDynamicComponentTypeHandle(componentType);
                        if (!chunk.Has(ref typeHandle))
                        {
                            Debug.LogError($"Component {componentType} not found on connection with unique ID {prevConnectionData.UniqueId} entity {connectionEntity.ToFixedString()} while trying to migrate connection component data");
                            continue;
                        }
                        var indexInChunk = entityManager.GetStorageInfo(connectionEntity).IndexInChunk;
                        var compSize = typeInfo.SizeInChunk;
                        var offset = indexInChunk * compSize;
                        var compDataPtr = (byte*)chunk
                            .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                            .GetUnsafeReadOnlyPtr() + offset;
                        UnsafeUtility.MemCpy(compDataPtr, component.Data.GetUnsafePtr(), compSize);
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Deploy the given host migration data in the given world. The data needs to be collected
        /// by <see cref="Get"/> and contains all the ghost data and specific host configuration data
        /// needed to set up the netcode state.
        /// </summary>
        /// <param name="toWorld">Destination world to deploy the migration data</param>
        /// <param name="fromData">Host migration data collected by the host migration system</param>
        public static unsafe void Set(in NativeArray<byte> fromData, World toWorld)
        {
            // Extract host data part
            int hostDataSize = 0;
            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref hostDataSize), (void*)fromData.GetUnsafePtr(), sizeof(int));
            if (hostDataSize + sizeof(int) > fromData.Length)
            {
                Debug.LogError($"Invalid host migration data: Trying to read {hostDataSize} host data bytes, but buffer only has {fromData.Length - sizeof(int)} bytes left");
                return;
            }
            var hostData = new NativeSlice<byte>(fromData, sizeof(int), hostDataSize);

            // Extract ghost data part
            var ghostDataPtr = (IntPtr)fromData.GetUnsafePtr() + sizeof(int) + hostDataSize;
            int ghostDataSize = 0;
            int ghostDataStart = 2 * sizeof(int) + hostDataSize;    // where the ghost data portion will begin in the migration buffer
            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref ghostDataSize), (void*)ghostDataPtr, sizeof(int));
            if (ghostDataSize + ghostDataStart > fromData.Length)
            {
                Debug.LogError($"Invalid host migration data: Trying to read {ghostDataSize} ghost data bytes, but buffer only has {fromData.Length - ghostDataStart} bytes left");
                return;
            }
            var ghostData = new NativeSlice<byte>(fromData, 2*sizeof(int) + hostDataSize, ghostDataSize);

            Debug.Log($"Migrating server data, host data size = {hostDataSize}, ghost data size = {ghostDataSize}");
            var hostMigrationDataQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationStorage>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationStorage>();
            hostMigrationData.ValueRW.HostData = DecodeHostData(hostData);

            var config = hostMigrationData.ValueRW.HostData.Config;
            using var configQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationConfig>());
            toWorld.EntityManager.SetComponentData(configQuery.GetSingletonEntity(), config);
            Debug.Log($"Setting host migration configuration StoreOwnGhosts={config.StoreOwnGhosts} MigrationTimeout={config.MigrationTimeout} ServerUpdateInterval={config.ServerUpdateInterval}");

            hostMigrationData.ValueRW.Ghosts = DecompressAndDecodeGhostData(ghostData);

            // TODO: It appears this does not work
            toWorld.SetTime(new TimeData(hostMigrationData.ValueRO.HostData.ElapsedTime, 0));

            using var networkTimeQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            var networkTime = networkTimeQuery.GetSingletonRW<NetworkTime>();
            networkTime.ValueRW.ServerTick = hostMigrationData.ValueRO.HostData.ServerTick;
            networkTime.ValueRW.ElapsedNetworkTime = hostMigrationData.ValueRO.HostData.ElapsedNetworkTime;
            Debug.Log($"Setting server state: ElapsedTime={hostMigrationData.ValueRO.HostData.ElapsedTime} ServerTick={networkTime.ValueRW.ServerTick.TickValue} ElapsedNetworkTime={hostMigrationData.ValueRO.HostData.ElapsedNetworkTime}");


            // Set the allocation id to be at the same place it was on the original server
            // this will ensure any new ghosts instantiated during a migration won't be given
            // the same id as a migrating ghost
            var spawnedGhostEntityMapQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<SpawnedGhostEntityMap>());
            var spawnedGhostEntityMapData = spawnedGhostEntityMapQuery.GetSingletonRW<SpawnedGhostEntityMap>();
            if (spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[0] != 1 || spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[1] != 1)
                Debug.LogError($"GhostIds have been assigned before host migration data has been applied, there could be GhostId collisions. No ghosts should be instantiated before host migration data has been set.");

            spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[0] = hostMigrationData.ValueRO.HostData.NextNewGhostId;
            spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[1] = hostMigrationData.ValueRO.HostData.NextNewPrespawnGhostId;

            var prespawnGhostIdRangeBufferEntityQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<PrespawnGhostIdRange>());
            var prespawnGhostIdRangeBufferData = prespawnGhostIdRangeBufferEntityQuery.GetSingletonBuffer<PrespawnGhostIdRange>();

            // Setup the PrespawnGhostIdRanges, this will allow the subscene loading to match ghostIds to the old server
            foreach ( var prespawnGhostIdRange in hostMigrationData.ValueRO.HostData.PrespawnGhostIdRanges )
            {
                prespawnGhostIdRangeBufferData.Add(new PrespawnGhostIdRange() {
                    SubSceneHash = prespawnGhostIdRange.SubSceneHash,
                    FirstGhostId = prespawnGhostIdRange.FirstGhostId,
                    Count = 0,
                    Reserved = 0
                });
            }

            // migrate the network ids of the currently connected clients
            using var migratedNetworkIdsQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<MigratedNetworkIdsData>());
            if ( migratedNetworkIdsQuery.TryGetSingletonRW<MigratedNetworkIdsData>(out var migratedNetworkIds) )
            {
                migratedNetworkIds.ValueRW.MigratedNetworkIds.Clear(); // make sure its empty
                foreach (var c in hostMigrationData.ValueRO.HostData.Connections)
                {
                    migratedNetworkIds.ValueRW.MigratedNetworkIds.Add(c.UniqueId, c.NetworkId);
                }
            }

            // migrate the information used to assign NetworkIDs so new connections are assigned correctly without overlapping the migrated ids
            using var networkIDAllocationDataQuery = toWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIDAllocationData>());
            if (networkIDAllocationDataQuery.TryGetSingletonRW<NetworkIDAllocationData>(out var networkIDAllocationData))
            {
                networkIDAllocationData.ValueRW.NumNetworkIds.Value = hostMigrationData.ValueRO.HostData.NumNetworkIds;

                foreach ( var a in hostMigrationData.ValueRO.HostData.FreeNetworkIds )
                {
                    networkIDAllocationData.ValueRW.FreeNetworkIds.Enqueue( a );
                }
            }

            toWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
            bool hasPrespawns = false;
            for (int i = 0; i < hostMigrationData.ValueRO.Ghosts.Ghosts.Length; ++i)
            {
                if (hostMigrationData.ValueRO.Ghosts.Ghosts[i].GhostId < 0)
                {
                    hasPrespawns = true;
                    break;
                }
            }
            if (hasPrespawns)
                toWorld.EntityManager.CreateSingleton<ForcePrespawnListPrefabCreate>();

            // Trigger server host migration system
            var requestEntity = toWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationRequest>());
            toWorld.EntityManager.SetComponentData(requestEntity, new HostMigrationRequest(){ExpectedPrefabCount = hostMigrationData.ValueRW.Ghosts.GhostPrefabs.Length});
            toWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationInProgress>());
        }

        internal static unsafe GhostStorage DecompressAndDecodeGhostData(NativeSlice<byte> ghostDataBlob)
        {
            var dataPtr = (sbyte*)ghostDataBlob.GetUnsafePtr();
            var decodedBytes = Convert.FromBase64String(new string(dataPtr));

            using var inputStream = new MemoryStream(decodedBytes);
            using var decompressStream = new MemoryStream();
            using var compressionStream = new BrotliStream(inputStream, CompressionMode.Decompress);
            compressionStream.CopyTo(decompressStream);
            compressionStream.Flush();
            var decompressedBytes = new NativeArray<byte>(decompressStream.ToArray(), Allocator.Persistent);

            var reader = new DataStreamReader(decompressedBytes);
            var prefabCount = reader.ReadShort();
            var prefabs = new NativeArray<GhostPrefabData>(prefabCount, Allocator.Persistent);
            for (int i = 0; i < prefabCount; ++i)
            {
                var ghostTypeIndex = reader.ReadShort();
                var ghostTypeHash = reader.ReadInt();
                prefabs[i] = new GhostPrefabData()
                {
                    GhostTypeIndex = ghostTypeIndex,
                    GhostTypeHash = ghostTypeHash
                };
            }
            var ghostCount = reader.ReadShort();
            var ghostData = new GhostStorage()
            {
                GhostPrefabs = prefabs,
                // TODO: Free/reuse this buffer
                Ghosts = new NativeArray<GhostData>(ghostCount, Allocator.Persistent)
            };
            for (int i = 0; i < ghostCount; ++i)
            {
                var ghostId = reader.ReadInt();
                var spawnTick = reader.ReadUInt();
                var ghostType = reader.ReadShort();
                var componentCount = reader.ReadShort();
                var componentData = new NativeArray<DataComponent>(componentCount, Allocator.Persistent);
                for (int j = 0; j < componentCount; ++j)
                {
                    var stableTypeHash = reader.ReadULong();
                    var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(stableTypeHash);
                    var componentType = ComponentType.FromTypeIndex(typeIndex);
                    byte enabled = 0;
                    if (componentType.IsEnableable)
                        enabled = reader.ReadByte();
                    if (componentType.IsBuffer)
                    {
                        var elementSize = TypeManager.GetTypeInfo(typeIndex).ElementSize;
                        var bufferLength = reader.ReadInt();
                        var data = new NativeArray<byte>(bufferLength * elementSize, Allocator.Persistent);
                        reader.ReadBytes(data);
                        componentData[j] = new DataComponent() { StableHash = stableTypeHash, Length = bufferLength, Enabled = enabled == 1, Data = data };
                    }
                    else
                    {
                        var dataLength = TypeManager.GetTypeInfo(typeIndex).TypeSize;
                        var data = new NativeArray<byte>(dataLength, Allocator.Persistent);
                        reader.ReadBytes(data);
                        componentData[j] = new DataComponent() { StableHash = stableTypeHash, Enabled = enabled == 1, Data = data };
                    }
                }

                var newGhostData = new GhostData()
                {
                    GhostId = ghostId,
                    GhostType = ghostType,
                    DataComponents = componentData
                };
                newGhostData.SpawnTick.SerializedData = spawnTick;
                ghostData.Ghosts[i] = newGhostData;
            }
            return ghostData;
        }

        internal static unsafe HostDataStorage DecodeHostData(NativeSlice<byte> data)
        {
            if (data.Length == 0)
            {
                Debug.LogError("Empty buffer given when decoding host data.");
                return default;
            }

            // TODO: Avoid this copy, data reader could just support slices
            var toArray = new NativeArray<byte>(data.Length, Allocator.Temp);
            UnsafeUtility.MemCpy(toArray.GetUnsafePtr(), data.GetUnsafeReadOnlyPtr(), data.Length);
            var reader = new DataStreamReader(toArray);

            var hostData = new HostDataStorage();
            if (reader.Length == 0) return hostData;
            var connectionCount = reader.ReadShort();
            var connections = new NativeArray<HostConnectionData>(connectionCount, Allocator.Persistent);
            for (int i = 0; i < connectionCount; ++i)
            {
                var uniqueId = reader.ReadUInt();
                var networkId = reader.ReadInt();
                var inGame = reader.ReadByte() == 1;
                var scenesLoadedCount = reader.ReadShort();
                var componentCount = reader.ReadShort();
                var components = new NativeArray<ConnectionComponent>(componentCount, Allocator.Persistent);
                for (int j = 0; j < componentCount; ++j)
                {
                    var stableHash = reader.ReadULong();
                    var dataLength = reader.ReadShort();
                    var componentData = new NativeArray<byte>(dataLength, Allocator.Persistent);
                    reader.ReadBytes(componentData);
                    components[j] = new ConnectionComponent() { StableHash = stableHash, Data = componentData };
                }
                connections[i] = new HostConnectionData()
                {
                    UniqueId = uniqueId,
                    NetworkId = networkId,
                    NetworkStreamInGame = inGame,
                    ScenesLoadedCount = scenesLoadedCount,
                    Components = components
                };
            }
            hostData.Connections = connections;

            var subsceneCount = reader.ReadShort();
            var subscenes = new NativeArray<HostSubSceneData>(subsceneCount, Allocator.Persistent);
            for (int i = 0; i < subsceneCount; ++i)
            {
                subscenes[i] = new HostSubSceneData() { SubSceneGuid = new Hash128(reader.ReadUInt(), reader.ReadUInt(), reader.ReadUInt(), reader.ReadUInt()) };
            }
            hostData.SubScenes = subscenes;

            hostData.Config.StoreOwnGhosts = reader.ReadByte() == 1;
            hostData.Config.MigrationTimeout = reader.ReadFloat();
            hostData.Config.ServerUpdateInterval = reader.ReadFloat();
            hostData.ElapsedTime = reader.ReadDouble();
            hostData.ServerTick.SerializedData = reader.ReadUInt();
            hostData.ElapsedNetworkTime = reader.ReadDouble();
            hostData.NextNewGhostId = reader.ReadInt();
            hostData.NextNewPrespawnGhostId = reader.ReadInt();

            var prespawnGhostIdRangesCount = reader.ReadShort();
            var prespawnGhostIdRanges = new NativeArray<HostPrespawnGhostIdRangeData>(prespawnGhostIdRangesCount, Allocator.Persistent);
            for (int i = 0; i < prespawnGhostIdRangesCount; ++i)
            {
                prespawnGhostIdRanges[i] = new HostPrespawnGhostIdRangeData() { SubSceneHash = reader.ReadULong(), FirstGhostId = reader.ReadInt() };
            }
            hostData.PrespawnGhostIdRanges = prespawnGhostIdRanges;

            hostData.NumNetworkIds = reader.ReadInt();
            int numFreeIds = reader.ReadInt();
            hostData.FreeNetworkIds = new NativeArray<int>(numFreeIds,Allocator.Persistent);
            for ( int i=0; i<numFreeIds; ++i )
            {
                hostData.FreeNetworkIds[i] = reader.ReadInt();
            }

            return hostData;
        }
    }
}
