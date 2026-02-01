#if UNITY_EDITOR
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    /// <summary>Ensures that the baked ghost prespawn has a LocalTransform component.</summary>
    internal class AddLocalTransformToGhostBaker : MonoBehaviour
    {
        private class AddLocalTransformToGhostBakerBaker : Baker<AddLocalTransformToGhostBaker>
        {
            public override void Bake(AddLocalTransformToGhostBaker authoring)
            {
                GetEntity(TransformUsageFlags.Dynamic);
            }
        }
    }
}
#endif