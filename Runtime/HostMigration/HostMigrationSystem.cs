using System;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.LowLevel.StateSave;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Scenes;
using Debug = UnityEngine.Debug;
using Hash128 = Unity.Entities.Hash128;

namespace Unity.NetCode.HostMigration
{
    /// <summary>
    /// Enable the host migration feature. This will enable the host migration systems and
    /// is required for host migration to work.
    /// </summary>
    public struct EnableHostMigration : IComponentData { }

    /// <summary>
    /// This tag is added to ghost and non ghost entities on the new server when they have been respawned after a host migration.
    /// </summary>
    public struct IsMigrated : IComponentData { }

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

    struct HostMigrationStorage : IComponentData
    {
        public HostDataStorage HostData;
        public MigratedDataStorage MigratedEntities;
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
            MigrationTimeout = 30.0f,
            ServerUpdateInterval = 2.0f
        };
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
        /// The size of the last serialized host migration data blob. This is the blob accessed via <see cref="HostMigrationData.Get"/>.
        /// </summary>
        public int UpdateSize;
        /// <summary>
        /// The total size collected so far from the host migration system.
        /// </summary>
        public int TotalUpdateSize;
        /// <summary>
        /// The last time the host migration data blob was updated. Accessed via <see cref="HostMigrationData.Get"/>.
        /// </summary>
        public double LastDataUpdateTime;
    }

    /// <summary>
    /// Add to any entity to migrate the associated components in the NonGhostMigrationComponents buffer to the new host.
    /// A new entity will be created containing the components with thier data but not from the original archetype
    /// its upto the user to manually copy these components back to the correct entity using a user defined linkage method
    /// </summary>
    public struct IncludeInMigration : IComponentData
    {}

    /// <summary>
    /// Used internally to track scene entities which have non-ghost components which will be migrated during
    /// host migrations. These are attached to entities with the <see cref="IncludeInMigration"/> component placed in
    /// a subscene. When a migration occurs the host migration data will be applied to existing scene entities
    /// on the new host (no new entity created), and this identifier is used to establish the matches.
    /// </summary>
    public struct SceneEntityMigrationId : IComponentData
    {
        /// <summary>
        /// Value used to link entities together after a migration has occured
        /// </summary>
        public ulong Value;
    }

    /// <summary>
    /// Singleton buffer to declare which components should be migrated on non ghost entities marked for migration
    /// with the IncludeInMigration component
    /// </summary>
    public struct NonGhostMigrationComponents : IBufferElementData
    {
        /// <summary>
        /// Componet type to migrate
        /// </summary>
        public ComponentType ComponentToMigrate;
    }

    struct HostDataStorage
    {
        public NativeArray<HostConnectionData> Connections;
        public NativeArray<HostSubSceneData> SubScenes;
        public HostMigrationConfig Config;
        public double ElapsedTime;
        public NetworkTick ServerTick;
        public double ElapsedNetworkTime;
        public int NextNewGhostId;
        public int NextNewPrespawnGhostId;
        public NativeArray<HostPrespawnGhostIdRangeData> PrespawnGhostIdRanges;
        public int HostNetworkId;
        public int NumNetworkIds;
        public NativeArray<int> FreeNetworkIds;
    }

    struct MigratedDataStorage
    {
        public NativeArray<GhostData> Ghosts;
        public NativeArray<GhostPrefabData> GhostPrefabs;
        public NativeArray<MigratedEntityData> Entities;
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
        /// The spawn tick of this ghost
        /// </summary>
        public NetworkTick SpawnTick;
        /// <summary>
        /// The component data for each ghost component
        /// </summary>
        public NativeArray<DataComponent> DataComponents;
    }

    struct MigratedEntityData
    {
        /// <summary>
        /// The component data for each migrated component
        /// </summary>
        public NativeArray<DataComponent> MigratedDataComponents;
    }

    struct DataComponent
    {
        public ulong StableHash;
        public int Length;
        public bool Enabled;
        public NativeArray<byte> Data;
    }

    struct HostSubSceneData
    {
        public Hash128 SubSceneGuid;
    }

    struct HostPrespawnGhostIdRangeData
    {
        // the scene for which the range is applied to
        public ulong SubSceneHash;
        // the first id in the range
        public int FirstGhostId;
    }

    struct HostConnectionData
    {
        // NOTE: - transport already exchanges a unique connection token but it is internal connection data atm
        //       - transport NetworkConnection also already has a unique ConnectionId but it is also internal
        //         (consists of ID+Version, so a reused ID 0 will be Id=0,Version=2 which together will be unique)
        //       - this could simply be an incrementing integer, only difference to NetworkId is that it's never re-used
        //         throughout session, but this seems like duplicated data (we already have this but just internal transport data)
        public uint UniqueId;               // Unique ID to know what ghosts you owned before
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
    /// This system monitors for the host migration request and handles the actual
    /// migration itself with the data set in the HostMigrationData class. It also
    /// gathers the migration data for sending to the lobby (the sender will
    /// monitor the update timer on the data for detecting when new data is ready).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GhostSendSystem))]  // Send system assigns GhostID/GhostType on new entity instantiations (we need those set/ready in the host migration data)
    [BurstCompile]
    partial struct ServerHostMigrationSystem : ISystem
    {
        EntityQuery m_InGameQuery;
        EntityQuery m_ConnectionQuery;
        EntityQuery m_SubsceneQuery;

        EntityQuery m_NonGhostMigrationEntities;

        EntityStorageInfoLookup m_EntityStorageInfo;
        ComponentLookup<NetworkId> m_NetworkIdsLookup;
        ComponentLookup<ConnectionUniqueId> m_UniqueIdsLookup;

        double m_MigrationTime;
        double m_LastServerUpdate;
        NativeHashMap<uint, int> m_NetworkIdMap;
        NativeArray<ComponentType> m_DefaultComponents;
        HostMigrationData.Data m_HostMigrationCache;
        int m_LastSeenGhostPrefabCount;
        NativeHashSet<ComponentType> m_GhostComponentTypes;
        NativeHashSet<ComponentType> m_RequiredGhostComponentTypes;
        NativeHashSet<ComponentType> m_InputBuffers;
        NativeHashSet<ComponentType> m_RequiredMigrationEntityComponentTypes;
        NativeList<GhostPrefabData> m_GhostPrefabData;
        WorldStateSave m_GhostWorldStateSave;
        WorldStateSave m_MigrationEntitiesWorldStateSave;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<HostMigrationConfig>();
            m_HostMigrationCache = new HostMigrationData.Data();
            m_HostMigrationCache.ServerOnlyComponentsFlag = new NativeList<int>(64, Allocator.Persistent);
            m_HostMigrationCache.ServerOnlyComponentsPerGhostType = new NativeHashMap<int, NativeList<ComponentType>>(64, Allocator.Persistent);

            m_DefaultComponents = new NativeArray<ComponentType>(16, Allocator.Persistent);
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
            m_DefaultComponents[13] = ComponentType.ReadOnly<NetworkStreamIsReconnected>();
            m_DefaultComponents[14] = ComponentType.ReadOnly<IsMigrated>();
            m_DefaultComponents[15] = ComponentType.ReadOnly<EnablePacketLogging>();

            m_GhostComponentTypes = new NativeHashSet<ComponentType>(16, Allocator.Persistent);
            m_RequiredGhostComponentTypes = new NativeHashSet<ComponentType>(1, Allocator.Persistent);
            m_RequiredGhostComponentTypes.Add(ComponentType.ReadOnly<GhostInstance>());
            m_RequiredMigrationEntityComponentTypes = new NativeHashSet<ComponentType>(1, Allocator.Persistent);
            m_RequiredMigrationEntityComponentTypes.Add(ComponentType.ReadOnly<IncludeInMigration>());
            m_GhostPrefabData = new NativeList<GhostPrefabData>(Allocator.Persistent);
            m_InputBuffers = new NativeHashSet<ComponentType>(16, Allocator.Persistent);

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

            builder.Reset();
            builder.WithAll<IncludeInMigration>();
            m_NonGhostMigrationEntities = state.GetEntityQuery(builder);

            if (!SystemAPI.TryGetSingleton(out HostMigrationConfig _))
            {
                var entityConfig = state.EntityManager.CreateEntity(ComponentType.ReadWrite<HostMigrationConfig>());
                state.EntityManager.SetName(entityConfig,"HostMigrationConfig");

                state.EntityManager.SetComponentData(entityConfig, HostMigrationConfig.Default);
            }

            var statsEntity = state.EntityManager.CreateEntity(ComponentType.ReadOnly<HostMigrationStats>());
            state.EntityManager.SetName(statsEntity, "HostMigrationStats");
            state.EntityManager.CreateSingleton<HostMigrationStorage>();
            var hostMigrationData = SystemAPI.GetSingletonRW<HostMigrationStorage>();
            hostMigrationData.ValueRW.HostDataBlob = new NativeList<byte>(Allocator.Persistent);
            hostMigrationData.ValueRW.GhostDataBlob = new NativeList<byte>(Allocator.Persistent);

            m_GhostWorldStateSave = new WorldStateSave(Allocator.Persistent);
            m_MigrationEntitiesWorldStateSave = new WorldStateSave(Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            var hostMigrationData = SystemAPI.GetSingletonRW<HostMigrationStorage>();
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
            hostMigrationData.ValueRW.HostData.PrespawnGhostIdRanges.Dispose();
            hostMigrationData.ValueRW.HostData.FreeNetworkIds.Dispose();
            hostMigrationData.ValueRW.MigratedEntities.GhostPrefabs.Dispose();
            for (int i = 0; i < hostMigrationData.ValueRW.MigratedEntities.Ghosts.Length; i++)
                hostMigrationData.ValueRW.MigratedEntities.Ghosts[i].DataComponents.Dispose();
            for (int i = 0; i < hostMigrationData.ValueRW.MigratedEntities.Entities.Length; i++)
                hostMigrationData.ValueRW.MigratedEntities.Entities[i].MigratedDataComponents.Dispose();
            hostMigrationData.ValueRW.MigratedEntities.Ghosts.Dispose();
            hostMigrationData.ValueRW.MigratedEntities.Entities.Dispose();
            m_HostMigrationCache.ServerOnlyComponentsFlag.Dispose();
            foreach (var componentList in m_HostMigrationCache.ServerOnlyComponentsPerGhostType.GetValueArray(Allocator.Temp))
                componentList.Dispose();
            m_HostMigrationCache.ServerOnlyComponentsPerGhostType.Dispose();
            m_NetworkIdMap.Dispose();
            m_DefaultComponents.Dispose();
            m_GhostComponentTypes.Dispose();
            m_RequiredGhostComponentTypes.Dispose();
            m_RequiredMigrationEntityComponentTypes.Dispose();
            m_GhostPrefabData.Dispose();
            m_InputBuffers.Dispose();
            m_GhostWorldStateSave.Dispose();
            m_MigrationEntitiesWorldStateSave.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            var hostMigrationData = SystemAPI.GetSingleton<HostMigrationStorage>();

            // Restore the connection data from previous session, this happens after accepting the connection
            foreach (var (uniqueId, migrate, entity) in SystemAPI.Query<RefRO<ConnectionUniqueId>, RefRW<MigrateComponents>>().WithEntityAccess())
            {
                // First we need to add the component, causing a structural change
                if (migrate.ValueRW.Step == 0)
                {
                    migrate.ValueRW.Step++;
                    HostMigrationData.HandleReconnection(hostMigrationData.HostData.Connections, commandBuffer, entity, uniqueId.ValueRO);
                }
                // After the component is added to the connection entity we can copy the migrated connection data
                else if (migrate.ValueRW.Step == 1)
                {
                    commandBuffer.RemoveComponent<MigrateComponents>(entity);
                    HostMigrationData.RestoreConnectionComponentData(hostMigrationData.HostData.Connections, state.EntityManager, entity, uniqueId.ValueRO);
                }
            }

            CacheGhostComponentTypes(ref state);

            var config = SystemAPI.GetSingleton<HostMigrationConfig>();
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

                // Start loading the entity scenes as soon as the prespawn list prefab has been created
                if (migrationRequest.ServerSubScenes.Length == 0 && hostMigrationData.HostData.SubScenes.Length > 0)
                {
                    var sceneEntities = new NativeArray<Entity>(hostMigrationData.HostData.SubScenes.Length, Allocator.Persistent);
                    for (int i = 0; i < hostMigrationData.HostData.SubScenes.Length; ++i)
                    {
                        Debug.Log($"[HostMigration] Server world loading {hostMigrationData.HostData.SubScenes[i].SubSceneGuid}");
                        sceneEntities[i] = SceneSystem.LoadSceneAsync(state.WorldUnmanaged, hostMigrationData.HostData.SubScenes[i].SubSceneGuid);
                    }
                    migrationRequest.ServerSubScenes = sceneEntities;
                    SystemAPI.SetSingleton(migrationRequest);
                    return;
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
                    SpawnAllGhosts(ref state, hostMigrationData.MigratedEntities.Ghosts, hostMigrationData.MigratedEntities.GhostPrefabs);
                    SpawnAllMigratedEntities(ref state, hostMigrationData.MigratedEntities.Entities);
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
                        Debug.LogError($"Host migration failed. Did not load all ghost prefabs (expected {hostMigrationData.MigratedEntities.GhostPrefabs.Length} but only have {ghostCollection.NumLoadedPrefabs})");
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

                if (m_GhostWorldStateSave.Initialized)
                    m_GhostWorldStateSave.Reset();
                m_GhostWorldStateSave.RequiredTypesToSaveConfig = m_RequiredGhostComponentTypes;
                m_GhostWorldStateSave.OptionalTypesToSaveConfig = m_GhostComponentTypes;
                m_GhostWorldStateSave.Initialize(ref state);
                var ghostsStateSaveJob = m_GhostWorldStateSave.ScheduleStateSaveJob(ref state);

                if (m_MigrationEntitiesWorldStateSave.Initialized)
                    m_MigrationEntitiesWorldStateSave.Reset();
                m_MigrationEntitiesWorldStateSave.RequiredTypesToSaveConfig = m_RequiredMigrationEntityComponentTypes;

                var optionalNonGhostComponents = new NativeHashSet<ComponentType>(0, Allocator.Persistent);
                if ( SystemAPI.TryGetSingletonBuffer<NonGhostMigrationComponents>(out var migrationComponentsBuffer, true) )
                {
                    foreach ( var migrationComponent in migrationComponentsBuffer )
                    {
                        optionalNonGhostComponents.Add( migrationComponent.ComponentToMigrate );
                    }
                }

                optionalNonGhostComponents.Add(ComponentType.ReadOnly<SceneEntityMigrationId>());
                // We need to add the GhostInstance as optional so we can use it to copy back any migrated non-ghost component data to the correct ghost entity after the migration
                optionalNonGhostComponents.Add(ComponentType.ReadOnly<GhostInstance>());

                m_MigrationEntitiesWorldStateSave.OptionalTypesToSaveConfig = optionalNonGhostComponents;
                m_MigrationEntitiesWorldStateSave.Initialize(ref state);
                var migratedEntitiesWorldStateSaveJob = m_MigrationEntitiesWorldStateSave.ScheduleStateSaveJob(ref state);

                var updateJob = new GatherGhostsAndMigrationStatsJob()
                {
                    GhostsStateSave = m_GhostWorldStateSave,
                    MigrationEntitesStateSave = m_MigrationEntitiesWorldStateSave,
                    GhostPrefabs = m_GhostPrefabData,
                    GhostDataBlob = hostMigrationData.GhostDataBlob,
                    Stats = SystemAPI.GetSingletonRW<HostMigrationStats>(),
                    UpdateTime = state.WorldUnmanaged.Time.ElapsedTime,
                    HostDataSize = hostMigrationData.HostDataBlob.Length,
                };
                state.Dependency = updateJob.Schedule(JobHandle.CombineDependencies(state.Dependency, ghostsStateSaveJob, migratedEntitiesWorldStateSaveJob));

                state.Dependency.Complete();
            }

            // For new connections check if they previously owned any ghosts
            var connectionEventsForTick = SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;
            for (int i = 0; i < connectionEventsForTick.Length; ++i)
            {
                if (connectionEventsForTick[i].State == ConnectionState.State.Connected)
                {
                    var evt = connectionEventsForTick[i];
                    var uniqueIdLookup = SystemAPI.GetComponentLookup<ConnectionUniqueId>();
                    HandleNetworkStreamInGame(hostMigrationData.HostData.Connections, commandBuffer, evt.ConnectionEntity, uniqueIdLookup[evt.ConnectionEntity]);
                }
            }
            commandBuffer.Playback(state.EntityManager);
        }

        /// <summary>
        /// When the ghost prefab count changes check which ghost components are now being referenced in these
        /// ghosts, cache this in a hashset so we don't need to do this again
        /// </summary>
        void CacheGhostComponentTypes(ref SystemState state)
        {
            var ghostPrefabsBuffer = SystemAPI.GetSingletonBuffer<GhostCollectionPrefab>();
            if (ghostPrefabsBuffer.Length != m_LastSeenGhostPrefabCount)
            {
                m_LastSeenGhostPrefabCount = ghostPrefabsBuffer.Length;
                m_GhostComponentTypes.Clear();
                m_GhostPrefabData.Clear();

                // Move the ghost component types into a hashset for quicker lookup
                var ghostComponentCollection = SystemAPI.GetSingletonBuffer<GhostComponentSerializer.State>();
                var ghostComponentTypes = new NativeHashSet<ComponentType>(ghostComponentCollection.Length, Allocator.Temp);
                foreach (var ghostComponent in ghostComponentCollection)
                {
                    if (ghostComponent.ComponentType.IsBuffer && IsInputBuffer(ghostComponent.ComponentType))
                        continue;
                    ghostComponentTypes.Add(ghostComponent.ComponentType);
                }

                for (int i = 0; i < ghostPrefabsBuffer.Length; ++i)
                {
                    m_GhostPrefabData.Add(new GhostPrefabData()
                    {
                        GhostTypeIndex = i,
                        GhostTypeHash = ghostPrefabsBuffer[i].GhostType.GetHashCode()
                    });

                    foreach (var usedComponent in state.EntityManager.GetComponentTypes(ghostPrefabsBuffer[i].GhostPrefab))
                    {
                        if (ghostComponentTypes.Contains(usedComponent))
                            m_GhostComponentTypes.Add(usedComponent);
                    }
                }
            }
        }

        bool IsInputBuffer(ComponentType componentType)
        {
            if (m_InputBuffers.Contains(componentType))
                return true;
            var collectionData = SystemAPI.GetSingleton<GhostComponentSerializerCollectionData>();
            bool isInputBuffer = false;
            var componentTypeRW = componentType;
            componentTypeRW.AccessModeType = ComponentType.AccessMode.ReadWrite;
            foreach (var componentMapping in collectionData.InputComponentBufferMap)
            {
                if (componentMapping.Value == componentTypeRW)
                {
                    m_InputBuffers.Add(componentType);
                    isInputBuffer = true;
                    break;
                }
            }
            return isInputBuffer;
        }

        NativeList<Entity> SpawnAllGhosts(ref SystemState state, NativeArray<GhostData> ghosts, NativeArray<GhostPrefabData> ghostPrefabs)
        {
            var ghostEntities = new NativeList<Entity>(ghosts.Length, Allocator.Temp);
            // Create ghost type mapping in case the ghost type indexes are not the same (subscene load ordering changed for example)
            var ghostTypeMap = CreateGhostTypeMap(ghostPrefabs);

            // save the ghostIds we have used so we can mark unused ids as free once we have added all the override components
            var hostMigrationData = SystemAPI.GetSingleton<HostMigrationStorage>();
            NativeBitArray migratedGhostIds = new NativeBitArray(hostMigrationData.HostData.NextNewGhostId, Allocator.Temp);

            for (int i = 0; i < ghosts.Length; ++i)
            {
                var entity = Entity.Null;
                var ghost = ghosts[i];
                int ghostType = ghostTypeMap[ghost.GhostType];

                if (!PrespawnHelper.IsPrespawnGhostId(ghost.GhostId))
                {
                    if (ghostTypeMap.Count <= ghost.GhostType)
                    {
                        Debug.LogError($"Did not find migrated ghost type {ghost.GhostType} in the current servers ghost type list (count={ghostTypeMap.Count})");
                        return new NativeList<Entity>(0, Allocator.Temp);
                    }
                    var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
                    var buffer = state.EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);
                    entity = state.EntityManager.Instantiate(buffer[ghostType].GhostPrefab);

                    if ( ghost.GhostId == 0 )
                    {
                        Debug.LogError($"Received a migrated ghost with an id of 0 this should not be possible. GhostIds are assigned by the GhostSendSystem and this should always run before the migration system ensuring all ghosts have a valid id.");
                    }

                    state.EntityManager.AddComponentData(entity, new OverrideGhostData() { GhostId = ghost.GhostId, SpawnTick = ghost.SpawnTick });
                    migratedGhostIds.Set( ghost.GhostId, true );
                    ghostEntities.Add(entity);
                }
                else
                {
                    foreach (var (ghostId, prespawnEntity) in SystemAPI.Query<RefRO<GhostInstance>>().WithAll<PreSpawnedGhostIndex>().WithEntityAccess())
                    {
                        if (ghostId.ValueRO.ghostId == ghost.GhostId)
                        {
                            entity = prespawnEntity;
                            break;
                        }
                    }

                    if ( entity == Entity.Null)
                    {
                        Debug.LogError($"Trying to migrate prespawn entity with id {ghost.GhostId} but it's scene isn't/hasn't been loaded. This is usually caused by unloading/reordering of subscenes before a migration. Currently this is unsupported.");
                    }
                }

                SetEntityComponentData(ref state, entity, ghost.DataComponents, ghostType);
                state.EntityManager.AddComponent<IsMigrated>(entity);
            }

            // after instancing all the migrated ghosts we have added the override components so we move any unused ids back to the free list
            // this will keep the ids from wandering after multiple migrations
            var spawnedGhostEntityMapData = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>();

            for (int i = 1; i < hostMigrationData.HostData.NextNewGhostId; ++i) // start on 1 since GhostId 0 is not a valid id
            {
                if (!migratedGhostIds.IsSet(i))
                {
                    spawnedGhostEntityMapData.ValueRW.m_ServerFreeGhostIds.Enqueue(i);
                }
            }

            return ghostEntities;
        }

        unsafe void SpawnAllMigratedEntities(ref SystemState state, NativeArray<MigratedEntityData> migratedEntites)
        {
            var sceneEntityMigrationIdTypeInfo = TypeManager.GetTypeInfo(ComponentType.ReadOnly<SceneEntityMigrationId>().TypeIndex);
            var sceneEntityMigrationIdStableHash = sceneEntityMigrationIdTypeInfo.StableTypeHash;

            var ghostInstanceStableHash = TypeManager.GetTypeInfo(ComponentType.ReadOnly<GhostInstance>().TypeIndex).StableTypeHash;

            foreach ( var migratedEntity in migratedEntites )
            {
                ulong entityMigrationId = 0;
                GhostInstance ghostInstanceInMigrationData = default;
                for (int i = 0; i < migratedEntity.MigratedDataComponents.Length; ++i)
                {
                    if (migratedEntity.MigratedDataComponents[i].StableHash == sceneEntityMigrationIdStableHash)
                    {
                        var componentData = migratedEntity.MigratedDataComponents[i];
                        // Extract the ID value from the data component byte array
                        unsafe
                        {
                            SceneEntityMigrationId sceneEntityMigrationId = default;
                            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref sceneEntityMigrationId), componentData.Data.GetUnsafeReadOnlyPtr(), componentData.Data.Length);
                            entityMigrationId = sceneEntityMigrationId.Value;
                        }
                    }

                    if (migratedEntity.MigratedDataComponents[i].StableHash == ghostInstanceStableHash)
                    {
                        var componentData = migratedEntity.MigratedDataComponents[i];
                        unsafe
                        {
                            UnsafeUtility.MemCpy(UnsafeUtility.AddressOf(ref ghostInstanceInMigrationData), componentData.Data.GetUnsafeReadOnlyPtr(), componentData.Data.Length);
                        }
                    }
                }
                // Scene based entities do not need to be created as they're already there loaded from the subscene,
                // only apply the data on the entity which has the same migration identifier
                Entity entity = Entity.Null;
                if (entityMigrationId != 0)
                {
                    foreach (var (migrationId, sceneEntity) in SystemAPI.Query<RefRO<SceneEntityMigrationId>>().WithEntityAccess())
                    {
                        if (migrationId.ValueRO.Value == entityMigrationId)
                        {
                            entity = sceneEntity;
                            break;
                        }
                    }
                    if (entity == Entity.Null)
                    {
                        Debug.LogError($"Trying to migrate subscene based entity with migration id {entityMigrationId} but it's scene isn't/hasn't been loaded.");
                        continue;
                    }
                }
                else if (ghostInstanceInMigrationData.ghostId != 0)
                {
                    // find the ghost and set that as the entity
                    foreach (var (ghostInstance, ghostEntity) in SystemAPI.Query<RefRO<GhostInstance>>().WithEntityAccess())
                    {
                        if ( ghostInstance.ValueRO.ghostId == ghostInstanceInMigrationData.ghostId && ghostInstance.ValueRO.spawnTick == ghostInstanceInMigrationData.spawnTick )
                        {
                            entity = ghostEntity;
                            break;
                        }
                    }
                    Assert.IsTrue(entity != Entity.Null);
                }
                else
                {
                    entity = state.EntityManager.CreateEntity();
                    for (int i = 0; i < migratedEntity.MigratedDataComponents.Length; ++i)
                    {
                        var dataComponent = migratedEntity.MigratedDataComponents[i];
                        var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(dataComponent.StableHash);
                        state.EntityManager.AddComponent(entity, ComponentType.FromTypeIndex(typeIndex));
                    }

                    state.EntityManager.AddComponentData(entity, new IncludeInMigration() { });
                }

                state.EntityManager.AddComponent<IsMigrated>(entity);
                SetEntityComponentData(ref state, entity, migratedEntity.MigratedDataComponents);
            }
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

        unsafe void SetEntityComponentData(ref SystemState state, Entity entity, NativeArray<DataComponent> componentDatas, int ghostType = 0)
        {
            var chunk = state.EntityManager.GetChunk(entity);
            var entityStorageInfo = state.GetEntityStorageInfoLookup();
            var indexInChunk = entityStorageInfo[entity].IndexInChunk;

            foreach (var componentData in componentDatas)
            {
                var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(componentData.StableHash);
                var typeInfo = TypeManager.GetTypeInfo(typeIndex);
                var componentSize = typeInfo.SizeInChunk;
                var componentType = ComponentType.ReadWrite(typeIndex);
                var typeHandle = state.EntityManager.GetDynamicComponentTypeHandle(componentType);
                var ghostDataPtr = componentData.Data.GetUnsafePtr();

                if (!chunk.Has(ref typeHandle))
                {
                    if ( ghostType == 0 )
                    {
                        Debug.LogError($"Component {componentType} not found on entity {entity.ToFixedString()} while trying to migrate component data");
                    }
                    else
                    {
                        Debug.LogError($"Component {componentType} not found on ghost entity {entity.ToFixedString()} ghost type {ghostType} while trying to migrate ghost component data");
                    }

                    continue;
                }

                if (componentType.IsEnableable)
                    chunk.SetComponentEnabled(ref typeHandle, indexInChunk, componentData.Enabled);

                if (!componentType.IsBuffer)
                {
                    if (componentSize != 0)
                    {
                        int offset = indexInChunk * componentSize;
                        var compDataPtr = (byte*)chunk
                            .GetDynamicComponentDataArrayReinterpret<byte>(ref typeHandle, componentSize)
                            .GetUnsafeReadOnlyPtr() + offset;
                        UnsafeUtility.MemCpy(compDataPtr, ghostDataPtr, componentSize);
                    }
                }
                else
                {
                    // Deserialize buffer, the new ghosts will have 0 elements to start with
                    var bufferData = chunk.GetUntypedBufferAccessor(ref typeHandle);
                    var length = componentData.Length;
                    if (length > 0)
                    {
                        bufferData.ResizeUninitialized(indexInChunk, length);
                        var bufferPtr = bufferData.GetUnsafePtr(indexInChunk);
                        UnsafeUtility.MemCpy(bufferPtr, ghostDataPtr, length * componentSize);
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
            migrationData.ServerTick = networkTime.ServerTick;
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

            // Get the highest allocated ghost id, we use this to start the migrated server at the same value
            // so no new ghosts are created with clashing GhostIds when we migrate them
            migrationData.NextNewGhostId = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().m_ServerAllocatedGhostIds[0];
            migrationData.NextNewPrespawnGhostId = SystemAPI.GetSingleton<SpawnedGhostEntityMap>().m_ServerAllocatedGhostIds[1];

            // Collect the Prespawn GhostId Ranges, these are used to ensure prespans are given matching Ids between migrations
            if (SystemAPI.HasSingleton<PrespawnGhostIdRange>())
            {
                var prespawnGhostIdRanges = SystemAPI.GetBuffer<PrespawnGhostIdRange>(SystemAPI.GetSingletonEntity<PrespawnGhostIdRange>());

                migrationData.PrespawnGhostIdRanges = new NativeArray<HostPrespawnGhostIdRangeData>(prespawnGhostIdRanges.Length, Allocator.Persistent);
                for ( int i=0; i< prespawnGhostIdRanges.Length; ++i )
                {
                    migrationData.PrespawnGhostIdRanges[i] = new HostPrespawnGhostIdRangeData()
                    {
                        SubSceneHash = prespawnGhostIdRanges[i].SubSceneHash,
                        FirstGhostId = prespawnGhostIdRanges[i].FirstGhostId
                        // We don't need to copy the count here as it will be reassigned correctly in ServerPopulatePrespawnedGhostsSystem::AllocatePrespawnGhostRange
                    };
                }
            }

            // Write the local/host network ID
            var networkIdQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
            var networkIds = networkIdQuery.ToComponentDataArray<NetworkId>(Allocator.Temp);
            var localNetworkId = 0;
            if (networkIds.Length > 0)
                localNetworkId = networkIds[0].Value;
            migrationData.HostNetworkId = localNetworkId;

            migrationData.NumNetworkIds = SystemAPI.GetSingleton<NetworkIDAllocationData>().NumNetworkIds.Value;
            migrationData.FreeNetworkIds.Dispose();
            migrationData.FreeNetworkIds = SystemAPI.GetSingleton<NetworkIDAllocationData>().FreeNetworkIds.ToArray(Allocator.Persistent);

            hostDataBlob.Clear();

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
                dataStreamWriter.WriteByte(migrationData.Config.StoreOwnGhosts ? (byte)1 : (byte)0);
                dataStreamWriter.WriteFloat(migrationData.Config.MigrationTimeout);
                dataStreamWriter.WriteFloat(migrationData.Config.ServerUpdateInterval);
                dataStreamWriter.WriteDouble(migrationData.ElapsedTime);
                dataStreamWriter.WriteUInt(migrationData.ServerTick.SerializedData);
                dataStreamWriter.WriteDouble(migrationData.ElapsedNetworkTime);

                dataStreamWriter.WriteInt(migrationData.NextNewGhostId);
                dataStreamWriter.WriteInt(migrationData.NextNewPrespawnGhostId);

                dataStreamWriter.WriteShort((short)migrationData.PrespawnGhostIdRanges.Length);
                foreach (var idData in migrationData.PrespawnGhostIdRanges)
                {
                    dataStreamWriter.WriteULong(idData.SubSceneHash);
                    dataStreamWriter.WriteInt(idData.FirstGhostId);
                }

                dataStreamWriter.WriteInt(migrationData.HostNetworkId);
                dataStreamWriter.WriteInt(migrationData.NumNetworkIds);
                dataStreamWriter.WriteInt(migrationData.FreeNetworkIds.Length);
                foreach ( var fid in migrationData.FreeNetworkIds)
                    dataStreamWriter.WriteInt(fid);
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

    [BurstCompile]
    internal struct GatherGhostsAndMigrationStatsJob : IJob
    {
        [ReadOnly] public WorldStateSave GhostsStateSave;
        [ReadOnly] public WorldStateSave MigrationEntitesStateSave;
        [NativeDisableUnsafePtrRestriction] public RefRW<HostMigrationStats> Stats;
        public NativeList<byte> GhostDataBlob;
        public double UpdateTime;
        public int HostDataSize;
        [ReadOnly] public NativeList<GhostPrefabData> GhostPrefabs;

        [BurstCompile]
        public void Execute()
        {
            unsafe
            {
                GhostDataBlob.Clear();
                if (GhostsStateSave.EntityCount != 0 || MigrationEntitesStateSave.EntityCount != 0)
                {
                    // Use double estimated size, the actual size could be bigger than the estimate
                    var requiredSize = 2 * (MigrationEntitesStateSave.Size + GhostsStateSave.Size + GhostPrefabs.Length * sizeof(GhostPrefabData) + 2 * sizeof(int));
                    if (GhostDataBlob.Capacity < requiredSize)
                        GhostDataBlob.Resize(2 * requiredSize, NativeArrayOptions.ClearMemory);
                    GhostDataBlob.Length = GhostDataBlob.Capacity;
                    var writer = new DataStreamWriter(GhostDataBlob.AsArray());

                    // Write prefab data
                    var ghostPrefabWriter = writer;
                    writer.WriteInt(0);
                    writer.WriteInt(GhostsStateSave.AsSpan.Length);
                    writer.WriteInt(MigrationEntitesStateSave.AsSpan.Length);
                    writer.WriteShort((short)GhostPrefabs.Length);
                    foreach (var ghostPrefab in GhostPrefabs)
                    {
                        writer.WriteShort((short)ghostPrefab.GhostTypeIndex);
                        writer.WriteInt(ghostPrefab.GhostTypeHash);
                    }

                    ghostPrefabWriter.WriteInt(writer.Length);

                    // Copy ghost data from the state save in front of the ghost prefab data
                    var ghostPrefabDataSize = writer.Length;
                    var ghostDataInBlob = new Span<byte>(GhostDataBlob.GetUnsafePtr() + ghostPrefabDataSize, GhostsStateSave.AsSpan.Length);
                    GhostsStateSave.AsSpan.CopyTo(ghostDataInBlob);

                    // Copy migration entity data in front of the ghost data
                    var nonGhostDataInBlob = new Span<byte>(GhostDataBlob.GetUnsafePtr() + ghostPrefabDataSize + GhostsStateSave.AsSpan.Length, MigrationEntitesStateSave.AsSpan.Length);
                    MigrationEntitesStateSave.AsSpan.CopyTo(nonGhostDataInBlob);
                    GhostDataBlob.Length = ghostPrefabDataSize + GhostsStateSave.AsSpan.Length + MigrationEntitesStateSave.AsSpan.Length;
                }

                Stats.ValueRW.PrefabCount = GhostPrefabs.Length;
                Stats.ValueRW.GhostCount = GhostsStateSave.EntityCount;
                Stats.ValueRW.LastDataUpdateTime = UpdateTime;
                var updateSize = HostDataSize + GhostDataBlob.Length;
                Stats.ValueRW.UpdateSize = updateSize;
                Stats.ValueRW.TotalUpdateSize += updateSize;
            }
        }
    }
}
