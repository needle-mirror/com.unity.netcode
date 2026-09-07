using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Entities.Hybrid.Baking;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    /// <para>
    /// The GhostAuthoringComponent is the main entry point to configure and create replicated ghosts types in baked subscenes.
    /// The component must be added only to the GameObject hierarchy root.
    /// For runtime GameObject replication, please use <see cref="GhostObject"/>
    /// </para>
    /// <para>
    /// It allows setting all ghost properties,
    /// such as the replication mode <see cref="BaseGhostSettings.SupportedGhostModes"/>, bandwidth optimization strategy (<see cref="BaseGhostSettings.OptimizationMode"/>,
    /// the ghost <see cref="BaseGhostSettings.Importance"/> (how frequently is sent) and others).
    /// </para>
    /// </summary>
    /// <seealso cref="GhostAuthoringInspectionComponent"/>
    [RequireComponent(typeof(LinkedEntityGroupAuthoring))]
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringComponent)]
    [AddComponentMenu("Multiplayer/Ghost Authoring Component", 1)]
    [MovedFrom(true, "Unity.NetCode")]
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
        public new bool SupportsSendTypeOptimization => base.SupportsSendTypeOptimization;

    }
}
