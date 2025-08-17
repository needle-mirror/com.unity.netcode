using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Unity.NetCode
{
    /// <summary>
    /// Structure that contains the ghost <see cref="ArchetypeChunk"/> to serialize.
    /// Each chunk has its own priority, that is calculated based on the importance scaling
    /// factor (set for each ghost prefab at authoring time) and that can be further scaled
    /// using a custom <see cref="GhostImportance.BatchScaleImportanceFunction"/>.
    /// </summary>
    public struct PrioChunk : IComparable<PrioChunk>
    {
        /// <summary>
        /// The ghost chunk that should be processed.
        /// </summary>
        public ArchetypeChunk chunk;
        /// <summary>
        /// The priority of the chunk. When using the <see cref="GhostImportance.BatchScaleImportanceFunction"/>
        /// scaling, it is the method responsibility to update this with the scaled priority.
        /// </summary>
        public int priority;
        /// <summary>
        /// Fast-path denoting the relevancy of the entire chunk.
        /// </summary>
        /// <remarks>
        /// <para>
        ///     Defaults to <c>true</c> when relevancy is in mode <see cref="GhostRelevancyMode.Disabled"/> or <see cref="GhostRelevancyMode.SetIsIrrelevant"/>,
        ///     otherwise defaults to <c>false</c>.
        /// </para>
        /// <para>
        ///     When using this bool, there is no need to write ghost instances into the global `GhostRelevancySet`, unless
        ///     you need to add an exception (e.g. a ghost that is very far away from the player, yet should remain relevant).
        /// </para>
        /// <para>
        ///     Note: Why not use <see cref="priority"/> to denote relevancy? Because relevancy still requires the chunk
        ///     to be processed occassionally. In other words; there is a risk of breaking relevancy by forcing the importance
        ///     to be artificially low.
        /// </para>
        /// </remarks>
        public bool isRelevant;
        /// <summary>
        /// The first entity index in the chunk that should be serialized. Normally 0, but if was not possible to
        /// serialize the whole chunk, the next time we will start replicating ghosts from that index.
        /// </summary>
        internal int startIndex;
        /// <summary>
        /// The type index in the <see cref="GhostCollectionPrefab"/> used to retrieve the information for
        /// serializing the ghost.
        /// </summary>
        internal int ghostType;
        /// <summary>
        /// Used for sorting the based on the priority in descending order.
        /// </summary>
        /// <param name="other">Prio chunk</param>
        /// <returns>Descending order.</returns>
        public int CompareTo(PrioChunk other)
        {
            // Reverse priority for sorting
            return other.priority - priority;
        }
    }
    /// <summary>
    /// Singleton component used to control importance scaling (also called priority scaling) settings on the server.
    /// Used by the <see cref="GhostSendSystem"/> to help it prioritize which ghost chunks to write into each snapshot sent
    /// to each individual connection. I.e. Importance scaling is applied on a per-connection basis.
    /// Create this singleton in a server-only, user-code system to enable this feature.
    /// Further reading: https://docs.unity3d.com/Packages/com.unity.netcode@latest/index.html?subfolder=/manual/optimizations.html#importance-scaling
    /// </summary>
    /// <remarks>
    /// The most common use-case of importance scaling is "distance importance scaling". I.e. To send updates for nearby ghosts
    /// at a significantly higher frequency than for far away ghosts. Our default implementation (<see cref="GhostDistanceImportance"/>)
    /// does exactly that.
    /// </remarks>
    [BurstCompile]
    public struct GhostImportance : IComponentData
    {
        /// <summary>
        /// Scale importance delegate. This describes the interface <see cref="GhostSendSystem"/> will use to compute
        /// importance scaling. The higher importance value returned from this method, the more often a ghost's data is synchronized.
        /// See <see cref="GhostDistanceImportance"/> for example implementation.
        /// </summary>
        /// <param name="connectionData">Per connection data. Ex. position in the world that should be prioritized.</param>
        /// <param name="importanceData">Optional configuration data. Ex. Each tile's configuration. Handle IntPtr.Zero!</param>
        /// <param name="chunkTile">Per chunk information. Ex. each entity's tile index.</param>
        /// <param name="basePriority">Priority computed by <see cref="GhostSendSystem"/> after computing tick when last updated and irrelevance.</param>
        /// <returns>Scale importance value.</returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ScaleImportanceDelegate(IntPtr connectionData, IntPtr importanceData, IntPtr chunkTile, int basePriority);

        /// <summary>
        /// Default implementation of <see cref="ScaleImportanceDelegate"/>. Will return basePriority without computation.
        /// </summary>
        public static readonly PortableFunctionPointer<ScaleImportanceDelegate> NoScaleFunctionPointer =
            new PortableFunctionPointer<ScaleImportanceDelegate>(NoScale);

        /// <summary>
        /// Scale importance delegate. This describes the interface <see cref="GhostSendSystem"/> will use to compute
        /// importance scaling.
        /// The method is responsible to modify the <see cref="PrioChunk.priority"/> property for all the chunks (the higher the prioriy, the more often a ghost's data is synchronized).
        /// See <see cref="GhostDistanceImportance"/> for example implementation.
        /// </summary>
        /// <param name="connectionData">Per connection data. Ex. position in the world that should be prioritized.</param>
        /// <param name="importanceData">Optional configuration data. Ex. Each tile's configuration. Handle IntPtr.Zero!</param>
        /// <param name="sharedComponentTypeHandlePtr"><see cref="DynamicSharedComponentTypeHandle"/> to retrieve the per-chunk tile information. Ex. each chunk's tile index.</param>
        /// <param name="chunkData">Chunk data.</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void BatchScaleImportanceDelegate(IntPtr connectionData, IntPtr importanceData, IntPtr sharedComponentTypeHandlePtr,
            ref UnsafeList<PrioChunk> chunkData);
        /// <summary>
        /// <para>
        /// This function pointer will be invoked with collected data as described in <see cref="BatchScaleImportanceDelegate"/>.
        /// </para>
        /// <para>It is mandatory to set either this or <see cref="BatchScaleImportanceFunction"/> function pointer.
        /// It is also valid to set both, in which case the BatchScaleImportanceFunction is preferred.
        /// </para>
        /// </summary>
        [Obsolete("Prefer `BatchScaleImportanceDelegate` as it significantly reduces the total number of function pointer calls. RemoveAfter 1.x", false)]
        public PortableFunctionPointer<ScaleImportanceDelegate> ScaleImportanceFunction;
        /// <summary>
        /// <para>
        /// This function pointer will be invoked with collected data as described in <see cref="BatchScaleImportanceDelegate"/>.
        /// </para>
        /// <para>It is mandatory to set either this or <see cref="ScaleImportanceFunction"/> function pointer.
        /// It is also valid to set both, in which case the BatchScaleImportanceFunction is preferred.
        /// </para>
        /// </summary>
        public PortableFunctionPointer<BatchScaleImportanceDelegate> BatchScaleImportanceFunction;
        /// <summary>
        /// ComponentType for connection data. <see cref="GhostSendSystem"/> will query for this component type before
        /// invoking the function assigned to <see cref="BatchScaleImportanceFunction"/>.
        /// </summary>
        public ComponentType GhostConnectionComponentType;
        /// <summary>
        /// Optional singleton ComponentType for configuration data.
        /// Leave default if not required. <see cref="IntPtr.Zero"/> will be passed into the <see cref="BatchScaleImportanceFunction"/>.
        /// <see cref="GhostSendSystem"/> will query for this component type, passing the data into the
        /// <see cref="BatchScaleImportanceFunction"/> function when invoking it.
        /// </summary>
        public ComponentType GhostImportanceDataType;
        /// <summary>
        /// ComponentType for per chunk data. Must be a shared component type! Each chunk represents a group of entities,
        /// collected as they share some importance-related value (e.g. distance to the players character controller).
        /// <see cref="GhostSendSystem"/> will query for this component type before invoking the function assigned to <see cref="BatchScaleImportanceFunction"/>.
        /// </summary>
        /// <remarks>
        /// Tip: You can use the existence of this type to filter/decide which ghost chunks should even undergo importance scaling
        /// by the <see cref="GhostSendSystem"/>. To exclude a type from importance scaling, do not add this shared component to their chunk.
        /// </remarks>
        public ComponentType GhostImportancePerChunkDataType;

        [BurstCompile(DisableDirectCall = true)]
        [AOT.MonoPInvokeCallback(typeof(ScaleImportanceDelegate))]
        static int NoScale(IntPtr connectionData, IntPtr importanceData, IntPtr chunkTile, int basePriority)
        {
            return basePriority;
        }

#pragma warning disable 618 // Type or member is obsolete.
        /// <summary>
        /// This property successfully suppresses the obsolete warning.
        /// Attempting to do so inside the <see cref="GhostSendSystem"/> did not work (presumably for SystemAPI code-gen reasons).
        /// </summary>
        internal PortableFunctionPointer<ScaleImportanceDelegate> ScaleImportanceFunctionSuppressedWarning => ScaleImportanceFunction;
#pragma warning restore 618 // Type or member is obsolete.
    }
}
