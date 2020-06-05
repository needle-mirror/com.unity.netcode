using Unity.Entities;
using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Component signaling an entity which is replicated over the network
    /// </summary>
    public struct GhostComponent : IComponentData
    {
        public int ghostId;
    }
    /// <summary>
    /// Component storing the guid of the prefab the ghost was created from. This is used to lookup ghost type in a robust way which works even if two ghosts have the same archetype
    /// </summary>
    public struct GhostTypeComponent : IComponentData,
        IEquatable<GhostTypeComponent>
    {
        public uint guid0;
        public uint guid1;
        public uint guid2;
        public uint guid3;

        public static bool operator ==(GhostTypeComponent lhs, GhostTypeComponent rhs)
        {
            return lhs.guid0 == rhs.guid0 && lhs.guid1 == rhs.guid1 && lhs.guid2 == rhs.guid2 && lhs.guid3 == rhs.guid3;
        }
        public static bool operator !=(GhostTypeComponent lhs, GhostTypeComponent rhs)
        {
            return lhs.guid0 != rhs.guid0 || lhs.guid1 != rhs.guid1 || lhs.guid2 != rhs.guid2 || lhs.guid3 != rhs.guid3;
        }
        public bool Equals(GhostTypeComponent other)
        {
            return this == other;
        }
        public override bool Equals(object obj)
        {
            if(obj is GhostTypeComponent aGT) return Equals(aGT);
            return false;
        }
        public override int GetHashCode()
        {
            var result = guid0.GetHashCode();
            result = (result*31) ^ guid1.GetHashCode();
            result = (result*31) ^ guid2.GetHashCode();
            result = (result*31) ^ guid3.GetHashCode();
            return result;
        }
    }
    /// <summary>
    /// Component used on the server to make sure ghosts of difference ghost types are in different chunks even if they have the same archetype (but different data)
    /// </summary>
    public struct SharedGhostTypeComponent : ISharedComponentData
    {
        public GhostTypeComponent SharedValue;
    }

    /// <summary>
    /// Component on client signaling that an entity is predicted instead of interpolated
    /// </summary>
    public struct PredictedGhostComponent : IComponentData
    {
        public uint AppliedTick;
        public uint PredictionStartTick;
    }

    /// <summary>
    /// Component used to request predictive spawn of a ghost. Create an entity with this
    /// tag and an ISnapshotData of the ghost type you want to create. The ghost type must
    /// support predictive spawning to use this.
    /// </summary>
    public struct PredictedGhostSpawnRequestComponent : IComponentData
    {
    }

    /// <summary>
    /// The hash of all the ghost component data which exists in the scene. This can be
    /// used to sort the subscenes so the ghost IDs of the pre-spawned scene objects line
    /// up deterministically.
    /// </summary>
    public struct SubSceneGhostComponentHash : ISharedComponentData
    {
        public ulong Value;
    }

    /// <summary>
    /// The ghost ID on a pre-spawned entity, it's unique withing the subscene.
    /// </summary>
    public struct PreSpawnedGhostId : IComponentData
    {
        public int Value;
    }
}
