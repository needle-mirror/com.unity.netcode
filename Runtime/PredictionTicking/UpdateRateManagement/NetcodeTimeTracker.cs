using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using static Unity.NetCode.ClientServerTickRate.FrameRateMode;

namespace Unity.NetCode
{
    /// <summary>
    /// Keeps track of time and tick counts, accumulating time until ready to run a tick.
    /// </summary>
    internal unsafe class NetcodeTimeTracker
    {
        internal struct Count
        {
            // The total number of step the simulation should take. Correspond 1:1 to number of PredictedSimulationSystemGroup iterations (ignoring the effects of batching).
            public int TotalSteps;
            // The number of short steps, if for example Total is 4 and Short is 1 the update will
            // take 3 long steps followed by on short step
            public int ShortStepCount;
            // The length of the long steps, if this is for example 3 the long steps should use deltaTime*3
            // while the short steps should reduce it by one and use deltaTime*2
            public int LengthLongSteps;
        }

        internal int RemainingTicksToRun; // number of prediction loops to run
        private float m_AccumulatedTime;
        private bool m_IsFirstTimeExecuting = true;
        private double m_ElapsedTime;
        private Count m_UpdateCount;
        private ProfilerMarker m_fixedUpdateMarker;
        private readonly PredictedFixedStepSimulationSystemGroup m_PredictedFixedStepSimulationSystemGroup;
        private DoubleRewindableAllocators* m_OldGroupAllocators = null;

        internal NetcodeTimeTracker(ComponentSystemGroup group)
        {
            m_fixedUpdateMarker = new ProfilerMarker("ServerFixedUpdate");
            m_PredictedFixedStepSimulationSystemGroup = group.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>();
            var networkTimeQuery = group.World.EntityManager.CreateEntityQuery(typeof(NetworkTime));

            var netTimeEntity = group.World.EntityManager.CreateEntity(ComponentType.ReadWrite<NetworkTime>());
            group.World.EntityManager.SetName(netTimeEntity, "NetworkTimeSingleton");
            networkTimeQuery.SetSingleton(new NetworkTime
            {
                ServerTick = new NetworkTick(0),
                ServerTickFraction = 1f,
            });
        }

        internal Count RefreshUpdateCount(float deltaTime, float fixedTimeStep, int maxTimeSteps, int maxTimeStepLength)
        {
            return UpdateAccumulatorForDeltaTime(deltaTime, fixedTimeStep, maxTimeSteps, maxTimeStepLength, ref m_AccumulatedTime);
        }

        internal Count GetUpdateCountReadonly(float deltaTime, float fixedTimeStep, int maxTimeSteps, int maxTimeStepLength)
        {
            var accumulatedTime = m_AccumulatedTime;
            return UpdateAccumulatorForDeltaTime(deltaTime, fixedTimeStep, maxTimeSteps, maxTimeStepLength, ref accumulatedTime);
        }

        private static Count UpdateAccumulatorForDeltaTime(float deltaTime, float fixedTimeStep, int maxTimeSteps, int maxTimeStepLength, ref float accumulatedTime)
        {
            accumulatedTime += deltaTime;
            int updateCount = (int)(accumulatedTime / fixedTimeStep);
            // ex:
            // accumulatedTime = 0.16666666
            // fixedTimeStep = 0.0.016666666
            // updateCount = 10, maxTimeSteps = 4, maxTimeStepLength = 4
            int shortSteps = 0;
            int length = 1;
            if (updateCount > maxTimeSteps) // 10 > 4
            {
                // Required length
                // +maxTimeSteps-1 to get the implicit int cast to "round up"
                length = (updateCount + maxTimeSteps - 1) / maxTimeSteps; // (10 + 4 - 1) / 4 = 13/4 = (int)3.25 = 3
                if (length > maxTimeStepLength) // 3 ! > 4
                {
                    length = maxTimeStepLength;
                }
                else
                {
                    // Check how many will need to be long vs short
                    shortSteps = length * maxTimeSteps - updateCount; // 3 * 4 - 10 = 2
                }
                updateCount = maxTimeSteps; // 4
            }

            var longStepCount = updateCount - shortSteps; // 4 - 2 = 2
            var timeConsumedThisFrame = length * fixedTimeStep * longStepCount + (length - 1) * fixedTimeStep * shortSteps; // 3 * 0.016666666 * 2 + (3 - 1) * 0.016666666 * 2 = 0.1666666666 == accumulatedTime
            accumulatedTime -= timeConsumedThisFrame;
            return new Count
            {
                TotalSteps = updateCount,
                ShortStepCount = shortSteps,
                LengthLongSteps = length
            };
        }

        internal bool ShouldSleep(ClientServerTickRate tickRate)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return tickRate.TargetFrameRateMode != BusyWait;
#else
            return tickRate.TargetFrameRateMode == Sleep;
#endif
        }
        /// <summary>
        /// called on each prediction iteration, to update network time state accordingly
        /// returns true if has simulation to run
        /// </summary>
        internal bool InitializeNetworkTimeForFrame(ComponentSystemGroup group, ClientServerTickRate tickRate, Count updateCount)
        {
            // initialize all runs of prediction system group this frame
            // for whole frame server side. for prediction group only host side. TODO-2.0 all this should only be prediction group even for DGS? more consistent DGS vs host?
            m_UpdateCount = updateCount;
            RemainingTicksToRun = m_UpdateCount.TotalSteps;
            m_PredictedFixedStepSimulationSystemGroup.ConfigureTimeStep(tickRate); // TODO-MovePred

            if (ShouldSleep(tickRate))
            {
                AdjustTargetFrameRate(tickRate.SimulationTickRate, tickRate.SimulationFixedTimeStep);
            }

            return RemainingTicksToRun > 0;
        }

        internal void PopTime(ComponentSystemGroup group)
        {
            group.World.PopTime();
            group.World.RestoreGroupAllocator(m_OldGroupAllocators);
            m_fixedUpdateMarker.End();
        }

        internal void PushTime(ComponentSystemGroup group, float dt, NetworkTime networkTime)
        {
            group.World.PushTime(new TimeData(networkTime.ElapsedNetworkTime, dt));
            m_OldGroupAllocators = group.World.CurrentGroupAllocators;
            group.World.SetGroupAllocator(group.RateGroupAllocators);
            m_fixedUpdateMarker.Begin();
        }

        internal void UpdateNetworkTime(ComponentSystemGroup group, ClientServerTickRate tickRate, ref NetworkTime networkTime)
        {
            if (m_IsFirstTimeExecuting)
            {
                m_IsFirstTimeExecuting = false;
                // we want to keep the same behaviour as UpdateWorldTimeSystem which starts at 0 for the first frame
                // here this will be negative and then clamped to 0 later
                m_ElapsedTime = group.World.Time.ElapsedTime - group.World.Time.DeltaTime;
            }
            if (RemainingTicksToRun == (m_UpdateCount.ShortStepCount))
                --m_UpdateCount.LengthLongSteps;
            var dt = GetDeltaTimeForCurrentTick(tickRate);
            // Check for wrap around
            var currentServerTick = networkTime.ServerTick;
            currentServerTick.Increment();
            var nextTick = currentServerTick;
            nextTick.Add((uint)(m_UpdateCount.LengthLongSteps - 1));
            networkTime.ServerTick = nextTick;
            networkTime.EffectiveInputLatencyTicks = 0;
            networkTime.InterpolationTick = networkTime.ServerTick;
            networkTime.SimulationStepBatchSize = m_UpdateCount.LengthLongSteps;
            if (RemainingTicksToRun == 1)
                networkTime.Flags &= ~NetworkTimeFlags.IsCatchUpTick;
            else
                networkTime.Flags |= NetworkTimeFlags.IsCatchUpTick;
            m_ElapsedTime += dt;
            // At the beginning of the world, we'll be a few prediction ticks with a negative elapsedTime value if the first frame has a high deltaTime. This is needed so that during that first frame, if we execute multiple batched ticks that we're still following the world's elapsed time.
            networkTime.ElapsedNetworkTime = math.max(m_ElapsedTime, 0);
        }

        private void AdjustTargetFrameRate(int tickRate, float fixedTimeStep)
        {
            //
            // If running as headless we nudge the Application.targetFramerate back and forth
            // around the actual framerate -- always trying to have a remaining time of half a frame
            // The goal is to have the while loop above tick exactly 1 time
            //
            // The reason for using targetFramerate is to allow Unity to sleep between frames
            // reducing cpu usage on server.
            //
            int rate = tickRate;
            const float aboveHalfRange = 0.75f;
            const float belowHalfRange = 0.25f;
            if (m_AccumulatedTime > aboveHalfRange * fixedTimeStep)
                rate += 2; // higher rate means smaller deltaTime which means remaining accumulatedTime gets smaller
            else if (m_AccumulatedTime < belowHalfRange * fixedTimeStep)
                rate -= 2; // lower rate means bigger deltaTime which means remaining accumulatedTime gets bigger

            UnityEngine.Application.targetFrameRate = rate;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float GetDeltaTimeForCurrentTick(in ClientServerTickRate tickRate)
        {
            var dt = tickRate.SimulationFixedTimeStep * m_UpdateCount.LengthLongSteps;
            return dt;
        }
    }
}
