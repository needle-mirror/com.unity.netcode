using System.Collections.Generic;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.PerformanceTesting;
using Unity.Profiling;
using Unity.Transforms;

namespace Unity.NetCode.Tests.Performance
{
    internal class PerformanceTests
    {
        private const int numPrefabs = 1000;

        public ComponentType[] RootTypes=
        {
            ComponentType.ReadWrite<LocalTransform>(),
            ComponentType.ReadWrite<EnableableComponent_0>(),
            ComponentType.ReadWrite<EnableableComponent_1>(),
            ComponentType.ReadWrite<EnableableComponent_2>(),
            ComponentType.ReadWrite<EnableableComponent_3>(),
            ComponentType.ReadWrite<EnableableComponent_4>(),
            ComponentType.ReadWrite<EnableableComponent_5>(),
            ComponentType.ReadWrite<EnableableComponent_6>(),
            ComponentType.ReadWrite<EnableableComponent_7>(),
            ComponentType.ReadWrite<EnableableComponent_8>(),
            ComponentType.ReadWrite<EnableableComponent_9>(),
            ComponentType.ReadWrite<EnableableComponent_10>(),
            ComponentType.ReadWrite<EnableableComponent_11>(),
            ComponentType.ReadWrite<EnableableComponent_12>(),
            ComponentType.ReadWrite<EnableableComponent_13>(),
        };
        public ComponentType[] ChildTypes=
        {
            ComponentType.ReadWrite<LocalTransform>(),
            ComponentType.ReadWrite<EnableableComponent_0>(),
            ComponentType.ReadWrite<EnableableComponent_1>(),
            ComponentType.ReadWrite<EnableableComponent_2>(),
            ComponentType.ReadWrite<EnableableComponent_3>(),
            ComponentType.ReadWrite<EnableableComponent_4>(),
            ComponentType.ReadWrite<EnableableComponent_5>(),
            ComponentType.ReadWrite<EnableableComponent_6>(),
            ComponentType.ReadWrite<EnableableComponent_7>(),
            ComponentType.ReadWrite<EnableableComponent_8>(),
            ComponentType.ReadWrite<EnableableComponent_9>(),
            ComponentType.ReadWrite<EnableableComponent_10>()
        };

        private int oldJobWorker;
        private bool oldJobDebuggerEnabled;

        [SetUp]
        public void Startup()
        {
            oldJobWorker = JobsUtility.JobWorkerCount;
            oldJobDebuggerEnabled = JobsUtility.JobDebuggerEnabled;
            JobsUtility.JobWorkerCount = 0;
            JobsUtility.JobDebuggerEnabled = false;
        }
        [TearDown]
        public void ResetJobUtilty()
        {
            JobsUtility.JobWorkerCount = oldJobWorker;
            JobsUtility.JobDebuggerEnabled = oldJobDebuggerEnabled;
        }

        [Test, Performance]
        public void UseSingleBaseline([Values]bool useSingleBaseline)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            //Check perf on main thread only for now.

            var serverEntity = CreatePrefab($"Prefab", testWorld.ServerWorld.EntityManager, 5, RootTypes, ChildTypes,
                useSingleBaseline);
            CreatePrefab($"Prefab", testWorld.ClientWorlds[0].EntityManager, 5, RootTypes, ChildTypes,
                useSingleBaseline);

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; ++i)
            {
                testWorld.Tick();
            }
            for(int i=0;i<30;++i)
                testWorld.ServerWorld.EntityManager.Instantiate(serverEntity);
            var serverRecorders = PerfTestRecorder.CreateRecorders(testWorld.ServerWorld,
                testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>());
            var clientRecorders = PerfTestRecorder.CreateRecorders(testWorld.ClientWorlds[0],
                testWorld.ClientWorlds[0].GetExistingSystem<GhostReceiveSystem>());
            //Get all markers for the ghost collections
            PerfTestRecorder.StartRecording(serverRecorders);
            PerfTestRecorder.StartRecording(clientRecorders);

            for (int i = 0; i < 256; ++i)
            {
                testWorld.Tick();
            }
            PerfTestRecorder.StopRecording(serverRecorders);
            PerfTestRecorder.StopRecording(clientRecorders);
            //2 sample per frame per marker because the same marker is present on client and server
            PerfTestRecorder.Report(serverRecorders, "server");
            PerfTestRecorder.Report(clientRecorders, "client");
        }

        [Test, Performance]
        public void ImportLargeNumberOfPrefabs()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1);
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 32; ++i)
            {
                testWorld.Tick();
            }
            //Now import all the prefabs.
            for (int i = 0; i < numPrefabs; ++i)
                CreatePrefab($"Prefab-{i}", testWorld.ServerWorld.EntityManager, 5, RootTypes, ChildTypes);
            for (int i = 0; i < numPrefabs; ++i)
                CreatePrefab($"Prefab-{i}", testWorld.ClientWorlds[0].EntityManager, 5, RootTypes, ChildTypes);

            var serverRecorders = PerfTestRecorder.CreateRecorders(testWorld.ServerWorld,
                testWorld.ServerWorld.GetExistingSystem<GhostCollectionSystem>(),
                $"{testWorld.ServerWorld.Name}-GhostCollectionSystem_Stripping",
                $"{testWorld.ServerWorld.Name}-GhostCollectionSystem_Tracking",
                $"{testWorld.ServerWorld.Name}-GhostCollectionSystem_Mapping",
                $"{testWorld.ServerWorld.Name}-GhostCollectionSystem_Processing",
                $"{testWorld.ServerWorld.Name}-GhostCollectionSystem_UpdateNames");
            var clientRecorders = PerfTestRecorder.CreateRecorders(testWorld.ClientWorlds[0],
                testWorld.ClientWorlds[0].GetExistingSystem<GhostCollectionSystem>(),
                $"{testWorld.ClientWorlds[0].Name}-GhostCollectionSystem_Stripping",
                $"{testWorld.ClientWorlds[0].Name}-GhostCollectionSystem_Tracking",
                $"{testWorld.ClientWorlds[0].Name}-GhostCollectionSystem_Mapping",
                $"{testWorld.ClientWorlds[0].Name}-GhostCollectionSystem_Processing",
                $"{testWorld.ClientWorlds[0].Name}-GhostCollectionSystem_UpdateNames");
            //Get all markers for the ghost collections
            PerfTestRecorder.StartRecording(serverRecorders);
            PerfTestRecorder.StartRecording(clientRecorders);
            for (int i = 0; i < 256; ++i)
            {
                testWorld.Tick();
            }
            PerfTestRecorder.StopRecording(serverRecorders);
            PerfTestRecorder.StopRecording(clientRecorders);
            //2 sample per frame per marker because the same marker is present on client and server
            PerfTestRecorder.Report(serverRecorders, "server");
            PerfTestRecorder.Report(clientRecorders, "client");
        }

        private Entity CreatePrefab(string name, EntityManager entityManager, int numChild,
            ComponentType[] rootTypes,  ComponentType[] childTypes, bool useSingleBaseline = false)
        {
            var compSet = new ComponentTypeSet(rootTypes);
            var archetype = entityManager.CreateArchetype(rootTypes);
            var entity = entityManager.CreateEntity(archetype);
            var overrides = new NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride>(128,
                    Allocator.Temp);
            //need to add a couple of overrides here to test that path and also exercise the variant mapping (that is usually the slow path)
            overrides[new GhostPrefabCreation.Component
            {
                ComponentType = ComponentType.ReadWrite<LocalTransform>(),
                ChildIndex = 0
            }] = new GhostPrefabCreation.ComponentOverride
            {
                OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                Variant = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(PositionOnlyVariant), ComponentType.ReadOnly<LocalTransform>())
            };
            overrides[new GhostPrefabCreation.Component
            {
                ComponentType = ComponentType.ReadWrite<LocalTransform>(),
                ChildIndex = 1
            }] = new GhostPrefabCreation.ComponentOverride
            {
                OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                Variant = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(RotationOnlyVariant), ComponentType.ReadOnly<LocalTransform>()),
            };
            overrides[new GhostPrefabCreation.Component
            {
                ComponentType = ComponentType.ReadWrite<LocalTransform>(),
                ChildIndex = 2
            }] = new GhostPrefabCreation.ComponentOverride
            {
                OverrideType = GhostPrefabCreation.ComponentOverrideType.Variant,
                Variant = GhostVariantsUtility.UncheckedVariantHashNBC(typeof(RotationScaleVariant), ComponentType.ReadOnly<LocalTransform>()),
            };
            var leg = entityManager.AddBuffer<LinkedEntityGroup>(entity);
            leg.Add(entity);
            var childCompSet = new ComponentTypeSet(childTypes);
            for (int i = 0; i < numChild; ++i)
            {
                var childEntity = entityManager.CreateEntity();
                entityManager.AddComponent(childEntity, childCompSet);
                entityManager.GetBuffer<LinkedEntityGroup>(entity).Add(childEntity);
            }
            GhostPrefabCreation.ConvertToGhostPrefab(entityManager, entity, new GhostPrefabCreation.Config
            {
                Name = name,
                Importance = 0,
                SupportedGhostModes = GhostModeMask.All,
                DefaultGhostMode = GhostMode.Predicted,
                OptimizationMode = GhostOptimizationMode.Dynamic,
                UsePreSerialization = false,
                UseSingleBaseline = useSingleBaseline,
                CollectComponentFunc = default
            },overrides);
            return entity;
        }

    }
}
