#if UNITY_EDITOR

using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Burst.Intrinsics;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Tests
{
    internal class GameObjectGhostFieldTests
    {
        // A few tests here assume a worker count > 1. Setting this up here in case your local dev was setup with jobs disabled.
        int m_SavedWorkerCount;
        [SetUp]
        public void Setup()
        {
            m_SavedWorkerCount = JobsUtility.JobWorkerCount;
            JobsUtility.JobWorkerCount = 2;
        }

        [TearDown]
        public void Teardown()
        {
            JobsUtility.JobWorkerCount = m_SavedWorkerCount;
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        [Category(NetcodeTestCategories.Smoke)]
        public async Task BasicGameObjectGhostFieldOperations_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");

            const int toTest = 321;

            var serverObj = GameObject.Instantiate(prefab);
            // test update value right after spawn
            serverObj.SomeGhostField.Value = toTest;
            var value = serverObj.SomeBridgedVar.Value;
            value.value = toTest;
            serverObj.SomeBridgedVar.Value = value;

            Assert.IsTrue(serverObj.SomeGhostField.Value == toTest);
            Assert.IsTrue(serverObj.SomeBridgedVar.Value.value == toTest);

            Assert.AreEqual(serverObj.Ghost.Entity, serverObj.SomeGhostField.m_Entity);
            Assert.AreEqual(serverObj.Ghost.World.Unmanaged.SequenceNumber, serverObj.SomeGhostField.m_World.SequenceNumber);
            Assert.IsTrue(serverObj.Ghost.IsServer);

            await testWorld.TickMultipleAsync(4);

            var clientObj = PredictionCallbackHelper.ClientInstances[0];

            Assert.AreEqual(clientObj.Ghost.Entity, clientObj.SomeGhostField.m_Entity);
            Assert.AreEqual(clientObj.Ghost.World.Unmanaged.SequenceNumber, clientObj.SomeGhostField.m_World.SequenceNumber);
            Assert.IsTrue(clientObj.Ghost.World.IsClient());

            Assert.AreEqual(toTest, serverObj.SomeGhostField.Value);
            Assert.AreEqual(toTest, serverObj.SomeBridgedVar.Value.value);
            Assert.AreEqual(toTest, clientObj.SomeGhostField.Value);
            Assert.AreEqual(toTest, clientObj.SomeBridgedVar.Value.value);

            // update existing value
            serverObj.SomeGhostField.Value = toTest * 2;
            var bridgedValue = serverObj.SomeBridgedVar.Value;
            bridgedValue.value = toTest * 2;
            serverObj.SomeBridgedVar.Value = bridgedValue;
            Assert.IsTrue(serverObj.SomeGhostField.Value == toTest * 2);
            Assert.IsTrue(serverObj.SomeBridgedVar.Value.value == toTest * 2);

            await testWorld.TickMultipleAsync(4);
            Assert.AreEqual(toTest * 2, serverObj.SomeGhostField.Value);
            Assert.AreEqual(toTest * 2, serverObj.SomeBridgedVar.Value.value);
            Assert.AreEqual(toTest * 2, clientObj.SomeGhostField.Value);
            Assert.AreEqual(toTest * 2, clientObj.SomeBridgedVar.Value.value);
        }

        [Test(Description = "We're getting expected values at spawn")]
        public async Task InitialValues_AtSpawn_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication
            await testWorld.TickMultipleAsync(1);

            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");
            await testWorld.TickMultipleAsync(1); // let some time for the client to ack the new ghost type
            var serverObj = GameObject.Instantiate(prefab);

            await testWorld.TickMultipleAsync(3);
            Assert.AreEqual(0, serverObj.SomeGhostField.Value);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];
            Assert.AreEqual(0, clientObj.SomeGhostField.Value);

            GameObject.Destroy(serverObj.gameObject);
            await testWorld.TickMultipleAsync(3);
            Assert.AreEqual(0, PredictionCallbackHelper.ClientInstances.Count, "sanity check failed");
            serverObj = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(2);
            Assert.AreEqual(0, PredictionCallbackHelper.ClientInstances.Count, "sanity check failed");

            await testWorld.TickMultipleAsync(1);
            clientObj = PredictionCallbackHelper.ClientInstances[0];
            Assert.AreEqual(0, clientObj.SomeGhostField.Value);
            serverObj.SomeGhostField.Value = 123;
            await testWorld.TickMultipleAsync(1);
            Assert.AreEqual(123, clientObj.SomeGhostField.Value);
        }

        [Test]
        public async Task Behaviour_WithVariousTypes_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData", autoRegister: false);
            prefab.gameObject.AddComponent<VariousDataTypesForStateSync>();
            Netcode.RegisterPrefab(prefab.gameObject);
            await testWorld.TickMultipleAsync(1);

            var serverObj = GameObject.Instantiate(prefab).GetComponent<VariousDataTypesForStateSync>();

            serverObj.ValidateValues(expected: 0, expectedQuantized: 0f, nonReplicatedExpected: 0);
            await testWorld.TickMultipleAsync(1);
            serverObj.ValidateValues(expected: 0, expectedQuantized: 0f, nonReplicatedExpected: 0);

            await testWorld.TickMultipleAsync(3);
            var clientObj = PredictionCallbackHelper.ClientInstances[0].GetComponent<VariousDataTypesForStateSync>();
            clientObj.ValidateValues(expected: 0, expectedQuantized: 0f, nonReplicatedExpected: 0);

            serverObj.IncrementValues();
            serverObj.ValidateValues(expected: 1, expectedQuantized: 0.1f, nonReplicatedExpected: 1);
            await testWorld.TickMultipleAsync(1); // state sync takes 1 tick in tests
            clientObj.ValidateValues(expected: 1, expectedQuantized: 0f, nonReplicatedExpected: 0);
            serverObj.ValidateValues(expected: 1, expectedQuantized: 0.1f, nonReplicatedExpected: 1);

            // test quantization
            for (int i = 0; i < 10; i++)
            {
                serverObj.IncrementValues();
            }
            serverObj.ValidateValues(expected: 11, expectedQuantized: 1.1f, nonReplicatedExpected: 11);

            await testWorld.TickMultipleAsync(6);
            serverObj.ValidateValues(expected: 11, expectedQuantized: 1.1f, nonReplicatedExpected: 11); // shouldn't have changed
            clientObj.ValidateValues(expected: 11, expectedQuantized: 1f, nonReplicatedExpected: 0);
        }

        [Test(Description = "make sure GhostBehaviour inheriting from another still works as expected")]
        public async Task BehaviourInheritance_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication

            var prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "InheritanceTest", typeof(GhostBehaviourInheritance)); // interpolated ghost
            await testWorld.TickMultipleAsync(1);

            var serverObj = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(6);
            var serverBehaviour = serverObj.GetComponent<GhostBehaviourInheritance>();
            serverBehaviour.SomeExtraVar.Value = 123;
            serverBehaviour.SomeGhostField.Value = 321;
            serverBehaviour.SomeBridgedVar.Value = new SomeBridgedValue() { value = 999 };
            await testWorld.TickMultipleAsync(4);
            var clientBehaviour = GhostBehaviourInheritance.ClientInstances[0] as GhostBehaviourInheritance;
            Assert.AreEqual(123, clientBehaviour.SomeExtraVar.Value);
            Assert.AreEqual(321, clientBehaviour.SomeGhostField.Value);
            Assert.AreEqual(999, clientBehaviour.SomeBridgedVar.Value.value);

        }

        enum PerMethodValue : int
        {
            Before = 123,
            kAwakeValue = 100,
            kStartValue = 200,
            kUpdateValue = 300,
            kFixedUpdateValue = 400, // trying to test FixedUpdate in sequence with other events is hard since it can execute 0, 1 or many times. Testing it on its own
            kLateUpdateValue = 500,
            kOnDestroyValue = 600,
            kOnEnableValue = 700,
            LatestValue = 1000,
        }

        [Test(Description = "test accessing data from different monobehaviour callbacks")]
        public async Task AccessGhostData_FromMonobehaviourMethods()
        {
            //  like awake, start, OnDestroy, Update, FixedUpdate
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true); // this does a lot of the boilerplate of connecting, ticking, enabling replication

            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");
            await testWorld.TickAsync();

            int testCount = 0;
            void TestServerMethods(GameObject o, PerMethodValue oldValueToCheck, PerMethodValue newValue)
            {
                var callbackHelper = o.GetComponent<PredictionCallbackHelper>();
                if (!callbackHelper.Ghost.IsServer) return; // we test client side later, this is a server test
                testCount++;
                PerMethodValue oldValueVar = (PerMethodValue)callbackHelper.SomeGhostField.Value;
                PerMethodValue oldValueBridge = (PerMethodValue)callbackHelper.SomeBridgedVar.Value.value;

                // assign before the asserts, so we don't have chaining errors if there's one now
                callbackHelper.SomeGhostField.Value = (int)newValue;
                var value = callbackHelper.SomeBridgedVar.Value;
                value.value = (int)newValue;
                callbackHelper.SomeBridgedVar.Value = value;

                Assert.AreEqual(oldValueToCheck, oldValueVar);
                Assert.AreEqual(oldValueToCheck, oldValueBridge);
            }
            prefab.CallbackHolder.OnAwake += o =>
            {
                TestServerMethods(o, 0, PerMethodValue.kAwakeValue);
            };
            prefab.CallbackHolder.OnEnableEvent += o =>
            {
                TestServerMethods(o, PerMethodValue.kAwakeValue, PerMethodValue.kOnEnableValue);
            };
            prefab.CallbackHolder.OnStart += o =>
            {
                TestServerMethods(o, PerMethodValue.kOnEnableValue, PerMethodValue.kStartValue);
            };

            var serverObj = GameObject.Instantiate(prefab);
            Assert.AreEqual(2, testCount, "sanity check failed, not all initialization methods executed");
            await testWorld.TickAsync();
            Assert.AreEqual(3, testCount, "sanity check failed, not all initialization methods executed");

            bool executed = false;
            PerMethodValue valueBefore = PerMethodValue.Before;
            void TestDuring(GameObject o, PerMethodValue newValue)
            {
                executed = true;
                var callbackHelper = o.GetComponent<PredictionCallbackHelper>();
                PerMethodValue oldValueVar = (PerMethodValue)callbackHelper.SomeGhostField.Value;
                PerMethodValue oldValueBridge = (PerMethodValue)callbackHelper.SomeBridgedVar.Value.value;
                Assert.AreEqual(valueBefore, oldValueVar);
                Assert.AreEqual(valueBefore, oldValueBridge);
                callbackHelper.SomeGhostField.Value = (int)newValue;
                var value = callbackHelper.SomeBridgedVar.Value;
                value.value = (int)newValue;
                callbackHelper.SomeBridgedVar.Value = value;
            }

            void TestAfter(PredictionCallbackHelper o, PerMethodValue expected)
            {
                Assert.IsTrue(executed, $"sanity check failed, didn't execute expected method {expected}");
                executed = false;
                var callbackHelper = o.GetComponent<PredictionCallbackHelper>();
                PerMethodValue oldValueVar = (PerMethodValue)callbackHelper.SomeGhostField.Value;
                PerMethodValue oldValueBridge = (PerMethodValue)callbackHelper.SomeBridgedVar.Value.value;
                Assert.AreEqual(expected, oldValueVar);
                Assert.AreEqual(expected, oldValueBridge);
            }

            void ResetStateToBefore()
            {
                serverObj.SomeGhostField.Value = (int)PerMethodValue.Before;
                var bridgedValue = serverObj.SomeBridgedVar.Value;
                bridgedValue.value = (int)PerMethodValue.Before;
                serverObj.SomeBridgedVar.Value = bridgedValue;
            }
            async Task TestForAndTick(PerMethodValue value, NetcodeAwaitable awaitable)
            {
                ResetStateToBefore();
                await testWorld.TickAsync(waitInstruction: awaitable);
                TestAfter(serverObj, value);
                serverObj.ClearEvents();
            }

            serverObj.OnUpdate += o => TestDuring(o, PerMethodValue.kUpdateValue);
            await TestForAndTick(PerMethodValue.kUpdateValue, Awaitable.NextFrameAsync());
            serverObj.OnLateUpdate += o => TestDuring(o, PerMethodValue.kLateUpdateValue);
            await TestForAndTick(PerMethodValue.kLateUpdateValue, Awaitable.NextFrameAsync());
            // fixed update is special, since it can run 0 to n times per frame, so we need to await differently
            serverObj.OnFixedUpdate += o =>
            {
                TestDuring(o, PerMethodValue.kFixedUpdateValue);
                valueBefore = PerMethodValue.kFixedUpdateValue; // since FixedUpdate can execute n times, we make sure the following tests check for what we just set above
            };
            await TestForAndTick(PerMethodValue.kFixedUpdateValue, Awaitable.FixedUpdateAsync());

            serverObj.ClearEvents();
            var someBridgedValue = serverObj.SomeBridgedVar.Value;
            someBridgedValue.value = (int)PerMethodValue.LatestValue;
            serverObj.SomeBridgedVar.Value = someBridgedValue;
            serverObj.SomeGhostField.Value = (int)PerMethodValue.LatestValue;
            await testWorld.TickMultipleAsync(4); // make sure everything is spawned and synced

            // Check client side events
            Assert.AreEqual(1, PredictionCallbackHelper.ClientInstances.Count, "sanity check failed, client instance count");

            // check state access while OnDestroy
            ResetStateToBefore();
            serverObj.OnDestroyEvent += o =>
            {
                TestServerMethods(o, PerMethodValue.Before, PerMethodValue.kOnDestroyValue);
            };
            GameObject.Destroy(serverObj.gameObject);
            await testWorld.TickMultipleAsync(4);
            Assert.AreEqual(0, PredictionCallbackHelper.ClientInstances.Count, "sanity check failed, client instance count");

            // test client side
            serverObj = GameObject.Instantiate(prefab);
            serverObj.ClearEvents();
            serverObj.SomeGhostField.Value = (int)PerMethodValue.LatestValue;
            var value1 = serverObj.SomeBridgedVar.Value;
            value1.value = (int)PerMethodValue.LatestValue;
            serverObj.SomeBridgedVar.Value = value1;

            int clientExecutionCount = 0;
            void TestClientCanRead(GameObject o)
            {
                var callbackHelper = o.GetComponent<PredictionCallbackHelper>();
                if (callbackHelper.Ghost.IsServer) return;
                clientExecutionCount++;
                PerMethodValue currentValueVar = (PerMethodValue)callbackHelper.SomeGhostField.Value;
                PerMethodValue currentValueBridge = (PerMethodValue)callbackHelper.SomeBridgedVar.Value.value;
                Assert.AreEqual(PerMethodValue.LatestValue, currentValueBridge);
                Assert.AreEqual(PerMethodValue.LatestValue, currentValueVar);
            }

            prefab.CallbackHolder.OnAwake += o => TestClientCanRead(o);
            prefab.CallbackHolder.OnEnableEvent += o => TestClientCanRead(o);
            prefab.CallbackHolder.OnStart += o => TestClientCanRead(o);
            await testWorld.TickMultipleAsync(4);

            var clientObj = PredictionCallbackHelper.ClientInstances[0];

            clientObj.OnUpdate += o => TestClientCanRead(o);
            clientObj.OnLateUpdate += o => TestClientCanRead(o);

            await testWorld.TickAsync();
            Assert.AreEqual(5, clientExecutionCount); // awake, enable, start, update, lateupdate
            clientObj.ClearEvents();

            clientExecutionCount = 0;
            clientObj.OnFixedUpdate += o => TestClientCanRead(o);
            await testWorld.TickAsync(waitInstruction: Awaitable.FixedUpdateAsync());
            Assert.GreaterOrEqual(clientExecutionCount, 1);

            GameObject.Destroy(serverObj.gameObject);
            clientObj.OnDestroyEvent += o => TestClientCanRead(o);
            await testWorld.TickMultipleAsync(4);

            await testWorld.TickMultipleAsync(1); // extra tick at the end to make sure all logs are logged
        }

        [Test(Description = "Make sure appropriate errors are logged if doing wrong writes")]
        public async Task TestInvalidWrite_FailsElegantly()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");

            var serverHelper = GameObject.Instantiate(prefab);
            serverHelper.SomeGhostField.Value = 321;
            serverHelper.SomeBridgedVar.ValueAsRef.value = 321;
            await testWorld.TickMultipleAsync(4);
            var clientHelper = PredictionCallbackHelper.ClientInstances[0];
            Assert.AreEqual(321, clientHelper.SomeGhostField.Value, "sanity check failed");
            Assert.AreEqual(321, clientHelper.SomeBridgedVar.Value.value, "sanity check failed");

            // this is a predicted ghost, so we can write to the field client side
            clientHelper.SomeGhostField.Value = 123;
            clientHelper.SomeBridgedVar.ValueAsRef.value = 123;
            await testWorld.TickMultipleAsync(1);
            Assert.AreEqual(321, clientHelper.SomeGhostField.Value, "field should have been rolled back");
            Assert.AreEqual(321, clientHelper.SomeBridgedVar.Value.value, "field should have been rolled back");

            var interpolatedPrefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("interpolated ghost", autoRegister: false);
            interpolatedPrefab.Ghost.DefaultGhostMode = GhostMode.Interpolated;
            Netcode.RegisterPrefab(interpolatedPrefab.gameObject);
            var serverInterp = GameObject.Instantiate(interpolatedPrefab);
            serverInterp.SomeGhostField.Value = 888;
            serverInterp.SomeBridgedVar.ValueAsRef.value = 888;
            await testWorld.TickMultipleAsync(6);
            var clientInterp = PredictionCallbackHelper.ClientInstances[1];

            // Note: not handling ComponentRef case on purpose. This belongs to entities land (especially if entities introduce a ComponentRef) and N4E already has its own flow. Changing this would be a breaking change for existing users.
            Assert.Throws<UnityEngine.Assertions.AssertionException>(() =>
            {
                clientInterp.SomeGhostField.Value = 555;
            });
            Assert.AreEqual(888, clientInterp.SomeGhostField.Value, "value shouldn't have been written to!");
            await testWorld.TickMultipleAsync(1); // Systems are a pipeline, we need systems to execute to get the error on that data
            Assert.AreEqual(888, clientInterp.SomeBridgedVar.Value.value, "value shouldn't have been written to!");
            Assert.IsTrue(clientHelper.CanWriteState);
            Assert.IsTrue(serverHelper.CanWriteState);
            Assert.IsFalse(clientInterp.CanWriteState);
            Assert.IsTrue(serverInterp.CanWriteState);
        }

        [Test(Description = "Test that as long as the prefab is processed with the deterministic set of components, we can after that at runtime remove GhostBehaviours at will. This is useful for future stripping features")]
        public async Task TestCanRemove_GhostBehaviours_AtRuntime()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData", autoRegister: false);
            prefab.gameObject.AddComponent<SharedBridgeGhostBehaviourTest>();
            Netcode.RegisterPrefab(prefab.gameObject);
            var serverMonoBehaviour = GameObject.Instantiate(prefab);
            var serverGO = serverMonoBehaviour.gameObject;
            Object.Destroy(serverMonoBehaviour);
            await testWorld.TickMultipleAsync(4);
            var clientMonoBehaviour = PredictionCallbackHelper.ClientInstances[0];
            var clientGO = clientMonoBehaviour.gameObject;
            Object.Destroy(clientMonoBehaviour);

            await testWorld.TickMultipleAsync(4);

            // other GhostBehaviours continue working fine
            var otherServer = serverGO.GetComponent<SharedBridgeGhostBehaviourTest>();
            otherServer.bridge.Value = new SomeBridgedValue() { value = 111 };
            await testWorld.TickMultipleAsync(4);
            var otherClient = clientGO.GetComponent<SharedBridgeGhostBehaviourTest>();
            Assert.AreEqual(111, otherClient.bridge.Value.value);
        }

        [Test(Description = "make sure if we have two ghosts, their values are split and are not reusing the same underlying entity")]
        public async Task Test_NoOverlapBetweenEntities()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");

            var serverObj1 = GameObject.Instantiate(prefab);
            var serverObj2 = GameObject.Instantiate(prefab);
            serverObj1.SomeGhostField.Value = 123;
            serverObj2.SomeGhostField.Value = 111;
            serverObj1.SomeBridgedVar.Value = new SomeBridgedValue() { value = 456 };
            serverObj2.SomeBridgedVar.Value = new SomeBridgedValue() { value = 444 };
            Assert.AreEqual(123, serverObj1.SomeGhostField.Value);
            Assert.AreEqual(456, serverObj1.SomeBridgedVar.Value.value);
            Assert.AreEqual(111, serverObj2.SomeGhostField.Value);
            Assert.AreEqual(444, serverObj2.SomeBridgedVar.Value.value);

            await testWorld.TickMultipleAsync(4);
            var clientObj1 = PredictionCallbackHelper.ClientInstances[0];
            var clientObj2 = PredictionCallbackHelper.ClientInstances[1];
            if (clientObj1.SomeGhostField.Value != 123)
            {
                clientObj1 = clientObj2;
                clientObj2 = PredictionCallbackHelper.ClientInstances[0];
            }
            Assert.AreEqual(123, clientObj1.SomeGhostField.Value);
            Assert.AreEqual(456, clientObj1.SomeBridgedVar.Value.value);
            Assert.AreEqual(111, clientObj2.SomeGhostField.Value);
            Assert.AreEqual(444, clientObj2.SomeBridgedVar.Value.value);
        }

        [Test(Description = "Make sure GameObject GhostFields still respect change filter versions when writing to components")]
        public async Task TestChangeFiltering_StillWorks()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");

            var serverObj = GameObject.Instantiate(prefab);

            uint GetChangeVersion(ComponentType type)
            {
                var entityManager = testWorld.ServerWorld.EntityManager;
                var chunk = entityManager.GetChunk(serverObj.Ghost.Entity);
                var componentTypeHandle = entityManager.GetDynamicComponentTypeHandle(type);
                return chunk.GetChangeVersion(ref componentTypeHandle);
            }
            var previousVersion = GetChangeVersion(ComponentType.ReadOnly<SomeBridgedValue>());

            bool CheckChangedAndUpdateLastVersion(ComponentType type)
            {
                var entityManager = testWorld.ServerWorld.EntityManager;
                var chunk = entityManager.GetChunk(serverObj.Ghost.Entity);
                var componentTypeHandle = entityManager.GetDynamicComponentTypeHandle(type);
                var didChange = chunk.DidChange(ref componentTypeHandle, previousVersion);
                previousVersion = chunk.GetChangeVersion(ref componentTypeHandle);
                return didChange;
            }

            // Need to do one tick per ghost var access. Tick increments the global system version, else multiple writes in the same tick count as a single one.
            // This way, we validate that every single operation doesn't have rogue write version increases
            await testWorld.TickAsync();
            // check bridge
            Assert.IsFalse(CheckChangedAndUpdateLastVersion(ComponentType.ReadOnly<SomeBridgedValue>()), "sanity check failed, component shouldn't have been touched here");

            await testWorld.TickAsync();
            serverObj.SomeBridgedVar.Value = new SomeBridgedValue(){value = 123};
            Assert.IsTrue(CheckChangedAndUpdateLastVersion(ComponentType.ReadOnly<SomeBridgedValue>()));

            await testWorld.TickAsync();
            Assert.AreEqual(123, serverObj.SomeBridgedVar.Value.value);
            Assert.IsFalse(CheckChangedAndUpdateLastVersion(ComponentType.ReadOnly<SomeBridgedValue>()), "version was changed for a read only operation");

            await testWorld.TickAsync();
            serverObj.SomeGhostField.Value = 111;
            Assert.IsFalse(CheckChangedAndUpdateLastVersion(ComponentType.ReadOnly<SomeBridgedValue>()), "version was changed when the component shouldn't have been touched");


            // now check GhostField
            await testWorld.TickAsync();
            previousVersion = GetChangeVersion(serverObj.SomeGhostField.m_GeneratedWrapperComponentType);

            await testWorld.TickAsync();
            serverObj.SomeBridgedVar.Value = new SomeBridgedValue(){value = 123};

            await testWorld.TickAsync();
            Assert.IsFalse(CheckChangedAndUpdateLastVersion(serverObj.SomeGhostField.m_GeneratedWrapperComponentType), "ghost var component shouldn't have been touched here");

            await testWorld.TickAsync();
            serverObj.SomeGhostField.Value = 456;

            await testWorld.TickAsync();
            Assert.IsTrue(CheckChangedAndUpdateLastVersion(serverObj.SomeGhostField.m_GeneratedWrapperComponentType), "non tracked version change for ghost var, something is wrong with the setter, it doesn't apply version change");

            await testWorld.TickAsync();
            Assert.AreEqual(456, serverObj.SomeGhostField.Value);

            await testWorld.TickAsync();
            Assert.IsFalse(CheckChangedAndUpdateLastVersion(serverObj.SomeGhostField.m_GeneratedWrapperComponentType), "ghost var component shouldn't have been touched here, for a readonly access");
        }

        [Test]
        public async Task TestMultipleGhostField_OfSameType_BreaksElegantly()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData", autoRegister: false);
            prefab.gameObject.AddComponent<PredictionCallbackHelper>();

            var errorLog = new Regex("Having two GhostBehaviours with a shared sets of GhostField.*undefined behaviour");
            LogAssert.Expect(errorLog);
            LogAssert.Expect(errorLog); // twice, for client and server registration
            Netcode.RegisterPrefab(prefab.gameObject);

            await testWorld.TickMultipleAsync(30); // let it run a bit, see if there's errors at runtime
        }

        [Test(Description = "make sure that the ecs bridge acts in a more 'entities' way and allows sharing component data, on the same entity")]
        public async Task TestMultipleBridge_OfSameType_Works()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData", autoRegister: false);
            prefab.gameObject.AddComponent<SharedBridgeGhostBehaviourTest>();
            Netcode.RegisterPrefab(prefab.gameObject);
            var serverObj = GameObject.Instantiate(prefab);
            serverObj.SomeBridgedVar.Value = new SomeBridgedValue() { value = 321 };
            Assert.AreEqual(321, serverObj.gameObject.GetComponent<SharedBridgeGhostBehaviourTest>().bridge.Value.value);
            await testWorld.TickMultipleAsync(4);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];
            Assert.AreEqual(321, clientObj.SomeBridgedVar.Value.value);
            Assert.AreEqual(321, clientObj.gameObject.GetComponent<SharedBridgeGhostBehaviourTest>().bridge.Value.value);
        }

        public struct SomeRandomComponent : IComponentData
        {
            public float someValue;
        }
        [Test(Description = "Test various cases where the entity moves from a chunk to another, makes sure the internal pointer caching still gets refreshed correctly")]
        public async Task TestStructuralChanges_DontBreakGhostFields([Values] bool withExtraGhost)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");
            var serverObj = GameObject.Instantiate(prefab);
            if (withExtraGhost)
            {
                // the extra ghost means the internal cached chunk won't get invalidated when the first ghost moves out of it, leading to a different code path for pointer refresh
                GameObject.Instantiate(prefab);
            }
            await testWorld.TickMultipleAsync(4);
            serverObj.SomeGhostField.Value = 123;
            Assert.AreEqual(123, serverObj.SomeGhostField.Value);
            await testWorld.TickMultipleAsync(6);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];
            Assert.AreEqual(123, clientObj.SomeGhostField.Value, "sanity check failed");

            // structural change
            testWorld.ServerWorld.EntityManager.AddComponentData(serverObj.Ghost.Entity, new SomeRandomComponent(){someValue = 123.321f});

            // can still read after structural change
            Assert.AreEqual(123, serverObj.SomeGhostField.Value);

            testWorld.ServerWorld.EntityManager.RemoveComponent<SomeRandomComponent>(serverObj.Ghost.Entity);
            // can still write after structural change
            serverObj.SomeGhostField.Value = 456;
            Assert.AreEqual(456, serverObj.SomeGhostField.Value);

            // can still read, then write after structural change
            testWorld.ServerWorld.EntityManager.AddComponentData(serverObj.Ghost.Entity, new SomeRandomComponent(){someValue = 123.321f});
            Assert.AreEqual(456, serverObj.SomeGhostField.Value);
            serverObj.SomeGhostField.Value = 789;
            Assert.AreEqual(789, serverObj.SomeGhostField.Value);

            // replication still works
            Assert.AreEqual(123, clientObj.SomeGhostField.Value);
            await testWorld.TickMultipleAsync(6);
            Assert.AreEqual(789, clientObj.SomeGhostField.Value);
        }

        [Test]
        public async Task TestConcurrentAccess_DontBreakGhostFields()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(userSystems: new [] {
                typeof(TestConcurrentGhostFieldSystem), typeof(TestReadWriteAfterSystem), typeof(SequentialSystemsGroup)
            });
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");
            var serverObj = GameObject.Instantiate(prefab);

            // repeating multiple times doesn't give 100% certainty there's no race condition, but at least it's better than no checks
            for (int i = 0; i < 50; i++)
            {
                serverObj.SomeGhostField.Value = 111;
                TestConcurrentGhostFieldSystem.ServerObj = serverObj;
                TestReadWriteAfterSystem.ServerObj = serverObj;
                TestReadWriteAfterSystem.TestReadFromMainThread = true;
                await testWorld.TickMultipleAsync(1);
                Assert.AreEqual(222, serverObj.SomeGhostField.Value);

                serverObj.SomeGhostField.Value = 111;
                TestReadWriteAfterSystem.TestReadFromMainThread = false;
                await testWorld.TickMultipleAsync(1);
                Assert.AreEqual(333, serverObj.SomeGhostField.Value);
            }

            Assert.Greater(TestReadWriteAfterSystem.ValidTestCounter, 0, "sanity check failed, there was supposed to be at least 1 execution where the job was still not completed writing to the given component, but this run ran with all jobs already completed, rendering this test moot. This might be a device/OS scheduling thing. It's probably fine to rerun this test. If this test becomes too flaky, we can disable it.");
        }

        [Test(Description = "test what happens if users keep a GhostField stored somewhere and the underlying entity isn't valid anymore for various reasons")]
        public async Task TestElegantFailure_OnEntityMoveDestruction_ForCachedGhostFields([Values] bool destroyImmediate)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");
            var serverObj = GameObject.Instantiate(prefab);
            serverObj.SomeGhostField.Value = 123;
            await testWorld.TickMultipleAsync(4);
            var field = serverObj.SomeGhostField;

            // Destroy the underlying entity
            if (destroyImmediate)
            {
                GameObject.DestroyImmediate(serverObj.gameObject);
            }
            else
            {
                GameObject.Destroy(serverObj.gameObject);
                await testWorld.TickMultipleAsync(1); // wait for late update OnDestroy
            }

            Assert.Throws<UnityEngine.Assertions.AssertionException>(() => // Assert for the HasComponent check that'll fail
            {
                var v = field.Value;
            });
            Assert.Throws<UnityEngine.Assertions.AssertionException>(() => // calling this a second time should yield the same error. make sure the behaviour is idempotent
            {
                var v = field.Value;
            });

            await testWorld.TickMultipleAsync(30);
            Assert.Throws<UnityEngine.Assertions.AssertionException>(() =>
            {
                var v = field.Value;
            });
            Assert.Throws<UnityEngine.Assertions.AssertionException>(() =>
            {
                field.Value = 321;
            });

            // Move the underlying entity to a different chunk, making the current chunk disposed.
            var serverObj2 = GameObject.Instantiate(prefab);
            serverObj2.SomeGhostField.Value = 456;
            var field2 = serverObj2.SomeGhostField;
            var currentChunk = serverObj2.Ghost.World.EntityManager.GetChunk(serverObj2.Ghost.Entity);
            serverObj2.Ghost.World.EntityManager.AddComponent<TestComponent1>(serverObj2.Ghost.Entity);
            Assert.AreEqual(456, field2.Value);
            Assert.IsTrue(currentChunk.Invalid(), "sanity check failed, the assumption we had about the chunk composition for this test is wrong. We assume there's only a single entity in this chunk and that moving this entity will invalidate the chunk.");
            await testWorld.TickMultipleAsync(30);
            Assert.AreEqual(456, field2.Value, "data should still be accessible long term");
        }

        [Test(Description = "There's methods useful for IDE debugging we should test")]
        public async Task TestDebugMethods()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest(userSystems: new[] { typeof(SystemWithDependency), typeof(SystemWithDependencyNext) });
            await testWorld.ConnectAsync(enableGhostReplication: true);
            var prefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("BasicData");
            var serverObj = GameObject.Instantiate(prefab);
            serverObj.SomeGhostField.Value = 123;

            // test the value is accurate
            Assert.AreEqual("123", serverObj.SomeGhostField.GetDebugName());

            // test if there's a job running at the same time updating this component, that we get the right error
            testWorld.ServerWorld.GetExistingSystemManaged<SystemWithDependency>().helper = serverObj;
            var systemWithDependencyNext = testWorld.ServerWorld.GetExistingSystemManaged<SystemWithDependencyNext>();
            systemWithDependencyNext.helper = serverObj;
            await testWorld.TickMultipleAsync(1); // This should not log any error
            Assert.IsTrue(systemWithDependencyNext.done);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class SystemWithDependency : SystemBase
    {
        public PredictionCallbackHelper helper;
        public struct JobWithDependency : IJobChunk
        {
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Debug.Log("execute");
            }
        }
        protected override void OnCreate() { }

        protected override void OnUpdate()
        {
            if (helper == null) return;

            // Schedule a job which will have our component type as a dependency
            var genType = helper.SomeGhostField.m_GeneratedWrapperComponentType;
            var query = this.GetEntityQuery(genType);
            this.Dependency = new JobWithDependency().Schedule(query, this.Dependency);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateAfter(typeof(SystemWithDependency))]
    internal partial class SystemWithDependencyNext : SystemBase
    {
        public PredictionCallbackHelper helper;
        public bool done = false;

        protected override void OnUpdate()
        {
            if (helper == null) return;

            // make sure while debugging that users won't complete jobs by just hovering their mouse over a GhostField.
            Assert.IsTrue(helper.SomeGhostField.GetDebugName().Contains("Debugger Error: There's a job still not completed writing to this value."));
            done = true;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    // group to make sure our two systems execute right one after another
    internal partial class SequentialSystemsGroup : ComponentSystemGroup
    {

    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SequentialSystemsGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class TestConcurrentGhostFieldSystem : SystemBase
    {
        public static PredictionCallbackHelper ServerObj;

        EntityQuery m_TestGhostQuery;
        internal static JobHandle writeJobHandle;

        partial struct WriteJob : IJobChunk
        {
            public DynamicComponentTypeHandle generatedTypeHandle;
            public unsafe void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Thread.Sleep(100);
                Assert.AreEqual(1, chunk.Count, "sanity check failed");
                var compDataPtr = (int*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref generatedTypeHandle, 4).GetUnsafePtr();
                Assert.AreEqual(111, *compDataPtr); // test this is still the initial value
                *compDataPtr = 222;
            }
        }

        protected override void OnCreate()
        {
            m_TestGhostQuery = GetEntityQuery(typeof(GhostGameObjectLink));
            RequireForUpdate(m_TestGhostQuery);
        }

        protected override void OnUpdate()
        {
            var compType = ServerObj.SomeGhostField.m_GeneratedWrapperComponentType;
            compType.AccessModeType = ComponentType.AccessMode.ReadWrite;
            var j = new WriteJob()
            {
                generatedTypeHandle = GetDynamicComponentTypeHandle(compType)
            };
            Dependency = j.ScheduleParallel(m_TestGhostQuery, this.Dependency);
            writeJobHandle = Dependency;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SequentialSystemsGroup))]
    [UpdateAfter(typeof(TestConcurrentGhostFieldSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal partial class TestReadWriteAfterSystem : SystemBase
    {
        public static bool TestReadFromMainThread;
        public static PredictionCallbackHelper ServerObj;
        public static int ValidTestCounter;

        protected override void OnCreate()
        {
            RequireForUpdate<GhostGameObjectLink>();
        }

        protected override void OnUpdate()
        {
            // we can't execute this right after the job scheduling, since entities' dependency management hasn't registered the job with the global list of jobs. So the Complete that this GhostField does doesn't include the job
            Assert.IsTrue(TestConcurrentGhostFieldSystem.writeJobHandle != default, "sanity check failed");
            if (!TestConcurrentGhostFieldSystem.writeJobHandle.IsCompleted)
                ValidTestCounter++;
            if (TestReadFromMainThread)
                Assert.AreEqual(222, ServerObj.SomeGhostField.Value);
            else
                ServerObj.SomeGhostField.Value = 333; // we test later in the test that this indeed completed the job and that the job hasn't overwritten the value later
        }
    }
}
#endif
