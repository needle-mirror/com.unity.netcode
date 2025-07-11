#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using Unity.NetCode.Editor;
#endif
using UnityEngine;
#if USING_UNITY_LOGGING
using Unity.Logging;
using Unity.Logging.Sinks;
#endif

namespace Unity.NetCode.Tests
{
    internal struct NetCodeTestPrefabCollection : IComponentData
    {}
    internal struct NetCodeTestPrefab : IBufferElementData
    {
        public Entity Value;
    }

    internal class NetCodeTestWorld : IDisposable, INetworkStreamDriverConstructor
    {
        /// <summary>
        /// True if you want to forward all netcode logs from the server, to allow <see cref="LogAssert"/> usage.
        /// <b>WARNING: DISABLE "Force Log Settings" TOOL OR THIS TEST WILL FAIL!</b>
        /// </summary>
        /// <remarks>Defaults to true. <see cref="DebugPackets"/> and <see cref="LogLevel"/>.</remarks>
        public bool EnableLogsOnServer = true;
        /// <summary>
        /// True if you want to forward all netcode logs from the client, to allow <see cref="LogAssert"/> usage.
        /// <b>WARNING: DISABLE "Force Log Settings" TOOL OR THIS TEST WILL FAIL!</b>
        /// </summary>
        /// <remarks>Defaults to true. <see cref="DebugPackets"/> and <see cref="LogLevel"/>.</remarks>
        public bool EnableLogsOnClients = true;

        /// <summary>Enable packet dumping in tests? Useful to ensure serialization doesn't fail.</summary>
        /// <remarks>Note: Packet dump files will not be cleaned up!</remarks>
        public bool DebugPackets = false;
        /// <summary>If you want to test extremely verbose logs, you can modify this flag.</summary>
        public NetDebug.LogLevelType LogLevel = NetDebug.LogLevelType.Notify;

        static readonly ProfilerMarker k_TickServerInitializationSystem = new ProfilerMarker("TickServerInitializationSystem");
        static readonly ProfilerMarker k_TickClientInitializationSystem = new ProfilerMarker("TickClientInitializationSystem");
        static readonly ProfilerMarker k_TickServerSimulationSystem = new ProfilerMarker("TickServerSimulationSystem");
        static readonly ProfilerMarker k_TickClientSimulationSystem = new ProfilerMarker("TickClientSimulationSystem");
        static readonly ProfilerMarker k_TickClientPresentationSystem = new ProfilerMarker("TickClientPresentationSystem");

        public World DefaultWorld => m_DefaultWorld;
        public World ServerWorld
        {
            get { return m_ServerWorld; }
            set { m_ServerWorld = value; }
        }
        public World[] ClientWorlds => m_ClientWorlds;
        /// <summary>
        /// Logs how many times we've called <see cref="Tick"/>, zero-indexed
        /// (i.e. -1 before Tick is called, 0 on the first frame).
        /// </summary>
        public static int TickIndex { get; private set; }

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
        public ApplyMode DriverSimulatorPacketMode = ApplyMode.AllPackets;
        public int DriverMaxMessageSize = NetworkParameterConstants.MaxMessageSize;
        public int DriverReliablePipelineWindowSize = 32;
        public int UseMultipleDrivers = 0;
        public int DriverFragmentedPayloadCapacity = 16 * 1024;
        public int UseFakeSocketConnection = 1;
        private int WorldCreationIndex = 0;
        public NetCodeConfig m_OldGlobalConfig;
        NetCodeConfig m_GlobalConfigForTests;

        public int[] DriverFuzzFactor;
        public int DriverFuzzOffset = 0;
        public uint DriverRandomSeed = 0;

        private bool m_IsFirstTimeTicking = true;

#if UNITY_EDITOR
        private List<GameObject> m_GhostCollection;
        private BlobAssetStore m_BlobAssetStore;
#endif

        /// <summary>Configure how logging should occur in tests. We apply <see cref="LogLevel"/> and <see cref="DebugPackets"/> here.</summary>
        /// <param name="world">World to apply this config on.</param>
        private void SetupNetDebugConfig(World world)
        {
            var shouldLog = (world.IsServer() && EnableLogsOnServer) || (world.IsClient() && EnableLogsOnClients);
            world.EntityManager.CreateSingleton(new NetCodeDebugConfig
            {
                // Hack essentially disabling all logging for this world, as we should never have exceptions going via this logger anyway.
                LogLevel = shouldLog ? LogLevel : NetDebug.LogLevelType.Exception,
                DumpPackets = DebugPackets,
            });
        }

        public NetCodeTestWorld(bool useGlobalConfig=false, double initialElapsedTime = 42)
        {
#if UNITY_EDITOR

    #if UNITY_SERVER
            Debug.Log("WARNING: Your editor target is Server, some netcode tests (especially those involving connection) may fail!");
    #endif
            // Not having a default world means RegisterUnloadOrPlayModeChangeShutdown has not been called which causes memory leaks
            DefaultWorldInitialization.DefaultLazyEditModeInitialize();
#endif
            m_OldGlobalConfig = NetCodeConfig.Global;
            m_GlobalConfigForTests = useGlobalConfig ? ScriptableObject.CreateInstance<NetCodeConfig>() : null;
            NetCodeConfig.Global = m_GlobalConfigForTests;
            m_OldBootstrapAutoConnectPort = ClientServerBootstrap.AutoConnectPort;
            ClientServerBootstrap.AutoConnectPort = 0;
            m_DefaultWorld = new World("NetCodeTest");
            m_ElapsedTime = initialElapsedTime;
#if !UNITY_SERVER || UNITY_EDITOR
            m_ElapsedTimeClient = initialElapsedTime;
            TickIndex = -1;
            ClientTickIndex = -1;
#endif
            NetworkTimeSystem.ResetFixedTime();
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
            if(m_GlobalConfigForTests)
                UnityEngine.Object.DestroyImmediate(m_GlobalConfigForTests);
            NetCodeConfig.Global = m_OldGlobalConfig;
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

        private static List<Type> m_ControlSystems;
        private static List<Type> m_ClientSystems;
        private static List<Type> m_ThinClientSystems;
        private static List<Type> m_ServerSystems;
        private static List<Type> m_BakingSystems;

        public List<string> TestSpecificAdditionalAssemblies = new List<string>(8);

        int m_NumClients = -1;

        private static bool IsFromNetCodeAssembly(Type sys)
        {
            return sys.Assembly.FullName.StartsWith("Unity.NetCode,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Entities,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Transforms,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Scenes,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.EditorTests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.Physics.EditorTests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.TestsUtils,", StringComparison.Ordinal) ||
                typeof(IGhostComponentSerializerRegistration).IsAssignableFrom(sys);
        }

        private bool IsFromTestSpecificAdditionalAssembly(Type sys)
        {
            var sysAssemblyFullName = sys.Assembly.FullName;
            foreach (var extraNetcodeAssembly in TestSpecificAdditionalAssemblies)
            {
                if (sysAssemblyFullName.StartsWith(extraNetcodeAssembly, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public void Bootstrap(bool includeNetCodeSystems, params Type[] userSystems)
        {
            m_ControlSystems = new List<Type>(256);
            m_ClientSystems = new List<Type>(256);
            m_ThinClientSystems = new List<Type>(256);
            m_ServerSystems = new List<Type>(256);
            m_BakingSystems = new List<Type>(256);
#if !UNITY_SERVER || UNITY_EDITOR
            m_ControlSystems.Add(typeof(TickClientInitializationSystem));
            m_ControlSystems.Add(typeof(TickClientSimulationSystem));
            m_ControlSystems.Add(typeof(TickClientPresentationSystem));
#endif
#if !UNITY_CLIENT || UNITY_EDITOR
            m_ControlSystems.Add(typeof(TickServerInitializationSystem));
            m_ControlSystems.Add(typeof(TickServerSimulationSystem));
#endif
            m_ControlSystems.Add(typeof(DriverMigrationSystem));

            s_AllClientSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ClientSimulation);
            s_AllThinClientSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ThinClientSimulation);
            s_AllServerSystems ??= DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.ServerSimulation);

            bool IncludeNetcodeSystemsFilter(Type x) => IsFromNetCodeAssembly(x) || IsFromTestSpecificAdditionalAssembly(x);

            Func<Type, bool> filter = includeNetCodeSystems
                ? IncludeNetcodeSystemsFilter
                : IsFromTestSpecificAdditionalAssembly;

            m_ClientSystems.AddRange(s_AllClientSystems.Where(filter));
            m_ThinClientSystems.AddRange(s_AllThinClientSystems.Where(filter));
            m_ServerSystems.AddRange(s_AllServerSystems.Where(filter));

            m_ClientSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));
            m_ThinClientSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));
            m_ServerSystems.Add(typeof(Unity.Entities.UpdateWorldTimeSystem));

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
                if ((flags & WorldSystemFilterFlags.BakingSystem) != 0)
                    m_BakingSystems.Add(sys);
            }
        }

        public void CreateWorlds(bool server, int numClients, bool tickWorldAfterCreation = true, bool useThinClients = false)
        {
            m_NumClients = numClients;
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;
#if UNITY_EDITOR || NETCODE_DEBUG
            var oldDebugPort = GhostStatsConnection.Port;
            GhostStatsConnection.Port = 0;
#endif
            if (!m_DefaultWorldInitialized)
            {
                TypeManager.SortSystemTypesInCreationOrder(m_ControlSystems); // Ensure CreationOrder is respected.
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(m_DefaultWorld,
                    m_ControlSystems);
                m_DefaultWorldInitialized = true;
            }

            var testMethodName = NUnit.Framework.TestContext.CurrentContext.Test.MethodName;
            if (server)
            {
                if (m_ServerWorld != null)
                    throw new InvalidOperationException("Server world already created");
                m_ServerWorld = CreateServerWorld($"ServerTest-{testMethodName}");
#if UNITY_EDITOR
                BakeGhostCollection(m_ServerWorld);
#endif

                SetupNetDebugConfig(m_ServerWorld);
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

                        m_ClientWorlds[i] = CreateClientWorld($"ClientTest{i}-{testMethodName}", useThinClients);

                        SetupNetDebugConfig(m_ClientWorlds[i]);
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

#if UNITY_EDITOR || NETCODE_DEBUG
            GhostStatsConnection.Port = oldDebugPort;
#endif
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            //Run 1 tick so that all the ghost collection and the ghost collection component run once.
            if (tickWorldAfterCreation)
                Tick();

            TrySuppressNetDebug(true, true);
        }

        /// <summary>
        /// Suppress netcode warnings via the NetDebug.
        /// </summary>
        /// <param name="suppressRunInBackground">
        /// Tests will fail on CI due to `runInBackground = false`, so we must suppress the warning:
        /// Note that if netcode systems don't exist (i.e. no NetDebug), no suppression is necessary.
        /// </param>
        /// <param name="suppressApprovalRpc">
        /// This log can get very spammy in RPC tests, and it can bring down the logger (lol), so suppressed by default.
        /// </param>
        /// <remarks>Called multiple times as some tests don't tick until they've established a collection.</remarks>
        public bool TrySuppressNetDebug(bool suppressRunInBackground, bool suppressApprovalRpc)
        {
            var success = TryGetSingletonEntity<NetDebug>(ServerWorld) != default;
            if (success)
            {
                ref var netDebug = ref GetSingletonRW<NetDebug>(ServerWorld).ValueRW;
                netDebug.SuppressApplicationRunInBackgroundWarning = suppressRunInBackground;
                netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning = suppressApprovalRpc;
            }

            if (ClientWorlds != null)
            {
                foreach (var clientWorld in ClientWorlds)
                {
                    success &= TryGetSingletonEntity<NetDebug>(clientWorld) != default;
                    if (success)
                    {
                        ref var netDebug = ref GetSingletonRW<NetDebug>(clientWorld).ValueRW;
                        netDebug.SuppressApplicationRunInBackgroundWarning = suppressRunInBackground;
                        netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning = suppressApprovalRpc;
                    }
                }
            }
            return success;
        }

        public World CreateServerWorld(string name, World world = null)
        {
            if (world == null)
                world = new World(name, WorldFlags.GameServer);

            TypeManager.SortSystemTypesInCreationOrder(m_ServerSystems); // Ensure CreationOrder is respected.
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, m_ServerSystems);
            var initializationGroup = world.GetExistingSystemManaged<InitializationSystemGroup>();
            var simulationGroup = world.GetExistingSystemManaged<SimulationSystemGroup>();
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
#if !UNITY_CLIENT || UNITY_EDITOR
            var initializationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickServerInitializationSystem>();
            var simulationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickServerSimulationSystem>();
            initializationTickSystem.AddSystemGroupToTickList(initializationGroup);
            simulationTickSystem.AddSystemGroupToTickList(simulationGroup);
#endif
            ClientServerBootstrap.ServerWorlds.Add(world);
            return world;
        }

        private World CreateClientWorld(string name, bool thinClient, World world = null)
        {
            if (world == null)
                world = new World(name, thinClient ? WorldFlags.GameThinClient : WorldFlags.GameClient);

            // TODO: GameThinClient for ThinClientSystem for ultra thin
            TypeManager.SortSystemTypesInCreationOrder(m_ClientSystems); // Ensure CreationOrder is respected.
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, m_ClientSystems);
            var initializationGroup = world.GetExistingSystemManaged<InitializationSystemGroup>();
            var simulationGroup = world.GetExistingSystemManaged<SimulationSystemGroup>();
            var presentationGroup = world.GetExistingSystemManaged<PresentationSystemGroup>();
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
#if !UNITY_SERVER || UNITY_EDITOR
            var initializationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientInitializationSystem>();
            var simulationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientSimulationSystem>();
            var presentationTickSystem = m_DefaultWorld.GetExistingSystemManaged<TickClientPresentationSystem>();
            initializationTickSystem.AddSystemGroupToTickList(initializationGroup);
            simulationTickSystem.AddSystemGroupToTickList(simulationGroup);
            presentationTickSystem.AddSystemGroupToTickList(presentationGroup);
#endif
            ClientServerBootstrap.ClientWorlds.Add(world);
            return world;
        }

        const float k_defaultDT = 1f / 60f;
        public void TickMultiple( int numTicks, float dt = k_defaultDT)
        {
            for ( int t=0; t<numTicks; ++t )
            {
                Tick(dt);
            }
        }

        public void Tick(float dt = k_defaultDT)
        {
#if !UNITY_CLIENT || UNITY_EDITOR
            TickServerOnly(dt);
#endif
#if !UNITY_SERVER || UNITY_EDITOR
            TickClientOnly(dt);
#endif

#if USING_UNITY_LOGGING
            // Flush the pending logs since the system doing that might not have run yet which means Log.Expect does not work
            Logging.Internal.LoggerManager.ScheduleUpdateLoggers().Complete();
#endif
        }

        public void TickServerOnly(float dt = 1 / 60f)
        {
            ++TickIndex;
            //Debug.Log($"[{TickIndex}]: TICK");
            if (m_IsFirstTimeTicking)
            {
                // to emulate time system's logic
                m_IsFirstTimeTicking = false;
                m_ElapsedTime = -dt;
            }

            // Use fixed timestep in network time system to prevent time dependencies in tests
            m_ElapsedTime += dt;
            m_DefaultWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ServerWorld != null)
            {
                m_ServerWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            }

            TickServerWorld();
        }

#if !UNITY_SERVER || UNITY_EDITOR
        private bool m_IsFirstTimeTickingClient = true;
        private double m_ElapsedTimeClient = 0;
        public static int ClientTickIndex { get; private set; }

        // This is close to the same as the Tick method, but only ticks the client world
        // This is useful if a test needs to do partial ticks without ticking the server, or to get very specific timings between server and client
        public void TickClientOnly(float dt = 1 / 60f)
        {
            ++ClientTickIndex;
            if (m_IsFirstTimeTickingClient)
            {
                m_IsFirstTimeTickingClient = false;
                m_ElapsedTimeClient = -dt;
            }

            NetworkTimeSystem.s_FixedTimestampMS += (uint) (dt * 1000.0f);
            m_ElapsedTimeClient += dt;

            if (m_ClientWorlds != null)
            {
                foreach (var clientWorld in m_ClientWorlds)
                {
                    clientWorld.SetTime(new TimeData(m_ElapsedTimeClient, dt));
                }
            }

            TickClientWorld();
        }

        public void TickClientWorld()
        {
            k_TickClientInitializationSystem.Begin();
            m_DefaultWorld.GetExistingSystemManaged<TickClientInitializationSystem>().Update();
            k_TickClientInitializationSystem.End();
            k_TickClientSimulationSystem.Begin();
            m_DefaultWorld.GetExistingSystemManaged<TickClientSimulationSystem>().Update();
            k_TickClientSimulationSystem.End();
            k_TickClientPresentationSystem.Begin();
            m_DefaultWorld.GetExistingSystemManaged<TickClientPresentationSystem>().Update();
            k_TickClientPresentationSystem.End();
        }
#endif

#if !UNITY_CLIENT || UNITY_EDITOR
        public void TickServerWorld()
        {
            k_TickServerInitializationSystem.Begin();
            m_DefaultWorld.GetExistingSystemManaged<TickServerInitializationSystem>().Update();
            k_TickServerInitializationSystem.End();
            k_TickServerSimulationSystem.Begin();
            m_DefaultWorld.GetExistingSystemManaged<TickServerSimulationSystem>().Update();
            k_TickServerSimulationSystem.End();
        }
#endif

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

            TrySuppressNetDebug(true, true);
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

            TrySuppressNetDebug(true, true);
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
            var packetDelay = DriverSimulatedDelay;
            int networkRate = 60;

            // All 3 packet types every frame stored for maximum delay, doubled for safety margin
            int maxPackets = 2 * (networkRate * 3 * (packetDelay + DriverSimulatedJitter) + 999) / 1000;

            var fuzzFactor = 0;
            // We name it "ClientTestXX-NameOfTest", so extract the XX.
            var worldId = CalculateWorldId(world);
            if (DriverFuzzFactor?.Length >= worldId + 1)
            {
                fuzzFactor = DriverFuzzFactor[worldId];
            }

            var simParams = new SimulatorUtility.Parameters
            {
                Mode = DriverSimulatorPacketMode,
                MaxPacketSize = NetworkParameterConstants.MTU, MaxPacketCount = maxPackets,
                PacketDelayMs = packetDelay,
                PacketJitterMs = DriverSimulatedJitter,
                PacketDropInterval = DriverSimulatedDrop,
                FuzzFactor = fuzzFactor,
                FuzzOffset = DriverFuzzOffset,
                RandomSeed = DriverRandomSeed
            };
            var networkSettings = new NetworkSettings();
            networkSettings
                .WithReliableStageParameters(windowSize:DriverReliablePipelineWindowSize)
                .WithFragmentationStageParameters(payloadCapacity:DriverFragmentedPayloadCapacity)
                .WithNetworkConfigParameters
            (
                maxFrameTimeMS: 100,
                fixedFrameTimeMS: DriverFixedTime,
                maxMessageSize: DriverMaxMessageSize
            );
            networkSettings.AddRawParameterStruct(ref simParams);

            //We are forcing here the connection type to be a socket but thxe connection is instead based on IPC.
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

        public static int CalculateWorldId(World world)
        {
            var regex = new Regex(@"(ClientTest)(\d)", RegexOptions.Singleline);
            var match = regex.Match(world.Name);
            return int.Parse(match.Groups[2].Value);
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var networkSettings = new NetworkSettings();
            networkSettings
                .WithReliableStageParameters(windowSize: DriverReliablePipelineWindowSize)
                .WithFragmentationStageParameters(payloadCapacity:DriverFragmentedPayloadCapacity)
                .WithNetworkConfigParameters(
                maxFrameTimeMS: 100,
                fixedFrameTimeMS: DriverFixedTime,
                maxMessageSize: DriverMaxMessageSize
            );
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

        /// <summary>
        /// Will throw if connect fails.
        /// </summary>
        public void Connect(float dt = k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true)
        {
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;
            Debug.Assert(GetSingletonRW<NetworkStreamDriver>(ServerWorld).ValueRW.Listen(ep), $"[{ServerWorld.Name}] Listen failed during Connect!");
            var connectionEntities = new Entity[ClientWorlds.Length];
            for (int i = 0; i < ClientWorlds.Length; ++i)
                connectionEntities[i] = GetSingletonRW<NetworkStreamDriver>(ClientWorlds[i]).ValueRW.Connect(ClientWorlds[i].EntityManager, ep);
            int stepsLeft = maxSteps;
            for (int i = 0; i < ClientWorlds.Length; ++i)
            {
                while (TryGetSingletonEntity<NetworkId>(ClientWorlds[i]) == Entity.Null)
                {

                    if (stepsLeft <= 0)
                    {
                        var streamDriver = GetSingleton<NetworkStreamDriver>(ClientWorlds[i]);
                        if (failTestIfConnectionFails)
                        {
                            string connectionState = "No_NetworkConnection_Entity_Left";
                            if (ClientWorlds[i].EntityManager.Exists(connectionEntities[i]))
                            {
                                var nsc = ClientWorlds[i].EntityManager.GetComponentData<NetworkStreamConnection>(connectionEntities[i]);
                                connectionState = $"{connectionEntities[i].ToFixedString()} NetworkStreamConnection[{nsc.Value.ToFixedString()}-{nsc.CurrentState.ToString()}]";
                            }
                            Assert.Fail($"ClientWorld[{i}] failed to connect to the server after {maxSteps} ticks! Driver status: {connectionState}!");
                        }
                        return;
                    }
                    --stepsLeft;
                    Tick(dt);
                }
            }
        }
        public void StartSeverListen()
        {
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;
            Debug.Assert(GetSingletonRW<NetworkStreamDriver>(ServerWorld).ValueRW.Listen(ep), $"[{ServerWorld.Name}] Listen failed during Connect!");
        }

        public void ConnectSingleClientWorld(int clientIndex, float dt = NetCodeTestWorld.k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true)
        {
            Assert.True( clientIndex < ClientWorlds.Length );
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;

            var connectionEntity = GetSingletonRW<NetworkStreamDriver>(ClientWorlds[clientIndex]).ValueRW.Connect(ClientWorlds[clientIndex].EntityManager, ep);

            int stepsLeft = maxSteps;

            while (TryGetSingletonEntity<NetworkId>(ClientWorlds[clientIndex]) == Entity.Null)
            {
                if (stepsLeft <= 0)
                {
                    var streamDriver = GetSingleton<NetworkStreamDriver>(ClientWorlds[clientIndex]);
                    if (failTestIfConnectionFails)
                    {
                        string connectionState = "No_NetworkConnection_Entity_Left";
                        if (ClientWorlds[clientIndex].EntityManager.Exists(connectionEntity))
                        {
                            var nsc = ClientWorlds[clientIndex].EntityManager.GetComponentData<NetworkStreamConnection>(connectionEntity);
                            connectionState = $"{connectionEntity.ToFixedString()} NetworkStreamConnection[{nsc.Value.ToFixedString()}-{nsc.CurrentState.ToString()}]";
                        }
                        Assert.Fail($"ClientWorld {clientIndex}{ClientWorlds[clientIndex].Name} failed to connect to the server after {maxSteps} ticks! Driver status: {connectionState}!");
                    }
                    return;
                }
                --stepsLeft;
                Tick(dt);
            }
        }


        public void GoInGame(World w = null)
        {
            if (w == null)
            {
                if (ServerWorld != null)
                {
                    GoInGame(ServerWorld);
                }
                if (ClientWorlds == null) return;
                foreach (var clientWorld in ClientWorlds)
                {
                    GoInGame(clientWorld);
                }

                return;
            }

            var type = ComponentType.ReadOnly<NetworkId>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            foreach (var connection in connections)
            {
                w.EntityManager.AddComponentData(connection, new NetworkStreamInGame());
            }

            connections.Dispose();
        }

        public void ExitFromGame()
        {
            void RemoveTag(World world)
            {
                var type = ComponentType.ReadOnly<NetworkId>();
                using var query = world.EntityManager.CreateEntityQuery(type);
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
            var type = ComponentType.ReadOnly<NetworkId>();
            using var clientQuery = ClientWorlds[client].EntityManager.CreateEntityQuery(type);
            var clientEntity = clientQuery.ToEntityArray(Allocator.Temp);
            ClientWorlds[client].EntityManager.AddComponent<NetworkStreamInGame>(clientEntity[0]);
            var clientNetId = ClientWorlds[client].EntityManager.GetComponentData<NetworkId>(clientEntity[0]);
            clientEntity.Dispose();

            using var query = ServerWorld.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
            {
                var netId = ServerWorld.EntityManager.GetComponentData<NetworkId>(connections[i]);
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
            var type = ComponentType.ReadOnly<NetworkId>();
            using var clientQuery = ClientWorlds[client].EntityManager.CreateEntityQuery(type);
            var clientEntity = clientQuery.ToEntityArray(Allocator.Temp);
            ClientWorlds[client].EntityManager.RemoveComponent<NetworkStreamInGame>(clientEntity[0]);
            var clientNetId = ClientWorlds[client].EntityManager.GetComponentData<NetworkId>(clientEntity[0]);
            clientEntity.Dispose();

            using var query = ServerWorld.EntityManager.CreateEntityQuery(type);
            var connections = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; ++i)
            {
                var netId = ServerWorld.EntityManager.GetComponentData<NetworkId>(connections[i]);
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
            using var query = w.EntityManager.CreateEntityQuery(type);
            int entCount = query.CalculateEntityCount();
#if UNITY_EDITOR
            if (entCount >= 2)
                Debug.LogError("Trying to get singleton, but there are multiple matching entities");
#endif
            if (entCount != 1)
                return Entity.Null;
            return query.GetSingletonEntity();
        }

        public T GetSingleton<T>(World w) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadOnly<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            return query.GetSingleton<T>();
        }

        public RefRW<T> GetSingletonRW<T>(World w) where T : unmanaged, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            return query.GetSingletonRW<T>();
        }

        public DynamicBuffer<T> GetSingletonBuffer<T>(World w) where T : unmanaged, IBufferElementData
        {
            var type = ComponentType.ReadOnly<T>();
            using var query = w.EntityManager.CreateEntityQuery(type);
            return query.GetSingletonBuffer<T>();
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
                {
                    ghost = ghostObject.AddComponent<GhostAuthoringComponent>();
                }
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
            bakingSettings.ExtraSystems.AddRange(m_BakingSystems);
            BakingUtility.BakeGameObjects(intermediateWorld, new GameObject[] {}, bakingSettings);

            var bakingSystem = intermediateWorld.GetExistingSystemManaged<BakingSystem>();
            var intermediateEntity = bakingSystem.GetEntity(go);
            var intermediateEntityGuid = intermediateWorld.EntityManager.GetComponentData<EntityGuid>(intermediateEntity);

            // Copy all the tracked/baked entities. That TransformAuthoring is present on all entities added by the baker for the
            // converted gameobject. It is sufficient condition to copy all the additional entities as well.
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<EntityGuid>().WithOptions(EntityQueryOptions.IncludePrefab);

            using var bakedEntities = intermediateWorld.EntityManager.CreateEntityQuery(builder);
            world.EntityManager.MoveEntitiesFrom(intermediateWorld.EntityManager, bakedEntities);

            // Search for the entity in the final world by comparing the EntityGuid from entity in the intermediate world
            using var query = builder.Build(world.EntityManager);
            var entityArray = query.ToEntityArray(Allocator.Temp);
            var entityGUIDs = query.ToComponentDataArray<EntityGuid>(Allocator.Temp);
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
        public void SetDynamicAssemblyList(bool useDynamicAssemblyList)
        {
            GetSingletonRW<RpcCollection>(ServerWorld).ValueRW.DynamicAssemblyList = useDynamicAssemblyList;
            foreach (var clientWorld in ClientWorlds)
                GetSingletonRW<RpcCollection>(clientWorld).ValueRW.DynamicAssemblyList = useDynamicAssemblyList;
        }

        public void TickUntilClientsHaveAllGhosts(int maxTicks = 64)
        {
            World clientWorld = default;
            GhostCount ghostCount = default;
            Assert.IsTrue(ClientWorlds.Length > 0, "Sanity");
            for (int tickIdx = 0; tickIdx < maxTicks; ++tickIdx)
            {
                Tick();
                for (var worldIdx = 0; worldIdx < ClientWorlds.Length; worldIdx++)
                {
                    clientWorld = ClientWorlds[worldIdx];
                    ghostCount = GetSingleton<GhostCount>(clientWorld);
                    var clientHasAll = ghostCount.GhostCountOnServer != 0 && ghostCount.GhostCountInstantiatedOnClient == ghostCount.GhostCountOnServer;
                    ValidateGhostCount(clientWorld, ghostCount);
                    if (!clientHasAll)
                        goto continueContinue;
                }
                return;
                continueContinue:;
            }
            Assert.Fail($"TickUntilClientsHaveAllGhosts failed after {maxTicks} ticks! {clientWorld.Name} has {ghostCount.ToFixedString()}!");
        }

        public static void ValidateGhostCount(World clientWorld, GhostCount ghostCount)
        {
            using var receivedGhostCount = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
            Assert.AreEqual(receivedGhostCount.CalculateEntityCount(), ghostCount.GhostCountReceivedOnClient, $"GhostCount.GhostCountReceivedOnClient struct does not match ghost received count on {clientWorld.Name}!");
            using var instantiatedGhostCount = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.Exclude<PendingSpawnPlaceholder>());
            var instancedCount = instantiatedGhostCount.CalculateEntityCount();
            //if(instancedCount > 0) UnityEngine.Debug.Log($"{instancedCount} vs {ghostCount} = {clientWorld.EntityManager.GetChunk(instantiatedGhostCount.ToEntityArray(Allocator.Temp)[0]).Archetype}");
            Assert.AreEqual(instancedCount, ghostCount.GhostCountInstantiatedOnClient, $"GhostCount.GhostCountInstantiatedOnClient struct does not match ghost instance count on {clientWorld.Name}!");
        }

        public NetCodeTestLatencyProfile? Profile { get; private set; }
        public void SetTestLatencyProfile(NetCodeTestLatencyProfile latencyProfile)
        {
            Debug.Assert(Profile == null && m_ServerWorld == null && m_ClientWorlds == null && DriverSimulatedDelay == 0 && DriverSimulatedDrop == 0, "Already setup!");
            Profile = latencyProfile;
            DriverSimulatedDelay = latencyProfile switch
            {
                NetCodeTestLatencyProfile.RTT60ms => 30, // Rounds up to 33.34ms i.e. 2 ticks each way.
                NetCodeTestLatencyProfile.RTT16ms_PL5 => 16, // Rounds up to 16.67ms i.e. 1 tick each way.
                _ => 0,
            };
            DriverSimulatedDrop = latencyProfile switch
            {
                NetCodeTestLatencyProfile.PL33 => 3, // Every Nth.
                NetCodeTestLatencyProfile.RTT16ms_PL5 => 20, // Every Nth.
                _ => 0,
            };
        }

        /// <summary>Attempt to log to all available packet dumps.</summary>
        /// <param name="msg">Message to log.</param>
        public void TryLogPacket(in FixedString512Bytes msg)
        {
#if NETCODE_DEBUG
            TryPacketDump(m_ServerWorld, msg);
            foreach (var clientWorld in m_ClientWorlds)
                TryPacketDump(clientWorld, msg);

            static void TryPacketDump(World world, in FixedString512Bytes msg)
            {
                if (world == null || !world.IsCreated) return;
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<EnablePacketLogging>());
                foreach (var logger in query.ToComponentDataArray<EnablePacketLogging>(Allocator.Temp))
                {
                    if(logger.NetDebugPacketCache.IsCreated)
                        logger.NetDebugPacketCache.Log(msg);
                }
            }
#endif
        }
    }
}
