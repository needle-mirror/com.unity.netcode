using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode;
using Unity.NetCode.LowLevel.StateSave;
using Unity.NetCode.Tests;
using Unity.NetCode.Tests.Performance;
using Unity.PerformanceTesting;
using Unity.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace Tests.Editor
{

    internal struct TestComponentA : IComponentData
    {
        public int value;
    }

    internal struct TestComponentB : IComponentData
    {
        public bool valueBool;
        public byte valueByte;
        public sbyte valueSbyte;
        public double valueDouble;
        public float valueFloat;
        public int valueInt;
        public uint valueUint;
        public nint valueNint;
        public nuint valueNuint;
        public long valueLong;
        public ulong valueUlong;
        public short valueShort;
        public ushort valueUshort;
    }

    internal struct TestEnablebleComponent : IComponentData, IEnableableComponent
    {
        public int value;
    }

    internal struct TestBufferA : IBufferElementData, IEnableableComponent
    {
        public int value;
    }

    internal struct TestBufferB : IBufferElementData
    {
        public bool valueBool;
        public byte valueByte;
        public sbyte valueSbyte;
        public double valueDouble;
        public float valueFloat;
        public int valueInt;
        public uint valueUint;
        public nint valueNint;
        public nuint valueNuint;
        public long valueLong;
        public ulong valueUlong;
        public short valueShort;
        public ushort valueUshort;
    }

    internal struct PerfTestStruct
    {
        public ulong a;
        public ulong b;
    }

    internal struct PerfTestComp1 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp2 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp3 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp4 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp5 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp6 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp7 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp8 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp9 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp10 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp11 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp12 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp13 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp14 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp15 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp16 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp17 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp18 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp19 : IComponentData { public PerfTestStruct value; }
    internal struct PerfTestComp20 : IComponentData { public PerfTestStruct value; }

    internal struct TestStateSaveSingleton : IComponentData
    {
        internal WorldStateSave stateSaveToTest;
        public StateSaveTests.SaveStrategyToUse StrategyToUse;
        public int Count;
    }

    // non bursted system to just expose a callback that's defined in the test itself, for clarity
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [DisableAutoCreation]
    internal partial class BehaviourTestCreateStateSaveSystem : SystemBase
    {
        public delegate void StateSaveTest(BehaviourTestCreateStateSaveSystem system, Entity singletonEntity);
        public StateSaveTest StateSaveTestCallback;
        internal WorldStateSave stateToDispose;
        protected override void OnCreate()
        {
            Enabled = false;
            EntityManager.CreateEntity(typeof(TestStateSaveSingleton));
        }

        protected override void OnUpdate()
        {
            try
            {
                StateSaveTestCallback?.Invoke(this, SystemAPI.GetSingletonEntity<TestStateSaveSingleton>());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
        }

        protected override void OnDestroy()
        {
            var config = SystemAPI.GetSingleton<TestStateSaveSingleton>();
            if (config.stateSaveToTest.Initialized)
                config.stateSaveToTest.Dispose();
            if (stateToDispose.Initialized)
                stateToDispose.Dispose();
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateAfter(typeof(GhostSendSystem))]
    [DisableAutoCreation]
    [BurstCompile]
    internal partial struct PerfTestCreateStateSave : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
            state.EntityManager.CreateEntity(typeof(TestStateSaveSingleton));
        }

        [BurstCompile]
        internal struct ValidateAfterDependency : IJob
        {
            public int Count;
            internal WorldStateSave StateSave;
            public int TypeCount;

            [BurstCompile]
            public void Execute()
            {
                StateSaveTests.ValidateStateSave(Count, StateSave, TypeCount);
            }
        }

        public const string k_MarkerName = "StateSavePerfTest";
        static readonly ProfilerMarker s_PerfTestMarker = new(k_MarkerName);
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            // Setup
            var testStateSaveSingleton = SystemAPI.GetSingleton<TestStateSaveSingleton>();
            var strategyToUse = testStateSaveSingleton.StrategyToUse;
            var requiredTypesToSave = new NativeHashSet<ComponentType>(5, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), ComponentType.ReadOnly<TestEnablebleComponent>(), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp1>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp2>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp3>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp4>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp5>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp6>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp7>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp8>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp9>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp10>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp11>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp12>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp13>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp14>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp15>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp16>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp17>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp18>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp19>());
            requiredTypesToSave.Add(ComponentType.ReadOnly<PerfTestComp20>());

            // Test
            s_PerfTestMarker.Begin(); // Not including the above list allocations in the perf test as those shouldn't happen all the time anymore
            WorldStateSave stateSave;
            switch (strategyToUse)
            {
                case StateSaveTests.SaveStrategyToUse.Default:
                {
                    stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).Initialize(ref state);
                    state.Dependency = stateSave.ScheduleStateSaveJob(ref state);
                    break;
                }
                case StateSaveTests.SaveStrategyToUse.Indexed:
                {
                    var strategy = new IndexedByGhostSaveStrategy(SystemAPI.GetComponentTypeHandle<GhostInstance>());
                    stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).Initialize(ref state, strategy);
                    state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                    break;
                }

                default:
                    throw new NotImplementedException($"{strategyToUse} not implemented");
            }
            // state.Dependency.Complete(); // TODO should we complete as part of the perf test?
            s_PerfTestMarker.End();

            state.Dependency = new ValidateAfterDependency(){Count = testStateSaveSingleton.Count, StateSave = stateSave, TypeCount = strategyToUse == StateSaveTests.SaveStrategyToUse.Indexed ? 26 : 25 }.Schedule(state.Dependency);
            // Validate in test
            SystemAPI.SetSingleton(new TestStateSaveSingleton() { stateSaveToTest = stateSave });
            state.Enabled = false;
        }

        public void OnDestroy(ref SystemState state)
        {
            SystemAPI.GetSingleton<TestStateSaveSingleton>().stateSaveToTest.Dispose();
        }
    }

    internal class StateSaveTestDataConverter : TestNetCodeAuthoring.IConverter
    {
        public List<ComponentType> typesToAdd = new();
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent<Data>(entity);
            foreach (var componentType in typesToAdd)
            {
                baker.AddComponent(entity, componentType);
            }
        }
    }

    internal class StateSaveTests
    {
        public enum SaveStrategyToUse
        {
            Default,
            Indexed
        }

        static void UpdateTestComponents(int index, Entity entity, NetCodeTestWorld testWorld)
        {
            var em = testWorld.ServerWorld.EntityManager;
            if (em.HasComponent<TestComponentA>(entity))
            {
                em.SetComponentData(entity, new TestComponentA()
                {
                    value = index * 2
                });
            }

            if (em.HasComponent<TestComponentB>(entity))
            {
                em.SetComponentData(entity, GetTestComponentB(index));
            }

            if (em.HasComponent<TestEnablebleComponent>(entity))
            {
                em.SetComponentEnabled<TestEnablebleComponent>(entity, index % 2 == 0);
            }

            if (em.HasBuffer<TestBufferA>(entity))
            {
                var buf = em.GetBuffer<TestBufferA>(entity);
                for (int i = 0; i < 10; ++i)
                {
                    buf.Add(new TestBufferA()
                    {
                        value = index * 2 + i
                    });
                }
                em.SetComponentEnabled<TestBufferA>(entity, index % 2 == 0);
            }

            if (em.HasBuffer<TestBufferB>(entity))
            {
                var buf = em.GetBuffer<TestBufferB>(entity);
                for (int i = 0; i < 10; ++i)
                    buf.Add(GetTestBufferB(index, i));
            }
        }

        static TestComponentB GetTestComponentB(int index)
        {
            return new TestComponentB()
            {
                valueBool = true,
                valueByte = (byte)(index),
                valueSbyte = (sbyte)(index),
                valueDouble = 1.234 + index,
                valueFloat = 1.234f + index,
                valueInt = index,
                valueUint = (uint)(index),
                valueNint = index,
                valueNuint = (nuint)(index),
                valueLong = index,
                valueUlong = (ulong)(index),
                valueShort = (short)(index),
                valueUshort = (ushort)(index),
            };
        }

        static TestBufferB GetTestBufferB(int index, int bufIndex)
        {
            return new TestBufferB()
            {
                valueBool = true,
                valueByte = (byte)(index + bufIndex),
                valueSbyte = (sbyte)(index + bufIndex),
                valueDouble = 1.234 + index + bufIndex,
                valueFloat = 1.234f + index + bufIndex,
                valueInt = index + bufIndex,
                valueUint = (uint)(index + bufIndex),
                valueNint = index + bufIndex,
                valueNuint = (nuint)(index + bufIndex),
                valueLong = index + bufIndex,
                valueUlong = (ulong)(index + bufIndex),
                valueShort = (short)(index + bufIndex),
                valueUshort = (ushort)(index + bufIndex),
            };
        }

        static void ValidateCompB(in TestComponentB compB, int index)
        {
            Unity.Assertions.Assert.AreEqual(true, compB.valueBool);
            Unity.Assertions.Assert.AreEqual((byte)index, compB.valueByte);
            Unity.Assertions.Assert.AreEqual((sbyte)index, compB.valueSbyte);
            Unity.Assertions.Assert.AreEqual(1.234 + index, compB.valueDouble);
            Unity.Assertions.Assert.AreEqual(1.234f + index, compB.valueFloat);
            Unity.Assertions.Assert.AreEqual(index, compB.valueInt);
            Unity.Assertions.Assert.AreEqual((uint)index, compB.valueUint);
            Unity.Assertions.Assert.AreEqual((nint)index, compB.valueNint);
            Unity.Assertions.Assert.AreEqual((nuint)(index), compB.valueNuint);
            Unity.Assertions.Assert.AreEqual(index, compB.valueLong);
            Unity.Assertions.Assert.AreEqual((ulong)index, compB.valueUlong);
            Unity.Assertions.Assert.AreEqual((short)index, compB.valueShort);
            Unity.Assertions.Assert.AreEqual((ushort)index, compB.valueUshort);
        }

        static void ValidateBufferB(in TestBufferB compB, int index, int bufIndex)
        {
            int value = index + bufIndex;
            Unity.Assertions.Assert.AreEqual(true, compB.valueBool);
            Unity.Assertions.Assert.AreEqual((byte)value, compB.valueByte);
            Unity.Assertions.Assert.AreEqual((sbyte)value, compB.valueSbyte);
            Unity.Assertions.Assert.AreApproximatelyEqual(1.234f + value, (float)compB.valueDouble);
            Unity.Assertions.Assert.AreApproximatelyEqual(1.234f + value, compB.valueFloat);
            Unity.Assertions.Assert.AreEqual(value, compB.valueInt);
            Unity.Assertions.Assert.AreEqual((uint)value, compB.valueUint);
            Unity.Assertions.Assert.AreEqual((nint)value, compB.valueNint);
            Unity.Assertions.Assert.AreEqual((nuint)(value), compB.valueNuint);
            Unity.Assertions.Assert.AreEqual(value, compB.valueLong);
            Unity.Assertions.Assert.AreEqual((ulong)value, compB.valueUlong);
            Unity.Assertions.Assert.AreEqual((short)value, compB.valueShort);
            Unity.Assertions.Assert.AreEqual((ushort)value, compB.valueUshort);
        }

        [BurstDiscard]
        static void ThrowBursted()
        {
            throw new Exception("noooo");
        }

        const string k_ReadStateMarkerName = "Read State Perf Test";
        static readonly ProfilerMarker m_ReadStateMarker = new(k_ReadStateMarkerName);

        internal static unsafe void ValidateStateSave(int count, in WorldStateSave stateSave, int expectedTypeCount)
        {
            using var a = m_ReadStateMarker.Auto();
            var iterationCount = 0;
            foreach (var entry in stateSave)
            {
                Unity.Assertions.Assert.AreEqual(expectedTypeCount, entry.types.Length, "types count in state save mismatch");
                foreach (var compData in entry)
                {
                    var componentType = compData.Type;
                    if (componentType == ComponentType.ReadOnly<TestComponentA>())
                    {
                        compData.ToConcrete(out TestComponentA compA);
                        Unity.Assertions.Assert.AreEqual(iterationCount * 2, compA.value);
                    }

                    if (componentType == ComponentType.ReadOnly<TestComponentB>())
                    {
                        compData.ToConcrete(out TestComponentB compB);
                        ValidateCompB(compB, iterationCount);
                    }

                    if (componentType == ComponentType.ReadOnly<TestEnablebleComponent>())
                    {
                        Unity.Assertions.Assert.AreEqual(iterationCount % 2 == 0, compData.Enabled);
                    }

                    if (componentType == ComponentType.ReadOnly<TestBufferA>())
                    {
                        var bufA = new NativeList<TestBufferA>(compData.Length, Allocator.Temp);
                        compData.ToConcrete(ref bufA);
                        for (int i = 0; i < bufA.Length; ++i)
                            Unity.Assertions.Assert.AreEqual(iterationCount * 2 + i, bufA[i].value);
                        Unity.Assertions.Assert.AreEqual(iterationCount % 2 == 0, compData.Enabled);
                    }

                    if (componentType == ComponentType.ReadOnly<TestBufferB>())
                    {
                        var bufB = new NativeList<TestBufferB>(compData.Length, Allocator.Temp);
                        compData.ToConcrete(ref bufB);
                        for (int i = 0; i < bufB.Length; ++i)
                            ValidateBufferB(bufB[i], iterationCount, i);
                    }
                }

                entry.types.Dispose(); // Editor should crash if this actually tries to dispose the main allocation. The editor not crashing means we pass :)
                iterationCount++;
            }
            Unity.Assertions.Assert.AreEqual(count, iterationCount, "state save entry count mismatch");
        }

        void SimpleStateSave(BehaviourTestCreateStateSaveSystem system, Entity singletonEntity)
        {
            // Setup
            ref var state = ref system.CheckedStateRef;
            var config = state.EntityManager.GetComponentData<TestStateSaveSingleton>(singletonEntity);
            var requiredTypesToSave = new NativeHashSet<ComponentType>(5, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), ComponentType.ReadOnly<TestEnablebleComponent>(), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };

            // Test
            WorldStateSave stateSave;
            switch (config.StrategyToUse)
            {
                case SaveStrategyToUse.Default:
                {
                    // no need to specify strategy with default
                    stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).Initialize(ref state);
                    state.Dependency = stateSave.ScheduleStateSaveJob(ref state);
                    break;
                }
                case SaveStrategyToUse.Indexed:
                {
                    var strategy = new IndexedByGhostSaveStrategy(state.GetComponentTypeHandle<GhostInstance>());
                    stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).Initialize(ref state, strategy);
                    state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                    break;
                }

                default:
                    throw new NotImplementedException($"{config.StrategyToUse} not implemented");
            }
            state.Dependency.Complete();

            // Validate in test
            config.stateSaveToTest = stateSave;
            state.EntityManager.SetComponentData(singletonEntity, config);
            state.Enabled = false;
        }

        [Test, Description("Test state save with direct, no indexing, saving and make sure values are saved correctly")]
        public unsafe void StateSave_DefaultStrategy_Works([Values(0, 1, 5, 100, 500)] int count)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));

            testWorld.CreateWorlds(true, 1);

            var createStateSystem = testWorld.ServerWorld.GetExistingSystemManaged<BehaviourTestCreateStateSaveSystem>();
            createStateSystem.StateSaveTestCallback = SimpleStateSave;
            testWorld.GetSingletonRW<TestStateSaveSingleton>(testWorld.ServerWorld).ValueRW.StrategyToUse = SaveStrategyToUse.Default;
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < count; i++)
            {
                var ent = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(TestComponentA), typeof(TestComponentB), typeof(TestEnablebleComponent), typeof(TestBufferA), typeof(TestBufferB));
                UpdateTestComponents(i, ent, testWorld);
            }

            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;
            var result = testWorld.GetSingleton<TestStateSaveSingleton>(testWorld.ServerWorld);
            ValidateStateSave(count, result.stateSaveToTest, expectedTypeCount: 5);
        }

        [Test, Description("Test state save with a buffer created in another state save")]
        public unsafe void StateSave_InitializeWithBuffer_Works()
        {
            const int entityCount = 100;
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));

            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();

            var requiredTypesToSave = new NativeHashSet<ComponentType>(5, Allocator.Temp)
            {
                ComponentType.ReadOnly<TestComponentA>(),
                ComponentType.ReadOnly<TestComponentB>(),
                ComponentType.ReadOnly<TestEnablebleComponent>(),
                ComponentType.ReadOnly<TestBufferA>(),
                ComponentType.ReadOnly<TestBufferB>()
            };
            for (int i = 0; i < entityCount; i++)
            {
                var ent = testWorld.ServerWorld.EntityManager.CreateEntity(requiredTypesToSave.ToNativeArray(Allocator.Temp));
                UpdateTestComponents(i, ent, testWorld);
            }

            var stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave);
            stateSave.Initialize(ref testWorld.ServerWorld.Unmanaged.GetExistingSystemState<BehaviourTestCreateStateSaveSystem>());
            var stateSaveJob = stateSave.ScheduleStateSaveJob(ref testWorld.ServerWorld.Unmanaged.GetExistingSystemState<BehaviourTestCreateStateSaveSystem>());
            stateSaveJob.Complete();

            // It's assumed the buffer would normally be coming from some byte array, from disk or over the network for example
            var stateSaveBuffer = new NativeArray<byte>(stateSave.AsSpan.Length, Allocator.Temp);
            var originalEntityCount = stateSave.EntityCount;
            var originalSpanLength = stateSave.AsSpan.Length;
            var originalSize = stateSave.Size;
            stateSave.AsSpan.CopyTo(stateSaveBuffer);
            stateSave.Reset();

            var restoredStateSave = new WorldStateSave(Allocator.Persistent).InitializeFromBuffer(stateSaveBuffer);
            Assert.AreEqual(originalEntityCount, restoredStateSave.EntityCount, "Restored state save does not contain correct entity count");
            Assert.AreEqual(originalSpanLength, restoredStateSave.AsSpan.Length);
            Assert.AreEqual(originalSize, restoredStateSave.Size);
            Assert.AreEqual(0, restoredStateSave.OptionalTypesToSaveConfig.Count);
            Assert.AreEqual(0, restoredStateSave.RequiredTypesToSaveConfig.Count);
            ValidateStateSave(entityCount, restoredStateSave, expectedTypeCount: requiredTypesToSave.Count);
            restoredStateSave.Dispose();
        }

        static DynamicBuffer<NetCodeTestPrefab> SetupWithPrefab(NetCodeTestWorld testWorld, List<ComponentType> typesToAdd = null)
        {
            if (typesToAdd == null)
                typesToAdd = new List<ComponentType>() { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), ComponentType.ReadOnly<TestEnablebleComponent>(), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };
            // Predicted ghost
            var predictedGhostGO = new GameObject("PredictedGO");
            predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new StateSaveTestDataConverter() { typesToAdd = typesToAdd};
            var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
            ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
            ghostConfig.HasOwner = true;

            Assert.IsTrue(testWorld.CreateGhostCollection(predictedGhostGO));

            testWorld.CreateWorlds(true, 1);
            testWorld.GetSingletonRW<TestStateSaveSingleton>(testWorld.ServerWorld).ValueRW.StrategyToUse = SaveStrategyToUse.Indexed;
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 16; i++)
            {
                testWorld.Tick();
            }
            var prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
            var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
            var prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);
            return prefabs;
        }

        [Test, Description("Test state save with indexed saving and make sure values are saved correctly and accessible by their index too")]
        public void StateSave_IndexedStrategy_Works([Values(0, 1, 5, 100, 500)] int count)
        {
            // indexing saved state by ghost ID
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));

            // Setup
            var prefabs = SetupWithPrefab(testWorld);
            var createStateSystem = testWorld.ServerWorld.GetExistingSystemManaged<BehaviourTestCreateStateSaveSystem>();
            createStateSystem.StateSaveTestCallback = SimpleStateSave;

            List<Entity> allEntities = new List<Entity>();
            for (int i = 0; i < count; i++)
            {
                var ent = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);
                allEntities.Add(ent);
                UpdateTestComponents(i, ent, testWorld);
            }

            testWorld.Tick();

            // Test
            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;

            var result = testWorld.GetSingleton<TestStateSaveSingleton>(testWorld.ServerWorld);
            ValidateStateSave(count, result.stateSaveToTest, expectedTypeCount: 6); // 6 types. GhostInstance (for the indexing), TestComponentA, TestComponentB, TestEnableableComponent, TestBufferA and TestBufferB

            ValidateIndexedStateSave(count, allEntities, testWorld, result.stateSaveToTest, expectedTypesCount: 6);
        }

        static unsafe void ValidateIndexedStateSave(int count, List<Entity> allEntities, NetCodeTestWorld testWorld, WorldStateSave stateSaveToTest, int expectedTypesCount)
        {
            using var allObjects = stateSaveToTest.GetAllEntities(Allocator.Temp);
            using var entitiesInStateSave = new NativeHashSet<SavedEntityID>(count, Allocator.Temp);
            for (int i = 0; i < count; i++)
            {
                entitiesInStateSave.Add(allObjects[i]);
            }
            for (int i = 0; i < count; i++)
            {
                var ent = allEntities[i];
                var ghostInstance = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(ent);
                var savedEntityID = new SavedEntityID(ghostInstance);
                var types = stateSaveToTest.GetComponentTypes(savedEntityID);
                Assert.AreEqual(expectedTypesCount, types.Length);
                Assert.That(types.Contains(ComponentType.ReadOnly<GhostInstance>()));
                Assert.That(types.Contains(ComponentType.ReadOnly<TestComponentA>()));
                Assert.That(types.Contains(ComponentType.ReadOnly<TestComponentB>()));
                Assert.That(types.Contains(ComponentType.ReadOnly<TestEnablebleComponent>()));
                Assert.That(types.Contains(ComponentType.ReadOnly<TestBufferA>()));
                Assert.That(types.Contains(ComponentType.ReadOnly<TestBufferB>()));
                Assert.That(entitiesInStateSave.Contains(savedEntityID));
                Assert.That(stateSaveToTest.Exists(savedEntityID));
                Assert.That(stateSaveToTest.HasComponent(savedEntityID, ComponentType.ReadOnly<GhostInstance>()));
                Assert.That(stateSaveToTest.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentA>()));
                Assert.That(stateSaveToTest.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentB>()));
                Assert.That(stateSaveToTest.HasComponent(savedEntityID, ComponentType.ReadOnly<TestEnablebleComponent>()));
                Assert.That(stateSaveToTest.HasComponent(savedEntityID, ComponentType.ReadOnly<TestBufferA>()));
                Assert.That(stateSaveToTest.HasComponent(savedEntityID, ComponentType.ReadOnly<TestBufferB>()));
                Assert.IsTrue(stateSaveToTest.TryGetComponentData<GhostInstance>(savedEntityID, out var data));
                Assert.AreEqual(ghostInstance, data);
                Assert.IsTrue(stateSaveToTest.TryGetComponentData<TestComponentA>(savedEntityID, out var savedCompAGeneric));
                Assert.AreEqual(i * 2, savedCompAGeneric.value);
                Assert.IsTrue(stateSaveToTest.TryGetComponentData<TestComponentB>(savedEntityID, out var savedCompBGeneric));
                ValidateCompB(savedCompBGeneric, i);
                Assert.IsTrue(stateSaveToTest.TryGetComponentData(savedEntityID, ComponentType.ReadOnly<GhostInstance>(), out var ghostInstancePtr));
                UnsafeUtility.CopyPtrToStructure(ghostInstancePtr, out GhostInstance savedGhostInstance);
                Assert.AreEqual(ghostInstance, savedGhostInstance);
                Assert.IsTrue(stateSaveToTest.TryGetComponentData(savedEntityID, ComponentType.ReadOnly<TestComponentA>(), out var compAPtr));
                UnsafeUtility.CopyPtrToStructure(compAPtr, out TestComponentA savedCompA);
                Assert.AreEqual(i * 2, savedCompA.value);
                Assert.IsTrue(stateSaveToTest.TryGetComponentData(savedEntityID, ComponentType.ReadOnly<TestComponentB>(), out var compBPtr));
                UnsafeUtility.CopyPtrToStructure(compBPtr, out TestComponentB savedCompB);
                ValidateCompB(savedCompB, i);
            }
        }

        [Test, Description("Tests that optional types work as well and that we can have entities with different archetypes in the same save")]
        public void StateSave_OptionalTypes_Works()
        {
            // Setup
            var count = 200; // should be more than one chunk
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));
            testWorld.CreateWorlds(true, 1);
            ref var testStateSaveSingleton = ref testWorld.GetSingletonRW<TestStateSaveSingleton>(testWorld.ServerWorld).ValueRW;
            testStateSaveSingleton.StrategyToUse = SaveStrategyToUse.Default;
            testWorld.Connect();
            testWorld.GoInGame();

            List<Entity> allEntities = new();
            for (int i = 0; i < count; i++)
            {
                var ent = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(TestComponentA));
                if (i > count / 2f - 1)
                {
                    testWorld.ServerWorld.EntityManager.AddComponentData(ent, new TestComponentB());
                }
                allEntities.Add(ent);
                UpdateTestComponents(i, ent, testWorld);
            }

            // testing assumption that all entities creates more than 1 chunk
            Assert.That(testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(TestComponentA)).CalculateChunkCount(), Is.GreaterThan(1));

            var createStateSystem = testWorld.ServerWorld.GetExistingSystemManaged<BehaviourTestCreateStateSaveSystem>();
            // Test
            createStateSystem.Enabled = true;
            WorldStateSave stateSave = default;
            // Test optional types
            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(1, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>() };
                var optionalTypes = new NativeHashSet<ComponentType>(1, Allocator.Temp) { ComponentType.ReadOnly<TestComponentB>() };

                // Test
                var strategy = new DirectStateSaveStrategy();
                stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).WithOptionalTypes(optionalTypes).Initialize(ref state, strategy);

                system.stateToDispose = stateSave;
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            testWorld.Tick();
            createStateSystem.Enabled = false;

            // Validate
            Assert.That(stateSave.Initialized);
            Assert.AreEqual(count, stateSave.EntityCount);
            // count components
            var compACount = 0;
            var compBCount = 0;
            foreach (var entry in stateSave)
            {
                if (entry.types.Contains(ComponentType.ReadOnly<TestComponentA>())) compACount++;
                if (entry.types.Contains(ComponentType.ReadOnly<TestComponentB>())) compBCount++;
            }
            Assert.AreEqual(count, compACount);
            Assert.AreEqual(count / 2f, compBCount);

            // Test no required types
            createStateSystem.Enabled = true;
            stateSave.Dispose();
            Assert.That(!stateSave.Initialized);
            stateSave = default;
            Assert.That(!stateSave.Initialized);
            // Test optional types
            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(0, Allocator.Temp);
                var optionalTypes = new NativeHashSet<ComponentType>(1, Allocator.Temp) { ComponentType.ReadOnly<TestComponentB>() };

                // Test
                var strategy = new DirectStateSaveStrategy();
                stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).WithOptionalTypes(optionalTypes).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            testWorld.Tick();
            createStateSystem.Enabled = false;
            Assert.That(stateSave.Initialized);
            Assert.AreEqual(count / 2f, stateSave.EntityCount);
            // count components
            compACount = 0;
            compBCount = 0;
            foreach (var entry in stateSave)
            {
                if (entry.types.Contains(ComponentType.ReadOnly<TestComponentA>())) compACount++;
                if (entry.types.Contains(ComponentType.ReadOnly<TestComponentB>())) compBCount++;
            }
            Assert.AreEqual(0, compACount);
            Assert.AreEqual(count / 2f, compBCount);
        }

        [Test, Description("Test various error cases for state save")]
        public unsafe void StateSave_TestErrors()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));
            var prefabs = SetupWithPrefab(testWorld, new List<ComponentType>(){ ComponentType.ReadOnly<TestComponentA>()});

            var createStateSystem = testWorld.ServerWorld.GetExistingSystemManaged<BehaviourTestCreateStateSaveSystem>();

            var entity = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);
            testWorld.Tick();

            WorldStateSave stateSave = default;
            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(1, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>() };
                var optionalTypes = new NativeHashSet<ComponentType>(1, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>() }; // test duplicate type
                Assert.Throws<ArgumentException>(() =>
                {
                    ref SystemState s = ref system.CheckedStateRef;
                    stateSave = new WorldStateSave(Allocator.Temp).WithRequiredTypes(requiredTypesToSave).WithOptionalTypes(optionalTypes).Initialize(ref s);
                });
                Unity.Assertions.Assert.IsFalse(stateSave.Initialized);

                // Test having no required or optional types throws an error
                Assert.Throws<ArgumentException>(() =>
                {
                    ref SystemState s = ref system.CheckedStateRef;
                    stateSave = new WorldStateSave(Allocator.Temp).Initialize(ref s);
                });
                Unity.Assertions.Assert.IsFalse(stateSave.Initialized);

                optionalTypes = new NativeHashSet<ComponentType>(0, Allocator.Temp); // fix the issue for the next step

                // Test
                var strategy = new IndexedByGhostSaveStrategy(state.GetComponentTypeHandle<GhostInstance>());
                stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).WithOptionalTypes(optionalTypes).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };

            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;

            Assert.That(stateSave.Initialized);
            // Test trying to access a type that's not present (comp B is not there)
            Assert.That(!testWorld.ServerWorld.EntityManager.HasComponent<TestComponentB>(entity));
            var savedGhostID = new SavedEntityID(testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(entity));
            var savedTypes = stateSave.GetComponentTypes(savedGhostID);
            Assert.AreEqual(2, savedTypes.Length);
            Assert.That(savedTypes.Contains(ComponentType.ReadOnly<GhostInstance>()));
            Assert.That(savedTypes.Contains(ComponentType.ReadOnly<TestComponentA>()));
            Assert.IsFalse(stateSave.TryGetComponentData(savedGhostID, ComponentType.ReadOnly<TestComponentB>(), out var _));
            Assert.IsFalse(stateSave.TryGetComponentData<TestComponentB>(savedGhostID, out var _));

            savedTypes.Dispose(); // make sure we don't crash, this should no-op as this array's memory is just a slice of the main state save allocation
            // Test initializing a second time the same state save
            Assert.Throws<InvalidOperationException>(() =>
            {
                SystemState s = default;
                stateSave.WithRequiredTypes(new(1, Allocator.Temp) { ComponentType.ReadOnly<GhostInstance>() }).Initialize(ref s);
            });
        }

        [Test, Description("We can reuse a state save allocation between different saves to avoid the perf hit of allocating. testing this works here will reuse, then test the reuse doesn't work if we need more space or need too little space. will test this by growing and shrinking the amount of entities to save")]
        public unsafe void StateSave_ReuseMemoryAllocation_Works([Values(100, 200)] int count)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));

            testWorld.CreateWorlds(true, 1);

            var createStateSystem = testWorld.ServerWorld.GetExistingSystemManaged<BehaviourTestCreateStateSaveSystem>();
            testWorld.Connect();
            testWorld.GoInGame();

            var allEntities = new List<Entity>();
            for (int i = 0; i < count; i++)
            {
                var ent = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(TestComponentA), typeof(TestComponentB), typeof(TestEnablebleComponent), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>());
                allEntities.Add(ent);
                UpdateTestComponents(i, ent, testWorld);
            }

            Assert.AreEqual((count == 100 ? 2 : 4), testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(TestComponentA)).CalculateChunkCount());
            WorldStateSave stateSave = default;
            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(5, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), typeof(TestEnablebleComponent), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };

                // Test
                var strategy = new DirectStateSaveStrategy();
                stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;
            Assert.That(stateSave.Initialized);
            ValidateStateSave(count, stateSave, expectedTypeCount: 5);

            // Test we can reuse allocation correctly if the size is the same
            int oldSize = stateSave.AsSpan.Length;
            var oldAdr = (byte*)UnsafeUtility.AddressOf(ref stateSave.AsSpan[0]);

            Assert.IsTrue(oldAdr != null);
            stateSave.Reset();
            Assert.IsTrue(oldAdr != null);
            Assert.IsFalse(stateSave.Initialized);

            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(5, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), typeof(TestEnablebleComponent), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };

                // Test
                var strategy = new DirectStateSaveStrategy();
                stateSave = stateSave.WithRequiredTypes(requiredTypesToSave).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;

                // initializing the same way, but not doing the save job means we should still have a valid state save if we're reusing the same memory allocation
                // state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;
            var newAdr = (byte*)UnsafeUtility.AddressOf(ref stateSave.AsSpan[0]);
            Assert.IsTrue(oldAdr == newAdr && oldSize == stateSave.AsSpan.Length);
            ValidateStateSave(count, stateSave, expectedTypeCount: 5);

            // Test that if we need more space, then we can't reuse the allocation and still create a new allocation
            stateSave.Reset();
            Assert.IsFalse(stateSave.Initialized);

            // increase number of tracked entities
            var newCount = count * 2;
            for (int i = count; i < newCount; i++)
            {
                var ent = testWorld.ServerWorld.EntityManager.CreateEntity(typeof(TestComponentA), typeof(TestComponentB), typeof(TestEnablebleComponent), typeof(TestBufferA), typeof(TestBufferB));
                allEntities.Add(ent);
                UpdateTestComponents(i, ent, testWorld);
            }

            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(5, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), typeof(TestEnablebleComponent), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };

                // Test
                var strategy = new DirectStateSaveStrategy();
                stateSave = stateSave.WithRequiredTypes(requiredTypesToSave).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;

                // since we're not reusing the same adr, we still need to fill the new allocation
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;
            newAdr = (byte*)UnsafeUtility.AddressOf(ref stateSave.AsSpan[0]);
            // doing both checks (adr and length) in the same check, since it's possible for Unity to return the same address when we're changing size. It'll just reuse the same spot in memory but with different size if space is available.
            Assert.IsTrue(oldAdr != newAdr || oldSize != stateSave.AsSpan.Length);
            Assert.That(oldSize, Is.LessThan(stateSave.AsSpan.Length));
            ValidateStateSave(newCount, stateSave, expectedTypeCount: 5);

            // test that we don't grow forever
            // remove enough entities to trigger the "this is 2x too large, let's shrink" logic. so remove half the entities + 1
            for (int i = count - 1; i < newCount; i++)
            {
                testWorld.ServerWorld.EntityManager.DestroyEntity(allEntities[i]);
            }
            allEntities.RemoveRange(count - 1, count + 1);
            newCount = allEntities.Count;
            int doubledSize = stateSave.AsSpan.Length;
            var doubledOldAllocationAdr = (byte*)UnsafeUtility.AddressOf(ref stateSave.AsSpan[0]);

            stateSave.Reset();
            Assert.IsFalse(stateSave.Initialized);
            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(4, Allocator.Temp) { ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), typeof(TestEnablebleComponent), ComponentType.ReadOnly<TestBufferA>(), ComponentType.ReadOnly<TestBufferB>() };

                // Test
                var strategy = new DirectStateSaveStrategy();
                stateSave = stateSave.WithRequiredTypes(requiredTypesToSave).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;

                // since we're not reusing the same adr, we still need to fill the new allocation
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;
            newAdr = (byte*)UnsafeUtility.AddressOf(ref stateSave.AsSpan[0]);
            // doing both checks (adr and length) in the same check, since it's possible for Unity to return the same address when we're shrinking. It'll just reuse the same spot in memory but with different size.
            Assert.IsTrue(doubledOldAllocationAdr != newAdr || doubledSize != stateSave.AsSpan.Length);

            ValidateStateSave(newCount, stateSave, expectedTypeCount: 5);
        }

        [Test, Description("Test all APIs and make sure they throw if not initialized")]
        public unsafe void StateSave_AccessingStateSaveIfDisposed_ErrorsOut()
        {
            WorldStateSave stateSave = default;
            Assert.IsFalse(stateSave.Initialized);
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.AsSpan; });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.EntityCount; });
            Assert.Throws<ObjectDisposedException>(() => {
                SystemState state = default;
                var a = stateSave.ScheduleStateSaveJob(ref state, new DirectStateSaveStrategy());
            });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.TryGetComponentData<TestComponentA>(new SavedEntityID(), out var _); });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.TryGetComponentData(new SavedEntityID(), ComponentType.ReadOnly<TestComponentA>(), out var _); });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.HasComponent(new SavedEntityID(), ComponentType.ReadOnly<TestComponentA>()); });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.GetAllEntities(Allocator.Temp); });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.Exists(new SavedEntityID()); });
            Assert.Throws<ObjectDisposedException>(() => { var a = stateSave.GetComponentTypes(new SavedEntityID()); });
            Assert.Throws<ObjectDisposedException>(() =>
            {
                foreach (var entry in stateSave)
                {
                    throw new Exception("shouldn't be here");
                }
            });
        }

        [Test, Description("Tests various chunk compositions, making sure our structure still is able to distinguish the different archetypes and returns the appropriate data. Tests with ghost that have or not certain component types. Tests \"HasComponentType\" returning false for ghosts which don't have the component")]
        public void StateSave_VariousChunkComposition_Works()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(BehaviourTestCreateStateSaveSystem));

            // Setup
            GameObject AddPrefab(List<ComponentType> typesToAdd)
            {
                // Predicted ghost
                var predictedGhostGO = new GameObject("PredictedGO");
                predictedGhostGO.AddComponent<TestNetCodeAuthoring>().Converter = new StateSaveTestDataConverter() { typesToAdd = typesToAdd};
                var ghostConfig = predictedGhostGO.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghostConfig.SupportedGhostModes = GhostModeMask.Predicted;
                ghostConfig.HasOwner = true;
                return predictedGhostGO;
            }

            List<GameObject> prefabGOs = new();
            prefabGOs.Add(AddPrefab(new (){ ComponentType.ReadOnly<TestComponentA>()}));
            prefabGOs.Add(AddPrefab(new (){ ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>()}));
            prefabGOs.Add(AddPrefab(new (){ ComponentType.ReadOnly<TestComponentB>()}));
            prefabGOs.Add(AddPrefab(new (){ ComponentType.ReadOnly<TestComponentA>(), ComponentType.ReadOnly<TestComponentB>(), ComponentType.ReadOnly<PerfTestComp1>()}));
            Assert.IsTrue(testWorld.CreateGhostCollection(prefabGOs.ToArray()));

            testWorld.CreateWorlds(true, 1);
            testWorld.GetSingletonRW<TestStateSaveSingleton>(testWorld.ServerWorld).ValueRW.StrategyToUse = SaveStrategyToUse.Indexed;
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 16; i++)
            {
                testWorld.Tick();
            }
            var prefabsListQuery = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(NetCodeTestPrefabCollection));
            var prefabList = prefabsListQuery.ToEntityArray(Allocator.Temp)[0];
            var prefabs = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabList);

            var createStateSystem = testWorld.ServerWorld.GetExistingSystemManaged<BehaviourTestCreateStateSaveSystem>();
            var em = testWorld.ServerWorld.EntityManager;

            var justA = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value);
            em.SetComponentData(justA, new TestComponentA(){value = 123});
            var justAB = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[1].Value);
            em.SetComponentData(justAB, new TestComponentA(){value = 123});
            em.SetComponentData(justAB, GetTestComponentB(0));
            var justB = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[2].Value);
            em.SetComponentData(justB, GetTestComponentB(1));
            var justABP1 = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[3].Value);
            em.SetComponentData(justABP1, new TestComponentA(){value = 123});
            em.SetComponentData(justABP1, GetTestComponentB(2));
            em.SetComponentData(justABP1, new PerfTestComp1() { value = new PerfTestStruct() { a = 123, b = 321 } });

            testWorld.Tick();

            Assert.AreEqual(3, testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(TestComponentA)).CalculateChunkCount());
            Assert.AreEqual(3, testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(TestComponentB)).CalculateChunkCount());
            Assert.AreEqual(4, testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(GhostInstance)).CalculateChunkCount());

            // Test
            WorldStateSave stateSave = default;
            createStateSystem.StateSaveTestCallback = (system, singletonEntity) =>
            {
                // Setup
                ref var state = ref system.CheckedStateRef;
                var requiredTypesToSave = new NativeHashSet<ComponentType>(0, Allocator.Temp) { };
                var optionalTypes = new NativeHashSet<ComponentType>(3, Allocator.Temp)
                {
                    ComponentType.ReadOnly<TestComponentA>(),
                    ComponentType.ReadOnly<TestComponentB>(),
                    ComponentType.ReadOnly<PerfTestComp1>(),
                };

                // Test
                var strategy = new IndexedByGhostSaveStrategy(state.GetComponentTypeHandle<GhostInstance>());
                stateSave = new WorldStateSave(Allocator.Persistent).WithRequiredTypes(requiredTypesToSave).WithOptionalTypes(optionalTypes).Initialize(ref state, strategy);
                system.stateToDispose = stateSave;
                state.Dependency = stateSave.ScheduleStateSaveJob(ref state, strategy);
                state.Dependency.Complete();

                // Validate in test
                state.Enabled = false;
            };
            createStateSystem.Enabled = true;
            testWorld.Tick();
            createStateSystem.Enabled = false;

            var ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(justA);
            var savedEntityID = new SavedEntityID(ghost);
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentA>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<GhostInstance>()));
            Assert.AreEqual(2, stateSave.GetComponentTypes(savedEntityID).Length);
            Assert.IsTrue(stateSave.TryGetComponentData<TestComponentA>(savedEntityID, out var compA));
            Assert.AreEqual(123, compA.value);
            Assert.IsFalse(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentB>()));
            Assert.IsFalse(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<PerfTestComp1>()));

            ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(justAB);
            savedEntityID = new SavedEntityID(ghost);
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentA>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentB>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<GhostInstance>()));
            Assert.AreEqual(3, stateSave.GetComponentTypes(savedEntityID).Length);
            Assert.IsTrue(stateSave.TryGetComponentData<TestComponentA>(savedEntityID, out compA));
            Assert.AreEqual(123, compA.value);
            Assert.IsTrue(stateSave.TryGetComponentData<TestComponentB>(savedEntityID, out var compB));
            ValidateCompB(compB, 0);
            Assert.IsFalse(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<PerfTestComp1>()));

            ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(justB);
            savedEntityID = new SavedEntityID(ghost);
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentB>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<GhostInstance>()));
            Assert.AreEqual(2, stateSave.GetComponentTypes(savedEntityID).Length);
            Assert.IsTrue(stateSave.TryGetComponentData<TestComponentB>(savedEntityID, out compB));
            ValidateCompB(compB, 1);
            Assert.IsFalse(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentA>()));
            Assert.IsFalse(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<PerfTestComp1>()));

            ghost = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(justABP1);
            savedEntityID = new SavedEntityID(ghost);
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentA>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<TestComponentB>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<PerfTestComp1>()));
            Assert.IsTrue(stateSave.HasComponent(savedEntityID, ComponentType.ReadOnly<GhostInstance>()));
            Assert.AreEqual(4, stateSave.GetComponentTypes(savedEntityID).Length);
            Assert.IsTrue(stateSave.TryGetComponentData<TestComponentA>(savedEntityID, out compA));
            Assert.AreEqual(123, compA.value);
            Assert.IsTrue(stateSave.TryGetComponentData<TestComponentB>(savedEntityID, out compB));
            ValidateCompB(compB, 2);
            Assert.IsTrue(stateSave.TryGetComponentData<PerfTestComp1>(savedEntityID, out var perf1));
            Assert.AreEqual(123, perf1.value.a);
            Assert.AreEqual(321, perf1.value.b);
        }

        [Test, Description("Write and read performance tests for state save")]
        [Performance]
        [Repeat(10)]
        [Timeout(5 * 60 * 1000)] // 5 minutes
        public void StateSave_PerformanceTest([Values(100, 1_000, 10_000, 50_000, 100_000)] int count, [Values] SaveStrategyToUse strategyToUse)
        {
            // TODO test with various chunk composition and archetypes
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true, typeof(PerfTestCreateStateSave));

            NativeArray<NetCodeTestPrefab> prefabs = default;
            if (strategyToUse == SaveStrategyToUse.Indexed)
            {
                prefabs = SetupWithPrefab(testWorld).ToNativeArray(Allocator.Temp);
            }
            else
            {
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();
            }

            ref var createStateSystem = ref testWorld.ServerWorld.Unmanaged.GetExistingSystemState<PerfTestCreateStateSave>();
            ref var testStateSaveSingleton = ref testWorld.GetSingletonRW<TestStateSaveSingleton>(testWorld.ServerWorld).ValueRW;
            testStateSaveSingleton.StrategyToUse = strategyToUse;
            testStateSaveSingleton.Count = count;

            NativeArray<Entity> allEntities;
            if (strategyToUse == SaveStrategyToUse.Default)
            {
                var archetypeToSpawn = testWorld.ServerWorld.EntityManager.CreateArchetype(typeof(TestComponentA), typeof(TestComponentB), typeof(TestEnablebleComponent), typeof(TestBufferA), typeof(TestBufferB),
                    typeof(PerfTestComp1),
                    typeof(PerfTestComp2),
                    typeof(PerfTestComp3),
                    typeof(PerfTestComp4),
                    typeof(PerfTestComp5),
                    typeof(PerfTestComp6),
                    typeof(PerfTestComp7),
                    typeof(PerfTestComp8),
                    typeof(PerfTestComp9),
                    typeof(PerfTestComp10),
                    typeof(PerfTestComp11),
                    typeof(PerfTestComp12),
                    typeof(PerfTestComp13),
                    typeof(PerfTestComp14),
                    typeof(PerfTestComp15),
                    typeof(PerfTestComp16),
                    typeof(PerfTestComp17),
                    typeof(PerfTestComp18),
                    typeof(PerfTestComp19),
                    typeof(PerfTestComp20)
                );
                allEntities = testWorld.ServerWorld.EntityManager.CreateEntity(archetypeToSpawn, count, Allocator.Temp);
            }
            else
            {
                var ent = prefabs[0].Value;
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp1());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp2());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp3());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp4());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp5());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp6());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp7());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp8());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp9());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp10());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp11());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp12());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp13());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp14());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp15());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp16());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp17());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp18());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp19());
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new PerfTestComp20());
                allEntities = testWorld.ServerWorld.EntityManager.Instantiate(prefabs[0].Value, count, Allocator.Temp);
            }

            for (int i = 0; i < count; i++)
            {
                UpdateTestComponents(i, allEntities[i], testWorld);
            }

            // disable ghost spawning which takes a lot of time in this test. We only care about server side GhostInstances
            var relevancy = testWorld.GetSingleton<GhostRelevancy>(testWorld.ServerWorld);
            relevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

            createStateSystem.Enabled = true;

            var sampleGroups = new[]
            {
                new SampleGroup(PerfTestCreateStateSave.k_MarkerName, SampleUnit.Millisecond),
                new SampleGroup("StateSaveJob", SampleUnit.Millisecond),
                new SampleGroup("WorldStateSave.Initialize", SampleUnit.Millisecond),
                new SampleGroup(k_ReadStateMarkerName, SampleUnit.Millisecond),
            };

            using (Measure.ProfilerMarkers(sampleGroups))
            {
                testWorld.Tick();
                testWorld.Tick();
            }

            createStateSystem.Enabled = false;
            var result = testWorld.GetSingleton<TestStateSaveSingleton>(testWorld.ServerWorld);
            var expectedTypeCount = 5 + (strategyToUse == SaveStrategyToUse.Default ? 0 : 1) + 20;

            if (strategyToUse == SaveStrategyToUse.Indexed)
                ValidateIndexedStateSave(count, allEntities.ToList(), testWorld, result.stateSaveToTest, expectedTypesCount: expectedTypeCount);
        }
    }
}
