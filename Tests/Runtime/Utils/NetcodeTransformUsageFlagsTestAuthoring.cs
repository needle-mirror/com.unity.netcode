using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Entities;

/// <summary>
/// NetcodeTransformUsageFlagsTestAuthoring
/// </summary>
internal class NetcodeTransformUsageFlagsTestAuthoring : MonoBehaviour
{
    /// <summary>
    /// Baker for NetcodeTransformUsageFlagsTestAuthoring
    /// </summary>
    internal class Baker : Baker<NetcodeTransformUsageFlagsTestAuthoring>
    {
        /// <summary>
        /// Baker function
        /// </summary>
        /// <param name="authoring">Authoring instance</param>
        public override void Bake(NetcodeTransformUsageFlagsTestAuthoring authoring)
        {
            AddTransformUsageFlags(TransformUsageFlags.Dynamic);
        }
    }
}
