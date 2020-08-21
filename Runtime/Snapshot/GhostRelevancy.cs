using System;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.NetCode
{
    public enum GhostRelevancyMode
    {
        Disabled,
        SetIsRelevant,
        SetIsIrrelevant
    }
    public struct RelevantGhostForConnection : IEquatable<RelevantGhostForConnection>, IComparable<RelevantGhostForConnection>
    {
        public RelevantGhostForConnection(int connection, int ghost)
        {
            Connection = connection;
            Ghost = ghost;
        }
        public bool Equals(RelevantGhostForConnection other)
        {
            return Connection == other.Connection && Ghost == other.Ghost;
        }
        public int CompareTo(RelevantGhostForConnection other)
        {
            if (Connection == other.Connection)
                return Ghost - other.Ghost;
            return Connection - other.Connection;
        }
        public override int GetHashCode()
        {
            return (Connection << 24) | Ghost;
        }
        public int Connection;
        public int Ghost;
    }
}