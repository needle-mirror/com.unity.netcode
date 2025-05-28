using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Networking.Transport;
using Unity.Scenes;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

#if ENABLE_HOST_MIGRATION

namespace Unity.NetCode.HostMigration
{
    /// <summary>
    /// Host migration class used to access the host migration system, like getting the host migration data blob and
    /// functions for reacting to host migration events.
    /// </summary>
    public static class HostMigrationUtility
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
        /// <param name="world">The world where the migration data is stored</param>
        /// <param name="data">Destination list to copy the data, this will be resized if it is too small to store all the data</param>
        public static void GetHostMigrationData(World world, ref NativeList<byte> data)
        {
            var hostMigrationDataQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationData>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationData>();
            var hostData = hostMigrationData.ValueRO.HostDataBlob;
            var ghostData = hostMigrationData.ValueRO.GhostDataBlob;

            var compressedGhostData = CompressGhostDataIfEnabled(world, ghostData, hostData, out var size);

            if (data.Capacity < size)
                data.Resize(size*2, NativeArrayOptions.ClearMemory);

            // Set the size to exactly what will be copied
            data.Length = size;
            var dataArray = data.AsArray();
            CopyMigrationData(ref dataArray, hostData, compressedGhostData);
        }

        /// <summary>
        /// Get the host migration data which has been collected by the host migration system. There
        /// is no limit enforced on the total size of the migration data.
        /// </summary>
        /// <param name="world">The world where the migration data is stored</param>
        /// <param name="data">Destination buffer to copy the data into</param>
        /// <param name="size">The required size of the host data, this can be used to resize the destination buffer if it is too small</param>
        /// <returns>True if the data was successfully copied</returns>
        public static bool TryGetHostMigrationData(World world, ref NativeArray<byte> data, out int size)
        {
            // TODO: Cache this query, could pass in a SystemState and use GetEntityQuery, or use singleton pattern
            using var hostMigrationDataQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationData>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationData>();
            var hostData = hostMigrationData.ValueRO.HostDataBlob;
            var ghostData = hostMigrationData.ValueRO.GhostDataBlob;

            var compressedGhostData = CompressGhostDataIfEnabled(world, ghostData, hostData, out size);

            if (data.Length < size)
                return false;

            CopyMigrationData(ref data, hostData, compressedGhostData);
            return true;
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
            using var configQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationConfig>());
            var config = configQuery.GetSingleton<HostMigrationConfig>();

            // This is the required size for host/ghost data + headers for each
            size = hostData.Length + ghostData.Length + sizeof(int) + sizeof(int);

            if (config.StorageMethod == DataStorageMethod.StreamCompressed)
            {
                // NOTE: Compression needs to be done here as it's not burst compatible and can't be done in the host migration system
                var compressedGhostData = new NativeList<byte>(ghostData.Length, Allocator.Temp);
                CompressAndEncodeGhostData(ghostData, compressedGhostData);
                size = hostData.Length + compressedGhostData.Length + sizeof(int) + sizeof(int);

                // Statistics need to be updated as the recorded value earlier was uncompressed
                UpdateStatistics(world, size);
                return compressedGhostData;
            }

            return ghostData;
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
        /// Optional helper method to assist with the operations needed during a host migration process. <see cref="SetHostMigrationData"/>
        /// can also be called directly with a server world which has been manually set up to resume hosting
        /// with a given host migration data.
        ///
        /// Takes the given host migration data and starts the host migration process. This starts loading
        /// the entity scenes the host previously had loaded (if any). The <see cref="NetworkDriverStore"/> and <see cref="NetworkDriver"/> in the
        /// new server world will be created appropriately, the driver constructor will need to be capable of
        /// setting up the relay connection with the given constructor. The local client world will switch from relay to
        /// local IPC connection to the server world.
        /// </summary>
        /// <param name="driverConstructor">The network driver constructor registered in the new server world and also in the client world.</param>
        /// <param name="migrationData">The data blob containing host migration data, deployed to the new server world.</param>
        /// <returns>Returns false if there was any immediate failure when starting up the new server</returns>
        public static bool MigrateDataToNewServerWorld(INetworkStreamDriverConstructor driverConstructor, ref NativeArray<byte> migrationData)
        {
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = driverConstructor;
            var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            if (migrationData.Length == 0)
                Debug.LogWarning($"No host migration data given during host migration, no data will be deployed.");
            else
                SetHostMigrationData(serverWorld, ref migrationData);

            using var serverDriverQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
            var serverDriver = serverDriverQuery.GetSingletonRW<NetworkStreamDriver>();
            if (!serverDriver.ValueRW.Listen(NetworkEndpoint.AnyIpv4))
            {
                Debug.LogError($"NetworkStreamDriver.Listen() failed");
                return false;
            }
            var ipcPort = serverDriver.ValueRW.GetLocalEndPoint(serverDriver.ValueRW.DriverStore.FirstDriver).Port;

            // The client driver needs to be recreated, and then directly connected to new server world via IPC
            return ConfigureClientAndConnect(ClientServerBootstrap.ClientWorld, driverConstructor, NetworkEndpoint.LoopbackIpv4.WithPort(ipcPort));
        }

        /// <summary>
        /// Optional helper method to create the client driver with the given driver constructor and connect to the endpoint.
        /// The NetworkDriverStore will be recreated as the client can be switching from a local IPC connection to relay
        /// connection or reversed, the relay data can be set at driver creation time.
        /// </summary>
        /// <param name="clientWorld">The client world which needs to be configured.</param>
        /// <param name="driverConstructor">The network driver constructor used for creating a new network driver in the client world.</param>
        /// <param name="serverEndpoint">The network endpoint the client will connect to after configuring the network driver.</param>
        /// <returns>Returns true if the connect call succeeds</returns>
        public static bool ConfigureClientAndConnect(World clientWorld, INetworkStreamDriverConstructor driverConstructor, NetworkEndpoint serverEndpoint)
        {
            if (clientWorld == null || !clientWorld.IsCreated)
            {
                Debug.LogError("HostMigration.ConfigureClientAndConnect: Invalid client world provided");
                return false;
            }
            using var clientNetDebugQuery = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetDebug>());
            var clientNetDebug = clientNetDebugQuery.GetSingleton<NetDebug>();
            var clientDriverStore = new NetworkDriverStore();
            driverConstructor.CreateClientDriver(clientWorld, ref clientDriverStore, clientNetDebug);
            using var clientDriverQuery = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
            var clientDriver = clientDriverQuery.GetSingleton<NetworkStreamDriver>();
            clientDriver.ResetDriverStore(clientWorld.Unmanaged, ref clientDriverStore);

            var connectionEntity = clientDriver.Connect(clientWorld.EntityManager, serverEndpoint);
            if (connectionEntity == Entity.Null)
                return false;
            return true;
        }

        /// <summary>
        /// Deploy the given host migration data in the given world. The data needs to be collected
        /// by <see cref="GetHostMigrationData"/> and contains all the ghost data and specific host configuration data
        /// needed to set up the netcode state.
        /// </summary>
        /// <param name="world">Destination world to deploy the migration data</param>
        /// <param name="migrationData">Host migration data collected by the host migration system</param>
        public static unsafe void SetHostMigrationData(World world, ref NativeArray<byte> migrationData)
        {
            // Extract host data part
            int hostDataSize = 0;
            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref hostDataSize), (void*)migrationData.GetUnsafePtr(), sizeof(int));
            if (hostDataSize + sizeof(int) > migrationData.Length)
            {
                Debug.LogError($"Invalid host migration data: Trying to read {hostDataSize} host data bytes, but buffer only has {migrationData.Length - sizeof(int)} bytes left");
                return;
            }
            var hostData = new NativeSlice<byte>(migrationData, sizeof(int), hostDataSize);

            // Extract ghost data part
            var ghostDataPtr = (IntPtr)migrationData.GetUnsafePtr() + sizeof(int) + hostDataSize;
            int ghostDataSize = 0;
            int ghostDataStart = 2 * sizeof(int) + hostDataSize;    // where the ghost data portion will begin in the migration buffer
            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref ghostDataSize), (void*)ghostDataPtr, sizeof(int));
            if (ghostDataSize + ghostDataStart > migrationData.Length)
            {
                Debug.LogError($"Invalid host migration data: Trying to read {ghostDataSize} ghost data bytes, but buffer only has {migrationData.Length - ghostDataStart} bytes left");
                return;
            }
            var ghostData = new NativeSlice<byte>(migrationData, 2*sizeof(int) + hostDataSize, ghostDataSize);

            Debug.Log($"Migrating server data, host data size = {hostDataSize}, ghost data size = {ghostDataSize}");
            var hostMigrationDataQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<HostMigrationData>());
            var hostMigrationData = hostMigrationDataQuery.GetSingletonRW<HostMigrationData>();
            hostMigrationData.ValueRW.HostData = DecodeHostData(hostData);

            var config = hostMigrationData.ValueRW.HostData.Config;
            using var configQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HostMigrationConfig>());
            world.EntityManager.SetComponentData(configQuery.GetSingletonEntity(), config);
            Debug.Log($"Setting host migration configuration StoreOwnGhosts={config.StoreOwnGhosts} StorageMethod={config.StorageMethod} MigrationTimeout={config.MigrationTimeout} ServerUpdateInterval={config.ServerUpdateInterval}");

            hostMigrationData.ValueRW.Ghosts = DecodeGhostData(world.EntityManager, ghostData);

            // TODO: It appears this does not work
            world.SetTime(new TimeData(hostMigrationData.ValueRO.HostData.ElapsedTime, 0));

            using var networkTimeQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            var networkTime = networkTimeQuery.GetSingletonRW<NetworkTime>();
            networkTime.ValueRW.ServerTick = hostMigrationData.ValueRO.HostData.ServerTick;
            networkTime.ValueRW.ElapsedNetworkTime = hostMigrationData.ValueRO.HostData.ElapsedNetworkTime;
            Debug.Log($"Setting server state: ElapsedTime={hostMigrationData.ValueRO.HostData.ElapsedTime} ServerTick={networkTime.ValueRW.ServerTick.TickValue} ElapsedNetworkTime={hostMigrationData.ValueRO.HostData.ElapsedNetworkTime}");


            // Set the allocation id to be at the same place it was on the original server
            // this will ensure any new ghosts instantiated during a migration won't be given
            // the same id as a migrating ghost
            var spawnedGhostEntityMapQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<SpawnedGhostEntityMap>());
            var spawnedGhostEntityMapData = spawnedGhostEntityMapQuery.GetSingletonRW<SpawnedGhostEntityMap>();
            if (spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[0] != 1 || spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[1] != 1)
                Debug.LogError($"GhostIds have been assigned before host migration data has been applied, there could be GhostId collisions. No ghosts should be instantiated before host migration data has been set.");

            spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[0] = hostMigrationData.ValueRO.HostData.NextNewGhostId;
            spawnedGhostEntityMapData.ValueRW.m_ServerAllocatedGhostIds[1] = hostMigrationData.ValueRO.HostData.NextNewPrespawnGhostId;

            var prespawnGhostIdRangeBufferEntityQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<PrespawnGhostIdRange>());
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
            using var migratedNetworkIdsQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<MigratedNetworkIdsData>());
            if ( migratedNetworkIdsQuery.TryGetSingletonRW<MigratedNetworkIdsData>(out var migratedNetworkIds) )
            {
                migratedNetworkIds.ValueRW.MigratedNetworkIds.Clear(); // make sure its empty
                foreach (var c in hostMigrationData.ValueRO.HostData.Connections)
                {
                    migratedNetworkIds.ValueRW.MigratedNetworkIds.Add(c.UniqueId, c.NetworkId);
                }
            }

            // migrate the information used to assign NetworkIDs so new connections are assigned correctly without overlapping the migrated ids
            using var networkIDAllocationDataQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkIDAllocationData>());
            if (networkIDAllocationDataQuery.TryGetSingletonRW<NetworkIDAllocationData>(out var networkIDAllocationData))
            {
                networkIDAllocationData.ValueRW.NumNetworkIds.Value = hostMigrationData.ValueRO.HostData.NumNetworkIds;

                foreach ( var a in hostMigrationData.ValueRO.HostData.FreeNetworkIds )
                {
                    networkIDAllocationData.ValueRW.FreeNetworkIds.Enqueue( a );
                }
            }

            world.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
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
                world.EntityManager.CreateSingleton<ForcePrespawnListPrefabCreate>();

            // Trigger server host migration system
            var requestEntity = world.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationRequest>());
            world.EntityManager.SetComponentData(requestEntity, new HostMigrationRequest(){ExpectedPrefabCount = hostMigrationData.ValueRW.Ghosts.GhostPrefabs.Length});
            world.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationInProgress>());
        }


        internal static void SerializeGhostData(ref GhostStorage ghosts, NativeList<byte> ghostDataBlob)
        {
            var writer = new DataStreamWriter(40960, Allocator.Temp);
            WriteGhostData(ref ghosts);
            while (writer.HasFailedWrites)
            {
                writer = new DataStreamWriter(2*writer.Capacity, Allocator.Temp);
                WriteGhostData(ref ghosts);
                if (writer.Length > 10_000_000)
                {
                    Debug.LogError($"Invalid ghost data, size reached {writer.Length} bytes");
                    break;
                }
            }
            ghostDataBlob.AddRange(writer.AsNativeArray());

            void WriteGhostData(ref GhostStorage ghosts)
            {
                writer.WriteShort((short)ghosts.GhostPrefabs.Length);
                foreach (var ghostPrefab in ghosts.GhostPrefabs)
                {
                    writer.WriteShort((short)ghostPrefab.GhostTypeIndex);
                    writer.WriteInt(ghostPrefab.GhostTypeHash);
                }
                writer.WriteShort((short)ghosts.Ghosts.Length);
                foreach (var ghost in ghosts.Ghosts)
                {
                    writer.WriteShort((short)ghost.GhostId);
                    writer.WriteUInt(ghost.SpawnTick.SerializedData);
                    writer.WriteShort((short)ghost.GhostType);
                    writer.WriteShort((short)ghost.Data.Length);
                    writer.WriteBytes(ghost.Data);
                }
            }
        }

        internal static unsafe NativeArray<byte> GetGhostComponentData(Data data, ref SystemState state, Entity ghostEntity, int ghostType, out bool hasErrors)
        {
            hasErrors = false;
            var entityManager = state.EntityManager;
            const int bufferHeaderSize = sizeof(uint);    // Header just contains buffer length
            var chunk = entityManager.GetChunk(ghostEntity);
            var ghostTypeCollectionQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostCollectionPrefabSerializer>());
            var ghostTypeCollection = ghostTypeCollectionQuery.GetSingletonBuffer<GhostCollectionPrefabSerializer>();
            if (ghostType == -1)
            {
                var ghostCollectionQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                var ghostCollection = ghostCollectionQuery.GetSingleton<GhostCollection>();
                var ghostTypeComponent = entityManager.GetComponentData<GhostType>(ghostEntity);
                ghostType = ghostCollection.GhostTypeToColletionIndex[ghostTypeComponent];
            }
            else if (ghostType >= ghostTypeCollection.Length)
            {
                Debug.LogError($"Ghost collection not ready, requested ghost type {ghostType} but collection length is {ghostTypeCollection.Length}");
                hasErrors = true;
                return new NativeArray<byte>(0, Allocator.Temp);
            }

            var typeData = ghostTypeCollection[ghostType];
            var ghostCollectionComponentIndexQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostCollectionComponentIndex>());
            var ghostCollectionComponentIndex = ghostCollectionComponentIndexQuery.GetSingletonBuffer<GhostCollectionComponentIndex>();
            var ghostComponentCollectionQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostComponentSerializer.State>());
            var ghostComponentCollection = ghostComponentCollectionQuery.GetSingletonBuffer<GhostComponentSerializer.State>();
            var entityStorageInfo = state.GetEntityStorageInfoLookup();
            var indexInChunk = entityStorageInfo[ghostEntity].IndexInChunk;
            var collectionDataQuery = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostComponentSerializerCollectionData>());
            var collectionData = collectionDataQuery.GetSingleton<GhostComponentSerializerCollectionData>();

            // TODO: Initialize server-only component list, store with other ghost metadata during codegen instead
            // Only done once per ghost type, more ghost types can be streamed in via subscenes
            if (!data.ServerOnlyComponentsFlag.Contains(ghostType))
            {
                data.ServerOnlyComponentsFlag.Add(ghostType);
                var allServerOnlyComponents = new NativeList<ComponentType>(16, Allocator.Persistent);
                foreach (var ghostComponent in ghostComponentCollection)
                {
                    if (ghostComponent.PrefabType == GhostPrefabType.Server)
                    {
                        if (ghostComponent.ComponentType.IsZeroSized)
                            continue;
                        allServerOnlyComponents.Add(ghostComponent.ComponentType);
                    }
                }

                var archetype = chunk.Archetype;
                var componentTypes = archetype.GetComponentTypes(Allocator.Temp);
                var serverOnlyComponents = new NativeList<ComponentType>(16, Allocator.Persistent);
                foreach (var componentType in componentTypes)
                {
                    if (allServerOnlyComponents.Contains(componentType))
                    {
                        if (componentType.IsZeroSized)
                            continue;
                        serverOnlyComponents.Add(componentType);
                    }
                }
                if (serverOnlyComponents.Length > 0)
                    data.ServerOnlyComponentsPerGhostType.Add(ghostType, serverOnlyComponents);
            }

            var ghostDataSize = 0;
            int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
            // Calculate buffer size needed for the ghost data
            for (int comp = 0; comp < numBaseComponents; ++comp)
            {
                int serializerIdx = ghostCollectionComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                var ptr = (GhostComponentSerializer.State*)ghostComponentCollection.GetUnsafeReadOnlyPtr();
                ref readonly var ghostSerializer = ref ptr[serializerIdx];
                var componentType = ghostSerializer.ComponentType;
                var serializationStrategy = collectionData.GetCurrentSerializationStrategyForComponent(componentType, ghostSerializer.VariantHash, false);
                var typeHandle = entityManager.GetDynamicComponentTypeHandle(componentType);
                if (!chunk.Has(ref typeHandle))
                    continue;

                // TODO: This just allocates a one byte slot for the enable bit ahead of each component, should be doing it for all components per chunk
                if (componentType.IsEnableable)
                    ghostDataSize += 1;

                // Make room for a buffer header for each buffer instance we have
                if (componentType.IsBuffer)
                {
                    if (serializationStrategy.IsInputBuffer == 1)
                        continue;
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                    var bufferLength = bufferData.GetBufferLength(indexInChunk);
                    ghostDataSize += bufferHeaderSize + bufferLength * bufferData.ElementSize;
                }
                else if (componentType.IsComponent)
                {
                    var typeInfo = TypeManager.GetTypeInfo(componentType.TypeIndex);
                    ghostDataSize += typeInfo.SizeInChunk;
                }
            }

            // Calculate size needed for server only components
            if (data.ServerOnlyComponentsPerGhostType.ContainsKey(ghostType))
            {
                for (int i = 0; i < data.ServerOnlyComponentsPerGhostType[ghostType].Length; i++)
                {
                    var componentType = data.ServerOnlyComponentsPerGhostType[ghostType][i];
                    var typeInfo = TypeManager.GetTypeInfo(componentType.TypeIndex);
                    if (componentType.IsComponent)
                    {
                        ghostDataSize += typeInfo.SizeInChunk;
                    }
                    else if (componentType.IsBuffer)
                    {
                        var typeHandle = entityManager.GetDynamicComponentTypeHandle(componentType);
                        var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                        var bufferLength = bufferData.GetBufferLength(entityStorageInfo[ghostEntity].IndexInChunk);
                        ghostDataSize += bufferHeaderSize + bufferLength * bufferData.ElementSize;
                    }
                }
            }

            var ghostData = new NativeArray<byte>(ghostDataSize, Allocator.Temp);
            var ghostDataPtr = (byte*)ghostData.GetUnsafePtr();
            var writeCounter = 0;
            // Copy ghost components/buffers to migration data buffer
            for (int comp = 0; comp < numBaseComponents; ++comp)
            {
                int serializerIdx = ghostCollectionComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                var ptr = (GhostComponentSerializer.State*)ghostComponentCollection.GetUnsafeReadOnlyPtr();
                ref readonly var ghostSerializer = ref ptr[serializerIdx];
                var componentType = ghostSerializer.ComponentType;
                var serializationStrategy = collectionData.GetCurrentSerializationStrategyForComponent(componentType, ghostSerializer.VariantHash, false);
                var typeHandle = entityManager.GetDynamicComponentTypeHandle(componentType);
                if (!chunk.Has(ref typeHandle))
                    continue;

                if (componentType.IsEnableable)
                {
                    var handle = typeHandle;
                    var array = chunk.GetEnableableBits(ref handle);
                    var bitArray = new UnsafeBitArray(&array, 2 * sizeof(ulong));
                    var isSet = bitArray.IsSet(indexInChunk);
                    UnsafeUtility.MemCpy(ghostDataPtr, &isSet, 1);
                    ghostDataPtr += 1;
                    writeCounter += 1;
                }

                if (!componentType.IsBuffer)
                {
                    var compSize = ghostSerializer.ComponentSize;
                    int offset = indexInChunk * compSize;
                    var compDataPtr = (byte*)chunk
                        .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                        .GetUnsafeReadOnlyPtr() + offset;

                    UnsafeUtility.MemCpy(ghostDataPtr, compDataPtr, compSize);

                    ghostDataPtr += compSize;
                    writeCounter += compSize;
                }
                else
                {
                    if (serializationStrategy.IsInputBuffer == 1)
                        continue;
                    var bufferElementSize = ghostSerializer.ComponentSize;
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);

                    // Retrieve the whole buffer for current entity
                    var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(indexInChunk, out var length);
                    // Store the buffer length so we know how much should data should be copied when reading back this buffer
                    ((int*) ghostDataPtr)[0] = length;
                    ghostDataPtr += bufferHeaderSize;
                    if (length > 0)
                        UnsafeUtility.MemCpy(ghostDataPtr, (byte*) bufferPtr, length * bufferElementSize);
                    ghostDataPtr += length * bufferElementSize;
                    writeCounter += bufferHeaderSize + length * bufferElementSize;
                }
            }

            // Copy the server-only ghost data components/buffers
            if (data.ServerOnlyComponentsPerGhostType.ContainsKey(ghostType))
            {
                foreach (var componentType in data.ServerOnlyComponentsPerGhostType[ghostType])
                {
                    var typeInfo = TypeManager.GetTypeInfo(componentType.TypeIndex);
                    var typeHandle = entityManager.GetDynamicComponentTypeHandle(componentType);
                    if (!chunk.Has(ref typeHandle))
                        continue;
                    if (componentType.IsComponent)
                    {
                        var compSize = typeInfo.SizeInChunk;
                        int offset = indexInChunk * compSize;
                        var compDataPtr = (byte*)chunk
                            .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                            .GetUnsafeReadOnlyPtr() + offset;
                        UnsafeUtility.MemCpy(ghostDataPtr, compDataPtr, compSize);
                        ghostDataPtr += compSize;
                        writeCounter += compSize;
                    }
                    else if (componentType.IsBuffer)
                    {
                        if (!chunk.Has(ref typeHandle))
                            continue;
                        var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                        var bufferElementSize = bufferData.ElementSize;

                        // Retrieve the whole buffer for current entity
                        var bufferPtr = bufferData.GetUnsafeReadOnlyPtrAndLength(indexInChunk, out var length);

                        // Store the buffer length so we know how much should data should be copied when reading back this buffer
                        ((int*)ghostDataPtr)[0] = length;
                        ghostDataPtr += bufferHeaderSize;
                        if (length > 0)
                            UnsafeUtility.MemCpy(ghostDataPtr, (byte*)bufferPtr, length * bufferElementSize);
                        ghostDataPtr += length * bufferElementSize;
                        writeCounter += bufferHeaderSize + length * bufferElementSize;
                    }
                }
            }

            // If these don't match we calculated something incorrectly above, but it's non-fatal if the written size is smaller than the buffer size
            Debug.Assert(writeCounter == ghostDataSize);

            if (writeCounter > ghostDataSize)
            {
                Debug.LogError($"Writing out of bounds: destination buffer size is {ghostDataSize}, write counter is {writeCounter}");
                hasErrors = true;
            }

            return ghostData;
        }

        internal static unsafe GhostStorage DecodeGhostData(EntityManager entityManger, NativeSlice<byte> data)
        {
            return DecompressGhostData(data);
        }

        internal static unsafe GhostStorage DecompressGhostData(NativeSlice<byte> ghostDataBlob)
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
                var ghostId = reader.ReadShort();
                var spawnTick = reader.ReadUInt();
                var ghostType = reader.ReadShort();
                var dataLength = reader.ReadShort();
                var data = new NativeArray<byte>(dataLength, Allocator.Persistent);
                reader.ReadBytes(data);
                var newGhostData = new GhostData()
                {
                    GhostId = ghostId,
                    GhostType = ghostType,
                    Data = data
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

            hostData.Config.StorageMethod = (DataStorageMethod)reader.ReadByte();
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

#endif // ENABLE_HOST_MIGRATION
