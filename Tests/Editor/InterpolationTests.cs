#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using Unity.Transforms;
using Random = UnityEngine.Random;

namespace Unity.NetCode.Tests
{
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class MoveAlongAxisSystem : SystemBase
    {
        //move 1/10 unit per frame
        public float moveSpeed = 6f;

        protected override void OnUpdate()
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            var speed = moveSpeed;
            Entities.ForEach((Entity ent, ref LocalTransform tx) => { tx.Position += new float3(speed * deltaTime); }).Run();
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class TestInterpGhost : SystemBase
    {
        private float3 prevPos;
        protected override void OnUpdate()
        {
            Entities
                .WithoutBurst()
                .ForEach((Entity ent, in LocalTransform tx) =>
            {
                Assert.GreaterOrEqual(tx.Position.x, prevPos.x);
                prevPos = tx.Position;
            }).Run();
        }
    }

    internal class NetworkTimeTests
    {
        const float FrameTime = 1.0f / 60.0f;

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        [Category(NetcodeTestCategories.Smoke)]
        public void WhenUsingIPC_ClientPredictOnlyOneTickAhead()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // Spawn a new entity on the server. Server will start send snapshots now.
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                var connectionEnt = testWorld.TryGetSingletonEntity<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
                //the test is tick sensitive because it takes some time to the command age to adjust toward 0.
                //with the current setup (alpha = 1/8) and the current 20% max adjustment for the delta time it takes
                //around 50 ticks to get the command age around 0.
                for (int i = 0; i < 51; ++i)
                {
                    //There will be some interpolated tick since we are running slighty faster on client
                    testWorld.Tick(FrameTime*0.75f);
                    testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                    var ackComponent = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkSnapshotAck>(connectionEnt);
                    var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                    if (serverTick.IsValid && ackComponent.LastReceivedSnapshotByLocal.IsValid)
                    {
                        //because the client update after the server, the expected server tick (we simulated) must be equals to the ackComponent.LastReceivedSnapshotByLocal
                        //or slightly higher
                        //but the predicted tick
                        var ackTick = ackComponent.LastReceivedSnapshotByLocal;
                        Assert.IsFalse(ackTick.IsNewerThan(serverTick));
                        ackTick.Add(2);
                        Assert.IsFalse(serverTick.IsNewerThan(ackTick));
                        //The initial value can be greater than 0 and then sooner it get to 0 (the expected) after some ticks
                        Assert.LessOrEqual(ackComponent.ServerCommandAge, 128);
                    }
                }
                var ack = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkSnapshotAck>(connectionEnt);
                Assert.AreEqual(0, ack.ServerCommandAge, "Server command age should be zero!");
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void InterpolationAndPredictedTickNeverGoesBack()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(MoveAlongAxisSystem), typeof(TestInterpGhost));
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // Spawn a new entity on the server. Server will start send snapshots now.
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                NetworkTick prevTargetTick = NetworkTick.Invalid;
                NetworkTick prevInterpTick = NetworkTick.Invalid;
                for (int i = 0; i < 50; ++i)
                {
                    var currentFrameTime = Random.Range(FrameTime*0.75f, FrameTime*1.25f);
                    testWorld.Tick(currentFrameTime);
                    var networkTimeSystemData = testWorld.GetSingleton<NetworkTimeSystemData>(testWorld.ClientWorlds[0]);
                    if (networkTimeSystemData.predictTargetTick.IsValid)
                    {
                        if (prevTargetTick.IsValid)
                        {
                            Assert.IsFalse(prevTargetTick.IsNewerThan(networkTimeSystemData.predictTargetTick));
                            Assert.IsFalse(prevInterpTick.IsNewerThan(networkTimeSystemData.interpolateTargetTick));
                        }
                        prevTargetTick = networkTimeSystemData.predictTargetTick;
                        prevInterpTick = networkTimeSystemData.interpolateTargetTick;
                    }
                }
            }
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        public void InterpolationTickAdaptToPacketDelay()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(MoveAlongAxisSystem), typeof(TestInterpGhost));
                //This is the max allowed latency (so internal buffers are sized correctly)
                testWorld.DriverSimulatedDelay = 200;
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // Spawn a new entity on the server. Server will start send snapshots now.
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                NetworkTick prevTargetTick = NetworkTick.Invalid;
                NetworkTick prevInterpTick = NetworkTick.Invalid;
                var delays = new []{ 70, 100, 200,150, 100 };
                foreach (var delay in delays)
                {
                    var connectionEnt = testWorld.TryGetSingletonEntity<NetworkStreamConnection>(testWorld.ClientWorlds[0]);
                    var connection = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkStreamConnection>(connectionEnt);
                    // TODO - Fetch as readonly when inner methods are marked as readonly (to prevent copy).
                    ref var driverInstance = ref testWorld.GetSingletonRW<NetworkStreamDriver>(testWorld.ClientWorlds[0]).ValueRW.DriverStore.GetDriverInstanceRW(connection.DriverId);
                    var simStageId = NetworkPipelineStageId.Get<SimulatorPipelineStage>();
                    driverInstance.driver.GetPipelineBuffers(driverInstance.unreliablePipeline, simStageId, connection.Value, out var _, out var _, out var simulatorBuffer);
                    unsafe
                    {
                        var simulatorCtx = (SimulatorUtility.Parameters*)simulatorBuffer.GetUnsafePtr();
                        simulatorCtx->PacketDelayMs = delay;
                    }
                    for (int i = 0; i < 50; ++i)
                    {
                        testWorld.Tick();
                        var networkTimeSystemData = testWorld.GetSingleton<NetworkTimeSystemData>(testWorld.ClientWorlds[0]);
                        if (networkTimeSystemData.predictTargetTick.IsValid)
                        {
                            if (prevTargetTick.IsValid)
                            {
                                Assert.IsFalse(prevTargetTick.IsNewerThan(networkTimeSystemData.predictTargetTick));
                                Assert.IsFalse(prevInterpTick.IsNewerThan(networkTimeSystemData.interpolateTargetTick));
                            }
                            prevTargetTick = networkTimeSystemData.predictTargetTick;
                            prevInterpTick = networkTimeSystemData.interpolateTargetTick;
                        }
                    }
                }
            }
        }

        [Test]
        [Ignore("Disabled as there is a bug with RTT calculations when sending RPCs - we do not correctly account for (i.e. subtract the cost of) reliable pipeline resends. Tracked as MTT-11335")]
        public void InterpolationTickAdaptToPacketDrop()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(MoveAlongAxisSystem), typeof(TestInterpGhost));
                testWorld.DriverSimulatedDelay = 5;
                testWorld.DriverSimulatedDrop = 3; // Interval, so 33%, or every 3rd packet.
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Interpolated;
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect(FrameTime, 128);
                testWorld.GoInGame();
                // Spawn a new entity on the server. Server will start send snapshots now.
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                NetworkTick prevTargetTick = NetworkTick.Invalid;
                NetworkTick prevInterpTick = NetworkTick.Invalid;
                var ntsd = default(NetworkTimeSystemData);
                for (int i = 0; i < 100; ++i)
                {
                    testWorld.Tick();
                    ntsd = testWorld.GetSingleton<NetworkTimeSystemData>(testWorld.ClientWorlds[0]);
                    if (ntsd.predictTargetTick.IsValid)
                    {
                        // UnityEngine.Debug.Log($"[Tick:{NetCodeTestWorld.TickIndex}] predictTargetTick:{ntsd.predictTargetTick.ToFixedString()} interpolateTargetTick:{ntsd.interpolateTargetTick.ToFixedString()} (delta:({ntsd.predictTargetTick.TicksSince(ntsd.interpolateTargetTick)})");
                        if (prevTargetTick.IsValid)
                        {
                            Assert.IsFalse(prevTargetTick.IsNewerThan(ntsd.predictTargetTick));
                            Assert.IsFalse(prevInterpTick.IsNewerThan(ntsd.interpolateTargetTick));
                        }
                        prevTargetTick = ntsd.predictTargetTick;
                        prevInterpTick = ntsd.interpolateTargetTick;
                    }
                }
                Assert.Greater(ntsd.currentInterpolationFrames, 2f, "currentInterpolationFrames");
                Assert.Less(ntsd.currentInterpolationFrames, 4f, "currentInterpolationFrames");
            }
        }
    }
}
