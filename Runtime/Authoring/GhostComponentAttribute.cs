using System;

namespace Unity.NetCode
{
    public class GhostDefaultComponentAttribute : Attribute
    {
        [Flags]
        public enum Type
        {
            InterpolatedClient = 1,
            PredictedClient = 2,
            Client = 3,
            Server = 4,
            All = 7
        }

        private Type m_targetType;
        public Type TargetType => m_targetType;

        public GhostDefaultComponentAttribute(Type targetType)
        {
            m_targetType = targetType;
        }
    }
}
