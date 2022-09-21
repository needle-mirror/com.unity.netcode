using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.PerformanceTesting;
using Unity.Profiling;
using UnityEngine;

namespace Unity.NetCode.Tests
{
#if UNITY_EDITOR
    public class NetcodeScenarioUtils
    {
        public struct ScenarioDesc
        {
            public float FrameTime;
            public GameObject[] GhostPrefabs;
            public Type[] Systems;
            public Type[] GhostComponentForVerification;
        }

        public struct ScenarioParams
        {
            public struct GhostSystemParameters
            {
                public int MinSendImportance;
                public int MinDistanceScaledSendImportance;
                public int MaxSendChunks;
                public int MaxSendEntities;
                public bool ForceSingleBaseline;
                public bool ForcePreSerialize;
            }

            public GhostSystemParameters GhostSystemParams;
            public int NumClients;
            public int WarmupFrames;
            public int DurationInFrames;
            public bool UseThinClients;
            public bool SetCommandTarget;

            public int[] SpawnCount;
        }

        public static void ExecuteScenario(ScenarioDesc scenario, ScenarioParams parameters)
        {
            using (var scenarioWorld = new NetCodeTestWorld())
            {
                var hasProxy = false;
                foreach (var system in scenario.Systems)
                {
                    if (system == typeof(GhostSendSystemProxy))
                    {
                        hasProxy = true;
                        break;
                    }
                }
                if (!hasProxy)
                {
                    Type[] systems = new Type[scenario.Systems.Length + 1];
                    scenario.Systems.CopyTo(systems, 0);
                    systems[scenario.Systems.Length] = typeof(GhostSendSystemProxy);
                    scenarioWorld.Bootstrap(true, systems);
                }
                else
                    scenarioWorld.Bootstrap(true, scenario.Systems);

                var frameTime = scenario.FrameTime;

                Assert.IsTrue(scenarioWorld.CreateGhostCollection(scenario.GhostPrefabs));

                // create worlds, spawn and connect
                scenarioWorld.CreateWorlds(true, parameters.NumClients, parameters.UseThinClients);

                var maxSteps = 4;
                Assert.IsTrue(scenarioWorld.Connect(frameTime, maxSteps));

                var ghostSendProxy = scenarioWorld.ServerWorld.GetOrCreateSystemManaged<GhostSendSystemProxy>();
                // ForcePreSerialize must be set before going in-game or it will not be applied
                ghostSendProxy.ConfigureSendSystem(parameters);

                // start simulation
                scenarioWorld.GoInGame();

                // instantiate

                var type = ComponentType.ReadOnly<NetworkIdComponent>();
                var query = scenarioWorld.ServerWorld.EntityManager.CreateEntityQuery(type);
                var connections = query.ToEntityArray(Allocator.TempJob);
                Assert.IsTrue(connections.Length == parameters.NumClients);
                Assert.IsTrue(scenario.GhostPrefabs.Length == parameters.SpawnCount.Length);

                var collectionEnt = scenarioWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(scenarioWorld.ServerWorld);

                for (int i = 0; i < scenario.GhostPrefabs.Length; i++)
                {
                    if (parameters.SpawnCount[i] == 0)
                        continue;

                    var collection = scenarioWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(collectionEnt);
                    var prefabs = scenarioWorld.ServerWorld.EntityManager.Instantiate(collection[i].Value, parameters.SpawnCount[i], Allocator.Temp);

                    if (scenarioWorld.ServerWorld.EntityManager.HasComponent<GhostOwnerComponent>(prefabs[0]))
                    {
                        Assert.IsTrue(prefabs.Length == parameters.NumClients);

                        for (int j = 0; j < connections.Length; ++j)
                        {
                            var networkComponent = scenarioWorld.ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(connections[j]);

                            scenarioWorld.ServerWorld.EntityManager.SetComponentData(prefabs[j], new GhostOwnerComponent { NetworkId = networkComponent.Value });
                            if (parameters.SetCommandTarget)
                                scenarioWorld.ServerWorld.EntityManager.SetComponentData(connections[j], new CommandTargetComponent {targetEntity = prefabs[j]});
                        }
                    }

                    Assert.IsTrue(prefabs != default);
                    Assert.IsTrue(prefabs.Length == parameters.SpawnCount[i]);
                }
                connections.Dispose();

                // warmup
                for (int i = 0; i < parameters.WarmupFrames; ++i)
                    scenarioWorld.Tick(frameTime);

                // run simulation
                ghostSendProxy.SetupStats(scenario.GhostPrefabs.Length, parameters);

                for (int i = 0; i < parameters.DurationInFrames; ++i)
                    scenarioWorld.Tick(frameTime);

                for (int i = 0; i < scenario.GhostComponentForVerification?.Length; i++)
                {
                    var q = scenarioWorld.ServerWorld.EntityManager.CreateEntityQuery(
                        scenario.GhostComponentForVerification[i]);
                    Assert.IsTrue(parameters.SpawnCount[i] == q.CalculateEntityCount());
                }
            }
        }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostSendSystem))]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class GhostSendSystemProxy : ComponentSystemGroup
    {
        private List<SampleGroup> m_GhostSampleGroups;
        private readonly SampleGroup m_SerializationGroup = new SampleGroup("SpeedOfLightGroup", SampleUnit.Nanosecond);

        private int m_ConnectionCount;
        private bool m_IsSetup;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ConnectionCount = 0;
            RequireForUpdate<GhostCollection>();
            RequireForUpdate<NetworkStreamInGame>();
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();

            var ghostSendSystem = World.GetExistingSystem<GhostSendSystem>();
            var simulationSystemGroup = World.GetExistingSystemManaged<SimulationSystemGroup>();
            simulationSystemGroup.RemoveSystemFromUpdateList(ghostSendSystem);
            AddSystemToUpdateList(ghostSendSystem);
        }

        public void ConfigureSendSystem(NetcodeScenarioUtils.ScenarioParams parameters)
        {
            ref var ghostSendSystemData = ref GetSingletonRW<GhostSendSystemData>().ValueRW;
            ghostSendSystemData.ForceSingleBaseline = parameters.GhostSystemParams.ForceSingleBaseline;
            ghostSendSystemData.ForcePreSerialize = parameters.GhostSystemParams.ForcePreSerialize;
        }

        public void SetupStats(int prefabCount, NetcodeScenarioUtils.ScenarioParams parameters)
        {
            this.m_ConnectionCount = parameters.NumClients;

            var capacity = 2 + (3 * prefabCount);
            m_GhostSampleGroups = new List<SampleGroup>(capacity);

            m_GhostSampleGroups.Add(new SampleGroup("Total Replicated Ghosts", SampleUnit.Undefined));
            m_GhostSampleGroups.Add(new SampleGroup("Total Replicated Ghost Length in Bytes", SampleUnit.Byte));

            var id = 0;
            for (int i = 2; i < capacity; i += 3)
            {
                m_GhostSampleGroups.Add(new SampleGroup($"GhostType[{id}] Serialized Entities", SampleUnit.Undefined));
                m_GhostSampleGroups.Add(new SampleGroup($"GhostType[{id}] Total Length in Bytes", SampleUnit.Byte));
                m_GhostSampleGroups.Add(new SampleGroup($"GhostType[{id}] Bits / Entity", SampleUnit.Byte));
                id++;
            }

            m_IsSetup = true;
        }

        protected override void OnUpdate()
        {
            var numLoadedPrefabs= GetSingleton<GhostCollection>().NumLoadedPrefabs;

            var markers = new string[]
            {
                "PrioritizeChunks", "GhostGroup",
                "GhostSendSystem:SerializeJob",
                "GhostSendSystem:SerializeJob (Burst)"
            };

            EntityManager.CompleteAllTrackedJobs();

            var k_MarkerName = "GhostSendSystem:SerializeJob (Burst)";
            using (var recorder = new ProfilerRecorder(new ProfilerCategory("SpeedOfLight.GhostSendSystem"), k_MarkerName, 1,
                ProfilerRecorderOptions.SumAllSamplesInFrame))
            {
                recorder.Reset();
                recorder.Start();
                if (m_IsSetup)
                {
                    using (Measure.ProfilerMarkers(markers))
                    {
                        using (Measure.Scope("GhostSendSystem_OnUpdate"))
                        {
                            base.OnUpdate();
                            EntityManager.CompleteAllTrackedJobs();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                            var netStats = GetSingletonRW<GhostStatsCollectionSnapshot>().ValueRW;
                            for (int worker = 1; worker < netStats.Workers; ++worker)
                            {
                                int statOffset = worker * netStats.Stride;
                                for (int i = 1; i < netStats.Size; ++i)
                                {
                                    netStats.Data[i] += netStats.Data[statOffset + i];
                                    netStats.Data[statOffset + i] = 0;
                                }
                            }

                            uint totalCount = 0;
                            uint totalLength = 0;

                            for (int i = 0; i < numLoadedPrefabs; ++i)
                            {
                                var count = netStats.Data[i * 3 + 4];
                                var length = netStats.Data[i * 3 + 5];
                                uint soloLength = 0;
                                if (count > 0)
                                    soloLength = length / count;

                                Measure.Custom(m_GhostSampleGroups[2+3*i], count / m_ConnectionCount); // Serialized Entities
                                Measure.Custom(m_GhostSampleGroups[2+3*i+1], length / m_ConnectionCount / 8); // Total Length in Bytes
                                Measure.Custom(m_GhostSampleGroups[2+3*i+2], soloLength); // Bits / Entity

                                totalCount += count;
                                totalLength += length;
                            }
                            Measure.Custom(m_GhostSampleGroups[0], totalCount/ m_ConnectionCount);
                            Measure.Custom(m_GhostSampleGroups[1], totalLength / m_ConnectionCount / 8);
#endif
                        }
                    }
                }
                else
                {
                    base.OnUpdate();
                    EntityManager.CompleteAllTrackedJobs();
                }

                if (m_IsSetup)
                    Measure.Custom(m_SerializationGroup, recorder.CurrentValueAsDouble / (1000*1000));
            }
        }
    }
#endif
}
