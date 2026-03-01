#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Tests
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

            var prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "Prediction", autoRegister: false, typeof(PredictionTestBehaviour));
            var authoring = prefab.GetComponent<GhostAdapter>();
            authoring.SupportedGhostModes = GhostModeMask.All;
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.HasOwner = true;
            authoring.SupportAutoCommandTarget = true;
            Netcode.RegisterPrefab(prefab);

            var serverObj = GameObject.Instantiate(prefab).GetComponent<PredictionTestBehaviour>();
            serverObj.Ghost.OwnerNetworkId = Netcode.Client.Connection.NetworkId;
            serverObj.name = "PredictionObjectForTest";
            await testWorld.TickMultipleAsync(6);
            var clientObj = GameObject.FindObjectsByType<PredictionTestBehaviour>(FindObjectsSortMode.None).First(x => x != serverObj);
            Assert.That(clientObj.Ghost.World.IsClient());

            Assert.That(serverObj.Ghost.OwnerNetworkId, Is.EqualTo(Netcode.Client.Connection.NetworkId));
            Assert.That(clientObj.Ghost.OwnerNetworkId, Is.EqualTo(Netcode.Client.Connection.NetworkId));

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

            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("exception in prediction loop");

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

            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("exception in prediction loop", autoRegister: false);
            prefab.GetComponent<GhostAdapter>().HasOwner = true;

            Netcode.RegisterPrefab(prefab.gameObject);
            var serverObj = GameObject.Instantiate(prefab);
            var serverObj2 = GameObject.Instantiate(prefab);
            serverObj.GetComponent<GhostAdapter>().OwnerNetworkId = new NetworkId() { Value = 1 };
            serverObj2.GetComponent<GhostAdapter>().OwnerNetworkId = new NetworkId() { Value = 1 };
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
                if (self.GetComponent<GhostAdapter>().NetworkTime.IsFirstTimeFullyPredictingTick)
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
                LogAssert.Expect(LogType.Exception, "Exception: " + exceptionToExpect);
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
            var prefabYesYes = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("prediction test yesyes", autoRegister: false);
            prefabYesYes.gameObject.AddComponent<GhostBehaviourA>();

            // 2 GhostBehaviours, only 1 with a PredictionUpdate
            var prefabYesNo = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("prediction test yesno", autoRegister: false);
            prefabYesNo.gameObject.AddComponent<BehaviourAllData>();

            // 1 GhostBehaviour, no PredictionUpdate
            var prefabNo = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("prediction test no", autoRegister: false).gameObject;
            Object.DestroyImmediate(prefabNo.GetComponent<PredictionCallbackHelper>(), allowDestroyingAssets: true);
            prefabNo.gameObject.AddComponent<BehaviourAllData>();

            // No GhostBehaviour, just a GhostObject
            var prefabZeroBehaviour = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("prediction test zero", autoRegister: false).gameObject;
            Object.DestroyImmediate(prefabZeroBehaviour.GetComponent<PredictionCallbackHelper>(), allowDestroyingAssets: true);

            Netcode.RegisterPrefab(prefabYesYes.gameObject);
            Netcode.RegisterPrefab(prefabYesNo.gameObject);
            Netcode.RegisterPrefab(prefabNo.gameObject);
            Netcode.RegisterPrefab(prefabZeroBehaviour.gameObject);

            var serverZero = GameObject.Instantiate(prefabZeroBehaviour);
            await testWorld.TickMultipleAsync(4);
            var clientZero = GameObject.FindObjectsByType<GhostAdapter>(sortMode: FindObjectsSortMode.None).First(ghost => ghost.IsClient);

            var serverNo = GameObject.Instantiate(prefabNo).GetComponent<BehaviourAllData>();
            await testWorld.TickMultipleAsync(4);
            var clientNo = GameObject.FindObjectsByType<BehaviourAllData>(sortMode: FindObjectsSortMode.None).First(data => data.Ghost.IsClient);

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

        [Test(Description = "make sure that enabling a GhostBehaviour mid replay does indeed reenable its prediction update (and that it doesn't stay stuck disabled)")]
        public async Task TestEnableDisable_GhostBehaviour_DifferentTick_SameFrame()
        {
            await using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 100;
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true, maxSteps: 100);

            // 2 GhostBehaviour each with a PredictionUpdate
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("PredHelper", autoRegister: true);

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

        [Test]
        public async Task PredictionUpdateRespectScriptExecutionOrder()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "Prediction", autoRegister: false,
                typeof(GhostBehaviourA), typeof(GhostBehaviourB));
            var authoring = prefab.GetComponent<GhostAdapter>();
            authoring.SupportedGhostModes = GhostModeMask.All;
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.HasOwner = true;
            authoring.SupportAutoCommandTarget = false;
            Netcode.RegisterPrefab(prefab);
            await testWorld.ConnectAsync(enableGhostReplication: true);

            var update = new List<MonoBehaviour>();
            var predictionUpdate = new List<MonoBehaviour>();
            var serverObjects = new GhostAdapter[5];
            for (int i = 0; i < 5; ++i)
            {
                var serverObj = GameObject.Instantiate(prefab).GetComponent<GhostBehaviourWithPriority>();
                foreach (var c in serverObj.GetComponents<GhostBehaviourWithPriority>())
                {
                    c.update = update;
                    c.predictionUpdate = predictionUpdate;
                }
                //awake the GhostBehaviours and GhostAdapter, because mock start as disabled.
                serverObj.Ghost.OwnerNetworkId = Netcode.Client.Connection.NetworkId;
                serverObj.name = $"Ghost{i}";
                serverObjects[i] = serverObj.Ghost;
            }
            //Run some ticks to get everything stable. So on average the client should do 4 tick of prediction later
            await testWorld.TickMultipleAsync(32);

            var clientUpdate = new List<MonoBehaviour>();
            var clientPredictionUpdate = new List<MonoBehaviour>();
            //we should have 5 ghost client-side
            var clientObjects = GameObject.FindObjectsByType<GhostAdapter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(go=>go.World.IsClient()).ToArray();
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
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("prediction");
            var serverHelper = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(4);
            var clientHelper = PredictionCallbackHelper.ClientInstances[0];

            void OnServerHelperOnOnPredictionEvent(GameObject o)
            {
                var helper = o.GetComponent<PredictionCallbackHelper>();
                var networkTime = helper.Ghost.World.EntityManager.CreateEntityQuery(typeof(NetworkTime)).GetSingleton<NetworkTime>();
                Assert.AreEqual(networkTime.ServerTick, Netcode.NetworkTime.ServerTick);
                Assert.AreEqual(networkTime.IsInPredictionLoop, Netcode.NetworkTime.IsInPredictionLoop);
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
            var prefabMonobehaviour = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("prediction");
            var activator = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("activator");

            bool serverPredictionCalled = false;
            bool serverStartCalled = false;
            bool clientPredictionCalled = false;
            bool clientStartCalled = false;

            prefabMonobehaviour.CallbackHolder.OnPrediction += o =>
            {
                if (o.GetComponent<GhostAdapter>().IsServer)
                {
                    serverPredictionCalled = true;
                    Assert.IsTrue(serverStartCalled);
                }
                else
                {
                    clientPredictionCalled = true;
                    Assert.IsTrue(clientStartCalled);
                }
            };
            prefabMonobehaviour.CallbackHolder.OnStart += o =>
            {
                if (o.GetComponent<GhostAdapter>().IsServer)
                {
                    serverStartCalled = true;
                    Assert.IsFalse(serverPredictionCalled);
                }
                else
                {
                    clientStartCalled = true;
                    Assert.IsFalse(clientPredictionCalled);
                }
            };
            prefabMonobehaviour.enabled = false;
            var serverHelper = GameObject.Instantiate(prefabMonobehaviour);
            await testWorld.TickMultipleAsync(32);
            var clientHelper = GameObject.FindObjectsByType<PredictionCallbackHelper>(sortMode: FindObjectsSortMode.None, findObjectsInactive: FindObjectsInactive.Include).First(o => o.Ghost.IsClient);

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
    }
}
#endif


