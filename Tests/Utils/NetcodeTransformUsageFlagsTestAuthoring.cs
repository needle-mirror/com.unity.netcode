using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Entities;

/// <summary>
/// NetcodeTransformUsageFlagsTestAuthoring
/// </summary>
public class NetcodeTransformUsageFlagsTestAuthoring : MonoBehaviour
{
    /// <summary>
    /// Baker for NetcodeTransformUsageFlagsTestAuthoring
    /// </summary>
    public class Baker : Baker<NetcodeTransformUsageFlagsTestAuthoring>
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
