using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    public partial class ExplicitDefaultSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class ExplicitClientSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class ExplicitServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial class ExplicitClientServerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }

    public enum SendForChildrenTestCase
    {
        YesViaDefaultNameDictionary,
        YesViaInspectionComponentOverride,
        NoViaDontSerializeVariantDefault,
    }

    public class BootstrapTests
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
