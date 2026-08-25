using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.NetCode.EntitiesInternalAccess;
using Unity.NetCode.LowLevel.StateSave;

namespace Unity.NetCode.Tracing
{

    /// <summary>
    /// This system is responsible for enabling tracing by creating the TracingDataSingleton.
    /// All other tracing systems RequireForUpdate the presence of the TracingDataSingleton.
    /// </summary>
    [BurstCompile]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial struct EnableTracingSystem : ISystem
    {
        private bool m_PrintRawTracesRequested;
        private bool m_PrintProcessedTracesRequested;
        private EntityQuery m_TracingDataQuery;
        private EntityQuery m_NetworkStreamInGameQuery;
        private EntityQuery m_UpdateGhostDataQuery;
        private EntityArchetype m_TracingDataSingletonArchetype;

        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_TracingDataQuery = state.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton));
            m_NetworkStreamInGameQuery = state.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame));
            m_TracingDataSingletonArchetype = state.EntityManager.CreateArchetype(typeof(TracingDataSingleton));
            m_UpdateGhostDataQuery = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance>().WithAll<TracingNameCollected>());
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var tracingSingletonPresent = m_TracingDataQuery.TryGetSingletonRW<TracingDataSingleton>(out var tracingDataSingleton);

            // If we want tracing, and the singleton is not present or created, create it.
            if (TracingDataAccess.Config.Data.IsTracingEnabledAndReady() && (!tracingSingletonPresent || !tracingDataSingleton.ValueRO.IsCreated))
            {
                var newTracingDataSingleton = new TracingDataSingleton(state.WorldUnmanaged, Allocator.Persistent);
                Entity singletonEntity;
                if (!tracingSingletonPresent)
                    singletonEntity = state.EntityManager.CreateEntity(m_TracingDataSingletonArchetype);
                else
                    singletonEntity = m_TracingDataQuery.GetSingletonEntity();
                state.EntityManager.SetComponentData(singletonEntity, newTracingDataSingleton);
            }

            // Pause tracing if we do not have a NetworkStream since it is required for GhostIndexStateSaveStrategy.
            if (state.WorldUnmanaged.IsClient())
                TracingDataAccess.Config.Data.m_ClientHasConnection = !m_NetworkStreamInGameQuery.IsEmpty;
            else
                TracingDataAccess.Config.Data.m_ServerHasConnection = !m_NetworkStreamInGameQuery.IsEmpty;
        }

        public void OnDestroy(ref SystemState state)
        {
            // Set a static reference so that the singleton and it's data can be read outside playmode.
            // TracingDataAccess is responsible for disposing the singleton.
            if (state.Enabled && m_TracingDataQuery.TryGetSingletonRW<TracingDataSingleton>(out var tracingDataSingleton))
            {
                using var ghostsToCollect = m_UpdateGhostDataQuery.ToEntityArray(Allocator.Temp);

                TracingCollectGhostNamesSystem.UpdateGhostNames(state.EntityManager, tracingDataSingleton, ghostsToCollect);
                // Call an early dispose of state saves entity query that flags them as already disposed.
                // That way when we want to later dispose those state saves fully we now not to dispose entity queries because they were already disposed here.
                tracingDataSingleton.ValueRW.DisposeForWorld();
                if (state.WorldUnmanaged.IsClient())
                    TracingDataAccess.SetClientTracingDataSingleton(tracingDataSingleton.ValueRO);
                else
                    TracingDataAccess.SetServerTracingDataSingleton(tracingDataSingleton.ValueRO);
            }
        }
    }

    // This system schedules a trace job on the client at right after the prediction loop.
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [BurstCompile]
    internal partial struct TraceEndPredictionSystem : ISystem
    {
        private EntityQuery m_TracingDataQuery;
        ComponentTypeHandle<GhostInstance> m_GhostInstanceHandle;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TracingDataSingleton>();
            m_TracingDataQuery = state.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton));
            m_GhostInstanceHandle = state.GetComponentTypeHandle<GhostInstance>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            m_GhostInstanceHandle.Update(ref state);
            var singleton = m_TracingDataQuery.GetSingletonRW<TracingDataSingleton>();
            if (!TracingDataAccess.Config.Data.IsTracingEnabledAndReady())
                return;
            singleton.ValueRW.UnprocessedTraces.ScheduleTraceJob(
                ref state,
                new SystemID(TypeManager.GetSystemTypeIndex<TraceEndPredictionSystem>(), tracePosition: TracePosition.netcode),
                state.Dependency,
                m_GhostInstanceHandle
            );
        }
    }

    // This system schedules a trace job on the client at the end of the frame
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(TracingPerFrameFinalizeSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [BurstCompile]
    internal partial struct TraceEndFrameSystem : ISystem
    {
        EntityQuery m_TracingDataQuery;
        ComponentTypeHandle<GhostInstance> m_GhostInstanceHandle;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<TracingDataSingleton>();
            m_TracingDataQuery = state.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton));
            m_GhostInstanceHandle = state.GetComponentTypeHandle<GhostInstance>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var tracingDataSingleton = m_TracingDataQuery.GetSingletonRW<TracingDataSingleton>();
            if (!TracingDataAccess.Config.Data.IsTracingEnabledAndReady())
                return;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!networkTime.ServerTick.IsValid) return;

            var rawState = new RawTracingStateSave(new WorldID(state.WorldUnmanaged))
            {
                system = new SystemID(TypeManager.GetSystemTypeIndex<TraceEndFrameSystem>(), tracePosition: TracePosition.netcode),
                tick = new TickID() {value = networkTime.ServerTick},
                networkTime = networkTime,
                tickDeltaTime = state.WorldUnmanaged.Time.DeltaTime,
            };
            rawState.SetSystemPairOverride(new SystemID(TypeManager.GetSystemTypeIndex<TraceEndPredictionSystem>(), tracePosition: TracePosition.netcode), WorldID.WorldType.Client);
            m_GhostInstanceHandle.Update(ref state);
            tracingDataSingleton.ValueRW.UnprocessedTraces.ScheduleTraceJob(
                state: ref state,
                currentState: ref rawState,
                saveStrategy: new IndexedByGhostSaveStrategy(m_GhostInstanceHandle),
                dependency: state.Dependency
            );
        }
    }


    /// <summary>
    /// This system calls complete on all the tracing job handle and their dependency at the end of the frame.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    internal partial struct TracingPerFrameFinalizeSystem : ISystem
    {
        private EntityQuery m_TracingDataQuery;
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TracingDataSingleton>();
            m_TracingDataQuery = state.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton));
        }

        public void OnUpdate(ref SystemState state)
        {
            var singleton = m_TracingDataQuery.GetSingletonRW<TracingDataSingleton>();
            if (TracingDataAccess.Config.Data.IsTracingEnabledAndReady())
                singleton.ValueRW.UnprocessedTraces.EndOfFrameComplete();
        }
    }


    internal struct TracingNameCollected : IComponentData
    {
        public SavedEntityID CollectedId;
    }

    /// <summary>
    /// This system collects the name of all ghosts (or entity) if dots debug names are enabled to later display in UI.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [BurstCompile]
    internal partial struct TracingCollectGhostNamesSystem : ISystem
    {
        private EntityQuery m_TracingDataQuery;
        private EntityQuery m_NewGhostQuery;
        private EntityQuery m_KnownGhostQuery;
        public void OnCreate(ref SystemState state)
        {
            #if DOTS_DISABLE_DEBUG_NAMES
            state.Enabled = false;
            #endif
            m_TracingDataQuery = state.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton));
            m_NewGhostQuery = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance>().WithNone<TracingNameCollected>());
            m_KnownGhostQuery = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance, TracingNameCollected>());
            state.RequireForUpdate(m_TracingDataQuery);
            state.RequireAnyForUpdate(m_NewGhostQuery, m_KnownGhostQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var tracingDataSingleton = m_TracingDataQuery.GetSingletonRW<TracingDataSingleton>();

            if (!tracingDataSingleton.ValueRO.IsCreated)
                return;

            var ghostNames = tracingDataSingleton.ValueRO.GhostNames;

            // Re-collect ghosts whose identity changed since last collection
            using (var knownGhosts = m_KnownGhostQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (var ghost in knownGhosts)
                {
                    var currentId = new SavedEntityID(state.EntityManager.GetComponentData<GhostInstance>(ghost));
                    var collected = state.EntityManager.GetComponentData<TracingNameCollected>(ghost);
                    if (currentId.Equals(collected.CollectedId))
                        continue;
                    state.EntityManager.GetName(ghost, out var ghostName);
                    ghostNames[currentId] = ghostName;
                    state.EntityManager.SetComponentData(ghost, new TracingNameCollected { CollectedId = currentId });
                }
            }

            // First-time collection for newly spawned ghosts
            using (var newGhosts = m_NewGhostQuery.ToEntityArray(Allocator.Temp))
            {
                if (newGhosts.Length == 0)
                    return;
                foreach (var ghost in newGhosts)
                {
                    var id = new SavedEntityID(state.EntityManager.GetComponentData<GhostInstance>(ghost));
                    state.EntityManager.GetName(ghost, out var ghostName);
                    ghostNames[id] = ghostName;
                }
                state.EntityManager.AddComponent<TracingNameCollected>(newGhosts);
                foreach (var ghost in newGhosts)
                {
                    var id = new SavedEntityID(state.EntityManager.GetComponentData<GhostInstance>(ghost));
                    state.EntityManager.SetComponentData(ghost, new TracingNameCollected { CollectedId = id });
                }
            }
        }

        internal static void UpdateGhostNames(EntityManager em, RefRW<TracingDataSingleton> tracingDataSingleton, NativeArray<Entity> ghostsToCollect)
        {
            foreach (var ghost in ghostsToCollect)
            {
                var ghostInstance = em.GetComponentData<GhostInstance>(ghost);
                var key = new SavedEntityID(ghostInstance);
                em.GetName(ghost, out var ghostName);
                tracingDataSingleton.ValueRW.GhostNames[key] = ghostName;
            }
        }
    }

    /// <summary>
    /// This systems schedule traces jobs before after every system (unless systems are filtered out) using internal entities access.
    /// It also adds the traced component as dependency to the scheduled trace job.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [BurstCompile]
    internal partial class DeepTracingRegistrationSystem : SystemBase
    {
        static readonly SharedStatic<ComponentTypeHandle<GhostInstance>> s_GhostInstanceHandle = SharedStatic<ComponentTypeHandle<GhostInstance>>.GetOrCreate<ComponentTypeHandle<GhostInstance>>();

        protected override void OnCreate()
        {
            RequireForUpdate<TracingDataSingleton>();
            s_GhostInstanceHandle.Data = CheckedStateRef.GetComponentTypeHandle<GhostInstance>(isReadOnly: true);
        }

        protected override void OnUpdate()
        {
            var predictedSimulationSystemGroup = this.World.GetExistingSystemManaged<PredictedSimulationSystemGroup>();
            SetTracingRecursive(predictedSimulationSystemGroup);
            Enabled = false;
        }

        private static void SetTracingRecursive(ComponentSystemGroup parentGroup)
        {
            EntitiesStaticInternalAccessBursted.SetOnUpdateBefore(parentGroup, UpdateBeforeFunctionPointer);
            EntitiesStaticInternalAccessBursted.SetOnUpdateAfter(parentGroup, UpdateAfterFunctionPointer);
            foreach (var sys in parentGroup.ManagedSystems)
            {
                if (TypeManager.IsSystemAGroup(sys.GetType()))
                {
                    var group = (sys as ComponentSystemGroup);
                    SetTracingRecursive(group);
                }
            }
        }

        [BurstCompile]
        private static void UpdateBeforeFunctionPointer(SystemTypeIndex targetSystem, ref SystemState state)
        {
            Trace(targetSystem, ref state, TracePosition.before);
        }

        [BurstCompile]
        private static void UpdateAfterFunctionPointer(SystemTypeIndex targetSystem, ref SystemState state)
        {
            Trace(targetSystem, ref state, TracePosition.after);
        }

        [BurstCompile]
        private static void Trace(SystemTypeIndex targetSystem, ref SystemState state, TracePosition position)
        {
            //todo-next@NetcodeWorld cache this query
            var tracingDataSingleton = new EntityQueryBuilder(Allocator.Temp).WithAllRW<TracingDataSingleton>().Build(ref state).GetSingletonRW<TracingDataSingleton>();
            if (!TracingDataAccess.Config.Data.IsTracingEnabledAndReady())
                return;
            if (TracingDataAccess.Config.Data.OnlyTraceAfter && position == TracePosition.before)
                return;
            // If we have filters for which system to trace, check that the targeted system is in the list.
            if(!TracingDataAccess.Config.Data.SystemTypesToTrace.IsEmpty && !TracingDataAccess.Config.Data.SystemTypesToTrace.Contains(targetSystem))
                return;

            var traceTypesToRead = tracingDataSingleton.ValueRW.UnprocessedTraces.GetComponentTypesDependency(Allocator.Temp);
            JobHandle toCompleteForTrace = default;
            if (traceTypesToRead.Length > 0)
            {
                EntitiesStaticInternalAccessBursted.GetDependency(ref state, ref traceTypesToRead, ref toCompleteForTrace);
            }

            s_GhostInstanceHandle.Data.Update(ref state);
            var jobHandle = tracingDataSingleton.ValueRW.UnprocessedTraces.ScheduleTraceJob(ref state, new SystemID(targetSystem, position), toCompleteForTrace, s_GhostInstanceHandle.Data, false);

            if (traceTypesToRead.Length > 0)
            {
                EntitiesStaticInternalAccessBursted.AddDependency(ref state, ref traceTypesToRead, ref jobHandle);
            }
        }
    }
}
