using System;
using System.Collections.Generic;
using Unity.Core;
using Unity.Entities;

namespace Unity.NetCode.Tests
{
    public class NetCodeTestWorld : IDisposable
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

        public NetCodeTestWorld()
        {
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
            systems.AddRange(userSystems);
            ClientServerBootstrap.GenerateSystemLists(systems);
        }

        public void CreateWorlds(bool server, int numClients)
        {
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
            }

            if (numClients > 0)
            {
                if (m_ClientWorlds != null)
                    throw new InvalidOperationException("Client worlds already created");
                m_ClientWorlds = new World[numClients];
                for (int i = 0; i < numClients; ++i)
                    m_ClientWorlds[i] = ClientServerBootstrap.CreateClientWorld(m_DefaultWorld, $"ClientTest{i}");
            }
        }

        public void Tick(float dt)
        {
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
    }
}