using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    ///     Add this to your Scene (on a root GameObject only!) to replace the automatic bootstrapping setting specified in
    ///     your <see cref="NetcodeConfig" /> ProjectSettings asset. Note: Netcode will only search the Active scene for
    ///     this MonoBehaviour, and only during bootstrap (which occurs only on game boot, before the first MonoBehaviour Awake).
    /// </summary>
    /// <remarks>
    ///     Our <see cref="Unity.Entities.ICustomBootstrap" /> (<see cref="ClientServerBootstrap" />) will use the first
    ///     one it finds in any scenes. 2 (or more) will err.
    ///     Also note: This will not work if you use your own bootstrapper, unless you call
    ///     <see cref="ClientServerBootstrap.DetermineIfBootstrappingEnabled" /> early, and return false if false.
    /// </remarks>
    [AddComponentMenu("Multiplayer/Override Automatic Netcode Bootstrap", 3)]
    [MovedFrom(true, "Unity.NetCode")]
    public sealed class OverrideAutomaticNetcodeBootstrap : MonoBehaviour, IComparable<OverrideAutomaticNetcodeBootstrap>
    {
        /// <inheritdoc cref="NetcodeConfig.AutomaticBootstrapSetting" />
        [Tooltip("Note: This will only replace the bootstrap for this one scene, and only if this scene is the Active scene when entering playmode, or the first scene in the build.")]
        public NetcodeConfig.AutomaticBootstrapSetting ForceAutomaticBootstrapInScene = NetcodeConfig.AutomaticBootstrapSetting.EnableAutomaticBootstrap;

        private void OnValidate()
        {
            if(transform.root != transform)
                Debug.LogError($"OverrideAutomaticNetcodeBootstrap can only be added to the root GameObject! '{this}' is invalid, and should be moved or deleted!", this);
        }

        /// <summary>
        /// Tries to ensure deterministic-ish sort ordering, for reliable bootstrapping behaviour.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        int IComparable<OverrideAutomaticNetcodeBootstrap>.CompareTo(OverrideAutomaticNetcodeBootstrap other)
        {
            if (ReferenceEquals(this, other)) return 0;
            if (ReferenceEquals(null, other)) return 1;
            var nameSort = string.Compare(name, other.name, StringComparison.Ordinal);
            if (nameSort != 0) return nameSort;
            return GetEntityId().CompareTo(other.GetEntityId());
        }
    }
}
