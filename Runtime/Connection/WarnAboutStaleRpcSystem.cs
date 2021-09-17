#if ENABLE_UNITY_COLLECTIONS_CHECKS
using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode
{
    [UpdateInWorld(TargetWorld.ClientAndServer)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class WarnAboutStaleRpcSystem : SystemBase
    {
        NetDebugSystem m_NetDebugSystem;
        ushort m_MaxRpcAgeFrames = 4;

        /// <summary>
        ///     A NetCode RPC will trigger a warning if it hasn't been consumed or destroyed (which is a proxy for 'handled') after
        ///     this many simulation frames (inclusive).
        ///     <see cref="ReceiveRpcCommandRequestComponent.Age" />.
        ///     Set to 0 to opt out.
        /// </summary>
        public ushort MaxRpcAgeFrames
        {
            get => m_MaxRpcAgeFrames;
            set
            {
                m_MaxRpcAgeFrames = value;
                Enabled = value > 0;
            }
        }

        protected override void OnCreate()
        {
            m_NetDebugSystem = World.GetExistingSystem<NetDebugSystem>();
        }

        protected override void OnUpdate()
        {
            var worldName = new FixedString32Bytes(World.Name);
            var maxRpcAgeFrames = MaxRpcAgeFrames;
            var netDebug = m_NetDebugSystem.NetDebug;
            Entities.ForEach((Entity entity, ref ReceiveRpcCommandRequestComponent command) =>
            {
                if (!command.IsConsumed && ++command.Age >= maxRpcAgeFrames)
                {
                    var warning = (FixedString512Bytes)FixedString.Format("In '{0}', NetCode RPC {1} has not been consumed or destroyed for '{2}' (MaxRpcAgeFrames) frames!", worldName, entity.ToFixedString(), command.Age);
                    warning.Append((FixedString128Bytes)" Assumed unhandled. Call .Consume(), or remove the RPC component, or destroy the entity.");
                    netDebug.LogWarning(warning);

                    command.Consume();
                }
            }).Run();
        }
    }
}
#endif
