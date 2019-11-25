using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ServerInitializationSystemGroup : InitializationSystemGroup
    {
        protected override void OnUpdate()
        {
#pragma warning disable 618
            // we're keeping World.Active until we can properly remove them all
            var defaultWorld = World.Active;
            World.Active = World;
            base.OnUpdate();
            World.Active = defaultWorld;
#pragma warning restore 618
        }
    }

#if !UNITY_CLIENT || UNITY_SERVER || UNITY_EDITOR
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class TickServerInitializationSystem : ComponentSystemGroup
    {
        public override void SortSystemUpdateList()
        {
        }
    }
#endif
}