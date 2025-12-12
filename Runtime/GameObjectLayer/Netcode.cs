
using System;
using Unity.Burst;
using Unity.Entities;
#if UNITY_EDITOR
    using UnityEditor;
#endif
using UnityEngine;
namespace Unity.NetCode
{
    /// <summary>
    /// Main point of access to Netcode APIs. All global calls and configuration should be available from here.
    /// When writing gameplay code, this is the main class you should remember when dealing with Netcode.
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

#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        internal static ref GhostEntityMapping EntityMappingRef => ref s_EntityMapping.Data;
        // Needs to be unique to mimic entities integration. if we want to iterate over all entities in a world, we need a separate data structure tracking those
        private class EntityMapFieldKey { }
        private static readonly SharedStatic<GhostEntityMapping> s_EntityMapping = SharedStatic<GhostEntityMapping>.GetOrCreate<Netcode, EntityMapFieldKey>();
#endif
        #region singleton

        // The only static state in Netcode should be instance
        protected internal static Netcode Instance
        {
            get
            {
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
            // These need to happen outside the constructor, since some of these's initialization depends on the instance being set already
            // for example Client() accesses Instance.m_WorldManager and so Instance needs to be set already
            // m_WorldManager = new NetcodeWorldManager();
            m_Client = new Client();
            m_Server = new Server();
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            m_ClientPrefabsRegistry = new PrefabsRegistry() {m_IsClient = true};
            m_ServerPrefabsRegistry = new PrefabsRegistry() {m_IsClient = false};
            // ClientServerBootstrap.CustomDriverConstructors = default;
            // GhostBehaviourTypeManager = new GhostBehaviourTypeManager();
            // BootstrapSceneOverrideManager = new BootstrapSceneOverrideManager();
            EntityMappingRef = new GhostEntityMapping(true);
#endif

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

        public void Dispose()
        {
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
            EntityMappingRef.Dispose();
#endif
        }

        #endregion // singleton

#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
        /// <summary>
        /// Registers a prefab with the client and server. This is a convenience method for <see cref="PrefabsRegistry.RegisterPrefab"/>.
        /// </summary>
        /// <param name="prefab"></param>
        public static void RegisterPrefab(UnityEngine.GameObject prefab, World forWorld = null)
        {
            // TODO-release this flow shouldn't be needed once we have UDM and/or auto prefab registration
            if (forWorld == null || forWorld.IsServer())
                Instance.m_ServerPrefabsRegistry.RegisterPrefab(prefab);
            if (forWorld == null || forWorld.IsClient())
                Instance.m_ClientPrefabsRegistry.RegisterPrefab(prefab);
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
}
