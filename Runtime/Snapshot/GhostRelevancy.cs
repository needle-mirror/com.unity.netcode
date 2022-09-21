using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// Specify how the ghosts added to the relevancy set should be used.
    /// </summary>
    public enum GhostRelevancyMode
    {
        /// <summary>
        /// The default, no relevancy will applied.
        /// </summary>
        Disabled,
        /// <summary>
        /// All ghosts added to relevancy set are considered relevant, and serialized for the specified connection if possible.
        /// </summary>
        SetIsRelevant,
        /// <summary>
        /// All ghosts added to relevancy set are considered not-relevant, and will be not serialized for the specified connection.
        /// </summary>
        SetIsIrrelevant
    }

    /// <summary>
    /// The RelevantGhostForConnection is a connection-ghost pairs, used to populate the <see cref="GhostRelevancy"/> set
    /// at runtime, by declaring which ghosts are relevant for a given connection.
    /// </summary>
    public struct RelevantGhostForConnection : IEquatable<RelevantGhostForConnection>, IComparable<RelevantGhostForConnection>
    {
        /// <summary>
        /// Construct a new instance with the given connection id and ghost
        /// </summary>
        /// <param name="connection">The connection id</param>
        /// <param name="ghost"></param>
        public RelevantGhostForConnection(int connection, int ghost)
        {
            Connection = connection;
            Ghost = ghost;
        }
        /// <summary>
        /// return whenever the <paramref name="other"/> RelevantGhostForConnection is equals the current instance.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public bool Equals(RelevantGhostForConnection other)
        {
            return Connection == other.Connection && Ghost == other.Ghost;
        }
        /// <summary>
        /// Comparison operator, used for sorting.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(RelevantGhostForConnection other)
        {
            if (Connection == other.Connection)
                return Ghost - other.Ghost;
            return Connection - other.Connection;
        }
        /// <summary>
        /// A hash code suitable to insert the RelevantGhostForConnection into an hashmap or
        /// other key-value pair containers. Is guarantee to be unique for the connection, ghost pairs.
        /// </summary>
        /// <returns></returns>
        public override int GetHashCode()
        {
            return (Connection << 24) | Ghost;
        }
        /// <summary>
        /// The connection for which this ghost is relevant.
        /// </summary>
        public int Connection;
        /// <summary>
        /// the ghost id of the entity.
        /// </summary>
        public int Ghost;
    }

    /// <summary>
    /// Singleton entity presente on the server, that should be used to collect every frame the set of ghosts
    /// that should be replicated to clients.
    /// </summary>
    public struct GhostRelevancy : IComponentData
    {
        internal GhostRelevancy(NativeParallelHashMap<RelevantGhostForConnection, int> set)
        {
            GhostRelevancySet = set;
            GhostRelevancyMode = GhostRelevancyMode.Disabled;
        }
        /// <summary>
        /// Specify if the ghosts present in the <see cref="GhostRelevancySet"/> should be replicated (relevant) or not replicated
        /// (irrelevant) to the the client.
        /// </summary>
        public GhostRelevancyMode GhostRelevancyMode;
        /// <summary>
        /// A sorted collection of (connection, ghost) pairs, that should be used to specify which ghosts, for a given
        /// connection, should be replicated (or not replicated, based on the <see cref="GhostRelevancyMode"/>) for the current
        /// simulated tick.
        /// </summary>
        public readonly NativeParallelHashMap<RelevantGhostForConnection, int> GhostRelevancySet;
    }
}
