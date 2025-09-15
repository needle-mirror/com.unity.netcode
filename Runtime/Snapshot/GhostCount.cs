using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Singleton component with APIs and collections required for Ghost counting.
    /// </summary>
    [BurstCompile]
    public struct GhostCount : IComponentData
    {
        /// <summary>
        /// The <b>approximate</b> total number of relevant ghosts that the server wishes to send to this client.
        /// Updated in each snapshot, thus updated on the client whenever a snapshot is received.
        /// </summary>
        /// <seealso cref="InstantiatedPercent"/>
        /// <seealso cref="ReceivedPercent"/>
        public int GhostCountOnServer => IsCreated ? m_GhostCompletionCount[0] : 0;

        /// <inheritdoc cref="GhostCountReceivedOnClient"/>
        [Obsolete("Prefer either GhostCountInstantiatedOnClient or GhostCountReceivedOnClient, as this variable is ambiguous (and maps to GhostCountReceivedOnClient). RemoveAfter 1.x.", false)]
        public int GhostCountOnClient => IsCreated ? m_GhostCompletionCount[1] : 0;

        /// <summary>
        /// The total number of relevant (to our connection) ghosts that the client has actually instantiated
        /// (but skips/ignores <see cref="PendingSpawnPlaceholder"/> ghost instances).
        /// Count is updated any time a finalized ghost is actually instantiated or destroyed (via the <see cref="GhostSpawnSystemGroup"/>).
        /// <br/>Zero if <see cref="IsCreated"/> is false.
        /// Use this with <see cref="GhostCountOnServer"/> to figure out how much of the state the client has received.
        /// </summary>
        /// <remarks>
        /// Note: If the relevant set suddenly changes - or the server destroys many ghosts within a single frame -
        /// it's possible to have more ghosts on the client than the client should have.
        /// </remarks>
        /// <seealso cref="InstantiatedPercent"/>
        /// <seealso cref="GhostCountReceivedOnClient"/>
        public int GhostCountInstantiatedOnClient => IsCreated ? m_GhostCompletionCount[2] : 0;

        /// <summary>
        /// The total number of relevant (to our connection) ghosts that the client has received (NOT instantiated!).
        /// Count is updated any time a snapshot is received and processed.
        /// <br/>The number of received ghosts can be different from the number of currently spawned ghosts.
        /// <br/>Zero if <see cref="IsCreated"/> is false.
        /// Use this with <see cref="GhostCountOnServer"/> to figure out how much of the state the client has received.
        /// </summary>
        /// <remarks>
        /// Note: If the relevant set suddenly changes - or the server destroys many ghosts within a single frame -
        /// it's possible to have more ghosts on the client than the client should have.
        /// </remarks>
        /// <seealso cref="ReceivedPercent"/>
        /// <seealso cref="GhostCountInstantiatedOnClient"/>
        public int GhostCountReceivedOnClient => IsCreated ? m_GhostCompletionCount[1] : 0;

        /// <summary>
        /// Denotes the percentage of ghosts instantiated on the client (<see cref="GhostCountInstantiatedOnClient"/>)
        /// versus the number of ghosts the server has said exist (i.e. <see cref="GhostCountOnServer"/>).
        /// <br/>Only counts relevant ghosts!
        /// <br/>0% when no ghosts are expected: I.e. No ghosts spawned on server, or no ghosts considered relevant,
        /// or if this struct is not initialized (i.e. when <see cref="IsCreated"/> is false).
        /// Distinct from <see cref="ReceivedPercent"/>!
        /// </summary>
        /// <remarks>
        /// Note: If the relevant set suddenly changes - or the server destroys many ghosts within a single frame -
        /// it's possible to have more ghosts on the client than the client should have. Therefore, this value can be
        /// greater than 100%.
        /// <br/>Also note: Due to above nuances, it's possible to have the correct count of ghosts, but it's the
        /// incorrect set. In other words: This percentage is a naive approximation of 'the client has replicated
        /// everything they need'.
        /// </remarks>
        public float InstantiatedPercent => IsCreated && GhostCountOnServer != 0 ? (float) GhostCountInstantiatedOnClient / GhostCountOnServer : -1;

        /// <summary>
        /// Denotes the percentage of ghosts received by the client (<see cref="GhostCountReceivedOnClient"/>)
        /// versus the number of ghosts the server has said exist to us (i.e. <see cref="GhostCountOnServer"/>).
        /// <br/>Only counts relevant ghosts!
        /// <br/>0% when no ghosts are expected: I.e. No ghosts spawned on server, or no ghosts considered relevant,
        /// or if this struct is not initialized (i.e. when <see cref="IsCreated"/> is false).
        /// Distinct from <see cref="InstantiatedPercent"/>!
        /// </summary>
        /// <remarks>
        /// Note: If the relevant set suddenly changes - or the server destroys many ghosts within a single frame -
        /// it's possible to have more ghosts on the client than the client should have. Therefore, this value can be
        /// greater than 100%.
        /// <br/>Also note: Due to above nuances, it's possible to have the correct count of ghosts, but it's the
        /// incorrect set. In other words: This percentage is a naive approximation of 'the client has replicated
        /// everything they need'.
        /// </remarks>
        public float ReceivedPercent => IsCreated && GhostCountOnServer != 0 ? (float) GhostCountReceivedOnClient / GhostCountOnServer : -1;

        /// <summary>Helper denoting if the values are valid.</summary>
        public bool IsCreated => m_GhostCompletionCount.IsCreated;

        internal NativeArray<int> m_GhostCompletionCount;

        /// <summary>
        /// Construct and initialize the new ghost count instance.
        /// </summary>
        /// <param name="ghostCompletionCount"></param>
        internal GhostCount(NativeArray<int> ghostCompletionCount)
        {
            m_GhostCompletionCount = ghostCompletionCount;
        }

        /// <summary>
        /// For debugging and logging.
        /// </summary>
        /// <returns>Logs <c>GhostCount[received:GhostCountReceivedOnClient %, inst:GhostCountInstantiatedOnClient %, server:GhostCountOnServer]</c>.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString128Bytes ToFixedString() => IsCreated ? $"GhostCount[received:{GhostCountReceivedOnClient} {(int)(ReceivedPercent * 100)}%, inst:{GhostCountInstantiatedOnClient} {(int)(InstantiatedPercent * 100)}%, server:{GhostCountOnServer}]" : "GhostCount[default]";

        /// <inheritdoc cref="ToFixedString"/>
        public override string ToString() => ToFixedString().ToString();
    }
}
