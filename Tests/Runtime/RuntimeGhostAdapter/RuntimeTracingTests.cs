using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode.LowLevel.StateSave;
using Unity.Netcode.Tracing;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    class RuntimeTracingTests
    {
        [Test(Description = "Canary to make sure we can trace at runtime and in builds.")]
        [DisableSingleWorldHostTest]
        public async Task TestRuntimeCanTrace()
        {
            // there used to be issues where deep tracing would fail in builds only. Making sure there's no regression here
            using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();
            var prefab = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("tracing test");

            TracingDataAccess.ResetStaticState();
            TracingDataAccess.Config.Data.AddRequiredTypeToTrace(ComponentType.ReadOnly<LocalTransform>());
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(TypeManager.GetSystemTypeIndex<GhostBehaviourPredictionSystem>());

            await testWorld.ConnectAsync(enableGhostReplication: true);

            prefab.CallbackHolder.OnPrediction += PredictionLogic;
            var server = GameObject.Instantiate(prefab);
            await testWorld.TickMultipleAsync(4);
            var client = PredictionCallbackHelper.ClientInstances[0];

            TracingDataAccess.Config.Data.EnableTracing = true;

            void PredictionLogic(GameObject go)
            {
                go.transform.position += Vector3.one;
            }

            await testWorld.TickMultipleAsync(10);

            var data = await TracingDataAccess.GetProcessedWorldsData(new CancellationToken());

            AssertNoDiffAboveEpsilon(data.ClientWorldData.DiffInfo, "client");
            AssertNoDiffAboveEpsilon(data.ServerWorldData.DiffInfo, "server");

            Assert.AreEqual(9, data.ClientWorldData.FrameIDs.Count);
            Assert.AreEqual(9, data.ServerWorldData.TickIDs.Count);
            Assert.AreEqual(9, data.ClientWorldData.PerFrameData.Count);
            var serverPerTickData = data.ServerWorldData.PerTickData;
            Assert.AreEqual(9, serverPerTickData.Count);

            var firstTick = serverPerTickData[serverPerTickData.GetKeyArray(Allocator.Temp)[0]];
            Assert.Greater(firstTick.PerSystemData.Count, 1);
            var firstSystem = firstTick.PerSystemData.GetKeyArray(Allocator.Temp)[0];
            Assert.AreEqual(firstTick.PerSystemData[firstSystem].MainGameTracingState.m_RawStateSave.stateSave.EntityCount, 1);
            firstTick.PerSystemData[firstSystem].MainGameTracingState
                .m_RawStateSave.stateSave
                .TryGetComponentData(new SavedEntityID(server.Ghost.GhostInfo), out LocalTransform savedTransform);
            Assert.AreEqual(new float3(4, 4, 4), savedTransform.Position);
        }

        static void AssertNoDiffAboveEpsilon(in DiffInfo diffInfo, string worldName)
        {
            if (!diffInfo.HasDiff)
                return;
            const float epsilon = 1e-4f;
            foreach (var kvp in diffInfo.AggregatesRO)
            {
                var reasons = kvp.Key.Reasons;
                // PartialTick only tags expected partial-tick mismatches, it's not a divergence by itself.
                reasons &= ~DiffInfo.DiffReasons.PartialTick;
                if ((reasons & DiffInfo.DiffReasons.ComponentData) != 0 && !(kvp.Value > epsilon))
                    reasons &= ~DiffInfo.DiffReasons.ComponentData;
                Assert.That(reasons, Is.EqualTo(DiffInfo.DiffReasons.Undefined), $"{worldName} has a diff above epsilon: {kvp.Key} amount:{kvp.Value:E3}");
            }
        }
    }
}
