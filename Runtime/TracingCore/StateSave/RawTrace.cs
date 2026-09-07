using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Netcode.LowLevel.StateSave;
using Unity.Netcode.NetcodeTime;
using Unity.Profiling;
using UnityEngine;

namespace Unity.Netcode.Tracing
{
    /// Wrapper around WorldStateSave that adds metadata for tracing
    internal struct RawTracingStateSave : IDisposable
    {
        public WorldID worldId;
        public TickID tick;
        public FrameID frame;
        public SystemID system;
        public StateID stateID;
        public float tickDeltaTime;
        public NetworkTime networkTime;
        public float frameDeltaTime;
        public SystemID SystemPairOverride { get; private set; } // special systems (like GhostUpdateSystem) should be diffed to other systems on the authority (like GhostSendSystem), not the previous one
        public WorldID.WorldType SystemPairOverrideWorld { get; private set; }

        internal WorldStateSave stateSave;

        public bool Initialized;

        public RawTracingStateSave(WorldID world) : this()
        {
            worldId = world;
            frame = new FrameID() { value = Time.frameCount };
            frameDeltaTime = Time.unscaledDeltaTime;
            stateID = StateID.DefaultNetcode;
            Initialized = true;
        }

        public void SetSystemPairOverride(SystemID systemPair, WorldID.WorldType worldPairType)
        {
            SystemPairOverride = systemPair;
            SystemPairOverrideWorld = worldPairType;
        }

        /// <see cref="StateSave.DisposeForWorld"/>
        public void DisposeForWorld()
        {
            stateSave.DisposeForWorld();
        }

        public void Dispose()
        {
            stateSave.Dispose();
            Initialized = false;
            this = default;
        }

        public override string ToString()
        {
            if (!Initialized) return $"[Raw Tracing State] Uninitialized";

            var toReturn = $"[Raw Tracing State] worldId:{worldId} tick:{tick},frame:{frame},system:{system},stateID:{stateID},tickDeltaTime:{tickDeltaTime},networkTime:{networkTime},frameDeltaTime:{frameDeltaTime},SystemPairOverride:{SystemPairOverride},SystemPairOverrideWorld:{SystemPairOverrideWorld}";
            foreach (var objectID in stateSave.GetAllEntities(Allocator.Temp))
            {
                toReturn += $"objectID:{objectID}\t";
            }

            return toReturn;
        }
    }

    // Unprocessed traces for a single world
    [BurstCompile]
    internal struct UnprocessedTraces : IDisposable, IEnumerable
    {
        NativeArray<RawTracingStateSave> m_AllTraces; // fixed size traces, looping around when have run for longer than the given window
        NativeList<JobHandle> m_PerSystemJob;
        Allocator m_Allocator;
        int m_CurrentWriteIndex;
        int m_MaxTracesCount;
        int m_Capacity;

        // caching and pre-allocations
        EntityQuery m_CachedTimeQuery;
        NativeArray<Byte> m_PreAllocation; // so we don't allocate all the time

        public UnprocessedTraces(Allocator allocator)
        {
            m_Capacity = 100;
            m_AllTraces = new(m_Capacity, allocator);
            m_PerSystemJob = new(20 * 12, allocator);

            m_CurrentWriteIndex = 0;
            m_MaxTracesCount = 0;
            m_CachedTimeQuery = default;
            m_PreAllocation = new NativeArray<Byte>(100, allocator);
            IsCreated = true;
            m_Allocator = allocator;
        }

        public bool IsCreated { get; set; }

        /// <summary>
        /// Number of raw traces currently stored, i.e. how many elements <see cref="GetEnumerator"/> will return.
        /// </summary>
        public int Count => m_MaxTracesCount;

        public void Clear()
        {
            m_CurrentWriteIndex = 0;
            m_MaxTracesCount = 0;
        }

        /// <see cref="StateSave.DisposeForWorld"/>
        public void DisposeForWorld()
        {
            for (var i = 0; i < m_AllTraces.Length; i++)
            {
                var trace = m_AllTraces[i];
                if (trace.Initialized)
                    trace.DisposeForWorld();
                m_AllTraces[i] = trace;
            }
        }

        public void Dispose()
        {
            foreach (var trace in m_AllTraces)
            {
                if (trace.Initialized)
                    trace.Dispose();
            }
            Clear();
            m_AllTraces.Dispose();
            m_PerSystemJob.Dispose();
            m_PreAllocation.Dispose();
            IsCreated = false;
        }

        // Types for the job's read dependency
        public NativeList<TypeIndex> GetComponentTypesDependency(Allocator allocator)
        {
            var config = TracingDataAccess.Config.Data;
            var requiredTypesToTraceCount = 0;
            if (config.RequiredTypesToTrace.IsCreated)
                requiredTypesToTraceCount = config.RequiredTypesToTrace.Count;
            var optionalTypesToSaveCount = 0;
            if (config.OptionalTypesToTrace.IsCreated)
                optionalTypesToSaveCount = config.OptionalTypesToTrace.Count;

            var toReturn = new NativeList<TypeIndex>(requiredTypesToTraceCount + optionalTypesToSaveCount+ 1, allocator);
            foreach (var type in config.RequiredTypesToTrace)
            {
                if (NeedsJobDependency(type.TypeIndex))
                    toReturn.Add(type.TypeIndex);
            }

            foreach (var type in config.OptionalTypesToTrace)
            {
                if (NeedsJobDependency(type.TypeIndex))
                    toReturn.Add(type.TypeIndex);
            }
            toReturn.Add(ComponentType.ReadOnly<GhostInstance>().TypeIndex); // for the indexed strategy

            return toReturn;
        }

        // Plain tags (zero-sized, non-enableable) have no data to depend on and trip an assert in
        // ComponentDependencyManager if tracked. Presence is still saved; they just skip the dep list.
        static bool NeedsJobDependency(TypeIndex typeIndex) =>
            !(TypeManager.IsZeroSized(typeIndex) && !TypeManager.IsEnableable(typeIndex));

        [BurstCompile]
        public JobHandle ScheduleTraceJob(ref SystemState state, SystemID system, JobHandle dependency, ComponentTypeHandle<GhostInstance> ghostInstanceHandle, bool autoSystemDependency = true)
        {
            if (m_CachedTimeQuery.Equals(default))
            {
                m_CachedTimeQuery = state.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<NetworkTime>());
            }

            var networkTime = m_CachedTimeQuery.GetSingleton<NetworkTime>();
            if (!networkTime.ServerTick.IsValid)
            {
                if (autoSystemDependency)
                    return dependency;
                return default; // skip invalid ticks, we always need a tick to trace
            }

            RawTracingStateSave currentState = new(new WorldID(state.WorldUnmanaged))
            {
                frame = new FrameID() {value = Time.frameCount },
                frameDeltaTime = Time.unscaledDeltaTime,
                stateID = StateID.DefaultNetcode,
                system = system,
                tick = new TickID() {value = networkTime.ServerTick},
                networkTime = networkTime,
                tickDeltaTime = state.WorldUnmanaged.Time.DeltaTime,
            };
            currentState.SetSystemPairOverride(default, default);

            return ScheduleTraceJob(
                state: ref state,
                currentState: ref currentState,
                saveStrategy: new IndexedByGhostSaveStrategy(ghostInstanceHandle),
                dependency: dependency,
                autoSystemDependency: autoSystemDependency
            );
        }

        static readonly ProfilerMarker s_StateSaveMarker = new ProfilerMarker("ScheduledStateSave");
        static readonly ProfilerMarker schedulingMarker = new ProfilerMarker("state save job scheduling");

        [BurstCompile]
        public JobHandle ScheduleTraceJob<T>(ref SystemState state, ref RawTracingStateSave currentState, in T saveStrategy, JobHandle dependency, bool autoSystemDependency = true) where T : IStateSaveStrategy
        {
            var config = TracingDataAccess.Config.Data;
            if (!config.IsTracingEnabledAndReady())
            {
                if (autoSystemDependency)
                    return dependency;
                return default;
            }

            if (!currentState.tick.value.IsValid) throw new InvalidOperationException("Invalid tick"); // skip invalid ticks, we always need a tick to trace

            // Design note on specifying which component type to save
            // as a user, how do I express what I want to trace? simple list of required types? simple list of optional types?
            // the goal is to remove noise and have better perf. So the initial query for "what" to trace should be restrictive --> so AND for all types, not OR. Then users can filter at runtime after that with existing data that was already traced?

            WorldStateSave worldSave;
            using (s_StateSaveMarker.Auto())
            {
                // Increase the capacity if needed, the real limit is the size in memory of the traces inside.
                if (m_Capacity <= m_CurrentWriteIndex)
                {
                    var newCapacity = (int)(m_Capacity * 1.1);
                    var newArray = new NativeArray<RawTracingStateSave>(newCapacity, m_Allocator);
                    NativeArray<RawTracingStateSave>.Copy(m_AllTraces, newArray, m_Capacity);
                    m_AllTraces.Dispose();
                    m_AllTraces = newArray;
                    m_Capacity = newCapacity;
                }
                // If the write index went back to zero we start reusing existing (initialized) allocations
                var traceToOverwrite = m_AllTraces[m_CurrentWriteIndex];
                if (traceToOverwrite.Initialized)
                {
                    // We might have looped, but we don't want to allocate new memory all the time, so looking to reuse old state save
                    worldSave = traceToOverwrite.stateSave;
                    // Todo check that all previous trace completed if too many traces for trace count
                    worldSave.Reset();
                }
                else
                {
                    worldSave = new WorldStateSave(Allocator.Persistent);
                }

                worldSave = worldSave
                    .WithOptionalTypes(config.OptionalTypesToTrace)
                    .WithRequiredTypes(config.RequiredTypesToTrace)
                    .Initialize(ref state, saveStrategy);

                schedulingMarker.Begin();
                var job = worldSave.ScheduleStateSaveJob(ref state, saveStrategy, dependency, autoSystemDependency);
                if (autoSystemDependency)
                    state.Dependency = job;
                m_PerSystemJob.Add(job);
                schedulingMarker.End();

                currentState.stateSave = worldSave;
                m_AllTraces[m_CurrentWriteIndex++] = currentState;
                if(m_CurrentWriteIndex > m_MaxTracesCount)
                    m_MaxTracesCount = m_CurrentWriteIndex;

                // If we exceeded the max allocated size for the combine client and server traces the write index loop back to zero so that we start reusing existing memory allocations.
                bool shouldLoopBack;
                if (state.WorldUnmanaged.IsClient())
                    shouldLoopBack = TracingDataAccess.AddClientTraceSize(currentState.stateSave.Size);
                else
                    shouldLoopBack = TracingDataAccess.AddServerTraceSize(currentState.stateSave.Size);

                if (shouldLoopBack)
                {
                    m_MaxTracesCount = m_CurrentWriteIndex;
                    m_CurrentWriteIndex = 0;
                }

                return job;
            }
        }

        public void EndOfFrameComplete()
        {
            foreach (var handle in m_PerSystemJob)
            {
                handle.Complete();
            }
            m_PerSystemJob.Clear();
        }


        public RawTracingStateSave LastElement()
        {
            if(LastIndexWritten() < 0 || LastIndexWritten() >= m_AllTraces.Length)
                throw new InvalidOperationException("No last element, have not written any traces yet");
            return m_AllTraces[LastIndexWritten()];
        }

        public RawTracingStateSave FirstElement()
        {
            if (m_MaxTracesCount == 0)
                return default;
            return m_AllTraces[FirstIndex()];
        }

        public RawTracingStateSave Get(int index)
        {
            return m_AllTraces[index];
        }

        // ring queue for unprocessedtraces, so have to have special enumerator for iterating all over them.
        public unsafe struct UnprocessedTracesEnumerator : IEnumerator<RawTracingStateSave>
        {
            int currentIndex;
            int firstIndex; // loop's first index in the list
            int returnedCount;
            int capacity;
            int count;
            UnprocessedTraces* traces;

            public UnprocessedTracesEnumerator(int firstIndex, int count, int capacity, UnprocessedTraces* traces)
            {
                // IEnumerator starts with a MoveNext before getting the first element
                currentIndex = this.firstIndex = firstIndex -1;
                this.count = count;
                this.traces = traces;
                returnedCount = 0;
                this.capacity = capacity;
            }

            public bool MoveNext()
            {
                // if next would be above count, don't move.
                if (returnedCount + 1 > count) return false;

                ++currentIndex;
                currentIndex %= capacity;

                ++returnedCount;
                return true;
            }

            public void Reset()
            {
                currentIndex = firstIndex;
                returnedCount = 0;
            }

            public RawTracingStateSave Current => (*traces).Get(currentIndex);

            object IEnumerator.Current => Current;

            public void Dispose()
            {

            }
        }

        public int FirstIndex()
        {
            var first = 0;
            if (m_CurrentWriteIndex != m_MaxTracesCount)
            {
                first = m_CurrentWriteIndex + 1; // we already looped back, so the index after current is the first one.
            }

            return first;
        }

        public int LastIndexWritten()
        {
            return m_CurrentWriteIndex - 1;
        }

        public unsafe IEnumerator<RawTracingStateSave> GetEnumerator()
        {
            return new UnprocessedTracesEnumerator(this.FirstIndex(), this.m_MaxTracesCount, this.m_MaxTracesCount, (UnprocessedTraces*)UnsafeUtility.AddressOf(ref this));
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
