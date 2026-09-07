using System;
using System.Collections.Generic;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Netcode.NetcodeTime;
using Unity.Networking.Transport;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using static Unity.Netcode.NetcodeConfig.HostWorldMode;


namespace Unity.Netcode
{
    /// <summary>
    /// Main point of access to Netcode APIs. All global calls and configuration should be available from here.
    /// When writing gameplay code, this is the main class you should remember when dealing with Netcode.
    /// For an unmanaged version that Burst compatible, please use <see cref="Netcode.Unmanaged"/>.
    /// </summary>
    // Design note: Similar with Physics.Raycast() and other such APIs.
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    class Netcode : IDisposable
    {
        // todo-release bunch of dereferencing here, should investigate later with perf tests
        /// <summary>
        /// Returns true if the game has a client role and is connected
        /// IsClientRole and IsServerRole can both be true at the same time.
        /// </summary>
        public static bool IsClientRole
        {
            get
            {
                var activeWorld = ClientServerBootstrap.ClientWorld;
                return activeWorld.ExistsAndIsCreated() && activeWorld.IsClient() && activeWorld.LocalConnection.GetConnectionState() >= ConnectionState.State.Connecting;
            }
        }

        /// <summary>
        /// Returns true if the game has a server role and is listening for connections
        /// IsClientRole and IsServerRole can both be true at the same time.
        /// </summary>
        public static bool IsServerRole
        {
            get
            {
                var activeWorld = ClientServerBootstrap.ServerWorld;
                return activeWorld.ExistsAndIsCreated() && activeWorld.IsServer() && activeWorld.Listening();
            }
        }

        /// <summary>
        /// Returns true if the game has both a client and server role at the same time.
        /// If you're  a client in a standalone build, you could have IsHostRole == false and IsClientServerBuild == true.
        /// You can have a client role, still in a host or client/server build.
        /// </summary>
        public static bool IsHostRole => IsClientRole && IsServerRole && ClientServerBootstrap.ServerWorld.IsHost();
        /// <summary>
        /// Whether there's any online functionality available
        /// </summary>
        public static bool IsActive => Instance != null && (IsClientRole || IsServerRole);

        /// <summary>
        /// Returns true if the build is a dedicated server build, with client content stripped
        /// </summary>
        public static bool IsServerBuildOnly => ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Server; // TODO-release integrate with content selection/build target? and test
        /// <summary>
        /// Returns true if the build is a client build, with server content stripped
        /// </summary>
        public static bool IsClientBuildOnly => ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Client;
        /// <summary>
        /// Returns true if the build is a standalone build, with all content available
        /// This is similar to what a default singleplayer build would be.
        /// </summary>
        public static bool IsClientServerBuild => ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.ClientAndServer;
        bool m_Initialized;

        internal OfflineCache m_OfflineCache;

        internal GhostBehaviourTypeManager GhostBehaviourTypeManager;

        /// <summary>
        /// Access the Remotes API
        /// </summary>
        public static RemoteHandler Remote;

        // GhostEntityMapping m_EntityMapping; // Needs to be unique to mimic entities integration. if we want to iterate over all entities in a world, we need a separate data structure tracking those

        internal const string kTempBuildFolder = "netcode-build-assets-temp";

        internal NetcodeWorld m_ActiveWorld;

        public static NetworkTime Time
        {
            get
            {
                Assert.IsTrue(Instance.m_ActiveWorld.ExistsAndIsCreated(), "Should only be called when netcode is initialized.");
                return Instance.m_ActiveWorld.NetworkTime;
            }
        }

        public static float DeltaTime
        {
            get
            {
                Assert.IsTrue(Instance.m_ActiveWorld.ExistsAndIsCreated(), "Should only be called when netcode is initialized.");
                return Instance.m_ActiveWorld.DeltaTime;
            }
        }

        #region singleton

        // The only static state in Netcode should be instance
        protected internal static Netcode Instance
        {
            get
            {
                // From Unity's doc: Important: There’s no protection against accessing non-readonly or mutable
                // static data from within a job. Accessing this kind of data circumvents all safety systems and might crash your application or the Unity Editor.
                // So some best practice around thread safety and static access: either don't have static fields or they have threadsafe ones (e.g. typemanager is mostly
                // threadsafe, because it's mostly readonly). Calling "IsExecutingJob" helps prevent these kinds of invisible issues for users (else there's zero errors and it can become race conditions galore).
                if (JobsUtility.IsExecutingJob) throw new Exception("Static access while in a job is unsafe and unsupported by Unity's job system. Please save the instance you're trying to access as a field inside the job to take full advantage of the jobs safety system. e.g. new MyJob(){ myField = Netcode.Something; }");

                // TODO-release investigate if we can just use RuntimeInitializationOnLoad instead of lazy initialization.
                if (s_Instance == null)
                {
                    s_Instance = new Netcode();
                    s_Instance.Initialize();
                }

                return s_Instance;
            }
        }

        protected internal static Netcode s_Instance;

        /// <summary>
        /// Gives access to parts of the APIs that are unmanaged. This is useful when your code needs to run
        /// with Burst.
        /// </summary>
        public static ref NetcodeUnmanaged Unmanaged
        {
            get
            {
                // See Netcode.Instance for some best practices around statics and thread safety
                if (JobsUtility.IsExecutingJob) throw new Exception("Static access while in a job is unsafe and unsupported by Unity's job system. Please save the instance you're trying to access as a field inside the job to take full advantage of the jobs safety system. e.g. new MyJob(){ myField = Netcode.Unmanaged.Something; }");

                if (!s_UnmanagedInstance.Data.Initialized)
                    s_UnmanagedInstance.Data.TryInitialize();
                return ref s_UnmanagedInstance.Data;
            }
        }

        // Using NetcodeUnmanaged as the key as well. There should be very little reason to add another SharedStatic for NetcodeUnmanaged in this class. The design goal is for it to be unique at all times.
        private static readonly SharedStatic<NetcodeUnmanaged> s_UnmanagedInstance = SharedStatic<NetcodeUnmanaged>.GetOrCreate<NetcodeUnmanaged, NetcodeUnmanaged>();

#if UNITY_EDITOR
        // entities bootstrapping happens in BeforeSceneLoad, we need Netcode to be initialized before that
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void SetupStaticCallbacks()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeAfterEnterEditMode;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAfterEnterEditMode; // need to call this to dispose native allocations when we still know about them
            EditorApplication.playModeStateChanged -= EditorApplicationOnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += EditorApplicationOnPlayModeStateChanged; // This needs to happen after playmode world are destroyed, so we can still access Netcode APIs in OnDestroy

            if (EditorAnalytics.enabled)
            {
                var analytics = new Analytics.GameObjectBridgeData
                {
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
                    GameObjectsUsed = true,
#endif
                    SingleWorldHostUsed = NetcodeConfig.Global.HostWorldModeSelection == NetcodeConfig.HostWorldMode.SingleWorld,
                };
                Analytics.NetCodeAnalytics.SendAnalytic(new Analytics.GameObjectBridgeAnalytic(analytics));
            }
        }

        static void EditorApplicationOnPlayModeStateChanged(PlayModeStateChange obj)
        {
            if (obj == PlayModeStateChange.EnteredEditMode)
                DisposeAfterEnterEditMode();
        }

        //         [AfterEnteringEditMode]
#endif
        internal static void DisposeAfterEnterEditMode()
        {
            if (s_Instance != null)
                s_Instance.Dispose();
            s_Instance = null;
        }

        internal static void Reset()
        {
            if (s_Instance != null)
                s_Instance.Dispose();
            s_Instance = new Netcode();
            s_Instance.Initialize();
        }

        protected internal Netcode()
        {
            // Users can't instantiate this. Needs to be there for TestNetcode override in our tests (need a default constructor)
        }

        internal void Initialize()
        {
            if (m_Initialized)
                return;
            m_Initialized = true;
            // These need to happen outside the constructor, since some of these's initialization depends on the instance being set already
            // for example Client() accesses Instance.m_WorldManager and so Instance needs to be set already
            // m_WorldManager = new NetcodeWorldManager();
            this.m_OfflineCache = new();
            // ClientServerBootstrap.CustomDriverConstructors = default;
            GhostBehaviourTypeManager = new GhostBehaviourTypeManager();
            // BootstrapSceneOverrideManager = new BootstrapSceneOverrideManager();
            s_UnmanagedInstance.Data.TryInitialize();

            Remote = new RemoteHandler();

            InitializeWithAssets(); // TODO-release for now should be ok to call from here, but once entities changes bootstrap ordering, we'll need to move this to a new "after assets have loaded" place
            // for now bootstrapping happens here: BeforeSceneLoad (from https://docs.unity3d.com/ScriptReference/RuntimeInitializeOnLoadMethodAttribute.html)
        }

        internal void InitializeWithAssets()
        {
            // Some initialization logic relies on resources being already loaded. Since Initialize can be called any time, we need to make sure to isolate those specific initialization steps else where.
            // m_Config = NetCodeConfig.RuntimeTryFindSettings();
            // if (m_Config == null) m_Config = NetCodeConfig.CreateNewGlobalInstance();
            GhostBehaviourTypeManager.InitializeGhostBehaviourInfos();

            // BootstrapSceneOverrideManager.InitializeWithAssets();
        }


        internal static bool IsInitialized => s_Instance != null && s_Instance.m_Initialized;

        public void Dispose()
        {
            m_Initialized = false;
            s_UnmanagedInstance.Data.Dispose();
        }

        /// <summary>
        /// Updates the Netcode.m_ActiveWorld to the next potential world that's not the currently provided system. Useful for when
        /// a world gets destroyed and we want to set the m_ActiveWorld to the next most likely successor.
        /// </summary>
        /// <param name="currentWorld"></param>
        internal static void SetSuccessorActiveWorld(NetcodeWorld currentWorld)
        {
            foreach (var potentialActiveWorld in Netcode.GetNetcodeWorlds())
            {
                if (potentialActiveWorld != currentWorld)
                {
                    Instance.m_ActiveWorld = potentialActiveWorld;
                    return;
                }
            }

            Instance.m_ActiveWorld = null;
        }
        #endregion // singleton


        #region world management

        // Internal design note: So far there's little state that needs to be cached. If we see there's more and more, we could potentially have a shared interface between OfflineCache and NetcodeWorld to make sure at compile time we're not forgetting anything.
        internal class OfflineCache
        {
            /// <summary>
            /// A place to keep track of prefabs registrations for worlds which have not yet been created (like
            /// when no world exists yet), new worlds will process this list and register entity prefabs for each one.
            /// </summary>
            internal List<GameObject> m_PrefabPlaceholder = new();

            public event OnConnectionEventDelegate OnConnectionEvent;

            public void InitializeWorld(NetcodeWorld world)
            {
                PrefabsRegistry.RegisterPrefabBatch(m_PrefabPlaceholder, world);
                world.OnConnectionEvent += OnConnectionEvent;
            }
        }

        // TODO-next@breakingChange move this to NetcodeWorldManager like in neutron?
        // These seem trivial but they are overriden in NetcodeTestWorld so that they can be cleaned up automatically in tests
        internal virtual NetcodeWorld CreateServerWorld()
        {
            return ClientServerBootstrap.CreateServerWorld("Server");
        }
        internal virtual NetcodeWorld CreateClientWorld()
        {
            return ClientServerBootstrap.CreateClientWorld("Client");
        }
        internal virtual NetcodeWorld CreateSingleWorldHost()
        {
            return ClientServerBootstrap.CreateSingleWorldHost("Host");
        }


        internal static IEnumerable<NetcodeWorld> GetNetcodeWorlds()
        {
            // in order priority get a series of potential worlds to be the default "active world"
            foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
            {
                yield return serverWorld;
            }

            foreach (var clientWorld in ClientServerBootstrap.ClientWorlds)
            {
                if (clientWorld.IsHost()) continue; // we've already covered this world in the first loop
                yield return clientWorld;
            }

            foreach (var thinClientWorld in ClientServerBootstrap.ThinClientWorlds)
            {
                yield return thinClientWorld;
            }
        }

        #endregion

        #region Prefabs

        /// <inheritdoc cref="PrefabsRegistry.RegisterPrefab" />
        public static void RegisterPrefab(GameObject prefab, NetcodeWorld forWorld)
        {
            PrefabsRegistry.RegisterPrefab(prefab, forWorld);
        }

        /// <summary>
        /// Registers a prefab with all netcode worlds and also queue for later registration for
        /// worlds which are created later.
        /// </summary>
        /// <param name="prefab">GameObject prefab to register</param>
        public static void RegisterPrefab(GameObject prefab)
        {
            // TODO-release@prefabRegistration this flow shouldn't be needed once we have UDM and/or auto prefab registration. It'd still be needed for Addressables support
            foreach (var world in GetNetcodeWorlds())
            {
                PrefabsRegistry.RegisterPrefab(prefab, world);
            }
            // Always add to placeholder list, any world created later will then also register this prefab
            if (Instance.m_OfflineCache.m_PrefabPlaceholder.Contains(prefab))
                return;
            Instance.m_OfflineCache.m_PrefabPlaceholder.Add(prefab);
        }
        #endregion

        #region connectivity

        #region callbacks

        // Design note: If we see the "cache and apply" pattern is adding boilerplate, we could create ourselves an internal interface for both the cache and NetcodeWorld and just iterate over all (both cache and netcode world) in the same way.

        public static event OnConnectionEventDelegate OnConnectionEvent
        {
            add
            {
                Instance.m_OfflineCache.OnConnectionEvent += value;
                if (!Instance.m_ActiveWorld.ExistsAndIsCreated())
                    return;

                foreach (var world in GetNetcodeWorlds())
                {
                    world.OnConnectionEvent += value;
                }
            }
            remove
            {
                Instance.m_OfflineCache.OnConnectionEvent -= value;
                if (!Instance.m_ActiveWorld.ExistsAndIsCreated())
                    return;

                foreach (var world in GetNetcodeWorlds())
                {
                    world.OnConnectionEvent -= value;
                }
            }
        }

        #endregion

        public static Connection LocalConnection
        {
            get
            {
                var netWorld = ClientServerBootstrap.ClientWorld;
                if (!netWorld.ExistsAndIsCreated())
                    return default;
                return netWorld.LocalConnection;
            }
        }

        static readonly List<Connection> m_EmptyList = new(); // instead of reallocating an empty list all the time, allocating once
        public static List<Connection> AllConnections
        {
            get
            {
                if (!Instance.m_ActiveWorld.ExistsAndIsCreated())
                    return m_EmptyList;
                return Instance.m_ActiveWorld.AllConnections;
            }
        }


        // TODO-release@breakingChange rework this when we split driver lifecycle from worlds. This is required right now because drivers are automatically listening/connecting on world creation if auto connect port is set
        // TODO-next@breakingChange remove AutoConnectPort
        class TemporarilyDisableAutoConnect : IDisposable
        {
            ushort m_OldPort;

            public TemporarilyDisableAutoConnect()
            {
                m_OldPort = ClientServerBootstrap.AutoConnectPort;
                ClientServerBootstrap.AutoConnectPort = 0;
            }
            public void Dispose()
            {
                ClientServerBootstrap.AutoConnectPort = m_OldPort;
            }
        }

        /// <summary>
        /// Connect to specified server/host endpoint. Will create appropriate client <see cref="NetcodeWorld"/>.
        /// You can specify the endpoint like this
        /// <code>
        /// NetworkEndpoint.LoopbackIpv4.WithPort(8888); // will connect to 127.0.0.1:8888 (local connection)
        /// NetworkEndpoint.Parse("123.123.123.123", 1234); // will connect to 123.123.123.123:1234
        /// </code>
        /// To specify connection timeouts, see <see cref="NetcodeConfig.MaxConnectAttempts"/> and <see cref="NetcodeConfig.ConnectTimeoutMS"/>
        /// </summary>
        /// <remarks>
        /// You can override driver creation using <see cref="INetworkStreamDriverConstructor"/> and assign it to <see cref="NetworkStreamReceiveSystem.DriverConstructor"/>.
        /// </remarks>
        /// <param name="endpoint"></param>
        /// <returns></returns>
        public static Connection Connect(NetworkEndpoint endpoint)
        {
            // We don't override the driver constructor to let users override that themselves. We just use whatever is there.
            using var a = new TemporarilyDisableAutoConnect();
            var world = ClientServerBootstrap.ClientWorld;
            if (!world.ExistsAndIsCreated())
            {
                world = Instance.CreateClientWorld();
            }
            return world.Connect(endpoint);
        }

        /// <summary>
        /// Starts listening for connections. Will create the appropriate <see cref="NetcodeWorld"/> in the background.
        /// Accepts a <see cref="NetworkEndpoint"/> which can be created like this
        /// <code>
        /// NetworkEndpoint.AnyIpv4.WithPort(8888); // will listen on 0.0.0.0:8888
        /// NetworkEndpoint.Parse("123.123.123.123", 1234); // will listen on 123.123.123.123:1234
        /// </code>
        /// </summary>
        /// <remarks>
        /// You can override driver creation using <see cref="INetworkStreamDriverConstructor"/> and assign it to <see cref="NetworkStreamReceiveSystem.DriverConstructor"/>.
        /// </remarks>
        /// <param name="endpoint"></param>
        /// <returns>true if successfully listen on specified endpoint. Failure can happen if the port is already used for example.</returns>
        public static bool Listen(NetworkEndpoint endpoint)
        {
            using var a = new TemporarilyDisableAutoConnect();
            var world = ClientServerBootstrap.ServerWorld;
            if (!world.ExistsAndIsCreated())
            {
                world = Instance.CreateServerWorld();
            }
            return world.Listen(endpoint);
        }

        /// <summary>
        /// Starts a server for client hosting. This will start listening for connections while also having a local client.
        /// The default is a single world, but this can be configured to use a <see cref="NetcodeConfig.HostWorldMode.BinaryWorlds"/> setup.
        /// Accepts a <see cref="NetworkEndpoint"/> which can be created like this
        /// <code>
        /// NetworkEndpoint.AnyIpv4.WithPort(8888); // will listen on 0.0.0.0:8888
        /// NetworkEndpoint.Parse("123.123.123.123", 1234); // will listen on 123.123.123.123:1234
        /// </code>
        /// </summary>
        /// <param name="endpoint">Endpoint to listen on.</param>
        /// <param name="hostWorldMode">Creates a single world when in <see cref="NetcodeConfig.HostWorldMode.SingleWorld"/> mode.</param>
        /// <remarks>
        /// You can override driver creation using <see cref="INetworkStreamDriverConstructor"/> and assign it to <see cref="NetworkStreamReceiveSystem.DriverConstructor"/>.
        /// </remarks>
        /// <returns>true if successfully listen on specified endpoint. Failure can happen if the port is already used for example.</returns>
        public static Connection StartAsHost(NetworkEndpoint endpoint, NetcodeConfig.HostWorldMode hostWorldMode = SingleWorld)
        {
            using var a = new TemporarilyDisableAutoConnect();
            var serverWorld = ClientServerBootstrap.ServerWorld;
            if (!serverWorld.ExistsAndIsCreated())
            {
                if (hostWorldMode == SingleWorld)
                {
                    // A host world is a server world with some client systems running basically. No need for client driver init or anything like that.
                    serverWorld = Instance.CreateSingleWorldHost();
                }
                else
                {
                    serverWorld = Instance.CreateServerWorld();
                }
            }

            if (!serverWorld.Listen(endpoint))
            {
                return default;
            }
            if (hostWorldMode == SingleWorld)
            {
                return serverWorld.LocalConnection;
            }

            var clientWorld = ClientServerBootstrap.ClientWorld;
            if (!clientWorld.ExistsAndIsCreated())
            {
                clientWorld = Instance.CreateClientWorld();
            }

            Connection result = clientWorld.Connect(serverWorld.GetIPCEndpoint());

            return result;
        }

        /// <summary>
        /// Goes through all netcode worlds and shuts them down. <see cref="NetcodeWorld.Shutdown"/>.
        /// Optionally allows keeping the underlying netcode world for reuse. In this case, expect connections to be shutdown asynchronously in the next frame.
        /// Note that trying to reuse a world for a different role isn't allowed. For example keeping a client world to serve as a host world.
        /// This is because this world contains filtered client systems only and would need to be recreated with additional server systems to work as expected.
        /// </summary>
        /// <param name="disposeWorld">By default, will dispose all the worlds, destroying the associated ghosts with them.</param>
        public static void Shutdown(bool disposeWorld = true)
        {
            for (int i = World.All.Count - 1; i >= 0; i--)
            {
                if (World.All[i] is not NetcodeWorld world) continue; // World.All contains a bunch of non netcode worlds, like baking worlds and default world

                if (disposeWorld)
                {
                    world.Dispose();
                }
                else
                {
                    world.Shutdown();
                }
            }
        }

        /// <summary>
        /// Checks if this server is listening for connections.
        /// </summary>
        /// <returns></returns>
        public static bool Listening()
        {
            var netWorld = ClientServerBootstrap.ServerWorld;
            if (!netWorld.ExistsAndIsCreated())
                return false;
            return netWorld.Listening();
        }

        /// <summary>
        /// Request a disconnect from the server for this client. This is done asynchronously and won't be disconnected after this call.
        /// </summary>
        public static void RequestDisconnectFromServer()
        {
            var netWorld = ClientServerBootstrap.ClientWorld;
            netWorld.AssertIsClientOnly();
            netWorld.RequestDisconnectFromServer();
        }

        /// <summary>
        /// Request a disconnect on all client connections from this server. This is done asynchronously. Clients won't be disconnected after this call.
        /// </summary>
        public static void RequestDisconnectAllClients()
        {
            var netWorld = ClientServerBootstrap.ServerWorld;
            netWorld.AssertIsServer();
            netWorld.RequestDisconnectAllClients();
        }

        #endregion
    }

    /// <summary>
    /// Unmanaged version of the Netcode API. Similar to World.Unmanaged, in that it offers a subset of the features in <see cref="Netcode"/> that's burst compatible
    /// </summary>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct NetcodeUnmanaged
    {
        internal bool Initialized;

        internal void TryInitialize()
        {
            if (Initialized) return;

            Initialized = true;
            m_EntityMapping = GhostEntityMapping.Create();
        }

        internal void Dispose()
        {
            m_EntityMapping.Dispose();
            Initialized = false;
        }

        internal GhostEntityMapping m_EntityMapping;
        // Needs to be unique to mimic entities integration. if we want to iterate over all entities in a world, we need a separate data structure tracking those
    }
}
