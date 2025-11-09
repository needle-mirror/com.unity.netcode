using System;
using Unity.Entities.Editor;

namespace Unity.NetCode.Editor
{
    static class NetcodeEditorUtility
    {
        internal static void ShowGhostComponentInspectorContent(Type type)
        {
            ContentUtilities.ShowComponentInspectorContent(type);
        }
    }
}
