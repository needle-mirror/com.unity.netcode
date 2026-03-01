#if UNITY_EDITOR
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Assert = UnityEngine.Assertions.Assert;

namespace Unity.NetCode.Tests
{
    internal class GhostInputBehaviourTests
    {
        [Test]
        public async Task GatherInput_IsCalledFor_PredictedGhost([Values] bool testInterpolated)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("GatherInput", autoRegister: false).GetComponent<GhostAdapter>();
            prefab.HasOwner = true;
            prefab.SupportedGhostModes = testInterpolated ? GhostModeMask.Interpolated : GhostModeMask.Predicted;
            prefab.SupportAutoCommandTarget = true;
            prefab.gameObject.AddComponent<TestInputBehaviour>();
            Netcode.RegisterPrefab(prefab.gameObject);

            await testWorld.ConnectAsync(enableGhostReplication: true);

            //Runs for a bit to sync the initial network time etc etc.
            await testWorld.TickMultipleAsync(32); // stabilize ticks
            var serverGo = Object.Instantiate(prefab).gameObject;
            serverGo.GetComponent<GhostAdapter>().OwnerNetworkId = new NetworkId() { Value = 1 };
            // Wait for the client side spawn
            await testWorld.TickMultipleAsync(4);
            // Now run a few ticks to let the test do its thing and collect inputs.
            // There should be partial ticks in there, but test logic should be fine with it.
            await testWorld.TickMultipleAsync(16);

            //This should have run for 1 ticks. So we should have as value 16 on the server
            //Same for the client. Client-side we need to get the spawned gameobject that match this one.
            //TODO-release@potentialUX: we need an easier way (in general, not just for sake of test) to map a GameObject to an entity to a Ghost ID. Useful for tools like mppm if we want to ping another ghost in a clone for example or for tools around binary world setup. Only useful for tooling though? Nothing runtime?
            var client = PredictionCallbackHelper.ClientInstances[0];
            var clientGo = client.gameObject;
            Assert.AreNotEqual(serverGo, clientGo);
            if (testInterpolated)
                Assert.IsFalse(clientGo.WorldExt().EntityManager.HasComponent<PredictedGhost>(clientGo.EntityExt(false)), "sanity check failed, client should not be a predicted ghost");

            //There is the command slack and frame update to consider, so the client will try to stay 3 tick ahead.
            //Considering we are seeing the last update of the server at this point, the delta in between the client
            //and server input value should be 4
            Assert.AreEqual(testInterpolated ? 15 : 17, clientGo.GetComponent<TestInputBehaviour>().InputData.Value.Value);
            Assert.AreEqual(testInterpolated ? 11 : 13, serverGo.GetComponent<TestInputBehaviour>().InputData.Value.Value);
        }

        [Test]
        public async Task CanGetCurrentAndPreviousInputData()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("PredictedCube", autoRegister: false).GetComponent<GhostAdapter>();
            prefab.SupportedGhostModes = GhostModeMask.Predicted;
            prefab.HasOwner = true;
            prefab.SupportAutoCommandTarget = true;
            prefab.gameObject.AddComponent<TestInputBehaviour>();
            Netcode.RegisterPrefab(prefab.gameObject);

            await testWorld.ConnectAsync(enableGhostReplication: true);

            //sync network time, so client does not skip ticks
            await testWorld.TickMultipleAsync(32);
            //We should be 4 ahead the server
            var serverTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
            var clientTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
            Assert.AreEqual(4, clientTick.TicksSince(serverTick));
            var serverGo = Object.Instantiate(prefab);
            serverGo.GetComponent<GhostAdapter>().OwnerNetworkId = new NetworkId() { Value = 1 };
            //do some ticks and yield to start all the GhostBehaviours.
            for (int i = 0; i < 16; ++i)
            {
                await Awaitable.NextFrameAsync();
                await testWorld.TickAsync();
            }
        }

        [Test(Description = "make sure input gathering is only called on client, only on owner")]
        public async Task TestInputs_RespectsOwnership()
        {
            await using var testWorld = new NetCodeTestWorld();

            await testWorld.SetupGameObjectTest(clientCount: 2); // one client that owns the ghost, the other that doesn't
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("PredictedCube", autoRegister: false).GetComponent<GhostAdapter>();
            prefab.HasOwner = true;
            prefab.DefaultGhostMode = GhostMode.Predicted; // both client 0 and 1 predict the ghost, but since only client 0 owns it, only it has input authority
            prefab.SupportAutoCommandTarget = true;
            Netcode.RegisterPrefab(prefab.gameObject);

            await testWorld.ConnectAsync(enableGhostReplication: true);
            var serverGhost = GameObject.Instantiate(prefab);
            serverGhost.OwnerNetworkId = testWorld.GetSingleton<NetworkId>(testWorld.ClientWorlds[0]);
            await testWorld.TickMultipleAsync(4);

            var client0Ghost = PredictionCallbackHelper.ClientInstances.First(o => o.Ghost.World == testWorld.ClientWorlds[0]);
            var client1Ghost = PredictionCallbackHelper.ClientInstances.First(o => o.Ghost.World == testWorld.ClientWorlds[1]);

            // client 0 should be the owner and so should have input gathering
            // client 1 and server should not have input gathering

            void InvalidInputGathering(GameObject o)
            {
                Assert.IsFalse(true, "there shouldn't be any input gathering here");
            }
            client1Ghost.OnInputEvent += InvalidInputGathering;
            serverGhost.GetComponent<PredictionCallbackHelper>().OnInputEvent += InvalidInputGathering;
            int validInputCount = 0;
            client0Ghost.OnInputEvent += o =>
            {
                Assert.AreEqual(testWorld.ClientWorlds[0], o.GetComponent<GhostAdapter>().World);
                validInputCount += 1;
            };

            await testWorld.TickMultipleAsync(1);
            Assert.AreEqual(1, validInputCount);

            // validate if a new ghost that's owned by 1 appears, that ownership is still respected
            var serverGhost_B = GameObject.Instantiate(prefab);
            serverGhost_B.OwnerNetworkId = testWorld.GetSingleton<NetworkId>(testWorld.ClientWorlds[1]);
            await testWorld.TickMultipleAsync(4);
            var client0Ghost_B = PredictionCallbackHelper.ClientInstances.First(o => o != client0Ghost && o.Ghost.World == testWorld.ClientWorlds[0]);
            var client1Ghost_B = PredictionCallbackHelper.ClientInstances.First(o => o != client1Ghost && o.Ghost.World == testWorld.ClientWorlds[1]);

            client0Ghost_B.OnInputEvent += InvalidInputGathering;
            serverGhost_B.GetComponent<PredictionCallbackHelper>().OnInputEvent += InvalidInputGathering;
            var validInputCount_B = 0;
            client1Ghost_B.OnInputEvent += o =>
            {
                validInputCount_B += 1;
            };
            await testWorld.TickMultipleAsync(1);
            Assert.AreEqual(1, validInputCount_B);

            await testWorld.TickMultipleAsync(32); // run multiple ticks, validate there's no error log
        }
    }
}
#endif
