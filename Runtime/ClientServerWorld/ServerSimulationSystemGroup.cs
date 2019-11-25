using System.Collections.Generic;
using Unity.Core;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;

namespace Unity.NetCode
{
    // Update loop for client and server worlds
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ServerSimulationSystemGroup : ComponentSystemGroup
    {
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

        protected override void OnCreate()
        {
            AddSystemToUpdateList(World.GetOrCreateSystem<NetworkReceiveSystemGroup>());
            m_ServerTick = 1;
            m_fixedUpdateMarker = new ProfilerMarker("ServerFixedUpdate");
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
#pragma warning disable 618
            var defaultWorld = World.Active;
            World.Active = World;
            int updateCount = m_fixedTimeLoop.GetUpdateCount(Time.DeltaTime);
            for (int tickAge = updateCount-1; tickAge >= 0; --tickAge)
            {
                using (m_fixedUpdateMarker.Auto())
                {
                    World.SetTime(new TimeData(previousTime.ElapsedTime - m_fixedTimeLoop.accumulatedTime - m_fixedTimeLoop.fixedTimeStep * tickAge, m_fixedTimeLoop.fixedTimeStep));
                    base.OnUpdate();
                    ++m_ServerTick;
                    if (m_ServerTick == 0)
                        ++m_ServerTick;
                }
            }

            World.Active = defaultWorld;
#pragma warning restore 618
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

            Application.targetFrameRate = rate;
        }

        public override void SortSystemUpdateList()
        {
            // Extract list of systems to sort (excluding built-in systems that are inserted at fixed points)
            var toSort = new List<ComponentSystemBase>(m_systemsToUpdate.Count);
            BeginSimulationEntityCommandBufferSystem beginEcbSys = null;
            LateSimulationSystemGroup lateSysGroup = null;
            EndSimulationEntityCommandBufferSystem endEcbSys = null;
            NetworkReceiveSystemGroup netRecvSys = null;
            foreach (var s in m_systemsToUpdate) {
                if (s is BeginSimulationEntityCommandBufferSystem) {
                    beginEcbSys = (BeginSimulationEntityCommandBufferSystem)s;
                } else if (s is NetworkReceiveSystemGroup) {
                    netRecvSys = (NetworkReceiveSystemGroup)s;
                    netRecvSys.SortSystemUpdateList(); // not handled by base-class sort call below
                } else if (s is LateSimulationSystemGroup) {
                    lateSysGroup = (LateSimulationSystemGroup)s;
                    lateSysGroup.SortSystemUpdateList(); // not handled by base-class sort call below
                } else if (s is EndSimulationEntityCommandBufferSystem) {
                    endEcbSys = (EndSimulationEntityCommandBufferSystem)s;
                } else {
                    toSort.Add(s);
                }
            }
            m_systemsToUpdate = toSort;
            base.SortSystemUpdateList();
            // Re-insert built-in systems to construct the final list
            var finalSystemList = new List<ComponentSystemBase>(toSort.Count);
            if (beginEcbSys != null)
                finalSystemList.Add(beginEcbSys);
            if (netRecvSys != null)
                finalSystemList.Add(netRecvSys);
            foreach (var s in m_systemsToUpdate)
                finalSystemList.Add(s);
            if (lateSysGroup != null)
                finalSystemList.Add(lateSysGroup);
            if (endEcbSys != null)
                finalSystemList.Add(endEcbSys);
            m_systemsToUpdate = finalSystemList;
        }
    }

#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class TickServerSimulationSystem : ComponentSystemGroup
    {
        public override void SortSystemUpdateList()
        {
        }
    }
#endif
}