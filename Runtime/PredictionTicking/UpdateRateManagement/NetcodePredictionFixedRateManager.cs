using Unity.Collections;
using Unity.Core;
using Unity.Entities;

namespace Unity.NetCode
{

    unsafe class NetcodePredictionFixedRateManager : IRateManager
    {
        public float Timestep
        {
            get => m_TimeStep;
            set
            {
                m_TimeStep = value;
#if UNITY_EDITOR || NETCODE_DEBUG
                m_DeprecatedTimeStep = value;
#endif
            }
        }

        int m_RemainingUpdates;
        float m_TimeStep;
        double m_ElapsedTime;
        private EntityQuery networkTimeQuery;
        //used to track invalid usage of the TimeStep setter.
#if UNITY_EDITOR || NETCODE_DEBUG
        float m_DeprecatedTimeStep;
        public float DeprecatedTimeStep
        {
            get=> m_DeprecatedTimeStep;
            set => m_DeprecatedTimeStep = value;
        }

#endif
        DoubleRewindableAllocators* m_OldGroupAllocators = null;

        public NetcodePredictionFixedRateManager(float defaultTimeStep)
        {
            SetTimeStep(defaultTimeStep);
        }

        public void OnCreate(ComponentSystemGroup group)
        {
            networkTimeQuery = group.EntityManager.CreateEntityQuery(typeof(NetworkTime));
        }

        public void SetTimeStep(float timeStep)
        {
            m_TimeStep = timeStep;
#if UNITY_EDITOR || NETCODE_DEBUG
            m_DeprecatedTimeStep = 0f;
#endif
        }

        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            // if this is true, means we're being called a second or later time in a loop
            if (m_RemainingUpdates > 0)
            {
                group.World.PopTime();
                group.World.RestoreGroupAllocator(m_OldGroupAllocators);
                --m_RemainingUpdates;
            }
            else if(m_TimeStep > 0f)
            {
                // Add epsilon to account for floating point inaccuracy
                m_RemainingUpdates = (int)((group.World.Time.DeltaTime + 0.001f) / m_TimeStep);
                if (m_RemainingUpdates > 0)
                {
                    var networkTime = networkTimeQuery.GetSingleton<NetworkTime>();
                    m_ElapsedTime = group.World.Time.ElapsedTime;
                    if (networkTime.IsPartialTick)
                    {
                        //dt = m_FixedTimeStep * networkTime.ServerTickFraction;
                        //elapsed since last full tick = m_ElapsedTime - dt;
                        m_ElapsedTime -= group.World.Time.DeltaTime;
                        m_ElapsedTime += m_RemainingUpdates * m_TimeStep;
                    }
                }
            }
            if (m_RemainingUpdates == 0)
                return false;
            group.World.PushTime(new TimeData(
                elapsedTime: m_ElapsedTime - (m_RemainingUpdates-1)*m_TimeStep,
                deltaTime: m_TimeStep));
            m_OldGroupAllocators = group.World.CurrentGroupAllocators;
            group.World.SetGroupAllocator(group.RateGroupAllocators);
            return true;
        }
    }
}
