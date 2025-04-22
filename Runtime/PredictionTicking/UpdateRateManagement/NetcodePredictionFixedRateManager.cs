using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    unsafe class NetcodePredictionFixedRateManager
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
        int m_StepRatio;
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

        public int RemainingUpdates => m_RemainingUpdates;

        public NetcodePredictionFixedRateManager(ComponentSystemGroup group)
        {
            SetTimeStep(0f, 0);
            networkTimeQuery = group.EntityManager.CreateEntityQuery(typeof(NetworkTime));
        }

        public void SetTimeStep(float timeStep, int ratio)
        {
            m_TimeStep = timeStep;
            m_StepRatio = ratio;
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
                var networkTime = networkTimeQuery.GetSingleton<NetworkTime>();
                //While not running for partial ticks in case the of stepRatio 1:1 ? Because the ClientSimulationSystemGroup
                //already ensure we are in withing the 5% of the tick rate, so rounding in this case does not make much of a sense.
                //But for stepRatio > 1, the current physic loop run faster, meaning that partial ticks actually cause physics to do
                //potentially 1 or more steps.
                if (!networkTime.IsPartialTick || m_StepRatio > 1)
                {
                    m_RemainingUpdates = (int) (group.World.Time.DeltaTime / m_TimeStep);
                    m_ElapsedTime = group.World.Time.ElapsedTime;
                    //on the client we allow the physics to run for partial ticks. This is a valid situation in case the fixed loop run at higher tick rate than the
                    //simulation. This though add some extra burden client side in term of physics stepping. For example:
                    //if the step ratio is 2 (run at 120hz), client will run 3 physics simulation per tick instead of 2 on average
                    // 1 tick, for partial, dt > physics dt
                    // 2 ticks full tick
                    //
                    // While it is correct that physics run this way (is running at 120 hz), it may be not what you want (to keep the cost lower).
                    // Should an option seems like necessary to configure this behavior and let the user be in control?
                    if (networkTime.IsPartialTick)
                    {
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
