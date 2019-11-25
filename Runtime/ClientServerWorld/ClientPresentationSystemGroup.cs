using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientPresentationSystemGroup : PresentationSystemGroup
    {
        protected override void OnUpdate()
        {
            if (HasSingleton<ThinClientComponent>())
                return;

#pragma warning disable 618
            // we're keeping World.Active until we can properly remove them all
            var defaultWorld = World.Active;
            World.Active = World;
            base.OnUpdate();
            World.Active = defaultWorld;
#pragma warning restore 618
        }
    }

#if !UNITY_SERVER
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [AlwaysUpdateSystem]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Default)]
    public class TickClientPresentationSystem : ComponentSystemGroup
    {
        public override void SortSystemUpdateList()
        {
        }
    }
#endif
}