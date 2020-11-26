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

            public int GetUpdateCount(float deltaTime)
            {
                accumulatedTime += deltaTime;
                int updateCount = (int)(accumulatedTime / fixedTimeStep);
                accumulatedTime = accumulatedTime % fixedTimeStep;
                if (updateCount > maxTimeSteps)
                    updateCount = maxTimeSteps;
                return updateCount;
            }

        }

        private uint m_ServerTick;
        public uint ServerTick => m_ServerTick;
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
            m_fixedTimeLoop.fixedTimeStep = 1.0f / (float) tickRate.SimulationTickRate;
            int updateCount = m_fixedTimeLoop.GetUpdateCount(Time.DeltaTime);
            for (int tickAge = updateCount-1; tickAge >= 0; --tickAge)
            {
                using (m_fixedUpdateMarker.Auto())
                {
                    m_currentTime += m_fixedTimeLoop.fixedTimeStep;
                    World.SetTime(new TimeData(m_currentTime, m_fixedTimeLoop.fixedTimeStep));
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
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
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