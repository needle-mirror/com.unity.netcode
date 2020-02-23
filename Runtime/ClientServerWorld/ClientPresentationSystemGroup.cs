using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientPresentationSystemGroup : PresentationSystemGroup
    {
#if !UNITY_SERVER
        internal TickClientPresentationSystem ParentTickSystem;
        protected override void OnDestroy()
        {
            if (ParentTickSystem != null)
                ParentTickSystem.RemoveSystemFromUpdateList(this);
        }
#endif
        protected override void OnUpdate()
        {
            if (HasSingleton<ThinClientComponent>())
                return;
            base.OnUpdate();
        }
    }

#if !UNITY_SERVER
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class TickClientPresentationSystem : ComponentSystemGroup
    {
        protected override void OnDestroy()
        {
            foreach (var sys in Systems)
            {
                var grp = sys as ClientPresentationSystemGroup;
                if (grp != null)
                    grp.ParentTickSystem = null;
            }
        }
    }
#endif
}