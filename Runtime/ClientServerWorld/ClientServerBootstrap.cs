using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode
{
    // Bootstrap of client and server worlds
    [UnityEngine.Scripting.Preserve]
    public class ClientServerBootstrap : ICustomBootstrap
    {
        public static List<Type> DefaultWorldSystems => s_State.DefaultWorldSystems;
        public static List<Type> ExplicitDefaultWorldSystems => s_State.ExplicitDefaultWorldSystems;

        internal struct ChildSystem
        {
            public Type SystemType;
            public Type ParentSystemType;
        }

        internal struct State
        {
            public List<Type> DefaultWorldSystems;
            public List<Type> ExplicitDefaultWorldSystems;
            public List<Type> ClientInitializationSystems;
            public List<Type> ClientSimulationSystems;
            public List<Type> ClientPresentationSystems;
            public List<ChildSystem> ClientChildSystems;
            public List<Type> ServerInitializationSystems;
            public List<Type> ServerSimulationSystems;
            public List<ChildSystem> ServerChildSystems;
        }

        internal static State s_State;

        /// <summary>
        /// Utility method for creating the default way when bootstrapping. Can be used in
        /// custom implementations of `Initialize` to generate systems lists and populate the
        /// default world. If the `world` parameter specified that world will be used and
        /// populated with systems instead of creating a new one.
        /// </summary>
        public World CreateDefaultWorld(string defaultWorldName, World world = null)
        {
            if (world == null)
            {
                // The default world must be created before generating the system list in order to have a valid TypeManager instance.
                // The TypeManage is initialised the first time we create a world.
                world = new World(defaultWorldName, WorldFlags.Game);
                World.DefaultGameObjectInjectionWorld = world;
            }

            var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
            GenerateSystemLists(systems);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, ExplicitDefaultWorldSystems);
#if !UNITY_DOTSRUNTIME
            ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
#endif
            return world;
        }
        public virtual bool Initialize(string defaultWorldName)
        {
            var world = CreateDefaultWorld(defaultWorldName);
            CreateDefaultClientServerWorlds(world);
            return true;
        }

        /// <summary>
        /// Utility method for creating the default client and server worlds based on the settings
        /// in the playmode tools in the editor or client / server defined in a player.
        /// Can be used in custom implementations of `Initialize`.
        /// </summary>
        public virtual void CreateDefaultClientServerWorlds(World defaultWorld)
        {
            PlayType playModeType = RequestedPlayType;
            int numClientWorlds = 1;
            int totalNumClients = numClientWorlds;

            if (playModeType == PlayType.Server || playModeType == PlayType.ClientAndServer)
            {
                CreateServerWorld(defaultWorld, "ServerWorld");
            }

            if (playModeType != PlayType.Server)
            {
#if UNITY_EDITOR
                int numThinClients = RequestedNumThinClients;
                totalNumClients += numThinClients;
#endif
                for (int i = 0; i < numClientWorlds; ++i)
                {
                    CreateClientWorld(defaultWorld, "ClientWorld" + i);
                }
#if UNITY_EDITOR
                for (int i = numClientWorlds; i < totalNumClients; ++i)
                {
                    var clientWorld = CreateClientWorld(defaultWorld, "ClientWorld" + i);
                    clientWorld.EntityManager.CreateEntity(typeof(ThinClientComponent));
                }
#endif
            }
        }

        private static void AddSystemsToList(List<Type> src, List<Type> allManagedSystems, List<Type> allUnmanagedSystems)
        {
            foreach (var stype in src)
                AddSystemToList(stype, allManagedSystems, allUnmanagedSystems);
        }
        private static void AddSystemToList(Type stype, List<Type> allManagedSystems, List<Type> allUnmanagedSystems)
        {
            if (typeof(ComponentSystemBase).IsAssignableFrom(stype))
                allManagedSystems.Add(stype);
            else if (typeof(ISystem).IsAssignableFrom(stype))
                allUnmanagedSystems.Add(stype);
            else
                throw new InvalidOperationException("Bad type");
        }
        public static World CreateClientWorld(World defaultWorld, string name, World worldToUse = null)
        {
#if UNITY_SERVER
            throw new NotImplementedException();
#else
            var world = worldToUse!=null ? worldToUse : new World(name, WorldFlags.Game);
            var initializationGroup = world.GetOrCreateSystem<ClientInitializationSystemGroup>();
            var simulationGroup = world.GetOrCreateSystem<ClientSimulationSystemGroup>();
            var presentationGroup = world.GetOrCreateSystem<ClientPresentationSystemGroup>();
            //These system are always created for dots-runtime automatically as part of the default world initialization.
            //In hybrid, they must be explicitly added to system list and they are only used for tests purpose.
            var initializationTickSystem = defaultWorld.GetExistingSystem<TickClientInitializationSystem>();
            var simulationTickSystem = defaultWorld.GetExistingSystem<TickClientSimulationSystem>();
            var presentationTickSystem = defaultWorld.GetExistingSystem<TickClientPresentationSystem>();

            //Retrieve all clients systems and create all at once via GetOrCreateSystemsAndLogException.
            var allManagedTypes = new List<Type>(s_State.ClientInitializationSystems.Count +
                                            s_State.ClientSimulationSystems.Count +
                                            s_State.ClientPresentationSystems.Count +
                                            s_State.ClientChildSystems.Count + 3);
            var allUnmanagedTypes = new List<Type>();

            AddSystemsToList(s_State.ClientInitializationSystems, allManagedTypes, allUnmanagedTypes);
            AddSystemsToList(s_State.ClientSimulationSystems, allManagedTypes, allUnmanagedTypes);
            AddSystemsToList(s_State.ClientPresentationSystems, allManagedTypes, allUnmanagedTypes);
            foreach (var systemParentType in s_State.ClientChildSystems)
            {
                AddSystemToList(systemParentType.SystemType, allManagedTypes, allUnmanagedTypes);
            }
            world.GetOrCreateSystemsAndLogException(allManagedTypes.ToArray());

            foreach (var t in allUnmanagedTypes)
                world.GetOrCreateUnmanagedSystem(t);


            //Step2: group update binding
            foreach (var systemType in s_State.ClientInitializationSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemType))
                {
                    var system = world.GetExistingSystem(systemType);
                    initializationGroup.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemType))
                {
                    initializationGroup.AddUnmanagedSystemToUpdateList(world.Unmanaged.GetExistingUnmanagedSystem(systemType));
                }
            }

            foreach (var systemType in s_State.ClientSimulationSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemType))
                {
                    var system = world.GetExistingSystem(systemType);
                    simulationGroup.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemType))
                {
                    simulationGroup.AddUnmanagedSystemToUpdateList(world.Unmanaged.GetExistingUnmanagedSystem(systemType));
                }
            }

            foreach (var systemType in s_State.ClientPresentationSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemType))
                {
                    var system = world.GetExistingSystem(systemType);
                    presentationGroup.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemType))
                {
                    presentationGroup.AddUnmanagedSystemToUpdateList(world.Unmanaged.GetExistingUnmanagedSystem(systemType));
                }
            }
            foreach (var systemParentType in s_State.ClientChildSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemParentType.SystemType))
                {
                    var system = world.GetExistingSystem(systemParentType.SystemType);
                    var group = world.GetExistingSystem(systemParentType.ParentSystemType) as ComponentSystemGroup;
                    group.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemParentType.SystemType))
                {
                    var sysref = world.Unmanaged.GetExistingUnmanagedSystem(systemParentType.SystemType);
                    var group = world.GetExistingSystem(systemParentType.ParentSystemType) as ComponentSystemGroup;
                    group.AddUnmanagedSystemToUpdateList(sysref);
                }

            }
            initializationGroup.SortSystems();
            simulationGroup.SortSystems();
            presentationGroup.SortSystems();

            //Bind main world group to tick systems (DefaultWorld tick the client world)
            if (initializationTickSystem != null && simulationTickSystem != null && presentationTickSystem != null)
            {
                initializationGroup.ParentTickSystem = initializationTickSystem;
                initializationTickSystem.AddSystemToUpdateList(initializationGroup);
                simulationGroup.ParentTickSystem = simulationTickSystem;
                simulationTickSystem.AddSystemToUpdateList(simulationGroup);
                presentationGroup.ParentTickSystem = presentationTickSystem;
                presentationTickSystem.AddSystemToUpdateList(presentationGroup);
            }
            else
            {
#if UNITY_DOTSRUNTIME
                //These systems are mandatory
                throw new InvalidOperationException("TickClientInitializationSystem, TickClientSimulationSystem and TickClientPresentationSystem are missing");
#else
                ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
#endif
            }

            if (AutoConnectPort != 0 && DefaultConnectAddress != NetworkEndPoint.AnyIpv4)
            {
                NetworkEndPoint ep;
#if UNITY_EDITOR
                var addr = RequestedAutoConnect;
                if (!NetworkEndPoint.TryParse(addr, AutoConnectPort, out ep))
#endif
                    ep = DefaultConnectAddress.WithPort(AutoConnectPort);
                world.GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);
            }
            return world;
#endif
        }
        public static World CreateServerWorld(World defaultWorld, string name, World worldToUse = null)
        {
#if UNITY_CLIENT && !UNITY_SERVER && !UNITY_EDITOR
            throw new NotImplementedException();
#else
            var world = worldToUse!=null ? worldToUse : new World(name, WorldFlags.Game);
            var initializationGroup = world.GetOrCreateSystem<ServerInitializationSystemGroup>();
            var simulationGroup = world.GetOrCreateSystem<ServerSimulationSystemGroup>();
            //These system are always created for dots-runtime automatically as part of the default world initialization.
            //In hybrid, they must be explicitly added to system list and they are only used for tests purpose.
            var initializationTickSystem = defaultWorld.GetExistingSystem<TickServerInitializationSystem>();
            var simulationTickSystem = defaultWorld.GetExistingSystem<TickServerSimulationSystem>();

            //Retrieve all clients systems and create all at once via GetOrCreateSystemsAndLogException.
            var allManagedTypes = new List<Type>(s_State.ServerInitializationSystems.Count +
                                            s_State.ServerSimulationSystems.Count +
                                            s_State.ServerChildSystems.Count + 2);
            var allUnmanagedTypes = new List<Type>();

            AddSystemsToList(s_State.ServerInitializationSystems, allManagedTypes, allUnmanagedTypes);
            AddSystemsToList(s_State.ServerSimulationSystems, allManagedTypes, allUnmanagedTypes);
            foreach (var systemParentType in s_State.ServerChildSystems)
            {
                AddSystemToList(systemParentType.SystemType, allManagedTypes, allUnmanagedTypes);
            }
            world.GetOrCreateSystemsAndLogException(allManagedTypes.ToArray());
            foreach (var t in allUnmanagedTypes)
                world.GetOrCreateUnmanagedSystem(t);

            //Step2: group update binding
            foreach (var systemType in s_State.ServerInitializationSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemType))
                {
                    var system = world.GetExistingSystem(systemType);
                    initializationGroup.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemType))
                {
                    initializationGroup.AddUnmanagedSystemToUpdateList(world.Unmanaged.GetExistingUnmanagedSystem(systemType));
                }
            }

            foreach (var systemType in s_State.ServerSimulationSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemType))
                {
                    var system = world.GetExistingSystem(systemType);
                    simulationGroup.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemType))
                {
                    simulationGroup.AddUnmanagedSystemToUpdateList(world.Unmanaged.GetExistingUnmanagedSystem(systemType));
                }
            }
            foreach (var systemParentType in s_State.ServerChildSystems)
            {
                if (typeof(ComponentSystemBase).IsAssignableFrom(systemParentType.SystemType))
                {
                    var system = world.GetExistingSystem(systemParentType.SystemType);
                    var group = world.GetExistingSystem(systemParentType.ParentSystemType) as ComponentSystemGroup;
                    group.AddSystemToUpdateList(system);
                }
                else if (typeof(ISystem).IsAssignableFrom(systemParentType.SystemType))
                {
                    var sysref = world.Unmanaged.GetExistingUnmanagedSystem(systemParentType.SystemType);
                    var group = world.GetExistingSystem(systemParentType.ParentSystemType) as ComponentSystemGroup;
                    group.AddUnmanagedSystemToUpdateList(sysref);
                }
            }
            initializationGroup.SortSystems();
            simulationGroup.SortSystems();

            //Bind main world group to tick systems (DefaultWorld tick the client world)
            if (initializationTickSystem != null && simulationTickSystem != null)
            {
                initializationGroup.ParentTickSystem = initializationTickSystem;
                initializationTickSystem.AddSystemToUpdateList(initializationGroup);
                simulationGroup.ParentTickSystem = simulationTickSystem;
                simulationTickSystem.AddSystemToUpdateList(simulationGroup);
            }
            else
            {
#if UNITY_DOTSRUNTIME
                //These systems are mandatory
                throw new InvalidOperationException("TickServerSimulationSystem and TickServerInitializationSystem are missing");
#else
                ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);
#endif
            }
            if (AutoConnectPort != 0)
                world.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(DefaultListenAddress.WithPort(AutoConnectPort));

            return world;
#endif
        }

        /// <summary>
        /// The default port to use for auto connection. The default value is zero, which means do not auto connect.
        /// If this is set to a valid port any call to `CreateClientWorld` - including `CreateDefaultWorlds` and `Initialize` -
        /// will try to connect to the specified port and address - assuming `DefaultConnectAddress` is valid.
        /// Any call to `CreateServerWorld` - including `CreateDefaultWorlds` and `Initialize` - will listen on the specified
        /// port and listen address.
        /// </summary>
        public static ushort AutoConnectPort = 0;
        /// <summary>
        /// The default address to connect to when using auto connect (`AutoConnectPort` is not zero).
        /// If this valud is `NetworkEndPoint.AnyIpv4` auto connect will not be used even if the port is specified.
        /// This is to allow auto listen without auto connect.
        /// The address specified in the Multiplayer PlayMode Tools takes precedence over this when running in the editor,
        /// if that address is not valid or you are running in a player `DefaultConnectAddress` will be used instead.
        /// An invalid `DefaultConnectAddress` will disable auto connect even if the address in Multiplayer PlayMode Tools is valid.
        /// </summary>
        public static NetworkEndPoint DefaultConnectAddress = NetworkEndPoint.LoopbackIpv4;
        /// <summary>
        /// The default address to listen on when using auto connect (`AutoConnectPort` is not zero).
        /// </summary>
        public static NetworkEndPoint DefaultListenAddress = NetworkEndPoint.AnyIpv4;
        public enum PlayType
        {
            ClientAndServer = 0,
            Client = 1,
            Server = 2
        }
#if UNITY_SERVER
        public static PlayType RequestedPlayType => PlayType.Server;
#elif UNITY_EDITOR
        public const int k_MaxNumThinClients = 32;
        public static PlayType RequestedPlayType =>
            (PlayType) UnityEditor.EditorPrefs.GetInt("MultiplayerPlayMode_" + UnityEngine.Application.productName +
                                                      "_Type");

        public static int RequestedNumThinClients
        {
            get
            {
                int numClientWorlds =
                    UnityEditor.EditorPrefs.GetInt("MultiplayerPlayMode_" + UnityEngine.Application.productName +
                                                   "_NumThinClients");
                if (numClientWorlds < 0)
                    numClientWorlds = 0;
                if (numClientWorlds > k_MaxNumThinClients)
                    numClientWorlds = k_MaxNumThinClients;
                return numClientWorlds;
            }
        }

        public static string RequestedAutoConnect
        {
            get
            {
                switch (RequestedPlayType)
                {
                    case PlayType.Client:
                        return UnityEditor.EditorPrefs.GetString(
                            "MultiplayerPlayMode_" + UnityEngine.Application.productName + "_AutoConnectAddress");
                    case PlayType.ClientAndServer:
                        return "127.0.0.1";
                    default:
                        return "";
                }
            }
        }
#elif UNITY_CLIENT
        public static PlayType RequestedPlayType => PlayType.Client;
#else
        public static PlayType RequestedPlayType => PlayType.ClientAndServer;
#endif

        [Flags]
        private enum WorldType
        {
            NoWorld = 0,
            DefaultWorld = 1,
            ClientWorld = 2,
            ServerWorld = 4,
            ExplicitWorld = 8
        }

        protected static T GetSystemAttribute<T>(Type systemType)
            where T : Attribute
        {
            var attribs = TypeManager.GetSystemAttributes(systemType, typeof(T));
            if (attribs.Length != 1)
                return null;
            return attribs[0] as T;
        }
        protected internal static void GenerateSystemLists(IReadOnlyList<Type> systems)
        {
            s_State.DefaultWorldSystems = new List<Type>();
            s_State.ExplicitDefaultWorldSystems = new List<Type>();
            s_State.ClientInitializationSystems = new List<Type>();
            s_State.ClientSimulationSystems = new List<Type>();
            s_State.ClientPresentationSystems = new List<Type>();
            s_State.ClientChildSystems = new List<ChildSystem>();
            s_State.ServerInitializationSystems = new List<Type>();
            s_State.ServerSimulationSystems = new List<Type>();
            s_State.ServerChildSystems = new List<ChildSystem>();

            foreach (var type in systems)
            {
                var targetWorld = GetSystemAttribute<UpdateInWorldAttribute>(type);
                if ((targetWorld != null && targetWorld.World == TargetWorld.Default) ||
#if !UNITY_DOTSRUNTIME
                    type == typeof(ConvertToEntitySystem) ||
#endif
                    type == typeof(InitializationSystemGroup) ||
                    type == typeof(SimulationSystemGroup) ||
                    type == typeof(PresentationSystemGroup))
                {
                    DefaultWorldSystems.Add(type);
                    ExplicitDefaultWorldSystems.Add(type);
                    continue;
                }
                else if (type == typeof(WorldUpdateAllocatorResetSystem))
                {
                    ExplicitDefaultWorldSystems.Add(type);
                    continue;
                }

                var groups = TypeManager.GetSystemAttributes(type, typeof(UpdateInGroupAttribute));;
                if (groups.Length == 0)
                {
                    groups = new Attribute[]{new UpdateInGroupAttribute(typeof(SimulationSystemGroup))};
                }

                foreach (var grp in groups)
                {
                    var group = grp as UpdateInGroupAttribute;
                    if (group.GroupType == typeof(ClientAndServerSimulationSystemGroup) ||
                        group.GroupType == typeof(SimulationSystemGroup))
                    {
                        if (group.GroupType == typeof(ClientAndServerSimulationSystemGroup) && targetWorld != null && targetWorld.World == TargetWorld.Default)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld(TargetWorld.Default)] when using [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup)] {0}",
                                type));
                        if (group.GroupType == typeof(SimulationSystemGroup) &&
                            (targetWorld == null || targetWorld.World == TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & TargetWorld.Server) != 0)
                        {
                            s_State.ServerSimulationSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & TargetWorld.Client) != 0)
                        {
                            s_State.ClientSimulationSystems.Add(type);
                        }
                    }
                    else if (group.GroupType == typeof(ClientAndServerInitializationSystemGroup) ||
                             group.GroupType == typeof(InitializationSystemGroup))
                    {
                        if (group.GroupType == typeof(ClientAndServerInitializationSystemGroup) && targetWorld != null && targetWorld.World == TargetWorld.Default)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld(TargetWorld.Default)] when using [UpdateInGroup(typeof(ClientAndServerInitializationSystemGroup)] {0}",
                                type));
                        if (group.GroupType == typeof(InitializationSystemGroup) &&
                            (targetWorld == null || targetWorld.World == TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & TargetWorld.Server) != 0)
                        {
                            s_State.ServerInitializationSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & TargetWorld.Client) != 0)
                        {
                            s_State.ClientInitializationSystems.Add(type);
                        }
                    }
                    else if (group.GroupType == typeof(ServerInitializationSystemGroup))
                    {
                        if (targetWorld != null)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld] when using [UpdateInGroup(typeof(ServerInitializationSystemGroup)] {0}",
                                type));
                        s_State.ServerInitializationSystems.Add(type);
                    }
                    else if (group.GroupType == typeof(ClientInitializationSystemGroup))
                    {
                        if (targetWorld != null)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld] when using [UpdateInGroup(typeof(ClientInitializationSystemGroup)] {0}",
                                type));
                        s_State.ClientInitializationSystems.Add(type);
                    }
                    else if (group.GroupType == typeof(ServerSimulationSystemGroup))
                    {
                        if (targetWorld != null)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld] when using [UpdateInGroup(typeof(ServerSimulationSystemGroup)] {0}",
                                type));
                        s_State.ServerSimulationSystems.Add(type);
                    }
                    else if (group.GroupType == typeof(ClientSimulationSystemGroup))
                    {
                        if (targetWorld != null)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld] when using [UpdateInGroup(typeof(ClientSimulationSystemGroup)] {0}",
                                type));
                        s_State.ClientSimulationSystems.Add(type);
                    }
                    else if (group.GroupType == typeof(ClientPresentationSystemGroup) ||
                             group.GroupType == typeof(PresentationSystemGroup))
                    {
                        if (group.GroupType == typeof(ClientPresentationSystemGroup) && targetWorld != null)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld] when using [UpdateInGroup(typeof(ClientPresentationSystemGroup)] {0}",
                                type));
                        if (targetWorld != null && (targetWorld.World & TargetWorld.Server) != 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use presentation systems on the server {0}",
                                type));
                        if (group.GroupType == typeof(PresentationSystemGroup) &&
                            (targetWorld == null || targetWorld.World == TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & TargetWorld.Client) != 0)
                        {
                            s_State.ClientPresentationSystems.Add(type);
                        }
                    }
                    else
                    {
                        var mask = GetTopLevelWorldMask(group.GroupType);
                        if (targetWorld != null && targetWorld.World == TargetWorld.Default &&
                            (mask & WorldType.DefaultWorld) == 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot update in default world when parent is not in the default world {0}", type));
                        if ((targetWorld != null && (targetWorld.World & TargetWorld.Client) != 0) &&
                            (mask & WorldType.ClientWorld) == 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot update in client world when parent is not in the client world {0}", type));
                        if ((targetWorld != null && (targetWorld.World & TargetWorld.Server) != 0) &&
                            (mask & WorldType.ServerWorld) == 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot update in server world when parent is not in the server world {0}", type));
                        if ((mask & WorldType.DefaultWorld) != 0 &&
                            (targetWorld == null || targetWorld.World == TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null || (mask & WorldType.ExplicitWorld) != 0)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if ((mask & WorldType.ClientWorld) != 0 &&
                            (targetWorld == null || (targetWorld.World & TargetWorld.Client) != 0))
                        {
                            s_State.ClientChildSystems.Add(new ChildSystem{SystemType = type, ParentSystemType = group.GroupType});
                        }
                        if ((mask & WorldType.ServerWorld) != 0 &&
                            (targetWorld == null || (targetWorld.World & TargetWorld.Server) != 0))
                        {
                            s_State.ServerChildSystems.Add(new ChildSystem{SystemType = type, ParentSystemType = group.GroupType});
                        }
                    }
                }
            }
        }
        static WorldType GetTopLevelWorldMask(Type type)
        {
            var targetWorld = GetSystemAttribute<UpdateInWorldAttribute>(type);
            if (targetWorld != null)
            {
                if (targetWorld.World == TargetWorld.Default)
                    return WorldType.DefaultWorld | WorldType.ExplicitWorld;
                if (targetWorld.World == TargetWorld.Client)
                    return WorldType.ClientWorld;
                if (targetWorld.World == TargetWorld.Server)
                    return WorldType.ServerWorld;
                return WorldType.ClientWorld | WorldType.ServerWorld;
            }

            var groups = TypeManager.GetSystemAttributes(type, typeof(UpdateInGroupAttribute));
            if (groups.Length == 0)
            {
                if (type == typeof(ClientAndServerSimulationSystemGroup) ||
                    type == typeof(ClientAndServerInitializationSystemGroup))
                    return WorldType.ClientWorld | WorldType.ServerWorld;
                if (type == typeof(SimulationSystemGroup) || type == typeof(InitializationSystemGroup))
                    return WorldType.DefaultWorld | WorldType.ClientWorld | WorldType.ServerWorld;
                if (type == typeof(ServerSimulationSystemGroup) || type == typeof(ServerInitializationSystemGroup))
                    return WorldType.ServerWorld;
                if (type == typeof(ClientSimulationSystemGroup) ||
                    type == typeof(ClientInitializationSystemGroup) ||
                    type == typeof(ClientPresentationSystemGroup))
                    return WorldType.ClientWorld;
                if (type == typeof(PresentationSystemGroup))
                    return WorldType.DefaultWorld | WorldType.ClientWorld;
                // Empty means the same thing as SimulationSystemGroup
                return WorldType.DefaultWorld | WorldType.ClientWorld | WorldType.ServerWorld;
            }

            WorldType mask = WorldType.NoWorld;
            foreach (var grp in groups)
            {
                var group = grp as UpdateInGroupAttribute;
                mask |= GetTopLevelWorldMask(group.GroupType);
            }

            return mask;
        }
    }
}
