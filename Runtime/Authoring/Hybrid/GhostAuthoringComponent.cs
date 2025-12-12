using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Entities.Hybrid.Baking;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>
    /// The GhostAuthoringComponent is the main entry point to configure and create replicated ghosts types in baked subscenes.
    /// The component must be added only to the GameObject hierarchy root.
    /// For runtime GameObject replication, please use <see cref="GhostAdapter"/>
    /// </para>
    /// <para>
    /// It allows setting all ghost properties,
    /// such as the replication mode <see cref="SupportedGhostModes"/>, bandwidth optimization strategy (<see cref="OptimizationMode"/>,
    /// the ghost <see cref="Importance"/> (how frequently is sent) and others).
    /// </para>
    /// </summary>
    /// <seealso cref="GhostAuthoringInspectionComponent"/>
    [RequireComponent(typeof(LinkedEntityGroupAuthoring))]
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringComponent)]
    public class GhostAuthoringComponent: BaseGhostSettings
    {
        /// <summary>
        /// Validate the name of the GameObject prefab.
        /// </summary>
        /// <param name="ghostNameHash">Outputs the hash generated from the name.</param>
        /// <returns>The FS equivalent of the gameObject.name.</returns>
        public FixedString64Bytes GetAndValidateGhostName(out ulong ghostNameHash)
        {
            var ghostName = gameObject.name;
            var ghostNameFs = new FixedString64Bytes();
            var nameCopyError = FixedStringMethods.CopyFromTruncated(ref ghostNameFs, ghostName);
            ghostNameHash = TypeHash.FNV1A64(ghostName);
            if (nameCopyError != CopyError.None)
                Debug.LogError($"{nameCopyError} when saving GhostName \"{ghostName}\" into FixedString64Bytes, became: \"{ghostNameFs}\"!", this);
            return ghostNameFs;
        }
        /// <summary>True if we can apply the <see cref="GhostSendType"/> optimization on this Ghost.</summary>
        public bool SupportsSendTypeOptimization => SupportedGhostModes != GhostModeMask.All || DefaultGhostMode == GhostMode.OwnerPredicted;

    }
}
