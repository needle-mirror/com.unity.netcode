using System;
using System.Collections.Generic;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Collections;
#if UNITY_EDITOR
using Unity.NetCode.Editor;
using UnityEngine;
#endif

namespace Unity.NetCode.Tests
{
    public class NetCodeTestWorld : IDisposable, INetworkStreamDriverConstructor
    {
        public World DefaultWorld => m_DefaultWorld;
        public World ServerWorld => m_ServerWorld;
        public World[] ClientWorlds => m_ClientWorlds;

        private World m_DefaultWorld;
        private World[] m_ClientWorlds;
        private World m_ServerWorld;
        private ClientServerBootstrap.State m_OldBootstrapState;
        private bool m_DefaultWorldInitialized;
        private double m_ElapsedTime;
        public int DriverFixedTime = 16;
        public int DriverSimulatedDelay = 0;

        public int[] DriverFuzzFactor;
        public int DriverFuzzOffset = 0;
        public uint DriverRandomSeed = 0;

#if UNITY_EDITOR
        private GameObject m_GhostCollection;
        private BlobAssetStore m_BlobAssetStore;
#endif

        public List<string> NetCodeAssemblies = new List<string>{
        };

        public NetCodeTestWorld()
        {
#if UNITY_EDITOR
            // Not having a default world means RegisterUnloadOrPlayModeChangeShutdown has not been called which causes memory leaks
            DefaultWorldInitialization.DefaultLazyEditModeInitialize();
#endif
            m_OldBootstrapState = ClientServerBootstrap.s_State;
            ClientServerBootstrap.s_State = default;
            m_DefaultWorld = new World("NetCodeTest");
            m_ElapsedTime = 42;
        }
        public void Dispose()
        {
            if (m_ClientWorlds != null)
            {
                for (int i = 0; i < m_ClientWorlds.Length; ++i)
                {
                    m_ClientWorlds[i].Dispose();
                }
            }
            if (m_ServerWorld != null)
                m_ServerWorld.Dispose();
            if (m_DefaultWorld != null)
                m_DefaultWorld.Dispose();
            m_ClientWorlds = null;
            m_ServerWorld = null;
            m_DefaultWorld = null;
            ClientServerBootstrap.s_State = m_OldBootstrapState;

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

        private static List<Type> s_NetCodeSystems;
        public void Bootstrap(bool includeNetCodeSystems, params Type[] userSystems)
        {
            var systems = new List<Type>();
            if (includeNetCodeSystems)
            {
                if (s_NetCodeSystems == null)
                {
                    s_NetCodeSystems = new List<Type>();
                    var sysList = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
                    foreach (var sys in sysList)
                    {
                        if (sys.Assembly.FullName.StartsWith("Unity.NetCode,") ||
                            sys.Assembly.FullName.StartsWith("Unity.Entities,") ||
                            sys.Assembly.FullName.StartsWith("Unity.Transforms,"))
                        {
                            s_NetCodeSystems.Add(sys);
                        }
                    }
                }
                systems.AddRange(s_NetCodeSystems);
            }
            if (NetCodeAssemblies.Count > 0)
            {
                var sysList = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default);
                foreach (var sys in sysList)
                {
                    bool shouldAdd = false;
                    var sysName = sys.Assembly.FullName;
                    foreach (var asm in NetCodeAssemblies)
                    {
                        shouldAdd |= sysName.StartsWith(asm);
                    }
                    if (shouldAdd)
                    {
                        systems.Add(sys);
                    }
                }
            }
            systems.AddRange(userSystems);
            ClientServerBootstrap.GenerateSystemLists(systems);
        }

        public void CreateWorlds(bool server, int numClients)
        {
            var oldConstructor = NetworkStreamReceiveSystem.s_DriverConstructor;
            NetworkStreamReceiveSystem.s_DriverConstructor = this;
            if (!m_DefaultWorldInitialized)
            {
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(m_DefaultWorld,
                    ClientServerBootstrap.ExplicitDefaultWorldSystems);
                m_DefaultWorldInitialized = true;
            }

            if (server)
            {
                if (m_ServerWorld != null)
                    throw new InvalidOperationException("Server world already created");
                m_ServerWorld = ClientServerBootstrap.CreateServerWorld(m_DefaultWorld, "ServerTest");
#if UNITY_EDITOR
                if (m_GhostCollection != null)
                    GameObjectConversionUtility.ConvertGameObjectHierarchy(m_GhostCollection, GameObjectConversionSettings.FromWorld(m_ServerWorld, m_BlobAssetStore));
#endif
            }

            if (numClients > 0)
            {
                if (m_ClientWorlds != null)
                    throw new InvalidOperationException("Client worlds already created");
                m_ClientWorlds = new World[numClients];
                for (int i = 0; i < numClients; ++i)
                {
                    m_ClientWorlds[i] = ClientServerBootstrap.CreateClientWorld(m_DefaultWorld, $"ClientTest{i}");
#if UNITY_EDITOR
                    if (m_GhostCollection != null)
                        GameObjectConversionUtility.ConvertGameObjectHierarchy(m_GhostCollection, GameObjectConversionSettings.FromWorld(m_ClientWorlds[i], m_BlobAssetStore));
#endif
                }
            }
            NetworkStreamReceiveSystem.s_DriverConstructor = oldConstructor;
        }

        public void Tick(float dt)
        {
            // Use fixed timestep in network time system to prevent time dependencies in tests
            NetworkTimeSystem.s_FixedTimestampMS += (uint)(dt*1000.0f);
            m_ElapsedTime += dt;
            m_DefaultWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ServerWorld != null)
                m_ServerWorld.SetTime(new TimeData(m_ElapsedTime, dt));
            if (m_ClientWorlds != null)
            {
                for (int i = 0; i < m_ClientWorlds.Length; ++i)
                    m_ClientWorlds[i].SetTime(new TimeData(m_ElapsedTime, dt));
            }
            m_DefaultWorld.GetExistingSystem<TickServerInitializationSystem>().Update();
            m_DefaultWorld.GetExistingSystem<TickClientInitializationSystem>().Update();
            m_DefaultWorld.GetExistingSystem<TickServerSimulationSystem>().Update();
            m_DefaultWorld.GetExistingSystem<TickClientSimulationSystem>().Update();
            m_DefaultWorld.GetExistingSystem<TickClientPresentationSystem>().Update();
        }

        public void CreateClientDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline)
        {
            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};

            var netParams = new NetworkConfigParameter
            {
                maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts,
                connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS,
                disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
                maxFrameTimeMS = 100,
                fixedFrameTimeMS = DriverFixedTime
            };
            var packetDelay = DriverSimulatedDelay;
            int networkRate = 60;
            // All 3 packet types every frame stored for maximum delay, doubled for safety margin
            int maxPackets = 2 * (networkRate * 3 * packetDelay + 999) / 1000;

            var fuzzFactor = 0;
            const int kStringLength = 10; // we name it ClientTest e.g. 10 bytes long.
            var worldId = int.Parse(world.Name.Substring(kStringLength, world.Name.Length - kStringLength));
            if (DriverFuzzFactor?.Length >= worldId + 1)
            {
                fuzzFactor = DriverFuzzFactor[worldId];
            }

            var simParams = new SimulatorUtility.Parameters
            {
                MaxPacketSize = NetworkParameterConstants.MTU, MaxPacketCount = maxPackets,
                PacketDelayMs = packetDelay,
                FuzzFactor = fuzzFactor,
                FuzzOffset = DriverFuzzOffset,
                RandomSeed = DriverRandomSeed
            };
            driver = new NetworkDriver(new IPCNetworkInterface(), netParams, reliabilityParams, simParams);

            if (DriverSimulatedDelay + fuzzFactor > 0)
            {
                unreliablePipeline = driver.CreatePipeline(typeof(SimulatorPipelineStage),
                    typeof(SimulatorPipelineStageInSend));
                reliablePipeline = driver.CreatePipeline(typeof(SimulatorPipelineStageInSend),
                    typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
            }
            else
            {
                unreliablePipeline = driver.CreatePipeline(typeof(NullPipelineStage));
                reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            }
        }
        public void CreateServerDriver(World world, out NetworkDriver driver, out NetworkPipeline unreliablePipeline, out NetworkPipeline reliablePipeline)
        {
            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};

            var netParams = new NetworkConfigParameter
            {
                maxConnectAttempts = NetworkParameterConstants.MaxConnectAttempts,
                connectTimeoutMS = NetworkParameterConstants.ConnectTimeoutMS,
                disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
                maxFrameTimeMS = 100,
                fixedFrameTimeMS = DriverFixedTime
            };
            driver = new NetworkDriver(new IPCNetworkInterface(), netParams, reliabilityParams);

            unreliablePipeline = driver.CreatePipeline(typeof(NullPipelineStage));
            reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
        }

        public bool Connect(float dt, int maxSteps)
        {
            var ep = NetworkEndPoint.LoopbackIpv4;
            ep.Port = 7979;
            ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);
            for (int i = 0; i < ClientWorlds.Length; ++i)
                ClientWorlds[i].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);
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

        public unsafe Entity TryGetSingletonEntity<T>(World w)
        {
            var type = ComponentType.ReadOnly<T>();
            var query = w.EntityManager.CreateEntityQuery(type);
            int entCount = query.CalculateEntityCount();
#if UNITY_EDITOR
            if (entCount >= 2)
                Debug.LogError("Trying to get singleton, but there are multiple matching entities");
#endif
            if (entCount != 1)
                return Entity.Null;
            return query.GetSingletonEntity();
        }

#if UNITY_EDITOR
        private readonly bool k_ForceRegenerateAllCode = false;
        public bool CreateGhostCollection(string rootPath, string name, params GameObject[] ghostTypes)
        {
            bool validateOnly = !k_ForceRegenerateAllCode;
            if (m_GhostCollection != null)
                return false;
            var collectionGameObject = new GameObject();
            var collection = collectionGameObject.AddComponent<GhostCollectionAuthoringComponent>();
            collection.NamePrefix = name;
            collection.SerializerCollectionPath = rootPath + name + "GhostSerializerCollection.cs";
            collection.DeserializerCollectionPath = rootPath + name + "GhostDeserializerCollection.cs";

            bool success = true;

            foreach (var ghostObject in ghostTypes)
            {
                var ghost = ghostObject.GetComponent<GhostAuthoringComponent>();
                if (ghost == null)
                    ghost = ghostObject.AddComponent<GhostAuthoringComponent>();

                ghost.RootPath = rootPath;
                ghost.SnapshotDataPath = ghostObject.name + "SnapshotData.cs";
                ghost.UpdateSystemPath = ghostObject.name + "UpdateSystem.cs";
                ghost.SerializerPath = ghostObject.name + "Serializer.cs";

                ghost.prefabId = Guid.NewGuid().ToString().Replace("-", "");

                GhostAuthoringComponentEditor.SyncComponentList(ghost);
                if (GhostAuthoringComponentEditor.GenerateGhost(ghost, validateOnly) != GhostCodeGen.Status.NotModified)
                    success = false;

                collection.Ghosts.Add(new GhostCollectionAuthoringComponent.Ghost{prefab = ghost, enabled = true});
            }
            // Trigger code-gen for collection and ghosts, if anything changes that is an error
            if (GhostCollectionAuthoringComponentEditor.GenerateCollection(collection, validateOnly) != GhostCodeGen.Status.NotModified)
                success = false;
            if (!success)
                return false;
            m_GhostCollection = collectionGameObject;
            m_BlobAssetStore = new BlobAssetStore();
            return true;
        }
        public void SpawnOnServer(GameObject go)
        {
            if (m_GhostCollection == null)
                throw new InvalidOperationException("Cannot spawn ghost on server without setting up the ghost first");
            GameObjectConversionUtility.ConvertGameObjectHierarchy(go, GameObjectConversionSettings.FromWorld(ServerWorld, m_BlobAssetStore));

        }
#endif
    }
}