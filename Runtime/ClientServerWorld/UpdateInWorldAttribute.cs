using System;
using Unity.Entities;

namespace Unity.NetCode
{
    [Flags]
    public enum TargetWorld
    {
        Default = 0,
        Client = 1,
        Server = 2,
        ClientAndServer = 3
    }

    public class UpdateInWorldAttribute : Attribute
    {
        private TargetWorld m_world;
        public TargetWorld World => m_world;

        public UpdateInWorldAttribute(TargetWorld w)
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