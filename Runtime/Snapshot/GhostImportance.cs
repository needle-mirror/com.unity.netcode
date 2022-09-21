using System;
using System.Runtime.InteropServices;
using AOT;
using Unity.Burst;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Singleton component used to control importance settings
    /// </summary>
    [BurstCompile]
    public struct GhostImportance : IComponentData
    {
        /// <summary>
        /// Scale importance delegate. This describes the interface <see cref="GhostSendSystem"/> will use to compute
        /// importance scaling. The higher importance value returned from this method, the more often a ghost's data is synchronized.
        /// See <see cref="GhostDistanceImportance"/> for example implementation.
        /// </summary>
        /// <param name="connectionData">Per connection data. Ex. position in the world that should be prioritized.</param>
        /// <param name="importanceData">Configuration data. Ex. Each tile's configuration.</param>
        /// <param name="chunkTile">Per chunk information. Ex. each entity's tile index.</param>
        /// <param name="basePriority">Priority computed by <see cref="GhostSendSystem"/> after computing tick when last updated and irrelevance.</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ScaleImportanceDelegate(IntPtr connectionData, IntPtr importanceData, IntPtr chunkTile, int basePriority);

        /// <summary>
        /// Default implementation of <see cref="ScaleImportanceDelegate"/>. Will return basePriority without computation.
        /// </summary>
        public static readonly PortableFunctionPointer<ScaleImportanceDelegate> NoScaleFunctionPointer =
            new PortableFunctionPointer<ScaleImportanceDelegate>(NoScale);

        /// <summary>
        /// This function pointer will be invoked with collected data as described in <see cref="ScaleImportanceDelegate"/>.
        /// </summary>
        public PortableFunctionPointer<ScaleImportanceDelegate> ScaleImportanceFunction;
        /// <summary>
        /// ComponentType for connection data. <see cref="GhostSendSystem"/> will query for this component type before
        /// invoking the function assigned to <see cref="ScaleImportanceFunction"/>.
        /// </summary>
        public ComponentType GhostConnectionComponentType;
        /// <summary>
        /// ComponentType for configuration data. <see cref="GhostSendSystem"/> will query for this component type before
        /// invoking the function assigned to <see cref="ScaleImportanceFunction"/>.
        /// </summary>
        public ComponentType GhostImportanceDataType;
        /// <summary>
        /// ComponentType for per chunk data. Each chunk represents a group of entities.
        /// <see cref="GhostSendSystem"/> will query for this component type before invoking the function assigned to <see cref="ScaleImportanceFunction"/>.
        /// </summary>
        public ComponentType GhostImportancePerChunkDataType;

        [BurstCompile(DisableDirectCall = true)]
        [MonoPInvokeCallback(typeof(ScaleImportanceDelegate))]
        private static int NoScale(IntPtr connectionData, IntPtr importanceData, IntPtr chunkTile, int basePriority)
        {
            return basePriority;
        }
    }
}
