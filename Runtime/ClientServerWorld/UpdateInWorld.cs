using System;
using Unity.Entities;

namespace Unity.NetCode
{
    public class UpdateInWorld : Attribute
    {
        [Flags]
        public enum TargetWorld
        {
            Default = 0,
            Client = 1,
            Server = 2,
            ClientAndServer = 3
        }

        private TargetWorld m_world;
        public TargetWorld World => m_world;

        public UpdateInWorld(TargetWorld w)
        {
            m_world = w;
        }
    }

    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientAndServerSimulationSystemGroup : ComponentSystemGroup
    {
    }

    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class ClientAndServerInitializationSystemGroup : ComponentSystemGroup
    {
    }
}