using System.Collections.Generic;
using Unity.Core;
using Unity.Entities;
using Unity.Profiling;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientSimulationSystemGroup : ComponentSystemGroup
    {
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
        private ProfilerMarker m_fixedUpdateMarker;

        protected override void OnCreate()
        {
            m_beginBarrier = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_NetworkReceiveSystemGroup = World.GetOrCreateSystem<NetworkReceiveSystemGroup>();
            m_NetworkTimeSystem = World.GetOrCreateSystem<NetworkTimeSystem>();
            m_fixedUpdateMarker = new ProfilerMarker("ClientFixedUpdate");
        }

        protected List<ComponentSystemBase> m_systemsInGroup = new List<ComponentSystemBase>();

        public override IEnumerable<ComponentSystemBase> Systems => m_systemsInGroup;

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

            var previousTime = Time;
            float networkDeltaTime = Time.DeltaTime;
            // Set delta time for the NetworkTimeSystem
            if (networkDeltaTime > (float) tickRate.MaxSimulationStepsPerFrame / (float) tickRate.SimulationTickRate)
            {
                networkDeltaTime = (float) tickRate.MaxSimulationStepsPerFrame / (float) tickRate.SimulationTickRate;
                World.SetTime(new TimeData(Time.ElapsedTime, networkDeltaTime));
            }

#pragma warning disable 618
            var defaultWorld = World.Active;
            World.Active = World;
#pragma warning restore 618
            m_beginBarrier.Update();
            m_NetworkReceiveSystemGroup.Update();

            // Calculate update time based on values received from the network time system
            var curServerTick = m_NetworkTimeSystem.predictTargetTick;
            var curInterpoationTick = m_NetworkTimeSystem.interpolateTargetTick;
            uint deltaTicks = curServerTick - m_previousServerTick;

            bool fixedTick = HasSingleton<FixedClientTickRate>();
            double currentTime = Time.ElapsedTime;
            if (fixedTick)
            {
                if (curServerTick != 0)
                {
                    m_serverTickFraction = m_interpolationTickFraction = 1;
                    var fraction = m_NetworkTimeSystem.subPredictTargetTick;
                    if (fraction < 1)
                        currentTime -= fraction * fixedTimeStep;
                    networkDeltaTime = fixedTimeStep;
                    if (deltaTicks > (uint) tickRate.MaxSimulationStepsPerFrame)
                        deltaTicks = (uint) tickRate.MaxSimulationStepsPerFrame;
                }
                else
                {
                    deltaTicks = 1;
                }
            }
            else
            {
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

                deltaTicks = curServerTick - m_previousServerTick;
                if (deltaTicks > (uint) tickRate.MaxSimulationStepsPerFrame)
                    deltaTicks = (uint) tickRate.MaxSimulationStepsPerFrame;
                networkDeltaTime = (deltaTicks + m_serverTickFraction - m_previousServerTickFraction) * fixedTimeStep;
                deltaTicks = 1;

            }

            m_previousServerTick = curServerTick;
            m_previousServerTickFraction = m_serverTickFraction;


            for (uint i = 0; i < deltaTicks; ++i)
            {
                if (fixedTick)
                    m_fixedUpdateMarker.Begin();
                var tickAge = deltaTicks - 1 - i;
                m_serverTick = curServerTick - tickAge;
                m_interpolationTick = curInterpoationTick - tickAge;
                World.SetTime(new TimeData(currentTime - fixedTimeStep * tickAge, networkDeltaTime));
                base.OnUpdate();
                if (fixedTick)
                    m_fixedUpdateMarker.End();
            }

#pragma warning disable 618
            World.Active = defaultWorld;
#pragma warning restore 618
            World.SetTime(previousTime);
        }

        public override void SortSystemUpdateList()
        {
            // Extract list of systems to sort (excluding built-in systems that are inserted at fixed points)
            var toSort = new List<ComponentSystemBase>(m_systemsToUpdate.Count);
            BeginSimulationEntityCommandBufferSystem beginEcbSys = null;
            LateSimulationSystemGroup lateSysGroup = null;
            EndSimulationEntityCommandBufferSystem endEcbSys = null;
            GhostSpawnSystemGroup ghostSpawnSys = null;
            foreach (var s in m_systemsToUpdate) {
                if (s is BeginSimulationEntityCommandBufferSystem) {
                    beginEcbSys = (BeginSimulationEntityCommandBufferSystem)s;
                } else if (s is GhostSpawnSystemGroup) {
                    ghostSpawnSys = (GhostSpawnSystemGroup)s;
                    ghostSpawnSys.SortSystemUpdateList(); // not handled by base-class sort call below
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
            if (ghostSpawnSys != null)
                finalSystemList.Add(ghostSpawnSys);
            foreach (var s in m_systemsToUpdate)
                finalSystemList.Add(s);
            if (lateSysGroup != null)
                finalSystemList.Add(lateSysGroup);
            if (endEcbSys != null)
                finalSystemList.Add(endEcbSys);
            m_systemsToUpdate = finalSystemList;

            m_NetworkReceiveSystemGroup.SortSystemUpdateList();
            m_systemsInGroup = new List<ComponentSystemBase>(2 + m_systemsToUpdate.Count);
            m_systemsInGroup.Add(m_beginBarrier);
            m_systemsInGroup.Add(m_NetworkReceiveSystemGroup);
            if (beginEcbSys != null)
                m_systemsInGroup.AddRange(m_systemsToUpdate.GetRange(1, m_systemsToUpdate.Count-1));
            else
                m_systemsInGroup.AddRange(m_systemsToUpdate);
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
        public override void SortSystemUpdateList()
        {
        }
    }
#endif
}