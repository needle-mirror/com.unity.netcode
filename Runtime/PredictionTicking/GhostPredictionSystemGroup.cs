using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode
{
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst=true)]
    [BurstCompile]
    internal partial struct GhostPredictionDisableSimulateSystem : ISystem
    {
        ComponentTypeHandle<Simulate> m_SimulateHandle;
        ComponentTypeHandle<PredictedGhost> m_PredictedHandle;
        ComponentTypeHandle<GhostChildEntity> m_GhostChildEntityHandle;
        BufferTypeHandle<LinkedEntityGroup> m_LinkedEntityGroupHandle;
        EntityQuery m_PredictedQuery;
        EntityQuery m_NetworkTimeSingleton;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_SimulateHandle = state.GetComponentTypeHandle<Simulate>();
            m_PredictedHandle = state.GetComponentTypeHandle<PredictedGhost>(true);
            m_GhostChildEntityHandle = state.GetComponentTypeHandle<GhostChildEntity>(true);
            m_LinkedEntityGroupHandle = state.GetBufferTypeHandle<LinkedEntityGroup>(true);
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<Simulate>()
                .WithAll<GhostInstance, PredictedGhost>()
#pragma warning disable NETC0001
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
#pragma warning restore NETC0001
            m_PredictedQuery = state.GetEntityQuery(builder);
            m_NetworkTimeSingleton = state.GetEntityQuery(ComponentType.ReadOnly<NetworkTime>());
        }
        [BurstCompile]
        struct TogglePredictedJob : IJobChunk
        {
            public ComponentTypeHandle<Simulate> simulateHandle;
            [ReadOnly] public ComponentTypeHandle<PredictedGhost> predictedHandle;
            [ReadOnly] public ComponentTypeHandle<GhostChildEntity> ghostChildEntityHandle;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupHandle;
            public EntityStorageInfoLookup storageInfoFromEntity;
            public NetworkTick tick;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);

                var predicted = chunk.GetNativeArray(ref predictedHandle);
                var enabledMask = chunk.GetEnabledMask(ref simulateHandle);
                if (chunk.Has(ref linkedEntityGroupHandle))
                {
                    var linkedEntityGroupArray = chunk.GetBufferAccessor(ref linkedEntityGroupHandle);

                    for(int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; i++)
                    {
                        var shouldPredict = predicted[i].ShouldPredict(tick);
                        var isPredicting = enabledMask.GetBit(i);
                        enabledMask[i] = shouldPredict;
                        if (isPredicting != shouldPredict)
                        {
                            var linkedEntityGroup = linkedEntityGroupArray[i];
                            for (int child = 1; child < linkedEntityGroup.Length; ++child)
                            {
                                var storageInfo = storageInfoFromEntity[linkedEntityGroup[child].Value];
                                if (storageInfo.Chunk.Has(ref ghostChildEntityHandle) && storageInfo.Chunk.Has(ref simulateHandle))
                                    storageInfo.Chunk.SetComponentEnabled(ref simulateHandle, storageInfo.IndexInChunk, shouldPredict);
                            }
                        }
                    }
                }
                else
                {
                    for(int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; i++)
                        enabledMask[i] = predicted[i].ShouldPredict(tick);
                }
            }
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var networkTime = m_NetworkTimeSingleton.GetSingleton<NetworkTime>();
            var tick = networkTime.ServerTick;
            m_SimulateHandle.Update(ref state);
            m_PredictedHandle.Update(ref state);
            m_GhostChildEntityHandle.Update(ref state);
            m_LinkedEntityGroupHandle.Update(ref state);
            var predictedJob = new TogglePredictedJob
            {
                simulateHandle = m_SimulateHandle,
                predictedHandle = m_PredictedHandle,
                ghostChildEntityHandle = m_GhostChildEntityHandle,
                linkedEntityGroupHandle = m_LinkedEntityGroupHandle,
                storageInfoFromEntity = state.GetEntityStorageInfoLookup(),
                tick = tick
            };
            state.Dependency = predictedJob.ScheduleParallel(m_PredictedQuery, state.Dependency);
        }
    }

    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast=true)]
    [BurstCompile]
    internal partial struct GhostPredictionEnableSimulateSystem : ISystem
    {
        ComponentTypeHandle<Simulate> m_SimulateHandle;
        private EntityQuery m_GhostQuery;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_SimulateHandle = state.GetComponentTypeHandle<Simulate>();
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithDisabled<Simulate>()
                .WithAny<GhostInstance, GhostChildEntity>();
            m_GhostQuery = state.GetEntityQuery(builder);
        }
        [BurstCompile]
        struct EnableAllPredictedGhostSimulate : IJobChunk
        {
            public ComponentTypeHandle<Simulate> simulateHandle;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var enabledMask = chunk.GetEnabledMask(ref simulateHandle);
                for(int i=0;i<chunk.Count;++i)
                    enabledMask[i] = true;
            }
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var netTime = SystemAPI.GetSingleton<NetworkTime>();
            if (netTime.IsFinalPredictionTick)
            {
                m_SimulateHandle.Update(ref state);
                state.Dependency = new EnableAllPredictedGhostSimulate()
                {
                    simulateHandle = m_SimulateHandle,
                }.ScheduleParallel(m_GhostQuery, state.Dependency);
            }
        }
    }


    /// <summary>
    /// <para>The parent group for all (roughly) deterministic gameplay systems that modify predicted ghosts.
    /// This system group runs for both the client and server worlds at a fixed time step, as specified by
    /// the <see cref="ClientServerTickRate.SimulationTickRate"/> setting.
    /// To understand the differences between this group and the PredictedFixedStepSimulationSystemGroup,
    /// refer to the <see cref="PredictedFixedStepSimulationSystemGroup"/> documentation.</para>
    /// <para>On the server, this group is only updated once per tick, because it runs in tandem with the <see cref="SimulationSystemGroup"/>.
    /// In other words, because the SimulationSystemGroup runs at a fixed time step, and only once per frame, this system inherits those properties.
    /// On the client, the group implements client-side prediction logic by running the client simulation ahead of the server.</para>
    /// <para><b>Important: Because the client is predicting ahead of the server, all systems in this group are updated multiple times
    /// per simulation frame, every time the client receives a new snapshot (see <see cref="ClientServerTickRate.NetworkTickRate"/>
    /// and <see cref="ClientServerTickRate.SimulationTickRate"/>). This is called rollback and re-simulation.</b></para>
    /// <para>These re-simulation prediction group ticks also get more frequent at higher pings.
    /// For example, a client with a 200ms ping is likely to re-simulate roughly twice as many frames than a client with a 100ms connection, with caveats.
    /// The number of predicted, re-simulated frames can easily reach double digits, so systems in this group
    /// must be exceptionally fast, and are likely to use a lot of CPU.
    /// <i>You can use prediction group batching to help mitigate this. Refer to <see cref="ClientTickRate.MaxPredictionStepBatchSizeRepeatedTick"/>.</i></para>
    /// <para>This group contains all predicted simulation (simulation that is the same on both client and server).
    /// On the server, all prediction logic is treated as the authoritative game state, which is only simulated once.</para>
    /// <para>Note: This SystemGroup is intentionally added to non-netcode worlds, to help enable single-player testing.</para>
    /// </summary>
    /// <remarks>Because child systems in this group are updated so frequently (multiple times per frame on the client,
    /// and for all predicted ghosts on the server), this group is usually the most expensive on both builds.
    /// Pay particular attention to the systems that run in this group to keep your performance in check.
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst=true)]
    [UpdateBefore(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    public partial class PredictedSimulationSystemGroup : ComponentSystemGroup
    {}

    /// <summary>
    /// <para>A fixed update group inside the ghost prediction. This is equivalent to <see cref="FixedStepSimulationSystemGroup"/> but for prediction.
    /// The fixed update group can have a higher update frequency than the rest of the prediction, and it does not do partial ticks.</para>
    /// <para>Note: This SystemGroup is intentionally added to non-netcode worlds, to help enable single-player testing.</para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class PredictedFixedStepSimulationSystemGroup : ComponentSystemGroup
    {
        /// <summary>
        /// Return the NetcodePredictionFixedRateManager instance that govern the update logic of the group.
        /// </summary>
        internal NetcodePredictionFixedRateManager InternalRateManager => m_InternalRateManager;
        /// <summary>
        /// Set the timestep used by this group, in seconds. The default value is 1/60 seconds.
        /// </summary>
        public float Timestep
        {
            get
            {
                return m_InternalRateManager.Timestep;
            }
            [Obsolete("The PredictedFixedStepSimulationSystemGroup.TimeStep setter has been deprecated and will be removed (RemovedAfter Entities 1.0)." +
                "Please use the ClientServerTickRate.PredictedFixedStepSimulationTickRatio to set the desired rate for this group. " +
                "Any TimeStep value set using the RateManager directly will be overwritten with the setting provided in the ClientServerTickRate", false)]
            set
            {
                m_InternalRateManager.Timestep = value;
            }
        }
        /// <summary>
        /// Set the current time step as ratio at which the this group run in respect to the simulation/prediction loop. Default value is 1,
        /// that it, the group run at the same fixed rate as the <see cref="PredictedSimulationSystemGroup"/>.
        /// </summary>
        /// <param name="tickRate">The ClientServerTickRate used for the simulation.</param>
        internal void ConfigureTimeStep(in ClientServerTickRate tickRate)
        {
            if(m_InternalRateManager == null)
                return;
            tickRate.Validate();
            var fixedTimeStep = tickRate.PredictedFixedStepSimulationTimeStep;
#if UNITY_EDITOR || NETCODE_DEBUG
            if (m_InternalRateManager.DeprecatedTimeStep != 0f)
            {
                var timestep = m_InternalRateManager.Timestep;
                if (math.distance(timestep, fixedTimeStep) > 1e-4f)
                {
                    UnityEngine.Debug.LogWarning($"The PredictedFixedStepSimulationSystemGroup.TimeStep is {timestep}ms ({math.ceil(1f/timestep)}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {fixedTimeStep}ms ({math.ceil(1f/fixedTimeStep)}FPS).\n" +
                                                 "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                 "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                }
            }
#endif
            m_InternalRateManager.SetTimeStep(tickRate.PredictedFixedStepSimulationTimeStep, tickRate.PredictedFixedStepSimulationTickRatio);
        }

        NetcodePredictionFixedRateManager m_InternalRateManager;
        private ComponentSystemBase m_BeginFixedStepSimulationEntityCommandBufferSystem;
        private ComponentSystemBase m_EndFixedStepSimulationEntityCommandBufferSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            SetRateManagerCreateAllocator(null);
            m_InternalRateManager = new NetcodePredictionFixedRateManager(this);
            m_BeginFixedStepSimulationEntityCommandBufferSystem = World.GetExistingSystemManaged<BeginFixedStepSimulationEntityCommandBufferSystem>();
            m_EndFixedStepSimulationEntityCommandBufferSystem = World.GetExistingSystemManaged<EndFixedStepSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            while (m_InternalRateManager.ShouldGroupUpdate(this))
            {
                m_BeginFixedStepSimulationEntityCommandBufferSystem.Update();
                base.OnUpdate();
                m_EndFixedStepSimulationEntityCommandBufferSystem.Update();
            }
        }
    }
}
