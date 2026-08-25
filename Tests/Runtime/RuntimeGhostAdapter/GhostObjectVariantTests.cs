#if UNITY_EDITOR
using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class GhostObjectVariantTests
    {
        [GhostComponentVariation(typeof(LocalTransform), displayName: "Some Transform Test Override", isTestVariant: true)]
        internal struct TransformOverride : IComponentData
        {
            [GhostField] public quaternion Rotation;
        }

        [Test(Description = "Test various variants configurations, syncing rotation")]
        public async Task TestSimpleCase_GhostObjectSendType_Works([Values] bool withVariant)
        {
            var isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            await testWorld.ConnectAsync(enableGhostReplication: true);

            var ghostSendTypes = new[] { GhostSendType.AllClients, GhostSendType.OnlyPredictedClients, GhostSendType.OnlyInterpolatedClients, GhostSendType.DontSend };
            foreach (var sendTypeToTest in ghostSendTypes)
            {
                var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("variant ghost" + sendTypeToTest, autoRegister: false);
                var overrides = prefab.gameObject.AddComponent<GhostAuthoringInspectionComponent>();
                // we change the serialize field directly. This is like changing the monobehaviour in the inspector
                // normally ghost prefab type should strip things, but we ignore this for GhostObject. The stripping should be done at the GhostBehaviour level, this way the code that assumes a component is there also doesn't run.
                overrides.GetOrAddPrefabOverride(typeof(LocalTransform), PrefabsRegistry.EntityGuidFromGameObject(prefab.gameObject), GhostPrefabType.All);
                if (withVariant)
                    overrides.ComponentOverrides[0].VariantHash = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformOverride).FullName, typeof(LocalTransform).FullName);

                overrides.ComponentOverrides[0].SendTypeOptimization = sendTypeToTest;

                Netcode.RegisterPrefab(prefab.gameObject);

                var server = GameObject.Instantiate(prefab);

                server.Ghost.GhostTransform.ValueAsRef.Position = Vector3.one * 321;
                server.Ghost.GhostTransform.ValueAsRef.Rotation.value = new float4(0.5f);
                server.Ghost.PublishTransform();
                await testWorld.TickMultipleAsync(6);
                var client = PredictionCallbackHelper.ClientInstances.First(g => g.Ghost.World != testWorld.ServerWorld);
                Assert.NotNull(client);

                Assert.AreEqual(321, server.Ghost.GhostTransform.Value.Position.x);

                Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<LocalTransform>(server.Ghost.Entity));
                bool expectedSent = withVariant || (sendTypeToTest & GhostSendType.OnlyPredictedClients) == 0;
                Assert.AreEqual(expectedSent ? 0 : 321, client.Ghost.GhostTransform.Value.Position.x);
                Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LocalTransform>(client.Ghost.Entity));

                if ((sendTypeToTest & GhostSendType.OnlyPredictedClients) != 0)
                {
                    Assert.AreEqual(0.5f, client.Ghost.GhostTransform.Value.Rotation.value.x);
                }
                else if ((sendTypeToTest & GhostSendType.OnlyInterpolatedClients) != 0)
                {
                    Assert.AreEqual(0, client.Ghost.GhostTransform.Value.Rotation.value.x);
                }

                GameObject.Destroy(server.gameObject);
                await testWorld.TickMultipleAsync(6);
            }
        }
    }
}
#endif
