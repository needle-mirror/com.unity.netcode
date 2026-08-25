using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class PredictionSwitchingTests
    {
        [Test(Description = "Test basic cases for prediction switching.")]
        public async Task PredictionSwitchingWork([Values] bool useInstanceAPI)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("Switcher", autoRegister: false);
            prefab.Ghost.SupportedGhostModes = GhostModeMask.All;
            prefab.Ghost.DefaultGhostMode = GhostMode.Interpolated;
            prefab.GetComponent<PredictionCallbackHelper>().CallbackHolder.OnPrediction += o =>
            {
                o.transform.position += new Vector3(1f, 0f, 1f);
            };
            Netcode.RegisterPrefab(prefab.gameObject);
            await testWorld.ConnectAsync(enableGhostReplication:true);
            //Run for a bit to stabilize ticks and time
            await testWorld.TickMultipleAsync(32);
            var serverObj = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(32);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];

            Assert.AreEqual(1, PredictionCallbackHelper.ClientInstances.Count, "sanity check failed");
            Assert.AreEqual(1, PredictionCallbackHelper.ServerInstances.Count, "sanity check failed");
            Assert.IsFalse(clientObj.Ghost.IsPredictedGhost);

            //this is interpolated, so we should be 2/3 ticks behind
            Assert.IsTrue(math.abs(serverObj.transform.position.x - clientObj.transform.position.x - 3f) <= 1e-4f, $"client: {clientObj.transform.position.x}, server: {serverObj.transform.position.x}");

            bool success;
            if (useInstanceAPI)
                success = clientObj.Ghost.ConvertToPredicted();
            else
                success = testWorld.ClientWorlds[0].PredictionSwitching.ToPredicted(clientObj.Ghost);

            Assert.IsTrue(success, "Failed to switch ghost to predicted mode");
            await testWorld.TickMultipleAsync(1);
            //Check that the object is actually converted to predicted ghost and no interpolation is actually applied.
            Assert.IsTrue(clientObj.Ghost.IsPredictedGhost);
            if (useInstanceAPI)
                success = clientObj.Ghost.ConvertToInterpolated();
            else
                success = testWorld.ClientWorlds[0].PredictionSwitching.ToInterpolated(clientObj.Ghost);
            Assert.IsTrue(success, "Failed to switch ghost to interpolated mode");
            Assert.IsFalse(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
            await testWorld.TickMultipleAsync(1);
            //Check that the object is actually converted to predicted ghost and no interpolation is actually applied.
            Assert.IsFalse(clientObj.Ghost.IsPredictedGhost, "ghost shouldn't be predicted");
            Assert.IsFalse(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
            //Check that the object is actually converted to predicted ghost and no interpolation is actually applied.
            if (useInstanceAPI)
                success = clientObj.Ghost.ConvertToPredicted(0.16f);
            else
                success = testWorld.ClientWorlds[0].PredictionSwitching.ToPredicted(clientObj.Ghost, 0.16f);
            Assert.IsTrue(success);
            await testWorld.TickMultipleAsync(1);
            Assert.IsTrue(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
            await testWorld.TickMultipleAsync(10);
            //Smoothing should be done now
            Assert.IsFalse(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
            //Check that the object is actually converted to predicted ghost and no interpolation is actually applied.
            if (useInstanceAPI)
                success = clientObj.Ghost.ConvertToInterpolated(0.16f);
            else
                success =testWorld.ClientWorlds[0].PredictionSwitching.ToInterpolated(clientObj.Ghost, 0.16f);
            Assert.IsTrue(success);
            await testWorld.TickMultipleAsync(1);
            Assert.IsTrue(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
            for (int i = 0; i < 8; ++i)
            {
                await testWorld.TickMultipleAsync(1);
                // transform is flip flopping between authoritative and smoothed. authoritative during tick, smoothed during rest of the update.
                var authoritativeTransform = clientObj.Ghost.GhostTransform.Value;
                var smoothedTransform = clientObj.transform;
                //Check that ghost position transform position are actually different (they should be)
                Assert.That(math.distance(smoothedTransform.position, authoritativeTransform.Position) > 1e-5f, $"Expected smoothed transform and authoritative transform be different while smoothing but got {smoothedTransform.position} - {authoritativeTransform.Position} after {i} ticks");
                Assert.That(math.distance(smoothedTransform.position, clientObj.transform.localPosition) < 1e-5f, $"Expected GameObject transform to be in sync with the smoothed transform while smoothing but got {smoothedTransform.position} - {clientObj.transform.localPosition} after {i} ticks");
            }

            await testWorld.TickMultipleAsync(1);
            //Smoothing should be done now
            Assert.IsFalse(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
        }

        [Test(Description = "Makes sure that when there IS smoothing on the underlying entity, that it's also applied on the GameObject")]
        public async Task GameObjectTransformFollowTheSmoothedPosition([Values] bool useInstanceAPI)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("Switcher", autoRegister: false);
            prefab.Ghost.SupportedGhostModes = GhostModeMask.All;
            prefab.Ghost.DefaultGhostMode = GhostMode.Predicted;
            prefab.CallbackHolder.OnPrediction += o =>
            {
                o.transform.position += new Vector3(1f, 0f, 1f);
            };
            Netcode.RegisterPrefab(prefab.gameObject);
            await testWorld.ConnectAsync(enableGhostReplication:true);
            //Run for a bit to stabilize ticks and time
            await testWorld.TickMultipleAsync(32);
            GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(32);
            var clientObj = PredictionCallbackHelper.ClientInstances[0];
            //Check that the object is actually converted to predicted ghost and no interpolation is actually applied.
            bool success;
            if (useInstanceAPI)
                success = clientObj.Ghost.ConvertToInterpolated(0.16f);
            else
                success = testWorld.ClientWorlds[0].PredictionSwitching.ToInterpolated(clientObj.Ghost, 0.16f);
            Assert.IsTrue(success);
            await testWorld.TickMultipleAsync(1);
            Assert.IsTrue(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));

            prefab.GetComponent<PredictionCallbackHelper>().OnUpdate += o =>
            {
                //On update the position should be still the last smoothed position (prev frame)
                var smoothedTransform = clientObj.transform;
                var authoritativeTransform = clientObj.Ghost.GhostTransform.Value;
                Assert.AreEqual(authoritativeTransform.Position, (float3)clientObj.Ghost.Position);
                Assert.AreEqual(authoritativeTransform.Rotation, (quaternion)clientObj.Ghost.Rotation);
                Assert.That(math.distance(smoothedTransform.position, authoritativeTransform.Position) < 1e-5f, $"Expected GameObject transform to be in sync with the LTW while smoothing  but got {smoothedTransform.position} - {authoritativeTransform.Position}");

            };
            prefab.GetComponent<PredictionCallbackHelper>().OnLateUpdate += o =>
            {
                //On update the position should be still the smoothed position
                var smoothedTransform = clientObj.transform;
                var authoritativeTransform = clientObj.Ghost.GhostTransform.Value;
                Assert.AreEqual(authoritativeTransform.Position, (float3)clientObj.Ghost.Position);
                Assert.AreEqual(authoritativeTransform.Rotation, (quaternion)clientObj.Ghost.Rotation);
                Assert.That(math.distance(smoothedTransform.position, authoritativeTransform.Position) < 1e-5f, $"Expected GameObject transform to be in sync with the LTW while smoothing  but got {smoothedTransform.position} - {authoritativeTransform.Position}");
            };
            for (int i = 0; i < 8; ++i)
            {
                await testWorld.TickMultipleAsync(1);
                //Check that ghost position transform position are actually different (they should be..)
                var smoothedTransform = clientObj.transform;
                var authoritativeTransform = clientObj.Ghost.GhostTransform.Value;
                Assert.AreEqual(authoritativeTransform.Position, (float3)clientObj.Ghost.Position);
                Assert.AreEqual(authoritativeTransform.Rotation, (quaternion)clientObj.Ghost.Rotation);
                Assert.That(math.distance(smoothedTransform.position, authoritativeTransform.Position) > 1e-5f, $"Expected LTW and LT be different while smoothing but got {smoothedTransform.position} - {authoritativeTransform.Position}");
                Debug.Log($"{smoothedTransform.position} - {smoothedTransform.position}");
                var ltw = clientObj.Ghost.World.EntityManager.GetComponentData<LocalToWorld>(clientObj.Ghost.Entity);
                Assert.That(math.distance(ltw.Position, clientObj.transform.localPosition) < 1e-5f, $"Expected GameObject transform to be in sync with the LTW while smoothing  but got {ltw.Position} - {clientObj.transform.localPosition}");
            }
            await testWorld.TickMultipleAsync(1);
            //Smoothing should be done now
            Assert.IsFalse(clientObj.Ghost.World.EntityManager.HasComponent<SwitchPredictionSmoothing>(clientObj.Ghost.Entity));
            {
                var smoothedTransform = clientObj.transform;
                var authoritativeTransform = clientObj.Ghost.GhostTransform.Value;
                Assert.AreEqual(authoritativeTransform.Position, (float3)clientObj.Ghost.Position);
                Assert.AreEqual(authoritativeTransform.Rotation, (quaternion)clientObj.Ghost.Rotation);
                Assert.That(math.distance(smoothedTransform.position, authoritativeTransform.Position) < 1e-5f, $"Expected LTW and LT be different while smoothing but got {smoothedTransform.position} - {authoritativeTransform.Position}");
                var lt = clientObj.Ghost.World.EntityManager.GetComponentData<LocalTransform>(clientObj.Ghost.Entity);
                Assert.That(math.distance(lt.Position, clientObj.transform.localPosition) < 1e-5f, $"Expected GameObject transform to be in sync with the LT when smoothing is off but got {lt.Position} - {clientObj.transform.localPosition}");
                Debug.Log($"{smoothedTransform.position} - {authoritativeTransform.Position}");
            }
        }

        [Test(Description = "Makes sure prediction switching works with other features, that GhostFields are not broken by switching for example")]
        public async Task TestThatSwitching_DoesNotAffectOtherFeatures([Values] bool startWithPredicted, [Values(0f, 0.2f)] float transitionTime)
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("Switcher", autoRegister: false);
            prefab.Ghost.SupportedGhostModes = GhostModeMask.All;
            if (startWithPredicted)
                prefab.Ghost.DefaultGhostMode = GhostMode.Predicted;
            else
                prefab.Ghost.DefaultGhostMode = GhostMode.Interpolated;
            Netcode.RegisterPrefab(prefab.gameObject);

            await testWorld.ConnectAsync(enableGhostReplication: true);

            var server = GameObject.Instantiate(prefab);
            server.SomeGhostField.Value = 123;
            await testWorld.TickMultipleAsync(6);
            var client = PredictionCallbackHelper.ClientInstances[0];
            Assert.IsTrue(testWorld.GetSingleton<GhostInstance>(testWorld.ClientWorlds[0]).ghostId != default, "sanity check failed, this test requires a single entity in its chunk.");
            Assert.AreEqual(123, server.SomeGhostField.Value);
            Assert.AreEqual(123, client.SomeGhostField.Value);
            // There was a bug where having transition times would add enough components to trigger chunk reuse, leading to GhostFields' cached pointer to be out of sync.
            // We test here this doesn't happen again. (there's a more specific test for this in GhostField tests as well)
            if (startWithPredicted)
                Assert.IsTrue(client.Ghost.ConvertToInterpolated(transitionTime));
            else
                Assert.IsTrue(client.Ghost.ConvertToPredicted(transitionTime));
            // test with no writes
            for (int i = 0; i < 10; i++)
            {
                await testWorld.TickMultipleAsync(1);
                Assert.AreEqual(123, client.SomeGhostField.Value);
            }
            server.SomeGhostField.Value = -1;
            for (int i = 0; i < 20; i++)
            {
                await testWorld.TickMultipleAsync(6);
                Assert.AreEqual(i - 1, client.SomeGhostField.Value);
                Assert.AreEqual(i - 1, server.SomeGhostField.Value);
                server.SomeGhostField.Value = i;
            }

            server.SomeGhostField.Value = -1;
            if (startWithPredicted)
                Assert.IsTrue(client.Ghost.ConvertToPredicted(transitionTime));
            else
                Assert.IsTrue(client.Ghost.ConvertToInterpolated(transitionTime));
            for (int i = 0; i < 20; i++)
            {
                await testWorld.TickMultipleAsync(6);
                Assert.AreEqual(i - 1, client.SomeGhostField.Value);
                Assert.AreEqual(i - 1, server.SomeGhostField.Value);
                server.SomeGhostField.Value = i;
            }
        }
    }
}
