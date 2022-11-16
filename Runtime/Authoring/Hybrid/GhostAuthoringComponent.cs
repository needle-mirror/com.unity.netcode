using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Entities.Hybrid.Baking;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>The GhostAuthoringComponent is the main entry point to configure and create replicated ghosts types.
    /// The component must be added only to the GameObject hierarchy root.</para>
    /// <para>It allows setting all ghost properties,
    /// such as the replication mode <see cref="SupportedGhostModes"/>, bandwidth optimization strategy (<see cref="OptimizationMode"/>,
    /// the ghost <see cref="Importance"/> (how frequently is sent) and others).</para>
    /// <seealso cref="GhostAuthoringInspectionComponent"/>
    /// </summary>
    [RequireComponent(typeof(LinkedEntityGroupAuthoring))]
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringComponent)]
    public class GhostAuthoringComponent : MonoBehaviour
    {
#if UNITY_EDITOR
        void OnValidate()
        {
            if (gameObject.scene.IsValid())
                return;
            var path = UnityEditor.AssetDatabase.GetAssetPath(gameObject);
            if (string.IsNullOrEmpty(path))
                return;
            var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
            if (!string.Equals(guid, prefabId, StringComparison.OrdinalIgnoreCase))
            {
                prefabId = guid;
            }
        }
#endif

        /// <summary>
        /// Force the ghost baker to treat this GameObject as if it was a prefab. This is used if you want to programmatically create
        /// a ghost prefab as a GameObject and convert it to an Entity prefab with ConvertGameObjectHierarchy.
        /// </summary>
        [NonSerialized] public bool ForcePrefabConversion;

        /// <summary>
        /// The ghost mode used if you do not manually change it using a GhostSpawnClassificationSystem.
        /// If set to OwnerPredicted the ghost will be predicted on the client which owns it and interpolated elsewhere.
        /// You must not change the mode using a classification system if using owner predicted.
        /// </summary>
        [Tooltip("The ghost mode used if you do not manually change it using a GhostSpawnClassificationSystem. If set to OwnerPredicted the ghost will be predicted on hte client which owns it and interpolated elsewhere. You must not change the mode using a classification system if using owner predicted.")]
        public GhostMode DefaultGhostMode = GhostMode.Interpolated;
        /// <summary>
        /// The ghost modes supported by this ghost. This will perform some more optimizations at authoring time but make it impossible to change ghost mode at runtime.
        /// </summary>
        [Tooltip("The ghost modes supported by this ghost. Setting to anything other than All will allow NetCode to perform some more optimizations at authoring time. However, it makes it impossible to change ghost mode at runtime.")]
        public GhostModeMask SupportedGhostModes = GhostModeMask.All;
        /// <summary>
        /// This setting is only for optimization, the ghost will be sent when modified regardless of this setting.
        /// Optimizing for static makes snapshots slightly larger when they change, but smaller when they do not change.
        /// </summary>
        [Tooltip("Optimization: Marking as `Static` makes snapshots slightly larger when GhostField values change, but smaller when they do not change.\n\n<b>Note: This is just an optimization. I.e. Changes to GhostFields will always be replicated (it's just a question of how).</b>")]
        public GhostOptimizationMode OptimizationMode = GhostOptimizationMode.Dynamic;
        /// <summary>
        /// If not all ghosts can fit in a snapshot only the most important ghosts will be sent. Higher importance means the ghost is more likely to be sent.
        /// </summary>
        [Tooltip("If not all ghosts can fit in a snapshot, only the most important ghosts will be sent. Higher importance means the ghost is more likely to be sent.")]
        public int Importance = 1;
        /// <summary>
        /// For internal use only, the prefab GUID used to distinguish between different variant of the same prefab.
        /// </summary>
        [SerializeField]internal string prefabId = "";
        /// <summary>
        /// Add a GhostOwnerComponent tracking which connection owns this component.
        /// You must set the GhostOwnerComponent to a valid NetworkIdComponent.Value at runtime.
        /// </summary>
        [Tooltip("Automatically adds a GhostOwnerComponent, which allows the server to set (and track) which connection owns this ghost. In your server code, you must set the GhostOwnerComponent to a valid NetworkIdComponent.Value at runtime.")]
        public bool HasOwner;
        /// <summary>
        /// Automatically send all ICommandData buffers if the ghost is owned by the current connection,
        /// AutoCommandTarget.Enabled is true and the ghost is predicted.
        /// </summary>
        [Tooltip("Automatically sends all ICommandData buffers when the following conditions are met: \n\n - The ghost is owned by the current connection.\n\n - AutoCommandTarget is added, and Enabled is true.\n\n - The ghost is predicted.")]
        public bool SupportAutoCommandTarget = true;
        /// <summary>
        /// Add a CommandDataInterpolationDelay component so the interpolation delay of each client is tracked.
        /// This is used for server side lag-compensation.
        /// </summary>
        [Tooltip("Add a CommandDataInterpolationDelay component so the interpolation delay of each client is tracked. This is used for server side lag-compensation.")]
        public bool TrackInterpolationDelay;
        /// <summary>
        /// Add a GhostGroup component which makes it possible for this entity to be the root of a ghost group.
        /// </summary>
        [Tooltip("Add a GhostGroup component which makes it possible for this entity to be the root of a ghost group.")]
        public bool GhostGroup;
        /// <summary>
        /// Force this ghost to be quantized and copied to the snapshot format once for all connections instead
        /// of once per connection. This can save CPU time in the ghost send system if the ghost is
        /// almost always sent to at least one connection, and it contains many serialized components, serialized
        /// components on child entities or serialized buffers. A common case where this can be useful is the ghost
        /// for the character / player.
        /// </summary>
        [Tooltip("Force this ghost to be quantized and copied to the snapshot format once for all connections instead of once per connection. This can save CPU time in the ghost send system if the ghost is almost always sent to at least one connection, and it contains many serialized components, serialized components on child entities, or serialized buffers. A common case where this can be useful is the ghost for the character / player.")]
        public bool UsePreSerialization;
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
