using System;
using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    // Bootstrap of client and server worlds
    public class ClientServerBootstrap : ICustomBootstrap
    {
        public static List<Type> DefaultWorldSystems => s_State.DefaultWorldSystems;
        public static List<Type> ExplicitDefaultWorldSystems => s_State.ExplicitDefaultWorldSystems;

        internal struct State
        {
            public List<Type> DefaultWorldSystems;
            public List<Type> ExplicitDefaultWorldSystems;
            public List<Type> ClientInitializationSystems;
            public List<Type> ClientSimulationSystems;
            public List<Type> ClientPresentationSystems;
            public List<Tuple<Type, Type>> ClientChildSystems;
            public List<Type> ServerInitializationSystems;
            public List<Type> ServerSimulationSystems;
            public List<Tuple<Type, Type>> ServerChildSystems;
        }

        internal static State s_State;

        public virtual bool Initialize(string defaultWorldName)
        {
            // The default world must be created before generating the system list in order to have a valid TypeManager instance.
            // The TypeManage is initialised the first time we create a world.
            var world = new World(defaultWorldName);
            World.DefaultGameObjectInjectionWorld = world;

            var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
            GenerateSystemLists(systems);

            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, ExplicitDefaultWorldSystems);
            ScriptBehaviourUpdateOrder.UpdatePlayerLoop(world);

            PlayType playModeType = RequestedPlayType;
            int numClientWorlds = RequestedNumClients;

            int totalNumClients = numClientWorlds;
            if (playModeType != PlayType.Server)
            {
#if UNITY_EDITOR
                int numThinClients = RequestedNumThinClients;
                totalNumClients += numThinClients;
#endif
                for (int i = 0; i < numClientWorlds; ++i)
                {
                    CreateClientWorld(world, "ClientWorld" + i);
                }
#if UNITY_EDITOR
                for (int i = numClientWorlds; i < totalNumClients; ++i)
                {
                    var clientWorld = CreateClientWorld(world, "ClientWorld" + i);
                    clientWorld.EntityManager.CreateEntity(typeof(ThinClientComponent));
                }
#endif
            }

            if (playModeType != PlayType.Client)
            {
                CreateServerWorld(world, "ServerWorld");
            }
            return true;
        }

        public static World CreateClientWorld(World defaultWorld, string name)
        {
#if UNITY_SERVER
            throw new NotImplementedException();
#else
            var world = new World(name);
            var initializationGroup = world.GetOrCreateSystem<ClientInitializationSystemGroup>();
            var simulationGroup = world.GetOrCreateSystem<ClientSimulationSystemGroup>();
            var presentationGroup = world.GetOrCreateSystem<ClientPresentationSystemGroup>();
            //Disable groups legacy sorting behavior
            initializationGroup.UseLegacySortOrder = false;
            simulationGroup.UseLegacySortOrder = false;
            presentationGroup.UseLegacySortOrder = false;

            //Pre-create also all the necessary tick systems in the DefaultWorld
            var initializationTickSystem = defaultWorld.GetOrCreateSystem<TickClientInitializationSystem>();
            var simulationTickSystem = defaultWorld.GetOrCreateSystem<TickClientSimulationSystem>();
            var presentationTickSystem = defaultWorld.GetOrCreateSystem<TickClientPresentationSystem>();

            //Retrieve all clients systems and create all at once via GetOrCreateSystemsAndLogException.
            var allSystems = new List<Type>(s_State.ClientInitializationSystems.Count +
                                            s_State.ClientSimulationSystems.Count +
                                            s_State.ClientPresentationSystems.Count +
                                            s_State.ClientChildSystems.Count + 3);

            // By using the two-phase creation, we can't rely anymore on the fact that the World.GetOrCreateSystem always return
            // a system whose OnCreate method as been invoked. It depend on the system initialization order.
            // This break almost all the systems who were relying on that logic and it might require some new changes
            // on the Entities framework or (better) a rethink and decoupling of the systems.
            // In general, OnCreate is reliable ONLY for caching other system instances.
            // ** After trying different solutions I decided to explicitly add the most critical systems that are required to
            // pre-created before the simulation group **
            allSystems.Add(typeof(RpcSystem));  //Needed by RpcCommandSystem
            allSystems.Add(typeof(GhostUpdateSystemGroup)); //Needed by GhostSendSystem
            //NetworkReceiveSystem must be manually created but is not automatically updated by any group. Instead,
            //the ClientSimulationSystem tick it manually before the fixed-update / main update loop
            allSystems.Add(typeof(NetworkReceiveSystemGroup));
            allSystems.AddRange(s_State.ClientInitializationSystems);
            allSystems.AddRange(s_State.ClientSimulationSystems);
            allSystems.AddRange(s_State.ClientPresentationSystems);
            foreach (var systemParentType in s_State.ClientChildSystems)
            {
                allSystems.Add(systemParentType.Item1);
            }
            world.GetOrCreateSystemsAndLogException(allSystems.ToArray());

            //Step2: group update binding
            foreach (var systemType in s_State.ClientInitializationSystems)
            {
                var system = world.GetExistingSystem(systemType);
                initializationGroup.AddSystemToUpdateList(system);
            }

            simulationGroup.AddSystemToUpdateList(world.GetExistingSystem(typeof(RpcSystem)));
            foreach (var systemType in s_State.ClientSimulationSystems)
            {
                var system = world.GetExistingSystem(systemType);
                simulationGroup.AddSystemToUpdateList(system);
            }
            foreach (var systemType in s_State.ClientPresentationSystems)
            {
                var system = world.GetExistingSystem(systemType);
                presentationGroup.AddSystemToUpdateList(system);
            }
            foreach (var systemParentType in s_State.ClientChildSystems)
            {
                var system = world.GetExistingSystem(systemParentType.Item1);
                var group = world.GetExistingSystem(systemParentType.Item2) as ComponentSystemGroup;
                group.AddSystemToUpdateList(system);
            }
            initializationGroup.SortSystems();
            simulationGroup.SortSystemsAndNetworkSystemGroup();
            presentationGroup.SortSystems();

            //Bind main world group to tick systems (DefaultWorld tick the client world)
            initializationGroup.ParentTickSystem = initializationTickSystem;
            initializationTickSystem.AddSystemToUpdateList(initializationGroup);
            simulationGroup.ParentTickSystem = simulationTickSystem;
            simulationTickSystem.AddSystemToUpdateList(simulationGroup);
            presentationGroup.ParentTickSystem = presentationTickSystem;
            presentationTickSystem.AddSystemToUpdateList(presentationGroup);

            return world;
#endif
        }
        public static World CreateServerWorld(World defaultWorld, string name)
        {
#if UNITY_CLIENT && !UNITY_SERVER && !UNITY_EDITOR
            throw new NotImplementedException();
#else
            var world = new World(name);
            var initializationGroup = world.GetOrCreateSystem<ServerInitializationSystemGroup>();
            var simulationGroup = world.GetOrCreateSystem<ServerSimulationSystemGroup>();
            //Disable groups legacy sorting behavior
            initializationGroup.UseLegacySortOrder = false;
            simulationGroup.UseLegacySortOrder = false;

            //Pre-create also all the necessary tick systems in the DefaultWorld
            var initializationTickSystem = defaultWorld.GetOrCreateSystem<TickServerInitializationSystem>();
            var simulationTickSystem = defaultWorld.GetOrCreateSystem<TickServerSimulationSystem>();

            //Retrieve all clients systems and create all at once via GetOrCreateSystemsAndLogException.
            var allSystems = new List<Type>(s_State.ServerInitializationSystems.Count +
                                            s_State.ServerSimulationSystems.Count +
                                            s_State.ServerChildSystems.Count + 2);

            allSystems.AddRange(s_State.ServerInitializationSystems);
            // By using the two-phase creation, we can't rely anymore on the fact that the World.GetOrCreateSystem always return
            // a system whose OnCreate method as been invoked. It depend on the system initialization order.
            // This break almost all the systems who were relying on that logic and it might require some new changes
            // on the Entities framework or (better) a rethink and decoupling of the systems.
            // In general, OnCreate is reliable ONLY for caching other system instances.
            // ** After trying different solutions I decided to explicitly add the most critical systems that are required to
            // pre-created before the simulation group **
            allSystems.Add(typeof(RpcSystem));
            allSystems.Add(typeof(NetworkReceiveSystemGroup));
            allSystems.AddRange(s_State.ServerSimulationSystems);
            foreach (var systemParentType in s_State.ServerChildSystems)
            {
                allSystems.Add(systemParentType.Item1);
            }
            world.GetOrCreateSystemsAndLogException(allSystems.ToArray());

            //Step2: group update binding
            foreach (var systemType in s_State.ServerInitializationSystems)
            {
                var system = world.GetExistingSystem(systemType);
                initializationGroup.AddSystemToUpdateList(system);
            }

            simulationGroup.AddSystemToUpdateList(world.GetExistingSystem(typeof(RpcSystem)));
            //For the server, the NetworkReceiveSystemGroup is part of the SimulationGroup and it is automatically updated as
            //usual.
            simulationGroup.AddSystemToUpdateList(world.GetExistingSystem(typeof(NetworkReceiveSystemGroup)));
            foreach (var systemType in s_State.ServerSimulationSystems)
            {
                var system = world.GetExistingSystem(systemType);
                simulationGroup.AddSystemToUpdateList(system);
            }
            foreach (var systemParentType in s_State.ServerChildSystems)
            {
                var system = world.GetExistingSystem(systemParentType.Item1);
                var group = world.GetExistingSystem(systemParentType.Item2) as ComponentSystemGroup;
                group.AddSystemToUpdateList(system);
            }
            initializationGroup.SortSystems();
            simulationGroup.SortSystems();

            //Bind main world group to tick systems (DefaultWorld tick the client world)
            initializationGroup.ParentTickSystem = initializationTickSystem;
            initializationTickSystem.AddSystemToUpdateList(initializationGroup);
            simulationGroup.ParentTickSystem = simulationTickSystem;
            simulationTickSystem.AddSystemToUpdateList(simulationGroup);

            return world;
#endif
        }

        public enum PlayType
        {
            ClientAndServer = 0,
            Client = 1,
            Server = 2
        }
#if UNITY_SERVER
        public static PlayType RequestedPlayType => PlayType.Server;
        public static int RequestedNumClients => 0;
#elif UNITY_EDITOR
        public const int k_MaxNumClients = 4;
        public const int k_MaxNumThinClients = 32;
        public static PlayType RequestedPlayType =>
            (PlayType) UnityEditor.EditorPrefs.GetInt("MultiplayerPlayMode_" + UnityEngine.Application.productName +
                                                      "_Type");

        public static int RequestedNumClients
        {
            get
            {
                int numClientWorlds =
                    UnityEditor.EditorPrefs.GetInt("MultiplayerPlayMode_" + UnityEngine.Application.productName +
                                                   "_NumClients");
                if (numClientWorlds < 1)
                    numClientWorlds = 1;
                if (numClientWorlds > k_MaxNumClients)
                    numClientWorlds = k_MaxNumClients;
                return numClientWorlds;
            }
        }

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
        public static int RequestedNumClients => 1;
#else
        public static PlayType RequestedPlayType => PlayType.ClientAndServer;
        public static int RequestedNumClients => 1;
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
            s_State.ClientChildSystems = new List<Tuple<Type, Type>>();
            s_State.ServerInitializationSystems = new List<Type>();
            s_State.ServerSimulationSystems = new List<Type>();
            s_State.ServerChildSystems = new List<Tuple<Type, Type>>();

            foreach (var type in systems)
            {
                var targetWorld = GetSystemAttribute<UpdateInWorld>(type);
                if ((targetWorld != null && targetWorld.World == UpdateInWorld.TargetWorld.Default) ||
                    type == typeof(InitializationSystemGroup) ||
                    type == typeof(SimulationSystemGroup) ||
                    type == typeof(PresentationSystemGroup) ||
                    type == typeof(ConvertToEntitySystem))
                {
                    DefaultWorldSystems.Add(type);
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
                        if (group.GroupType == typeof(ClientAndServerSimulationSystemGroup) && targetWorld != null && targetWorld.World == UpdateInWorld.TargetWorld.Default)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld(UpdateInWorld.TargetWorld.Default)] when using [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup)] {0}",
                                type));
                        if (group.GroupType == typeof(SimulationSystemGroup) &&
                            (targetWorld == null || targetWorld.World == UpdateInWorld.TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Server) != 0)
                        {
                            s_State.ServerSimulationSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Client) != 0)
                        {
                            s_State.ClientSimulationSystems.Add(type);
                        }
                    }
                    else if (group.GroupType == typeof(ClientAndServerInitializationSystemGroup) ||
                             group.GroupType == typeof(InitializationSystemGroup))
                    {
                        if (group.GroupType == typeof(ClientAndServerInitializationSystemGroup) && targetWorld != null && targetWorld.World == UpdateInWorld.TargetWorld.Default)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use [UpdateInWorld(UpdateInWorld.TargetWorld.Default)] when using [UpdateInGroup(typeof(ClientAndServerInitializationSystemGroup)] {0}",
                                type));
                        if (group.GroupType == typeof(InitializationSystemGroup) &&
                            (targetWorld == null || targetWorld.World == UpdateInWorld.TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Server) != 0)
                        {
                            s_State.ServerInitializationSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Client) != 0)
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
                        if (targetWorld != null && (targetWorld.World & UpdateInWorld.TargetWorld.Server) != 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot use presentation systems on the server {0}",
                                type));
                        if (group.GroupType == typeof(PresentationSystemGroup) &&
                            (targetWorld == null || targetWorld.World == UpdateInWorld.TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Client) != 0)
                        {
                            s_State.ClientPresentationSystems.Add(type);
                        }
                    }
                    else
                    {
                        var mask = GetTopLevelWorldMask(group.GroupType);
                        if (targetWorld != null && targetWorld.World == UpdateInWorld.TargetWorld.Default &&
                            (mask & WorldType.DefaultWorld) == 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot update in default world when parent is not in the default world {0}", type));
                        if ((targetWorld != null && (targetWorld.World & UpdateInWorld.TargetWorld.Client) != 0) &&
                            (mask & WorldType.ClientWorld) == 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot update in client world when parent is not in the client world {0}", type));
                        if ((targetWorld != null && (targetWorld.World & UpdateInWorld.TargetWorld.Server) != 0) &&
                            (mask & WorldType.ServerWorld) == 0)
                            UnityEngine.Debug.LogWarning(String.Format(
                                "Cannot update in server world when parent is not in the server world {0}", type));
                        if ((mask & WorldType.DefaultWorld) != 0 &&
                            (targetWorld == null || targetWorld.World == UpdateInWorld.TargetWorld.Default))
                        {
                            DefaultWorldSystems.Add(type);
                            if (targetWorld != null || (mask & WorldType.ExplicitWorld) != 0)
                                ExplicitDefaultWorldSystems.Add(type);
                        }
                        if ((mask & WorldType.ClientWorld) != 0 &&
                            (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Client) != 0))
                        {
                            s_State.ClientChildSystems.Add(new Tuple<Type,Type>(type, group.GroupType));
                        }
                        if ((mask & WorldType.ServerWorld) != 0 &&
                            (targetWorld == null || (targetWorld.World & UpdateInWorld.TargetWorld.Server) != 0))
                        {
                            s_State.ServerChildSystems.Add(new Tuple<Type,Type>(type, group.GroupType));
                        }
                    }
                }
            }
        }
        static WorldType GetTopLevelWorldMask(Type type)
        {
            var targetWorld = GetSystemAttribute<UpdateInWorld>(type);
            if (targetWorld != null)
            {
                if (targetWorld.World == UpdateInWorld.TargetWorld.Default)
                    return WorldType.DefaultWorld | WorldType.ExplicitWorld;
                if (targetWorld.World == UpdateInWorld.TargetWorld.Client)
                    return WorldType.ClientWorld;
                if (targetWorld.World == UpdateInWorld.TargetWorld.Server)
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
