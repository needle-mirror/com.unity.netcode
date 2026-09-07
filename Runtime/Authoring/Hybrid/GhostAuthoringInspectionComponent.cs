using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Assemblies;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    /// <para>MonoBehaviour you may optionally add to any/all GameObjects in a Ghost Prefab, which allows inspecting of (and saving of) "Ghost Meta Data". E.g.</para>
    /// <para> - Override/Tweak some of the component replication properties, for both child and root entities.</para>
    /// <para> - Assign to each component which <see cref="GhostComponentVariationAttribute">variant</see> to use.</para>
    /// </summary>
    /// <seealso cref="GhostAuthoringComponent"/>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostAuthoringInspetionComponent)]
    [AddComponentMenu("Multiplayer/Ghost Authoring Inspection Component", 2)]
    [MovedFrom(true, "Unity.NetCode")]
    public class GhostAuthoringInspectionComponent : MonoBehaviour, IPrefabOverrideProvider
    {
        // TODO: This doesn't support multi-edit.
        internal static bool forceBake;
        internal static bool forceRebuildInspector = true;
        internal static bool forceSave;

        /// <summary>
        /// List of all saved modifications that the user has applied to this entity.
        /// If not set, defaults to whatever Attribute values the user has setup on each <see cref="GhostInstance"/>.
        /// </summary>
        [FormerlySerializedAs("m_ComponentOverrides")]
        [SerializeField]
        internal ComponentOverride[] ComponentOverrides = Array.Empty<ComponentOverride>();

        /// <summary>Read-only view of the saved overrides. Mutate via <see cref="GetOrAddPrefabOverride"/> +
        /// <see cref="SavePrefabOverride"/> or <see cref="RemoveComponentOverrideByIndex"/> so the editor's
        /// dirty/save signal fires correctly.</summary>
        /// <returns>A read-only view backed directly by the underlying override array; iteration order is the
        /// array's storage order.</returns>
        public IReadOnlyList<ComponentOverride> EnumerateOverrides() => ComponentOverrides;

        /// <summary>Number of saved overrides on this inspection component.</summary>
        public int OverrideCount => ComponentOverrides.Length;

        ///<summary>Not the fastest way but on average is taking something like 10-50us or less to find the type,
        ///so seem reasonably fast even with tens of components per prefab.</summary>
        static Type FindTypeFromFullTypeNameInAllAssemblies(string fullName)
        {
            // TODO - Consider using the TypeManager.
            foreach (var a in CurrentAssemblies.GetLoadedAssemblies())
            {
                var type = a.GetType(fullName, false);
                if (type != null)
                    return type;
            }
            return default;
        }

        [ContextMenu("Force Re-Bake Prefab")]
        void ForceBake()
        {
            forceBake = true;
            forceRebuildInspector = true;
        }

        /// <summary>Heals an override serialized before the Unity.NetCode rename in place. True if anything was
        /// rewritten. <paramref name="compType"/> is the resolved component type (null when unknown), so callers
        /// don't pay for a second assembly scan.</summary>
        static bool TryMigrateLegacyOverride(ref ComponentOverride mod, out Type compType)
        {
            // Important note: this healing is done only when the prefab is dirtied or when baking. Which means if users don't touch their prefabs for a while, this healing code will still be needed for a while as well. This healing code shouldn't be removed unless we have a way to guarantee users have healed all their prefabs.
            var migrated = false;
            compType = FindTypeFromFullTypeNameInAllAssemblies(mod.FullTypeName);
            if (compType == null)
            {
                var currentName = GhostVariantsUtility.ToCurrentFullName(mod.FullTypeName);
                if (!string.Equals(currentName, mod.FullTypeName, StringComparison.Ordinal))
                {
                    compType = FindTypeFromFullTypeNameInAllAssemblies(currentName);
                    if (compType != null)
                    {
                        mod.FullTypeName = currentName;
                        migrated = true;
                    }
                }
            }
            if (compType != null && mod.IsVariantOverriden
                && GhostVariantsUtility.TryMigrateLegacyVariantHash(mod.VariantHash, compType, out var currentHash))
            {
                mod.VariantHash = currentHash;
                migrated = true;
            }
            return migrated;
        }

        /// <summary>Logs a Unity error for each entry in <see cref="EnumerateOverrides"/> whose
        /// <see cref="ComponentOverride.FullTypeName"/> does not resolve to a loaded type.
        /// Overrides serialized before the Unity.NetCode rename are migrated in place (and re-saved) first.</summary>
        internal void LogErrorIfComponentOverrideIsInvalid()
        {
            for (var i = 0; i < ComponentOverrides.Length; i++)
            {
                ref var mod = ref ComponentOverrides[i];
                if (TryMigrateLegacyOverride(ref mod, out var compType))
                {
                    Debug.Log($"Ghost Prefab '{name}': migrated pre-rename 'Component Override' to '{mod}'.", this);
                    forceSave = true;
                    forceBake = true;
                }
                if (compType == null)
                {
                    Debug.LogError($"Ghost Prefab '{name}' has an invalid 'Component Override' targeting an unknown component type '{mod.FullTypeName}'. " +
                                   "If this type has been renamed, you will unfortunately need to manually re-add this override. If it has been deleted, simply re-commit this prefab.");
                    RemoveComponentOverrideByIndex(i);
                    forceSave = true;
                    forceBake = true;
                    i--;
                }
            }
        }

        /// <summary>Returns the existing <see cref="ComponentOverride"/> for (<paramref name="managedType"/>,
        /// <paramref name="entityGuid"/>) on this inspection component, or appends a new entry initialized with
        /// <paramref name="defaultPrefabType"/> and returns that.</summary>
        /// <remarks>
        /// <para>Returns by ref into the underlying serialized array. <b>The ref is invalidated</b> by any subsequent
        /// call to <see cref="GetOrAddPrefabOverride"/> that adds a NEW entry, or by
        /// <see cref="RemoveComponentOverrideByIndex"/> — both can resize the backing array. Don't hold the ref
        /// across those calls.</para>
        /// <para>This operation is NOT persisted. Call <see cref="SavePrefabOverride"/> after mutating the returned
        /// ref to flag the inspection component as dirty.</para>
        /// </remarks>
        /// <param name="managedType">The component type to override. Matched (case-insensitive) against
        /// <see cref="ComponentOverride.FullTypeName"/>.</param>
        /// <param name="entityGuid">The entity within the prefab hierarchy this override targets. Must match either
        /// this GameObject's entity id or one of its descendants', else throws.</param>
        /// <param name="defaultPrefabType">Initial <see cref="ComponentOverride.PrefabType"/> when a new entry is
        /// appended. Ignored when an existing entry is returned.</param>
        /// <returns>A ref to the existing or newly-appended override entry. Caller must call
        /// <see cref="SavePrefabOverride"/> after mutating it.</returns>
        public ref ComponentOverride GetOrAddPrefabOverride(Type managedType, EntityGuid entityGuid, GhostPrefabType defaultPrefabType)
        {
            if (!gameObject || !this)
                throw new ArgumentException($"Attempting to GetOrAddPrefabOverride for entityGuid '{entityGuid}' to '{this}', but GameObject and/or InspectionComponent has been destroyed!");

            if (gameObject.GetEntityId() != entityGuid.OriginatingEntityId && !TryGetFirstMatchingGameObjectInChildren(gameObject.transform, entityGuid, out _))
            {
                throw new ArgumentException($"Attempting to GetOrAddPrefabOverride for entityGuid '{entityGuid}' to '{this}', but entityGuid does not match our gameObject, nor our children!");
            }

            if (TryFindExistingOverrideIndex(managedType, entityGuid, out var index))
            {
                return ref ComponentOverrides[index];
            }

            // Did not find, so add:
            ref var found = ref AddComponentOverrideRaw();
            found = new ComponentOverride
            {
                EntityIndex = entityGuid.Serial,
                FullTypeName = managedType.FullName,
            };
            found.Reset();
            found.PrefabType = defaultPrefabType;
            return ref found;
        }

        internal ref ComponentOverride AddComponentOverrideRaw()
        {
            Array.Resize(ref ComponentOverrides, ComponentOverrides.Length + 1);
            return ref ComponentOverrides[ComponentOverrides.Length - 1];
        }

        /// <summary>Persists changes made to <paramref name="componentOverride"/> (by-ref). If the override no
        /// longer carries any overridden fields (<see cref="ComponentOverride.HasOverriden"/> is false), removes
        /// it from the array entirely.</summary>
        /// <param name="componentOverride">The override entry being saved, passed by ref so that auto-removal
        /// (when no fields are overridden) can resolve its index.</param>
        /// <param name="reason">Free-text reason for the save, useful when debugging editor undo/save flow. Not persisted.</param>
        /// <remarks>At runtime this only flags the editor signal that drives a re-save; outside the editor the
        /// flag is harmless.</remarks>
        public void SavePrefabOverride(ref ComponentOverride componentOverride, string reason)
        {
            forceSave = true;

            // Remove the override entirely if its no longer overriding anything.
            if (!componentOverride.HasOverriden)
            {
                var index = FindExistingOverrideIndex(ref componentOverride);
                RemoveComponentOverrideByIndex(index);
            }
        }

        /// <summary>Removes the override at <paramref name="index"/> by swapping in the last element and resizing.
        /// Order of remaining entries is not preserved.</summary>
        /// <param name="index">Position in the override array to remove. Out-of-range indices are silently ignored
        /// when the array is empty; otherwise behaviour matches a normal indexed write to the array.</param>
        /// <remarks>Invalidates any <c>ref</c> previously obtained from <see cref="GetOrAddPrefabOverride"/>.</remarks>
        public void RemoveComponentOverrideByIndex(int index)
        {
            if (ComponentOverrides.Length == 0) return;
            if (index < ComponentOverrides.Length - 1)
            {
                ComponentOverrides[index] = ComponentOverrides[ComponentOverrides.Length - 1];
            }
            Array.Resize(ref ComponentOverrides, ComponentOverrides.Length - 1);
        }

        /// <summary>Finds the array index of <paramref name="currentOverride"/> by matching
        /// <see cref="ComponentOverride.FullTypeName"/> (case-insensitive). Throws if not found.</summary>
        /// <param name="currentOverride">The override entry to locate. Passed by ref so callers holding a ref into
        /// the underlying array can ask "where am I?" without copying.</param>
        /// <returns>The position of <paramref name="currentOverride"/> in the underlying array.</returns>
        /// <remarks>Intended for callers holding a <c>ref</c> obtained from <see cref="GetOrAddPrefabOverride"/> —
        /// the entry MUST exist by construction. For non-ref lookups, use <see cref="TryFindExistingOverrideIndex(Type, in EntityGuid, out int)"/>.</remarks>
        public int FindExistingOverrideIndex(ref ComponentOverride currentOverride)
        {
            for (int i = 0; i < ComponentOverrides.Length; i++)
            {
                if (string.Equals(ComponentOverrides[i].FullTypeName, currentOverride.FullTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            throw new InvalidOperationException("Unable to find index of override, which should be impossible as we're passing currentOverride by ref!");
        }

        /// <summary>Does a depth first search to find an element in the transform hierarchy matching this EntityGuid.</summary>
        /// <param name="current">Root element to search from.</param>
        /// <param name="entityGuid">Query: First to match with this EntityGuid.</param>
        /// <param name="foundGameObject">First element matching the query. Will be set to null otherwise.</param>
        /// <returns>True if found.</returns>
        static bool TryGetFirstMatchingGameObjectInChildren(Transform current, EntityGuid entityGuid, out GameObject foundGameObject)
        {
            if (current.gameObject.GetEntityId() == entityGuid.OriginatingEntityId)
            {
                foundGameObject = current.gameObject;
                return true;
            }

            if (current.childCount == 0)
            {
                foundGameObject = null;
                return false;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (TryGetFirstMatchingGameObjectInChildren(child, entityGuid, out foundGameObject))
                {
                    return true;
                }
            }
            foundGameObject = null;
            return false;
        }

        /// <summary>Finds all <see cref="GhostAuthoringInspectionComponent"/>'s on this Ghost Authoring Prefab
        /// (including in children) and flattens their overrides into one list, paired with the GameObject each
        /// override was authored on.</summary>
        /// <param name="ghostAuthoring">Root prefab to search from.</param>
        /// <param name="validate">If true, calls <see cref="LogErrorIfComponentOverrideIsInvalid"/> on each
        /// inspection component visited.</param>
        /// <returns>A flat list of every override across the prefab hierarchy, each paired with the GameObject
        /// that hosts the inspection component the override was authored on.</returns>
        public static List<(GameObject, ComponentOverride)> CollectAllComponentOverridesInInspectionComponents(BaseGhostSettings ghostAuthoring, bool validate)
        {
            var inspectionComponents = CollectAllInspectionComponents(ghostAuthoring);
            var allComponentOverrides = new List<(GameObject, ComponentOverride)>(inspectionComponents.Count * 4);
            foreach (var inspectionComponent in inspectionComponents)
            {
                if(validate)
                    inspectionComponent.LogErrorIfComponentOverrideIsInvalid();

                foreach (var componentOverride in inspectionComponent.ComponentOverrides)
                {
                    allComponentOverrides.Add((inspectionComponent.gameObject, componentOverride));
                }
            }

            return allComponentOverrides;
        }

        /// <summary>Returns every <see cref="GhostAuthoringInspectionComponent"/> attached to
        /// <paramref name="ghostAuthoring"/>'s GameObject or any descendant.</summary>
        /// <param name="ghostAuthoring">The ghost authoring whose GameObject hierarchy is searched.</param>
        /// <returns>A list containing the inspection components on the root GameObject followed by those on every
        /// descendant. Empty if none are present.</returns>
        public static List<GhostAuthoringInspectionComponent> CollectAllInspectionComponents(BaseGhostSettings ghostAuthoring)
        {
            var inspectionComponents = new List<GhostAuthoringInspectionComponent>(8);
            ghostAuthoring.gameObject.GetComponents(inspectionComponents);
            ghostAuthoring.GetComponentsInChildren(inspectionComponents);
            return inspectionComponents;
        }

        /// <summary>Saved override values for a single (entity, component) pair on a Ghost Prefab.</summary>
        [Serializable]
        public struct ComponentOverride : IComparer<ComponentOverride>, IComparable<ComponentOverride>
        {
            /// <summary>Sentinel for an unset <see cref="PrefabType"/> or <see cref="SendTypeOptimization"/>: -1 (cast to the relevant enum).</summary>
            public const int NoOverride = -1;

            ///<summary>
            /// For sake of serialization we are using the type fullname because we can't rely on the TypeIndex for the component.
            /// StableTypeHash cannot be used either because layout or fields changes affect the hash too (so is not a good candidate for that).
            /// </summary>
            public string FullTypeName;

            ///<summary>The entity guid index reference.</summary>
            [FormerlySerializedAs("EntityGuid")] public ulong EntityIndex;

            ///<summary>Override what modes are available for that type. If `None`, this component is removed from the prefab/entity instance.</summary>
            /// <remarks>Note that <see cref="VariantHash"/> can clobber this value.</remarks>
            public GhostPrefabType PrefabType;

            ///<summary>Override which client type it will be sent to, if we're able to determine.</summary>
            [FormerlySerializedAs("OwnerPredictedSendType")]
            public GhostSendType SendTypeOptimization;

            ///<summary>Select which variant we would like to use. 0 means the default. Compute via
            /// <see cref="GhostVariantsUtility.ResolveVariantHashFromType"/> (which honors well-known special variants
            /// like <see cref="DontSerializeVariant"/>), or <see cref="GhostVariantsUtility.UncheckedVariantHashNBC(Type, ComponentType)"/>
            /// when you already know the variant is a user-defined struct.</summary>
            public ulong VariantHash;

            /// <summary>Editor-only flag set during inspection-component validation to mark that this entry mapped
            /// to a known component type. Not serialized, not meaningful at runtime.</summary>
            public bool DidCorrectlyMap { get; internal set; }

            /// <summary>True if any of <see cref="PrefabType"/>, <see cref="SendTypeOptimization"/>, or <see cref="VariantHash"/> is set.</summary>
            public bool HasOverriden => IsPrefabTypeOverriden || IsSendTypeOptimizationOverriden || IsVariantOverriden;

            /// <summary>True if <see cref="PrefabType"/> is set (not equal to <see cref="NoOverride"/>).</summary>
            public bool IsPrefabTypeOverriden => (int)PrefabType != NoOverride;

            /// <summary>True if <see cref="SendTypeOptimization"/> is set (not equal to <see cref="NoOverride"/>).</summary>
            public bool IsSendTypeOptimizationOverriden => (int)SendTypeOptimization != NoOverride;

            /// <summary>True if <see cref="VariantHash"/> is non-zero.</summary>
            public bool IsVariantOverriden => VariantHash != 0;

            /// <summary>Resets <see cref="PrefabType"/>, <see cref="SendTypeOptimization"/>, and <see cref="VariantHash"/>
            /// back to "no override". Does not touch <see cref="FullTypeName"/> or <see cref="EntityIndex"/>.</summary>
            public void Reset()
            {
                PrefabType = (GhostPrefabType)NoOverride;
                SendTypeOptimization = (GhostSendType)NoOverride;
                VariantHash = 0;
            }

            /// <inheritdoc/>
            public override string ToString() => $"ComponentOverride['{FullTypeName}', EntityIndex:'{EntityIndex}', prefabType:{PrefabType}, sto:{SendTypeOptimization}, variantH:{VariantHash}]";

            /// <summary>Sort order: by <see cref="FullTypeName"/>, then <see cref="EntityIndex"/>, then <see cref="VariantHash"/>.</summary>
            /// <param name="x">First override to compare.</param>
            /// <param name="y">Second override to compare.</param>
            /// <returns>Negative if <paramref name="x"/> precedes <paramref name="y"/>, positive if it follows, zero
            /// if all three sort keys are equal.</returns>
            public int Compare(ComponentOverride x, ComponentOverride y)
            {
                var fullTypeNameComparison = string.Compare(x.FullTypeName, y.FullTypeName, StringComparison.Ordinal);
                if (fullTypeNameComparison != 0) return fullTypeNameComparison;
                var entityGuidComparison = x.EntityIndex.CompareTo(y.EntityIndex);
                return entityGuidComparison != 0 ? entityGuidComparison : x.VariantHash.CompareTo(y.VariantHash);
            }

            /// <inheritdoc/>
            public int CompareTo(ComponentOverride other)
            {
                return Compare(this, other);
            }
        }

        /// <summary>Looks up the index of the override targeting <paramref name="managedType"/> on the entity
        /// identified by <paramref name="guid"/>, if one exists.</summary>
        /// <param name="managedType">Component type to match. <see cref="Type.FullName"/> is compared case-insensitively
        /// against <see cref="ComponentOverride.FullTypeName"/>.</param>
        /// <param name="guid">Entity identifier within the prefab; only <see cref="EntityGuid.Serial"/> is used.</param>
        /// <param name="index">Set to the matching index when this method returns true; -1 otherwise.</param>
        /// <returns>True if found; <paramref name="index"/> is the position in the override list, else -1.</returns>
        public bool TryFindExistingOverrideIndex(Type managedType, in EntityGuid guid, out int index)
        {
            var managedTypeFullName = managedType.FullName;
            return TryFindExistingOverrideIndex(managedTypeFullName, guid.Serial, out index);
        }

        /// <summary>Looks up the index of the override matching <paramref name="managedTypeFullName"/> +
        /// <paramref name="entityGuid"/>, if one exists. The string overload exists so callers without a runtime
        /// <see cref="Type"/> handle (e.g. tooling reading serialized prefab data) can still query.</summary>
        /// <param name="managedTypeFullName">Component type's <see cref="Type.FullName"/>; compared case-insensitively
        /// against <see cref="ComponentOverride.FullTypeName"/>.</param>
        /// <param name="entityGuid">The entity serial to match (mirrors <see cref="EntityGuid.Serial"/>).</param>
        /// <param name="index">Set to the matching index when this method returns true; -1 otherwise.</param>
        /// <returns>True if found; <paramref name="index"/> is the position in the override list, else -1.</returns>
        public bool TryFindExistingOverrideIndex(string managedTypeFullName, in ulong entityGuid, out int index)
        {
            for (index = 0; index < ComponentOverrides.Length; index++)
            {
                ref var componentOverride = ref ComponentOverrides[index];
                if (componentOverride.EntityIndex == entityGuid && string.Equals(componentOverride.FullTypeName, managedTypeFullName, StringComparison.OrdinalIgnoreCase))
                {
                    componentOverride.DidCorrectlyMap = true;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride> IPrefabOverrideProvider.GetPrefabOverrides()
        {
            var go = this.gameObject;
            NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride> toReturn = new NativeParallelHashMap<GhostPrefabCreation.Component, GhostPrefabCreation.ComponentOverride>(1, Allocator.Temp);

            var ghostAuthoringInspectionComponent = go.GetComponent<GhostAuthoringInspectionComponent>();
            if (ghostAuthoringInspectionComponent == null)
                return default;

            var savedOverrides = ghostAuthoringInspectionComponent.ComponentOverrides;

            GhostObject ghost = go.GetComponent<GhostObject>();

            using NativeList<ComponentType> allComponents = new(Allocator.Temp);
            foreach (var componentType in ghost.GetComponentTypes())
            {
                allComponents.Add(componentType);
            }

            allComponents.AddRange(ghost.GetDefaultAttachedComponents());

            for (int compIdx = 0; compIdx < allComponents.Count; compIdx++)
            {
                // Find the override
                ComponentOverride foundOverride = default;
                foundOverride.Reset();
                var currentComp = allComponents[compIdx];
                foreach (var overrideEntry in savedOverrides)
                {
                    if (TypeManager.GetFullNameHash(currentComp.TypeIndex) == TypeManager.CalculateFullNameHash(GhostVariantsUtility.ToCurrentFullName(overrideEntry.FullTypeName)))
                    {
                        foundOverride = overrideEntry;
                        break;
                    }
                }

                if (foundOverride.HasOverriden)
                {
                    // Data saved before the Unity.NetCode rename may reach players without the editor heal
                    // having run; TypeCache is unavailable here, so only the default-serializer case remaps.
                    if (foundOverride.IsVariantOverriden
                        && GhostVariantsUtility.TryMigrateLegacyVariantHash(foundOverride.VariantHash, currentComp.GetManagedType(), out var migratedHash))
                        foundOverride.VariantHash = migratedHash;
                    GhostPrefabCreation.ComponentOverrideType overrideType = default;
                    if (foundOverride.IsVariantOverriden) overrideType |= GhostPrefabCreation.ComponentOverrideType.Variant;
                    if (foundOverride.IsPrefabTypeOverriden) overrideType |= GhostPrefabCreation.ComponentOverrideType.PrefabType;
                    if (foundOverride.IsSendTypeOptimizationOverriden) overrideType |= GhostPrefabCreation.ComponentOverrideType.SendMask;
                    toReturn[new GhostPrefabCreation.Component()
                    {
                        ComponentType = currentComp, ChildIndex = 0, // TODO handle children, right now this is only for root ghost
                    }] = new GhostPrefabCreation.ComponentOverride()
                    {
                        Variant = foundOverride.VariantHash, OverrideType = overrideType, SendMask = foundOverride.SendTypeOptimization, PrefabType = foundOverride.PrefabType,
                    };
                }
            }

            return toReturn;
        }
    }
}
