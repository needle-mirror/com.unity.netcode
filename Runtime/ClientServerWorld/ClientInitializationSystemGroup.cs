using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientInitializationSystemGroup : InitializationSystemGroup
    {
#if !UNITY_SERVER
        internal TickClientInitializationSystem ParentTickSystem;
        protected override void OnDestroy()
        {
            if (ParentTickSystem != null)
                ParentTickSystem.RemoveSystemFromUpdateList(this);
#if !UNITY_DOTSRUNTIME
            ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(World);
#endif
        }
#endif
    }

#if !UNITY_SERVER
#if !UNITY_DOTSRUNTIME
    [DisableAutoCreation]
#endif
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [AlwaysUpdateSystem]
    [UpdateInWorld(TargetWorld.Default)]
    public class TickClientInitializationSystem : ComponentSystemGroup
    {
        protected override void OnDestroy()
        {
            foreach (var sys in Systems)
            {
                var grp = sys as ClientInitializationSystemGroup;
                if (grp != null)
                    grp.ParentTickSystem = null;
            }
        }
    }
#endif
}
