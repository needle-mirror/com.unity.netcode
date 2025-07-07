using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.Tests;
using Unity.Physics.Systems;
using UnityEngine;

namespace Unity.NetCode.Physics.Tests
{
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PhysicsSimulationGroup))]
    partial class PhysicCheck : SystemBase
    {
        public NetworkTick lastTick;
        protected override void OnUpdate()
        {
            lastTick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
        }
    }

    //Increment a predicted ghost field inside fixed step prediction group by using the end command buffer.
    //It uses the latest value on SomaData compoent and increment it by one each time the PredictedFixedStepSimulationSystemGroup run.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PhysicsSimulationGroup))]
    partial class TestCmdBufferUpdate : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingleton<BeginFixedStepSimulationEntityCommandBufferSystem.Singleton>();
            var cmd = singleton.CreateCommandBuffer(World.Unmanaged);
            var time = SystemAPI.GetSingleton<NetworkTime>();
            foreach (var (data, ent) in SystemAPI.Query<RefRO<SomeData>>().WithEntityAccess())
            {
                var newValue = data.ValueRO;
                newValue.Value += 1;
                cmd.SetComponent(ent, newValue);
            }
        }
    }

    //Increment a predicted ghost field in prediction loop.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    partial class TestPredictionLoopSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var data in SystemAPI.Query<RefRW<AllPredictedComponentData>>())
            {
                data.ValueRW.Value += 1;
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    partial struct MovedBeforeISystem : ISystem
    {
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    partial struct MovedAfterSystem : ISystem
    {
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(MovedAfterSystem))]
    partial struct MovedIndirectSystem : ISystem
    {
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    partial struct StayISystem : ISystem
    {
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    partial class MovedBeforeSystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(PhysicsSystemGroup))]
    partial class MovedAfterSystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(MovedAfterSystemBase))]
    partial class MovedIndirectSystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    partial class StaySystemBase : SystemBase
    {
        protected override void OnUpdate()
        {
        }
    }

    internal class PhysicsLoopConfigurationTests
    {
        [Test]
        public void SystemsAreMovedInThePredictedFixedStepGroup()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");
            var systemsToMove = new Type[]
            {
                typeof(MovedBeforeISystem),
                typeof(MovedAfterSystem),
                typeof(MovedIndirectSystem),
                typeof(StayISystem),
                typeof(MovedBeforeSystemBase),
                typeof(MovedAfterSystemBase),
                typeof(MovedIndirectSystemBase),
                typeof(StaySystemBase)
            };
            testWorld.Bootstrap(true,systemsToMove);

            testWorld.CreateWorlds(true, 1);
            //check that all these systems has been moved
            var predictedFixedGroup = testWorld.ServerWorld.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>();
            var fixedGroup = testWorld.ServerWorld.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            var systems = predictedFixedGroup.GetAllSystems();
            var allFixedSystems = fixedGroup.GetAllSystems();
            Assert.IsTrue(systems.Contains(testWorld.ServerWorld.GetExistingSystem<PhysicsSystemGroup>()), "PhysicsSystemGroup not moved");
            foreach (var s in systemsToMove)
            {
                var shouldBeMoved = s != typeof(StaySystemBase) && s != typeof(StayISystem);
                Assert.AreEqual(shouldBeMoved, systems.Contains(testWorld.ServerWorld.GetExistingSystem(s)), $"{s.Name} not moved into {nameof(PredictedFixedStepSimulationSystemGroup)}");
                Assert.AreEqual(shouldBeMoved, !allFixedSystems.Contains(testWorld.ServerWorld.GetExistingSystem(s)), $"{s.Name} not moved from {nameof(FixedStepSimulationSystemGroup)}");
            }
        }

        class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new AllPredictedComponentData());
                baker.AddComponent(entity, new SomeData());
            }
        }

        internal enum PhysicsRunMode
        {
            RequirePredictedGhost = 0,
            EnableLagCompensation = 1,
            RequirePhysicsEntities = 2,
            AlwaysRun = 3,
        }

        [Test]
        public void CommandBufferSystems_AreUpdateMultipleTimes()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");

                testWorld.Bootstrap(true, typeof(TestCmdBufferUpdate), typeof(TestPredictionLoopSystem));
                var cubeGameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var rb = cubeGameObject.AddComponent<Rigidbody>();
                rb.useGravity = false;
                rb.isKinematic = true;
                var authoringComponent = cubeGameObject.AddComponent<GhostAuthoringComponent>();
                cubeGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
                authoringComponent.SupportedGhostModes = GhostModeMask.Predicted;
                testWorld.CreateGhostCollection(cubeGameObject);
                testWorld.CreateWorlds(true, 1);
                var ctr = new ClientServerTickRate();
                ctr.PredictedFixedStepSimulationTickRatio = 2;
                var ctrEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));
                testWorld.ServerWorld.EntityManager.AddComponentData(ctrEntity, ctr);
                ctrEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(ClientServerTickRate));
                testWorld.ClientWorlds[0].EntityManager.AddComponentData(ctrEntity, ctr);
                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();
                var serverEntity = testWorld.SpawnOnServer(0);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new SomeData());
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new AllPredictedComponentData());
                var prevServerTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                var prevClientTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
                for (int i = 0; i < 32; ++i)
                     testWorld.Tick();
                var clientTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
                var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                var serverPhysicsTicks = ctr.PredictedFixedStepSimulationTickRatio*serverTick.TicksSince(prevServerTick);
                var serverTicks = serverTick.TicksSince(prevServerTick);
                var clientTicks = clientTick.TicksSince(prevClientTick) + clientTick.TicksSince(serverTick);
                var clientPhysicsTicks = ctr.PredictedFixedStepSimulationTickRatio*clientTicks;
                //if the fractional part of the tick is sufficient to let the fixed step group to run, we need to include
                //an extra +1
                if (clientTime.IsPartialTick)
                {
                    if (clientTime.ServerTickFraction < 0.5f)
                    {
                        clientPhysicsTicks -= ctr.PredictedFixedStepSimulationTickRatio;
                    }
                    else
                    {
                        --clientPhysicsTicks;
                    }
                }

                var clientEntity = testWorld.TryGetSingletonEntity<GhostInstance>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEntity);
                Assert.AreEqual(serverPhysicsTicks, testWorld.ServerWorld.EntityManager.GetComponentData<SomeData>(serverEntity).Value);
                Assert.AreEqual(clientPhysicsTicks, testWorld.ClientWorlds[0].EntityManager.GetComponentData<SomeData>(clientEntity).Value);
                Assert.AreEqual(serverTicks, testWorld.ServerWorld.EntityManager.GetComponentData<AllPredictedComponentData>(serverEntity).Value);
                Assert.AreEqual(clientTicks , testWorld.ClientWorlds[0].EntityManager.GetComponentData<AllPredictedComponentData>(clientEntity).Value);
            }
        }

        [Test]
        public void EnablePhysicToRunWithoutPredictedGhosts([Values]PredictionLoopUpdateMode loopMode,
            [Values]PhysicsRunMode physicsRunMode)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");
                testWorld.Bootstrap(true, typeof(PhysicCheck));

                //Static ghost
                var cubeGameObject = new GameObject();
                cubeGameObject.name = "StaticGeo";
                cubeGameObject.isStatic = true;
                cubeGameObject.AddComponent<UnityEngine.BoxCollider>().size = new Vector3(1,1,1);

                Assert.IsTrue(testWorld.CreateGhostCollection(cubeGameObject));
                testWorld.CreateWorlds(true, 1);

                if (loopMode == PredictionLoopUpdateMode.AlwaysRun)
                {
                    var clientTickRate = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(ClientTickRate));
                    var tickRate = NetworkTimeSystem.DefaultClientTickRate;
                    tickRate.PredictionLoopUpdateMode = PredictionLoopUpdateMode.AlwaysRun;
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientTickRate, tickRate);
                }

                if (physicsRunMode == PhysicsRunMode.EnableLagCompensation)
                {
                    //for client we need to set the history size
                    testWorld.ClientWorlds[0].EntityManager.AddComponentData(testWorld.ClientWorlds[0].EntityManager.CreateEntity(), new LagCompensationConfig
                    {
                        ServerHistorySize = 0,
                        ClientHistorySize = 1
                    });
                    testWorld.ServerWorld.EntityManager.AddComponentData(testWorld.ServerWorld.EntityManager.CreateEntity(), new LagCompensationConfig());
                }
                else if(physicsRunMode != PhysicsRunMode.RequirePredictedGhost)
                {
                    var clientConfig = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(PhysicsGroupConfig));
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientConfig, new PhysicsGroupConfig
                    {
                        PhysicsRunMode = physicsRunMode == PhysicsRunMode.RequirePhysicsEntities ? PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities : PhysicGroupRunMode.AlwaysRun
                    });
                    var serverConfig = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(PhysicsGroupConfig));
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverConfig, new PhysicsGroupConfig
                    {
                        PhysicsRunMode = physicsRunMode == PhysicsRunMode.RequirePhysicsEntities ? PhysicGroupRunMode.LagCompensationEnabledOrAnyPhysicsEntities : PhysicGroupRunMode.AlwaysRun
                    });
                }

                testWorld.Connect();
                testWorld.GoInGame();
                //TODO we can add more coverage but the logic itself is simple enough to no justify adding more combinations
                //create the non replicated world static geometry entity.
                testWorld.SpawnOnServer(0);
                //var dynamicEnt = testWorld.SpawnOnServer(1);
                for(int i=0;i<64;++i)
                    testWorld.Tick();

                //On the server the loopMode setting does not matter.
                if (physicsRunMode == PhysicsRunMode.EnableLagCompensation)
                {
                    Assert.IsTrue(testWorld.ServerWorld.GetExistingSystemManaged<PhysicCheck>().lastTick ==
                                  testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick);
                    Assert.IsTrue(testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ServerWorld).LatestStoredTick.IsValid, "history must be recorded on the server, even without ghost");
                }
                if (physicsRunMode == PhysicsRunMode.RequirePredictedGhost)
                {
                    Assert.IsFalse(testWorld.ServerWorld.GetExistingSystemManaged<PhysicCheck>().lastTick.IsValid);
                }
                else
                {
                    Assert.IsTrue(testWorld.ServerWorld.GetExistingSystemManaged<PhysicCheck>().lastTick == testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick);
                }
                //On the client if the loopMode is set to RunOnlyWhenPredictedGhostArePresent, the prediction loop does not run
                //in case no predicted ghost is present. And so, no history should be recorded, nor physics loop run, nor prediction has run
                var clientNetworkTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                if (loopMode == PredictionLoopUpdateMode.RequirePredictedGhost)
                {
                    Assert.AreEqual(0,clientNetworkTime.PredictedTickIndex);
                    Assert.IsFalse(testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ClientWorlds[0]).LatestStoredTick.IsValid, "history should not be recorded without ghost because prediction loop does not run");
                    // no need to test further conditions
                    return;
                }
                //if the loopMode is set to AlwaysRun, the prediction loop should have run
                Assert.Greater(clientNetworkTime.PredictedTickIndex, 0);

                // when lag compensation is set, physics however run only once, for firsttimepredicted tick condition only, that it is partially
                //   incorrect in case we have high-frequency physics loop, because physics should be able to run also for partial ticks in case
                //   on the client. But, does it make sense running the physics loop in that case, if there is nothing to actually predict? (everything
                //   is kinematic and driven by server). Looks to me no, so the behaviour seems fine.
                var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                if(time.IsPartialTick)
                    time.ServerTick.Decrement();
                if (physicsRunMode == PhysicsRunMode.EnableLagCompensation)
                {
                    Assert.IsTrue(testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ClientWorlds[0]).LatestStoredTick.IsValid, "history should be recorded when lag compensation is enabled");
                    Assert.IsTrue(testWorld.ClientWorlds[0].GetExistingSystemManaged<PhysicCheck>().lastTick == time.ServerTick);
                }
                else if(physicsRunMode == PhysicsRunMode.RequirePredictedGhost)
                {
                    Assert.IsFalse(testWorld.ClientWorlds[0].GetExistingSystemManaged<PhysicCheck>().lastTick == time.ServerTick);
                }
                else
                {
                    Assert.IsTrue(testWorld.ClientWorlds[0].GetExistingSystemManaged<PhysicCheck>().lastTick == time.ServerTick);
                }
            }
        }
    }
}
