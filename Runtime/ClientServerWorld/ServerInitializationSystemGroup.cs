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
#if !UNITY_DOTSRUNTIME
            //The playerloop is double buffered. However, becasue the ComponentSystemBase is a managed class
            //and the StatePtr is set to null, all the DummyWrapper instances will get called but will result in
            //an early return.
            ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(World);
#endif
        }
#endif
    }

#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
#if !UNITY_DOTSRUNTIME
    [DisableAutoCreation]
#endif
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [AlwaysUpdateSystem]
    [UpdateInWorld(TargetWorld.Default)]
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
