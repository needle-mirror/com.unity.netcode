using NUnit.Framework;
using Unity.NetCode;
using Unity.NetCode.Tests;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Tests.Editor
{
    [Category(NetcodeTestCategories.Foundational)]
    internal class SnapshotSequenceIdTests
    {
        [Test]
        public void CalculateSequenceIdDelta_Works()
        {
            // Check SSId's that we've confirmed (via ServerTick) are NEW:
            const bool confirmedNewer = true;
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(5, 5, confirmedNewer));
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(250, 250, confirmedNewer));
            Assert.AreEqual(1, NetworkSnapshotAck.CalculateSequenceIdDelta(1, 0, confirmedNewer));
            Assert.AreEqual(1, NetworkSnapshotAck.CalculateSequenceIdDelta(2, 1, confirmedNewer));
            Assert.AreEqual(2, NetworkSnapshotAck.CalculateSequenceIdDelta(1, byte.MaxValue, confirmedNewer));
            Assert.AreEqual(10, NetworkSnapshotAck.CalculateSequenceIdDelta(130, 120, confirmedNewer));
            Assert.AreEqual(255, NetworkSnapshotAck.CalculateSequenceIdDelta(5, 6, confirmedNewer));

            // Check SSId's that we've confirmed (via ServerTick) are OLD (i.e. stale):
            const bool confirmedStale = false;
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(5, 5, confirmedStale));
            Assert.AreEqual(0, NetworkSnapshotAck.CalculateSequenceIdDelta(250, 250, confirmedStale));
            Assert.AreEqual(-1, NetworkSnapshotAck.CalculateSequenceIdDelta(0, 1, confirmedStale));
            Assert.AreEqual(-255, NetworkSnapshotAck.CalculateSequenceIdDelta(0, byte.MaxValue, confirmedStale));
            Assert.AreEqual(-2, NetworkSnapshotAck.CalculateSequenceIdDelta(byte.MaxValue, 1, confirmedStale));
            Assert.AreEqual(-(256 - 10), NetworkSnapshotAck.CalculateSequenceIdDelta(130, 120, confirmedStale));
            Assert.AreEqual(-255, NetworkSnapshotAck.CalculateSequenceIdDelta(6, 5, confirmedStale));
        }

        [Test]
        public void SnapshotSequenceId_Statistics_NetworkPacketLoss_Works()
        {
            // Test transport packet loss:
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50;
                testWorld.DriverSimulatedDrop = 20; // Interval, so 5%.
                //We need to set this to receive only, otherwise the packet receive and the send jobs will
                //update the shared packet count internally, causing either more loss or less loss
                //(depending what run first). And because we are not using a specific seed, the delay can affect
                //that.
                //this ensure only the Receive job increment the packet count using from dropping packet at the expected
                //interval
                testWorld.DriverSimulatorPacketMode = ApplyMode.ReceivedPacketsOnly;

                var stats = RunForAWhile(testWorld);
                // Other kinds of packet loss should not have occurred:
                Assert.Zero(stats.NumPacketsCulledOutOfOrder);
                Assert.Zero(stats.NumPacketsCulledAsArrivedOnSameFrame);
                // Expecting loss here:
                Assert.NotZero(stats.NumPacketsDroppedNeverArrived);
                // This could be higher due to low number of samples.
                AssertPercentInRange(stats.NetworkPacketLossPercent, 4, 8, "NetworkPacketLossPercent");
                // Check combined:
                Assert.AreEqual(stats.NumPacketsDroppedNeverArrived, stats.CombinedPacketLossCount);
                AssertPercentInRange(stats.CombinedPacketLossPercent, 4, 8, "CombinedPacketLossPercent");
            }
        }

        [Test]
        public void SnapshotSequenceId_Statistics_OutOfOrderAndClobbered_Works()
        {
            // Test jitter packet loss (out of order, and multiple arriving on the same frame):
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50;
                testWorld.DriverSimulatedJitter = 40;

                var stats = RunForAWhile(testWorld);
                // Other kind of packet loss should not have occurred:

                // NumPacketsDroppedNeverArrived will ASSUME there has been some loss,
                // until we confirm it's actually just an out of order packet.
                Assert.LessOrEqual(stats.NumPacketsDroppedNeverArrived, 5, "NumPacketsDroppedNeverArrived");
                AssertPercentInRange(stats.NetworkPacketLossPercent, 0, 1, "NetworkPacketLossPercent");
                // Expecting loss here:
                Assert.NotZero(stats.NumPacketsCulledAsArrivedOnSameFrame, "NumPacketsCulledAsArrivedOnSameFrame");
                AssertPercentInRange(stats.ArrivedOnTheSameFrameClobberedPacketLossPercent, 4, 11, "ArrivedOnTheSameFrameClobberedPacketLossPercent");
                Assert.NotZero(stats.NumPacketsCulledOutOfOrder, "NumPacketsCulledOutOfOrder");
                AssertPercentInRange(stats.OutOfOrderPacketLossPercent, 35, 45, "OutOfOrderPacketLossPercent");
                // Check combined:
                AssertPercentInRange(stats.CombinedPacketLossPercent, 40, 60, "CombinedPacketLossPercent");
            }
        }



        [Test]
        public void SnapshotSequenceId_Statistics_Combined_Works()
        {
            // Test all of them together:
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50;
                testWorld.DriverSimulatedJitter = 40;
                testWorld.DriverSimulatedDrop = 20; // Interval, so 5%.
                //We need to set this to receive only, otherwise the packet receive and the send jobs will
                //update the shared packet count internally, causing either more loss or less loss
                //(depending what run first). And because we are not using a specific seed, the delay can affect
                //that.
                //this ensure only the Receive job increment the packet count using from dropping packet at the expected
                //interval
                testWorld.DriverSimulatorPacketMode = ApplyMode.ReceivedPacketsOnly;

                var stats = RunForAWhile(testWorld);
                // Expecting loss across all types:
                Assert.NotZero(stats.NumPacketsDroppedNeverArrived);
                AssertPercentInRange(stats.NetworkPacketLossPercent, 4, 8, "NetworkPacketLossPercent");
                Assert.NotZero(stats.NumPacketsCulledAsArrivedOnSameFrame);
                AssertPercentInRange(stats.ArrivedOnTheSameFrameClobberedPacketLossPercent, 7, 9, "ArrivedOnTheSameFrameClobberedPacketLossPercent");
                Assert.NotZero(stats.NumPacketsCulledOutOfOrder);
                AssertPercentInRange(stats.OutOfOrderPacketLossPercent, 30, 50, "OutOfOrderPacketLossPercent");
                // Check combined:
                AssertPercentInRange(stats.CombinedPacketLossPercent, 45, 55, "CombinedPacketLossPercent");
            }
        }

        private static void AssertPercentInRange(double perc, int min, int max, string fieldName)
        {
            var percMultiplied = (int)(perc * 100);
            Assert.GreaterOrEqual(percMultiplied, min, $"{fieldName} - Percent {perc:P1} within {min} and {max}!");
            Assert.LessOrEqual(percMultiplied, max, $"{fieldName} - Percent {perc:P1} within {min} and {max}!");
        }

        private static SnapshotPacketLossStatistics RunForAWhile(NetCodeTestWorld testWorld)
        {
            const float frameTime = 1.0f / 60.0f;
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject("RandomGhostToTriggerSnapshotSends");
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(GhostTypeConverter.GhostTypes.EnableableComponents, EnabledBitBakedValue.StartEnabledAndWaitForClientSpawn);
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect(frameTime, 32); // Packet loss can mess with this!
            testWorld.GoInGame();

            const int seconds = 25;
            for (var i = 0; i < seconds * 60; i++)
                testWorld.Tick();

            var stats = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).SnapshotPacketLoss;
            Debug.Log($"Stats after test: {stats.ToFixedString()}!");
            Assert.NotZero(stats.NumPacketsReceived, "Test setup issue!");
            return stats;
        }
    }
}
