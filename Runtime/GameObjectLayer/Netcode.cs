using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
namespace Unity.NetCode
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
        public static bool IsClientRole => Client.HasServerConnection();
        /// <summary>
        /// Returns true if the game has a server role and is listening for connections
        /// IsClientRole and IsServerRole can both be true at the same time.
        /// </summary>
        public static bool IsServerRole => Server.Listening();
        /// <summary>
        /// Returns true if the game has both a client and server role at the same time.
        /// If you're  a client in a standalone build, you could have IsHostRole == false and IsClientServerBuild == true.
        /// You can have a client role, still in a host or client/server build.
        /// </summary>
        public static bool IsHostRole => IsClientRole && IsServerRole;
        /// <summary>
        /// Whether there's any online functionality
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
        Client m_Client;
        Server m_Server;
        bool m_Initialized;

        /// <summary>
        /// A place to keep track of prefabs registrations for worlds which have not yet been created (like
        /// when no world exists yet), new worlds will process this list and register entity prefabs for each one.
        /// </summary>
        List<GameObject> m_PrefabPlaceholder;

#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        PrefabsRegistry m_ServerPrefabsRegistry;
        PrefabsRegistry m_ClientPrefabsRegistry;
#endif
        // GhostEntityMapping m_EntityMapping; // Needs to be unique to mimic entities integration. if we want to iterate over all entities in a world, we need a separate data structure tracking those

        /// <summary>
        /// Default client access.
        /// </summary>
        public static Client Client => Instance.m_Client;

        /// <summary>
        /// Default server access.
        /// </summary>
        public static Server Server => Instance.m_Server;


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
            m_Client = new Client();
            m_Server = new Server();
            m_PrefabPlaceholder = new List<GameObject>();
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            // ClientServerBootstrap.CustomDriverConstructors = default;
            // GhostBehaviourTypeManager = new GhostBehaviourTypeManager();
            // BootstrapSceneOverrideManager = new BootstrapSceneOverrideManager();
#endif
            s_UnmanagedInstance.Data.TryInitialize();

            InitializeWithAssets(); // TODO-release for now should be ok to call from here, but once entities changes bootstrap ordering, we'll need to move this to a new "after assets have loaded" place
            // for now bootstrapping happens here: BeforeSceneLoad (from https://docs.unity3d.com/ScriptReference/RuntimeInitializeOnLoadMethodAttribute.html)
        }

        internal void InitializeWithAssets()
        {
            // Some initialization logic relies on resources being already loaded. Since Initialize can be called any time, we need to make sure to isolate those specific initialization steps else where.
            // m_Config = NetCodeConfig.RuntimeTryFindSettings();
            // if (m_Config == null) m_Config = NetCodeConfig.CreateNewGlobalInstance();
            // GhostBehaviourTypeManager.InitializeGhostBehaviourInfos();
            // BootstrapSceneOverrideManager.InitializeWithAssets();
        }

        internal void InitializePlaceholderPrefabs(World forWorld)
        {
            if (m_PrefabPlaceholder == null)
                return;
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            foreach (var prefab in m_PrefabPlaceholder)
                PrefabsRegistry.RegisterPrefab(prefab, forWorld);
#endif
        }

        internal static bool IsInitialized => s_Instance != null && s_Instance.m_Initialized;

        public void Dispose()
        {
            m_Initialized = false;
            s_UnmanagedInstance.Data.Dispose();
        }

        #endregion // singleton

#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        /// <inheritdoc cref="PrefabsRegistry.RegisterPrefab" />
        public static void RegisterPrefab(GameObject prefab, World forWorld)
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
            // TODO-release this flow shouldn't be needed once we have UDM and/or auto prefab registration
            foreach (var world in World.All)
            {
                if (world.IsClient() || world.IsServer())
                    PrefabsRegistry.RegisterPrefab(prefab, world);
            }
            // Always add to placeholder list, any world created later will then also register this prefab
            if (Instance.m_PrefabPlaceholder.Contains(prefab))
                return;
            Instance.m_PrefabPlaceholder.Add(prefab);
        }
#endif

        /// <summary>
        /// Callback when server has started. Once the callback is called, it's consumed and so needs to be re-registered if needed again.
        /// </summary>
        public delegate void OnServerStartedDelegate();
        internal OnServerStartedDelegate OnServerStarted;

        /// <summary>
        /// Runs an action if the server is started or queues the action to be executed later if not started yet. The action is then consumed and so needs to be re-registered if needed again.
        /// </summary>
        /// <param name="action"></param>
        public static void RunOnServerStarted(OnServerStartedDelegate action)
        {
            if (IsServerRole) action();
            else Instance.OnServerStarted += action;
        }
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
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            m_EntityMapping = new GhostEntityMapping(true);
#endif
        }

        internal void Dispose()
        {
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            m_EntityMapping.Dispose();
#endif
            Initialized = false;
        }

#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        internal GhostEntityMapping m_EntityMapping;
        // Needs to be unique to mimic entities integration. if we want to iterate over all entities in a world, we need a separate data structure tracking those
#endif
    }
}
