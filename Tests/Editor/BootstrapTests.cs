using System;
using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    internal partial class ExplicitDefaultSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class ExplicitClientSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ExplicitServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class ExplicitClientServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }

    /// <summary>The <see cref="GhostPredictionHistorySystem"/> does some additional saving and writing, which needs to be tested.</summary>
    internal enum PredictionSetting
    {
        WithPredictedEntities = 1,
        WithInterpolatedEntities = 2,
    }

    /// <summary>Defines which variant to use during testing (and how that variant is applied), thus testing all user flows.</summary>
    internal enum SendForChildrenTestCase
    {
        /// <summary>
        /// Creating a parent and child overload via <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/>
        /// using the map from <see cref="GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride"/>.
        /// </summary>
        YesViaExplicitVariantRule,
        /// <summary>
        /// Creating a child-only overload via <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/>.
        /// Parents will default to <see cref="DontSerializeVariant"/>.
        /// Note that components on children MAY STILL NOT replicate (due to their own child-replication rules).
        /// </summary>
        YesViaExplicitVariantOnlyAllowChildrenToReplicateRule,
        /// <summary>Forcing the variant to DontSerializeVariant via <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/>.</summary>
        NoViaExplicitDontSerializeVariantRule,
        /// <summary>Using the <see cref="GhostAuthoringInspectionComponent"/> to define an override on a child.</summary>
        YesViaInspectionComponentOverride,
        /// <summary>
        /// Children default to <see cref="DontSerializeVariant"/>.
        /// Note that: If the type only has 1 variant, it'll default to it.
        /// </summary>
        Default,
    }

    [Category(NetcodeTestCategories.Foundational)]
    [Category(NetcodeTestCategories.Smoke)]
    internal class BootstrapTests
    {
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

                Assert.IsNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitDefaultSystem>());
                Assert.IsNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitDefaultSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystemManaged<ExplicitClientSystem>());
                Assert.IsNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitClientSystem>());
                Assert.IsNotNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitClientSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystemManaged<ExplicitServerSystem>());
                Assert.IsNotNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitServerSystem>());
                Assert.IsNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitServerSystem>());

                Assert.IsNull(testWorld.DefaultWorld.GetExistingSystemManaged<ExplicitClientServerSystem>());
                Assert.IsNotNull(testWorld.ServerWorld.GetExistingSystemManaged<ExplicitClientServerSystem>());
                Assert.IsNotNull(testWorld.ClientWorlds[0].GetExistingSystemManaged<ExplicitClientServerSystem>());
            }
        }
        [Test]
        public void DisposingClientServerWorldDoesNotCauseErrors()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false);
                testWorld.CreateWorlds(true, 1);

                testWorld.Tick();
                testWorld.DisposeAllClientWorlds();
                testWorld.Tick();
                testWorld.DisposeServerWorld();
                testWorld.Tick();
            }
        }
        [Test]
        public void DisposingDefaultWorldBeforeClientServerWorldDoesNotCauseErrors()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(false);
                testWorld.CreateWorlds(true, 1);

                testWorld.Tick();
                testWorld.DisposeDefaultWorld();
            }
        }

        [Test]
        public void ResetNetworkDriverStore()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);

            {
                var serverDriver = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                //Check it is possible to change the driver if the world is in a stable state and there are no connection or listening interfaces.
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateServerDriver(testWorld.ServerWorld, ref driverStore, netDebug);
                serverDriver.ResetDriverStore(testWorld.ServerWorld.Unmanaged, ref driverStore);
            }
            {
                var clientDriver = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                //Check it is possible to change the driver if the world is in a stable state and there are no connection or listening interfaces.
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateClientDriver(testWorld.ClientWorlds[0], ref driverStore, netDebug);
                clientDriver.ResetDriverStore(testWorld.ClientWorlds[0].Unmanaged, ref driverStore);
            }
            testWorld.Connect();
        }
        [Test]
        public void ResetNetworkDriverStore_ThrowIfConnectionsArePresent()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            {
                var serverDriver = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                //Check it is possible to change the driver if the world is in a stable state and there are no connection or listening interfaces.
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateServerDriver(testWorld.ServerWorld, ref driverStore, netDebug);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    serverDriver.ResetDriverStore(testWorld.ServerWorld.Unmanaged, ref driverStore);
                });
            }
            {
                var clientDriver = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver)).GetSingleton<NetworkStreamDriver>();
                var netDebug = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(NetDebug)).GetSingleton<NetDebug>();
                //Check it is possible to change the driver if the world is in a stable state and there are no connection or listening interfaces.
                var driverStore = new NetworkDriverStore();
                var constructor = testWorld;
                constructor.CreateClientDriver(testWorld.ClientWorlds[0], ref driverStore, netDebug);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    clientDriver.ResetDriverStore(testWorld.ClientWorlds[0].Unmanaged, ref driverStore);
                });
            }
        }
    }
}
