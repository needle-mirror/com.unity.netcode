using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ServerInitializationSystemGroup : InitializationSystemGroup
    {
#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
        internal TickServerInitializationSystem ParentTickSystem;
        protected override void OnDestroy()
        {
            if (ParentTickSystem != null)
                ParentTickSystem.RemoveSystemFromUpdateList(this);
        }
#endif
    }

#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class TickServerInitializationSystem : ComponentSystemGroup
    {
        protected override void OnDestroy()
        {
            foreach (var sys in Systems)
            {
                var grp = sys as ServerInitializationSystemGroup;
                if (grp != null)
                    grp.ParentTickSystem = null;
            }
        }
    }
#endif
}