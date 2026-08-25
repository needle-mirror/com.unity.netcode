#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
using Unity.NetCode.Tracing;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.TestTools.TestRunner;
using Unity.NetCode.Editor;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Tests
{
    internal struct NetCodeTestPrefabCollection : IComponentData
    {}
    internal struct NetCodeTestPrefab : IBufferElementData
    {
        public Entity Value;
    }

    /// <summary>
    /// Test class done to override certain <see cref="Netcode"/> functionatities for tests only.
    /// </summary>
    internal class TestNetcode : Netcode
    {
        NetCodeTestWorld m_TestWorld;

        internal TestNetcode(NetCodeTestWorld testWorld)
        {
            s_Instance = this;
            m_TestWorld = testWorld;
        }

        internal override NetcodeWorld CreateClientWorld()
        {
            int lastClientIndex = m_TestWorld.ClientWorlds?.Length ?? 0;
            m_TestWorld.CreateWorlds(server: false, numClients: 1, tickWorldAfterCreation: false);
            return m_TestWorld.ClientWorlds[lastClientIndex];
        }

        internal override NetcodeWorld CreateSingleWorldHost()
        {
            m_TestWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1, tickWorldAfterCreation: false);
            return m_TestWorld.m_ServerWorlds[^1];
        }

        internal override NetcodeWorld CreateServerWorld()
        {
            m_TestWorld.CreateWorlds(server: true, numClients: 0, tickWorldAfterCreation: false, throwIfWorldsAlreadyExist: false);
            return m_TestWorld.m_ServerWorlds[^1];
        }
    }
    internal class NetCodeTestWorld : IDisposable, INetworkStreamDriverConstructor, IAsyncDisposable, IPrebuildSetup, IPostBuildCleanup
    {
        internal interface ITestWorldStrategy : IDisposable
        {
            void Bootstrap(NetCodeTestWorld testWorld);
            NetcodeWorld CreateClientWorld(string name, bool thinClient, NetcodeWorld world = null);
            NetcodeWorld CreateServerWorld(string name, NetcodeWorld world = null);
            NetcodeWorld CreateHostWorld(string name, NetcodeWorld world = null);
            void DisposeClientWorld(NetcodeWorld clientWorld);
            void DisposeServerWorld(NetcodeWorld serverWorld);
            void TickNoAwait(float dt, bool skipSanityCheck = false);
            Task TickAsync(float dt, Awaitable waitInstruction = null, bool skipSanityCheck = false);
            void TickClientWorld(float dt, bool clientOnly);
            void TickServerWorld(float dt);
            void RemoveWorldFromUpdateList(World world);
            World DefaultWorld { get; }
            void DisposeDefaultWorld();
        }

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

        public World DefaultWorld => m_WorldStrategy.DefaultWorld;
        public NetcodeWorld ServerWorld
        {
            get { return m_ServerWorlds[0]; }
            set { m_ServerWorlds[0] = value; }
        }
        public NetcodeWorld[] ClientWorlds
        {
            get
            {
                if (m_ClientWorlds == null) return null;
                return m_ClientWorlds.ToArray();
            }
        }

        /// <summary>
        /// Logs how many times we've called <see cref="Tick"/>, zero-indexed
        /// (i.e. -1 before Tick is called, 0 on the first frame).
        /// </summary>
        public static int TickIndex { get; private set; }

        public List<NetcodeWorld> m_ClientWorlds;
        public NetcodeWorld m_ServerWorld => m_ServerWorlds[0];
        public List<NetcodeWorld> m_ServerWorlds;
        private ushort m_OldBootstrapAutoConnectPort;
        private bool m_DefaultWorldInitialized;
        private double m_ElapsedTime;
        public int MaxFrameTime = 100;
        public int DriverFixedTime = 16;
        public int ConnectTimeout = NetworkParameterConstants.ConnectTimeoutMS;
        public int MaxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts;
        public int DriverSimulatedDelay = 0; // ms
        public int DriverSimulatedJitter = 0;
        public int DriverSimulatedDrop = 0;
        public ApplyMode DriverSimulatorPacketMode = ApplyMode.AllPackets;
        public int DriverMaxMessageSize = NetworkParameterConstants.MaxMessageSize;
        public int DriverReliablePipelineWindowSize = 32;
        public int UseMultipleDrivers = 0;
        public bool ForceUDPSocket = false;
        public int DriverFragmentedPayloadCapacity = 16 * 1024;
        public int UseFakeSocketConnection = 1;
        private int WorldCreationIndex = 0;
        public NetCodeConfig m_OldGlobalConfig;
        List<NetCodeConfig> m_OldConfigsList = new();
        NetCodeConfig m_GlobalConfigForTests;

        INetworkStreamDriverConstructor m_OldDriverConstructor;

        public Scene m_EmptyScene;
        public string k_TestSceneName = "TestScene";
        public const string k_GeneratedFolderBasePath = "Assets/Tests/Generated/";
        NetCodeConfig m_TempBootstrapConfig;

        public int[] DriverFuzzFactor;
        public int DriverFuzzOffset = 0;
        public uint DriverRandomSeed = 0;

        private bool m_IsFirstTimeTicking = true;

        internal List<Scene> m_AdditionalScenesToCleanup = new();

#if UNITY_EDITOR
        private List<GameObject> m_GhostCollection;
        private BlobAssetStore m_BlobAssetStore;
#endif

        public bool AlwaysDispose;
        internal ITestWorldStrategy m_WorldStrategy;

        // Used to override the single world host mode for tests that don't support it
        // Uses editor
        public static bool OverrideUseSingleWorldHost
        {
            get
            {
#if NETCODETESTWORLD_FORCE_SINGLE_WORLD_HOST
                return true;
#elif UNITY_EDITOR && NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
                return UnityEditor.EditorPrefs.GetBool(k_ForceSingleWorldHostPrefKey, false);
#else
                return false;
#endif
            }
        }

        /// <summary>Configure how logging should occur in tests. We apply <see cref="LogLevel"/> and <see cref="DebugPackets"/> here.</summary>
        /// <param name="world">World to apply this config on.</param>
        private void SetupNetDebugConfig(World world)
        {
            // Treat the host (a server world that also runs client systems) as a SERVER for logging: it follows the
            // server log setting, not the client one. For pure client/server worlds (binary mode) a world is either
            // server xor client, so this is identical to the previous behaviour; it only changes the single-world host,
            // letting tests that isolate logs to one side (e.g. protocol-version errors) keep working — the host's
            // server-side logs follow EnableLogsOnServer instead of leaking via the client flag.
            var shouldLog = world.IsServer() ? EnableLogsOnServer : (world.IsClient() && EnableLogsOnClients);
            world.EntityManager.CreateSingleton(new NetCodeDebugConfig
            {
                // Hack essentially disabling all logging for this world, as we should never have exceptions going via this logger anyway.
                LogLevel = shouldLog ? LogLevel : NetDebug.LogLevelType.Exception,
                DumpPackets = DebugPackets,
            });
        }

        public NetCodeTestWorld(double initialElapsedTime = 42, bool keepExistingNetcodeInstance = false)
        {
            if (OverrideUseSingleWorldHost && !SingleWorldHostUtils.CurrentTestSupportsSingleWorldHost())
                Assert.Ignore("Test doesn't support single world host, but the global override is enabled.");
#if UNITY_EDITOR

#if UNITY_SERVER
            Debug.Log("WARNING: Your editor target is Server, some netcode tests (especially those involving connection) may fail!");
#endif
            // Not having a default world means RegisterUnloadOrPlayModeChangeShutdown has not been called which causes memory leaks
            DefaultWorldInitialization.DefaultLazyEditModeInitialize();
#endif
            SetupTestNetcodeAPI(keepExistingNetcodeInstance);

            m_OldConfigsList.AddRange(Resources.FindObjectsOfTypeAll<NetCodeConfig>());
            m_OldGlobalConfig = NetCodeConfig.Global;
            m_GlobalConfigForTests = ScriptableObject.CreateInstance<NetCodeConfig>();
            NetCodeConfig.Global = m_GlobalConfigForTests;

            if (m_GlobalConfigForTests != null)
            {
#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8); tests still set it to control deterministic resolution.
                if (m_OldGlobalConfig != null) m_OldGlobalConfig.IsGlobalConfig = false;
                m_GlobalConfigForTests.IsGlobalConfig = true;
#pragma warning restore 618
            }

            // overriding the driver constructor test wide instead of only at world creation time. This way we're sure any APIs relying on this will use the appropriate overriden drivers instead of the default ones.
            m_OldDriverConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;


            m_OldBootstrapAutoConnectPort = ClientServerBootstrap.AutoConnectPort;
            ClientServerBootstrap.AutoConnectPort = 0;
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
            // Dispose the persistent SystemTypeIndex lists first so they are released even if later teardown throws.
            DisposeSystemLists();

            // destroy configs that were generated by tests (the ones that weren't there when starting the test)
            foreach (var config in Resources.FindObjectsOfTypeAll<NetCodeConfig>())
            {
                if (!m_OldConfigsList.Contains(config))
                {
                    Object.DestroyImmediate(config, allowDestroyingAssets: true);
                }
            }
            if (m_WorldStrategy != null) // doing a null check since if this is null, this can hide other exceptions in unity's test log reports, if the exception happens before worlds are created
                m_WorldStrategy.Dispose();
            if (m_ClientWorlds != null)
            {
                for (int i = 0; i < m_ClientWorlds.Count; ++i)
                {
                    m_WorldStrategy.DisposeClientWorld(m_ClientWorlds[i]);
                }
            }

            if (m_ServerWorlds != null)
            {
                for (int i = 0; i < m_ServerWorlds.Count; i++)
                {
                    var netcodeWorld = m_ServerWorlds[i];
                    if (netcodeWorld.ExistsAndIsCreated())
                        m_WorldStrategy.DisposeServerWorld(netcodeWorld);
                }
            }

            m_ClientWorlds = null;
            m_ServerWorlds = null;
            ClientServerBootstrap.AutoConnectPort = m_OldBootstrapAutoConnectPort;

            TracingDataAccess.Dispose();
            TracingDataAccess.DisposeProcessedWorldData();

#if UNITY_EDITOR
            if (m_GhostCollection != null)
                m_BlobAssetStore.Dispose();
#endif
            if (m_GlobalConfigForTests)
                UnityEngine.Object.DestroyImmediate(m_GlobalConfigForTests, allowDestroyingAssets: true);
            if (m_OldGlobalConfig != null)
#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8); tests still set it to control deterministic resolution.
                m_OldGlobalConfig.IsGlobalConfig = true;
#pragma warning restore 618
            NetCodeConfig.Global = m_OldGlobalConfig;
            m_GlobalConfigForTests = null;
            m_OldGlobalConfig = null;

            NetworkStreamReceiveSystem.DriverConstructor = m_OldDriverConstructor;

            Netcode.Reset(); // happens at the end to make sure Netcode APIs stay accessible as long as possible, even in System's OnDestroy

            // sanity check
            {
                List<World> toForceCleanup = new();
                foreach (var world in World.All)
                {
                    if (world.IsClient() || world.IsServer() || world.IsThinClient())
                    {
                        Debug.LogError($"world {world} wasn't cleaned up, force cleaning now");
                        toForceCleanup.Add(world);
                    }
                }

                foreach (var world in toForceCleanup)
                {
                    world.Dispose();
                }

                if (toForceCleanup.Count > 0)
                {
                    Assert.Fail("world cleanup issue, sanity check failed");
                }
            }

            Assert.AreEqual(0, ClientServerBootstrap.ServerWorlds.Count);
            Assert.AreEqual(0, ClientServerBootstrap.ClientWorlds.Count);
        }

        public void DisposeAllClientWorlds()
        {
            for (int i = 0; i < m_ClientWorlds.Count; ++i)
            {
                if (m_ClientWorlds[i].IsHost()) continue; // gonna be disposed server side
                m_WorldStrategy.DisposeClientWorld(m_ClientWorlds[i]);
            }

            m_ClientWorlds = null;
        }

        public void DisposeServerWorld()
        {
            m_WorldStrategy.DisposeServerWorld(m_ServerWorlds[0]);
            m_ServerWorlds.Clear();
        }

        public void DisposeDefaultWorld()
        {
            m_WorldStrategy.DisposeDefaultWorld();
        }

        private void SetupTestNetcodeAPI(bool keepExistingNetcodeInstance)
        {
            // Only keep an existing instance if it's actually initialized
            keepExistingNetcodeInstance &= Netcode.IsInitialized;
            if (!keepExistingNetcodeInstance)
            {
                Netcode.DisposeAfterEnterEditMode();
                var testNetcode = new TestNetcode(this); // this auto registers to Netcode.Instance
                testNetcode.Initialize();

                // testNetcode.InitializeWithAssets(); // TODO-release@entitiesIntegration for now this is called as part of Netcode.Initialize
                Assert.IsTrue(Netcode.Instance is TestNetcode);
            }
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

        // Cached as SystemTypeIndex[] (instead of IReadOnlyList<Type>) so we populate the per-world lists from
        // GetAllSystemTypeIndices and avoid the reflection used by GetAllSystems. Managed arrays so the static cache
        // does not leak native memory across domain reloads.
        internal static SystemTypeIndex[] s_AllClientSystems;
        internal static SystemTypeIndex[] s_AllThinClientSystems;
        internal static SystemTypeIndex[] s_AllServerSystems;
        internal static SystemTypeIndex[] s_AllHostSystems;

        // SystemTypeIndex lists so we can use the reflection-free SortSystemTypesInCreationOrder and
        // AddSystemsToRootLevelSystemGroups overloads. Allocated Persistent and disposed in Dispose (and guard-disposed
        // before re-allocation in Bootstrap).
        internal static NativeList<SystemTypeIndex> m_ControlSystems;
        internal static NativeList<SystemTypeIndex> m_ClientSystems;
        internal static NativeList<SystemTypeIndex> m_ThinClientSystems;
        internal static NativeList<SystemTypeIndex> m_ServerSystems;
        internal static NativeList<SystemTypeIndex> m_HostSystems;
        // Baking systems are passed to BakingSettings.ExtraSystems (which takes Type), so this stays a List<Type>.
        internal static List<Type> m_BakingSystems;

        public List<string> TestSpecificAdditionalAssemblies = new List<string>(8);

        int m_NumClients = 0;

        private static bool IsFromNetCodeAssembly(Type sys)
        {
            return sys.Assembly.FullName.StartsWith("Unity.NetCode,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Entities,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Transforms,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.Scenes,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.Editor,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.Editor.Tests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.GhostAdapterRuntime.Tests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.Physics.Editor.Tests,", StringComparison.Ordinal) ||
                sys.Assembly.FullName.StartsWith("Unity.NetCode.TestsUtils.Runtime.Tests,", StringComparison.Ordinal) ||
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
            Bootstrap(includeNetCodeSystems ? SystemResolutionMode.NetcodeAndUserSystems : SystemResolutionMode.OnlyIncludeUserSystems, false, false, userSystems);
        }

        public enum SystemResolutionMode
        {
            AllSystems,
            NetcodeAndUserSystems,
            OnlyIncludeUserSystems,
        }

        public void Bootstrap(SystemResolutionMode systemResolutionMode, bool includePresentationSystemsOnClient, bool useNormalMainLoop, params Type[] userSystems)
        {
            if (useNormalMainLoop)
            {
                m_WorldStrategy = new PlayModeTestWorldStrategy();
            }
            else
            {
                m_WorldStrategy = new EditModeTestWorldStrategy();
            }

            m_WorldStrategy.Bootstrap(this);

            DisposeSystemLists();
            m_ControlSystems = new NativeList<SystemTypeIndex>(256, Allocator.Persistent);
            m_ClientSystems = new NativeList<SystemTypeIndex>(256, Allocator.Persistent);
            m_ThinClientSystems = new NativeList<SystemTypeIndex>(256, Allocator.Persistent);
            m_ServerSystems = new NativeList<SystemTypeIndex>(256, Allocator.Persistent);
            m_HostSystems = new NativeList<SystemTypeIndex>(256, Allocator.Persistent);
            m_BakingSystems = new List<Type>(256);
#if !UNITY_SERVER || UNITY_EDITOR
            m_ControlSystems.Add(TypeManager.GetSystemTypeIndex<TickClientInitializationSystem>());
            m_ControlSystems.Add(TypeManager.GetSystemTypeIndex<TickClientSimulationSystem>());
            m_ControlSystems.Add(TypeManager.GetSystemTypeIndex<TickClientPresentationSystem>());
#endif
#if !UNITY_CLIENT || UNITY_EDITOR
            m_ControlSystems.Add(TypeManager.GetSystemTypeIndex<TickServerInitializationSystem>());
            m_ControlSystems.Add(TypeManager.GetSystemTypeIndex<TickServerSimulationSystem>());
#endif
            m_ControlSystems.Add(TypeManager.GetSystemTypeIndex<DriverMigrationSystem>());

            WorldSystemFilterFlags clientFlags = WorldSystemFilterFlags.ClientSimulation;
            if (includePresentationSystemsOnClient)
                clientFlags |= WorldSystemFilterFlags.Presentation;
            s_AllClientSystems ??= GetAllSystemTypeIndicesArray(clientFlags);
            s_AllThinClientSystems ??= GetAllSystemTypeIndicesArray(WorldSystemFilterFlags.ThinClientSimulation);
            s_AllServerSystems ??= GetAllSystemTypeIndicesArray(WorldSystemFilterFlags.ServerSimulation);
            s_AllHostSystems ??= GetAllSystemTypeIndicesArray(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);

            bool ShouldIncludeSystemFilter(Type type)
            {
                switch (systemResolutionMode)
                {
                    case SystemResolutionMode.AllSystems:
                        return true;
                    case SystemResolutionMode.NetcodeAndUserSystems:
                        return IsFromNetCodeAssembly(type) || IsFromTestSpecificAdditionalAssembly(type);
                    case SystemResolutionMode.OnlyIncludeUserSystems:
                        return IsFromTestSpecificAdditionalAssembly(type);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(systemResolutionMode), systemResolutionMode, null);
                }
            }

            AddIfShouldIncludeSystemFilter(s_AllClientSystems, m_ClientSystems);
            AddIfShouldIncludeSystemFilter(s_AllThinClientSystems, m_ThinClientSystems);
            AddIfShouldIncludeSystemFilter(s_AllServerSystems, m_ServerSystems);
            AddIfShouldIncludeSystemFilter(s_AllHostSystems, m_HostSystems);

            var updateWorldTimeSystem = TypeManager.GetSystemTypeIndex<Unity.Entities.UpdateWorldTimeSystem>();
            m_ClientSystems.Add(updateWorldTimeSystem);
            m_ThinClientSystems.Add(updateWorldTimeSystem);
            m_ServerSystems.Add(updateWorldTimeSystem);
            m_HostSystems.Add(updateWorldTimeSystem);


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
                        flags |= ((WorldSystemFilterAttribute)attrs[0]).ChildDefaultFilterFlags;
                    }
                }

                var sysIndex = TypeManager.GetSystemTypeIndex(sys);
                if ((flags & WorldSystemFilterFlags.ClientSimulation) != 0)
                {
                    m_ClientSystems.Add(sysIndex);
                }
                if ((flags & WorldSystemFilterFlags.ThinClientSimulation) != 0)
                {
                    m_ThinClientSystems.Add(sysIndex);
                }
                if ((flags & WorldSystemFilterFlags.ServerSimulation) != 0)
                {
                    m_ServerSystems.Add(sysIndex);
                }
                if (((flags & WorldSystemFilterFlags.ClientSimulation) != 0) || ((flags & WorldSystemFilterFlags.ServerSimulation) != 0))
                {
                    m_HostSystems.Add(sysIndex);
                }
                if ((flags & WorldSystemFilterFlags.BakingSystem) != 0)
                {
                    m_BakingSystems.Add(sys);
                }
            }

            void AddIfShouldIncludeSystemFilter(SystemTypeIndex[] allSystems, NativeList<SystemTypeIndex> targetSystems)
            {
                foreach (var system in allSystems)
                {
                    // ShouldIncludeSystemFilter needs the managed Type to inspect the assembly, so resolve it here.
                    // This only happens at test setup, not on the per-world creation path.
                    if (ShouldIncludeSystemFilter(TypeManager.GetSystemType(system)))
                    {
                        targetSystems.Add(system);
                    }
                }
            }
        }

        static SystemTypeIndex[] GetAllSystemTypeIndicesArray(WorldSystemFilterFlags filterFlags)
        {
            // GetAllSystemTypeIndices returns the indices directly, avoiding the reflection that GetAllSystems uses to
            // map them back to System.Type. We copy into a managed array so the static cache holds no native memory.
            var indices = DefaultWorldInitialization.GetAllSystemTypeIndices(filterFlags);
            var result = new SystemTypeIndex[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                result[i] = indices[i];
            }
            return result;
        }

        static void DisposeSystemLists()
        {
            if (m_ControlSystems.IsCreated)
            {
                m_ControlSystems.Dispose();
            }
            if (m_ClientSystems.IsCreated)
            {
                m_ClientSystems.Dispose();
            }
            if (m_ThinClientSystems.IsCreated)
            {
                m_ThinClientSystems.Dispose();
            }
            if (m_ServerSystems.IsCreated)
            {
                m_ServerSystems.Dispose();
            }
            if (m_HostSystems.IsCreated)
            {
                m_HostSystems.Dispose();
            }
        }

        public void CreateAdditionalClientWorlds(int numClients, bool tickWorldAfterCreation = true, float dt = k_defaultDT, bool useThinClients = false)
        {
            CreateWorlds(false, numClients, tickWorldAfterCreation, dt, useThinClients, throwIfWorldsAlreadyExist: false);
        }

        public async Task CreateAdditionalClientWorldsAsync(int numClients, bool tickWorldAfterCreation = true,
            float dt = k_defaultDT, bool useThinClients = false)
        {
            await CreateWorldsAsync(false, numClients, tickWorldAfterCreation, dt, useThinClients, throwIfWorldsAlreadyExist: false);
        }

        public void CreateWorlds(bool server, int numClients, bool tickWorldAfterCreation = true,
            float dt = k_defaultDT,
            bool useThinClients = false, bool throwIfWorldsAlreadyExist = true, int numHostWorlds = 0)
        {
            CreateWorlds(server ? 1 : 0, numClients, tickWorldAfterCreation, dt, useThinClients, throwIfWorldsAlreadyExist, numHostWorlds);
        }

        public void CreateWorlds(int numServer, int numClients, bool tickWorldAfterCreation = true, float dt = k_defaultDT,
            bool useThinClients = false, bool throwIfWorldsAlreadyExist = true, int numHostWorlds = 0)
        {
            Assert.IsFalse(tickWorldAfterCreation && m_WorldStrategy is PlayModeTestWorldStrategy, "Calling CreateWorlds from playmode tests will result in editor hang because of the below wait if we expect to tick the world after creation. Make sure to use the async version of this method or to not tick the world after creation.");

            CreateWorldsAsync(numServer, numClients, tickWorldAfterCreation, dt, useThinClients, throwIfWorldsAlreadyExist, numHostWorlds: numHostWorlds).Wait();
        }

        public async Task CreateWorldsAsync(bool server, int numClients, bool tickWorldAfterCreation = true,
            float dt = k_defaultDT, bool useThinClients = false, bool throwIfWorldsAlreadyExist = true,
            int numHostWorlds = 0)
        {
            await CreateWorldsAsync(server ? 1 : 0, numClients, tickWorldAfterCreation, dt, useThinClients, throwIfWorldsAlreadyExist, numHostWorlds);
        }

        public async Task CreateWorldsAsync(int numServer, int numClients, bool tickWorldAfterCreation = true, float dt = k_defaultDT, bool useThinClients = false, bool throwIfWorldsAlreadyExist = true, int numHostWorlds = 0)
        {
            // Most tests should work with both binary and single world host, so instead of duplicating all tests
            // we create this way to override which mode to use so we can easily test both modes in our CI.
            if (OverrideUseSingleWorldHost)
            {
                if (numServer > 0)
                {
                    numHostWorlds += numServer;
                    numServer = 0;
                }
            }

            m_NumClients += numClients;
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;
#if UNITY_EDITOR || NETCODE_DEBUG
            var oldDebugPort = GhostStatsConnection.Port;
            GhostStatsConnection.Port = 0;
#endif
            if (!m_DefaultWorldInitialized && DefaultWorld != null)
            {
                TypeManager.SortSystemTypesInCreationOrder(m_ControlSystems); // Ensure CreationOrder is respected.
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(DefaultWorld,
                    m_ControlSystems);
                m_DefaultWorldInitialized = true;
            }

            var testMethodName = NUnit.Framework.TestContext.CurrentContext.Test.MethodName;

            if (m_ServerWorlds == null && (numServer > 0 || numHostWorlds > 0))
                m_ServerWorlds = new();
            if (numServer > 0)
            {
                if (m_ServerWorlds.Count > 0 && throwIfWorldsAlreadyExist)
                    throw new InvalidOperationException("Server world already created");
                for (int i = 0; i < numServer; i++)
                {
                    var newServer = m_WorldStrategy.CreateServerWorld($"ServerTest-{i}");
                    SetupNetDebugConfig(newServer);
                    m_ServerWorlds.Add(newServer);
                }
            }

            // calling this before client world creation, so that the order of update calls when ticking remains server/host first then clients second
            if (numHostWorlds > 0)
            {
                if (numHostWorlds > 1) throw new NotImplementedException();
                // TODO handle calling this more than once per test
                // We don't add the host world to the client list, as we want a deterministic way to retrieve extra client worlds. If I do testWorld.ClientWorlds[0], which client is it going to return if it can contain hosts?
                // For now, treating a host world as just a server world.
                var newServer = m_WorldStrategy.CreateHostWorld($"HostTest-0");
                SetupNetDebugConfig(newServer);
                m_ServerWorlds.Add(newServer);
            }

            if (numClients > 0)
            {
                var oldSize = m_ClientWorlds?.Count ?? 0;
                var newSize = oldSize + numClients;

                if (m_ClientWorlds != null && m_ClientWorlds.Count > 0 && !m_ClientWorlds[0].IsHost() && throwIfWorldsAlreadyExist)
                    throw new InvalidOperationException("Client worlds already created");
                if (m_ClientWorlds == null)
                    m_ClientWorlds = new List<NetcodeWorld>(newSize);

                WorldCreationIndex = 0;
                for (int i = oldSize; i < newSize; ++i)
                {
                    try
                    {
                        WorldCreationIndex = i;

                        var clientWorldName = $"{(useThinClients ? "Thin" : "")}ClientTest-{i}";
                        m_ClientWorlds.Add(m_WorldStrategy.CreateClientWorld(clientWorldName, useThinClients));

                        SetupNetDebugConfig(m_ClientWorlds[i]);
                    }
                    catch (Exception e)
                    {
                        m_ClientWorlds = null;
                        Debug.LogException(e);
                        throw;
                    }
                }
            }

#if UNITY_EDITOR || NETCODE_DEBUG
            GhostStatsConnection.Port = oldDebugPort;
#endif
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

            // This needs to execute before the first tick
            TrySuppressNetDebug(true, true);

            //Run 1 tick so that all the ghost collection and the ghost collection component run once.
            if (tickWorldAfterCreation)
                await TickAsync(dt);

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
            var success = true;

            void Suppress(NetcodeWorld world)
            {
                if (AlwaysDispose || world.ExistsAndIsCreated())
                {
                    success &= TryGetSingletonEntity<NetDebug>(world) != default;
                    if (success)
                    {
                        ref var netDebug = ref GetSingletonRW<NetDebug>(world).ValueRW;
                        netDebug.SuppressApplicationRunInBackgroundWarning = suppressRunInBackground;
                        netDebug.SuppressApprovalRpcSentWhenApprovalFlowDisabledWarning = suppressApprovalRpc;
                    }
                }
            }

            if (m_ServerWorlds != null)
            {
                foreach (var serverWorld in m_ServerWorlds)
                {
                    Suppress(serverWorld);
                }
            }

            if (ClientWorlds != null)
            {
                foreach (var clientWorld in ClientWorlds)
                {
                    Suppress(clientWorld);
                }
            }
            return success;
        }
#if UNITY_EDITOR || NETCODE_DEBUG
        public Entity TryCreateGhostMetricsSingleton(World world)
        {
            if (!world.EntityManager.CreateEntityQuery(typeof(GhostMetricsMonitor)).TryGetSingletonEntity<GhostMetricsMonitor>(out var singletonEntity))
            {
                singletonEntity = world.EntityManager.CreateEntity(
                    typeof(GhostMetricsMonitor),
                    typeof(SnapshotMetrics),
                    typeof(GhostMetrics),
                    typeof(GhostNames),
                    typeof(PredictionErrorNames)
                );
            }
            return singletonEntity;
        }
#endif

        public void ApplyDT(float dt, bool skipSanityCheck = false)
        {
            if (!skipSanityCheck)
                Assert.That(dt, Is.Not.InRange(0.9999f, 1.0001f), "sanity check after making the silly mistake of setting '1' for count, but used dt instead and spending too much time debugging this. Did you mean to set a number of iteration count to 1 and used the wrong parameter?"); // this check can be removed if you absolutely need to set 1 whole second of delta time.
            ApplyDTServer(dt);
            ApplyDTClient(dt);
        }

        public void ApplyDTServer(float dt)
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
            DefaultWorld?.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ServerWorlds != null)
            {
                foreach (var serverWorld in m_ServerWorlds)
                {
                    if (serverWorld.ExistsAndIsCreated())
                        serverWorld.SetTime(new TimeData(m_ElapsedTime, dt));
                }
            }
        }

        private bool m_IsFirstTimeTickingClient = true;
        private double m_ElapsedTimeClient = 0;
        public static int ClientTickIndex { get; private set; }

        public void ApplyDTClient(float dt)
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
                    if (clientWorld.IsCreated)
                        clientWorld.SetTime(new TimeData(m_ElapsedTimeClient, dt));
                }
            }
        }

        const float k_defaultDT = 1f / 60f;

        public void Tick(float dt = k_defaultDT, bool skipSanityCheck = false)
        {
            m_WorldStrategy.TickNoAwait(dt, skipSanityCheck: skipSanityCheck);
        }

        public void TickMultiple(int numTicks, float dt = k_defaultDT)
        {
            for (int t = 0; t < numTicks; ++t)
            {
                Tick(dt);
            }
        }

        internal enum AwaitableType
        {
            NextFrame,
            EndOfFrame,
            FixedUpdate
        }

        /// <summary>
        /// Executes a full engine frame, including all systems.
        /// This will execute until the EndOfFrame, contrary to yield return null which executes until after Update()
        /// In edit mode tests, this ticks NetcodeTestWorld normally with no frame yield and so this method is not really async.
        /// </summary>
        /// <param name="dt"></param>
        /// <param name="waitInstruction">By default, ticking ticks for the full frame (so until EndOfFrame). To end up in a different part of the frame, use Awaitable.Something to and pass it in as an argument here.</param>
        /// <returns></returns>
        public async Task TickAsync(float dt = k_defaultDT, Awaitable waitInstruction = null)
        {
            await m_WorldStrategy.TickAsync(dt, waitInstruction);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="count"></param>
        /// <param name="dt"></param>
        /// <param name="awaitableType">can't call the same Awaitable instance multiple times, it needs to be a new instance each frame</param>
        /// <exception cref="NotImplementedException"></exception>
        public async Task TickMultipleAsync(int count, float dt = k_defaultDT, AwaitableType awaitableType = AwaitableType.NextFrame)
        {
            for (int i = 0; i < count; i++)
            {
                Awaitable waitInstruction;
                switch (awaitableType)
                {
                    case AwaitableType.NextFrame:
                        waitInstruction = Awaitable.NextFrameAsync();
                        break;
                    case AwaitableType.EndOfFrame:
                        // waitInstruction = Awaitable.EndOfFrameAsync();
                        throw new Exception("Wait for EndOfFrame isn't supported. Headless unity won't execute EndOfFrame.");
                    case AwaitableType.FixedUpdate:
                        waitInstruction = Awaitable.FixedUpdateAsync();
                        break;
                    default:
                        throw new NotImplementedException();
                }
                await TickAsync(dt, waitInstruction);
            }
        }

        public void TickServerWorld(float dt = k_defaultDT)
        {
            m_WorldStrategy.TickServerWorld(dt);
        }

#if !UNITY_SERVER || UNITY_EDITOR

        // This is close to the same as the Tick method, but only ticks the client world
        // This is useful if a test needs to do partial ticks without ticking the server, or to get very specific timings between server and client
        public void TickClientWorld(float dt = k_defaultDT, bool clientOnly = true)
        {
            m_WorldStrategy.TickClientWorld(dt, clientOnly);
        }
#endif

        public void SetPacketDropPercentForWorld(World world, int percent)
        {
            // ModifySimulatorStageParameters writes to the driver's pipeline buffers, which RPC and
            // snapshot send/recv jobs hold read handles on. Complete in-flight jobs before mutating to
            // avoid an AtomicSafetyHandle violation.
            world.EntityManager.CompleteAllTrackedJobs();

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
            if (query.IsEmptyIgnoreFilter)
                throw new InvalidOperationException($"No NetworkStreamDriver on {world.Name}!");
            ref var streamDriver = ref query.GetSingletonRW<NetworkStreamDriver>().ValueRW;
            ref var driverStore = ref streamDriver.DriverStore;
            bool anySimulatorMutated = false;
            for (int driverId = driverStore.FirstDriver; driverId < driverStore.LastDriver; ++driverId)
            {
                ref var driver = ref driverStore.GetDriverRW(driverId);
                if (!driver.CurrentSettings.TryGet<SimulatorUtility.Parameters>(out var simParams))
                    continue;
                simParams.PacketDropPercentage = percent;
                driver.ModifySimulatorStageParameters(simParams);
                anySimulatorMutated = true;
            }
            if (!anySimulatorMutated)
                throw new InvalidOperationException(
                    $"{nameof(SetPacketDropPercentForWorld)}: no driver in '{world.Name}' has a simulator pipeline stage installed. " +
                    $"Set {nameof(DriverSimulatedDelay)}, {nameof(DriverSimulatedJitter)}, {nameof(DriverSimulatedDrop)}, or {nameof(DriverFuzzFactor)} to a non-zero value before {nameof(CreateWorlds)} so the simulator stage gets installed.");
        }

        public void MigrateServerWorld(NetcodeWorld suppliedWorld = null)
        {
            DriverMigrationSystem migrationSystem = default;

            foreach (var world in World.All)
            {
                if ((migrationSystem = world.GetExistingSystemManaged<DriverMigrationSystem>()) != null)
                    break;
            }

            var ticket = migrationSystem.StoreWorld(ServerWorld);
            DisposeServerWorld();

            Assert.True(suppliedWorld == null || suppliedWorld.IsServer());
            NetcodeWorld newWorld = (NetcodeWorld)migrationSystem.LoadWorld(ticket, suppliedWorld);
            m_ServerWorlds.Add(m_WorldStrategy.CreateServerWorld(newWorld.Name, newWorld));

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
            m_WorldStrategy.DisposeClientWorld(ClientWorlds[index]);

            NetcodeWorld newWorld = (NetcodeWorld)migrationSystem.LoadWorld(ticket, suppliedWorld);
            m_ClientWorlds[index] = m_WorldStrategy.CreateClientWorld(newWorld.Name, false, newWorld);

            Assert.True(newWorld.Name == m_ClientWorlds[index].Name);

            TrySuppressNetDebug(true, true);
        }

        public NetcodeWorld CreateServerWorld(string name, NetcodeWorld world = null)
        {
            // return m_WorldStrategy.CreateServerWorld(name, world);
            NetcodeWorld oldServerWorld = null;
            if (m_ServerWorlds != null && m_ServerWorlds.Count > 0)
                oldServerWorld = m_ServerWorlds[^1];
            CreateWorlds(true, 0, false, throwIfWorldsAlreadyExist: false);
            NetcodeWorld lastCreatedWorld = m_ServerWorlds[^1];
            Assert.AreNotEqual(oldServerWorld, lastCreatedWorld, "sanity check failed");
            return lastCreatedWorld;
        }

        public void RestartClientWorld(int index)
        {
            var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
            NetworkStreamReceiveSystem.DriverConstructor = this;

            var name = m_ClientWorlds[index].Name;
            m_WorldStrategy.DisposeClientWorld(ClientWorlds[index]);

            m_ClientWorlds[index] = m_WorldStrategy.CreateClientWorld(name, false);
            SetupNetDebugConfig(ClientWorlds[index]);
            NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;
        }

        bool HasSimulatorParams =>
            DriverSimulatedDelay != 0 ||
            DriverSimulatedJitter != 0 ||
            DriverSimulatedDrop != 0 ||
            (DriverFuzzFactor != null && DriverFuzzFactor.Length > 0);

        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            //We are forcing here the connection type to be a socket but thxe connection is instead based on IPC.
            //The reason for that is that we want to be able to disable any check/logic that optimise for that use case
            //by default in the test.
            //It is possible however to disable this behavior using the provided opt
            var transportType = UseFakeSocketConnection == 1 ? TransportType.Socket : TransportType.IPC;

            var networkSettings = GetClientNetworkSettings(world, out int fuzzFactor);
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
            if (ForceUDPSocket)
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                driverInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
#else
                throw new NotSupportedException($"{nameof(ForceUDPSocket)} requires the UDP network interface, which is unavailable on WebGL player builds.");
#endif
            }
            else if (UseMultipleDrivers == 0)
                driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
            else
            {
                if ((WorldCreationIndex & 0x1) == 0)
                {
                    driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
                }
#if !UNITY_WEBGL || UNITY_EDITOR
                else
                {
                    driverInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
                }
#endif
            }

            //Fake the driver as it is always using a socket, even though we are also using IPC as a transport medium
            if (HasSimulatorParams)
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

        public NetworkSettings GetClientNetworkSettings(World world, out int fuzzFactor)
        {
            var packetDelay = DriverSimulatedDelay;
            int networkRate = 60;

            // All 3 packet types every frame stored for maximum delay, doubled for safety margin
            int maxPackets = 2 * (networkRate * 3 * (packetDelay + DriverSimulatedJitter) + 999) / 1000;

            fuzzFactor = 0;
            // We name it "ClientTest-XX", so extract the XX.
            if (DriverFuzzFactor != null && DriverFuzzFactor.Length > 0)
            {
                var worldId = CalculateWorldId(world);
                if (DriverFuzzFactor?.Length >= worldId + 1)
                    fuzzFactor = DriverFuzzFactor[worldId];
            }

            var simParams = new SimulatorUtility.Parameters
            {
                Mode = DriverSimulatorPacketMode,
                MaxPacketSize = NetworkParameterConstants.MTU,
                MaxPacketCount = maxPackets,
                PacketDelayMs = packetDelay,
                PacketJitterMs = DriverSimulatedJitter,
                PacketDropInterval = DriverSimulatedDrop,
                FuzzFactor = fuzzFactor,
                FuzzOffset = DriverFuzzOffset,
                RandomSeed = DriverRandomSeed
            };
            var networkSettings = new NetworkSettings();
            networkSettings
                .WithReliableStageParameters(windowSize: DriverReliablePipelineWindowSize)
                .WithFragmentationStageParameters(payloadCapacity: DriverFragmentedPayloadCapacity)
                .WithNetworkConfigParameters
                (
                    maxFrameTimeMS: MaxFrameTime,
                    fixedFrameTimeMS: DriverFixedTime,
                    connectTimeoutMS: ConnectTimeout,
                    maxConnectAttempts: MaxConnectAttempts,
                    maxMessageSize: DriverMaxMessageSize
                );
            if (HasSimulatorParams)
                networkSettings.AddRawParameterStruct(ref simParams);

            return networkSettings;
        }

        public static int CalculateWorldId(World world)
        {
            var regex = new Regex(@"(ClientTest-)(\d)", RegexOptions.Singleline);
            var match = regex.Match(world.Name);
            return int.Parse(match.Groups[2].Value);
        }

        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            var networkSettings = GetServerNetworkSettings();

            if (!ForceUDPSocket)
            {
                var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
                driverInstance.driver = NetworkDriver.Create(new IPCNetworkInterface(), networkSettings);
                DefaultDriverBuilder.CreateServerPipelines(ref driverInstance);
                driverStore.RegisterDriver(TransportType.IPC, driverInstance);
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            if (UseMultipleDrivers != 0 || ForceUDPSocket)
            {
                var socketInstance = new NetworkDriverStore.NetworkDriverInstance();
                socketInstance.driver = NetworkDriver.Create(new UDPNetworkInterface(), networkSettings);
                DefaultDriverBuilder.CreateServerPipelines(ref socketInstance);
                driverStore.RegisterDriver(TransportType.Socket, socketInstance);
            }
#endif
        }

        public NetworkSettings GetServerNetworkSettings()
        {
            var networkSettings = new NetworkSettings();
            networkSettings
                .WithReliableStageParameters(windowSize: DriverReliablePipelineWindowSize)
                .WithFragmentationStageParameters(payloadCapacity: DriverFragmentedPayloadCapacity)
                .WithNetworkConfigParameters(
                    maxFrameTimeMS: MaxFrameTime,
                    fixedFrameTimeMS: DriverFixedTime,
                    maxMessageSize: DriverMaxMessageSize
                );

            return networkSettings;
        }

        public void Connect(float dt = k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true, bool tickUntilConnected = true, bool enableGhostReplication = false, bool withConnectionState = false)
        {
            ConnectAsync(dt, maxSteps, failTestIfConnectionFails, tickUntilConnected, enableGhostReplication: enableGhostReplication, withConnectionState: withConnectionState).Wait();
        }

        /// <summary>
        /// Will throw if connect fails.
        /// </summary>
        public async Task ConnectAsync(float dt = k_defaultDT, int maxSteps = 7, bool failTestIfConnectionFails = true, bool tickUntilConnected = true, bool enableGhostReplication = false, bool withConnectionState = false, bool serverListen = true)
        {
            // Initialize prefab registry as is usually done in Connect/Listen routines
            var ep = NetworkEndpoint.LoopbackIpv4;
            ep.Port = 7979;
            if (serverListen)
                Debug.Assert(GetSingletonRW<NetworkStreamDriver>(ServerWorld).ValueRW.Listen(ep), $"[{ServerWorld.Name}] Listen failed during Connect!");
            if ((ClientWorlds == null || ClientWorlds.Length == 0) && !ServerWorld.IsHost())
                throw new InvalidOperationException("If the server isn't a host, there should be other client worlds to connect to!");
            if (ServerWorld.IsHost())
                await TickAsync(dt);

            if (ClientWorlds != null && ClientWorlds.Length > 0)
            {
                var connectionEntities = new Entity[ClientWorlds.Length];
                for (int i = 0; i < ClientWorlds.Length; ++i)
                {
                    if (ClientWorlds[i].IsHost())
                    {
                        await TickAsync(dt); // Do a single tick to actually let the connections be updated
                        continue;
                    }

                    Entity entityToUse;
                    if (withConnectionState)
                        entityToUse = ClientWorlds[i].EntityManager.CreateEntity(typeof(ConnectionState));
                    else
                        entityToUse = default;

                    connectionEntities[i] = GetSingletonRW<NetworkStreamDriver>(ClientWorlds[i]).ValueRW.Connect(ClientWorlds[i].EntityManager, ep, ent: entityToUse);
                }

                int stepsLeft = maxSteps;
                if (tickUntilConnected)
                {
                    for (int i = 0; i < ClientWorlds.Length; ++i)
                    {
                        if (ClientWorlds[i].IsHost()) continue;
                        await TickUntilConnectedAsync(ClientWorlds[i], dt, stepsLeft, connectionEntities[i], failTestIfConnectionFails);
                    }
                }
            }

            if (enableGhostReplication)
            {
                GoInGame();
            }
        }

        public void TickUntilConnected(World world, float dt = k_defaultDT, int maxSteps = 7, Entity connectionEntity = default, bool failTestIfConnectionFails = true)
        {
            TickUntilConnectedAsync(world, dt, maxSteps, connectionEntity, failTestIfConnectionFails).Wait();
        }

        public async Task TickUntilConnectedAsync(World world, float dt = k_defaultDT, int maxSteps = 7, Entity connectionEntity = default, bool failTestIfConnectionFails = true)
        {
            var initialMaxSteps = maxSteps;
            int stepsLeft = maxSteps;

            while (TryGetSingletonEntity<NetworkId>(world) == Entity.Null)
            {
                if (stepsLeft <= 0)
                {
                    var streamDriver = GetSingleton<NetworkStreamDriver>(world);
                    if (failTestIfConnectionFails)
                    {
                        string connectionState = "No_NetworkConnection_Entity_Left";
                        if (world.EntityManager.Exists(connectionEntity))
                        {
                            var nsc = world.EntityManager.GetComponentData<NetworkStreamConnection>(connectionEntity);
                            connectionState = $"{connectionEntity.ToFixedString()} NetworkStreamConnection[{nsc.Value.ToFixedString()}-{nsc.CurrentState.ToString()}]";
                        }
                        Assert.Fail($"ClientWorld[{world.Name}] failed to connect to the server after {initialMaxSteps} ticks! Driver status: {connectionState}!");
                    }

                    return;
                }
                --stepsLeft;
                await m_WorldStrategy.TickAsync(dt);
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
            Assert.True(clientIndex < ClientWorlds.Length);
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

        public void SetSingleton<T>(World world, T value) where T : unmanaged, IComponentData
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
            var entity = query.GetSingletonEntity();
            world.EntityManager.SetComponentData(entity, value);
        }

        /// <summary>Set <see cref="ClientTickRate.AlwaysRollbackAllPredictedGhosts"/> on every client world, creating the singleton from defaults when a test hasn't made one. Call after CreateWorlds (and after any manual ClientTickRate creation), before ticking.</summary>
        public void SetAlwaysRollbackAllPredictedGhosts(bool value)
        {
            foreach (var client in ClientWorlds)
            {
                var em = client.EntityManager;
                var entity = TryGetSingletonEntity<ClientTickRate>(client);
                // Seed from DefaultClientTickRate, not a zero-initialized ClientTickRate - the latter has
                // invalid Prediction/InterpolationTimeScale fields that fail validation and stall prediction.
                if (entity == Entity.Null)
                    entity = em.CreateSingleton(NetworkTimeSystem.DefaultClientTickRate);
                var rate = em.GetComponentData<ClientTickRate>(entity);
                rate.AlwaysRollbackAllPredictedGhosts = value;
                em.SetComponentData(entity, rate);
            }
        }

        public bool TryFindGhostByInstance(World world, GhostInstance ghostId, out Entity entity)
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SpawnedGhostEntityMap>());
            var map = query.GetSingleton<SpawnedGhostEntityMap>();
            return map.Value.TryGetValue(ghostId, out entity);
        }

        /// <summary>
        /// Generates a fake 32-char hex GUID suitable for assigning to <see cref="GhostAuthoringComponent.prefabId"/>
        /// in tests. The baker rejects any ghost whose prefabId is empty (it's normally populated when the
        /// prefab asset is saved), so in-memory GameObjects need this stamped manually.
        /// </summary>
        public static string NewFakePrefabId() => Guid.NewGuid().ToString().Replace("-", "");
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
                ghost.prefabId = NewFakePrefabId();
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
            Debug.Assert(Profile == null && m_ServerWorlds == null && m_ClientWorlds == null && DriverSimulatedDelay == 0 && DriverSimulatedDrop == 0, "Already setup!");
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

        internal void RemoveWorldFromUpdateList(World world)
        {
            m_WorldStrategy.RemoveWorldFromUpdateList(world);
        }

        #region Editor Utility
#if UNITY_EDITOR && NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        private const string k_ForceSingleWorldHostMenuPath = "Window/Multiplayer/NetCodeTestWorld Force Use Single World Host";
        private const string k_ForceSingleWorldHostPrefKey = "NetCodeTestWorldForceUseSingleWorldHost";

        // Create window menu for NetCodeTestsUseSingleWorldHost
        [UnityEditor.MenuItem(k_ForceSingleWorldHostMenuPath, priority = 3008)] // 3008 is in relation to MultiplayerPlayModeWindow that has 3007
        public static void ToggleUseSingleWorldHost()
        {
            bool value = UnityEditor.EditorPrefs.GetBool(k_ForceSingleWorldHostPrefKey, false);

            value = !value;

            UnityEditor.EditorPrefs.SetBool(k_ForceSingleWorldHostPrefKey, value);
            UnityEditor.Menu.SetChecked(k_ForceSingleWorldHostMenuPath, value);
            AddHostTestsToggle(); // calling this here as well in case you open the window after InitializeOnLoad is called, this way there's still a way to manually add that custom button
        }

        [UnityEditor.InitializeOnLoadMethod]
        public static void SetMenuCheckState()
        {
            // Needs to be a delayCall because the menu system is not initialized at the time of this call, so we need to delay it until the next editor loop
            UnityEditor.EditorApplication.delayCall += () =>
            {
                var value = UnityEditor.EditorPrefs.GetBool(k_ForceSingleWorldHostPrefKey, false);
                UnityEditor.Menu.SetChecked(k_ForceSingleWorldHostMenuPath, value);
                AddHostTestsToggle(); // can't add it directly in InitializeOnLoadMethod, since test window hasn't finished initializing. Calling this now tries to create a new window instead of using the existing one.
            };
        }

        public static void AddHostTestsToggle()
        {
            EditorApplication.delayCall -= AddHostTestsToggle;
            if (Resources.FindObjectsOfTypeAll<TestRunnerWindow>().Length == 0)
                return;
            // window is already opened, so we change it
            var window = TestRunnerWindow.GetWindow(typeof(TestRunnerWindow), utility: false);
            var root = window.rootVisualElement;
            const string k_ToggleName = "NetcodeHostToggle";
            if (root.Q<Button>(k_ToggleName) != null)
                return;
            var enableHostButton = new Button();
            enableHostButton.name = k_ToggleName;

            void SetText()
            {
                enableHostButton.text = $"Toggle host tests [{NetCodeTestWorld.OverrideUseSingleWorldHost}]";
            }
            SetText();
            enableHostButton.clicked += () =>
            {
                ToggleUseSingleWorldHost();
                SetText();
            };
            enableHostButton.style.width = 150;
            root.Add(enableHostButton);
        }
#endif
        #endregion

        #region GameObject Playmode Tests

        /// <summary>
        ///  Call the following to setup GameObject tests
        /// <code>
        /// await using var TestWorld = new NetCodeTestWorld();
        /// await TestWorld.SetupGameObjectTest();
        /// </code>
        /// </summary>
        /// <returns></returns>
        public async Task SetupGameObjectTest(bool useNormalMainLoop = true, int serverCount = 1, int clientCount = 1, int singleWorldHostCount = 0, bool cleanGeneratedDirectory = true, bool keepExistingNetcodeInstance = false, bool enableConnectionEventWarning = false,
            params Type[] userSystems)
        {
            try
            {
                // Any playmode test will trigger IBootstrap's Initialize which already creates worlds we don't have control over
                // This test needs to run in a project with no custom bootstrapper for the below cleanup to still be valid
                if (ClientServerBootstrap.HasServerWorld)
                {
                    ClientServerBootstrap.ServerWorld.Dispose();
                }

                if (ClientServerBootstrap.HasClientWorlds)
                {
                    ClientServerBootstrap.ClientWorld.Dispose();
                }

                if (cleanGeneratedDirectory)
                    CleanGeneratedDirectory();

                // A previous test may have timed out before DisposeAsync could await the scene unload.
                // Unload the stale scene now so CreateScene doesn't throw "already exists".
                var staleScene = SceneManager.GetSceneByName(k_TestSceneName);
                if (staleScene.IsValid() && staleScene.isLoaded)
                {
                    await SceneManager.UnloadSceneAsync(staleScene);
                }

                m_EmptyScene = SceneManager.CreateScene(k_TestSceneName, new CreateSceneParameters() { localPhysicsMode = LocalPhysicsMode.Physics3D });
                SceneManager.SetActiveScene(m_EmptyScene);

                SetupTestNetcodeAPI(keepExistingNetcodeInstance); // Needs to run again since we might have disposed the above worlds, changing the various cached queries

                Bootstrap(NetCodeTestWorld.SystemResolutionMode.NetcodeAndUserSystems, includePresentationSystemsOnClient: false, useNormalMainLoop: useNormalMainLoop, userSystems: userSystems);
                if (serverCount > 0 || clientCount > 0 || singleWorldHostCount > 0)
                {
                    // Warning: multiple ticks can be called between test setup and the test execution. Test shouldn't rely on the exact number of ticks
                    await CreateWorldsAsync(serverCount > 0 ? true : false, clientCount, numHostWorlds: singleWorldHostCount, tickWorldAfterCreation: true); // Netcode default bootstrapping creates a server world and a client world, reproducing this here
                }
            }
            catch
            {
                // we don't want to leave the test in an in-between state, so still trying to clean things up to not fail subsequent tests
                try
                {
                    await DisposeAsync();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                finally
                {
                    Debug.LogError("Error trying to setup test, there might have been previous tests that didn't cleanup properly. This can happen when a timeout occurs for example.");
                }

                throw;
            }
        }

        // Pre-build setup from IPrebuildSetup interface for multiplayer role, making sure the role is the right one for tests
        public virtual void Setup()
        {
            if (!Application.isEditor) Assert.IsTrue(false, "Sanity check failed, we shouldn't be in a pre-build setup inside a build, this should be editor code only");
#if UNITY_EDITOR
            // Most tests assume no stripping. To create tests with stripped behaviour, you can override this behaviour in your test class
            MultiplayerPlayModePreferences.RequestedPlayType = ClientServerBootstrap.PlayType.ClientAndServer;
#endif
            // we want to make sure there's no rogue config in our tests, so we add a temporary global one
            // NetcodeConfig automatically takes the first config available, so setting one for it to take
            m_TempBootstrapConfig = ScriptableObject.CreateInstance<NetCodeConfig>();
#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8); tests still set it to control deterministic resolution.
            foreach (var netCodeConfig in Resources.FindObjectsOfTypeAll<NetCodeConfig>())
            {
                if (netCodeConfig.IsGlobalConfig)
                {
                    netCodeConfig.IsGlobalConfig = false;
                    m_OldGlobalConfig = netCodeConfig;
                    break;
                }
            }
            NetCodeConfig.Global = m_TempBootstrapConfig;
            m_TempBootstrapConfig.IsGlobalConfig = true;
#pragma warning restore 618
        }

        public virtual void Cleanup()
        {
            Object.DestroyImmediate(m_TempBootstrapConfig);
#pragma warning disable 618 // IsGlobalConfig is obsolete (RemovedAfter 6.8); tests still set it to control deterministic resolution.
            if (m_OldGlobalConfig != null) m_OldGlobalConfig.IsGlobalConfig = true;
#pragma warning restore 618
        }

        public async ValueTask DisposeAsync()
        {
            PredictionCallbackHelper.Reset();
            foreach (var predictionCallbackHelper in GameObject.FindObjectsByType<PredictionCallbackHelper>(FindObjectsInactive.Include))
            {
                predictionCallbackHelper.Dispose();
            }

            Dispose();
            CleanGeneratedDirectory();
            var emptyScene = SceneManager.GetSceneByName(k_TestSceneName);
            if (emptyScene.IsValid() && emptyScene.isLoaded)
            {
                await UnloadSceneAsync(emptyScene);
            }

            // This needs to live here to not clash with other world destruction above and sanity check below
            foreach (var scene in m_AdditionalScenesToCleanup)
            {
                await SceneManager.UnloadSceneAsync(scene);
            }


            var foundObjects = GameObject.FindObjectsByType<GhostObject>(FindObjectsInactive.Include);
            for (int i = 0; i < foundObjects.Length; i++)
            {
                var foundGhost = foundObjects[i];
                GameObject.Destroy(foundGhost.gameObject);
            }

            // This means some cleanup logic didn't happen as expected
            List<GhostObject> invalidFoundObjects = new();
            foreach (var o in foundObjects)
            {
                if (!o.IsPrefab())
                    invalidFoundObjects.Add(o);
            }

            // if the fake prefab is created in a scene, then it's also destroyed when that scene is unloaded, however the generated SO won't. So we need
            // to do a pass here to clean those up.
            var foundSOs = ScriptableObject.FindObjectsByType<GhostPrefabReference>(FindObjectsInactive.Include);
            foreach (var foundSO in foundSOs)
            {
                ScriptableObject.DestroyImmediate(foundSO);
            }
            Assert.That(invalidFoundObjects, Is.Empty, "cleanup logic didn't happen as expected please review this");
        }

        async Task UnloadSceneAsync(Scene scene)
        {
            await SceneManager.UnloadSceneAsync(scene);
        }

        void CleanGeneratedDirectory()
        {
#if UNITY_EDITOR
            if (Directory.Exists(k_GeneratedFolderBasePath))
            {
                // removes meta file too
                AssetDatabase.DeleteAsset(k_GeneratedFolderBasePath); // make sure to reset the generated folder for each test
                AssetDatabase.Refresh(); // we want calls to Resources.FindObjectsOfTypeAll to return the right set of objects, so need to refresh now
            }
#endif
        }

        #endregion
    }
}
