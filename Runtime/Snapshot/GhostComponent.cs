using Unity.Entities;
using Unity.Collections;
using System;

namespace Unity.NetCode
{
    /// <summary>
    /// Component signaling an entity which is replicated over the network
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostComponent : IComponentData
    {
        /// <summary>
        /// The id assigned to the ghost by the server. When a ghost is destroyed, its id is recycled and can assigned to
        /// new ghosts. For that reason the ghost id cannot be used an unique identifier.
        /// The <see cref="ghostId"/>, <see cref="spawnTick"/> pairs is instead guaratee to be unique, since at any given
        /// point in time, there can be one and only one ghost that have a given id that has been spawned at that specific tick.
        /// </summary>
        public int ghostId;
        /// <summary>
        /// The ghost prefab type, as index inside the ghost prefab collection.
        /// </summary>
        public int ghostType;
        /// <summary>
        /// The tick the entity spawned on the server. Together with <see cref="ghostId"/> is guaranted to be always unique.
        /// </summary>
        public NetworkTick spawnTick;

        /// <summary>
        /// Implicitly convert a GhostComponent to a <see cref="SpawnedGhost"/> instance.
        /// </summary>
        /// <param name="comp"></param>
        /// <returns></returns>
        public static implicit operator SpawnedGhost(GhostComponent comp)
        {
            return new SpawnedGhost
            {
                ghostId = comp.ghostId,
                spawnTick = comp.spawnTick,
            };
        }
    }
    /// <summary>
    /// A tag added to child entities in a ghost with multiple entities. It should also be added to ghosts in a group if the ghost is not the root of the group.
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostChildEntityComponent : IComponentData
    {}
    /// <summary>
    /// Component storing the guid of the prefab the ghost was created from. This is used to lookup ghost type in a robust way which works even if two ghosts have the same archetype
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostTypeComponent : IComponentData,
        IEquatable<GhostTypeComponent>
    {
        /// <summary>
        /// The first 4 bytes of the prefab guid
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid0;
        /// <summary>
        /// The second 4 bytes of the prefab guid
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid1;
        /// <summary>
        /// The third 4 bytes of the prefab guid
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid2;
        /// <summary>
        /// The forth 4 bytes of the prefab guid
        /// </summary>
        [UnityEngine.SerializeField]
        internal uint guid3;

        /// <summary>
        /// Returns whether or not two GhostTypeComponent are identical.
        /// </summary>
        /// <param name="lhs"></param>
        /// <param name="rhs"></param>
        /// <returns>True if the the types guids are the same.</returns>
        public static bool operator ==(GhostTypeComponent lhs, GhostTypeComponent rhs)
        {
            return lhs.guid0 == rhs.guid0 && lhs.guid1 == rhs.guid1 && lhs.guid2 == rhs.guid2 && lhs.guid3 == rhs.guid3;
        }
        /// <summary>
        /// Returns whether or not two GhostTypeComponent are distinct.
        /// </summary>
        /// <param name="lhs"></param>
        /// <param name="rhs"></param>
        /// <returns>True if the the types guids are the different.</returns>
        public static bool operator !=(GhostTypeComponent lhs, GhostTypeComponent rhs)
        {
            return lhs.guid0 != rhs.guid0 || lhs.guid1 != rhs.guid1 || lhs.guid2 != rhs.guid2 || lhs.guid3 != rhs.guid3;
        }
        /// <summary>
        /// Returns whether or not the <see cref="other"/> reference is identical to the current instance.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(GhostTypeComponent other)
        {
            return this == other;
        }
        /// <summary>
        /// Returns whether or not the <see cref="obj"/> reference is of type `GhostTypeComponent`, and
        /// whether or not it's identical to the current instance.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>True if equal to the passed in `GhostTypeComponent`.</returns>
        public override bool Equals(object obj)
        {
            if(obj is GhostTypeComponent aGT) return Equals(aGT);
            return false;
        }

        /// <summary>
        /// Return an hashcode suitable for inserting the component into a dictionary or a sorted container.
        /// </summary>
        /// <returns>True if equal to the passed in `GhostTypeComponent`.</returns>
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
    /// Component used on the server to make sure the ghosts of different ghost types are in different chunks,
    /// even if they have the same archetype (regardless of component data).
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct SharedGhostTypeComponent : ISharedComponentData
    {
        /// <summary>
        /// Ghost type used for the this entity.
        /// </summary>
        public GhostTypeComponent SharedValue;
    }

    /// <summary>
    /// Component on client signaling that an entity is predicted (as opposed to interpolated).
    /// <seealso cref="GhostMode"/>
    /// <seealso cref="GhostModeMask"/>
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct PredictedGhostComponent : IComponentData
    {
        /// <summary>
        /// The last server snapshot that has been applied to the entity.
        /// </summary>
        public NetworkTick AppliedTick;
        /// <summary>
        /// <para>The server tick from which the entity should start predicting.</para>
        /// <para>When a new ghost snapshot is received, the entity is synced to the server state,
        /// and the PredictionStartTick is set to the snapshot server tick.</para>
        /// <para>Otherwise, the PredictionStartTick should correspond to:
        /// <para>- The last simulated full tick by the client (see <see cref="ClientServerTickRate"/>)
        /// if a prediction backup (see <see cref="GhostPredictionHistoryState"/>) exists </para>
        /// <para>- The last received snapshot tick if a continuation backup is not found.</para>
        /// </para>
        /// </summary>
        public NetworkTick PredictionStartTick;

        /// <summary>
        /// Query if the entity should be simulated (predicted) for the given tick.
        /// </summary>
        /// <param name="tick"></param>
        /// <returns>True if the entity should be simulated.</returns>
        public bool ShouldPredict(NetworkTick tick)
        {
            return !PredictionStartTick.IsValid || tick.IsNewerThan(PredictionStartTick);
        }
    }

    /// <summary>
    /// Component used to request predictive spawn of a ghost. Create an entity from a prefab
    /// in the ghost collection with this tag added. You need to implement a custom spawn
    /// classification system in order to use this.
    /// </summary>
    public struct PredictedGhostSpawnRequestComponent : IComponentData
    {
    }

    /// <summary>
    /// Component on the client signaling that an entity is a placeholder for a "not yet spawned" ghost.
    /// I.e. Not yet a "real" ghost.
    /// </summary>
    public struct PendingSpawnPlaceholderComponent : IComponentData
    {
    }

    /// <summary>
    /// Utility methods for working with GhostComponents.
    /// </summary>
    public static class GhostComponentUtilities
    {
        /// <summary>
        /// Find the first valid ghost type id in an array of ghost components.
        /// Pre-spawned ghosts have type id -1.
        /// </summary>
        /// <param name="self"></param>
        /// <returns>The ghost type index if a ghost with a valid type is found, -1 otherwise</returns>
        public static int GetFirstGhostTypeId(this NativeArray<GhostComponent> self)
        {
            return self.GetFirstGhostTypeId(out _);
        }

        /// <summary>
        /// Find the first valid ghost type id in an array of ghost components.
        /// Pre-spawned ghosts have type id -1.
        /// This method returns -1 if no ghost type id is found.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="firstGhost">The first valid ghost type index found will be stored in this variable.</param>
        /// <returns>A valid ghost type id, or -1 if no ghost type id was found.</returns>
        public static int GetFirstGhostTypeId(this NativeArray<GhostComponent> self, out int firstGhost)
        {
            firstGhost = 0;
            int ghostTypeId = self[0].ghostType;
            while (ghostTypeId == -1 && ++firstGhost < self.Length)
            {
                ghostTypeId = self[firstGhost].ghostType;
            }
            return ghostTypeId;
        }

        /// <summary>
        /// Retrieve the component name as <see cref="NativeText"/>. The method is burst compatible.
        /// </summary>
        /// <param name="self"></param>
        /// <returns>The component name.</returns>
        public static NativeText.ReadOnly GetDebugTypeName(this ComponentType self)
        {
            return TypeManager.GetTypeInfo(self.TypeIndex).DebugTypeName;
        }
    }
}
