using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Scenes;
#if USING_UNITY_SERIALIZATION
using Unity.Serialization.Binary;
using Unity.Serialization.Json;
#endif
using Debug = UnityEngine.Debug;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode
{
    /// <summary>
    /// Enable the host migration feature. This will enable the host migration systems and
    /// is required for host migration to work.
    /// </summary>
    public struct EnableHostMigration : IComponentData { }

    /// <summary>
    /// This tag is added to connections and ghost entities when they are reconnected or respawned.
    /// </summary>
    public struct IsReconnected : IComponentData { }

    /// <summary>
    /// This component will be present for the duration of a host migration. It can be used when certain
    /// systems or operations should run or not run according to host migration state.
    /// </summary>
    public struct HostMigrationInProgress : IComponentData { }

    /// <summary>
    /// Tag a connection to have the migrated component data copied into the components
    /// </summary>
    struct MigrateComponents : IComponentData
    {
        public int Step;
    }

    /// <summary>
    /// The unique ID assigned to the connection entity in this world, to be sent to the server in case of re-connects.
    /// It needs to be a separate singleton entity as the connection entity itself will be destroyed during disconnect.
    /// </summary>
    struct ConnectionUniqueId : IComponentData
    {
        public uint Value;
    }

    /// <summary>
    /// Temporary storage for a newly assigned connection unique ID from the server, the
    /// value can't be assigned immediately in the RPC where it is received.
    /// </summary>
    struct NewConnectionUniqueId : IComponentData
    {
        public uint Value;
    }

    struct HostMigrationData : IComponentData
    {
        public HostDataStorage HostData;
        public GhostStorage Ghosts;
        public NativeList<byte> HostDataBlob;
        public NativeList<byte> GhostDataBlob;
    }

    /// <summary>
    /// Request a host migration using HostMigrationData. Will wait until the given
    /// entity scenes have finished loading. While this component
    /// is present you know a host migration is still in process.
    /// </summary>
    struct HostMigrationRequest : IComponentData
    {
        /// <summary>
        /// The subscenes the new server taking over hosting duties is loading. These
        /// should finish loading before host migration can proceed to spawning ghosts.
        /// </summary>
        public NativeArray<Entity> ServerSubScenes;

        /// <summary>
        /// How many ghost prefab types should exist on the new host after loading
        /// all subscenes and preparing ghost prefab collection.
        /// </summary>
        public int ExpectedPrefabCount;
    }

    /// <summary>
    /// Configuration that can be tune the behaviour of certain internal systems
    /// within the host migration feature.
    /// </summary>
    public struct HostMigrationConfig : IComponentData
    {
        /// <inheritdoc cref="DataStorageMethod"/>
        public DataStorageMethod StorageMethod;

        /// <summary>
        /// Store ghosts owned by the local client on the host. As during a host migration
        /// the original host including the client are gone it might be ok to not include the
        /// ghosts owned by the now gone client.
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool StoreOwnGhosts;

        /// <summary>
        /// The time given for the data of the host migration to be deployed. Is
        /// mostly the time needed to wait for subscenes to load and the full list of ghost prefabs
        /// to be loaded.
        /// </summary>
        public float MigrationTimeout;

        /// <summary>
        /// The amount of time to elapse between gathering all the host migration data
        /// to be sent to the service.
        /// </summary>
        public float ServerUpdateInterval;

        /// <summary>
        /// Returns the default config options for host migration.
        /// </summary>
        public static HostMigrationConfig Default = new HostMigrationConfig()
        {
            StoreOwnGhosts = false,
            StorageMethod = DataStorageMethod.StreamCompressed,
            MigrationTimeout = 30.0f,
            ServerUpdateInterval = 2.0f
        };
    }

    /// <summary>
    /// How the host migration data is serialized for sending to the service.
    /// </summary>
    public enum DataStorageMethod : byte
    {
#if USING_UNITY_SERIALIZATION
        /// <summary>
        /// Use the JsonSerialization class from the com.unity.serialization package for storing host migration data.
        /// </summary>
        Json,
        /// <summary>
        /// Use the JsonSerialization class from the com.unity.serialization package but use the minified option to
        /// make the text more compact.
        /// </summary>
        JsonMinified,
        /// <summary>
        /// Use the BinarySerialization class from the com.unity.serialization package for storing host migration data.
        /// </summary>
        Binary,
#endif
        /// <summary>
        /// Serialize data with <see cref="DataStreamWriter"/> and compress into byte array.
        /// </summary>
        StreamCompressed
    }

    /// <summary>
    /// Statistics for a running host migration system on the host.
    /// </summary>
    public struct HostMigrationStats : IComponentData
    {
        /// <summary>
        /// How many ghosts are present in the host migration data.
        /// </summary>
        public int GhostCount;
        /// <summary>
        /// How many ghost prefabs are in the host migration data.
        /// </summary>
        public int PrefabCount;
        /// <summary>
        /// The size of the last serialized host migration data blob. This is the blob accessed via <see cref="HostMigration.GetHostMigrationData"/>.
        /// </summary>
        public int UpdateSize;
        /// <summary>
        /// The total upload size collected so far from the host migration system.
        /// </summary>
        public int TotalUpdateSize;
        /// <summary>
        /// The last time the host migration data blob was updated. Accessed via <see cref="HostMigration.GetHostMigrationData"/>.
        /// </summary>
        public double LastDataUpdateTime;
    }

    struct HostDataStorage
    {
        public NativeArray<HostConnectionData> Connections;
        public NativeArray<HostSubSceneData> SubScenes;
        public HostMigrationConfig Config;
        public double ElapsedTime;
        public uint ServerTick;
        public double ElapsedNetworkTime;
    }

    struct GhostStorage
    {
        public NativeArray<GhostData> Ghosts;
        public NativeArray<GhostPrefabData> GhostPrefabs;
    }

    struct GhostPrefabData
    {
        public int GhostTypeIndex;
        public int GhostTypeHash;
    }

    struct GhostData
    {
        // Assumes GhostType guid will match type index. A matching GhostCollectionPrefab
        // must exist
        public int GhostType;
        /// <summary>
        /// The ghost ID of this particular spawned ghost type
        /// </summary>
        public int GhostId;
        /// <summary>
        /// The component data for each ghost component
        /// </summary>
        public NativeArray<byte> Data;
    }

    struct HostSubSceneData
    {
        public Hash128 SubSceneGuid;
    }

    struct HostConnectionData
    {
        // NOTE: - transport already exchanges a unique connection token but it is internal connection data atm
        //       - transport NetworkConnection also already has a unique ConnectionId but it is also internal
        //         (consists of ID+Version, so a reused ID 0 will be Id=0,Version=2 which together will be unique)
        //       - this could simply be an incrementing integer, only difference to NetworkId is that it's never re-used
        //         throughout session, but this seems like duplicated data (we already have this but just internal transport data)
        public uint UniqueId;                      // Unique ID to know what ghosts you owned before
        public int NetworkId;               // This doesn't matter when there is a unique connection Id, maybe good for debugging
        public bool NetworkStreamInGame;    // Maybe it was off when the migration occured, should return to same status
        public int ScenesLoadedCount;       // PrespawnSectionAck buffer will follow up to the count value
        public NativeArray<ConnectionComponent> Components;
    }

    struct ConnectionComponent
    {
        public ulong StableHash;
        public NativeArray<byte> Data;
    }

    struct ConnectionMap : IComponentData
    {
        public NativeHashMap<uint, int> UniqueIdToPreviousNetworkId;
    }

    /// <summary>
    /// In the host migration data the ghosts will have their GhostOwner components pointing to the Network ID of the
    /// previous connection they had. After the migration the connections are likely not yet connected when the ghosts
    /// are spawned so these connections do not exist and will be invalid Network IDs. They would likely be pointing to the
    /// wrong connections as well as they will get IDs reassigned in the order they reconnect (will be random). These
    /// Network IDs will be added to this owner map and replaced with -1 IDs so that later when reconnecting connections
    /// are processed we can lookup which connection a ghost owner previously belonged to and update it if appropriate.
    /// </summary>
    struct GhostOwnerMap : IComponentData
    {
        public NativeHashMap<Entity, int> GhostEntityToPreviousNetworkId;
    }

    /// <summary>
    /// This system monitors for the host migration request and handles the actual
    /// migration itself with the data set in the HostMigrationData class. It also
    /// gathers the migration data for sending to the lobby (the sender will
    /// monitor the update timer on the data for detecting when new data is ready).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [BurstCompile]
    partial struct ServerHostMigrationSystem : ISystem
    {
        EntityQuery m_InGameQuery;
        EntityQuery m_ConnectionQuery;
        EntityQuery m_SubsceneQuery;

        EntityStorageInfoLookup m_EntityStorageInfo;
        ComponentLookup<NetworkId> m_NetworkIdsLookup;
        ComponentLookup<ConnectionUniqueId> m_UniqueIdsLookup;

        double m_MigrationTime;
        double m_LastServerUpdate;
        NativeHashMap<uint, int> m_NetworkIdMap;
        NativeHashMap<Entity, int> m_GhostOwnerMap;
        NativeArray<ComponentType> m_DefaultComponents;
        HostMigration.Data m_HostMigrationCache;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<HostMigrationConfig>();
            m_HostMigrationCache = new HostMigration.Data();
            m_HostMigrationCache.ServerOnlyComponentsFlag = new NativeList<int>(64, Allocator.Persistent);
            m_HostMigrationCache.ServerOnlyComponentsPerGhostType = new NativeHashMap<int, NativeList<ComponentType>>(64, Allocator.Persistent);
            m_HostMigrationCache.InputBuffers = new NativeHashMap<ComponentType, bool>(64, Allocator.Persistent);

            m_DefaultComponents = new NativeArray<ComponentType>(14, Allocator.Persistent);
            m_DefaultComponents[0] = ComponentType.ReadOnly<NetworkStreamConnection>();
            m_DefaultComponents[1] = ComponentType.ReadOnly<CommandTarget>();
            m_DefaultComponents[2] = ComponentType.ReadOnly<NetworkId>();
            m_DefaultComponents[3] = ComponentType.ReadOnly<NetworkSnapshotAck>();
            m_DefaultComponents[4] = ComponentType.ReadOnly<LinkedEntityGroup>();
            m_DefaultComponents[5] = ComponentType.ReadOnly<PrespawnSectionAck>();
            m_DefaultComponents[6] = ComponentType.ReadOnly<IncomingCommandDataStreamBuffer>();
            m_DefaultComponents[7] = ComponentType.ReadOnly<OutgoingRpcDataStreamBuffer>();
            m_DefaultComponents[8] = ComponentType.ReadOnly<IncomingRpcDataStreamBuffer>();
            m_DefaultComponents[9] = ComponentType.ReadOnly<NetworkStreamInGame>();
            m_DefaultComponents[10] = ComponentType.ReadOnly<Simulate>();
            m_DefaultComponents[11] = ComponentType.ReadOnly<ConnectionApproved>();
            m_DefaultComponents[12] = ComponentType.ReadOnly<ConnectionUniqueId>();
            m_DefaultComponents[13] = ComponentType.ReadOnly<IsReconnected>();

            m_LastServerUpdate = 0.0;
            m_MigrationTime = 0.0;
            state.RequireForUpdate<GhostCollection>();
            state.RequireForUpdate<NetworkStreamDriver>();
            state.RequireForUpdate<EnableHostMigration>();

            m_EntityStorageInfo = state.GetEntityStorageInfoLookup();
            m_NetworkIdsLookup = state.GetComponentLookup<NetworkId>();
            m_UniqueIdsLookup = state.GetComponentLookup<ConnectionUniqueId>();

            var builder = new EntityQueryBuilder(Allocator.Temp);
            builder.WithAll<NetworkStreamInGame>();
            m_InGameQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<NetworkId, CommandTarget, NetworkStreamConnection, ConnectionUniqueId>();
            m_ConnectionQuery = state.GetEntityQuery(builder);
            builder.Reset();
            builder.WithAll<SceneSectionData>();
            m_SubsceneQuery = state.GetEntityQuery(builder);

            if (!SystemAPI.TryGetSingleton(out HostMigrationConfig _))
            {
                var entityConfig = state.EntityManager.CreateEntity(ComponentType.ReadWrite<HostMigrationConfig>());
                state.EntityManager.SetName(entityConfig,"HostMigrationConfig");

                state.EntityManager.SetComponentData(entityConfig, HostMigrationConfig.Default);
            }

            var statsEntity = state.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationStats>());
            state.EntityManager.SetName(statsEntity, "HostMigrationStats");
            state.EntityManager.CreateSingleton<HostMigrationData>();
            var hostMigrationData = SystemAPI.GetSingletonRW<HostMigrationData>();
            hostMigrationData.ValueRW.HostDataBlob = new NativeList<byte>(Allocator.Persistent);
            hostMigrationData.ValueRW.GhostDataBlob = new NativeList<byte>(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            var hostMigrationData = SystemAPI.GetSingletonRW<HostMigrationData>();
            hostMigrationData.ValueRW.HostDataBlob.Dispose();
            hostMigrationData.ValueRW.GhostDataBlob.Dispose();
            for (int i = 0; i < hostMigrationData.ValueRW.HostData.Connections.Length; i++)
            {
                for (int j = 0; j < hostMigrationData.ValueRW.HostData.Connections[i].Components.Length; j++)
                    hostMigrationData.ValueRW.HostData.Connections[i].Components[j].Data.Dispose();
                hostMigrationData.ValueRW.HostData.Connections[i].Components.Dispose();
            }
            hostMigrationData.ValueRW.HostData.Connections.Dispose();
            hostMigrationData.ValueRW.HostData.SubScenes.Dispose();
            hostMigrationData.ValueRW.Ghosts.GhostPrefabs.Dispose();
            for (int i = 0; i < hostMigrationData.ValueRW.Ghosts.Ghosts.Length; i++)
                hostMigrationData.ValueRW.Ghosts.Ghosts[i].Data.Dispose();
            hostMigrationData.ValueRW.Ghosts.Ghosts.Dispose();
            m_HostMigrationCache.ServerOnlyComponentsFlag.Dispose();
            foreach (var componentList in m_HostMigrationCache.ServerOnlyComponentsPerGhostType.GetValueArray(Allocator.Temp))
                componentList.Dispose();
            m_HostMigrationCache.ServerOnlyComponentsPerGhostType.Dispose();
            m_HostMigrationCache.InputBuffers.Dispose();
            m_NetworkIdMap.Dispose();
            m_GhostOwnerMap.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var hostMigrationData = SystemAPI.GetSingleton<HostMigrationData>();

            // Restore the connection data from previous session, this happens after accepting the connection
            foreach (var (uniqueId, migrate, entity) in SystemAPI.Query<RefRO<ConnectionUniqueId>, RefRW<MigrateComponents>>().WithEntityAccess())
            {
                // First we need to add the component, causing a structural change
                if (migrate.ValueRW.Step == 0)
                {
                    migrate.ValueRW.Step++;
                    HostMigration.HandleReconnection(hostMigrationData.HostData.Connections, commandBuffer, entity, uniqueId.ValueRO);
                }
                // After the component is added to the connection entity we can copy the migrated connection data
                else if (migrate.ValueRW.Step == 1)
                {
                    commandBuffer.RemoveComponent<MigrateComponents>(entity);
                    HostMigration.RestoreConnectionComponentData(hostMigrationData.HostData.Connections, state.EntityManager, entity, uniqueId.ValueRO);
                }
            }

            // TODO: Make json path work with burst or remove it
            var config = SystemAPI.GetSingleton<HostMigrationConfig>();
            if (BurstCompiler.IsEnabled && config.StorageMethod != DataStorageMethod.StreamCompressed)
            {
                Debug.LogError($"HostMigrationConfig StorageMethod must be set to StreamCompressed with Burst enabled (will switch to this).");
                config.StorageMethod = DataStorageMethod.StreamCompressed;
                SystemAPI.SetSingleton(config);
                return;
            }

            if (SystemAPI.TryGetSingleton<HostMigrationRequest>(out var migrationRequest))
            {
                if (m_MigrationTime == 0.0)
                    m_MigrationTime = state.WorldUnmanaged.Time.ElapsedTime + config.MigrationTimeout;
                if (!SystemAPI.HasSingleton<ConnectionMap>())
                {
                    var connectionMapEntity = state.EntityManager.CreateEntity();
                    m_NetworkIdMap = new NativeHashMap<uint, int>(hostMigrationData.HostData.Connections.Length, Allocator.Persistent);
                    foreach (var con in hostMigrationData.HostData.Connections)
                        m_NetworkIdMap.Add(con.UniqueId, con.NetworkId);
                    state.EntityManager.AddComponentData(connectionMapEntity, new ConnectionMap(){UniqueIdToPreviousNetworkId = m_NetworkIdMap});
                }

                var allLoaded = true;
                for (int i = 0; i < migrationRequest.ServerSubScenes.Length; ++i)
                {
                    allLoaded &= SceneSystem.IsSceneLoaded(state.WorldUnmanaged, migrationRequest.ServerSubScenes[i]);
                }

                var ghostCollection = SystemAPI.GetSingleton<GhostCollection>();
                if (allLoaded && ghostCollection.NumLoadedPrefabs == migrationRequest.ExpectedPrefabCount)
                {
                    Debug.Log($"[HostMigration] Ready to deploy migration data (time: {m_MigrationTime-config.MigrationTimeout-state.WorldUnmanaged.Time.ElapsedTime})");
                    migrationRequest.ServerSubScenes.Dispose();
                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationRequest>());
                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationInProgress>());
                    var spawnedGhosts = SpawnAllGhosts(ref state, hostMigrationData.Ghosts.Ghosts, hostMigrationData.Ghosts.GhostPrefabs);
                    m_GhostOwnerMap = new NativeHashMap<Entity, int>(spawnedGhosts.Length, Allocator.Persistent);
                    var ghostOwnerLookup = SystemAPI.GetComponentLookup<GhostOwner>();
                    for (int i = 0; i < spawnedGhosts.Length; ++i)
                    {
                        if (ghostOwnerLookup.HasComponent(spawnedGhosts[i]))
                        {
                            var ghostOwner = ghostOwnerLookup[spawnedGhosts[i]];
                            m_GhostOwnerMap.Add(spawnedGhosts[i], ghostOwner.NetworkId);
                            state.EntityManager.SetComponentData(spawnedGhosts[i], new GhostOwner(){ NetworkId = -1 });
                        }
                    }
                    var ghostOwnerMapEntity = state.EntityManager.CreateEntity();
                    state.EntityManager.AddComponentData(ghostOwnerMapEntity, new GhostOwnerMap(){GhostEntityToPreviousNetworkId = m_GhostOwnerMap});
                    foreach (var (uniqueId, entity) in SystemAPI.Query<RefRO<ConnectionUniqueId>>().WithEntityAccess())
                    {
                        // We have to do this here as well as during HandleReconnection as the ghost collection will not initialize until a connection is in game first
                        ReconnectOwnedGhosts(ref state, commandBuffer, entity, uniqueId.ValueRO);
                    }
                }

                // Host migration has timed out (not all subscenes have finished loading and/or not all ghost prefabs exist)
                if (state.WorldUnmanaged.Time.ElapsedTime > m_MigrationTime)
                {
                    var ghostPrefabs = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>();
                    if (ghostPrefabs.Length == 0)
                        Debug.LogWarning("No ghost prefabs loaded!");

                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationRequest>());
                    state.EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<HostMigrationInProgress>());
                    if (!allLoaded)
                        Debug.LogError($"Host migration failed. Did not finish loading migrated scenes (subscene count:{hostMigrationData.HostData.SubScenes.Length})");
                    else
                        Debug.LogError($"Host migration failed. Did not load all ghost prefabs (expected {hostMigrationData.Ghosts.GhostPrefabs.Length} but only have {ghostCollection.NumLoadedPrefabs})");
                    if (m_InGameQuery.IsEmpty)
                        Debug.LogError($"No connection with NetworkStreamInGame found, no ghost prefab will be loaded into the ghost collection until that happens.");
                }
            }
            // Only do updates when no host migration is taking place
            else if (m_LastServerUpdate + config.ServerUpdateInterval < state.WorldUnmanaged.Time.ElapsedTime)
            {
                if (SystemAPI.GetSingleton<GhostCollection>().NumLoadedPrefabs == 0)
                    return;
                m_LastServerUpdate = state.WorldUnmanaged.Time.ElapsedTime;
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                state.EntityManager.CompleteAllTrackedJobs();
                GetHostConfigurationForSerializer(ref state, hostMigrationData.HostDataBlob, config, networkTime);
                GetGhostDataForSerializer(ref state, hostMigrationData.GhostDataBlob, config, out var hasErrors);

                var updateSize = hostMigrationData.GhostDataBlob.Length + hostMigrationData.HostDataBlob.Length;
                var migrationStats = SystemAPI.GetSingletonRW<HostMigrationStats>();
                migrationStats.ValueRW.LastDataUpdateTime = state.WorldUnmanaged.Time.ElapsedTime;
                migrationStats.ValueRW.UpdateSize = updateSize;
                migrationStats.ValueRW.TotalUpdateSize += updateSize;
            }

            // For new connections check if they previously owned any ghosts, but only if a connection map exists (a migration has occured)
            if (SystemAPI.HasSingleton<ConnectionMap>())
            {
                var connectionEventsForTick = SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;
                for (int i = 0; i < connectionEventsForTick.Length; ++i)
                {
                    if (connectionEventsForTick[i].State == ConnectionState.State.Connected)
                    {
                        var evt = connectionEventsForTick[i];
                        var uniqueIdLookup = SystemAPI.GetComponentLookup<ConnectionUniqueId>();
                        HandleNetworkStreamInGame(hostMigrationData.HostData.Connections, commandBuffer, evt.ConnectionEntity, uniqueIdLookup[evt.ConnectionEntity]);
                        ReconnectOwnedGhosts(ref state, commandBuffer, evt.ConnectionEntity, uniqueIdLookup[evt.ConnectionEntity]);
                    }
                }
            }
            commandBuffer.Playback(state.EntityManager);
        }

        NativeList<Entity> SpawnAllGhosts(ref SystemState state, NativeArray<GhostData> ghosts, NativeArray<GhostPrefabData> ghostPrefabs)
        {
            var ghostEntities = new NativeList<Entity>(ghosts.Length, Allocator.Temp);
            // Create ghost type mapping in case the ghost type indexes are not the same (subscene load ordering changed for example)
            var ghostTypeMap = CreateGhostTypeMap(ghostPrefabs);

            for (int i = 0; i < ghosts.Length; ++i)
            {
                var entity = Entity.Null;
                var ghost = ghosts[i];
                int ghostType = -1;
                if (ghost.GhostType >= 0)
                {
                    if (ghostTypeMap.Count <= ghost.GhostType)
                    {
                        Debug.LogError($"Did not find migrated ghost type {ghost.GhostType} in the current servers ghost type list (count={ghostTypeMap.Count})");
                        return new NativeList<Entity>(0, Allocator.Temp);
                    }
                    ghostType = ghostTypeMap[ghost.GhostType];
                    var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
                    var buffer = state.EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
                    entity = state.EntityManager.Instantiate(buffer[ghostType].GhostPrefab);
                    ghostEntities.Add(entity);
                }
                else
                {
                    var prespawnId = (int)(ghost.GhostId | 0x80000000);
                    foreach (var (ghostId, prespawnEntity) in SystemAPI.Query<RefRO<GhostInstance>>().WithAll<PreSpawnedGhostIndex>().WithEntityAccess())
                    {
                        if (ghostId.ValueRO.ghostId == prespawnId)
                            entity = prespawnEntity;
                    }
                }

                SetGhostComponentData(ref state, entity, ghostType, ghost.Data);
                state.EntityManager.AddComponent<IsReconnected>(entity);
            }
            return ghostEntities;
        }

        NativeHashMap<int, int> CreateGhostTypeMap(NativeArray<GhostPrefabData> ghostData)
        {
            var ghostPrefabs = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>();
            var ghostTypeMap = new NativeHashMap<int, int>(ghostPrefabs.Length, Allocator.Temp);
            // Go though all registered prefabs and verify the ghost type hash matches what the type index
            // from the migrated ghost types. When you say spawn index X you know the actual underlying ghost type
            // struct is the same as it was before the migration.
            for (int i = 0; i < ghostPrefabs.Length; ++i)
            {
                for (int j = 0; j < ghostData.Length; ++j)
                {
                    var prefab = ghostData[j];
                    if (prefab.GhostTypeHash == ghostPrefabs[i].GhostType.GetHashCode())
                        ghostTypeMap.Add(prefab.GhostTypeIndex, i);
                }
            }
            if (ghostTypeMap.Count != ghostPrefabs.Length)
                Debug.LogError($"Not all ghost type index have a mapping set (found {ghostTypeMap.Count} but expected {ghostPrefabs.Length})");

            return ghostTypeMap;
        }

        unsafe void SetGhostComponentData(ref SystemState state, Entity ghostEntity, int ghostType, NativeArray<byte> ghostData)
        {
            const int bufferHeaderSize = sizeof(uint);
            var ghostDataPtr = (byte*)ghostData.GetUnsafePtr();

            var chunk = state.EntityManager.GetChunk(ghostEntity);
            var ghostTypeCollection = SystemAPI.GetSingletonBuffer<GhostCollectionPrefabSerializer>();
            var ghostCollectionComponentIndex = SystemAPI.GetSingletonBuffer<GhostCollectionComponentIndex>();
            var ghostComponentCollection = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
            var entityStorageInfo = state.GetEntityStorageInfoLookup();
            var indexInChunk = entityStorageInfo[ghostEntity].IndexInChunk;

            if (ghostType == -1)
            {
                var ghostCollectionQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostCollection>());
                var ghostCollection = ghostCollectionQuery.GetSingleton<GhostCollection>();
                var ghostTypeComponent = state.EntityManager.GetComponentData<GhostType>(ghostEntity);
                ghostType = ghostCollection.GhostTypeToColletionIndex[ghostTypeComponent];
            }
            var typeData = ghostTypeCollection[ghostType];

            int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
            for (int comp = 0; comp < numBaseComponents; ++comp)
            {
                int serializerIdx = ghostCollectionComponentIndex[typeData.FirstComponent + comp].SerializerIndex;
                var ptr = (GhostComponentSerializer.State*)ghostComponentCollection.GetUnsafeReadOnlyPtr();
                ref readonly var ghostSerializer = ref ptr[serializerIdx];
                var componentType = ghostSerializer.ComponentType;
                var componentSize = ghostSerializer.ComponentSize;
                var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(componentType);

                if (componentType.IsEnableable)
                {
                    byte isSet = 0;
                    UnsafeUtility.MemCpy(&isSet, ghostDataPtr, 1);
                    chunk.SetComponentEnabled(ref typeHandle, indexInChunk, isSet == 1);
                    ghostDataPtr += 1;
                }

                if (!componentType.IsBuffer)
                {
                    int offset = indexInChunk * componentSize;
                    var compDataPtr = (byte*)chunk
                        .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, componentSize)
                        .GetUnsafeReadOnlyPtr() + offset;
                    UnsafeUtility.MemCpy(compDataPtr, ghostDataPtr, componentSize);

                    ghostDataPtr += componentSize;
                }
                else
                {
                    // Input buffers must also be skipped when restoring data as we're parsing ghost collection metadata
                    // if (Hint.Unlikely(!m_HostMigrationCache.InputBuffers.ContainsKey(componentType)))
                    //     HostMigration.IsInputBuffer(m_HostMigrationCache.InputBuffers, componentType);
                    // if (m_HostMigrationCache.InputBuffers[componentType])
                    //     continue;
                    // Deserialize buffer, the new ghosts will have 0 elements to start with
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                    var length = ((int*) ghostDataPtr)[0];
                    ghostDataPtr += bufferHeaderSize;
                    if (length > 0)
                    {
                        bufferData.ResizeUninitialized(indexInChunk, length);
                        var bufferPtr = bufferData.GetUnsafePtr(indexInChunk);
                        UnsafeUtility.MemCpy(bufferPtr, ghostDataPtr, length * componentSize);
                        ghostDataPtr += length * componentSize;
                    }
                }
            }

            // Deserialize the server-only ghost data
            var archetype = chunk.Archetype;
            var componentTypes = archetype.GetComponentTypes(Allocator.Temp);
            foreach (var componentType in componentTypes)
            {
                foreach (var ghostComponent in ghostComponentCollection)
                {
                    if (ghostComponent.PrefabType != GhostPrefabType.Server)
                        continue;
                    if (componentType == ghostComponent.ComponentType)
                    {
                        var typeInfo = TypeManager.GetTypeInfo(componentType.TypeIndex);
                        var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(componentType);
                        if (componentType.IsComponent && typeInfo.SizeInChunk != 0)
                        {
                            var compSize = typeInfo.SizeInChunk;
                            int offset = indexInChunk * compSize;
                            var compDataPtr = (byte*)chunk
                                .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                                .GetUnsafeReadOnlyPtr() + offset;
                            UnsafeUtility.MemCpy(compDataPtr, ghostDataPtr, compSize);
                            ghostDataPtr += compSize;
                        }
                        else if (componentType.IsBuffer)
                        {
                            if (!chunk.Has(ref typeHandle))
                                continue;
                            var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                            var bufferElementSize = bufferData.ElementSize;

                            // Deserialize buffer, the new ghosts will have 0 elements to start with
                            var length = ((int*) ghostDataPtr)[0];
                            ghostDataPtr += bufferHeaderSize;
                            if (length > 0)
                            {
                                bufferData.ResizeUninitialized(indexInChunk, length);
                                var bufferPtr = bufferData.GetUnsafePtr(indexInChunk);
                                UnsafeUtility.MemCpy(bufferPtr, ghostDataPtr, length * bufferElementSize);
                                ghostDataPtr += length * bufferElementSize;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gather the host data to be used for the host migration. The data is always stored
        /// in minified json.
        /// </summary>
        unsafe void GetHostConfigurationForSerializer(ref SystemState state, NativeList<byte> hostDataBlob, HostMigrationConfig config, NetworkTime networkTime)
        {
            m_EntityStorageInfo.Update(ref state);
            m_NetworkIdsLookup.Update(ref state);
            m_UniqueIdsLookup.Update(ref state);

            var conEntities = m_ConnectionQuery.ToEntityArray(Allocator.Temp);
            var migrationData = new HostDataStorage();
            migrationData.Connections = new NativeArray<HostConnectionData>(conEntities.Length, Allocator.Persistent);
            migrationData.ElapsedTime = state.WorldUnmanaged.Time.ElapsedTime;
            migrationData.Config = config;
            migrationData.ServerTick = networkTime.ServerTick.SerializedData;
            migrationData.ElapsedNetworkTime = networkTime.ElapsedNetworkTime;
            for (int i = 0; i < conEntities.Length; ++i)
            {
                var entity = conEntities[i];
                var chunk = state.EntityManager.GetChunk(entity);
                var indexInChunk = m_EntityStorageInfo[entity].IndexInChunk;
                var archetype = chunk.Archetype;
                var componentTypes = archetype.GetComponentTypes(Allocator.Temp);
                var hasInGame = state.EntityManager.HasComponent(entity, ComponentType.ReadOnly<NetworkStreamInGame>());
                var userComponents = new NativeList<ConnectionComponent>(componentTypes.Length, Allocator.Temp);
                for (int j = 0; j < componentTypes.Length; ++j)
                {
                    var componentType = componentTypes[j];
                    var found = false ;
                    for (int k = 0; k < m_DefaultComponents.Length; ++k)
                    {
                        if (m_DefaultComponents[k].TypeIndex.Index == componentType.TypeIndex.Index)
                            found = true;
                    }

                    if (!found)
                    {
                        var typeInfo = TypeManager.GetTypeInfo(componentType.TypeIndex);
                        var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(componentType);
                        if (!componentType.IsBuffer)
                        {
                            var compSize = typeInfo.SizeInChunk;
                            var connectionComponent = new ConnectionComponent()
                            {
                                StableHash = typeInfo.StableTypeHash,
                                // TODO: Cleanup allocation
                                Data = new NativeArray<byte>(compSize, Allocator.Persistent)
                            };
                            if (compSize != 0)
                            {
                                int offset = indexInChunk * compSize;
                                var compDataPtr = (byte*)chunk
                                    .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, compSize)
                                    .GetUnsafeReadOnlyPtr() + offset;
                                UnsafeUtility.MemCpy(connectionComponent.Data.GetUnsafePtr(), compDataPtr, compSize);
                            }
                            userComponents.Add(connectionComponent);
                        }
                        else
                        {

                        }
                    }
                }
                var conData = new HostConnectionData()
                {
                    UniqueId = m_UniqueIdsLookup[entity].Value,
                    NetworkId = m_NetworkIdsLookup[entity].Value,
                    NetworkStreamInGame = hasInGame,
                    Components = userComponents.AsArray()
                };
                migrationData.Connections[i] = conData;
            }

            // Collect scene host data
            var subsceneData = m_SubsceneQuery.ToComponentDataArray<SceneSectionData>(Allocator.Temp);
            migrationData.SubScenes = new NativeArray<HostSubSceneData>(subsceneData.Length, Allocator.Persistent);
            for (int i = 0; i < subsceneData.Length; ++i)
            {
                migrationData.SubScenes[i] = new HostSubSceneData()
                {
                    SubSceneGuid = subsceneData[i].SceneGUID
                };
            }

            hostDataBlob.Clear();

            // TODO: The host data must really always be the same method, as it contains the storage method
            // which is then synchronized to client via the host data. Json might be useful for debugging
            // but is incompatible with burst so must then always use DataStreamWriter
// #if USING_UNITY_SERIALIZATION
//             if (config.StorageMethod != DataStorageMethod.StreamCompressed)
//             {
//                 JsonSerializeHostData(hostDataBlob, migrationData);
//                 return;
//             }
// #endif
            var writer = new DataStreamWriter(1024, Allocator.Temp);
            WriteHostData(ref writer);
            while (writer.HasFailedWrites)
            {
                writer = new DataStreamWriter(2*writer.Capacity, Allocator.Temp);
                WriteHostData(ref writer);
                if (writer.Length > 100_000)
                {
                    Debug.LogError($"Invalid host data, size reached {writer.Length} bytes");
                    break;
                }
            }
            if (hostDataBlob.Length < writer.Length)
                hostDataBlob.ResizeUninitialized(writer.Length);
            hostDataBlob.CopyFrom(writer.AsNativeArray());

            void WriteHostData(ref DataStreamWriter dataStreamWriter)
            {
                dataStreamWriter.WriteShort((short)migrationData.Connections.Length);
                foreach (var connection in migrationData.Connections)
                {
                    dataStreamWriter.WriteUInt(connection.UniqueId);
                    dataStreamWriter.WriteInt(connection.NetworkId);
                    dataStreamWriter.WriteByte(connection.NetworkStreamInGame ? (byte)1 : (byte)0);
                    dataStreamWriter.WriteShort((short)connection.ScenesLoadedCount);
                    dataStreamWriter.WriteShort((short)connection.Components.Length);
                    foreach (var component in connection.Components)
                    {
                        dataStreamWriter.WriteULong(component.StableHash);
                        dataStreamWriter.WriteShort((short)component.Data.Length);
                        dataStreamWriter.WriteBytes(component.Data);
                    }
                }
                dataStreamWriter.WriteShort((short)migrationData.SubScenes.Length);
                foreach (var subscene in migrationData.SubScenes)
                {
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.x);
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.y);
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.z);
                    dataStreamWriter.WriteUInt(subscene.SubSceneGuid.Value.w);
                }
                dataStreamWriter.WriteByte((byte)migrationData.Config.StorageMethod);
                dataStreamWriter.WriteByte(migrationData.Config.StoreOwnGhosts ? (byte)1 : (byte)0);
                dataStreamWriter.WriteFloat(migrationData.Config.MigrationTimeout);
                dataStreamWriter.WriteFloat(migrationData.Config.ServerUpdateInterval);
                dataStreamWriter.WriteDouble(migrationData.ElapsedTime);
                dataStreamWriter.WriteUInt(migrationData.ServerTick);
                dataStreamWriter.WriteDouble(migrationData.ElapsedNetworkTime);
            }
        }

// #if USING_UNITY_SERIALIZATION
//         [BurstDiscard]
//         unsafe void JsonSerializeHostData(NativeList<byte> hostDataBlob, HostDataStorage migrationData)
//         {
//             var parameters = new JsonSerializationParameters
//             {
//                 UserDefinedAdapters = new List<IJsonAdapter>
//                 {
//                     new NativeArrayAdapter<byte>(),
//                     new NativeArrayAdapter<ulong>(),
//                     new NativeArrayAdapter<HostConnectionData>(),
//                     new NativeArrayAdapter<HostSubSceneData>(),
//                     new NativeArrayAdapter<ConnectionComponent>()
//                 },
//                 Minified = true
//             };
//
//             var jsonString = JsonSerialization.ToJson(migrationData, parameters);
//             if (jsonString.Length > hostDataBlob.Capacity)
//             {
//                 hostDataBlob.ResizeUninitialized(2 * jsonString.Length);
//                 hostDataBlob.Length = 0;
//             }
//             var stringBytes = Encoding.UTF8.GetBytes(jsonString);
//             fixed (byte* stringPtr = stringBytes)
//             {
//                 hostDataBlob.AddRange(stringPtr, stringBytes.Length);
//             }
//             // TODO: Adding null terminator since these will be converted to strings later and then depend on the termination
//             // It's easier to add it here than resize the buffer later. Drop this if the conversions can be avoided
//             hostDataBlob.Add(0);
//         }
// #endif

        /// <summary>
        /// Get ghost data in the format specified in the HostMigrationConfig. Includes the ghost instance ID,
        /// the ghost type and the full component data on all ghost components (not only ghost fields). The output will
        /// be either plain text (json) or base64 encoded binary data.
        /// </summary>
        /// <param name="state">The SystemState to use for collecting the data</param>
        /// <param name="ghostDataBlob">Reference to the output ghost data blob</param>
        /// <param name="config">The configuration for how data is written to the data blob</param>
        /// <param name="hasErrors">Will be set to true in case any errors occur during the operation</param>
        /// <returns></returns>
        // TODO: Handle errors better, add more validation...
        [BurstCompile]
        unsafe void GetGhostDataForSerializer(ref SystemState state, NativeList<byte> ghostDataBlob, HostMigrationConfig config, out bool hasErrors)
        {
            ghostDataBlob.Clear();
            hasErrors = false;
            var ghostQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostInstance>());
            var ghostEntities = ghostQuery.ToEntityArray(Allocator.Temp);
            var ghostInstances = ghostQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            var ghostPrefabsQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostCollectionPrefab>());
            var ghostPrefabs = ghostPrefabsQuery.GetSingletonBuffer<GhostCollectionPrefab>();
            var ownerLookup = state.GetComponentLookup<GhostOwner>();
            var prespawnSceneLoadedLookup = state.GetBufferLookup<PrespawnSceneLoaded>();

            var ghosts = new GhostStorage();
            ghosts.GhostPrefabs = new NativeArray<GhostPrefabData>(ghostPrefabs.Length, Allocator.Persistent);

            for (int i = 0; i < ghostPrefabs.Length; ++i)
            {
                ghosts.GhostPrefabs[i] = new GhostPrefabData()
                {
                    GhostTypeIndex = i,
                    GhostTypeHash = ghostPrefabs[i].GhostType.GetHashCode()
                };
            }

            // Find the network ID of the local client
            int localNetworkId = 0;
            if (!config.StoreOwnGhosts)
            {
                var networkIdQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
                var networkIds = networkIdQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
                if (networkIds.Length > 0)
                {
                    localNetworkId = networkIds[0].Value;
                }
            }

            var ghostList = new NativeList<GhostData>(ghostInstances.Length, Allocator.Temp);
            for (int i = 0; i < ghostInstances.Length; ++i)
            {
                // Omit the ghosts owned by the host as he's leaving the session
                if (!config.StoreOwnGhosts && ownerLookup.HasComponent(ghostEntities[i]) && ownerLookup[ghostEntities[i]].NetworkId == localNetworkId)
                    continue;
                if (prespawnSceneLoadedLookup.HasBuffer(ghostEntities[i]))
                    continue;

                var ghostData = HostMigration.GetGhostComponentData(m_HostMigrationCache, ref state, ghostEntities[i], ghostInstances[i].ghostType, out hasErrors);

                ghostList.Add(new GhostData()
                {
                    GhostId = ghostInstances[i].ghostId,
                    GhostType = ghostInstances[i].ghostType,
                    Data = ghostData
                });
            }

            ghosts.Ghosts = ghostList.AsArray();

#if USING_UNITY_SERIALIZATION
            if (config.StorageMethod == DataStorageMethod.JsonMinified || config.StorageMethod == DataStorageMethod.Json)
            {
                JsonSerializeGhosts(ref state, ghostDataBlob, config, ghosts);
                return;
            }
            if (config.StorageMethod == DataStorageMethod.Binary)
            {
                BinarySerializeGhosts(ref state, ghostDataBlob, ghosts);
                return;
            }
#endif
            HostMigration.SerializeGhostData(ref ghosts, ghostDataBlob);
            UpdateGhostStats(ref state, ref ghosts);
        }

#if USING_UNITY_SERIALIZATION
        [BurstDiscard]
        unsafe void BinarySerializeGhosts(ref SystemState state, NativeList<byte> ghostDataBlob, GhostStorage ghosts)
        {
            byte[] stringBytes;
            var parameters = new BinarySerializationParameters()
            {
                UserDefinedAdapters = new List<IBinaryAdapter>
                {
                    new NativeArrayBinaryAdapter<byte>(),
                    new NativeArrayBinaryAdapter<GhostPrefabData>(),
                    new NativeArrayBinaryAdapter<GhostData>()
                }
            };

            using var stream = new UnsafeAppendBuffer(16, 8, Allocator.Temp);
            BinarySerialization.ToBinary(&stream, ghosts, parameters);
            var span = new ReadOnlySpan<byte>(stream.Ptr, (int)stream.Length);
            var encoded = Convert.ToBase64String(span);
            stringBytes = Encoding.UTF8.GetBytes(encoded);
            fixed (byte* stringPtr = stringBytes)
            {
                ghostDataBlob.AddRange(stringPtr, stringBytes.Length);
            }
            ghostDataBlob.Add(0);
            UpdateGhostStats(ref state, ref ghosts);
        }

        [BurstDiscard]
        unsafe void JsonSerializeGhosts(ref SystemState state, NativeList<byte> ghostDataBlob, HostMigrationConfig config, GhostStorage ghosts)
        {
            byte[] stringBytes;
            var minified = config.StorageMethod == DataStorageMethod.JsonMinified;
            var parameters = new JsonSerializationParameters
            {
                UserDefinedAdapters = new List<IJsonAdapter>
                {
                    new NativeArrayAdapter<GhostData>(),
                    new NativeArrayAdapter<GhostPrefabData>(),
                    new NativeArrayAdapter<byte>()
                },
                Minified = minified
            };

            var jsonString = JsonSerialization.ToJson(ghosts, parameters);
            if (jsonString.Length > ghostDataBlob.Capacity)
            {
                ghostDataBlob.ResizeUninitialized(2*jsonString.Length);
                ghostDataBlob.Length = 0;
            }
            stringBytes = Encoding.UTF8.GetBytes(jsonString);
            fixed (byte* stringPtr = stringBytes)
            {
                ghostDataBlob.AddRange(stringPtr, stringBytes.Length);
            }
            ghostDataBlob.Add(0);
            UpdateGhostStats(ref state, ref ghosts);
        }
#endif

        void UpdateGhostStats(ref SystemState state, ref GhostStorage ghostStorage)
        {
            var statsQuery = state.GetEntityQuery(ComponentType.ReadWrite<HostMigrationStats>());
            var stats = statsQuery.GetSingletonRW<HostMigrationStats>();
            stats.ValueRW.GhostCount = ghostStorage.Ghosts.Length;
            stats.ValueRW.PrefabCount = ghostStorage.GhostPrefabs.Length;
        }

        /// <summary>
        /// For the given connection entity with this unique ID find any GhostOwner components on spawned ghosts which are
        /// pointing to the network ID the connection previously had, and update it to the current network ID. Also
        /// update the CommandTarget component to point to the new spawned ghost entity.
        /// </summary>
        void ReconnectOwnedGhosts(ref SystemState state, EntityCommandBuffer commandBuffer, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            var ghostOwnerMapQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostOwnerMap>());
            // If the ghost owner map is not yet created there are no spawned ghosts yet (or none with owner exists)
            if (!ghostOwnerMapQuery.TryGetSingleton<GhostOwnerMap>(out var ghostOwnerMap))
                return;
            var mapQuery = state.GetEntityQuery(ComponentType.ReadOnly<ConnectionMap>());
            var connectionMap = mapQuery.GetSingleton<ConnectionMap>();
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance,GhostOwner>();
            var ghostQuery = state.GetEntityQuery(builder);
            var owners = ghostQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            var ghostInstance = ghostQuery.ToComponentDataArray<GhostInstance>(Allocator.Temp);
            if (connectionMap.UniqueIdToPreviousNetworkId.TryGetValue(uniqueId.Value, out var previousNetworkId))
            {
                var newNetworkId = state.EntityManager.GetComponentData<NetworkId>(connectionEntity);
                var ownerEntities = ghostQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < owners.Length; ++i)
                {
                    var ghostEntity = ownerEntities[i];
                    if (ghostOwnerMap.GhostEntityToPreviousNetworkId[ghostEntity] == previousNetworkId)
                        commandBuffer.SetComponent(ownerEntities[i], new GhostOwner { NetworkId = newNetworkId.Value});
                }
            }
        }

        /// <summary>
        /// On the server check if an incoming connection is a known connection reconnecting
        /// which should then be placed in game immediately (as it was so before). Needs to be
        /// done after the connection is ready (has fully connected and has a network ID).
        /// </summary>
        void HandleNetworkStreamInGame(NativeArray<HostConnectionData> hostMigrationConnections, EntityCommandBuffer commandBuffer, Entity connectionEntity, ConnectionUniqueId uniqueId)
        {
            for (int j = 0; j < hostMigrationConnections.Length; ++j)
            {
                var prevConnectionData = hostMigrationConnections[j];
                if (prevConnectionData.UniqueId == uniqueId.Value)
                {
                    Debug.Log($"[HostMigration] Setting connection back to in game uniqueId:{uniqueId.Value}");
                    if (prevConnectionData.NetworkStreamInGame)
                        commandBuffer.AddComponent(connectionEntity, ComponentType.ReadOnly<NetworkStreamInGame>());
                    return;
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ThinClientSimulation)]
    partial struct TrackClientConnectionUniqueIdSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamConnection>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (uniqueId, entity) in SystemAPI.Query<RefRO<NewConnectionUniqueId>>().WithEntityAccess())
            {
                if (SystemAPI.TryGetSingleton<ConnectionUniqueId>(out var connectionUniqueId))
                {
                    if (connectionUniqueId.Value == uniqueId.ValueRO.Value)
                        commandBuffer.DestroyEntity(entity);
                    else
                        SystemAPI.SetSingleton(new ConnectionUniqueId(){Value = uniqueId.ValueRO.Value});
                }
                else
                {
                    commandBuffer.AddComponent(entity, new ConnectionUniqueId() { Value = uniqueId.ValueRO.Value });
                    commandBuffer.RemoveComponent<NewConnectionUniqueId>(entity);
                }
            }
            commandBuffer.Playback(state.EntityManager);
        }
    }
}
