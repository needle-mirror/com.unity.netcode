using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.NetCode
{
    /// <summary>
    /// ClientServerBootstrap is responsible for configuring and creating the server and client worlds at runtime when
    /// the game starts (or when entering Play Mode in the Editor).
    /// ClientServerBootstrap is intended as a base class for your own custom bootstrap code and provides utility methods
    /// for creating the client and server worlds.
    /// It also supports connecting the client to the server automatically, using the <see cref="AutoConnectPort"/> port and
    /// <see cref="DefaultConnectAddress"/>.
    /// For the server, ClientServerBootstrap allows binding the server transport to a specific listening port and address (especially useful
    /// when running the server on cloud providers) via <see cref="DefaultListenAddress"/>.
    /// </summary>
    /// <remarks>
    /// We strongly recommend setting `Application.runInBackground = true;` (or project-wide via Project Settings) once you intend to connect to the server (or accept connections on said server).
    /// If you don't, your multiplayer game will stall (and likely disconnect) if and when the application loses focus (such as by the player tabbing out), as netcode will be unable to tick (due to the application pausing).
    /// In fact, a Dedicated Server Build should probably always have `Run in Background` enabled.
    /// We provide suppressible error warnings for this case via `WarnAboutApplicationRunInBackground`.
    /// </remarks>
    [UnityEngine.Scripting.Preserve]
    public class ClientServerBootstrap : ICustomBootstrap
    {
        /// <summary>
        /// The maximum number of thin clients that can be created in the Editor.
        /// Created to avoid self-inflicted long editor hangs,
        /// although removed as users should be able to test large player counts (e.g. for UTP reasons).
        /// </summary>
        public const int k_MaxNumThinClients = 1000;

        /// <summary>
        /// A reference to the server world, assigned during the default server world creation. If there
        /// were multiple worlds created this will be the first one.
        /// </summary>
        public static World ServerWorld => ServerWorlds != null && ServerWorlds.Count > 0 && ServerWorlds[0].IsCreated ? ServerWorlds[0] : null;

        /// <summary>
        /// A reference to the client world, assigned during the default client world creation. If there
        /// were multiple worlds created this will be the first one.
        /// </summary>
        public static World ClientWorld => ClientWorlds != null && ClientWorlds.Count > 0 && ClientWorlds[0].IsCreated ? ClientWorlds[0] : null;

        /// <summary>
        /// A list of all server worlds created during the default creation flow. If this type of world
        /// is created manually (i.e. not via the bootstrap APIs), then this list needs to be manually populated.
        /// </summary>
        public static List<World> ServerWorlds => ClientServerTracker.ServerWorlds;

        /// <summary>
        /// A list of all client worlds (excluding thin client worlds!) created during the default creation flow. If this type of world
        /// is created manually (i.e. not via the bootstrap APIs), then this list needs to be manually populated.
        /// </summary>
        public static List<World> ClientWorlds => ClientServerTracker.ClientWorlds;

        /// <summary>
        /// A list of all thin client worlds created during the default creation flow. If this type of world
        /// is created manually  (i.e. not via the bootstrap APIs), then this list needs to be manually populated.
        /// </summary>
        public static List<World> ThinClientWorlds => ClientServerTracker.ThinClientWorlds;

        private static int s_NextThinClientId;

        private static OverrideAutomaticNetcodeBootstrap s_OverrideCache;
        private static bool s_OverrideCacheHasResult;

        /// <summary>
        /// Initialize the bootstrap class and reset the static data everytime a new instance is created.
        /// </summary>

        public ClientServerBootstrap()
        {
            s_NextThinClientId = 1;
            s_OverrideCache = default;
            s_OverrideCacheHasResult = default;
#if UNITY_SERVER && UNITY_CLIENT
            UnityEngine.Debug.LogError("Both UNITY_SERVER and UNITY_CLIENT defines are present. This is not allowed and will lead to undefined behaviour, they are for dedicated server or client only logic so can't work together.");
#endif
        }

        /// <summary>
        /// Utility method for creating a local world without any netcode systems.
        /// </summary>
        /// <param name="defaultWorldName">The name to use for the default world.</param>
        /// <returns>World with default systems added, set to run as the main local world.
        /// See <see cref="WorldFlags"/>.</returns>
        public static World CreateLocalWorld(string defaultWorldName)
        {
            // The default world must be created before generating the system list in order to have a valid TypeManager instance.
            // The TypeManager is initialized the first time any world is created.
            var world = new World(defaultWorldName, WorldFlags.Game);
            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
            return world;
        }

        /// <summary>
        /// Implement the ICustomBootstrap interface. Create the default client and server worlds
        /// based on the <see cref="RequestedPlayType"/>.
        /// In the Editor, it also creates thin client worlds, if <see cref="RequestedNumThinClients"/> is not 0.
        /// </summary>
        /// <param name="defaultWorldName">The name to use for the default world. Unused, can be null or empty.</param>
        /// <inheritdoc cref="ICustomBootstrap.Initialize"/>
        public virtual bool Initialize(string defaultWorldName)
        {
            // If the user added an OverrideDefaultNetcodeBootstrap MonoBehaviour to their active scene,
            // or disabled Bootstrapping project-wide, this is respected here.
            if (!DetermineIfBootstrappingEnabled())
                return false;

            CreateDefaultClientServerWorlds();
            return true;
        }

        /// <summary>
        /// Returns the first <see cref="OverrideAutomaticNetcodeBootstrap"/> in the active scene. Overrides added to any non-active scenes will report as errors.
        /// </summary>
        /// <remarks>This code includes an expensive FindObjectsOfType call, for validation purposes.</remarks>
        /// <param name="logNonErrors">If true, more details are logged, enabling debugging of flows.</param>
        /// <returns>The first override in the active scene.</returns>
        public static OverrideAutomaticNetcodeBootstrap DiscoverAutomaticNetcodeBootstrap(bool logNonErrors = false)
        {
            if (s_OverrideCacheHasResult)
                return s_OverrideCache;
            s_OverrideCacheHasResult = true;

            // Note that GetActiveScene will return invalid when domain reloads are ENABLED.
            var activeScene = SceneManager.GetActiveScene();
            // We must use `FindObjectsInactive.Include` here, otherwise we'll get zero results.
            var sceneConfigurations = UnityEngine.Object.FindObjectsByType<OverrideAutomaticNetcodeBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (sceneConfigurations.Length <= 0)
            {
                if(logNonErrors)
                    UnityEngine.Debug.Log($"[DiscoverAutomaticNetcodeBootstrap] Did not find any instances of `OverrideAutomaticNetcodeBootstrap`.");
                return s_OverrideCache;
            }
            Array.Sort(sceneConfigurations); // Attempt to make the results somewhat deterministic and reliable via sorting by `name`, then `InstanceId`.
            for (int i = 0; i < sceneConfigurations.Length; i++)
            {
                var config = sceneConfigurations[i];
                // A scene comparison here DOES NOT WORK in builds, as - in a build - the GameObject has not yet been attached to its scene.
                // Update 08/24: Also true when domain reloads are enabled!
                // Thus, Active Scene Validation is only performed when available (Editor && UnityEditor.EditorSettings.enterPlayModeOptions == None).
                // Note: Double-click on a scene to set it as the Active scene.
                var activeSceneIsValid = activeScene.IsValid() || SceneManager.loadedSceneCount == 1;
                var isConfigInActiveScene = !activeSceneIsValid || !config.gameObject.scene.IsValid() || config.gameObject.scene == activeScene;
                if (s_OverrideCache)
                {
                    var msg = $"[DiscoverAutomaticNetcodeBootstrap] Cannot select `OverrideAutomaticNetcodeBootstrap` on GameObject '{config.name}' with value `{config.ForceAutomaticBootstrapInScene}` (in scene '{LogScene(config.gameObject.scene, activeScene)}') as we've already selected another ('{s_OverrideCache.name}' with value `{s_OverrideCache.ForceAutomaticBootstrapInScene}` in scene '{LogScene(s_OverrideCache.gameObject.scene, activeScene)}')!";
                    if (config.gameObject.scene == s_OverrideCache.gameObject.scene || isConfigInActiveScene)
                    {
                        msg += " It's erroneous to have multiple in the same scene!";
                        UnityEngine.Debug.LogError(msg, config);
                    }
                    else
                    {
                        if (logNonErrors)
                        {
                            msg += $" AND this config ('{config.name}') is not in the Active scene!";
                            UnityEngine.Debug.Log(msg, config);
                        }
                    }
                    continue;
                }

                if (isConfigInActiveScene)
                {
                    s_OverrideCache = config;
                    if (logNonErrors)
                        UnityEngine.Debug.Log($"[DiscoverAutomaticNetcodeBootstrap] Using discovered `OverrideAutomaticNetcodeBootstrap` on GameObject '{s_OverrideCache.name}' with value `{s_OverrideCache.ForceAutomaticBootstrapInScene}` (in scene '{LogScene(s_OverrideCache.gameObject.scene, activeScene)}') as it's in the active scene ({LogScene(activeScene, activeScene)})!");
                    continue;
                }

                if (logNonErrors)
                    UnityEngine.Debug.Log($"[DiscoverAutomaticNetcodeBootstrap] Ignoring `OverrideAutomaticNetcodeBootstrap` on GameObject '{config.name}' with value `{config.ForceAutomaticBootstrapInScene}` (in scene '{LogScene(config.gameObject.scene, activeScene)}') as this scene is not the Active scene!");
            }
            return s_OverrideCache;

            static string LogScene(Scene scene, Scene active)
            {
                var isValid = scene.IsValid();
                var extraWhenValid = isValid ? $",name:'{scene.name}',path:'{scene.path}'" : null;
                return $"Scene[buildIdx:{scene.buildIndex},handle:{scene.handle},valid:{isValid},loaded:{scene.isLoaded},isSubScene:{scene.isSubScene},isActive:{(active == scene)},rootCount:{scene.rootCount}{extraWhenValid}]";
            }
        }

        /// <summary>
        /// Automatically discovers whether or not there is an <see cref="OverrideAutomaticNetcodeBootstrap" /> present
        /// in the active scene, and if there is, uses its value to clobber the default.
        /// </summary>
        /// <param name="logNonErrors">If true, more details are logged, enabling debugging of flows.</param>
        /// <returns>Whether there is an <see cref="OverrideAutomaticNetcodeBootstrap"/>. Otherwise false.</returns>
        public static bool DetermineIfBootstrappingEnabled(bool logNonErrors = false)
        {
            var automaticNetcodeBootstrap = DiscoverAutomaticNetcodeBootstrap(logNonErrors);
            var automaticBootstrapSettingValue = automaticNetcodeBootstrap
                ? automaticNetcodeBootstrap.ForceAutomaticBootstrapInScene
                : (NetCodeConfig.Global ? NetCodeConfig.Global.EnableClientServerBootstrap : NetCodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap);
            return automaticBootstrapSettingValue == NetCodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap;
        }

        /// <summary>
        /// Utility method for creating the default client and server worlds based on the settings
        /// in the PlayMode tools in the Editor or client/server defined in a player.
        /// Should be used in custom implementations of `Initialize`.
        /// </summary>
        protected virtual void CreateDefaultClientServerWorlds()
        {
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
            if (NetCodeConfig.Global != null && NetCodeConfig.Global.HostWorldModeSelection == NetCodeConfig.HostWorldMode.SingleWorld && RequestedPlayType == PlayType.ClientAndServer)
            {
                CreateSingleWorldHost("ClientAndServerWorld");
            }
            else
#endif
            {
                if (RequestedPlayType == PlayType.Server || RequestedPlayType == PlayType.ClientAndServer)
                    CreateServerWorld("ServerWorld");
                if (RequestedPlayType == PlayType.Client || RequestedPlayType == PlayType.ClientAndServer)
                    CreateClientWorld("ClientWorld");
            }

#if UNITY_EDITOR
            if (RequestedPlayType == PlayType.Client || RequestedPlayType == PlayType.ClientAndServer)
            {
                AutomaticThinClientWorldsUtility.BootstrapThinClientWorlds();
            }
#endif
        }

        /// <summary>
        /// Utility method for creating thin clients worlds.
        /// Can be used in custom implementations of `Initialize` as well as at runtime
        /// to add new clients dynamically.
        /// </summary>
        /// <returns>Thin client world instance.</returns>
        public static World CreateThinClientWorld()
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ThinClientSimulation);
            return CreateThinClientWorld(systems);
        }

        /// <param name="systems">List of systems to be included.</param>
        /// <inheritdoc cref="CreateThinClientWorld()"/>
        public static World CreateThinClientWorld(NativeList<SystemTypeIndex> systems)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            Debug.LogWarning("This executable was built using a 'server-only' build target (likely DGS). Thus, may not be able to successfully initialize thin client world.");
#endif
            var world = new World("ThinClientWorld" + s_NextThinClientId++, WorldFlags.GameThinClient);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);

            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
            ThinClientWorlds.Add(world);

            return world;
        }

#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        /// <inheritdoc cref="CreateSingleWorldHost(string,Unity.Collections.NativeList{Unity.Entities.SystemTypeIndex})"/>
        public static World CreateSingleWorldHost(string name)
#else
        internal static World CreateSingleWorldHost(string name)

#endif
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);
            return CreateSingleWorldHost(name, systems);
        }

        /// <summary>
        /// Utility method for creating combined client and server world (a single world "host")
        /// Can be used in custom implementations of `Initialize` as well as at runtime,
        /// to add new clients dynamically.
        /// </summary>
        /// <returns></returns>
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        public static World CreateSingleWorldHost(string name, NativeList<SystemTypeIndex> systems)
#else
        internal static World CreateSingleWorldHost(string name, NativeList<SystemTypeIndex> systems)
#endif
        {
#if (UNITY_CLIENT || UNITY_SERVER) && !UNITY_EDITOR
                throw new NotImplementedException();
#endif
            var world = new World(name, WorldFlags.GameServer | WorldFlags.GameClient);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            return world;
        }

        /// <summary>
        /// Utility method for creating new clients worlds.
        /// Can be used in custom implementations of `Initialize`, as well as at runtime (to add new clients dynamically),
        /// or when you need to create a client programmatically (for example; frontends that allow selecting "Create Game" vs "Join Game", or similar).
        /// </summary>
        /// <param name="name">The client world name</param>
        /// <returns>Client world instance.</returns>
        public static World CreateClientWorld(string name)
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);
            return CreateClientWorld(name, systems);
        }

        /// <param name="name">The client world name</param>
        /// <param name="systems">List of systems to be included.</param>
        /// <inheritdoc cref="CreateClientWorld(string)"/>
        public static World CreateClientWorld(string name, NativeList<SystemTypeIndex> systems)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            throw new PlatformNotSupportedException("This executable was built using a 'server-only' build target (likely DGS). Thus, cannot create client worlds.");
#else
            var world = new World(name, WorldFlags.GameClient);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            ClientWorlds.Add(world);
            return world;
#endif
        }


        /// <summary>
        /// Optional client bootstrap helper method, so your custom bootstrap flows can copy this subset of auto-connect logic.
        /// Reads <see cref="RequestedPlayType"/>, and checks for default AutoConnect arguments if valid.
        /// </summary>
        /// <param name="autoConnectEp">A valid endpoint for auto-connection.</param>
        /// <returns>True if the auto-connect Endpoint is specified for this given <see cref="RequestedPlayType"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the RequestedPlayType enum has an unknown value.</exception>
        public static bool TryFindAutoConnectEndPoint(out NetworkEndpoint autoConnectEp)
        {
            autoConnectEp = default;

            switch (RequestedPlayType)
            {
                case PlayType.Server:
                case PlayType.ClientAndServer:
                {
                    // Allow loopback + AutoConnectPort:
                    if (HasDefaultAddressAndPortSet(out autoConnectEp))
                    {
                        if (!DefaultConnectAddress.IsLoopback)
                        {
                            UnityEngine.Debug.LogWarning($"DefaultConnectAddress is set to `{DefaultConnectAddress.Address}`, but we expected it to be loopback as we're in mode `{RequestedPlayType}`. Using loopback instead!");
                            autoConnectEp = NetworkEndpoint.LoopbackIpv4;
                        }

                        return true;
                    }

                    // Otherwise do nothing.
                    return false;
                }
                case PlayType.Client:
                {
#if UNITY_EDITOR
                    // In the editor, the 'editor window specified' endpoint takes precedence, assuming it's a valid address:
                    if (AutoConnectPort != 0 && MultiplayerPlayModePreferences.IsEditorInputtedAddressValidForConnect(out autoConnectEp))
                        return true;
#endif

                    // Fallback to AutoConnectPort + DefaultConnectAddress.
                    if (HasDefaultAddressAndPortSet(out autoConnectEp))
                        return true;

                    // Otherwise do nothing.
                    return false;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(RequestedPlayType), RequestedPlayType, nameof(TryFindAutoConnectEndPoint));
            }
        }

        /// <summary>
        /// Returns true if user code has specified both an <see cref="AutoConnectPort"/> and <see cref="DefaultConnectAddress"/> set.
        /// </summary>
        /// <param name="autoConnectEp">The resulting combined <see cref="NetworkEndpoint"/>.</param>
        /// <returns>True if user code has specified both an <see cref="AutoConnectPort"/> and <see cref="DefaultConnectAddress"/>.</returns>
        public static bool HasDefaultAddressAndPortSet(out NetworkEndpoint autoConnectEp)
        {
            if (AutoConnectPort != 0 && DefaultConnectAddress != NetworkEndpoint.AnyIpv4)
            {
                autoConnectEp = DefaultConnectAddress.WithPort(AutoConnectPort);
                return true;
            }

            autoConnectEp = default;
            return false;
        }

        /// <summary>
        /// Utility method for creating a new server world.
        /// Can be used in custom implementations of `Initialize` as well as in your game logic (in particular client/server build)
        /// when you need to create the server programmatically (for example, a frontend that allows selecting the role or other logic).
        /// </summary>
        /// <param name="name">The server world name.</param>
        /// <returns>Server world instance.</returns>
        public static World CreateServerWorld(string name)
        {
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.ServerSimulation);
            return CreateServerWorld(name, systems);
        }

        /// <param name="systems">List of systems to be included.</param>
        /// <inheritdoc cref="CreateServerWorld(string)"/>
        public static World CreateServerWorld(string name, NativeList<SystemTypeIndex> systems)
        {
#if UNITY_CLIENT && !UNITY_SERVER && !UNITY_EDITOR
            throw new PlatformNotSupportedException("This executable was built using a 'client-only' build target. Thus, cannot create a server world. In your ProjectSettings, change your 'Client Build Target' to `ClientAndServer` to support creating client-hosted servers.");
#else

            var world = new World(name, WorldFlags.GameServer);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            if (World.DefaultGameObjectInjectionWorld == null)
                World.DefaultGameObjectInjectionWorld = world;

            ServerWorlds.Add(world);
            return world;
#endif
        }

        /// <summary>
        /// The default port to use for auto connection. The default value is zero, which means do not auto connect.
        /// If this is set to a valid port, any call to `CreateClientWorld` - including `CreateDefaultWorlds` and `Initialize` -
        /// will try to connect to the specified port and address, assuming `DefaultConnectAddress` is valid.
        /// Any call to `CreateServerWorld` - including `CreateDefaultWorlds` and `Initialize` - will listen on the specified
        /// port and listen address.
        /// </summary>
        public static ushort AutoConnectPort = 0;
        /// <summary>
        /// <para>The default address to connect to when using auto connect (`AutoConnectPort` is not zero).
        /// If this value is `NetworkEndPoint.AnyIpv4` auto connect will not be used, even if the port is specified.
        /// This is to allow auto listen without auto connect.</para>
        /// <para>The address specified in the `PlayMode Tools` window takes precedence over this when running in the Editor (in `PlayType.Client`).
        /// If that address is not valid or you are running in a player, then `DefaultConnectAddress` will be used instead.</para>
        /// </summary>
        /// <remarks>Note that the `DefaultConnectAddress.Port` will be clobbered by the `AutoConnectPort` if it's set.</remarks>
        public static NetworkEndpoint DefaultConnectAddress = NetworkEndpoint.LoopbackIpv4;
        /// <summary>
        /// The default address to listen on when using auto connect (`AutoConnectPort` is not zero).
        /// </summary>
        public static NetworkEndpoint DefaultListenAddress = NetworkEndpoint.AnyIpv4;
        /// <summary>
        /// <para>Denotes if the server should start listening for incoming connection automatically after the world has been created.</para>
        /// <para>
        /// If the <see cref="AutoConnectPort"/> is set, the server should start listening for connection using the <see cref="DefaultConnectAddress"/>
        /// and <see cref="AutoConnectPort"/>.
        /// </para>
        /// </summary>
        public static bool WillServerAutoListen => AutoConnectPort != 0;

        /// <summary>
        /// The current modality.
        /// </summary>
        /// <seealso cref="ClientServerBootstrap.RequestedPlayType"/>
        public enum PlayType
        {
            /// <summary>
            /// <para>The application can run as client, server, or both. By default, both client and server worlds are created
            /// and the application can host and play as client at the same time.</para>
            /// <para>
            /// This is the default modality when playing in the Editor, unless changed by using the PlayMode tool.
            /// </para>
            /// </summary>
            ClientAndServer = 0,
            /// <summary>
            /// The application runs as a client. Only client worlds are created and the application should connect to
            /// a server.
            /// </summary>
            Client = 1,
            /// <summary>
            /// The application runs as a server. Usually only the server world is created and the application can only
            /// listen for incoming connections.
            /// </summary>
            Server = 2,
        }

        /// <summary>
        /// The current play mode, used to configure drivers and worlds.
        /// <br/> - In editor, this is determined by the PlayMode tools window.
        /// <br/> - In builds, this is determined by the platform (and thus UNITY_SERVER and UNITY_CLIENT defines),
        /// which in turn are controlled by the Project Settings.
        /// </summary>
        /// <remarks>
        /// In builds, use this flag to determine whether your build supports running as a client,
        /// a server, or both.
        /// </remarks>
        public static PlayType RequestedPlayType
        {
            get
            {
#if UNITY_EDITOR
                return MultiplayerPlayModePreferences.RequestedPlayType;
#elif UNITY_SERVER
                return PlayType.Server;
#elif UNITY_CLIENT
                return PlayType.Client;
#else
                return PlayType.ClientAndServer;
#endif
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// The number of thin clients to create. Only available in the Editor.
        /// </summary>
        public static int RequestedNumThinClients => MultiplayerPlayModePreferences.RequestedNumThinClients;
#endif
        //Burst compatible counters that be used in job or ISystem to check when clients or server worlds are present
        internal struct ServerClientCount
        {
            public int serverWorlds;
            public int clientWorlds;
        }
        internal static readonly SharedStatic<ServerClientCount> WorldCounts = SharedStatic<ServerClientCount>.GetOrCreate<ClientServerBootstrap>();

        /// <summary>
        /// Check if a world with a <see cref="WorldFlags.GameServer"/> is present.
        /// </summary>
        /// <value>If at least one world with <see cref="WorldFlags.GameServer"/> flags has been created.</value>
        public static bool HasServerWorld => WorldCounts.Data.serverWorlds > 0;
        /// <summary>
        /// Check if a world with a <see cref="WorldFlags.GameClient"/> is present.
        /// </summary>
        /// <value>If at least one world with <see cref="WorldFlags.GameClient"/> flags has been created.</value>
        public static bool HasClientWorlds => WorldCounts.Data.clientWorlds > 0;

        static class ClientServerTracker
        {
            internal static List<World> ServerWorlds;
            internal static List<World> ClientWorlds;
            internal static List<World> ThinClientWorlds;
            static ClientServerTracker()
            {
                ServerWorlds = new List<World>();
                ClientWorlds = new List<World>();
                ThinClientWorlds = new List<World>();
            }
        }

        /// <summary>
        /// Helper; returns an IEnumerable iterating over all <see cref="ServerWorld"/>'s,
        /// then all <see cref="AllClientWorldsEnumerator"/> worlds (which itself iterates over all <see cref="ClientWorlds"/>,
        /// then all <see cref="ThinClientWorlds"/>).
        /// </summary>
        /// <returns>An IEnumerable.</returns>
        public static IEnumerable<World> AllNetCodeWorldsEnumerator()
        {
            foreach (var server in ServerWorlds)
                yield return server;
            foreach (var clientOrThinClient in AllClientWorldsEnumerator())
                yield return clientOrThinClient;
        }
        /// <summary>
        /// Helper; returns an IEnumerable iterating over all <see cref="ClientWorlds"/>,
        /// then all <see cref="ThinClientWorlds"/>.
        /// </summary>
        /// <returns>An IEnumerable.</returns>
        public static IEnumerable<World> AllClientWorldsEnumerator()
        {
            foreach (var client in ClientWorlds)
                yield return client;
            foreach (var thin in ThinClientWorlds)
                yield return thin;
        }

        /// <summary>
        /// Conditionally assign the given world to both the DefaultGameObjectInjectionWorld and/or CurrentlyActiveGameObjectWorld
        /// if the respective value is either null or the current worlds are not created.
        /// </summary>
        /// <param name="world"></param>
        internal static void AssignCurrentActiveWorldIfNotSet(World world)
        {
            if (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
                World.DefaultGameObjectInjectionWorld = world;
            /*if (ActiveGameObjectWorld.World == null || !ActiveGameObjectWorld.World.IsCreated)
                ActiveGameObjectWorld.World = world;*/
        }
    }

    /// <summary>
    /// Netcode-specific extension methods for worlds.
    /// </summary>
    public static class ClientServerWorldExtensions
    {
        /// <summary>
        /// Check if a world is a thin client.
        /// </summary>
        /// <param name="world">A <see cref="World"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a thin client world.</returns>
        public static bool IsThinClient(this World world)
        {
            return (world.Flags&WorldFlags.GameThinClient) == WorldFlags.GameThinClient;
        }
        /// <summary>
        /// Check if an unmanaged world is a thin client.
        /// </summary>
        /// <param name="world">A <see cref="WorldUnmanaged"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a thin client world.</returns>
        public static bool IsThinClient(this WorldUnmanaged world)
        {
            return (world.Flags&WorldFlags.GameThinClient) == WorldFlags.GameThinClient;
        }
        /// <summary>
        /// Check if a world is a client, will also return true for thin clients.
        /// </summary>
        /// <param name="world">A <see cref="World"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a client or a thin client world.</returns>
        public static bool IsClient(this World world)
        {
            return ((world.Flags&WorldFlags.GameClient) == WorldFlags.GameClient) || world.IsThinClient();
        }
        /// <summary>
        /// Check if an unmanaged world is a client, will also return true for thin clients.
        /// </summary>
        /// <param name="world">A <see cref="WorldUnmanaged"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a client or a thin client world.</returns>
        public static bool IsClient(this WorldUnmanaged world)
        {
            return ((world.Flags&WorldFlags.GameClient) == WorldFlags.GameClient) || world.IsThinClient();
        }
        /// <summary>
        /// Check if a world is a server.
        /// </summary>
        /// <param name="world">A <see cref="World"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a server world.</returns>
        public static bool IsServer(this World world)
        {
            return (world.Flags&WorldFlags.GameServer) == WorldFlags.GameServer;
        }
        /// <summary>
        /// Check if an unmanaged world is a server.
        /// </summary>
        /// <param name="world">A <see cref="WorldUnmanaged"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a server world.</returns>
        public static bool IsServer(this WorldUnmanaged world)
        {
            return (world.Flags&WorldFlags.GameServer) == WorldFlags.GameServer;
        }

        /// <summary>
        /// Check if a world is a single world host (both client and server role).
        /// </summary>
        /// <param name="world">A <see cref="World"/> instance</param>
        /// <returns>Whether <paramref name="world"/> is a client+server world.</returns>
        public static bool IsHost(this World world)
        {
            return IsClient(world) && IsServer(world);
        }

        /// <inheritdoc cref="IsHost(World)"/>
        public static bool IsHost(this WorldUnmanaged world)
        {
            return IsClient(world) && IsServer(world);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureServerWorldSystem : ISystem
    {
        EntityQuery m_SendDataQuery;
        EntityQuery m_TickRateQuery;
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            if (!state.World.IsServer())
                throw new InvalidOperationException("Server worlds must be created with the WorldFlags.GameServer flag");
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationGroup.SetRateManagerCreateAllocator(new NetcodeServerRateManager(simulationGroup));

            var predictionGroup = state.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            predictionGroup.RateManager = new NetcodeServerPredictionRateManager(predictionGroup);
            ++ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            if (ClientServerBootstrap.WillServerAutoListen)
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ClientServerBootstrap.DefaultListenAddress.WithPort(ClientServerBootstrap.AutoConnectPort));
            }

            m_SendDataQuery = state.GetEntityQuery(typeof(GhostSendSystemData));
            m_TickRateQuery = state.GetEntityQuery(typeof(ClientServerTickRate));
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery, m_SendDataQuery);

        }

#if UNITY_EDITOR
        public void OnUpdate(ref SystemState state)
        {
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery, m_SendDataQuery);
        }
#endif

        internal static void ApplyGlobalNetCodeConfigIfPresent(World world, EntityQuery tickRateQuery, EntityQuery ghostSendQuery)
        {
            var serverConfig = NetCodeConfig.Global;
            if (serverConfig)
            {
                if (tickRateQuery.TryGetSingletonRW<ClientServerTickRate>(out var clientServerTickRate))
                    clientServerTickRate.ValueRW = serverConfig.ClientServerTickRate;
                else
                    world.EntityManager.CreateSingleton(serverConfig.ClientServerTickRate);
                ghostSendQuery.GetSingletonRW<GhostSendSystemData>().ValueRW = NetCodeConfig.Global.GhostSendSystemData;
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;

            --ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            ClientServerBootstrap.ServerWorlds.Remove(state.World);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureClientWorldSystem : ISystem
    {
        EntityQuery m_TickRateQuery;
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            if (!state.World.IsClient() && !state.World.IsThinClient())
                throw new InvalidOperationException("Client worlds must be created with the WorldFlags.GameClient flag");
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationGroup.RateManager = new NetcodeClientRateManager(simulationGroup);

            var predictionGroup = state.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            predictionGroup.SetRateManagerCreateAllocator(new NetcodeClientPredictionRateManager(predictionGroup));

            ++ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            if (ClientServerBootstrap.TryFindAutoConnectEndPoint(out var autoConnectEp))
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(state.EntityManager, autoConnectEp);
            }
            m_TickRateQuery = state.GetEntityQuery(typeof(ClientTickRate));
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery);
        }

#if UNITY_EDITOR
        public void OnUpdate(ref SystemState state)
        {
            ApplyGlobalNetCodeConfigIfPresent(state.World, m_TickRateQuery);
        }
#endif

        internal static void ApplyGlobalNetCodeConfigIfPresent(World world, EntityQuery tickRateQuery)
        {
            var clientConfig = NetCodeConfig.Global;
            if (clientConfig)
            {
                if (tickRateQuery.TryGetSingletonRW<ClientTickRate>(out var clientTickRate))
                    clientTickRate.ValueRW = clientConfig.ClientTickRate;
                else
                    world.EntityManager.CreateSingleton(clientConfig.ClientTickRate);
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
                return;

            --ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ClientWorlds.Remove(state.World);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureThinClientWorldSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!state.World.IsThinClient())
                throw new InvalidOperationException("ThinClient worlds must be created with the WorldFlags.GameThinClient flag");
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationGroup.RateManager = new NetcodeClientRateManager(simulationGroup);

            ++ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            if (ClientServerBootstrap.TryFindAutoConnectEndPoint(out var autoConnectEp))
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(state.EntityManager, autoConnectEp);
            }
            // Thin client has no auto connect endpoint configured to connect to. Check if the client has connected to
            // something already (so it has manually connected), if so then connect to the same address
            else if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
            {
                using var driver = ClientServerBootstrap.ClientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamDriver>());
                UnityEngine.Assertions.Assert.IsFalse(driver.IsEmpty);
                var driverData = driver.ToComponentDataArray<NetworkStreamDriver>(Allocator.Temp);
                UnityEngine.Assertions.Assert.IsTrue(driverData.Length == 1);
                if (driverData[0].LastEndPoint.IsValid)
                    SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Connect(state.EntityManager, driverData[0].LastEndPoint);
            }

            state.Enabled = false;
        }

        public void OnDestroy(ref SystemState state)
        {
            --ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ThinClientWorlds.Remove(state.World);
            AutomaticThinClientWorldsUtility.AutomaticallyManagedWorlds.Remove(state.World);
        }
    }


    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    internal partial struct ConfigureSingleWorldHostSystem : ISystem
    {
        EntityQuery m_SendDataQuery;
        EntityQuery m_ClientTickRateQuery;
        EntityQuery m_ClientServerTickRateQuery;
        public void OnCreate(ref SystemState state)
        {
            if (!state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            var simulationGroup = state.World.GetExistingSystemManaged<SimulationSystemGroup>();
            var simulationRateManager = new NetcodeHostRateManager(simulationGroup);
            simulationGroup.SetRateManagerCreateAllocator(simulationRateManager);

            var predictionGroup = state.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            // On a Host, we only want the prediction loop to be fixed, we want to keep normal frame rate for rest of SimulationSystemGroup
            //Input gathering happens outside the prediction loop, to make sure we don't miss inputs
            predictionGroup.RateManager = new NetcodeHostPredictionRateManager(predictionGroup, simulationRateManager.TimeTracker);

            ++ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            ++ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ServerWorlds.Add(state.World);
            ClientServerBootstrap.ClientWorlds.Add(state.World);

            state.Enabled = false;

            if (ClientServerBootstrap.WillServerAutoListen)
            {
                SystemAPI.GetSingletonRW<NetworkStreamDriver>().ValueRW.Listen(ClientServerBootstrap.DefaultListenAddress.WithPort(ClientServerBootstrap.AutoConnectPort));
            }
            m_SendDataQuery = state.GetEntityQuery(typeof(GhostSendSystemData));
            m_ClientTickRateQuery = state.GetEntityQuery(typeof(ClientTickRate));
            m_ClientServerTickRateQuery = state.GetEntityQuery(typeof(ClientServerTickRate));
            ConfigureServerWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientServerTickRateQuery, m_SendDataQuery);
            ConfigureClientWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientTickRateQuery);
        }

#if UNITY_EDITOR
        public void OnUpdate(ref SystemState state)
        {
            ConfigureServerWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientServerTickRateQuery, m_SendDataQuery);
            ConfigureClientWorldSystem.ApplyGlobalNetCodeConfigIfPresent(state.World, m_ClientTickRateQuery);
        }
#endif
        public void OnDestroy(ref SystemState state)
        {
            if (!state.WorldUnmanaged.IsHost())
                return;

            --ClientServerBootstrap.WorldCounts.Data.serverWorlds;
            --ClientServerBootstrap.WorldCounts.Data.clientWorlds;
            ClientServerBootstrap.ServerWorlds.Remove(state.World);
            ClientServerBootstrap.ClientWorlds.Remove(state.World);
        }
    }
}
