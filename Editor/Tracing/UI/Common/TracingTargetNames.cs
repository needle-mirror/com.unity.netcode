using System.Collections.Generic;
using Unity.Entities;

namespace Unity.NetCode.Editor.Tracing.UI
{
    internal static class TracingTargetNames
    {
        static readonly Dictionary<SystemTypeIndex, string> s_SystemNames = new();
        static readonly Dictionary<TypeIndex, string> s_ComponentNames = new();

        public static string System(SystemTypeIndex system)
        {
            if (!s_SystemNames.TryGetValue(system, out var name))
                s_SystemNames[system] = name = TypeManager.GetSystemType(system)?.FullName ?? TypeManager.GetSystemName(system).ToString();
            return name;
        }

        public static string Component(TypeIndex component)
        {
            if (!s_ComponentNames.TryGetValue(component, out var name))
                s_ComponentNames[component] = name = TypeManager.GetType(component)?.FullName ?? component.ToString();
            return name;
        }
    }
}
