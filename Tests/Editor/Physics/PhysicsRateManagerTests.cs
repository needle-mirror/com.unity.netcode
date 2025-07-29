using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode.Tests;
using Unity.Physics.GraphicsIntegration;
using Unity.Physics.Systems;
using UnityEngine;

namespace Unity.NetCode.Physics.Tests
{
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    partial class CheckPhysicsRunOnPartial : SystemBase
    {
        public int numPartialTickUpdates;
        public int numFullTickUpdates;
        public NetworkTick firstTick;
        protected override void OnUpdate()
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            Assert.IsFalse(World.IsServer() && time.IsPartialTick);
            if (World.IsServer())
            {
                Assert.IsTrue(time.IsFirstTimeFullyPredictingTick);
            }
            if (time.IsPartialTick)
                ++numPartialTickUpdates;
            else if (time.IsFirstTimeFullyPredictingTick)
            {
                if (firstTick == default)
                    firstTick = time.ServerTick;
                ++numFullTickUpdates;
            }

        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    partial struct CheckRecordedTime : ISystem
    {
        private double elapsedTime;
        private double lastRecordedTime;
        private NetworkTick lastTick;
        void OnUpdate(ref SystemState state)
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            var currentElapsedTime = SystemAPI.Time.ElapsedTime;
            var deltaTime = SystemAPI.Time.DeltaTime;
            var timestep = state.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
            Assert.IsTrue(deltaTime >= timestep);
            var recordedTime = SystemAPI.GetSingletonBuffer<MostRecentFixedTime>()[0];
            Assert.GreaterOrEqual(currentElapsedTime, elapsedTime);
            Assert.GreaterOrEqual(recordedTime.ElapsedTime, lastRecordedTime);
            Assert.GreaterOrEqual(currentElapsedTime, lastRecordedTime);
            elapsedTime = currentElapsedTime;
            lastRecordedTime = recordedTime.ElapsedTime;
        }
    }

    internal class RateManagerTests
    {
        [TestCase(60, 60)]
        [TestCase(60, 120)]
        [TestCase(60, 180)]
        [TestCase(30, 30)]
        [TestCase(30, 90)]
        [TestCase(30, 120)]
        public void PartialTicksFixedStepUpdate_ReportCorrectElapsedTime(int simulationTickRate, int physicsTickRate)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");
            testWorld.Bootstrap(true, typeof(CheckPhysicsRunOnPartial));

            var cubeGameObject = new GameObject();
            var authoringComponent = cubeGameObject.AddComponent<GhostAuthoringComponent>();
            authoringComponent.SupportedGhostModes = GhostModeMask.Predicted;
            cubeGameObject.name = "Predicted";
            cubeGameObject.isStatic = false;
            var rb = cubeGameObject.AddComponent<Rigidbody>();
            rb.useGravity = false;
            cubeGameObject.AddComponent<UnityEngine.BoxCollider>().size = new Vector3(1,1,1);

            Assert.IsTrue(testWorld.CreateGhostCollection(cubeGameObject));
            testWorld.CreateWorlds(true, 1);
            SetupTickRate(testWorld, simulationTickRate, physicsTickRate);
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 128; ++i)
                testWorld.Tick();
            var clientTime0 = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            // Ensure client is on a full tick so we know what will happen in future ticks
            testWorld.TickClientWorld((1 - clientTime0.ServerTickFraction) / simulationTickRate);
            clientTime0 = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            Assert.IsFalse(clientTime0.IsPartialTick);

            testWorld.SpawnOnServer(0);

            var serverTime0 = testWorld.GetNetworkTime(testWorld.ServerWorld);
            var deltaTime = 1f / 60 / 4;
            for (int i = 0; i < 128; ++i)
                testWorld.Tick(deltaTime);

            var serverTime1 = testWorld.GetNetworkTime(testWorld.ServerWorld);
            var clientTime1 = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            var physicsFullTicks = serverTime1.ServerTick.TicksSince(serverTime0.ServerTick) * physicsTickRate/simulationTickRate;
            var runOnPartial = testWorld.ServerWorld.GetExistingSystemManaged<CheckPhysicsRunOnPartial>();
            Assert.AreEqual(0, runOnPartial.numPartialTickUpdates);
            Assert.AreEqual(physicsFullTicks, runOnPartial.numFullTickUpdates);
            //On the client side, the number of ticks can be slighty higher because of accumulated time for partial ticks and catchup.
            runOnPartial = testWorld.ClientWorlds[0].GetExistingSystemManaged<CheckPhysicsRunOnPartial>();
            physicsFullTicks = clientTime1.ServerTick.TicksSince(runOnPartial.firstTick);
            physicsFullTicks *= physicsTickRate/simulationTickRate;
            Assert.AreEqual(physicsFullTicks, runOnPartial.numFullTickUpdates);
            if(physicsTickRate > simulationTickRate)
                Assert.AreNotEqual(0, runOnPartial.numPartialTickUpdates);
            else
                Assert.AreEqual(0, runOnPartial.numPartialTickUpdates);

        }

        private void SetupTickRate(NetCodeTestWorld testWorld, int simulation, int physics)
        {
            var tickRateEntity = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(ClientServerTickRate));
            var tickRate = new ClientServerTickRate();
            tickRate.SimulationTickRate = simulation;
            tickRate.PredictedFixedStepSimulationTickRatio = physics/simulation;
            tickRate.ResolveDefaults();
            testWorld.ServerWorld.EntityManager.SetComponentData(tickRateEntity, tickRate);
        }
    }
}
