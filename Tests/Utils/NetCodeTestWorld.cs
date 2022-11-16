using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Collections;

using Unity.Logging;
using Unity.Logging.Sinks;
using Unity.Transforms;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEngine;
#endif

namespace Unity.NetCode.Tests
{
    public struct NetCodeTestPrefabCollection : IComponentData
    {}
    public struct NetCodeTestPrefab : IBufferElementData
    {
        public Entity Value;
    }

    public class NetCodeTestWorld : IDisposable, INetworkStreamDriverConstructor
    {
        public bool DebugPackets = false;

        public World DefaultWorld => m_DefaultWorld;
        public World ServerWorld => m_ServerWorld;
        public World[] ClientWorlds => m_ClientWorlds;

        private World m_DefaultWorld;
        private World[] m_ClientWorlds;
        private World m_ServerWorld;
        private ushort m_OldBootstrapAutoConnectPort;
        private bool m_DefaultWorldInitialized;
        private double m_ElapsedTime;
        public int DriverFixedTime = 16;
        public int DriverSimulatedDelay = 0;
        public int DriverSimulatedJitter = 0;
        public int DriverSimulatedDrop = 0;
        public int UseMultipleDrivers = 0;
        public int UseFakeSocketConnection = 1;
        private int WorldCreationIndex = 0;

        public int[] DriverFuzzFactor;
        public int DriverFuzzOffset = 0;
        public uint DriverRandomSeed = 0;

#if UNITY_EDITOR
        private List<GameObject> m_GhostCollection;
        private BlobAssetStore m_BlobAssetStore;
#endif
        public List<string> NetCodeAssemblies = new List<string> { };

        private static void ForwardUnityLoggingToDebugLog()
        {
            static void AddUnityDebugLogSink(Unity.Logging.Logger logger)
            {
                logger.GetOrCreateSink<UnityDebugLogSink>(new UnityDebugLogSink.Configuration(logger.Config.WriteTo, LogFormatterText.Formatter,
                    minLevelOverride: logger.MinimalLogLevelAcrossAllSystems, outputTemplateOverride: "{Message}"));
            }

            Unity.Logging.Internal.LoggerManager.OnNewLoggerCreated(AddUnityDebugLogSink);
            Unity.Logging.Internal.LoggerManager.CallForEveryLogger(AddUnityDebugLogSink);

            // Self log enabled, so any error inside logging will cause Debug.LogError -> failed test
            Unity.Logging.Internal.Debug.SelfLog.SetMode(Unity.Logging.Internal.Debug.SelfLog.Mode.EnabledInUnityEngineDebugLogError);
        }

        public NetCodeTestWorld()
        {
#if UNITY_EDITOR

            // Not having a default world means RegisterUnloadOrPlayModeChangeShutdown has not been called which causes memory leaks
            DefaultWorldInitialization.DefaultLazyEditModeInitialize();
#endif
            m_OldBootstrapAutoConnectPort = ClientServerBootstrap.AutoConnectPort;
            ClientServerBootstrap.AutoConnectPort = 0;
            m_DefaultWorld = new World("NetCodeTest");
            m_ElapsedTime = 42;

            ForwardUnityLoggingToDebugLog();
        }

        public void Dispose()
        {
            if (m_ClientWorlds != null)
            {
                for (int i = 0; i < m_ClientWorlds.Length; ++i)
                {
                    if (m_ClientWorlds[i] != null)
                    {
                        m_ClientWorlds[i].Dispose();
                    }
                }
            }

            if (m_ServerWorld != null)
                m_ServerWorld.Dispose();
            if (m_DefaultWorld != null)
                m_DefaultWorld.Dispose();
            m_ClientWorlds = null;
            m_ServerWorld = null;
            m_DefaultWorld = null;
            ClientServerBootstrap.AutoConnectPort = m_OldBootstrapAutoConnectPort;

#if UNITY_EDITOR
            if (m_GhostCollection != null)
                m_BlobAssetStore.Dispose();
#endif
        }

        public void DisposeAllClientWorlds()
        {
            for (int i = 0; i < m_ClientWorlds.Length; ++i)
            {
                m_ClientWorlds[i].Dispose();
            }

            m_ClientWorlds = null;
        }

        public void DisposeServerWorld()
        {
            m_ServerWorld.Dispose();
            m_ServerWorld = null;
        }

        public void DisposeDefaultWorld()
        {
            m_DefaultWorld.Dispose();
            m_DefaultWorld = null;
        }

        public void SetServerTick(NetworkTick tick)
        {
            var ent = TryGetSingletonEntity<NetworkTime>(m_ServerWorld);
            var networkTime = m_ServerWorld.EntityManager.GetComponentData<NetworkTime>(ent);
            networkTime.ServerTick = tick;
            m_ServerWorld.EntityManager.SetComponentData(ent, networkTime);
        }

        public NetworkTime GetNetworkTime(World world)
        {
            var ent = TryGetSingletonEntity<NetworkTime>(world);
            return world.EntityManager.GetComponentData<NetworkTime>(ent);
        }

        private static IReadOnlyList<Type> s_AllClientSystems;
        private static IReadOnlyList<Type> s_AllThinClientSystems;
        private static IReadOnlyList<Type> s_AllServerSystems;
        private static List<Type> s_NetCodeClientSystems;
        private static List<Type> s_NetCodeThinClientSystems;
        private static List<Type> s_NetCodeServerSystems;

        private static List<Type> m_ControlSystems;
        private static List<Type> m_ClientSystems;
        private static List<Type> m_ThinClientSystems;
        private static List<Type> m_ServerSystems;

        public List<Type> UserBakingSystems = new List<Type>();

        private static bool IsFromNetCodeAssembly(Type sys)
        {
            return sys.Assembly.FullName.StartsWith("Unity.NetCode,") ||
                sys.Assembly.FullName.StartsWith("Unity.Entities,") ||
                sys.Assembly.FullName.StartsWith("Unity.Transforms,") ||
                sys.Assembly.FullName.StartsWith("Unity.Scenes,") ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.EditorTests,") ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.TestsUtils,") ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.Physics.EditorTests,") ||
                typeof(IGhostComponentSerializerRegistration).IsAssignableFrom(sys);
        }

        public void Bootstrap(bool includeNetCodeSystems, params Type[] userSystems)
        {
            m_ControlSystems = new List<Type>();
            m_ClientSystems = new List<Type>();
            m_ThinClientSystems = new List<Type>();
            m_ServerSystems = new List<Type>();

            m_ControlSystems.Add(typeof(TickClientInitializationSystem));
            m_ControlSystems.Add(typeof(TickClientSimulationSystem));
            m_ControlSystems.Add(typeof(TickClientPresentationSystem));
            m_ControlSystems.Add(typeof(TickServerInitializationSystem));
            m_ControlSystems.Add(typeof(TickServerSimulationSystem));
            m_ControlSystems.Add(typeof(DriverMigrationSystem));

            if (s_NetCodeClientSystems == null)
            {
                s_NetCodeClientSystems = new List<Type>();
                s_AllClientSystems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ClientSimulation);
                foreach (var sys in s_AllClientSystems)
                {
                    if (IsFromNetCodeAssembly(sys))
                        s_NetCodeClientSystems.Add(sys);
                }
            }

            if (s_NetCodeThinClientSystems == null)
            {
                s_NetCodeThinClientSystems = new List<Type>();
                s_AllThinClientSystems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ThinClientSimulation);
                foreach (var sys in s_AllThinClientSystems)
                {
                    if (IsFromNetCodeAssembly(sys))
                        s_NetCodeThinClientSystems.Add(sys);
                }
            }

            if (s_NetCodeServerSystems == null)
            {
                s_NetCodeServerSystems = new List<Type>();
                s_AllServerSystems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ServerSimulation);
                foreach (var sys in s_AllServerSystems)
                {
                    if (IsFromNetCodeAssembly(sys))
                        s_NetCodeServerSystems.Add(sys);
                }
            }

            if (includeNetCodeSystems)
            {
                m_ClientSystems.AddRange(s_NetCodeClientSystems);
                m_ThinClientSystems.AddRange(s_NetCodeThinClientSystems);
                m_ServerSystems.AddRange(s_NetCodeServerSystems);
            }

            if (NetCodeAssemblies.Count > 0)
            {
                foreach (var sys in s_AllClientSystems)
                {
                    bool shouldAdd = false;
                    var sysName = sys.Assembly.FullName;
                    foreach (var asm in NetCodeAssemblies)
                        shouldAdd |= sysName.StartsWith(asm);
                    if (shouldAdd)
                        m_ClientSystems.Add(sys);
                }

                foreach (var sys in s_AllThinClientSystems)
                {
                    bool shouldAdd = false;
                    var sysName = sys.Assembly.FullName;
                    foreach (var asm in NetCodeAssemblies)
                        shouldAdd |= sysName.StartsWith(asm);
                    if (shouldAdd)
                        m_ThinClientSystems.Add(sys);
                }

                foreach (var sys in s_AllServerSystems)
                {
                    bool shouldAdd = false;
                    var sysName = sys.Assembly.FullName;
                    foreach (var asm in NetCodeAssemblies)
                        shouldAdd |= sysName.StartsWith(asm);
                    if (shouldAdd)
                        m_ServerSystems.Add(sys);
                }
            }

            foreach (var sys in userSystems)
            {
                var flags = WorldSystemFilterFlags.Default;
                var attrs = TypeManager.GetSystemAttributes(sys, typeof(WorldSystemFilterAttribute));
                if (attrs != null && attrs.Length == 1)
                    flags = ((WorldSystemFilterAttribute) attrs[0]).FilterFlags;
                var grp = sys;
                while ((flags & WorldSystemFilterFlags.Default) != 0)
                {
                    attrs = TypeManager.GetSystemAttributes(grp, typeof(UpdateInGroupAttribute));
                    if (attrs != null && attrs.Length == 1)
                        grp = ((UpdateInGroupAttribute) attrs[0]).GroupType;
                    else
                    {
                        flags &= ~WorldSystemFilterFlags.Default;
                        flags |= WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation;
                        break;
                    }

                    attrs = TypeManager.GetSystemAttributes(grp, typeof(WorldSystemFilterAttribute));
                    if (attrs != null && attrs.Length == 1)
                    {
                        flags &= ~WorldSystemFilterFlags.Default;
                        flags |= ((WorldSystemFilterAttribute) attrs[0]).ChildDefaultFilterFlags;
                    }
                }

                if ((flags & WorldSystemFilterFlags.ClientSimulation) != 0)
                    m_ClientSystems.Add(sys);
                if ((flags & WorldSystemFilterFlags.ThinClientSimulation) != 0)
                    m_ThinClientSystems.Add(sys);
                if ((flags & WorldSystemFilterFlags.ServerSimulation) != 0)
                    m_ServerSystems.Add(sys);
            }
        }

        public void CreateWorlds(bool server, int numClients, bool tickWorldAfterCreation = true, bool useThinClients = false)
        {
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;
            var oldDebugPort = GhostStatsConnection.Port;
            GhostStatsConnection.Port = 0;
            if (!m_DefaultWorldInitialized)
            {
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(m_DefaultWorld,
                    m_ControlSystems);
                m_DefaultWorldInitialized = true;
            }

            if (server)
            {
                if (m_ServerWorld != null)
                    throw new InvalidOperationException("Server world already created");
                m_ServerWorld = CreateServerWorld("ServerTest");
#if UNITY_EDITOR
                BakeGhostCollection(m_ServerWorld);
#endif
                if (DebugPackets)
                {
                    var dbg = m_ServerWorld.EntityManager.CreateEntity(ComponentType.ReadWrite<NetCodeDebugConfig>());
                    m_ServerWorld.EntityManager.SetComponentData(dbg, new NetCodeDebugConfig {LogLevel = NetDebug.LogLevelType.Debug, DumpPackets = true});
                }
            }

            if (numClients > 0)
            {
                if (m_ClientWorlds != null)
                    throw new InvalidOperationException("Client worlds already created");
                WorldCreationIndex = 0;
                m_ClientWorlds = new World[numClients];
                for (int i = 0; i < numClients; ++i)
                {
                    try
                    {
                        WorldCreationIndex = i;
                        m_ClientWorlds[i] = CreateClientWorld($"ClientTest{i}", useThinClients);

                        if (DebugPackets)
                        {
                            var dbg = m_ClientWorlds[i].EntityManager.CreateEntity(ComponentType.ReadWrite<NetCodeDebugConfig>());
                            m_ClientWorlds[i].EntityManager.SetComponentData(dbg, new NetCodeDebugConfig {LogLevel = NetDebug.LogLevelType.Debug, DumpPackets = true});
                        }
                    }
                    catch (Exception)
                    {
                        m_ClientWorlds = null;
                        throw;
                    }
#if UNITY_EDITOR
                    BakeGhostCollection(m_ClientWorlds[i]);
#endif
                }
            }

            GhostStatsConnection.Port = oldDebugPort;
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            //Run 1 tick so that all the ghost collection and the ghost collection component run once.
            if (tickWorldAfterCreation)
                Tick(1.0f / 60.0f);
        }

        private World CreateServerWorld(string name, World world = null)
        {
            if (world == null)
                world = new World(name, WorldFlags.GameServer);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, m_ServerSystems);
            var initializationGroup = world.GetExistingSystemManaged<InitializationSystemGroup>();
            var simulationGroup = world.GetExistingSystemManaged<SimulationSystemGroup>();
            var presentationGroup = world.GetExistingSystemManaged<PresentationSystemGroup>();
            var initializationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientInitializationSystem>();
            var simulationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientSimulationSystem>();
            var presentationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientPresentationSystem>();
            initializationTickSystem.AddSystemGroupToTickList(initializationGroup);
            simulationTickSystem.AddSystemGroupToTickList(simulationGroup);
            presentationTickSystem.AddSystemGroupToTickList(presentationGroup);
            return world;
        }

        private World CreateClientWorld(string name, bool thinClient, World world = null)
        {
            if (world == null)
                world = new World(name, thinClient ? WorldFlags.GameThinClient : WorldFlags.GameClient);

            // TODO: GameThinClient for ThinClientSystem for ultra thin
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, m_ClientSystems);
            var initializationGroup = world.GetExistingSystemManaged<InitializationSystemGroup>();
            var simulationGroup = world.GetExistingSystemManaged<SimulationSystemGroup>();
            var presentationGroup = world.GetExistingSystemManaged<PresentationSystemGroup>();

            var initializationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientInitializationSystem>();
            var simulationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientSimulationSystem>();
            var presentationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientPresentationSystem>();
            initializationTickSystem.AddSystemGroupToTickList(initializationGroup);
            simulationTickSystem.AddSystemGroupToTickList(simulationGroup);
            presentationTickSystem.AddSystemGroupToTickList(presentationGroup);
            return world;
        }

        public void Tick(float dt)
        {
            // Use fixed timestep in network time system to prevent time dependencies in tests
            NetworkTimeSystem.s_FixedTimestampMS += (uint) (dt * 1000.0f);
            m_ElapsedTime += dt;
            m_DefaultWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ServerWorld != null)
                m_ServerWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ClientWorlds != null)
            {
                for (int i = 0; i < m_ClientWorlds.Length; ++i)
                    m_ClientWorlds[i].SetTime(new TimeData(m_ElapsedTime, dt));
            }

            // Make sure the log flush does not run
            m_DefaultWorld.GetExistingSystemManaged<TickServerInitializationSystem>().Update();
            m_DefaultWorld.GetExistingSystemManaged<TickClientInitializationSystem>().Update();
            m_DefaultWorld.GetExistingSystemManaged<TickServerSimulationSystem>().Update();
            m_DefaultWorld.GetExistingSystemManaged<TickClientSimulationSystem>().Update();
            m_DefaultWorld.GetExistingSystemManaged<TickClientPresentationSystem>().Update();

            // Flush the pending logs since the system doing that might not have run yet which means Log.Expect does not work
            Logging.Internal.LoggerManager.ScheduleUpdateLoggers().Complete();
        }

        public void MigrateServerWorld(World suppliedWorld = null)
        {
            DriverMigrationSystem migrationSystem = default;

            foreach (var world in World.All)
            {
                if ((migrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            var ticket = migrationSystem.StoreWorld(ServerWorld);
            ServerWorld.Dispose();

            Assert.True(suppliedWorld == null || suppliedWorld.IsServer());
            var newWorld = migrationSystem.LoadWorld(ticket, suppliedWorld);
            m_ServerWorld = CreateServerWorld(newWorld.Name, newWorld);

            Assert.True(newWorld.Name == m_ServerWorld.Name);
        }

        public void MigrateClientWorld(int index, World suppliedWorld = null)
        {
            if (index > ClientWorlds.Length)
                throw new IndexOutOfRangeException($"ClientWorlds only contain {ClientWorlds.Length} items, you are trying to read index {index} that is out of range.");

            DriverMigrationSystem migrationSystem = default;

            foreach (var world in World.All)
            {
                if ((migrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            var ticket = migrationSystem.StoreWorld(ClientWorlds[index]);
            ClientWorlds[index].Dispose();

            var newWorld = migrationSystem.LoadWorld(ticket, suppliedWorld);
            m_ClientWorlds[index] = CreateClientWorld(newWorld.Name, false, newWorld);

            Assert.True(newWorld.Name == m_ClientWorlds[index].Name);
        }

        public void RestartClientWorld(int index)
        {
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;

            var name = m_ClientWorlds[index].Name;
            m_ClientWorlds[index].Dispose();

            m_ClientWorlds[index] = CreateClientWorld(name, false);
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;
        }

        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};
            var packetDelay = DriverSimulatedDelay;
            int networkRate = 60;

            // All 3 packet types every frame stored for maximum delay, doubled for safety margin
            int maxPackets = 2 * (networkRate * 3 * (packetDelay + DriverSimulatedJitter) + 999) / 1000;

            var fuzzFactor = 0;
            const int kStringLength = 10; // we name it ClientTest e.g. 10 bytes long.
            var worldId = int.Parse(world.Name.Substring(kStringLength, world.Name.Length - kStringLength));
            if (DriverFuzzFactor?.Length >= worldId + 1)
            {
                fuzzFactor = DriverFuzzFactor[worldId];
            }

            var simParams = new SimulatorUtility.Parameters
            {
                Mode = ApplyMode.AllPackets,
                MaxPacketSize = NetworkParameterConstants.MTU, MaxPacketCount = maxPackets,
                PacketDelayMs = packetDelay,
                PacketJitterMs = DriverSimulatedJitter,
                PacketDropInterval = DriverSimulatedDrop,
                FuzzFactor = fuzzFactor,
                FuzzOffset = DriverFuzzOffset,
                RandomSeed = DriverRandomSeed
            };
            var networkSettings = new NetworkSettings();
            networkSettings.WithNetworkConfigParameters
            (
                maxFrameTimeMS: 100,
                fixedFrameTimeMS: DriverFixedTime
            );
            networkSettings.AddRawParameterStruct(ref reliabilityParams);
            networkSettings.AddRawParameterStruct(ref simParams);

            //We are forcing here the connection type to be a socket but the connection is instead based on IPC.
            //The reason for that is that we want to be able to disable any check/logic that optimise for that use case
            //by default in the test.
            //It is possible however to disable this behavior using the provided opt
            var transportType = UseFakeSocketConnection == 1 ? TransportType.Socket : TransportType.IPC;

            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
            if (UseMultipleDrivers == 0)
                driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
            else
            {
                if ((WorldCreationIndex & 0x1) == 0)
                {
                    driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
                }
                else
                {
                    driverInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
                }

            }

            //Fake the driver as it is always using a socket, even though we are also using IPC as a transport medium

            if (DriverSimulatedDelay + fuzzFactor > 0)
            {
                DefaultDriverBuilder.CreateClientSimulatorPipelines(ref driverInstance);
                driverStore.RegisterDriver(transportType, driverInstance);
            }
            else
            {
                DefaultDriverBuilder.CreateClientPipelines(ref driverInstance);
                driverStore.RegisterDriver(transportType, driverInstance);
            }
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};

            var networkSettings = new NetworkSettings();
            networkSettings.WithNetworkConfigParameters(
                maxFrameTimeMS: 100,
                fixedFrameTimeMS: DriverFixedTime
            );

            networkSettings.AddRawParameterStruct(ref reliabilityParams);

            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
            driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
            DefaultDriverBuilder.CreateServerPipelines(ref driverInstance);
            driverStore.RegisterDriver(TransportType.IPC, driverInstance);
            if (UseMultipleDrivers != 0)
            {
                var socketInstance = new NetworkDriverStore.NetworkDriverInstance();
                socketInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
                DefaultDriverBuilder.CreateServerPipelines(ref socketInstance);
                driverStore.RegisterDriver(TransportType.Socket, socketInstance);
            }
        }

        public bool Connect(float dt, int maxSteps)
        {
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;
            GetSingletonRW<NetworkStreamDriver>(ServerWorld).ValueRW.Listen(ep);
            for (int i = 0; i < ClientWorlds.Length; ++i)
                GetSingletonRW<NetworkStreamDriver>(ClientWorlds[i]).ValueRW.Connect(ClientWorlds[i].EntityManager, ep);
            for (int i = 0; i < ClientWorlds.Length; ++i)
            {
                while (TryGetSingletonEntity<NetworkIdComponent>(ClientWorlds[i]) == Entity.Null)
                {
                    if (maxSteps <= 0)
                        return false;
                    --maxSteps;
                    Tick(dt);
                }
            }

            return true;
        }

        public void GoInGame(World w = null)
        {
            if (w == null)
            {
                if (ServerWorld != null)
                    GoInGame(ServerWorld);
                if (ClientWorlds != null)
                {
                    for (int i = 0; i < ClientWorlds.Length; ++i)
                        GoInGame(ClientWorlds[i]);
                }

                return;
            }

            var type = ComponentType.ReadOnly<NetworkIdComponent>();
            var query = w.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.TempJob);
            for (int i = 0; i < connections.Length; ++i)
                w.EntityManager.AddComponentData(connections[i], new NetworkStreamInGame());
            connections.Dispose();
        }

        public void ExitFromGame()
        {
            void RemoveTag(World world)
            {
                var type = ComponentType.ReadOnly<NetworkIdComponent>();
                var query = world.EntityManager.CreateEntityQuery(type);
                var connections = query.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < connections.Length; ++i)
                {
                    world.EntityManager.RemoveComponent<NetworkStreamInGame>(connections[i]);
                }

                connections.Dispose();
            }

            RemoveTag(ServerWorld);
            for (int i = 0; i < ClientWorlds.Length; ++i)
            {
                RemoveTag(ClientWorlds[i]);
            }
        }

        public void SetInGame(int client)
        {
            var type = ComponentType.ReadOnly<NetworkIdComponent>();
            var clientQuery = ClientWorlds[client].EntityManager.CreateEntityQuery(type);
            var clientEntity = clientQuery.ToEntityArray(Allocator.Temp);
            ClientWorlds[client].EntityManager.AddComponent<NetworkStreamInGame>(clientEntity[0]);
            var clientNetId = ClientWorlds[client].EntityManager.GetComponentData<NetworkIdComponent>(clientEntity[0]);
            clientEntity.Dispose();

            var query = ServerWorld.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
            {
                var netId = ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(connections[i]);
                if (netId.Value == clientNetId.Value)
                {
                    ServerWorld.EntityManager.AddComponent<NetworkStreamInGame>(connections[i]);
                    break;
                }
            }

            connections.Dispose();
        }

        public void RemoveFromGame(int client)
        {
            var type = ComponentType.ReadOnly<NetworkIdComponent>();
            var clientQuery = ClientWorlds[client].EntityManager.CreateEntityQuery(type);
            var clientEntity = clientQuery.ToEntityArray(Allocator.Temp);
            ClientWorlds[client].EntityManager.RemoveComponent<NetworkStreamInGame>(clientEntity[0]);
            var clientNetId = ClientWorlds[client].EntityManager.GetComponentData<NetworkIdComponent>(clientEntity[0]);
            clientEntity.Dispose();

            var query = ServerWorld.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
            {
                var netId = ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(connections[i]);
                if (netId.Value == clientNetId.Value)
                {
                    ServerWorld.EntityManager.RemoveComponent<NetworkStreamInGame>(connections[i]);
                    break;
                }
            }

            connections.Dispose();
        }

        public Entity TryGetSingletonEntity<T>(World w)
        {
            var type = ComponentType.ReadOnly<T>();
            using (var query = w.EntityManager.CreateEntityQuery(type))
            {
                int entCount = query.CalculateEntityCount();
#if UNITY_EDITOR
                if (entCount >= 2)
                    Debug.LogError("Trying to get singleton, but there are multiple matching entities");
#endif
                if (entCount != 1)
                    return Entity.Null;
                return query.GetSingletonEntity();
            }
        }

        public T GetSingleton<T>(World w) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadOnly<T>();
            using (var query = w.EntityManager.CreateEntityQuery(type))
            {
                return query.GetSingleton<T>();
            }
        }

        public RefRW<T> GetSingletonRW<T>(World w) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            using (var query = w.EntityManager.CreateEntityQuery(type))
            {
                return query.GetSingletonRW<T>();
            }
        }

        public DynamicBuffer<T> GetSingletonBuffer<T>(World w) where T : unmanaged, IBufferElementData
        {
            var type = ComponentType.ReadOnly<T>();
            using (var query = w.EntityManager.CreateEntityQuery(type))
            {
                return query.GetSingletonBuffer<T>();
            }
        }

#if UNITY_EDITOR
        public bool CreateGhostCollection(params GameObject[] ghostTypes)
        {
            if (m_GhostCollection != null)
                return false;
            m_GhostCollection = new List<GameObject>(ghostTypes.Length);

            foreach (var ghostObject in ghostTypes)
            {
                var ghost = ghostObject.GetComponent<GhostAuthoringComponent>();
                if (ghost == null)
                    {ghost = ghostObject.AddComponent<GhostAuthoringComponent>();}
                ghost.prefabId = Guid.NewGuid().ToString().Replace("-", "");
                m_GhostCollection.Add(ghostObject);
            }
            m_BlobAssetStore = new BlobAssetStore(128);
            return true;
        }

        public Entity SpawnOnServer(int prefabIndex)
        {
            if (m_GhostCollection == null)
                throw new InvalidOperationException("Cannot spawn ghost on server without setting up the ghost first");
            var prefabCollection = TryGetSingletonEntity<NetCodeTestPrefabCollection>(ServerWorld);
            if (prefabCollection == Entity.Null)
                throw new InvalidOperationException("Cannot spawn ghost on server if a ghost prefab collection is not created");
            var prefabBuffers = ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection);
            return ServerWorld.EntityManager.Instantiate(prefabBuffers[prefabIndex].Value);
        }

        private Entity BakeGameObject(GameObject go, World world, BlobAssetStore blobAssetStore)
        {
            // We need to use an intermediate world as BakingUtility.BakeGameObjects cleans up previously baked
            // entities. This means that we need to move the entities from the intermediate world into the final
            // world. As BakeGameObject returns the main baked entity, we use the EntityGUID to find that
            // entity in the final world
            using var intermediateWorld = new World("NetCodeBakingWorld");

            var bakingSettings = new BakingSettings(BakingUtility.BakingFlags.AddEntityGUID, blobAssetStore);
            bakingSettings.PrefabRoot = go;
            bakingSettings.ExtraSystems.AddRange(UserBakingSystems);
            BakingUtility.BakeGameObjects(intermediateWorld, new GameObject[] {}, bakingSettings);

            var bakingSystem = intermediateWorld.GetExistingSystemManaged<BakingSystem>();
            var intermediateEntity = bakingSystem.GetEntity(go);
            var intermediateEntityGuid = intermediateWorld.EntityManager.GetComponentData<EntityGuid>(intermediateEntity);
            // Copy all the tracked/baked entities. That TransformAuthoring is present on all entities added by the baker for the
            // converted gameobject. It is sufficient condition to copy all the additional entities as well.
#if !ENABLE_TRANSFORM_V1
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Prefab, EntityGuid, LocalTransform>();
#else
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<Prefab, EntityGuid, Translation>();
#endif

            using var bakedEntities = intermediateWorld.EntityManager.CreateEntityQuery(builder);
            world.EntityManager.MoveEntitiesFrom(intermediateWorld.EntityManager, bakedEntities);

            // Search for the entity in the final world by comparing the EntityGuid from entity in the intermediate world
            using var query = world.EntityManager.CreateEntityQuery(typeof(EntityGuid), typeof(Prefab));
            using var entityArray = query.ToEntityArray(Allocator.TempJob);
            using var entityGUIDs = query.ToComponentDataArray<EntityGuid>(Allocator.TempJob);
            for (int index = 0; index < entityGUIDs.Length; ++index)
            {
                if (entityGUIDs[index] == intermediateEntityGuid)
                {
                    return entityArray[index];
                }
            }

            Debug.LogError($"Copied Entity {intermediateEntityGuid} not found");
            return Entity.Null;
        }

        public Entity SpawnOnServer(GameObject go)
        {
            if (m_GhostCollection == null)
                throw new InvalidOperationException("Cannot spawn ghost on server without setting up the ghost first");
            int index = m_GhostCollection.IndexOf(go);
            if (index >= 0)
                return SpawnOnServer(index);

            return BakeGameObject(go, ServerWorld, m_BlobAssetStore);
        }

        public Entity BakeGhostCollection(World world)
        {
            if (m_GhostCollection == null)
                return Entity.Null;
            NativeList<Entity> prefabs = new NativeList<Entity>(m_GhostCollection.Count, Allocator.Temp);
            foreach (var prefab in m_GhostCollection)
            {
                var ghostAuth = prefab.GetComponent<GhostAuthoringComponent>();
                ghostAuth.ForcePrefabConversion = true;
                var prefabEnt = BakeGameObject(prefab, world, m_BlobAssetStore);
                ghostAuth.ForcePrefabConversion = false;
                world.EntityManager.AddComponentData(prefabEnt, default(Prefab));
                prefabs.Add(prefabEnt);
            }

            var collection = world.EntityManager.CreateEntity();
            world.EntityManager.AddComponentData(collection, default(NetCodeTestPrefabCollection));
            var prefabBuffer = world.EntityManager.AddBuffer<NetCodeTestPrefab>(collection);
            foreach (var prefab in prefabs)
                prefabBuffer.Add(new NetCodeTestPrefab {Value = prefab});
            return collection;
        }
#endif
    }
}
