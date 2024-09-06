using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Useful to abstract system update logic from business logic. Uses clear Enter/SubsequentEnter/Exit events instead of the single GroupUpdate
    /// </summary>
    internal abstract class TickRateManagerStrategy
    {
        public abstract bool Update(ComponentSystemGroup group);

        public delegate void TickRateManagerDelegate(ComponentSystemGroup group);
        public delegate bool ShouldRunDelegate(ComponentSystemGroup group);

        public TickRateManagerDelegate OnEnterSystemGroup;
        public TickRateManagerDelegate OnExitSystemGroup;
        public TickRateManagerDelegate OnSubsequentRuns;
    }

    internal class RunOnce : TickRateManagerStrategy
    {
        bool m_IsRunning;

        public ShouldRunDelegate ShouldRun;

        public override bool Update(ComponentSystemGroup group)
        {
            if (m_IsRunning)
            {
                OnExitSystemGroup?.Invoke(group);
                m_IsRunning = false;
                return false;
            }

            if (ShouldRun(group))
            {
                OnEnterSystemGroup?.Invoke(group);
                m_IsRunning = true;
                return true;
            }
            m_IsRunning = false;
            return false;
        }
    }

    internal class RunMultiple : TickRateManagerStrategy
    {
        bool m_IsRunning;

        public ShouldRunDelegate ShouldRunFirstTime;
        public ShouldRunDelegate ShouldContinueRun;

        public override bool Update(ComponentSystemGroup group)
        {
            if (!m_IsRunning && ShouldRunFirstTime(group))
            {
                m_IsRunning = true;
                OnEnterSystemGroup?.Invoke(group);
                return true;
            }

            if (m_IsRunning && ShouldContinueRun(group))
            {
                m_IsRunning = true;
                OnSubsequentRuns?.Invoke(group);
                return true;
            }

            if (m_IsRunning)
            {
                OnExitSystemGroup?.Invoke(group);
            }
            m_IsRunning = false;
            return false;
        }
    }
}
