using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class ExplicitDefaultSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class ExplicitClientSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Server)]
    public class ExplicitServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [UpdateInWorld(UpdateInWorld.TargetWorld.ClientAndServer)]
    public class ExplicitClientServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    public class BootstrapTests
    {
        [Test]
        public void BootstrapPutsTickSystemInDefaultWorld()
        {
            var oldBootstrapState = ClientServerBootstrap.s_State;
            ClientServerBootstrap.s_State = default;

            var systems = new List<Type>();
            systems.Add(typeof(TickClientInitializationSystem));
            systems.Add(typeof(TickClientSimulationSystem));
            systems.Add(typeof(TickClientPresentationSystem));
            systems.Add(typeof(TickServerInitializationSystem));
            systems.Add(typeof(TickServerSimulationSystem));
            ClientServerBootstrap.GenerateSystemLists(systems);

            Assert.True(ClientServerBootstrap.DefaultWorldSystems.Contains(typeof(TickClientInitializationSystem)));
            Assert.True(ClientServerBootstrap.ExplicitDefaultWorldSystems.Contains(typeof(TickClientInitializationSystem)));
            Assert.True(ClientServerBootstrap.DefaultWorldSystems.Contains(typeof(TickClientSimulationSystem)));
            Assert.True(ClientServerBootstrap.ExplicitDefaultWorldSystems.Contains(typeof(TickClientSimulationSystem)));
            Assert.True(ClientServerBootstrap.DefaultWorldSystems.Contains(typeof(TickClientPresentationSystem)));
            Assert.True(ClientServerBootstrap.ExplicitDefaultWorldSystems.Contains(typeof(TickClientPresentationSystem)));
            Assert.True(ClientServerBootstrap.DefaultWorldSystems.Contains(typeof(TickServerInitializationSystem)));
            Assert.True(ClientServerBootstrap.ExplicitDefaultWorldSystems.Contains(typeof(TickServerInitializationSystem)));
            Assert.True(ClientServerBootstrap.DefaultWorldSystems.Contains(typeof(TickServerSimulationSystem)));
            Assert.True(ClientServerBootstrap.ExplicitDefaultWorldSystems.Contains(typeof(TickServerSimulationSystem)));

            ClientServerBootstrap.s_State = oldBootstrapState;
        }
        [Test]
        public void BootstrapDoesNotPutNetworkTimeSystemInDefaultWorld()
        {
            var oldBootstrapState = ClientServerBootstrap.s_State;
            ClientServerBootstrap.s_State = default;

            var systems = new List<Type>();
            systems.Add(typeof(NetworkTimeSystem));
            ClientServerBootstrap.GenerateSystemLists(systems);

            Assert.False(ClientServerBootstrap.DefaultWorldSystems.Contains(typeof(NetworkTimeSystem)));
            Assert.False(ClientServerBootstrap.ExplicitDefaultWorldSystems.Contains(typeof(NetworkTimeSystem)));

            ClientServerBootstrap.s_State = oldBootstrapState;
        }
        [Test]
        public void BootstrapRespectsUpdateInWorld()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false,
                    typeof(ExplicitDefaultSystem),
                    typeof(ExplicitClientSystem),
                    typeof(ExplicitServerSystem),
                    typeof(ExplicitClientServerSystem));
                testWorld.CreateWorlds(true, 1);

                Assert.IsNotNull(testWorld.DefaultWorld.GetExistingSystem<ExplicitDefaultSystem>());
                Assert.IsNull(testWorld.ServerWorld.GetExistingSystem<ExplicitDefaultSystem>());
                Assert.IsNull(testWorld.ClientWorlds[0].GetExistingSystem<ExplicitDefaultSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystem<ExplicitClientSystem>());
                Assert.IsNull(testWorld.ServerWorld.GetExistingSystem<ExplicitClientSystem>());
                Assert.IsNotNull(testWorld.ClientWorlds[0].GetExistingSystem<ExplicitClientSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystem<ExplicitServerSystem>());
                Assert.IsNotNull(testWorld.ServerWorld.GetExistingSystem<ExplicitServerSystem>());
                Assert.IsNull(testWorld.ClientWorlds[0].GetExistingSystem<ExplicitServerSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystem<ExplicitClientServerSystem>());
                Assert.IsNotNull(testWorld.ServerWorld.GetExistingSystem<ExplicitClientServerSystem>());
                Assert.IsNotNull(testWorld.ClientWorlds[0].GetExistingSystem<ExplicitClientServerSystem>());
            }
        }
        [Test]
        public void DisposingClientServerWorldDoesNotCauseErrors()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false);
                testWorld.CreateWorlds(true, 1);

                testWorld.Tick(1.0f / 60.0f);
                testWorld.DisposeAllClientWorlds();
                testWorld.Tick(1.0f / 60.0f);
                testWorld.DisposeServerWorld();
                testWorld.Tick(1.0f / 60.0f);
            }
        }
        [Test]
        public void DisposingDefaultWorldBeforeClientServerWorldDoesNotCauseErrors()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false);
                testWorld.CreateWorlds(true, 1);

                testWorld.Tick(1.0f / 60.0f);
                testWorld.DisposeDefaultWorld();
            }
        }
    }
}