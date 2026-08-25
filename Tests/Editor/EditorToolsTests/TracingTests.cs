using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.NetCode.Editor.Tracing;
using Unity.NetCode.Editor.Tracing.UI;
using Unity.NetCode.Tracing;
using Unity.NetCode.Tests;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tests.Editor
{
    // These tests verify the result of the diff processing and by extension the raw data processing.
    [DisableSingleWorldHostTest]
    internal partial class TracingTests
    {
        // Set of components and their baker for the tracing tests.
        internal struct TestRequiredComponent : IComponentData
        {
            public int Value;
        }

        internal struct TestOptionalComponent : IComponentData
        {
            public int Value;
        }

        // Zero-sized tag for the trace-dependency regression test.
        internal struct TestTagComponent : IComponentData
        {
        }

        internal class TestComponentsAuthoring : MonoBehaviour
        {
            public bool HasOptionalComponent = true;
        }

        internal class TestComponentsBaker : Baker<TestComponentsAuthoring>
        {
            public override void Bake(TestComponentsAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new TestRequiredComponent() { Value = 0 });
                if(authoring.HasOptionalComponent)
                    AddComponent(entity, new TestOptionalComponent() { Value = 0 });
                // Present on both ghosts; only the tag test traces (and removes) it.
                AddComponent<TestTagComponent>(entity);
            }
        }

        // Simple test setup that runs a delegate that can be overwritten by each test.
        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        partial struct TestSystem : ISystem
        {
            ComponentTypeHandle<GhostInstance> m_GhostInstanceHandle;
            public delegate void UpdateLogic(ref SystemState state, ComponentTypeHandle<GhostInstance> ghostInstanceHandle);
            public static UpdateLogic Delegate;
            public void OnCreate(ref SystemState state)
            {
                m_GhostInstanceHandle = state.GetComponentTypeHandle<GhostInstance>(true);
            }
            public void OnUpdate(ref SystemState state)
            {
                m_GhostInstanceHandle.Update(ref state);
                Delegate?.Invoke(ref state, m_GhostInstanceHandle);
            }
        }

        NetCodeTestWorld m_TestWorld;

        [SetUp]
        public void Setup()
        {
            m_TestWorld = new NetCodeTestWorld();

            TracingDataAccess.ResetStaticState();
            TracingDataAccess.Config.Data.AddRequiredTypeToTrace(ComponentType.ReadOnly<TestRequiredComponent>());
            TracingDataAccess.Config.Data.AddOptionalTypeToTrace(ComponentType.ReadOnly<TestOptionalComponent>());
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(TypeManager.GetSystemTypeIndex(typeof(TestSystem)));
            NetCodeConfig.Global.TracingConfig.IgnorePartialTicks = false;
            TestSystem.Delegate = null;
            m_TestWorld.Bootstrap(true, typeof(TestSystem), typeof(TestSystemReset), typeof(TestClientOnlySystem), typeof(TestServerOnlySystem));
            var prefabs = new GameObject[2];
            for (int i = 0; i < prefabs.Length; i++)
            {
                var go = new GameObject();
                var testComp = go.AddComponent<TestComponentsAuthoring>();
                testComp.HasOptionalComponent = i == 0; // Only one ghost has the optional component
                var ghostConfig = go.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                prefabs[i] = go;
            }

            m_TestWorld.CreateGhostCollection(prefabs);
            m_TestWorld.CreateWorlds(true, 1);
            m_TestWorld.Connect();
            m_TestWorld.GoInGame();
            m_TestWorld.SpawnOnServer(0);
            m_TestWorld.SpawnOnServer(1);
            m_TestWorld.TickUntilClientsHaveAllGhosts();
            TracingDataAccess.Config.Data.EnableTracing = true;
        }

        [TearDown]
        public void TearDown()
        {
            TracingDataAccess.Config.Data.EnableTracing = false;
            if (m_TestWorld != null)
                m_TestWorld.Dispose();
            TracingDataAccess.DisposeAllWorldData();
            m_TestWorld = null;
        }

        // This test system reset the component values for TestRequiredComponent and TestOptionalComponent.
        [DisableAutoCreation]
        [UpdateAfter(typeof(TestSystem))]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        partial struct TestSystemReset : ISystem
        {
            EntityQuery m_TestRequiredQuery;
            EntityQuery m_TestOptionalQuery;

            public void OnCreate(ref SystemState state)
            {
                m_TestRequiredQuery =
                    state.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TestRequiredComponent>());
                m_TestOptionalQuery =
                    state.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TestOptionalComponent>());
            }

            public void OnUpdate(ref SystemState state)
            {
                var arrRequired = m_TestRequiredQuery.ToComponentDataArray<TestRequiredComponent>(Allocator.Temp);
                for (int i = 0; i < arrRequired.Length; ++i)
                {
                    var c = arrRequired[i];
                    c.Value = 0;
                    arrRequired[i] = c;
                }

                m_TestRequiredQuery.CopyFromComponentDataArray(arrRequired);

                var arrOptional = m_TestOptionalQuery.ToComponentDataArray<TestOptionalComponent>(Allocator.Temp);
                for (int i = 0; i < arrOptional.Length; ++i)
                {
                    var c = arrOptional[i];
                    c.Value = 0;
                    arrOptional[i] = c;
                }

                m_TestOptionalQuery.CopyFromComponentDataArray(arrOptional);
            }
        }

        [Test(Description = "Test that tracing works when the size limit is reached and the traces start to get ovewriten")]
        public async Task TracingTestLoopBack()
        {
            m_TestWorld.TickMultiple(2);
            // Use internal method to set very low limit (10000 bytes) and test loopback mechanism with fewer ticks.
            TracingDataAccess.s_WorldsSaveSize.Data.MaxTracesSizeMB = 10000f / (1024*1024);
            m_TestWorld.TickMultiple(10);

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            Assert.IsTrue(processedWorldData.ClientWorldData.PerFrameData.Count > 0, "Test should have traced some frames on the client");
            Assert.IsTrue(processedWorldData.ServerWorldData.PerTickData.Count > 0, "Test should have traced some ticks on the server");

        }

        [Test(Description = "Test that accessing the result of tracing works after the ecs worlds have been disposed.")]
        public async Task TracingTestProcessingOutsidePlaymode()
        {
            m_TestWorld.TickMultiple(2);
            m_TestWorld.DisposeServerWorld();
            m_TestWorld.DisposeAllClientWorlds();

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            Assert.IsTrue(processedWorldData.ClientWorldData.PerFrameData.Count > 0, "Test should have traced some frames on the client");
            Assert.IsTrue(processedWorldData.ServerWorldData.PerTickData.Count > 0, "Test should have traced some ticks on the server");

        }

        [Test(Description = "Test that only the requested data is traced and that the processed state save contains the expected data." +
                            "For this test, under the hoot, it gets the data from the static reference after the world is destroyed.")]
        public async Task TraceOnlyWhatsRequested()
        {
            // Setup
            var counter = 0;
            TestSystem.Delegate = (ref SystemState state, ComponentTypeHandle<GhostInstance> _) =>
            {
                var testRequiredQuery =
                    state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestRequiredComponent)));
                var testOptionalQuery =
                    state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestOptionalComponent)));
                counter++;
                var arrRequired = testRequiredQuery.ToComponentDataArray<TestRequiredComponent>(Allocator.Temp);
                for (int i = 0; i < arrRequired.Length; ++i)
                {
                    var c = arrRequired[i];
                    c.Value = counter;
                    arrRequired[i] = c;
                }

                testRequiredQuery.CopyFromComponentDataArray(arrRequired);

                var arrOptional = testOptionalQuery.ToComponentDataArray<TestOptionalComponent>(Allocator.Temp);
                for (int i = 0; i < arrOptional.Length; ++i)
                {
                    var c = arrOptional[i];
                    c.Value = -counter;
                    arrOptional[i] = c;
                }

                testOptionalQuery.CopyFromComponentDataArray(arrOptional);
            };

            // Act
            m_TestWorld.TickMultiple(4);

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert

            Assert.Greater(processedWorldData.ClientWorldData.PerFrameData.Count, 0);
            var clientTickData = processedWorldData.ClientWorldData.PerFrameData.GetValueArray(Allocator.Temp)[^1].PerTickData;
            Assert.Greater(clientTickData.Count, 0);

            var lastRequiredValue = -1;
            var lastOptionalValue = 1;
            var requiredValueGotUpdated = false;
            var optionalValueGotUpdated = false;
            foreach (var kvTick in clientTickData)
            {
                foreach (var kvSystem in kvTick.Value.PerSystemData)
                {
                    if (kvSystem.Value.TracePosition == TracePosition.netcode)
                        continue;

                    Assert.AreEqual(
                        TypeManager.GetSystemTypeIndex(typeof(TestSystem)),
                        kvSystem.Key.value,
                        "The only system that should be traced is TestSystem");

                    var stateSave = kvSystem.Value.MainGameTracingState.m_RawStateSave.stateSave;
                    Assert.IsTrue(2 == stateSave.EntityCount, "Two ghosts should have the required component");
                    var entities = stateSave.GetAllEntities(Allocator.Temp);
                    Assert.IsTrue(stateSave.EntityCount == stateSave.GetAllEntities(Allocator.Temp).Length,
                        "Get all entities count should match entity count");
                    var optionalComponentCount = 0;
                    foreach (var entity in entities)
                    {
                        Assert.IsTrue(
                            stateSave.TryGetComponentData(entity, out TestRequiredComponent requiredComponentData),
                            "All traced ghost should have the required component");
                        if (kvSystem.Value.TracePosition == TracePosition.after){
                            Assert.IsTrue(requiredComponentData.Value > 0,
                                "The TestSystem should have incremented the value");
                        }
                        else if (kvSystem.Value.TracePosition == TracePosition.before)
                            Assert.IsTrue(requiredComponentData.Value == 0,
                                "The TestSystemReset should have reset the value");

                        if(lastRequiredValue == -1) // If it's the first required value we get
                            lastRequiredValue = requiredComponentData.Value;
                        else if (lastRequiredValue != requiredComponentData.Value) // If not, was it updated?
                            requiredValueGotUpdated = true;

                        if (stateSave.TryGetComponentData(entity, out TestOptionalComponent optionalComponentData))
                        {
                            optionalComponentCount++;
                            if (kvSystem.Value.TracePosition == TracePosition.after){
                                Assert.IsTrue(optionalComponentData.Value < 0,
                                "The TestSystem should have decreased the value");
                            }
                            else if (kvSystem.Value.TracePosition == TracePosition.before)
                                Assert.IsTrue(optionalComponentData.Value == 0,
                                    "The TestSystemReset should have reset the value");

                            if(lastOptionalValue == 1) // If it's the first optional value we get (optional is zero or negative)
                                lastOptionalValue = optionalComponentData.Value;
                            else if (lastOptionalValue != optionalComponentData.Value)  // If not, was it updated?
                                optionalValueGotUpdated = true;
                        }
                    }

                    Assert.IsTrue(1 == optionalComponentCount, "One ghost should have the optional component");
                }
            }
            Assert.IsTrue(requiredValueGotUpdated, "Different tick should have different required component value");
            Assert.IsTrue(optionalValueGotUpdated, "Different tick should have different optional component value");
        }

        [Test(Description = "Test that when a component data is different between client and server," +
                            "the diff is properly processed, the diff reasons and that it appears after the system not before.")]
        public async Task TestDiffComponentData()
        {
            // Setup diff 1 component data on the client
            TestSystem.Delegate = (ref SystemState state, ComponentTypeHandle<GhostInstance> _) =>
            {
                if(state.World.IsServer())
                    return;
                var eq = state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestRequiredComponent)));
                var comps = eq.ToComponentDataArray<TestRequiredComponent>(Allocator.Temp);
                var c = comps[0];
                c.Value = 20;
                comps[0] = c;
                eq.CopyFromComponentDataArray(comps);
            };

            //Act Tick client first since it's ahead of server, and we want to compare the last tick for diff.
            m_TestWorld.TickMultiple(4);

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert
            var matchingTickData = GetFirstDiffedTick(processedWorldData.ClientWorldData);
            Assert.IsTrue(matchingTickData.DiffInfo.HasDiff, "Their should be a diff at the tick level");
            Assert.AreEqual(DiffInfo.DiffReasons.ComponentData, matchingTickData.DiffInfo.DiffReasonFlags, "The tick should carry the ComponentData reason from its systems");
            Assert.IsTrue(matchingTickData.PerSystemData.Count > 0, "There should be system data to compare");

            // The test system wrote the mispredicted value this tick, so the tick-level aggregate must
            // be filterable as a value change.
            var foundValueChangedAggregate = false;
            foreach (var kvp in matchingTickData.DiffInfo.m_Aggregates)
            {
                if (kvp.Key.System == TypeManager.GetSystemTypeIndex<TestSystem>()
                    && kvp.Key.Reasons == DiffInfo.DiffReasons.ComponentData
                    && kvp.Key.HasValueChanged)
                {
                    foundValueChangedAggregate = true;
                    Assert.Greater(kvp.Value, 0f, "a ComponentData aggregate should carry its diff amount");
                }
            }
            Assert.IsTrue(foundValueChangedAggregate, "The tick aggregates should contain a value-changed ComponentData entry stamped with TestSystem");
            foreach (var systemData in matchingTickData.PerSystemData.GetValueArray(Allocator.Temp))
            {
                if(systemData.Family != TraceType.Default)
                    continue;
                if (systemData.TracePosition == TracePosition.before)
                    Assert.IsFalse(systemData.DiffInfo.HasDiff, "Their should diff before the system introduce it");
                else if(systemData.TracePosition == TracePosition.after)
                {
                    Assert.IsTrue(systemData.DiffInfo.HasDiff, "Their should be a diff after the system introduce it");
                    Assert.AreEqual(DiffInfo.DiffReasons.ComponentData, systemData.DiffInfo.DiffReasonFlags, "The diff reason should be ComponentData");
                }
            }
        }

        [Test(Description = "Test that when a component is missing between client and server," +
                            "the diff is properly processed, the diff reasons and that it appears after the system not before.")]
        public async Task TestMissingComponent()
        {
            // No diff
            m_TestWorld.TickMultiple(2);

            // Act Tick client first since it's ahead of server and we want to compare the last tick for diff.
            TestSystem.Delegate = (ref SystemState state, ComponentTypeHandle<GhostInstance> _) =>
            {
                if(state.World.IsServer())
                    return;
                var eq = state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestRequiredComponent))).ToEntityArray(Allocator.Temp);
                state.EntityManager.RemoveComponent<TestOptionalComponent>(eq[0]);
            };
            m_TestWorld.TickMultiple(2);

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert
            var matchingTickData = GetFirstDiffedTick(processedWorldData.ClientWorldData);
            Assert.IsTrue(matchingTickData.DiffInfo.HasDiff, "Their should be a diff at the tick level");
            Assert.AreEqual(DiffInfo.DiffReasons.MissingComponent, matchingTickData.DiffInfo.DiffReasonFlags, "The tick should carry the MissingComponent reason from its systems");
            foreach (var systemData in matchingTickData.PerSystemData.GetValueArray(Allocator.Temp))
            {
                if(systemData.Family != TraceType.Default)
                    continue;
                if (systemData.TracePosition == TracePosition.before)
                    Assert.IsFalse(systemData.DiffInfo.HasDiff, "Their should diff before the system introduce it");
                else if(systemData.TracePosition == TracePosition.after)
                {
                    Assert.IsTrue(systemData.DiffInfo.HasDiff, "Their should be a diff after the system introduce it");
                    Assert.AreEqual(DiffInfo.DiffReasons.MissingComponent, systemData.DiffInfo.DiffReasonFlags, "The diff reason should be MissingComponent since the component was removed on the client");
                }
            }
        }

        [Test(Description = "Regression: tracing a zero-sized tag component must not trip the ComponentDependencyManager " +
                            "zero-sized assertion, and removing it on the client must still surface a MissingComponent diff.")]
        public async Task TestZeroSizedTagComponentTraces()
        {
            // Before the fix, tracing a zero-sized tag asserts in ComponentDependencyManager on the first trace.
            TracingDataAccess.Config.Data.AddOptionalTypeToTrace(ComponentType.ReadOnly<TestTagComponent>());

            // No diff yet: tag present on both sides.
            m_TestWorld.TickMultiple(2);

            // Remove the tag on the client so it traces fewer components than the server.
            TestSystem.Delegate = (ref SystemState state, ComponentTypeHandle<GhostInstance> _) =>
            {
                if (state.World.IsServer())
                    return;
                var eq = state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestTagComponent))).ToEntityArray(Allocator.Temp);
                if (eq.Length > 0)
                    state.EntityManager.RemoveComponent<TestTagComponent>(eq[0]);
            };
            m_TestWorld.TickMultiple(2);

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert
            var matchingTickData = GetFirstDiffedTick(processedWorldData.ClientWorldData);
            Assert.IsTrue(matchingTickData.DiffInfo.HasDiff, "There should be a diff at the tick level");
            Assert.AreEqual(DiffInfo.DiffReasons.MissingComponent, matchingTickData.DiffInfo.DiffReasonFlags, "The tick should carry the MissingComponent reason from its systems");
            foreach (var systemData in matchingTickData.PerSystemData.GetValueArray(Allocator.Temp))
            {
                if(systemData.Family != TraceType.Default)
                    continue;
                if (systemData.TracePosition == TracePosition.before)
                    Assert.IsFalse(systemData.DiffInfo.HasDiff, "There should be no diff before the system introduces it");
                else if(systemData.TracePosition == TracePosition.after)
                {
                    Assert.IsTrue(systemData.DiffInfo.HasDiff, "There should be a diff after the system introduces it");
                    Assert.AreEqual(DiffInfo.DiffReasons.MissingComponent, systemData.DiffInfo.DiffReasonFlags, "The diff reason should be MissingComponent since the tag was removed on the client");
                }
            }
        }

        // Multi-field struct for the per-field differ test; default (sequential) layout keeps GetAllFields
        // order aligned with memory offsets.
        struct ThreeFieldStruct
        {
            public int A;
            public int B;
            public int C;
        }

        [Test(Description = "ProcessDiffPerField flags exactly the changed field's bit and records its amount.")]
        public unsafe void TypeDiffer_ProcessDiffPerField_FlagsOnlyTheChangedField()
        {
            var differ = TypeDiffer.ConstructDifferRecursive(typeof(ThreeFieldStruct), Allocator.Temp);
            try
            {
                var left = new ThreeFieldStruct { A = 1, B = 2, C = 3 };
                var unchanged = new ThreeFieldStruct { A = 1, B = 2, C = 3 };
                var changedB = new ThreeFieldStruct { A = 1, B = 99, C = 3 };

                var amounts = stackalloc float[TypeDiffer.MaxFieldBits];
                UnsafeUtility.MemClear(amounts, TypeDiffer.MaxFieldBits * sizeof(float));
                var noDiff = differ.ProcessDiffPerField((byte*)UnsafeUtility.AddressOf(ref left), (byte*)UnsafeUtility.AddressOf(ref unchanged), amounts);
                Assert.AreEqual(0UL, noDiff, "Identical values must produce an empty mask.");

                var mask = differ.ProcessDiffPerField((byte*)UnsafeUtility.AddressOf(ref left), (byte*)UnsafeUtility.AddressOf(ref changedB), amounts);
                Assert.AreEqual(1UL << 1, mask, "Only field index 1 (B) should be flagged.");
                Assert.AreEqual(97f, amounts[1], "The unit's diff amount is the absolute delta.");
            }
            finally
            {
                differ.Dispose();
            }
        }

        struct VectorComponent : IComponentData
        {
            public float3 V;
        }

        struct ScalarThenVectorComponent : IComponentData
        {
            public int A;
            public float3 V;
        }

        struct QuaternionComponent : IComponentData
        {
            public quaternion Q;
        }

        [Test(Description = "ProcessDiffPerField expands math vectors into one unit per scalar, in the same order " +
                            "the table renders them, so a single changed component (e.g. z) maps to exactly one bit.")]
        public unsafe void TypeDiffer_ProcessDiffPerField_MapsVectorScalarsToUnits()
        {
            // float3: x,y,z -> units 0,1,2. Changing only z sets bit 2.
            using (var differ = ConstructDisposable(typeof(VectorComponent)))
            {
                var left = new VectorComponent { V = new float3(1f, 2f, 3f) };
                var changedZ = new VectorComponent { V = new float3(1f, 2f, 9f) };
                Assert.AreEqual(1UL << 2, Diff(differ, ref left, ref changedZ), "float3.z is unit 2");
            }

            // int then float3: A -> unit 0, V.x/y/z -> units 1,2,3 (also checks the vector's offset after a scalar).
            using (var differ = ConstructDisposable(typeof(ScalarThenVectorComponent)))
            {
                var left = new ScalarThenVectorComponent { A = 1, V = new float3(1f, 2f, 3f) };
                var changedA = new ScalarThenVectorComponent { A = 9, V = new float3(1f, 2f, 3f) };
                var changedVz = new ScalarThenVectorComponent { A = 1, V = new float3(1f, 2f, 9f) };
                Assert.AreEqual(1UL << 0, Diff(differ, ref left, ref changedA), "int field is unit 0");
                Assert.AreEqual(1UL << 3, Diff(differ, ref left, ref changedVz), "float3.z after an int is unit 3");
            }

            // quaternion nests a float4 (x,y,z,w) -> units 0,1,2,3. Changing z sets bit 2.
            using (var differ = ConstructDisposable(typeof(QuaternionComponent)))
            {
                var left = new QuaternionComponent { Q = new quaternion(0f, 0f, 0f, 1f) };
                var changedZ = new QuaternionComponent { Q = new quaternion(0f, 0f, 9f, 1f) };
                Assert.AreEqual(1UL << 2, Diff(differ, ref left, ref changedZ), "quaternion z is unit 2");
            }
        }

        static TypeDiffer ConstructDisposable(Type type) => TypeDiffer.ConstructDifferRecursive(type, Allocator.Temp);

        static unsafe ulong Diff<T>(TypeDiffer differ, ref T left, ref T right) where T : unmanaged
        {
            var amounts = stackalloc float[TypeDiffer.MaxFieldBits];
            UnsafeUtility.MemClear(amounts, TypeDiffer.MaxFieldBits * sizeof(float));
            return differ.ProcessDiffPerField((byte*)UnsafeUtility.AddressOf(ref left), (byte*)UnsafeUtility.AddressOf(ref right), amounts);
        }

        [Test(Description = "End-to-end: a misprediction reaches the client TracedComponent, the server value " +
                            "resolves, and read-time re-diffing flags the mispredicted field.")]
        public async Task TestDiffComponentData_ProjectsServerValueAndUnitDiffs()
        {
            TestSystem.Delegate = (ref SystemState state, ComponentTypeHandle<GhostInstance> _) =>
            {
                if (state.World.IsServer())
                    return;
                var eq = state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestRequiredComponent)));
                var comps = eq.ToComponentDataArray<TestRequiredComponent>(Allocator.Temp);
                var c = comps[0];
                c.Value = 20;
                comps[0] = c;
                eq.CopyFromComponentDataArray(comps);
            };

            m_TestWorld.TickMultiple(4);
            var processed = await TracingDataAccess.GetProcessedWorldsData();
            var client = processed.ClientWorldData;

            // Locate the diffed client frame/tick.
            FrameID frameId = default;
            TickID tickId = default;
            var foundTick = false;
            foreach (var f in client.FrameIDs)
            {
                var frame = client.PerFrameData[f];
                foreach (var t in frame.TickIDs)
                {
                    if (frame.PerTickData[t].DiffInfo.HasDiff)
                    {
                        frameId = f;
                        tickId = t;
                        foundTick = true;
                        break;
                    }
                }
                if (foundTick)
                    break;
            }
            Assert.IsTrue(foundTick, "Expected a diffed tick on the client.");

            // Client side: TestRequiredComponent under TestSystem carries the data diff on the mispredicted ghost.
            var clientTick = client.GetClientTickData(frameId, tickId);
            var ghostId = 0;
            TracedComponent clientTraced = default;
            var clientFound = false;
            foreach (var sys in clientTick.SystemsByExecutionOrder)
            {
                if (sys.SystemType != typeof(TestSystem) || sys.GhostComponents == null)
                    continue;
                foreach (var kv in sys.GhostComponents)
                {
                    if (kv.Value.Components != null
                        && kv.Value.Components.TryGetValue(typeof(TestRequiredComponent), out var tc)
                        && (tc.DiffReasonFlags & DiffInfo.DiffReasons.ComponentData) != 0)
                    {
                        ghostId = kv.Key;
                        clientTraced = tc;
                        clientFound = true;
                        break;
                    }
                }
                if (clientFound)
                    break;
            }
            Assert.IsTrue(clientFound, "The data diff should reach the client TracedComponent.");
            Assert.AreEqual(20, ((TestRequiredComponent)clientTraced.AfterValue).Value);

            // Server side: the same (shared) system resolves a non-null authoritative value to compare against.
            var serverTick = processed.ServerWorldData.GetServerTickData(tickId);
            TracedComponent serverTraced = default;
            var serverFound = false;
            foreach (var sys in serverTick.SystemsByExecutionOrder)
            {
                if (sys.SystemType != typeof(TestSystem) || sys.GhostComponents == null)
                    continue;
                if (sys.GhostComponents.TryGetValue(ghostId, out var g)
                    && g.Components != null
                    && g.Components.TryGetValue(typeof(TestRequiredComponent), out var tc))
                {
                    serverTraced = tc;
                    serverFound = true;
                    break;
                }
            }
            Assert.IsTrue(serverFound, "The server value should resolve for the Server Authority column.");
            Assert.IsNotNull(serverTraced.AfterValue);
            Assert.AreEqual(0, ((TestRequiredComponent)serverTraced.AfterValue).Value);

            // Read-time recompute, as the detail table's highlight does.
            var diffAmountsByUnit = new float[TypeDiffer.MaxFieldBits];
            var mask = TypeDiffer.ComputeUnitDiffs(typeof(TestRequiredComponent), clientTraced.AfterValue, serverTraced.AfterValue, diffAmountsByUnit);
            Assert.AreEqual(1UL << 0, mask & (1UL << 0), "The Value field (unit 0) should diff.");
            Assert.AreEqual(20f, diffAmountsByUnit[0], "The unit's diff amount is the client/server delta.");
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        partial struct TestClientOnlySystem : ISystem
        {

        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        partial struct TestServerOnlySystem : ISystem
        {

        }

        [Test(Description = "Test that when a system runs on the client but not the server, the diff is properly processed" +
                            "and it appears at the tick level and on the system with ExtraSystem as reason.")]
        public async Task TestExtraSystem()
        {
            // Setup Add client only system to traced systems
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(TypeManager.GetSystemTypeIndex<TestClientOnlySystem>());

            //Act Tick client first since it's ahead of server and we want to compare the last tick for diff.
            m_TestWorld.TickMultiple(4);
            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert
            var matchingTickData = GetFirstDiffedTick(processedWorldData.ClientWorldData);
            Assert.IsTrue(matchingTickData.DiffInfo.HasDiff, "Their should be a diff at the tick level");
            Assert.AreEqual(DiffInfo.DiffReasons.ExtraSystem, matchingTickData.DiffInfo.DiffReasonFlags, "The diff reason should be ExtraSystem");
            foreach (var system in matchingTickData.PerSystemData.GetKeyArray(Allocator.Temp))
            {
                var systemData = matchingTickData.PerSystemData[system];
                if(systemData.Family != TraceType.Default)
                    continue;
                if (system.value == TypeManager.GetSystemTypeIndex<TestClientOnlySystem>())
                {
                    Assert.IsTrue(systemData.DiffInfo.HasDiff, "Their should be a diff for the TestClientOnlySystem");
                    Assert.AreEqual(DiffInfo.DiffReasons.ExtraSystem, systemData.DiffInfo.DiffReasonFlags, "The diff reason should be ExtraSystem");
                }
                else if(system.tracePosition != TracePosition.netcode)
                    Assert.IsFalse(systemData.DiffInfo.HasDiff, "Their should no diff for the other user systems");
            }
        }

        [Test(Description = "Test that when a system runs on the server but not the client, the diff is properly processed" +
                            "and it appears on the client tick and on the server system with MissingSystem as reason.")]
        public async Task TestMissingSystem()
        {
            // Setup Add server only system to traced systems
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(TypeManager.GetSystemTypeIndex<TestServerOnlySystem>());

            //Act Tick client first since it's ahead of server and we want to compare the last tick for diff.
            m_TestWorld.TickMultiple(4);
            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert
            var (tickID, matchingTickData) = GetFirstDiffedTickWithID(processedWorldData.ClientWorldData);
            Assert.IsTrue(matchingTickData.DiffInfo.HasDiff, "Their should be a diff at the tick level");
            Assert.AreEqual(DiffInfo.DiffReasons.MissingSystem, matchingTickData.DiffInfo.DiffReasonFlags, "The diff reason should be MissingSystem");

            // The per-system reason lives on the server tick, where the system was traced.
            var serverTickData = processedWorldData.ServerWorldData.PerTickData[tickID];
            var foundServerOnlySystem = false;
            foreach (var system in serverTickData.PerSystemData.GetKeyArray(Allocator.Temp))
            {
                if (system.value != TypeManager.GetSystemTypeIndex<TestServerOnlySystem>())
                    continue;
                foundServerOnlySystem = true;
                var systemData = serverTickData.PerSystemData[system];
                Assert.IsTrue(systemData.DiffInfo.HasDiff, "Their should be a diff for the TestServerOnlySystem");
                Assert.AreEqual(DiffInfo.DiffReasons.MissingSystem, systemData.DiffInfo.DiffReasonFlags, "The diff reason should be MissingSystem");
            }
            Assert.IsTrue(foundServerOnlySystem, "The server tick should contain traces for the TestServerOnlySystem");
        }

        static void AssertNoMissingExtraSystemOnNetcodeTraces(TickData tick)
        {
            const DiffInfo.DiffReasons oneSided = DiffInfo.DiffReasons.MissingSystem | DiffInfo.DiffReasons.ExtraSystem;
            foreach (var system in tick.PerSystemData.GetKeyArray(Allocator.Temp))
            {
                if (system.tracePosition != TracePosition.netcode)
                    continue;

                var systemData = tick.PerSystemData[system];
                if (systemData.DiffInfo.m_Aggregates.IsCreated)
                {
                    foreach (var kvp in systemData.DiffInfo.m_Aggregates)
                        Assert.IsTrue((kvp.Key.Reasons & oneSided) == 0 || kvp.Key.System != system.value,
                            $"netcode-position trace {TypeManager.GetSystemName(system.value)} is flagged {kvp.Key.Reasons}");
                }

                if (tick.DiffInfo.m_Aggregates.IsCreated)
                {
                    foreach (var kvp in tick.DiffInfo.m_Aggregates)
                        Assert.IsTrue((kvp.Key.Reasons & oneSided) == 0 || kvp.Key.System != system.value,
                            $"the tick flags netcode-position trace {TypeManager.GetSystemName(system.value)} as {kvp.Key.Reasons}");
                }
            }
        }

        [Test(Description = "The tool's own per-tick anchors (TraceEndPrediction/TraceEndFrame) are state-saved" +
                            "unconditionally, even when the system selection does not contain them. Every other" +
                            "traced system must come from the selection.")]
        public async Task NetcodePositionAnchors_AreTracedRegardlessOfSystemSelection()
        {
            var endPrediction = TypeManager.GetSystemTypeIndex<TraceEndPredictionSystem>();
            var endFrame = TypeManager.GetSystemTypeIndex<TraceEndFrameSystem>();
            Assert.IsFalse(TracingDataAccess.Config.Data.SystemTypesToTrace.Contains(endPrediction), "sanity: the selection must not contain the anchors");
            Assert.IsFalse(TracingDataAccess.Config.Data.SystemTypesToTrace.Contains(endFrame), "sanity: the selection must not contain the anchors");

            // Long enough that early anchor-carrying client ticks get matching server ticks, so the
            // client-vs-server diff actually runs over the anchors (a too-short recording would make the
            // one-sided assertions below vacuously pass).
            m_TestWorld.TickMultiple(12);
            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Client: the anchors are traced by design despite not being selected; anything else traced
            // must be in the selection (the deep-trace filter).
            var foundEndPrediction = false;
            var foundEndFrame = false;
            var anchorTickDiffedAgainstServer = false;
            var clientFrames = processedWorldData.ClientWorldData.PerFrameData.GetValueArray(Allocator.Temp);
            Assert.Greater(clientFrames.Length, 0, "the recording should contain frames");
            foreach (var frame in clientFrames)
            {
                var tickIDs = frame.PerTickData.GetKeyArray(Allocator.Temp);
                foreach (var tickID in tickIDs)
                {
                    var tick = frame.PerTickData[tickID];
                    var tickHasAnchor = false;
                    foreach (var system in tick.PerSystemData.GetKeyArray(Allocator.Temp))
                    {
                        if (system.value == endPrediction)
                        {
                            foundEndPrediction = true;
                            tickHasAnchor = true;
                            Assert.AreEqual(TracePosition.netcode, system.tracePosition);
                        }
                        else if (system.value == endFrame)
                        {
                            foundEndFrame = true;
                            tickHasAnchor = true;
                            Assert.AreEqual(TracePosition.netcode, system.tracePosition);
                        }
                        else
                        {
                            Assert.IsTrue(TracingDataAccess.Config.Data.SystemTypesToTrace.Contains(system.value),
                                $"{TypeManager.GetSystemName(system.value)} was traced on the client without being selected");
                        }
                    }

                    // Only anchor ticks that exist on the server too went through the client-vs-server
                    // diff; those must not report the anchors as missing/extra systems.
                    if (tickHasAnchor && processedWorldData.ServerWorldData.PerTickData.ContainsKey(tickID))
                    {
                        anchorTickDiffedAgainstServer = true;
                        AssertNoMissingExtraSystemOnNetcodeTraces(tick);
                    }
                }
            }
            Assert.IsTrue(foundEndPrediction, "the End Of Prediction anchor should be traced even when not selected");
            Assert.IsTrue(foundEndFrame, "the End Of Frame anchor should be traced even when not selected");
            Assert.IsTrue(anchorTickDiffedAgainstServer, "at least one anchor-carrying tick should have been diffed against the server, otherwise the one-sided assertions are vacuous");

            // Server: the anchors are client-only, so every traced system must come from the selection.
            var serverTicks = processedWorldData.ServerWorldData.PerTickData.GetValueArray(Allocator.Temp);
            foreach (var tick in serverTicks)
            {
                foreach (var system in tick.PerSystemData.GetKeyArray(Allocator.Temp))
                {
                    Assert.IsTrue(TracingDataAccess.Config.Data.SystemTypesToTrace.Contains(system.value),
                        $"{TypeManager.GetSystemName(system.value)} was traced on the server without being selected");
                }
            }
        }

        [Test(Description = "Test that when an object is missing between client and server, the diff is properly processed," +
                            "the diff reasons and that it appears after the system not before.")]
        [TestCase (true, TestName = "ObjectMissingOnClient")]
        [TestCase (false, TestName = "ObjectMissingOnServer")]
        public async Task TestDiffEntityCount(bool isClient)
        {
            // No diff
            m_TestWorld.TickMultiple(2);

            var removedGhostInstanceId = -1;
            // Act Tick client first since it's ahead of server and we want to compare the last tick for diff.
            TestSystem.Delegate = (ref SystemState state, ComponentTypeHandle<GhostInstance> _) =>
            {
                var eq = state.EntityManager.CreateEntityQuery(new ComponentType(typeof(TestRequiredComponent)), new ComponentType(typeof(GhostInstance)));
                if(eq.IsEmpty || isClient != state.World.IsClient() || removedGhostInstanceId != -1)
                    return;
                var entity = eq.ToEntityArray(Allocator.Temp)[0];
                removedGhostInstanceId = state.EntityManager.GetComponentData<GhostInstance>(entity).ghostId;
                state.EntityManager.RemoveComponent<TestRequiredComponent>(entity);
            };
            m_TestWorld.TickMultiple(2);

            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();

            // Assert
            // The ghost vanished from the traces of the side that removed the component, so the reason is
            // MissingGhost when the client lost it and ExtraGhost when the server did.
            var expectedReason = isClient ? DiffInfo.DiffReasons.MissingGhost : DiffInfo.DiffReasons.ExtraGhost;
            var matchingTickData = GetFirstDiffedTick(processedWorldData.ClientWorldData);
            Assert.IsTrue(matchingTickData.DiffInfo.HasDiff, "Their should be a diff at the tick level");
            Assert.AreEqual(expectedReason, matchingTickData.DiffInfo.DiffReasonFlags, $"The tick should carry the {expectedReason} reason from its systems");

            // The ghost (de)spawned during the system, so its aggregate must count as a value change —
            // otherwise "Changed values only" would hide the very mismatch the spawn/despawn caused.
            var foundGhostAggregate = false;
            foreach (var kvp in matchingTickData.DiffInfo.m_Aggregates)
            {
                if (kvp.Key.GhostId != removedGhostInstanceId || (kvp.Key.Reasons & expectedReason) == 0)
                    continue;
                foundGhostAggregate = true;
                Assert.IsTrue(kvp.Key.HasValueChanged, "a ghost removed during the tick should be stamped as a value change");
            }
            Assert.IsTrue(foundGhostAggregate, "the tick aggregates should contain the missing/extra ghost entry");

            foreach (var systemData in matchingTickData.PerSystemData.GetValueArray(Allocator.Temp))
            {
                if(systemData.Family != TraceType.Default)
                    continue;
                if (systemData.TracePosition == TracePosition.before)
                    Assert.IsFalse(systemData.DiffInfo.HasDiff, "Their should no diff before the system introduce it");
                if(systemData.TracePosition == TracePosition.after)
                {
                    Assert.IsTrue(systemData.DiffInfo.HasDiff, "Their should be a diff after the system introduce it");
                    Assert.AreEqual(expectedReason, systemData.DiffInfo.DiffReasonFlags, $"The diff reason should be {expectedReason}");
                    var missingObjects = isClient ? systemData.DiffInfo.MissingObjectsFromClient : systemData.DiffInfo.MissingObjectsFromServer;
                    Assert.AreEqual(1, missingObjects.Length, "There should be one missing object");
                    Assert.AreEqual(removedGhostInstanceId, missingObjects[0].value.ghostId, "The missing object should be the one removed by the test");
                }
            }
        }

        [Test(Description = "Tracing can be paused and resumed")]
        public async Task TracingCanBePausedAndResumed()
        {
            // Start tracing
            m_TestWorld.TickMultiple(1);
            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
            Assert.IsTrue(processedWorldData.ClientWorldData.FrameIDs.Count > 0, "Client should have traces");
            Assert.AreEqual(1, processedWorldData.ServerWorldData.TickIDs.Count, "Server should have one trace");
            // Pause tracing
            TracingDataAccess.Config.Data.EnableTracing = false;
            // Resuming should not cause error
            m_TestWorld.TickMultiple(2);
            TracingDataAccess.Config.Data.EnableTracing = true;
            TracingDataAccess.DisposeProcessedWorldData();
            m_TestWorld.TickMultiple(1);
            processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
            Assert.IsTrue(processedWorldData.ClientWorldData.FrameIDs.Count > 0, "Client should have traces");
            Assert.IsTrue(processedWorldData.ServerWorldData.TickIDs.Count > 1, "Server should have more than one trace");
        }

        [Test(Description = "Tracing can be stopped and restarted")]
        public async Task TracingCanBeStoppedAndRestarted()
        {
            // Start tracing
            m_TestWorld.TickMultiple(1);
            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
            Assert.IsTrue(processedWorldData.ClientWorldData.FrameIDs.Count > 0, "Client should have traces");
            Assert.IsTrue(processedWorldData.ServerWorldData.TickIDs.Count == 1, "Server should have 1 trace");
            // Stop tracing
            TracingDataAccess.DisposeAllWorldData();
            // Resuming should not cause error
            m_TestWorld.TickMultiple(1);
            processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
            Assert.IsTrue(processedWorldData.ClientWorldData.FrameIDs.Count > 0, "Client should have traces");
            Assert.IsTrue(processedWorldData.ServerWorldData.TickIDs.Count == 1, "Server should still have 1 trace");
        }

        [Test (Description = "Tracing is paused after calling GetProcessedWorldsData and resumed after calling DisposeProcessedWorldData")]
        public async Task TracingIsPausedAfterProcessingData()
        {
            // One normal cycle
            m_TestWorld.TickMultiple(1);
            var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
            Assert.IsTrue(processedWorldData.ClientWorldData.FrameIDs.Count > 0, "Client should have traces");
            Assert.IsTrue(processedWorldData.ServerWorldData.TickIDs.Count == 1, "Server should have 1 trace");
            // Resuming should not cause error
            m_TestWorld.TickMultiple(2);
            // Dispose of processed data to allow resuming tracing
            TracingDataAccess.DisposeProcessedWorldData();
            // Resuming should not cause error and resume tracing
            m_TestWorld.TickMultiple(1);
            processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
            Assert.IsTrue(processedWorldData.ClientWorldData.FrameIDs.Count > 0, "Client should have traces");
            Assert.IsTrue(processedWorldData.ServerWorldData.TickIDs.Count > 1, "Server should have more than one trace");
        }

        [Test (Description = "Tracing is paused after calling GetProcessedWorldsData and resumed after calling DisposeProcessedWorldData")]
        public async Task ProcessingWorksOverMultipleFrames()
        {
            TracingDataAccess.m_ForceProcessingOverMultipleFrames = true;
            try
            {
                m_TestWorld.TickMultiple(4);
                var processedWorldData = await TracingDataAccess.GetProcessedWorldsData();
                Assert.IsNotNull(processedWorldData);
            }
            finally
            {
                 TracingDataAccess.m_ForceProcessingOverMultipleFrames = false;
            }
        }

        struct TestJob : IJob
        {
            public void Execute()
            {
            }
        }

        TickData GetFirstDiffedTick(WorldData worldData)
        {
            return GetFirstDiffedTickWithID(worldData).Item2;
        }

        (TickID, TickData) GetFirstDiffedTickWithID(WorldData worldData)
        {
            var lastFrame = worldData.PerFrameData.GetValueArray(Allocator.Temp)[^1];
            foreach (var tickID in lastFrame.TickIDs)
            {
                var tickData = lastFrame.PerTickData[tickID];
                if (tickData.DiffInfo.HasDiff)
                    return (tickID, tickData);
            }

            return default;
        }
    }

    /// <summary>
    /// EditMode coverage for <see cref="SelectTracingTargetWindow"/>: tab/category display, Tracing.json persistence,
    /// and search filtering (uses internal editor APIs exercised by <c>Unity.NetCode.Editor.Tests</c>).
    /// </summary>
    [TestFixture]
    internal class SelectTracingTargetWindowEditModeTests
    {
        const string k_MainStyleSheetGuid = "67b936a114f34ef58ad86e624cece4b2";
        const string k_VariablesDarkSheetGuid = "22021193a5a54b5b97583f4fcec65e5e";
        const string k_VariablesLightSheetGuid = "a51d6f89764f4b15a736fdd72e8a5c68";

        TracingTargetSelectionFile m_BackupSelection;
        SelectTracingTargetWindow m_Window;

        [SetUp]
        public void SetUp()
        {
            m_BackupSelection = CloneSelection(NetCodeTracingTargetSettings.GetSelection());
            NetCodeTracingTargetSettings.SaveSelection(new TracingTargetSelectionFile());
            m_Window = ScriptableObject.CreateInstance<SelectTracingTargetWindow>();
            AssignStyleSheetsFromPackageGuids(m_Window);
            m_Window.CreateGUI();
            // Attach a panel so the MultiColumnTreeView creates row elements (needed for row toggle UI test).
            m_Window.Show();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Window != null)
            {
                m_Window.Close();
                UnityEngine.Object.DestroyImmediate(m_Window);
                m_Window = null;
            }

            NetCodeTracingTargetSettings.SaveSelection(m_BackupSelection ?? new TracingTargetSelectionFile());
        }

        [Test]
        public void SystemsTab_DisplaysOnlySystems_AndCountMatchesAllTabSystemRows()
        {
            ClickTab("Systems");
            var systemsOnlyCount = GetDisplayedEntryCount(m_Window);
            Assert.Greater(systemsOnlyCount, 0, "Expected at least one concrete ECS system type (ComponentSystemBase or ISystem) in the project");

            foreach (var type in EnumerateDisplayedTypes(m_Window))
            {
                Assert.IsTrue(TracingTargetTypes.IsTracingEcsSystemType(type),
                    $"Systems tab should list only ECS system types; got {type.FullName}");
            }

            ClickTab("All");
            var systemRowsOnAll = CountDisplayedEntriesWithKind(m_Window, "System");
            Assert.AreEqual(systemsOnlyCount, systemRowsOnAll,
                "Systems tab count should match the number of system rows on the All tab (same backing set, same search).");
        }

        [Test]
        public void SystemsTab_DefaultPredictedFilter_ShowsOnlyPredictionLoopSystems_AndDisablingRevealsTheRest()
        {
            Assert.IsTrue(TracingTargetTypes.IsPredictionSystem(typeof(PredictedSimulationSystemGroup)),
                "The prediction root group itself is part of the prediction loop.");
            Assert.IsTrue(TracingTargetTypes.IsPredictionSystem(typeof(PredictedFixedStepSimulationSystemGroup)),
                "Groups nested in the prediction group are part of the prediction loop.");
            Assert.IsFalse(TracingTargetTypes.IsPredictionSystem(typeof(GhostSendSystem)),
                "GhostSendSystem updates in SimulationSystemGroup, outside the prediction loop.");

            ClickTab("Systems");
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingPredicted),
                "The Predicted column filter must ship enabled.");
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingPredicted,
                    TracingColumnFilters.k_PredictedFilterValueNotPredicted),
                "The Predicted filter must hide non-predicted systems by default.");

            var displayed = EnumerateDisplayedTypes(m_Window).ToList();
            Assert.Greater(displayed.Count, 0, "Netcode ships several prediction-loop systems.");
            foreach (var type in displayed)
            {
                Assert.IsTrue(TracingTargetTypes.IsPredictionSystem(type),
                    $"{type.FullName} is not predicted; the default Predicted filter must hide it.");
            }

            CollectionAssert.Contains(displayed, typeof(PredictedFixedStepSimulationSystemGroup));
            CollectionAssert.DoesNotContain(displayed, typeof(GhostSendSystem));

            // Systems get moved into the prediction loop at runtime, so the rest stays reachable behind the filter.
            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingPredicted, false);
            var unfiltered = EnumerateDisplayedTypes(m_Window).ToList();
            Assert.Greater(unfiltered.Count, displayed.Count,
                "Disabling the Predicted filter must reveal the client/server systems outside the prediction loop.");
            CollectionAssert.Contains(unfiltered, typeof(GhostSendSystem));
        }

        [Test]
        public void IsPredictionSystem_CoversSystemsNetcodeMovesIntoThePredictionLoopAtRuntime()
        {
            Assert.IsTrue(TracingTargetTypes.IsPredictionSystem(typeof(PredictedGhostSpawnSystem)),
                "PredictedSpawningSystemGroup adds PredictedGhostSpawnSystem to the prediction loop at world creation.");

            var netcodePhysicsPresent = Type.GetType("Unity.NetCode.PredictedPhysicsConfigSystem, Unity.NetCode.Physics") != null;
            var physicsGroup = Type.GetType("Unity.Physics.Systems.PhysicsSystemGroup, Unity.Physics");
            if (!netcodePhysicsPresent || physicsGroup == null)
            {
                Assert.Ignore("Netcode physics is not part of this project; the physics reparenting cannot be asserted.");
            }

            Assert.IsTrue(TracingTargetTypes.IsPredictionSystem(physicsGroup),
                "PredictedPhysicsConfigSystem reparents PhysicsSystemGroup into the prediction loop at world creation.");

            var exportPhysicsWorld = Type.GetType("Unity.Physics.Systems.ExportPhysicsWorld, Unity.Physics");
            if (exportPhysicsWorld != null)
            {
                Assert.IsTrue(TracingTargetTypes.IsPredictionSystem(exportPhysicsWorld),
                    "Systems nested inside the physics group follow it into the prediction loop.");
            }

            // Fixed-step systems with no UpdateBefore/UpdateAfter chain to the physics group stay behind
            // (mirrors MovePhysicsSystemUtilities: InjectTemporalCoherenceDataSystem orders against the
            // begin-fixed-step command buffer only).
            var injectTemporalCoherence = Type.GetType("Unity.Physics.Systems.InjectTemporalCoherenceDataSystem, Unity.Physics");
            if (injectTemporalCoherence != null)
            {
                Assert.IsFalse(TracingTargetTypes.IsPredictionSystem(injectTemporalCoherence),
                    "Fixed-step systems without ordering against the physics group are not moved by netcode physics.");
            }
        }

        [Test]
        public void NonPredictedSystem_IsSelectable_OnceThePredictedFilterIsDisabled()
        {
            ClickTab("Systems");
            CollectionAssert.DoesNotContain(EnumerateDisplayedTypes(m_Window).ToList(), typeof(GhostSendSystem),
                "GhostSendSystem is outside the prediction loop; the default Predicted filter hides it.");

            // The Predicted column is informative only; reaching a non-predicted system goes through the filter.
            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingPredicted, false);
            CollectionAssert.Contains(EnumerateDisplayedTypes(m_Window).ToList(), typeof(GhostSendSystem));

            m_Window.SetSelected(typeof(GhostSendSystem), TracingTargetKind.System, true);
            var saved = NetCodeTracingTargetSettings.GetSelection();
            Assert.IsTrue(saved.entries.Exists(e => e.assemblyQualifiedName == typeof(GhostSendSystem).AssemblyQualifiedName),
                "A non-predicted system must be selectable for tracing once the filter is disabled.");
            m_Window.SetSelected(typeof(GhostSendSystem), TracingTargetKind.System, false);
        }

        [Test]
        public void ColumnValueFilter_PredictedPopup_ListsBothStates_AndIgnoresComponentRows()
        {
            ClickTab("All");
            // Popup values come from the pre-filter entries: both states are listed even while "Not predicted" rows are hidden.
            var values = new List<string>();
            m_Window.CollectUniqueColumnFilterValues(TracingColumnFilters.k_ColumnTracingPredicted, values);
            CollectionAssert.Contains(values, TracingColumnFilters.k_PredictedFilterValuePredicted);
            CollectionAssert.Contains(values, TracingColumnFilters.k_PredictedFilterValueNotPredicted);
            CollectionAssert.DoesNotContain(values, TracingColumnFilters.k_ColumnFilterNoValue,
                "The Predicted popup must not list a None entry even while component rows (no predicted value) are displayed.");

            // Component rows are ignored by the Predicted filter: hiding the remaining state hides exactly the system rows.
            var baseline = GetDisplayedEntryCount(m_Window);
            var systemRows = CountDisplayedEntriesWithKind(m_Window, "System");
            Assert.Greater(systemRows, 0, "Expected predicted system rows on the All tab.");
            Assert.Greater(baseline, systemRows, "Expected component rows on the All tab.");
            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingPredicted,
                TracingColumnFilters.k_PredictedFilterValuePredicted, true);
            Assert.AreEqual(baseline - systemRows, GetDisplayedEntryCount(m_Window),
                "Hiding every Predicted value must hide exactly the system rows; component rows are ignored.");
        }

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        [Test]
        public void ComponentsTab_DefaultUnmanagedFilter_ShowsOnlyUnmanagedComponents_AndDisablingRevealsManaged()
        {
            Assert.IsTrue(TracingTargetTypes.IsUnmanagedComponentType(typeof(LocalTransform)),
                "Struct components are unmanaged.");
            Assert.IsFalse(TracingTargetTypes.IsUnmanagedComponentType(
                    typeof(TracingManagedComponentFixture.ManagedTracingFixtureComponent)),
                "Class components are managed.");

            ClickTab("Components");
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingUnmanaged),
                "The Unmanaged column filter must ship enabled.");
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingUnmanaged,
                    TracingColumnFilters.k_UnmanagedFilterValueManaged),
                "The Unmanaged filter must hide managed components by default.");

            var displayed = EnumerateDisplayedTypes(m_Window).ToList();
            Assert.Greater(displayed.Count, 0, "Expected unmanaged component rows on the Components tab.");
            foreach (var type in displayed)
            {
                Assert.IsTrue(TracingTargetTypes.IsUnmanagedComponentType(type),
                    $"{type.FullName} is managed; the default Unmanaged filter must hide it.");
            }

            CollectionAssert.DoesNotContain(displayed, typeof(TracingManagedComponentFixture.ManagedTracingFixtureComponent));

            // Managed components stay reachable behind the filter: they can't be value-traced, but marking one
            // as required still filters which ghosts are traced.
            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingUnmanaged, false);
            var unfiltered = EnumerateDisplayedTypes(m_Window).ToList();
            Assert.Greater(unfiltered.Count, displayed.Count,
                "Disabling the Unmanaged filter must reveal the managed components.");
            CollectionAssert.Contains(unfiltered, typeof(TracingManagedComponentFixture.ManagedTracingFixtureComponent));
        }

        [Test]
        public void ColumnValueFilter_UnmanagedPopup_ListsBothStates_AndIgnoresSystemRows()
        {
            ClickTab("All");
            // Popup values come from the pre-filter entries: both states are listed even while "Managed" rows are hidden.
            var values = new List<string>();
            m_Window.CollectUniqueColumnFilterValues(TracingColumnFilters.k_ColumnTracingUnmanaged, values);
            CollectionAssert.Contains(values, TracingColumnFilters.k_UnmanagedFilterValueUnmanaged);
            CollectionAssert.Contains(values, TracingColumnFilters.k_UnmanagedFilterValueManaged);
            CollectionAssert.DoesNotContain(values, TracingColumnFilters.k_ColumnFilterNoValue,
                "The Unmanaged popup must not list a None entry even while system rows (no unmanaged value) are displayed.");

            // System rows are ignored by the Unmanaged filter: hiding the remaining state hides exactly the component rows.
            var baseline = GetDisplayedEntryCount(m_Window);
            var componentRows = CountDisplayedEntriesWithKind(m_Window, "Component");
            Assert.Greater(componentRows, 0, "Expected unmanaged component rows on the All tab.");
            Assert.Greater(baseline, componentRows, "Expected system rows on the All tab.");
            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingUnmanaged,
                TracingColumnFilters.k_UnmanagedFilterValueUnmanaged, true);
            Assert.AreEqual(baseline - componentRows, GetDisplayedEntryCount(m_Window),
                "Hiding every Unmanaged value must hide exactly the component rows; system rows are ignored.");
        }
#endif

        [Test]
        public void ColumnValueFilter_HidingTheColumn_SuspendsTheFilter_AndShowingItReappliesIt()
        {
            ClickTab("All");
            var baseline = GetDisplayedEntryCount(m_Window);
            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace,
                TracingColumnFilters.k_NamespaceFilterPatternUnityPrefix, true);
            var filtered = GetDisplayedEntryCount(m_Window);
            Assert.Less(filtered, baseline, "Hiding Unity.* namespaces must hide rows.");

            // Hiding the column (the header context menu path) suspends its value filter: the rows come back
            // immediately, without waiting for an unrelated refresh.
            var namespaceColumn = m_Window.TracingTreeView.columns[TracingColumnFilters.k_ColumnTracingNamespace];
            namespaceColumn.visible = false;
            Assert.AreEqual(baseline, GetDisplayedEntryCount(m_Window),
                "Hiding the column must suspend its value filter and restore the hidden rows.");

            // Showing the column again re-applies the still-configured filter to the rows.
            namespaceColumn.visible = true;
            Assert.AreEqual(filtered, GetDisplayedEntryCount(m_Window),
                "Showing the column again must re-apply its value filter to the rows.");
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace),
                "The filter configuration must survive the hide/show round-trip.");
        }

        [Test]
        public void ComponentsTab_DisplaysOnlyComponents_AndCountMatchesAllTabComponentRows()
        {
            ClickTab("Components");
            var componentsOnlyCount = GetDisplayedEntryCount(m_Window);
            Assert.Greater(componentsOnlyCount, 0, "Expected at least one IComponentData type from TypeManager");

            foreach (var type in EnumerateDisplayedTypes(m_Window))
            {
                Assert.IsTrue(typeof(IComponentData).IsAssignableFrom(type) && !type.IsInterface,
                    $"Components tab should list only component types; got {type.FullName}");
            }

            ClickTab("All");
            var componentRowsOnAll = CountDisplayedEntriesWithKind(m_Window, "Component");
            Assert.AreEqual(componentsOnlyCount, componentRowsOnAll,
                "Components tab count should match the number of component rows on the All tab.");
        }

        [Test]
        public void GhostInstance_SelectsLikeAnyOtherComponent_AndOnlyTogglesItsVisibility()
        {
            // GhostInstance is always traced by the backend; selecting it only makes the views show it.
            ClickTab("Selected");
            CollectionAssert.DoesNotContain(EnumerateDisplayedTypes(m_Window).ToList(), typeof(GhostInstance),
                "An unselected GhostInstance must not show on the Selected tab.");
            Assert.IsFalse(TracingWindowUtility.IsGhostInstanceSelected(),
                "An unselected GhostInstance must report unselected to the views.");

            m_Window.SetSelected(typeof(GhostInstance), TracingTargetKind.Component, true);
            // A second component guarantees SetTypesToTrace initializes the trace-request sets.
            m_Window.SetSelected(typeof(LocalTransform), TracingTargetKind.Component, true);
            var saved = NetCodeTracingTargetSettings.GetSelection();
            Assert.IsTrue(saved.entries.Exists(e => e.assemblyQualifiedName == typeof(GhostInstance).AssemblyQualifiedName),
                "A selected GhostInstance must persist in the tracing selection like any other component.");

            // SetSelected bypasses the row toggles' refresh, so re-enter the tab to rebuild its rows.
            ClickTab("All");
            ClickTab("Selected");
            CollectionAssert.Contains(EnumerateDisplayedTypes(m_Window).ToList(), typeof(GhostInstance),
                "A selected GhostInstance must show on the Selected tab.");

            TracingWindowUtility.SetTypesToTrace();
            Assert.IsTrue(TracingWindowUtility.IsGhostInstanceSelected(),
                "A selected GhostInstance must report selected to the views.");
            Assert.IsFalse(TracingDataAccess.Config.Data.RequiredTypesToTrace.Contains(ComponentType.ReadWrite<GhostInstance>()),
                "GhostInstance must never join the trace request; the backend always traces it.");
            Assert.IsFalse(TracingDataAccess.Config.Data.OptionalTypesToTrace.Contains(ComponentType.ReadWrite<GhostInstance>()),
                "GhostInstance must never join the trace request; the backend always traces it.");

            m_Window.SetSelected(typeof(GhostInstance), TracingTargetKind.Component, false);
            TracingWindowUtility.SetTypesToTrace();
            Assert.IsFalse(TracingWindowUtility.IsGhostInstanceSelected(),
                "Deselecting GhostInstance must hide it from the views again.");
        }

        [Test]
        public void SelectAndDeselect_PersistsToTracingSettings()
        {
            var key = typeof(LocalTransform).AssemblyQualifiedName;

            m_Window.SetSelected(typeof(LocalTransform), TracingTargetKind.Component, true);
            var afterSelect = NetCodeTracingTargetSettings.GetSelection();
            Assert.That(afterSelect.entries, Is.Not.Null);
            Assert.That(afterSelect.entries.Exists(e => e.assemblyQualifiedName == key), $"Tracing selection should contain {typeof(LocalTransform).AssemblyQualifiedName} after select.");

            m_Window.SetSelected(typeof(LocalTransform), TracingTargetKind.Component, false);
            var afterClear = NetCodeTracingTargetSettings.GetSelection();
            Assert.That(afterClear.entries, Is.Not.Null);
            Assert.IsFalse(afterClear.entries.Exists(e => e.assemblyQualifiedName == typeof(LocalTransform).AssemblyQualifiedName), "Tracing selection should no longer contain the type after deselect.");
        }

        [Test]
        public void ComponentSelection_RequiredFilter_PersistsToTracingSettings()
        {
            var key = typeof(LocalTransform).AssemblyQualifiedName;

            m_Window.SetSelected(typeof(LocalTransform), TracingTargetKind.Component, true);
            var defaultEntry = NetCodeTracingTargetSettings.GetSelection().entries.Find(e => e.assemblyQualifiedName == key);
            Assert.NotNull(defaultEntry);
            Assert.IsFalse(defaultEntry.required, "Newly selected components default to Optional (required=false).");

            m_Window.SetComponentTracingRequirement(typeof(LocalTransform), true);
            var afterRequired = NetCodeTracingTargetSettings.GetSelection().entries.Find(e => e.assemblyQualifiedName == key);
            Assert.NotNull(afterRequired);
            Assert.IsTrue(afterRequired.required);

            m_Window.SetComponentTracingRequirement(typeof(LocalTransform), false);
            var afterOptional = NetCodeTracingTargetSettings.GetSelection().entries.Find(e => e.assemblyQualifiedName == key);
            Assert.NotNull(afterOptional);
            Assert.IsFalse(afterOptional.required);
        }

        [Test]
        public void TargetTabs_PinLocalTransformFirst_UntilAColumnSortIsApplied()
        {
            ClickTab("All");
            Assert.AreEqual(typeof(LocalTransform), FirstDisplayedTypeOrNull(m_Window),
                "The All tab must surface LocalTransform (the scene-visualization component) first by default.");

            // An explicit column sort takes over the ordering.
            m_Window.TracingTreeView.sortColumnDescriptions.Add(
                new SortColumnDescription(TracingColumnFilters.k_ColumnTracingName, SortDirection.Ascending));
            Assert.AreNotEqual(typeof(LocalTransform), FirstDisplayedTypeOrNull(m_Window),
                "An explicit column sort must order LocalTransform like any other row.");
            CollectionAssert.Contains(EnumerateDisplayedTypes(m_Window).ToList(), typeof(LocalTransform),
                "Sorting must reorder LocalTransform, not hide it.");

            // Removing the sort restores the pin.
            m_Window.TracingTreeView.sortColumnDescriptions.Clear();
            Assert.AreEqual(typeof(LocalTransform), FirstDisplayedTypeOrNull(m_Window),
                "Clearing the column sort must restore the LocalTransform pin.");
        }

        [Test]
        public void NameCell_ShowsClickableSceneViewIcon_OnlyOnTheLocalTransformRow()
        {
            ClickTab("All");
            var displayed = EnumerateDisplayedTypes(m_Window).ToList();
            var localTransformIndex = displayed.IndexOf(typeof(LocalTransform));
            Assert.GreaterOrEqual(localTransformIndex, 0, "LocalTransform must be displayed on the All tab.");
            var otherIndex = displayed.FindIndex(t => t != typeof(LocalTransform));
            Assert.GreaterOrEqual(otherIndex, 0);

            var cell = m_Window.MakeTracingNameTreeCell();
            m_Window.BindTracingNameTreeCell(cell, localTransformIndex);
            var sceneVisIcon = cell.Q<VisualElement>(SelectTracingTargetWindow.k_UssNameTracingRowSceneVisIcon);
            Assert.NotNull(sceneVisIcon, "The name cell must carry the Scene view icon element.");
            Assert.IsFalse(sceneVisIcon.ClassListContains(TracingWindowUssClasses.Hidden),
                "The Scene view icon must show on the LocalTransform row.");

            m_Window.BindTracingNameTreeCell(cell, otherIndex);
            Assert.IsTrue(sceneVisIcon.ClassListContains(TracingWindowUssClasses.Hidden),
                "Rows other than LocalTransform must hide the Scene view icon.");
        }

        [Test]
        public void SystemsTab_SearchFiltersDisplayedTypes()
        {
            ClickTab("Systems");
            var baseline = GetDisplayedEntryCount(m_Window);
            Assert.Greater(baseline, 3, "Expected several systems so filtering can shrink the list.");

            var firstType = FirstDisplayedTypeOrNull(m_Window);
            Assert.NotNull(firstType);
            var token = firstType.FullName ?? firstType.Name;
            SetSearchQuery(token);

            var filtered = GetDisplayedEntryCount(m_Window);
            Assert.Less(filtered, baseline, "Full-name search should narrow the systems list below the unfiltered count.");
            foreach (var t in EnumerateDisplayedTypes(m_Window))
            {
                Assert.IsTrue(m_Window.PassesSearch(t),
                    $"Displayed system {t.FullName} should still pass PassesSearch for '{token}'.");
            }
        }

        [Test]
        public void ColumnValueFilter_OnlyWorldPredictedUnmanagedAndNamespaceFilterable()
        {
            Assert.IsTrue(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingWorld));
            Assert.IsTrue(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingPredicted));
            Assert.IsTrue(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingUnmanaged));
            Assert.IsTrue(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingNamespace));

            Assert.IsFalse(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingName),
                "The Name column must not offer the header value filter.");
            Assert.IsFalse(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingSelectAll),
                "The mass-selection column must not offer the header value filter.");
            Assert.IsFalse(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingType),
                "The Type column must not offer the header value filter.");
            Assert.IsFalse(TracingColumnFilters.IsColumnValueFilterable(TracingColumnFilters.k_ColumnTracingFilteringOptions),
                "The Filtering Options column must not offer the header value filter.");
        }

        [Test]
        public void ColumnValueFilter_NamespacePopup_ListsPatternFiltersLast_AllShownByDefault()
        {
            ClickTab("All");
            var values = new List<string>();
            m_Window.CollectUniqueColumnFilterValues(TracingColumnFilters.k_ColumnTracingNamespace, values);

            var unityPatternIndex = values.IndexOf(TracingColumnFilters.k_NamespaceFilterPatternUnityPrefix);
            var generatedPatternIndex = values.IndexOf(TracingColumnFilters.k_NamespaceFilterPatternGenerated);
            Assert.GreaterOrEqual(unityPatternIndex, 0, "The Namespace popup must list the 'Unity.*' pattern filter.");
            Assert.AreEqual(unityPatternIndex + 1, generatedPatternIndex,
                "'*Generated*' must directly follow 'Unity.*' in the popup's footer section.");

            // Only the optional None placeholder may follow the pattern filters, just above the All toggle.
            var placeholderIndex = values.IndexOf(TracingColumnFilters.k_ColumnFilterNoValue);
            if (placeholderIndex >= 0)
            {
                Assert.AreEqual(generatedPatternIndex + 1, placeholderIndex,
                    "The None placeholder must directly follow the pattern filters.");
            }

            Assert.AreEqual(values.Count - 1, placeholderIndex >= 0 ? placeholderIndex : generatedPatternIndex,
                "The special entries must be the last values listed.");

            foreach (var value in values)
            {
                Assert.IsFalse(m_Window.ColumnFilters.IsColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace, value),
                    $"All values, including the pattern filters, must be ticked (not hidden) by default; '{value}' was hidden.");
            }
        }

        [Test]
        public void ColumnValueFilter_UnityPrefixPattern_HidesUnityNamespaces_AndUntickingRestoresThem()
        {
            ClickTab("All");
            var baseline = GetDisplayedEntryCount(m_Window);
            var unityCount = EnumerateDisplayedTypes(m_Window)
                .Count(t => t.Namespace != null && t.Namespace.StartsWith("Unity.", StringComparison.Ordinal));
            Assert.Greater(unityCount, 0, "Expected rows in Unity.* namespaces (netcode/entities types).");
            Assert.Greater(baseline, unityCount, "Expected rows outside Unity.* namespaces (test fixtures).");

            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace,
                TracingColumnFilters.k_NamespaceFilterPatternUnityPrefix, true);
            Assert.AreEqual(baseline - unityCount, GetDisplayedEntryCount(m_Window),
                "Unticking 'Unity.*' should remove exactly the rows whose namespace starts with 'Unity.'.");
            foreach (var type in EnumerateDisplayedTypes(m_Window))
            {
                Assert.IsFalse(type.Namespace != null && type.Namespace.StartsWith("Unity.", StringComparison.Ordinal),
                    $"{type.FullName} is in a Unity.* namespace and should be hidden by the pattern filter.");
            }

            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace,
                TracingColumnFilters.k_NamespaceFilterPatternUnityPrefix, false);
            Assert.AreEqual(baseline, GetDisplayedEntryCount(m_Window), "Re-ticking the pattern should restore the hidden rows.");
            Assert.IsFalse(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace),
                "Re-ticking the last hidden value must drop the filter toggle back to off.");
        }

        [Test]
        public void ColumnValueFilter_HidingValue_AutoEnablesFilter_AndTogglingItOffKeepsConfiguredValues()
        {
            ClickTab("All");
            var baseline = GetDisplayedEntryCount(m_Window);
            var generatedCount = EnumerateDisplayedTypes(m_Window)
                .Count(t => t.Namespace != null && t.Namespace.IndexOf("Generated", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Greater(generatedCount, 0, "Expected at least one row in a *Generated* namespace (fixture component).");
            Assert.IsFalse(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace),
                "Column filters must start disabled.");

            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace,
                TracingColumnFilters.k_NamespaceFilterPatternGenerated, true);
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace),
                "Hiding a value from the popup must enable the column's filter.");
            Assert.AreEqual(baseline - generatedCount, GetDisplayedEntryCount(m_Window));

            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace, false);
            Assert.AreEqual(baseline, GetDisplayedEntryCount(m_Window),
                "Toggling the filter off must stop hiding rows.");
            Assert.IsTrue(m_Window.ColumnFilters.IsColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace,
                    TracingColumnFilters.k_NamespaceFilterPatternGenerated),
                "Toggling the filter off must keep its configured values.");

            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace, true);
            Assert.AreEqual(baseline - generatedCount, GetDisplayedEntryCount(m_Window),
                "Toggling the filter back on must reapply the kept configuration.");
        }

        [Test]
        public void StatusLabel_ShowsShowingFraction_OnlyWhileAFilterHidesSomething()
        {
            ClickTab("All");
            // SetUp calls CreateGUI() manually and Show() triggers a second build; the window updates the label
            // of the most recent build, so query the last match.
            var statusLabel = m_Window.rootVisualElement.Query<Label>("tracing-status-label").Last();
            Assert.NotNull(statusLabel);
            StringAssert.Contains("(showing ", statusLabel.text,
                "The default Predicted and Unmanaged filters hide rows, so the fraction is visible from the start.");

            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingPredicted, false);
            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingUnmanaged, false);
            StringAssert.DoesNotContain("(showing ", statusLabel.text,
                "Disabling the default Predicted and Unmanaged filters must remove the fraction from the status text.");

            var total = GetDisplayedEntryCount(m_Window);
            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace, true);
            StringAssert.DoesNotContain("(showing ", statusLabel.text,
                "An enabled filter that hides nothing keeps the plain status text.");

            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingNamespace,
                TracingColumnFilters.k_NamespaceFilterPatternUnityPrefix, true);
            StringAssert.Contains($"(showing {GetDisplayedEntryCount(m_Window)}/{total})", statusLabel.text,
                "A filter that hides rows must surface the shown/total fraction.");

            m_Window.ColumnFilters.SetColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace, false);
            StringAssert.DoesNotContain("(showing ", statusLabel.text,
                "Disabling the filter must remove the fraction from the status text.");
        }

        [Test]
        public void ColumnValueFilter_WorldPopup_NeverListsNone_AndIgnoresRowsWithoutWorld()
        {
            ClickTab("All");
            var values = new List<string>();
            m_Window.CollectUniqueColumnFilterValues(TracingColumnFilters.k_ColumnTracingWorld, values);

            Assert.Greater(values.Count, 0, "Expected World values from the system rows.");
            CollectionAssert.DoesNotContain(values, TracingColumnFilters.k_ColumnFilterNoValue,
                "The World popup must not list a None entry even while component rows (no world) are displayed.");

            // Even a force-hidden placeholder must not hide the world-less rows.
            var baseline = GetDisplayedEntryCount(m_Window);
            m_Window.ColumnFilters.SetColumnFilterValueHidden(TracingColumnFilters.k_ColumnTracingWorld,
                TracingColumnFilters.k_ColumnFilterNoValue, true);
            Assert.AreEqual(baseline, GetDisplayedEntryCount(m_Window),
                "Rows without a world must be ignored by the World filter.");
        }

        [Test]
        public void FilterableColumnHeaders_ShowFilterDropdownToggle_OnlyOnWorldPredictedUnmanagedAndNamespace()
        {
            var header = m_Window.TracingTreeView.Q(className: "unity-multi-column-header");
            Assert.NotNull(header, "The tree view must have a multi-column header.");

            foreach (var columnName in new[]
                     {
                         TracingColumnFilters.k_ColumnTracingWorld,
                         TracingColumnFilters.k_ColumnTracingPredicted,
                         TracingColumnFilters.k_ColumnTracingUnmanaged,
                         TracingColumnFilters.k_ColumnTracingNamespace
                     })
            {
                var headerColumn = header.Q(columnName);
                Assert.NotNull(headerColumn, $"Expected a header element for column '{columnName}'.");
                Assert.NotNull(headerColumn.Q(className: SelectTracingTargetWindow.k_UssClassTracingHeaderFilterButton),
                    $"Column '{columnName}' must show the header filter dropdown toggle.");
            }

            foreach (var columnName in new[]
                     {
                         TracingColumnFilters.k_ColumnTracingName,
                         TracingColumnFilters.k_ColumnTracingSelectAll,
                         TracingColumnFilters.k_ColumnTracingType,
                         TracingColumnFilters.k_ColumnTracingFilteringOptions
                     })
            {
                var headerColumn = header.Q(columnName);
                Assert.NotNull(headerColumn, $"Expected a header element for column '{columnName}'.");
                Assert.IsNull(headerColumn.Q(className: SelectTracingTargetWindow.k_UssClassTracingHeaderFilterButton),
                    $"Column '{columnName}' must not show the filter dropdown toggle.");
            }
        }

        [Test]
        public void ColumnValueFilter_BatchHideAllValues_EmptiesList_AndBatchRestoreBringsAllBack()
        {
            ClickTab("All");
            var baseline = GetDisplayedEntryCount(m_Window);
            Assert.Greater(baseline, 0);

            var values = new List<string>();
            m_Window.CollectUniqueColumnFilterValues(TracingColumnFilters.k_ColumnTracingNamespace, values);

            m_Window.ColumnFilters.SetColumnFilterValuesHidden(TracingColumnFilters.k_ColumnTracingNamespace, values, true);
            Assert.AreEqual(0, GetDisplayedEntryCount(m_Window),
                "Hiding every unique value of a column (disable all) should hide every row.");

            m_Window.ColumnFilters.SetColumnFilterValuesHidden(TracingColumnFilters.k_ColumnTracingNamespace, values, false);
            Assert.AreEqual(baseline, GetDisplayedEntryCount(m_Window),
                "Re-showing every value (enable all) should restore the full list.");
            Assert.IsFalse(m_Window.ColumnFilters.IsColumnFilterEnabled(TracingColumnFilters.k_ColumnTracingNamespace),
                "Re-ticking everything (All) must drop the filter toggle back to off.");
        }

        [Test]
        public void ColumnValueFilter_WorldFilter_HidesOnlySystemRows_AndDoesNotLeakIntoComponentsTab()
        {
            ClickTab("Components");
            var componentsBaseline = GetDisplayedEntryCount(m_Window);

            ClickTab("All");
            var allBaseline = GetDisplayedEntryCount(m_Window);
            var systemRows = m_Window.DisplayEntries.Count(e => e.Kind == TracingTargetKind.System);
            Assert.Greater(systemRows, 0, "Expected system rows on the All tab.");
            Assert.Greater(allBaseline, systemRows, "Expected component rows (no world) on the All tab.");

            var values = new List<string>();
            m_Window.CollectUniqueColumnFilterValues(TracingColumnFilters.k_ColumnTracingWorld, values);
            m_Window.ColumnFilters.SetColumnFilterValuesHidden(TracingColumnFilters.k_ColumnTracingWorld, values, true);
            Assert.AreEqual(allBaseline - systemRows, GetDisplayedEntryCount(m_Window),
                "Hiding every World value must hide exactly the system rows; rows without a world are ignored.");

            // The World column is hidden on the Components tab, so its filter must not apply there.
            ClickTab("Components");
            Assert.AreEqual(componentsBaseline, GetDisplayedEntryCount(m_Window),
                "A World-column filter must not leak into the Components tab where that column is hidden.");

            ClickTab("All");
            m_Window.ColumnFilters.SetColumnFilterValuesHidden(TracingColumnFilters.k_ColumnTracingWorld, values, false);
            Assert.AreEqual(allBaseline, GetDisplayedEntryCount(m_Window));
        }

        [Test]
        public void ComponentsTab_SearchTagKeyword_MatchesByFullNameOrByTagKindFilter()
        {
            const string query = "tag";
            ClickTab("Components");
            SetSearchQuery(query);
            Assert.Greater(GetDisplayedEntryCount(m_Window), 0, "Expected some components when searching 'tag'.");

            foreach (var type in EnumerateDisplayedTypes(m_Window))
            {
                var fullName = type.FullName ?? string.Empty;
                var nameMatches = fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                var shape = TracingTargetTypes.GetComponentDataShape(type);
                var kindFilterMatches = SelectTracingTargetWindow.SearchMatchesComponentKindFilter(query, shape);
                Assert.IsTrue(nameMatches || kindFilterMatches, $"Type {type.FullName} should match '{query}' by full-name substring (same as PassesSearch) or by tag/data kind filter; shape={shape}.");
            }
        }

        static void AssignStyleSheetsFromPackageGuids(SelectTracingTargetWindow window)
        {
            var so = new SerializedObject(window);
            AssignIfMissing(so, "m_StyleSheet", k_MainStyleSheetGuid);
            AssignIfMissing(so, "m_VariablesDarkSheet", k_VariablesDarkSheetGuid);
            AssignIfMissing(so, "m_VariablesLightSheet", k_VariablesLightSheetGuid);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void AssignIfMissing(SerializedObject so, string propertyName, string guid)
        {
            var prop = so.FindProperty(propertyName);
            Assert.NotNull(prop, $"Missing serialized property {propertyName} on SelectTracingTargetWindow.");
            if (prop.objectReferenceValue != null)
            {
                return;
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            Assert.IsFalse(string.IsNullOrEmpty(path), $"Could not resolve stylesheet guid {guid}");
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            Assert.NotNull(sheet, $"Could not load StyleSheet at {path}");
            prop.objectReferenceValue = sheet;
        }

        static TracingTargetSelectionFile CloneSelection(TracingTargetSelectionFile src)
        {
            var dst = new TracingTargetSelectionFile();
            if (src?.entries == null)
            {
                return dst;
            }

            foreach (var e in src.entries)
            {
                dst.entries.Add(new TracingTargetSelectionEntry
                {
                    assemblyQualifiedName = e.assemblyQualifiedName,
                    kind = e.kind,
                    required = e.required
                });
            }

            return dst;
        }

        void ClickTab(string tabName)
        {
            var tab = Enum.Parse<SelectTracingTargetWindow.TabKind>(tabName);
            m_Window.OnTabClicked(tab);
        }

        void SetSearchQuery(string query)
        {
            var field = m_Window.rootVisualElement.Query<UnityEditor.UIElements.ToolbarSearchField>().Last();
            Assert.NotNull(field, "The search field must exist in the window.");
            field.value = query ?? string.Empty;
        }

        static int GetDisplayedEntryCount(SelectTracingTargetWindow window) => window.DisplayEntries.Count;

        static Type FirstDisplayedTypeOrNull(SelectTracingTargetWindow window)
        {
            foreach (var t in EnumerateDisplayedTypes(window))
            {
                return t;
            }

            return null;
        }

        static IEnumerable<Type> EnumerateDisplayedTypes(SelectTracingTargetWindow window)
        {
            foreach (var e in window.DisplayEntries)
            {
                yield return e.Type;
            }
        }

        static int CountDisplayedEntriesWithKind(SelectTracingTargetWindow window, string tracingTargetKindName)
        {
            var expected = Enum.Parse<TracingTargetKind>(tracingTargetKindName);
            var count = 0;
            foreach (var e in window.DisplayEntries)
            {
                if (e.Kind == expected)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Tracing keeps process-wide static state (config + the processed-data flag); when domain reload is disabled it
    /// must be reset explicitly on reload, otherwise tests and the tool itself inherit stale state.
    /// </summary>
    [TestFixture]
    internal class TracingStaticStateResetTests
    {
        [Test]
        public void ResetStaticState_ClearsProcessedAndForceProcessingFlags()
        {
            TracingDataAccess.IsProcessed = true;
            TracingDataAccess.m_ForceProcessingOverMultipleFrames = true;

            TracingDataAccess.ResetStaticState();

            Assert.IsFalse(TracingDataAccess.IsProcessed, "IsProcessed must be cleared by ResetStaticState.");
            Assert.IsFalse(TracingDataAccess.m_ForceProcessingOverMultipleFrames, "The processing-over-multiple-frames test override must be cleared by ResetStaticState.");
        }

        [UnityTest]
#if ENABLE_CORECLR
        [Explicit("CoreCLR: entering play mode fails domain reload because BindingRegistryLiveProperties..cctor throws MethodAccessException constructing the internal UnityEditor.InspectorUtility.LivePropertyChangedCallback delegate, see https://jira.unity3d.com/browse/UUM-150429")]
#endif
        public IEnumerator EnteringPlayMode_ResetsProcessedFlag_EvenWithDomainReloadDisabled()
        {
            TracingDataAccess.IsProcessed = true;

            yield return new EnterPlayMode();

            // With domain reload disabled, the AfterAssembliesLoaded reset is the only thing that clears this flag on play-mode enter.
            Assert.IsFalse(TracingDataAccess.IsProcessed,
                "Entering play mode must reset IsProcessed so tracing state does not leak across play sessions when domain reload is disabled.");

            yield return new ExitPlayMode();
        }
    }

}

// Namespace deliberately contains 'Generated' so the Namespace popup's '*Generated*' pattern-filter tests
// always have at least one matching row.
namespace Tests.Editor.TracingGeneratedNamespaceFixture
{
    internal struct PlainNameTracingComponent : Unity.Entities.IComponentData
    {
        public int Value;
    }
}

// Managed (class) component fixture: listed by the picker, hidden by the default Unmanaged column filter, and
// never value-captured by tracing. Class components are illegal when managed components are disabled.
#if !UNITY_DISABLE_MANAGED_COMPONENTS
namespace Tests.Editor.TracingManagedComponentFixture
{
    internal class ManagedTracingFixtureComponent : Unity.Entities.IComponentData
    {
        public int Value;
    }
}
#endif
