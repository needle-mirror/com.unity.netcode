using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    unsafe class NetcodeClientPredictionRateManager : IRateManager
    {
        private EntityQuery m_NetworkTimeQuery;
        private EntityQuery m_ClientServerTickRateQuery;
        private EntityQuery m_ClientTickRateQuery;

        private EntityQuery m_AppliedPredictedTicksQuery;
        private EntityQuery m_UniqueInputTicksQuery;

        private EntityQuery m_GhostQuery;

        private NetworkTick m_LastFullPredictionTick;

        private int m_TickIdx;
        private NetworkTick m_TargetTick;
        private NetworkTime m_CurrentTime;
        private float m_FixedTimeStep;
        private double m_ElapsedTime;

        private NativeArray<NetworkTick> m_AppliedPredictedTickArray;
        private int m_NumAppliedPredictedTicks;

        private uint m_MaxBatchSize;
        private uint m_MaxBatchSizeFirstTimeTick;
        private DoubleRewindableAllocators* m_OldGroupAllocators = null;

        public struct TickComparer : IComparer<NetworkTick>
        {
            public TickComparer(NetworkTick target)
            {
                m_TargetTick = target;
            }
            NetworkTick m_TargetTick;
            public int Compare(NetworkTick x, NetworkTick y)
            {
                var ageX = m_TargetTick.TicksSince(x);
                var ageY = m_TargetTick.TicksSince(y);
                // Sort by decreasing age, which gives increasing ticks with oldest tick first
                return ageY - ageX;
            }
        }

        internal NetcodeClientPredictionRateManager(ComponentSystemGroup group)
        {
            // Create the queries for singletons
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkTime>());
            m_ClientServerTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            m_ClientTickRateQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientTickRate>());

            m_AppliedPredictedTicksQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<GhostPredictionGroupTickState>());
            m_UniqueInputTicksQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<UniqueInputTickMap>());

            var builder = new EntityQueryDesc
            {
                All = new[]{ComponentType.ReadWrite<Simulate>()},
                Any = new []{ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GhostChildEntity>()},
                Options = EntityQueryOptions.IgnoreComponentEnabledState
            };
            m_GhostQuery = group.World.EntityManager.CreateEntityQuery(builder);
        }
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            ref var networkTime = ref m_NetworkTimeQuery.GetSingletonRW<NetworkTime>().ValueRW;
            if (m_TickIdx == 0)
            {
                networkTime.PredictedTickIndex = 0;
                networkTime.NumPredictedTicksExpected = 0;
                m_CurrentTime = networkTime;
                m_ClientTickRateQuery.TryGetSingleton<ClientTickRate>(out var clientTickRate);

                m_AppliedPredictedTicksQuery.CompleteDependency();
                m_UniqueInputTicksQuery.CompleteDependency();

                var appliedPredictedTicks = m_AppliedPredictedTicksQuery.GetSingletonRW<GhostPredictionGroupTickState>().ValueRW.AppliedPredictedTicks;
                var uniqueInputTicks = m_UniqueInputTicksQuery.GetSingletonRW<UniqueInputTickMap>().ValueRW.TickMap;


                // Nothing to predict yet, because the connection is not in game yet and no snapshot has
                // being received so far (still waiting for the first snapshot)
                if (!m_CurrentTime.ServerTick.IsValid)
                    return false;

                // If there is not predicted ghost (so no continuation or rollback to do)
                if(appliedPredictedTicks.IsEmpty)
                {
                    uniqueInputTicks.Clear();
                    appliedPredictedTicks.Clear();
                    //early exit if the prediction mode require ghosts are present, thus the appliedPredictedTicks should be non empty.
                    if (clientTickRate.PredictionLoopUpdateMode == PredictionLoopUpdateMode.RequirePredictedGhost)
                    {
                        m_LastFullPredictionTick = NetworkTick.Invalid;
                        return false;
                    }
                }

                m_TargetTick = m_CurrentTime.ServerTick;
                m_ClientServerTickRateQuery.TryGetSingleton<ClientServerTickRate>(out var clientServerTickRate);
                clientServerTickRate.ResolveDefaults();
                m_FixedTimeStep = clientServerTickRate.SimulationFixedTimeStep;
                m_ElapsedTime = group.World.Time.ElapsedTime;
                if (networkTime.IsPartialTick)
                {
                    m_TargetTick.Decrement();
                    m_ElapsedTime -= m_FixedTimeStep * networkTime.ServerTickFraction;
                }
                // We must simulate the last full tick since the history backup is applied there
                appliedPredictedTicks.TryAdd(m_TargetTick, m_TargetTick);
                // We must simulate at the tick we used as last full tick last time since smoothing and error reporting is happening there
                if (m_LastFullPredictionTick.IsValid && m_TargetTick.IsNewerThan(m_LastFullPredictionTick))
                    appliedPredictedTicks.TryAdd(m_LastFullPredictionTick, m_LastFullPredictionTick);
                else if (!m_LastFullPredictionTick.IsValid)
                    m_LastFullPredictionTick = m_TargetTick;



                m_AppliedPredictedTickArray = appliedPredictedTicks.GetKeyArray(Allocator.Temp);

                NetworkTick oldestTick = NetworkTick.Invalid;
                for (int i = 0; i < m_AppliedPredictedTickArray.Length; ++i)
                {
                    NetworkTick appliedTick = m_AppliedPredictedTickArray[i];
                    if (!oldestTick.IsValid || oldestTick.IsNewerThan(appliedTick))
                        oldestTick = appliedTick;
                }
                //If this condition trigger (that is, removed pretty much where we should start predicting from)
                //it is ok and correct to exit.
                if (!oldestTick.IsValid)
                {
                    uniqueInputTicks.Clear();
                    appliedPredictedTicks.Clear();
                    return false;
                }
                bool hasNew = false;
                for (var i = oldestTick; i != m_TargetTick; i.Increment())
                {
                    var nextTick = i;
                    nextTick.Increment();
                    if (uniqueInputTicks.TryGetValue(nextTick, out var inputTick))
                    {
                        hasNew |= appliedPredictedTicks.TryAdd(i, i);
                    }
                }
                uniqueInputTicks.Clear();
                if (hasNew)
                    m_AppliedPredictedTickArray = appliedPredictedTicks.GetKeyArray(Allocator.Temp);

                appliedPredictedTicks.Clear();
                m_AppliedPredictedTickArray.Sort(new TickComparer(m_CurrentTime.ServerTick));

                m_NumAppliedPredictedTicks = m_AppliedPredictedTickArray.Length;
                // remove everything newer than the target tick
                while (m_NumAppliedPredictedTicks > 0 && m_AppliedPredictedTickArray[m_NumAppliedPredictedTicks-1].IsNewerThan(m_TargetTick))
                    --m_NumAppliedPredictedTicks;
                // remove everything older than "server tick - max inputs"
                int toRemove = 0;
                while (toRemove < m_NumAppliedPredictedTicks && (uint)m_CurrentTime.ServerTick.TicksSince(m_AppliedPredictedTickArray[toRemove]) > CommandDataUtility.k_CommandDataMaxSize)
                    ++toRemove;
                if (toRemove > 0)
                {
                    m_NumAppliedPredictedTicks -= toRemove;
                    for (int i = 0; i < m_NumAppliedPredictedTicks; ++i)
                        m_AppliedPredictedTickArray[i] = m_AppliedPredictedTickArray[i+toRemove];
                }

                networkTime.Flags |= NetworkTimeFlags.IsInPredictionLoop | NetworkTimeFlags.IsFirstPredictionTick;
                networkTime.Flags &= ~(NetworkTimeFlags.IsFinalPredictionTick|NetworkTimeFlags.IsFinalFullPredictionTick|NetworkTimeFlags.IsFirstTimeFullyPredictingTick);
                networkTime.NumPredictedTicksExpected = m_TargetTick.TicksSince(oldestTick) + (m_CurrentTime.IsPartialTick ? 1 : 0);

                group.World.EntityManager.SetComponentEnabled<Simulate>(m_GhostQuery, false);

                if (clientTickRate.MaxPredictionStepBatchSizeRepeatedTick < 1)
                    clientTickRate.MaxPredictionStepBatchSizeRepeatedTick = 1;
                if (clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick < 1)
                    clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick = 1;
                m_MaxBatchSize = (uint)clientTickRate.MaxPredictionStepBatchSizeRepeatedTick;
                m_MaxBatchSizeFirstTimeTick = (uint)clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick;
                if (!m_LastFullPredictionTick.IsValid)
                    m_MaxBatchSize = m_MaxBatchSizeFirstTimeTick;
                m_TickIdx = 1;
            }
            else
            {
                networkTime.Flags &= ~NetworkTimeFlags.IsFirstPredictionTick;
                group.World.PopTime();
                group.World.RestoreGroupAllocator(m_OldGroupAllocators);
            }
            if (m_TickIdx < m_NumAppliedPredictedTicks)
            {
                NetworkTick predictingTick = m_AppliedPredictedTickArray[m_TickIdx];
                NetworkTick prevTick = m_AppliedPredictedTickArray[m_TickIdx-1];
                uint batchSize = (uint)predictingTick.TicksSince(prevTick);
                if (batchSize > m_MaxBatchSize)
                {
                    batchSize = m_MaxBatchSize;
                    predictingTick = prevTick;
                    predictingTick.Add(batchSize);
                    m_AppliedPredictedTickArray[m_TickIdx-1] = predictingTick;
                }
                else
                {
                    ++m_TickIdx;
                }
                uint tickAge = (uint)m_TargetTick.TicksSince(predictingTick);

                // If we just reached the last full tick we predicted last time, switch to use the separate long step setting for new ticks
                if (predictingTick == m_LastFullPredictionTick)
                    m_MaxBatchSize = m_MaxBatchSizeFirstTimeTick;

                if (predictingTick == m_CurrentTime.ServerTick)
                    networkTime.Flags |= NetworkTimeFlags.IsFinalPredictionTick;
                if (predictingTick == m_TargetTick)
                    networkTime.Flags |= NetworkTimeFlags.IsFinalFullPredictionTick;
                if (!m_LastFullPredictionTick.IsValid || predictingTick.IsNewerThan(m_LastFullPredictionTick))
                {
                    networkTime.Flags |= NetworkTimeFlags.IsFirstTimeFullyPredictingTick;
                    m_LastFullPredictionTick = predictingTick;
                }
                networkTime.ServerTick = predictingTick;
                networkTime.SimulationStepBatchSize = (int)batchSize;
                networkTime.ServerTickFraction = 1f;
                group.World.PushTime(new TimeData(m_ElapsedTime - m_FixedTimeStep*tickAge, m_FixedTimeStep*batchSize));
                m_OldGroupAllocators = group.World.CurrentGroupAllocators;
                group.World.SetGroupAllocator(group.RateGroupAllocators);
                networkTime.PredictedTickIndex++;
                return true;
            }

            if (m_TickIdx == m_NumAppliedPredictedTicks && m_CurrentTime.IsPartialTick)
            {
#if UNITY_EDITOR || NETCODE_DEBUG
                if(networkTime.IsFinalPredictionTick)
                    throw new InvalidOperationException("IsFinalPredictionTick should not be set before executing the final prediction tick");
#endif
                networkTime.ServerTick = m_CurrentTime.ServerTick;
                networkTime.EffectiveInputLatencyTicks = m_CurrentTime.EffectiveInputLatencyTicks;
                networkTime.SimulationStepBatchSize = 1;
                networkTime.ServerTickFraction = m_CurrentTime.ServerTickFraction;
                networkTime.Flags |= NetworkTimeFlags.IsFinalPredictionTick;
                networkTime.Flags &= ~(NetworkTimeFlags.IsFinalFullPredictionTick | NetworkTimeFlags.IsFirstTimeFullyPredictingTick);
                group.World.PushTime(new TimeData(group.World.Time.ElapsedTime, m_FixedTimeStep * m_CurrentTime.ServerTickFraction));
                m_OldGroupAllocators = group.World.CurrentGroupAllocators;
                group.World.SetGroupAllocator(group.RateGroupAllocators);
                ++m_TickIdx;
                networkTime.PredictedTickIndex++;
                return true;
            }
#if UNITY_EDITOR || NETCODE_DEBUG
            if (!networkTime.IsFinalPredictionTick)
                throw new InvalidOperationException("IsFinalPredictionTick should not be set before executing the final prediction tick");
            if (networkTime.ServerTick != m_CurrentTime.ServerTick)
                throw new InvalidOperationException("ServerTick should be equals to current server tick at the end of the prediction loop");
            if (math.abs(networkTime.ServerTickFraction-m_CurrentTime.ServerTickFraction) > 1e-6f)
                throw new InvalidOperationException("ServerTickFraction should be equals to current tick fraction at the end of the prediction loop");
#endif
            // Reset all the prediction flags. They are not valid outside the prediction loop
            networkTime.Flags &= ~(NetworkTimeFlags.IsInPredictionLoop |
                                   NetworkTimeFlags.IsFirstPredictionTick |
                                   NetworkTimeFlags.IsFinalPredictionTick |
                                   NetworkTimeFlags.IsFinalFullPredictionTick |
                                   NetworkTimeFlags.IsFirstTimeFullyPredictingTick);
            networkTime.SimulationStepBatchSize = m_CurrentTime.SimulationStepBatchSize;
            m_TickIdx = 0;
            return false;
        }
        public float Timestep
        {
            get
            {
                throw new System.NotImplementedException();
            }
            set
            {
                throw new System.NotImplementedException();
            }
        }
    }

}
