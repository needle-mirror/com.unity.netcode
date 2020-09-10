using System;
using System.Collections.Generic;
using Unity.Core;
using Unity.Entities;
using Unity.Profiling;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientSimulationSystemGroup : SimulationSystemGroup
    {
#if !UNITY_SERVER
        internal TickClientSimulationSystem ParentTickSystem;
        protected override void OnDestroy()
        {
            if (ParentTickSystem != null)
                ParentTickSystem.RemoveSystemFromUpdateList(this);
        }
#endif

        private BeginSimulationEntityCommandBufferSystem m_beginBarrier;
        private NetworkReceiveSystemGroup m_NetworkReceiveSystemGroup;
        private NetworkTimeSystem m_NetworkTimeSystem;
        public float ServerTickDeltaTime { get; private set; }
        public uint ServerTick => m_serverTick;
        public float InterpolationTickFraction => m_interpolationTickFraction;
        public float ServerTickFraction => m_serverTickFraction;
        public uint InterpolationTick => m_interpolationTick;
        private uint m_serverTick;
        private uint m_interpolationTick;
        private float m_serverTickFraction;
        private float m_interpolationTickFraction;
        private uint m_previousServerTick;
        private float m_previousServerTickFraction;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_beginBarrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_NetworkReceiveSystemGroup = World.GetOrCreateSystem<NetworkReceiveSystemGroup>();
            m_NetworkTimeSystem = World.GetOrCreateSystem<NetworkTimeSystem>();
        }

        public override IEnumerable<ComponentSystemBase> Systems
        {
            get
            {
                yield return m_NetworkReceiveSystemGroup;
                foreach (var v in base.Systems)
                {
                    yield return v;
                }
            }
        }

        protected override void OnUpdate()
        {
            var tickRate = default(ClientServerTickRate);
            if (HasSingleton<ClientServerTickRate>())
            {
                tickRate = GetSingleton<ClientServerTickRate>();
            }

            tickRate.ResolveDefaults();

            float fixedTimeStep = 1.0f / (float) tickRate.SimulationTickRate;
            ServerTickDeltaTime = fixedTimeStep;

            float networkDeltaTime = Time.DeltaTime;
            // Set delta time for the NetworkTimeSystem
            World.PushTime(Time);
            if (networkDeltaTime > (float) tickRate.MaxSimulationStepsPerFrame / (float) tickRate.SimulationTickRate)
            {
                networkDeltaTime = (float) tickRate.MaxSimulationStepsPerFrame / (float) tickRate.SimulationTickRate;
                World.SetTime(new TimeData(Time.ElapsedTime, networkDeltaTime));
            }

            m_beginBarrier.Update();
            m_NetworkReceiveSystemGroup.Update();

            // Calculate update time based on values received from the network time system
            var curServerTick = m_NetworkTimeSystem.predictTargetTick;
            var curInterpoationTick = m_NetworkTimeSystem.interpolateTargetTick;

            double currentTime = Time.ElapsedTime;
            m_serverTickFraction = m_NetworkTimeSystem.subPredictTargetTick;
            m_interpolationTickFraction = m_NetworkTimeSystem.subInterpolateTargetTick;

            // If the tick is within +/- 5% of a frame from matching a tick - just use the actual tick instead
            if (m_serverTickFraction < 0.05f)
                m_serverTickFraction = 1;
            else
                ++curServerTick;
            if (m_serverTickFraction > 0.95f)
                m_serverTickFraction = 1;
            if (m_interpolationTickFraction < 0.05f)
                m_interpolationTickFraction = 1;
            else
                ++curInterpoationTick;
            if (m_interpolationTickFraction > 0.95f)
                m_interpolationTickFraction = 1;

            uint deltaTicks = curServerTick - m_previousServerTick;
            if (deltaTicks > (uint) tickRate.MaxSimulationStepsPerFrame)
                deltaTicks = (uint) tickRate.MaxSimulationStepsPerFrame;
            networkDeltaTime = (deltaTicks + m_serverTickFraction - m_previousServerTickFraction) * fixedTimeStep;

            m_previousServerTick = curServerTick;
            m_previousServerTickFraction = m_serverTickFraction;


            m_serverTick = curServerTick;
            m_interpolationTick = curInterpoationTick;
            World.SetTime(new TimeData(currentTime, networkDeltaTime));
            base.OnUpdate();
            World.PopTime();
        }

        //FIXME: this work but is not ideal. Because it is an overload and not an override (virtual), if the method is
        //called using a reference to the base class interface only the SimulationSystem will be sorted and the NetworkReceiveSystemGroup
        //will be not. While technically incorrect, this work in practice because we are not changing / adding new systems
        //to the NetworkReceiveSystemGroup at runtime.
        //Best things to do is to add a new parent group that encapsulate both and tick/sort that instead.
        public void SortSystemsAndNetworkSystemGroup()
        {
            base.SortSystems();
            m_NetworkReceiveSystemGroup.SortSystems();
        }
    }

#if !UNITY_SERVER
#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [UpdateAfter(typeof(TickServerSimulationSystem))]
#endif
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class TickClientSimulationSystem : ComponentSystemGroup
    {
        protected override void OnDestroy()
        {
            foreach (var sys in Systems)
            {
                var grp = sys as ClientSimulationSystemGroup;
                if (grp != null)
                    grp.ParentTickSystem = null;
            }
        }
    }
#endif
}