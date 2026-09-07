using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Netcode.NetcodeTime;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.Netcode.Tests
{
    internal class PredictionTests
    {
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        [Category(NetcodeTestCategories.Smoke)]
        public async Task TestPredictionUpdateWithInput()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            // Test if PredictionUpdate is called with the right timings
            await testWorld.ConnectAsync(enableGhostReplication: true);

            var prefab = GhostObjectPrefabHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "Prediction", autoRegister: false, typeof(PredictionTestBehaviour));
            var authoring = prefab.GetComponent<GhostObject>();
            authoring.SupportedGhostModes = GhostModeMask.All;
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.HasOwner = true;
            authoring.SupportAutoCommandTarget = true;
            Netcode.RegisterPrefab(prefab);

            var serverObj = GameObject.Instantiate(prefab).GetComponent<PredictionTestBehaviour>();
            serverObj.Ghost.OwnerNetworkId = testWorld.ClientWorlds[0].LocalConnection.NetworkId;
            serverObj.name = "PredictionObjectForTest";
            await testWorld.TickMultipleAsync(6);
            var clientObj = GameObject.FindObjectsByType<PredictionTestBehaviour>().Where(g => !g.Ghost.IsPrefab()).First(x => x != serverObj);
            Assert.That(clientObj.Ghost.World.IsClient());

            Assert.That(serverObj.Ghost.OwnerNetworkId, Is.EqualTo(testWorld.ClientWorlds[0].LocalConnection.NetworkId));
            Assert.That(clientObj.Ghost.OwnerNetworkId, Is.EqualTo(testWorld.ClientWorlds[0].LocalConnection.NetworkId));

            clientObj.ValueForInput = 0;
            int nbTicks = 10;
            await testWorld.TickMultipleAsync(nbTicks);

            Assert.That(clientObj.ValueForInput, Is.EqualTo(nbTicks));
            // local prediction works
            Assert.That(clientObj.PredictedValue, Is.EqualTo(nbTicks));
            // server sees an updated value coming from the input
            Assert.That(serverObj.PredictedValue, Is.EqualTo(nbTicks - 4), "no updates on server side"); // todo does 4 ticks make sense for the input to
            // reach the server and be processed?
        }

        [Test]
        public async Task TestBasicPredictionWorks()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("exception in prediction loop");

            var serverHelper = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(4);
            var clientHelper = PredictionCallbackHelper.ClientInstances[0];

            void OnPrediction(GameObject go)
            {
                var callbackHelper = go.GetComponent<PredictionCallbackHelper>();
                callbackHelper.SomeGhostField.Value += 1;
            }

            serverHelper.OnPredictionEvent += OnPrediction;
            clientHelper.OnPredictionEvent += OnPrediction;

            await testWorld.TickMultipleAsync(30);

            Assert.AreEqual(30, serverHelper.SomeGhostField.Value);
            Assert.AreEqual(30+4, clientHelper.SomeGhostField.Value);

            var secondServerGhost = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(3);
            var secondClientGhost = PredictionCallbackHelper.ClientInstances[1];

            secondServerGhost.OnPredictionEvent += OnPrediction;
            secondClientGhost.OnPredictionEvent += OnPrediction;

            GameObject.Destroy(serverHelper);
            await testWorld.TickMultipleAsync(30); // make sure destroying one of the predicted ghosts doesn't mess with the other ones.

            Assert.AreEqual(30, secondServerGhost.SomeGhostField.Value);
            Assert.AreEqual(30+4, secondClientGhost.SomeGhostField.Value);
        }

        [Test]
        public async Task TestExceptionInPredictionLoop()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            // Test if PredictionUpdate has exception, that it fails gracefully
            await testWorld.ConnectAsync(enableGhostReplication: true);

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("exception in prediction loop", autoRegister: false);
            prefab.GetComponent<GhostObject>().HasOwner = true;

            Netcode.RegisterPrefab(prefab.gameObject);
            var serverObj = GameObject.Instantiate(prefab);
            var serverObj2 = GameObject.Instantiate(prefab);
            serverObj.GetComponent<GhostObject>().OwnerNetworkId = testWorld.GetSingleton<NetworkId>(testWorld.ClientWorlds[0]);
            serverObj2.GetComponent<GhostObject>().OwnerNetworkId = testWorld.GetSingleton<NetworkId>(testWorld.ClientWorlds[0]);
            await testWorld.TickMultipleAsync(4);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];
            Assert.That(clientObj.Ghost.World.IsClient());
            var clientObj2 = PredictionCallbackHelper.ClientInstances[1];
            Assert.That(clientObj2.Ghost.World.IsClient());
            Assert.That(clientObj, Is.Not.EqualTo(clientObj2));

            var exceptionToExpect = "normal expected exception in prediction loop";
            var executionCount = 0;
            void ExceptionInPrediction(GameObject self)
            {
                if (self.GetComponent<GhostObject>().NetworkTime.IsFirstTimeFullyPredictingTick)
                {
                    executionCount++;
                    throw new Exception(exceptionToExpect);
                }
            }

            void ExceptionInInputLoop(GameObject _)
            {
                executionCount++;
                throw new Exception(exceptionToExpect);
            }
            clientObj.OnPredictionEvent += ExceptionInPrediction;
            clientObj2.OnPredictionEvent += ExceptionInPrediction;
            clientObj.OnInputEvent += ExceptionInInputLoop;
            clientObj2.OnInputEvent += ExceptionInInputLoop;
            serverObj.OnPredictionEvent += ExceptionInPrediction;
            serverObj2.OnPredictionEvent += ExceptionInPrediction;
            int expectedExceptionCount = 6;
            for (int i = 0; i < expectedExceptionCount; i++)
            {
                LogAssert.Expect(LogType.Error, new Regex($".*Exception: {exceptionToExpect}.*"));
            }
            await testWorld.TickAsync();
            Assert.That(executionCount, Is.EqualTo(expectedExceptionCount)); // make sure both predicted update ran and that one exception didn't affect the other object's loop
        }

        [Test(Description = "we have some logic that early returns if there's no Prediction update, so testing this doesn't mess with behaviours that do have an update")]
        public async Task Mix_WithAndWithout_PredictionUpdate_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);

            // 2 GhostBehaviour each with a PredictionUpdate
            var prefabYesYes = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("prediction test yesyes", autoRegister: false);
            prefabYesYes.gameObject.AddComponent<GhostBehaviourA>();

            // 2 GhostBehaviours, only 1 with a PredictionUpdate
            var prefabYesNo = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("prediction test yesno", autoRegister: false);
            prefabYesNo.gameObject.AddComponent<BehaviourAllData>();

            // 1 GhostBehaviour, no PredictionUpdate
            var prefabNo = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("prediction test no", autoRegister: false).gameObject;
            Object.DestroyImmediate(prefabNo.GetComponent<PredictionCallbackHelper>(), allowDestroyingAssets: true);
            prefabNo.gameObject.AddComponent<BehaviourAllData>();

            // No GhostBehaviour, just a GhostObject
            var prefabZeroBehaviour = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("prediction test zero", autoRegister: false).gameObject;
            Object.DestroyImmediate(prefabZeroBehaviour.GetComponent<PredictionCallbackHelper>(), allowDestroyingAssets: true);

            Netcode.RegisterPrefab(prefabYesYes.gameObject);
            Netcode.RegisterPrefab(prefabYesNo.gameObject);
            Netcode.RegisterPrefab(prefabNo.gameObject);
            Netcode.RegisterPrefab(prefabZeroBehaviour.gameObject);

            var serverZero = GameObject.Instantiate(prefabZeroBehaviour);
            await testWorld.TickMultipleAsync(4);
            var clientZero = GameObject.FindObjectsByType<GhostObject>().Where(ghost => !ghost.IsPrefab()).First(ghost => ghost.IsClient);

            var serverNo = GameObject.Instantiate(prefabNo).GetComponent<BehaviourAllData>();
            await testWorld.TickMultipleAsync(4);
            var clientNo = GameObject.FindObjectsByType<BehaviourAllData>().Where(g => !g.Ghost.IsPrefab()).First(data => data.Ghost.IsClient);

            var serverYesNo = GameObject.Instantiate(prefabYesNo);
            await testWorld.TickMultipleAsync(4);
            var clientYesNo = PredictionCallbackHelper.ClientInstances[0];

            var serverYesYes = GameObject.Instantiate(prefabYesYes);
            await testWorld.TickMultipleAsync(4);
            var clientYesYes = PredictionCallbackHelper.ClientInstances[1];

            void OnPrediction(GameObject go)
            {
                var callbackHelper = go.GetComponent<PredictionCallbackHelper>();
                callbackHelper.SomeGhostField.Value += 1;
            }

            serverYesYes.OnPredictionEvent += OnPrediction;
            serverYesYes.GetComponent<GhostBehaviourA>().OnPredictionEvent += OnPrediction;
            clientYesYes.OnPredictionEvent += OnPrediction;
            clientYesYes.GetComponent<GhostBehaviourA>().OnPredictionEvent += OnPrediction;

            serverYesNo.OnPredictionEvent += OnPrediction;
            clientYesNo.OnPredictionEvent += OnPrediction;

            await testWorld.TickMultipleAsync(30);

            Assert.AreEqual(60, serverYesYes.SomeGhostField.Value);
            Assert.AreEqual(60+8, clientYesYes.SomeGhostField.Value);

            Assert.AreEqual(30, serverYesNo.SomeGhostField.Value);
            Assert.AreEqual(30+4, clientYesNo.SomeGhostField.Value);
        }

#if !NETCODE_SNAPSHOT_HISTORY_SIZE_6
        // At history size 6 the server throttles snapshot send cadence (withholding sends until in-flight snapshots are acked), so fewer snapshots arrive and the expected prediction-replay counts below no longer hold.
        [Test(Description = "make sure that enabling a GhostBehaviour mid replay does indeed reenable its prediction update (and that it doesn't stay stuck disabled)")]
        public async Task TestEnableDisable_GhostBehaviour_DifferentTick_SameFrame()
        {
            await using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 100;
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true, maxSteps: 100);

            // 2 GhostBehaviour each with a PredictionUpdate
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("PredHelper", autoRegister: true);

            var server1 = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(30);
            var client1 = PredictionCallbackHelper.ClientInstances[0];
            var server2 = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(30);
            var client2 = PredictionCallbackHelper.ClientInstances[1];
            client2.enabled = false;

            int ticksToWait = 3;
            client1.OnPredictionEvent += o =>
            {
                if (--ticksToWait <= 0)
                    client2.enabled = true; // enable the other ghost after a few ticks
                client1.SomeGhostField.Value++; // still increment myself for sanity checking
            };
            client2.OnPredictionEvent += o =>
            {
                client2.SomeGhostField.Value++; // only gets executed when client1 enables me
            };
            await testWorld.TickMultipleAsync(1);
            Assert.AreEqual(18, client1.SomeGhostField.Value, "sanity check failed, not getting the expected number of ticks");
            Assert.AreEqual(15, client2.SomeGhostField.Value);
        }
#endif

        [Test]
        public async Task PredictionUpdateRespectScriptExecutionOrder()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var prefab = GhostObjectPrefabHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "Prediction", autoRegister: false,
                typeof(GhostBehaviourA), typeof(GhostBehaviourB));
            var authoring = prefab.GetComponent<GhostObject>();
            authoring.SupportedGhostModes = GhostModeMask.All;
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.HasOwner = true;
            authoring.SupportAutoCommandTarget = false;
            Netcode.RegisterPrefab(prefab);
            await testWorld.ConnectAsync(enableGhostReplication: true);

            var update = new List<MonoBehaviour>();
            var predictionUpdate = new List<MonoBehaviour>();
            var serverObjects = new GhostObject[5];
            for (int i = 0; i < 5; ++i)
            {
                var serverObj = GameObject.Instantiate(prefab).GetComponent<GhostBehaviourWithPriority>();
                foreach (var c in serverObj.GetComponents<GhostBehaviourWithPriority>())
                {
                    c.update = update;
                    c.predictionUpdate = predictionUpdate;
                }
                //awake the GhostBehaviours and GhostObject, because mock start as disabled.
                serverObj.Ghost.OwnerNetworkId = testWorld.ClientWorlds[0].LocalConnection.NetworkId;
                serverObj.name = $"Ghost{i}";
                serverObjects[i] = serverObj.Ghost;
            }
            //Run some ticks to get everything stable. So on average the client should do 4 tick of prediction later
            await testWorld.TickMultipleAsync(32);

            var clientUpdate = new List<MonoBehaviour>();
            var clientPredictionUpdate = new List<MonoBehaviour>();
            //we should have 5 ghost client-side
            var clientObjects = GameObject.FindObjectsByType<GhostObject>(FindObjectsInactive.Exclude)
                .Where(go=>!go.World.IsServer() && !go.IsPrefab()).ToArray();
            Assert.AreEqual(5, clientObjects.Length);
            for (int i = 0; i < 5; ++i)
            {
                foreach (var c in clientObjects[i].GetComponents<GhostBehaviourWithPriority>())
                {
                    c.update = clientUpdate;
                    c.predictionUpdate = clientPredictionUpdate;
                }
            }
            for (int i = 0; i < 5; ++i)
            {
                Assert.AreEqual(100, Netcode.Instance.GhostBehaviourTypeManager.GhostBehaviourInfos[typeof(GhostBehaviourA)].ScriptSortOrder);
                Assert.AreEqual(200, Netcode.Instance.GhostBehaviourTypeManager.GhostBehaviourInfos[typeof(GhostBehaviourB)].ScriptSortOrder);
                Assert.AreEqual(100, Netcode.Instance.GhostBehaviourTypeManager.GhostBehaviourInfos[typeof(GhostBehaviourA)].ScriptSortOrder);
                Assert.AreEqual(200, Netcode.Instance.GhostBehaviourTypeManager.GhostBehaviourInfos[typeof(GhostBehaviourB)].ScriptSortOrder);
                //bucket  0 - > default bucket
                //bucket  1 - > first bucket (that in this case is 100)
                //bucket  2 - > first bucket (that in this case is 200)
                int bhvrAIndex = 0;
                int bhvrBIndex = 1;
                var allServerInfo = testWorld.ServerWorld.EntityManager.GetComponentData<GhostBehaviour.GhostBehaviourTracking>(serverObjects[i].Entity).allBehaviourTypeInfo;
                var allClientInfo = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostBehaviour.GhostBehaviourTracking>(clientObjects[i].Entity).allBehaviourTypeInfo;

                // TODO-next@sanity this test is flaky, any sample side GhostBehaviour with a sort between 100 and 200 will make this test fail. For our own sanity, we should have a way to only include scripts relevant to a given test. same as we're doing with systems.
                var baseBucket = allServerInfo[bhvrAIndex].UpdateBucket;
                Assert.That(baseBucket, Is.Not.EqualTo(0));
                Assert.AreEqual(baseBucket + 1, allServerInfo[bhvrBIndex].UpdateBucket);
                Assert.AreEqual(baseBucket, allClientInfo[bhvrAIndex].UpdateBucket);
                Assert.AreEqual(baseBucket + 1, allClientInfo[bhvrBIndex].UpdateBucket);
            }
            update.Clear();
            predictionUpdate.Clear();
            clientUpdate.Clear();
            clientPredictionUpdate.Clear();
            await testWorld.TickMultipleAsync(1);

            //server expect: 5 mono1, 5 mono2, 5 mono3
            Assert.AreEqual(5*2, update.Count);
            Assert.AreEqual(5*2, predictionUpdate.Count);
            for (int i = 0; i < update.Count; ++i)
            {
                if(i<5)
                    Assert.IsTrue(update[i].GetType() == typeof(GhostBehaviourA));
                else if(i<10)
                    Assert.IsTrue(update[i].GetType() == typeof(GhostBehaviourB));
            }
            for (int i = 0; i < predictionUpdate.Count; ++i)
            {
                if(i<5)
                    Assert.IsTrue(predictionUpdate[i].GetType() == typeof(GhostBehaviourA));
                else if(i<10)
                    Assert.IsTrue(predictionUpdate[i].GetType() == typeof(GhostBehaviourB));
            }
            Assert.AreEqual(5*2, clientUpdate.Count);
            for (int i = 0; i < update.Count; ++i)
            {
                if(i<5)
                    Assert.IsTrue(clientUpdate[i].GetType() == typeof(GhostBehaviourA));
                else if(i<10)
                    Assert.IsTrue(clientUpdate[i].GetType() == typeof(GhostBehaviourB));

            }
            //expect 4 prediction tick (2 for the latency + 2 for the slack)
            Assert.AreEqual(5*2*4, clientPredictionUpdate.Count);
            for (int i = 0; i < predictionUpdate.Count; ++i)
            {
                if(i<5)
                    Assert.IsTrue(clientPredictionUpdate[i].GetType() == typeof(GhostBehaviourA));
                else if(i<10)
                    Assert.IsTrue(clientPredictionUpdate[i].GetType() == typeof(GhostBehaviourB));
            }
        }

        [Test]
        public async Task TestCanCall_NetworkTime_FromPrediction()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("prediction");
            var serverHelper = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(4);
            var clientHelper = PredictionCallbackHelper.ClientInstances[0];

            void OnServerHelperOnOnPredictionEvent(GameObject o)
            {
                var helper = o.GetComponent<PredictionCallbackHelper>();
                var networkTime = helper.Ghost.World.EntityManager.CreateEntityQuery(typeof(NetworkTime)).GetSingleton<NetworkTime>();
                Assert.AreEqual(networkTime.ServerTick, Netcode.Time.ServerTick);
                Assert.AreEqual(networkTime.IsInPredictionLoop, Netcode.Time.IsInPredictionLoop);
            }

            serverHelper.OnPredictionEvent += OnServerHelperOnOnPredictionEvent;
            clientHelper.OnPredictionEvent += OnServerHelperOnOnPredictionEvent;
            await testWorld.TickMultipleAsync(30);
        }

        [Test]
        public async Task TestDisabledBehaviour_StillWaitsForStart()
        {
            // The didStart check for running prediction means if a GhostBehaviour starts disabled and we enable it in the next prediction update from another ghost behaviour, we'd need to make sure the order is respected there, even if part of the ghost has already been activated. (so for example if GhostObject is enabled already and only this specific behaviour is disabled, we'd need to make sure it's the behaviour's didStart that's checked, and not the GhostObject's)

            await using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 100;
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true, maxSteps: 100);
            var prefabMonobehaviour = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("prediction");
            var activator = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("activator");

            bool serverPredictionCalled = false;
            bool serverStartCalled = false;
            bool clientPredictionCalled = false;
            bool clientStartCalled = false;

            prefabMonobehaviour.CallbackHolder.OnPrediction += o =>
            {
                var ghost = o.GetComponent<GhostObject>();
                if (ghost.IsServer)
                {
                    serverPredictionCalled = true;
                    Assert.IsTrue(serverStartCalled);
                }
                if (ghost.IsClient) // both can be true for host
                {
                    clientPredictionCalled = true;
                    Assert.IsTrue(clientStartCalled);
                }
            };
            prefabMonobehaviour.CallbackHolder.OnStart += o =>
            {
                var ghost = o.GetComponent<GhostObject>();
                if (ghost.IsServer)
                {
                    serverStartCalled = true;
                    Assert.IsFalse(serverPredictionCalled);
                }
                if (ghost.IsClient) // both can be true for host
                {
                    clientStartCalled = true;
                    Assert.IsFalse(clientPredictionCalled);
                }
            };
            prefabMonobehaviour.enabled = false;
            var serverHelper = GameObject.Instantiate(prefabMonobehaviour);
            await testWorld.TickMultipleAsync(32);
            var clientHelper = GameObject.FindObjectsByType<PredictionCallbackHelper>(findObjectsInactive: FindObjectsInactive.Include).Where(ghost => !ghost.Ghost.IsPrefab()).First(o => o.Ghost.IsClient);

            Assert.IsFalse(clientStartCalled);
            Assert.IsFalse(clientPredictionCalled);
            Assert.IsFalse(serverStartCalled);
            Assert.IsFalse(serverPredictionCalled);

            var serverActivator = GameObject.Instantiate(activator);
            await testWorld.TickMultipleAsync(32);
            var clientActivator = PredictionCallbackHelper.ClientInstances[1];
            // another ghost sets this ghost behaviour enabled during prediction.
            clientActivator.OnPredictionEvent += o =>
            {
                clientHelper.enabled = true;
            };

            await testWorld.TickMultipleAsync(2);

            Assert.IsTrue(clientStartCalled);
            Assert.IsTrue(clientPredictionCalled);
        }

        [Test(Description = "GameObject version of the SingleWorldHostTests.SingleWorldHost_Interpolation_Works ECS version")]
        public async Task SingleWorldHost_Interpolation_Works([Values] SingleWorldHostInterpolationMode mode)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(serverCount:0, clientCount:0, singleWorldHostCount:1);
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("host smooth", autoRegister: false);
            var ghostPrefab = prefab.GetComponent<GhostObject>();
            ghostPrefab.SingleWorldHostInterpolationSmoothing = mode;
            ghostPrefab.DefaultGhostMode = GhostMode.Predicted;
            Netcode.RegisterPrefab(ghostPrefab.gameObject);

            var settingsEntity = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            var clientServerTickRate = new ClientServerTickRate();
            clientServerTickRate.ResolveDefaults();
            clientServerTickRate.SimulationTickRate = 20;
            testWorld.ServerWorld.EntityManager.SetComponentData(settingsEntity, clientServerTickRate);

            await testWorld.ConnectAsync(enableGhostReplication: true, dt: clientServerTickRate.SimulationFixedTimeStep);

            Assert.AreEqual(20, testWorld.GetSingleton<ClientServerTickRate>(testWorld.ServerWorld).SimulationTickRate);

            await testWorld.TickMultipleAsync(3); // position ourselves at the same starting point as the ECS version of the test

            var serverGO = GameObject.Instantiate(prefab);

            serverGO.OnPredictionEvent += o =>
            {
                o.transform.localPosition += Vector3.one * Netcode.DeltaTime;
                o.transform.Rotate(Vector3.up, math.radians(Netcode.DeltaTime));
            };

            var hasSmoothComponent = testWorld.ServerWorld.EntityManager.HasComponent<NetcodeSmoothHostLocalToWorld>(serverGO.Ghost.Entity);
            Assert.That(hasSmoothComponent, Is.EqualTo(mode == SingleWorldHostInterpolationMode.Interpolate), $"NetcodeSmoothHostLocalToWorld presence should match mode={mode}");

            await SingleWorldHostSharedTest.ValidateHostInterpolation(testWorld, mode, () => serverGO.Ghost.GhostTransform.Value,
                () => new LocalTransform() { Position = serverGO.transform.position, Rotation = serverGO.transform.rotation});
        }

        [Test(Description = "There's certain conditions where go transform is overriden by smoothing, but in other cases, the go transform should still be the authority when outside the prediction loop")]
        public async Task MakeSureTransformChanges_OutsidePrediction_AreStillApplied([Values] SingleWorldHostInterpolationMode mode)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("ghost", autoRegister: false);

            var ghostPrefab = prefab.GetComponent<GhostObject>();
            ghostPrefab.SingleWorldHostInterpolationSmoothing = mode;
            ghostPrefab.DefaultGhostMode = GhostMode.Predicted;
            Netcode.RegisterPrefab(ghostPrefab.gameObject);

            var server = GameObject.Instantiate(prefab);

            // test is currently right after Update(), so we do an authoritative position change which should "stick" and not get overriden by presentation systems
            server.transform.position = Vector3.one;

            await testWorld.TickMultipleAsync(4);
            var client = PredictionCallbackHelper.ClientInstances[0];
            if (mode == SingleWorldHostInterpolationMode.Interpolate && testWorld.ServerWorld.IsHost())
            {
                // host smoothing should have overriden the position and reset it back to the authoritative position
                Assert.That(server.transform.position, Is.EqualTo(Vector3.zero), "server position");
                Assert.That(client.transform.position, Is.EqualTo(Vector3.zero), "client position");
            }
            else
            {
                Assert.That(server.transform.position, Is.EqualTo(Vector3.one), "server position");
                Assert.That(client.transform.position, Is.EqualTo(Vector3.one), "client position");
            }

            server.Ghost.Position = Vector3.one * 2;
            // normally the GO transform is the source of truth. So we need to publish manually in order to get that change to stick
            server.Ghost.PublishTransform();
            await testWorld.TickMultipleAsync(3);

            Assert.That(server.transform.position, Is.EqualTo(Vector3.one * 2), "server position");
            Assert.That(client.transform.position, Is.EqualTo(Vector3.one * 2), "client position");
        }

        [Test(Description = "A GhostBehaviour on a child GameObject belongs to the root GhostObject: it must get PredictionUpdate called and its GhostFields replicated exactly like a root behaviour.")]
        public async Task NestedGhostBehaviour_Prediction_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);

            var prefab = GhostObjectUtils.CreateNestedPredictionCallbackHelperPrefab("NestedPrediction");

            var serverRoot = Object.Instantiate(prefab);
            var serverHelper = serverRoot.GetComponentInChildren<PredictionCallbackHelper>();
            await testWorld.TickMultipleAsync(4);
            var clientHelper = PredictionCallbackHelper.ClientInstances[0];
            var clientRoot = clientHelper.Ghost.gameObject;
            Assert.That(clientHelper.Ghost.World.IsClient());

            // The behaviour really is nested, and its Ghost resolved to this instance's root rather than the prefab's.
            Assert.That(serverHelper.gameObject, Is.Not.SameAs(serverRoot), "server behaviour should be on the child");
            Assert.That(serverHelper.Ghost, Is.SameAs(serverRoot.GetComponent<GhostObject>()), "server nested behaviour should point at its own root");
            Assert.That(clientHelper.gameObject, Is.Not.SameAs(clientRoot), "client behaviour should be on the child");
            Assert.That(clientHelper.Ghost, Is.SameAs(clientRoot.GetComponent<GhostObject>()), "client nested behaviour should point at its own root");

            // The nested behaviour was picked up by GhostObject's GetComponentsInChildren discovery.
            var serverInfo = testWorld.ServerWorld.EntityManager.GetComponentData<GhostBehaviour.GhostBehaviourTracking>(serverHelper.Ghost.Entity).allBehaviourTypeInfo;
            var clientInfo = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostBehaviour.GhostBehaviourTracking>(clientHelper.Ghost.Entity).allBehaviourTypeInfo;
            Assert.AreEqual(1, serverInfo.Length, "server should track the nested GhostBehaviour");
            Assert.AreEqual(1, clientInfo.Length, "client should track the nested GhostBehaviour");

            void OnPrediction(GameObject go)
            {
                var callbackHelper = go.GetComponent<PredictionCallbackHelper>();
                callbackHelper.SomeGhostField.Value += 1;
            }

            serverHelper.OnPredictionEvent += OnPrediction;
            clientHelper.OnPredictionEvent += OnPrediction;

            await testWorld.TickMultipleAsync(30);

            // Same counts as TestBasicPredictionWorks: the client runs 4 extra predicted replay ticks.
            Assert.AreEqual(30, serverHelper.SomeGhostField.Value);
            Assert.AreEqual(30 + 4, clientHelper.SomeGhostField.Value);

            // GhostFields declared on a nested behaviour still replicate.
            serverHelper.ClearEvents();
            clientHelper.ClearEvents();
            serverHelper.SomeGhostField.Value = 123;
            await testWorld.TickMultipleAsync(8);
            Assert.AreEqual(123, clientHelper.SomeGhostField.Value, "nested GhostField should have replicated to the client");
        }
    }
}
