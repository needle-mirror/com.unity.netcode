using System;
using NUnit.Framework;
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

    public class PhysicsLoopConfigurationTests
    {
        public enum PhysicsRunMode
        {
            RequirePredictedGhost = 0,
            EnableLagCompensation = 1,
            RequirePhysicsEntities = 2,
            AlwaysRun = 3,
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
