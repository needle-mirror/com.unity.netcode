using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Unity.Entities.Hybrid.Baking;
using Unity.NetCode.Hybrid;
using UnityEngine.Serialization;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>The GhostAuthoringComponent is the main entry point to configure and create replicated ghosts types.
    /// The component must be added only to the GameObject hierarchy root.</para>
    /// <para>It allows setting all ghost properties,
    /// such as the replication mode <see cref="SupportedGhostModes"/>, bandwidth optimization strategy (<see cref="OptimizationMode"/>,
    /// the ghost <see cref="Importance"/> (how frequently is sent) and others).</para>
    /// </summary>
    /// <seealso cref="GhostAuthoringInspectionComponent"/>
    [RequireComponent(typeof(LinkedEntityGroupAuthoring))]
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringComponent)]
    public class GhostAuthoringComponent : MonoBehaviour
    {
#if UNITY_EDITOR
    void OnValidate()
    {
        string assetPath = null;
        if (UnityEditor.EditorUtility.IsPersistent(gameObject))  // faster way to check if part of an asset than gameObject.scene.IsValid()
        {
            assetPath = UnityEditor.AssetDatabase.GetAssetPath(gameObject);
        }
        else
        {
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject);
            if (prefabStage != null)
                assetPath = prefabStage.assetPath;
        }

        if (!string.IsNullOrEmpty(assetPath))
        {
            var guid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
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
        [Tooltip("The `GhostMode` used when first spawned (assuming you do not manually change it, using a GhostSpawnClassificationSystem).\n\nIf set to 'Owner Predicted', the ghost will be 'Predicted' on the client which owns it, and 'Interpolated' on all others. If using 'Owner Predicted', you cannot change the ghost mode via a classification system.")]
        public GhostMode DefaultGhostMode = GhostMode.Interpolated;
        /// <summary>
        /// The ghost modes supported by this ghost. This will perform some more optimizations at authoring time but make it impossible to change ghost mode at runtime.
        /// </summary>
        [Tooltip("Every `GhostMode` supported by this ghost. Setting this to either 'Predicted' or 'Interpolated' will allow NetCode to perform some more optimizations at authoring time. However, it makes it impossible to change ghost mode at runtime.")]
        public GhostModeMask SupportedGhostModes = GhostModeMask.All;
        /// <summary>
        /// This setting is only for optimization, the ghost will be sent when modified regardless of this setting.
        /// Optimizing for static makes snapshots slightly larger when they change, but smaller when they do not change.
        /// </summary>
        [Tooltip("Bandwidth and CPU optimization:\n\n - <b>Static</b> - This ghost will only be added to a snapshot when its ghost values actually change.\n<i>Examples: Barrels, trees, dropped items, asteroids etc.</i>\n\n - <b>Dynamic</b> - This ghost will be replicated at a regular interval, regardless of whether or not its values have changed, allowing for more aggressive compression.\n<i>Examples: Character controllers, missiles, important gameplay items like CTF flags and footballs etc.</i>\n\n<i>Marking a ghost as `Static` makes snapshots slightly larger when replicated values change, but smaller when they do not.</i>")]
        public GhostOptimizationMode OptimizationMode = GhostOptimizationMode.Dynamic;
        /// <summary>
        /// If not all ghosts can fit in a snapshot only the most important ghosts will be sent. Higher importance means the ghost is more likely to be sent.
        /// </summary>
        [Tooltip(@"<b>Importance</b> determines how ghost chunks are prioritized against each other when working out what to send in the upcoming snapshot. Higher values are sent more frequently. Applied at the chunk level.
<i>Simplified example: When comparing a gameplay-critical <b>Player</b> ghost with an <b>Importance</b> of 100 to a cosmetic <b>Cone</b> ghost with an <b>Importance</b> of 1, the <b>Player</b> ghost will likely be sent 100 times for every 1 time the <b>Cone</b> will be.</i>")]
        [Min(1)]
        public int Importance = 1;

        /// <summary>
        ///     The theoretical maximum send frequency (in Hz) for ghost chunks of this ghost prefab type (excluding a few nuanced exceptions).
        ///     Important Note: The MaxSendRate only denotes the maximum possible replication frequency, and cannot be enforced in all cases.
        ///     Other factors (like <see cref="ClientServerTickRate.NetworkTickRate"/>, ghost instance count, <see cref="Importance"/>,
        ///     Importance-Scaling, <see cref="GhostSendSystemData.DefaultSnapshotPacketSize"/>, and structural changes etc.)
        ///     will determine the final/live send rate.
        /// </summary>
        /// <remarks>
        /// Use this to brute-force reduce the bandwidth consumption of your most impactful ghost types.
        /// Note: Predicted ghosts are particularly impacted by this, as a lower value here reduces rollback and re-simulation frequency
        /// (as we only rollback and re-simulate a predicted ghost after it is received), which can save client CPU cycles in aggregate.
        /// However, it may cause larger client misprediction errors, which leads to larger corrections.
        /// </remarks>
        [Tooltip(@"The <b>theoretical</b> maximum send frequency (in <b>Hertz</b>) for ghost chunks of this ghost prefab type.

<b>Important Note:</b> The <b>MaxSendRate</b> only denotes the maximum possible replication frequency. Other factors (like <b>NetworkTickRate</b>, ghost instance count, <b>Importance</b>, <b>Importance-Scaling</b>, <b>DefaultSnapshotPacketSize</b> etc.) will determine the live send rate.

<i>Use this to brute-force reduce the bandwidth consumption of your most impactful ghost types.</i>")]
        public byte MaxSendRate;

        /// <summary>
        /// For internal use only, the prefab GUID used to distinguish between different variant of the same prefab.
        /// </summary>
        [SerializeField]internal string prefabId = "";
        /// <summary>
        /// Add a GhostOwner tracking which connection owns this component.
        /// You must set the GhostOwner to a valid NetworkId.Value at runtime.
        /// </summary>
        [Tooltip("Automatically adds a `GhostOwner`, which allows the server to set (and track) which connection owns this ghost. In your server code, you must set the `GhostOwner` to a valid `NetworkId.Value` at runtime.")]
        public bool HasOwner;
        /// <summary>
        /// Automatically adds the <see cref="AutoCommandTarget"/> component to your ghost prefab,
        /// which enables the "Auto Command Target" feature, which automatically sends all `ICommandData` and `IInputComponentData`
        /// buffers to the server (assuming the ghost is owned by the current connection, and `AutoCommandTarget.Enabled` is true).
        /// </summary>
        [Tooltip("Enables the \"Auto Command Target\" feature, which automatically sends all `ICommandData` and `IInputComponentData` auto-generated buffers to the server if the following conditions are met: \n\n - The ghost is owned by the current connection (handled by user-code).\n\n - The `AutoCommandTarget` component is added to the ghost entity (enabled by this checkbox), and it's `[GhostField] public bool Enabled;` field is true (the default value).\n\nSupports both predicted and interpolated ghosts.")]
        public bool SupportAutoCommandTarget = true;
        /// <summary>
        /// Add a CommandDataInterpolationDelay component so the interpolation delay of each client is tracked.
        /// This is used for server side lag-compensation.
        /// </summary>
        [Tooltip("Add a `CommandDataInterpolationDelay` component so the interpolation delay of each client is tracked.\n\nThis is used for server side lag-compensation (it allows the server to more accurately estimate how far behind your interpolated ghosts are, leading to better hit registration, for example).\n\nThis should be enabled if you expect to use input commands (from this 'Owner Predicted' ghost) to interact with other, 'Interpolated' ghosts (example: shooting or hugging another 'Player').")]
        public bool TrackInterpolationDelay;
        /// <summary>
        /// Add a GhostGroup component which makes it possible for this entity to be the root of a ghost group.
        /// </summary>
        [Tooltip("Add a `GhostGroup` component, which makes it possible for this entity to be the root of a 'Ghost Group'.\n\nA 'Ghost Group' is a collection of ghosts who must always be replicated in the same snapshot, which is useful (for example) when trying to keep an item like a weapon in sync with the player carrying it.\n\nTo use this feature, you must add the target ghost entity to this `GhostGroup` buffer at runtime (e.g. when the weapon is first picked up by the player).\n\n<i>Note that GhostGroups slow down serialization, as they force entity chunk random-access. Therefore, prefer other solutions.</i>")]
        public bool GhostGroup;
        /// <summary>
        /// Force this ghost to be quantized and copied to the snapshot format once for all connections instead
        /// of once per connection. This can save CPU time in the ghost send system if the ghost is
        /// almost always sent to at least one connection, and it contains many serialized components, serialized
        /// components on child entities or serialized buffers. A common case where this can be useful is the ghost
        /// for the character / player.
        /// </summary>
        [Tooltip("CPU optimization that forces this ghost to be quantized and copied to the snapshot format <b>once for all connections</b> (instead of once <b>per connection</b>). This can save CPU time in the `GhostSendSystem` assuming all of the following:\n\n - The ghost contains many serialized components, serialized components on child entities, or serialized buffers.\n\n - The ghost is almost always sent to at least one connection.\n\n<i>Example use-cases: Players, important gameplay items like footballs and crowns, global entities like map settings and dynamic weather conditions.</i>")]
        public bool UsePreSerialization;
        /// <summary>
        /// CPU optimization that forces using a single baseline for delta compression for this specific prefab type.
        /// Enabling this option positively affect CPU on both client and server, especially when the archetype has a large number of components, many of which rarely change.
        /// As downside, it negatively affect the bandwidth, especially when the component/buffer data changes are highly predictable and linear (i.e moving at linear speed or incrementing a counter).
        /// But as counter-balancing factor, it allow for some bandwidth saving (and CPU saving on server) when the replicated entity has no changes for a certain amount of time, avoiding re-sending "redundant" information and ghost ids.
        /// This becomes handy and useful in scenarios when the ghost is more suited for dynamic updates than for static optimization (i.e many no-changes moments gut sparse) and/or holistically the majority of the component data changes does not follow linear patterns,
        /// as such, the three baselines cost does not justify the saving in bandwidth.
        /// </summary>
        [Tooltip("CPU optimization that forces using a single baseline for delta compression for this specific prefab type.\\nEnabling this option positively affect CPU on both client and server, especially when the archetype has a large number of components, many of which rarely change. As downside, it negatively affect the bandwidth, especially when the component/buffer data changes are highly predictable and linear (i.e moving at linear speed or incrementing a counter).\\nAs counter-balancing factor, it allow for some bandwidth saving (and CPU saving on server) when the replicated entity has no changes for a certain amount of time, avoiding re-sending \"redundant\" information and ghost ids. This becomes handy and useful in scenarios when the ghost is more suited for dynamic updates than for static optimization (i.e many no-changes moments gut sparse) and/or holistically the majority of the component data changes does not follow linear patterns, as such, the three baselines cost does not justify the saving in bandwidth.")]
        public bool UseSingleBaseline;
        /// <summary>
        /// <para>
        /// Only for client, force <i>predicted spawn ghost</i> of this type to rollback and re-predict their state from the tick client spawned them until
        /// the authoritative server spawn has been received and classified. In order to save some CPU, the ghost state is rollback only in case a
        /// new snapshot has been received, and it contains new predicted ghost data for this or other ghosts.
        /// </para>
        /// <para>
        /// By default this option is set to false, meaning that predicted spawned ghost by the client never rollback their original state and re-predict
        /// until the authoritative data is received. This behaviour is usually fine in many situation and it is cheaper in term of CPU.
        /// </para>
        /// </summary>
        [Tooltip("Only for client, force <i>predicted spawn ghost</i> of this type to rollback and re-predict their state from their spawn tick until the authoritative server spawn has been received and classified. In order to save some CPU, the ghost state is rollback only in case a new snapshot has been received, and it contains new predicted ghost data for this or other ghosts.\nBy default this option is set to false, meaning that predicted spawned ghost by the client never rollback their original state and re-predict until the authoritative data is received. This behaviour is usually fine in many situation and it is cheaper in term of CPU.")]
        public bool RollbackPredictedSpawnedGhostState;
        /// <summary>
        /// <para>
        /// Client CPU optimization, force <i>predicted ghost</i> of this type to replay and re-predict their state from the last received snapshot tick in case of a structural change
        /// or in general when an entry for the entity cannot be found in the prediction backup (see <see cref="GhostPredictionHistorySystem"/>).
        /// </para>
        /// <para>
        /// By default this option is set to true, to preserve the original 1.0 behavior. Once the optimization is turned on, removing or adding replicated components from the predicted ghost on the client may cause issue on the restored value. Please check the documentation, in particular the Prediction edge case and known issue.
        /// </para>
        /// </summary>
        [Tooltip("Client CPU optimization, force <i>predicted ghost</i> of this type to replay and re-predict their state from the last received snapshot tick in case of a structural change or in general when an entry for the entity cannot be found in the prediction backup.\nBy default this option is set to true, to preserve the original 1.0 behavior. Once the optimization is turned on, removing or adding replicated components from the predicted ghost on the client may cause some issue in regard the restored value when the component is re-added. Please check the documentation for more details, in particular the <i>Prediction edge case and known issue</i> section.")]
        public bool RollbackPredictionOnStructuralChanges = true;


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

        /// <summary>Helper.</summary>
        /// <param name="ghostName"></param>
        /// <returns></returns>
        internal GhostPrefabCreation.Config AsConfig(FixedString64Bytes ghostName)
        {
            return new GhostPrefabCreation.Config
            {
                Name = ghostName,
                Importance = Importance,
                MaxSendRate = MaxSendRate,
                SupportedGhostModes = SupportedGhostModes,
                DefaultGhostMode = DefaultGhostMode,
                // Prevent `OptimizationMode.Static` when using `GhostGroup`.
                // This logic mirrors the logic in GhostAuthoringComponentEditor.
                OptimizationMode = GhostGroup ? GhostOptimizationMode.Dynamic : OptimizationMode,
                UsePreSerialization = UsePreSerialization,
                PredictedSpawnedGhostRollbackToSpawnTick = RollbackPredictedSpawnedGhostState,
                RollbackPredictionOnStructuralChanges = RollbackPredictionOnStructuralChanges,
            };
        }
    }
}
