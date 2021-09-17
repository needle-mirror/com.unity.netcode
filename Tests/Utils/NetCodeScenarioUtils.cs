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

                var ghostSendProxy = scenarioWorld.ServerWorld.GetExistingSystem<GhostSendSystemProxy>();
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
    [UpdateInGroup(typeof(ServerSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostSendSystem))]
    [UpdateAfter(typeof(EndSimulationEntityCommandBufferSystem))]
    public partial class GhostSendSystemProxy : SystemBase
    {
        private GhostSendSystem ghostSendSystem;
        private ServerSimulationSystemGroup simGroup;

        private List<SampleGroup> ghostSampleGroups;
        private SampleGroup serializationGroup = new SampleGroup("SpeedOfLightGroup", SampleUnit.Nanosecond);

        private int connectionCount;
        private bool isSetup = false;

        protected override void OnCreate()
        {
            connectionCount = 0;
            if (World.GetExistingSystem<GhostSendSystem>() != null)
            {
                ghostSendSystem = World.GetExistingSystem<GhostSendSystem>();
                simGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
                ghostSendSystem.Enabled = false;
            }
            RequireSingletonForUpdate<GhostCollection>();
            RequireSingletonForUpdate<NetworkStreamInGame>();
        }

        public void ConfigureSendSystem(NetcodeScenarioUtils.ScenarioParams parameters)
        {
            ghostSendSystem.ForceSingleBaseline = parameters.GhostSystemParams.ForceSingleBaseline;
            ghostSendSystem.ForcePreSerialize = parameters.GhostSystemParams.ForcePreSerialize;
        }
        public void SetupStats(int prefabCount, NetcodeScenarioUtils.ScenarioParams parameters)
        {
            this.connectionCount = parameters.NumClients;

            var capacity = 2 + (3 * prefabCount);
            ghostSampleGroups = new List<SampleGroup>(capacity);

            ghostSampleGroups.Add(new SampleGroup("Total Replicated Ghosts", SampleUnit.Undefined));
            ghostSampleGroups.Add(new SampleGroup("Total Replicated Ghost Length in Bytes", SampleUnit.Byte));

            var id = 0;
            for (int i = 2; i < capacity; i += 3)
            {
                ghostSampleGroups.Add(new SampleGroup($"GhostType[{id}] Serialized Entities", SampleUnit.Undefined));
                ghostSampleGroups.Add(new SampleGroup($"GhostType[{id}] Total Length in Bytes", SampleUnit.Byte));
                ghostSampleGroups.Add(new SampleGroup($"GhostType[{id}] Bits / Entity", SampleUnit.Byte));
                id++;
            }

            isSetup = true;
        }

        protected override void OnUpdate()
        {
            var numLoadedPrefabs= GetSingleton<GhostCollection>().NumLoadedPrefabs;
            int netStatSize = 0;
            int netStatStride = 0;
            var intsPerCacheLine = JobsUtility.CacheLineSize/4;
            netStatSize = numLoadedPrefabs * 3 + 3 + 1;
            netStatStride = (netStatSize + intsPerCacheLine-1) & (~(intsPerCacheLine-1));

            var markers = new string[]
            {
                "PrioritizeChunks", "GhostGroup",
                "GhostSendSystem:SerializeJob",
                "GhostSendSystem:SerializeJob (Burst)"
            };

            ghostSendSystem.Enabled = true;

            EntityManager.CompleteAllJobs();

            var k_MarkerName = "GhostSendSystem:SerializeJob (Burst)";
            using (var recorder = new ProfilerRecorder(new ProfilerCategory("SpeedOfLight.GhostSendSystem"), k_MarkerName, 1,
                ProfilerRecorderOptions.SumAllSamplesInFrame))
            {
                recorder.Reset();
                recorder.Start();
                if (isSetup)
                {
                    var stats = ghostSendSystem.m_NetStats;
                    var statsCache =
                        new NativeArray<uint>(netStatStride * JobsUtility.MaxJobThreadCount, Allocator.Temp);
                    for (int worker = 1; worker < JobsUtility.MaxJobThreadCount; ++worker)
                    {
                        int statOffset = worker * netStatStride;
                        // First uint is tick
                        if (stats[0] == 0)
                            statsCache[0] += stats[statOffset];
                        for (int i = 1; i < netStatSize; ++i)
                        {
                            statsCache[i] += stats[statOffset + i];
                            statsCache[statOffset + i] = 0;
                        }
                    }

                    using (Measure.ProfilerMarkers(markers))
                    {
                        using (Measure.Scope("GhostSendSystem_OnUpdate"))
                        {
                            ghostSendSystem.Update();
                            EntityManager.CompleteAllJobs();

#if UNITY_EDITOR || DEVELOPMENT_BUILD

                    uint totalCount = 0;
                    uint totalLength = 0;

                    for (int i = 0; i < numLoadedPrefabs; ++i)
                    {
                        var count = statsCache[i * 3 + 4];
                        var length = statsCache[i * 3 + 5];
                        uint soloLength = 0;
                        if (count > 0)
                            soloLength = length / count;

                        Measure.Custom(ghostSampleGroups[2+3*i], count / connectionCount); // Serialized Entities
                        Measure.Custom(ghostSampleGroups[2+3*i+1], length / connectionCount / 8); // Total Length in Bytes
                        Measure.Custom(ghostSampleGroups[2+3*i+2], soloLength); // Bits / Entity

                        totalCount += count;
                        totalLength += length;
                    }
                    Measure.Custom(ghostSampleGroups[0], totalCount/ connectionCount);
                    Measure.Custom(ghostSampleGroups[1], totalLength / connectionCount / 8);
#endif
                        }
                    }
                }
                else
                {
                    ghostSendSystem.Update();
                    EntityManager.CompleteAllJobs();
                }

                if (isSetup)
                    Measure.Custom(serializationGroup, recorder.CurrentValueAsDouble / (1000*1000));
            }

            ghostSendSystem.Enabled = false;
        }
    }
#endif
}
