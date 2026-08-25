using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

namespace Unity.NetCode.Tests
{

    internal class EditModeTestWorldStrategy : NetCodeTestWorld.ITestWorldStrategy
    {
        NetCodeTestWorld m_TestWorld;
        private bool m_DefaultWorldInitialized;
        internal List<World> m_WorldsToUpdate;
        private World m_DefaultWorld;

        static readonly ProfilerMarker k_TickServerInitializationSystem = new ProfilerMarker("TickServerInitializationSystem");
        static readonly ProfilerMarker k_TickClientInitializationSystem = new ProfilerMarker("TickClientInitializationSystem");
        static readonly ProfilerMarker k_TickServerSimulationSystem = new ProfilerMarker("TickServerSimulationSystem");
        static readonly ProfilerMarker k_TickClientSimulationSystem = new ProfilerMarker("TickClientSimulationSystem");
        static readonly ProfilerMarker k_TickServerPresentationSystem = new ProfilerMarker("TickServerPresentationSystem");
        static readonly ProfilerMarker k_TickClientPresentationSystem = new ProfilerMarker("TickClientPresentationSystem");

        public NetcodeWorld CreateServerWorld(string name, NetcodeWorld world = null)
        {
            EnsureDefaultWorldInitialized();
            if (world == null)
                world = new NetcodeWorld(name, WorldFlags.GameServer);
            ClientServerBootstrap.AssignCurrentActiveWorldIfNotSet(world);
            TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ServerSystems); // Ensure CreationOrder is respected.
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_ServerSystems);
#if !UNITY_CLIENT || UNITY_EDITOR
            AppendWorldToUpdateList(world);
#endif
            
#if UNITY_EDITOR
            m_TestWorld.BakeGhostCollection(world);
#endif
            return world;
        }

        public NetcodeWorld CreateHostWorld(string name, NetcodeWorld world = null)
        {
            EnsureDefaultWorldInitialized();
            if (world == null)
                world = new NetcodeWorld(name, WorldFlags.GameServer | WorldFlags.GameClient);
            ClientServerBootstrap.AssignCurrentActiveWorldIfNotSet(world);

            TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_HostSystems); // Ensure CreationOrder is respected.
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_HostSystems);
#if !UNITY_SERVER || UNITY_EDITOR
            AppendWorldToUpdateList(world);
#endif

#if UNITY_EDITOR
            m_TestWorld.BakeGhostCollection(world);
#endif
            return world;
        }

        public void Bootstrap(NetCodeTestWorld testWorld)
        {
            this.m_TestWorld = testWorld;
            m_WorldsToUpdate = new List<World>();
            m_DefaultWorld = new World("NetCodeTest");
        }

        public void Dispose()
        {
            if (m_DefaultWorld != null)
                m_DefaultWorld.Dispose();
            m_DefaultWorld = null;
        }

        public void DisposeDefaultWorld()
        {
            m_DefaultWorld.Dispose();
            m_DefaultWorld = null;
        }

        public NetcodeWorld CreateClientWorld(string name, bool thinClient, NetcodeWorld world = null)
        {
            EnsureDefaultWorldInitialized();
            if (world == null)
                world = new NetcodeWorld(name, thinClient ? WorldFlags.GameThinClient : WorldFlags.GameClient);
            ClientServerBootstrap.AssignCurrentActiveWorldIfNotSet(world);
            if (world.IsThinClient())
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ThinClientSystems); // Ensure CreationOrder is respected.
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_ThinClientSystems);
            }
            else
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ClientSystems); // Ensure CreationOrder is respected.
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, NetCodeTestWorld.m_ClientSystems);
            }
#if !UNITY_SERVER || UNITY_EDITOR
            AppendWorldToUpdateList(world);
#endif
            
#if UNITY_EDITOR
            m_TestWorld.BakeGhostCollection(world);
#endif
            return world;
        }


        void EnsureDefaultWorldInitialized()
        {
            if (!m_DefaultWorldInitialized)
            {
                TypeManager.SortSystemTypesInCreationOrder(NetCodeTestWorld.m_ControlSystems); // Ensure CreationOrder is respected.
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(m_DefaultWorld,
                    NetCodeTestWorld.m_ControlSystems);
                m_DefaultWorldInitialized = true;
            }
        }

        public void AppendWorldToUpdateList(World world)
        {
            var tracker = world.GetOrCreateSystemManaged<NetCodeTestWorldWorldTracker>();
            tracker.testWorld = m_TestWorld;
            world.GetExistingSystemManaged<UpdateWorldTimeSystem>().Enabled = false;
            //world.GetExistingSystemManaged<SimulationSystemGroup>().RemoveSystemFromUpdateList(world.GetExistingSystemManaged<EarlyUpdateSystemGroup>());
            //world.GetExistingSystemManaged<SimulationSystemGroup>().RemoveSystemFromUpdateList(world.GetExistingSystemManaged<PreLateUpdateSystemGroup>());
            m_WorldsToUpdate.Add(world);
        }

        public void DisposeClientWorld(NetcodeWorld clientWorld)
        {
            if (clientWorld != null)
            {
                RemoveWorldFromUpdateList(clientWorld);

                if (m_TestWorld.AlwaysDispose || clientWorld.IsCreated) // issue with shutdown test, shutdown already destroys a world, no need to dispose it again
                {
                    clientWorld.Dispose();
                }
            }
        }

        public void DisposeServerWorld(NetcodeWorld serverWorld)
        {
            if (serverWorld != null)
            {
                RemoveWorldFromUpdateList(serverWorld);

                if (m_TestWorld.AlwaysDispose || serverWorld.IsCreated)
                {
                    serverWorld.Dispose();
                }
            }
        }

        // This can't just call TickClientWorld or TickServerWorld because it needs to run the systems in the correct order
        public void TickNoAwait(float dt, bool skipSanityCheck = false)
        {
            m_TestWorld.ApplyDT(dt, skipSanityCheck: skipSanityCheck);

            // Make sure the log flush does not run
            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientInitializationSystem
                    : k_TickServerInitializationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(InitializationSystemGroup));
            }

            //TODO: add proper marker
            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientInitializationSystem
                    : k_TickServerInitializationSystem;
                //UpdateWorldSystemGroup(world, ref marker, typeof(EarlyUpdateSystemGroup));
            }

            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientSimulationSystem
                    : k_TickServerSimulationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(SimulationSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                var profilerMarker = k_TickClientPresentationSystem;
                //UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PreLateUpdateSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                var marker = world.IsClient()
                    ? k_TickClientPresentationSystem
                    : k_TickServerPresentationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(PresentationSystemGroup));
            }
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task TickAsync(float dt, Awaitable waitInstruction = null, bool skipSanityCheck = false)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            // This is called from the various methods that tick the world in NetcodeTestWorld, like TickUntilConnected. In order to have a single implementation, we use the TickAsync from them and then just Wait on them in edit mode. But this means TickAsync needs to work in edit mode so we just TickNoYield here.
            Assert.IsNull(waitInstruction, "Awaitable not supported in edit mode");
            TickNoAwait(dt, skipSanityCheck: skipSanityCheck);
        }

        public void TickClientWorld(float dt, bool clientOnly)
        {
            m_TestWorld.ApplyDTClient(dt);

            bool CanTick(World world)
            {
                return clientOnly ? !world.IsServer() : world.IsClient();
            }

            foreach (var world in m_WorldsToUpdate)
            {
                if(!CanTick(world))
                    continue;
                var marker = k_TickClientInitializationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(InitializationSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!CanTick(world))
                    continue;
                var marker = k_TickClientInitializationSystem;
                //UpdateWorldSystemGroup(world, ref marker, typeof(EarlyUpdateSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!CanTick(world))
                    continue;
                var marker = k_TickClientSimulationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(SimulationSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!CanTick(world))
                    continue;
                var profilerMarker = k_TickClientPresentationSystem;
                //UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PreLateUpdateSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!CanTick(world))
                    continue;
                var profilerMarker = k_TickClientPresentationSystem;
                UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PresentationSystemGroup));
            }
        }

        public void TickServerWorld(float dt)
        {
            m_TestWorld.ApplyDTServer(dt);

            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var marker = k_TickServerInitializationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(InitializationSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var marker = k_TickServerInitializationSystem;
                //UpdateWorldSystemGroup(world, ref marker, typeof(EarlyUpdateSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var marker = k_TickServerSimulationSystem;
                UpdateWorldSystemGroup(world, ref marker, typeof(SimulationSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                if(!world.IsServer())
                    continue;
                var profilerMarker = k_TickServerPresentationSystem;
                //UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PreLateUpdateSystemGroup));
            }
            foreach (var world in m_WorldsToUpdate)
            {
                // At runtime, server worlds can have PresentationSystems too (for late update)
                if(!world.IsServer())
                    continue;
                var profilerMarker = k_TickServerPresentationSystem;
                UpdateWorldSystemGroup(world, ref profilerMarker, typeof(PresentationSystemGroup));
            }
        }

        public void RemoveWorldFromUpdateList(World world)
        {
            m_WorldsToUpdate.Remove(world);
        }

        public World DefaultWorld => m_DefaultWorld;

        private static void UpdateWorldSystemGroup(World world, ref ProfilerMarker marker, Type systemType)
        {
            marker.Begin();
            var systemManaged = world.GetExistingSystemManaged(systemType);
            systemManaged?.Update();
            marker.End();
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    internal partial class NetCodeTestWorldWorldTracker : SystemBase
    {
        public NetCodeTestWorld testWorld;

        protected override void OnCreate()
        {
            Enabled = false;
        }
        protected override void OnDestroy()
        {
            testWorld.RemoveWorldFromUpdateList(World);
        }
        protected override void OnUpdate()
        {
            throw new NotImplementedException();
        }
    }

}
