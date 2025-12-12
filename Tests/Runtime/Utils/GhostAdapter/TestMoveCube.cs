#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class TestMoveCube : GhostBehaviour//<TestMoveCube.MoveData>
    {
        private Vector3 m_OriginPos;
        public static int CubeInstances;

        public override void Awake()
        {
            transform.position = Vector3.zero;
            m_OriginPos = transform.position;
            CubeInstances++;
        }

        public override void OnDestroy()
        {
            CubeInstances--;
        }

        public bool BelongTo(World world)
        {
            return world == this.Ghost.World;
        }
    }
}
#endif
