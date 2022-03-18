using System;
using System.Collections.Generic;
using Unity.Core;
using Unity.Entities;
using Unity.Profiling;

namespace Unity.NetCode
{
    // Update loop for client and server worlds
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ServerSimulationSystemGroup : SimulationSystemGroup
    {
#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
        internal TickServerSimulationSystem ParentTickSystem;
        protected override void OnDestroy()
        {
            if (ParentTickSystem != null)
                ParentTickSystem.RemoveSystemFromUpdateList(this);
        }
#endif
        private struct FixedTimeLoop
        {
            public float accumulatedTime;
            public float fixedTimeStep;
            public int maxTimeSteps;
            public int maxTimeStepLength;

            public struct Count
            {
                // The total number of step the simulation should take
                public int Total;
                // The number of short steps, if for example Total is 4 and Short is 1 the update will
                // take 3 long steps followed by on short step
                public int Short;
                // The length of the long steps, if this is for example 3 the long steps should use deltaTime*3
                // while the short steps should reduce it by one and use deltaTime*2
                public int Length;
            }

            public Count GetUpdateCount(float deltaTime)
            {
                accumulatedTime += deltaTime;
                int updateCount = (int)(accumulatedTime / fixedTimeStep);
                accumulatedTime = accumulatedTime % fixedTimeStep;
                int shortSteps = 0;
                int length = 1;
                if (updateCount > maxTimeSteps)
                {
                    // Required length
                    length = (updateCount + maxTimeSteps - 1) / maxTimeSteps;
                    if (length > maxTimeStepLength)
                        length = maxTimeStepLength;
                    else
                    {
                        // Check how many will need to be long vs short
                        shortSteps = length * maxTimeSteps - updateCount;
                    }
                    updateCount = maxTimeSteps;
                }
                return new Count
                {
                    Total = updateCount,
                    Short = shortSteps,
                    Length = length
                };
            }

        }

        private uint m_ServerTick;
        public uint ServerTick
        {
            get { return m_ServerTick; }
            internal set { m_ServerTick = value; }
        }
        public bool IsCatchUpTick {get; private set;}

        private FixedTimeLoop m_fixedTimeLoop;
        private ProfilerMarker m_fixedUpdateMarker;
        private double m_currentTime;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ServerTick = 1;
            m_fixedUpdateMarker = new ProfilerMarker("ServerFixedUpdate");
            m_currentTime = Time.ElapsedTime;
        }

        protected override void OnUpdate()
        {
            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();

            var previousTime = Time;

            m_fixedTimeLoop.maxTimeSteps = tickRate.MaxSimulationStepsPerFrame;
            m_fixedTimeLoop.maxTimeStepLength = tickRate.MaxSimulationLongStepTimeMultiplier;
            m_fixedTimeLoop.fixedTimeStep = 1.0f / (float) tickRate.SimulationTickRate;
            var updateCount = m_fixedTimeLoop.GetUpdateCount(Time.DeltaTime);
            for (int tickAge = updateCount.Total-1; tickAge >= 0; --tickAge)
            {
                using (m_fixedUpdateMarker.Auto())
                {
                    if (tickAge == (updateCount.Short - 1))
                        --updateCount.Length;

                    // Check for wrap around
                    uint curTick = m_ServerTick + (uint)(updateCount.Length - 1);
                    if (m_ServerTick < curTick)
                        ++m_ServerTick;
                    m_ServerTick = curTick;

                    var dt = m_fixedTimeLoop.fixedTimeStep * updateCount.Length;
                    m_currentTime += dt;
                    World.SetTime(new TimeData(m_currentTime, dt));
                    IsCatchUpTick = (tickAge != 0);
                    base.OnUpdate();
                    ++m_ServerTick;
                    if (m_ServerTick == 0)
                        ++m_ServerTick;
                }
            }

            World.SetTime(previousTime);
#if UNITY_SERVER
            if (tickRate.TargetFrameRateMode != ClientServerTickRate.FrameRateMode.BusyWait)
#else
            if (tickRate.TargetFrameRateMode == ClientServerTickRate.FrameRateMode.Sleep)
#endif
            {
                AdjustTargetFrameRate(tickRate.SimulationTickRate);
            }
        }

        void AdjustTargetFrameRate(int tickRate)
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
            if (m_fixedTimeLoop.accumulatedTime > 0.75f * m_fixedTimeLoop.fixedTimeStep)
                rate += 2; // higher rate means smaller deltaTime which means remaining accumulatedTime gets smaller
            else if (m_fixedTimeLoop.accumulatedTime < 0.25f * m_fixedTimeLoop.fixedTimeStep)
                rate -= 2; // lower rate means bigger deltaTime which means remaining accumulatedTime gets bigger

            // TODO: need to do solve this for dots runtime. For now just do nothing
            #if !UNITY_DOTSRUNTIME
            UnityEngine.Application.targetFrameRate = rate;
            #endif
        }

    }

#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
#if !UNITY_DOTSRUNTIME
    [DisableAutoCreation]
#endif
    [AlwaysUpdateSystem]
    [UpdateInWorld(TargetWorld.Default)]
    public class TickServerSimulationSystem : ComponentSystemGroup
    {
        protected override void OnDestroy()
        {
            foreach (var sys in Systems)
            {
                var grp = sys as ServerSimulationSystemGroup;
                if (grp != null)
                    grp.ParentTickSystem = null;
            }
        }
    }
#endif
}
