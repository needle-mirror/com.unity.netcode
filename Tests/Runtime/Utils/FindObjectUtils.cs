using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Tests
{
    internal static class FindObjectUtils
    {
        public static T[] FindObjectsByType<T>() where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return GameObject.FindObjectsByType<T>();
#else
            return GameObject.FindObjectsByType<T>(FindObjectsSortMode.None);
#endif
        }

        public static T[] FindObjectsByType<T>(FindObjectsInactive findObjectsInactive) where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return GameObject.FindObjectsByType<T>(findObjectsInactive);
#else
            return GameObject.FindObjectsByType<T>(findObjectsInactive, FindObjectsSortMode.None);
#endif
        }
    }
}
