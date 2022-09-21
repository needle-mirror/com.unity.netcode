using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>MonoBehaviour you may optionally add to any/all GameObjects in a Ghost Prefab, which allows inspecting of (and saving of) "Ghost Meta Data". E.g.</para>
    /// <para> - Override/Tweak some of the component replication properties, for both child and root entities.</para>
    /// <para> - Assign to each component which <see cref="GhostComponentVariationAttribute">variant</see> to use.</para>
    /// <seealso cref="GhostAuthoringComponent"/>
    /// </summary>
    [DisallowMultipleComponent]
    public class GhostAuthoringInspectionComponent : MonoBehaviour
    {
        // TODO: This doesn't support multi-edit.
        internal static bool forceBake = true;
        internal static bool forceRebuildInspector = true;
        internal static bool forceSave;
        internal static bool toggleShowingUnmodifiableComponents;

        /// <summary>
        /// List of all saved modifications that the user has applied to this entity.
        /// If not set, defaults to whatever Attribute values the user has setup on each <see cref="GhostComponent"/>.
        /// </summary>
        [FormerlySerializedAs("m_ComponentOverrides")]
        [SerializeField]
        internal ComponentOverride[] ComponentOverrides = Array.Empty<ComponentOverride>();

        ///<summary>Not the fastest way but on average is taking something like 10-50us or less to find the type,
        ///so seem reasonably fast even with tens of components per prefab.</summary>
        static Type FindTypeFromFullTypeNameInAllAssemblies(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
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

        [ContextMenu("Toggle Showing Un-modifiable Components")]
        void ToggleShowingUnmodifiableComponents()
        {
            toggleShowingUnmodifiableComponents = true;
        }

        /// <summary>Removes all invalid overrides.</summary>
        void ValidateModifiers()
        {
            bool IsInvalidOverride(ref ComponentOverride mod, Transform root, out bool willRemove, out string reason)
            {
                var thisGameObject = gameObject;
                if (mod.GameObject == null)
                {
                    reason = $"ComponentOverride `{mod.FullTypeName}` GameObject '{mod.GameObject}' has been destroyed. " +
                        "Presumed merge issue. Automatically fixing, ensure you re-commit.";
                    mod.GameObject = thisGameObject;
                    willRemove = false;
                    return true;
                }

                if (mod.GameObject != thisGameObject)
                {
                    reason = $"ComponentOverride `{mod.FullTypeName}` GameObject '{mod.GameObject}' is different to '{thisGameObject}'. ComponentOverrides on this `GhostAuthoringInspectionComponent` MUST be for this GameObject. ";
                    var existingInspection = mod.GameObject.GetComponent<GhostAuthoringInspectionComponent>();
                    if (existingInspection)
                    {
                        if(existingInspection.TryFindExistingOverrideIndexViaFullName(mod.FullTypeName, in mod.EntityGuid, out var existingIndex))
                            reason += $"Unable to MOVE ComponentOverride for type `{mod.FullTypeName}` to '{mod.GameObject}' as destination already has an `GhostAuthoringInspectionComponent` with this override defined. Compare:\nExisting: {existingInspection.ComponentOverrides[existingIndex]}!\nNew: {mod}! Deleting duplicate on wrong GameObject.";
                        willRemove = true;
                        return true;
                    }

                    existingInspection = mod.GameObject.AddComponent<GhostAuthoringInspectionComponent>();
                    ref var newMod = ref existingInspection.AddComponentOverrideRaw();
                    newMod = mod;
                    reason += $"Corrected automatically by moving the mod to the correct inspection component (from {thisGameObject} to {mod.GameObject})!";
                    willRemove = true;
                    return true;
                }

                if (!mod.GameObject.transform.IsChildOf(root))
                {
                    reason = $"ComponentOverride `{mod.FullTypeName}` GameObject '{mod.GameObject}' has been " +
                        $"unparented from the root '{root}'. Presumed merge issue, removing.";
                    willRemove = true;
                    return true;
                }

                var compType = FindTypeFromFullTypeNameInAllAssemblies(mod.FullTypeName);
                if (compType == null)
                {
                    willRemove = true;
                    reason = $"ComponentOverride has unknown component type '{mod.FullTypeName}'. If this type has been renamed, you will unfortunately need to manually " +
                        "re-add this override. If it has been deleted, simply re-commit this prefab.";
                    return true;
                }

                // Cannot check variant here without it being quite expensive.
                // Thus, cannot exclude just due to lack of `SupportPrefabOverridesAttribute`.
                // However, this will be caught later.
                reason = default;
                willRemove = false;
                return false;
            }

            var parent = transform;
            for (var i = 0; i < ComponentOverrides.Length; i++)
            {
                ref var mod = ref ComponentOverrides[i];
                if (IsInvalidOverride(ref mod, parent, out var willRemove, out var reason))
                {
                    var removeInfo = string.Empty;
                    if (willRemove)
                    {
                        removeInfo = $"Removing the ComponentOverride now (index {i}) as quick-fix.";
                        RemoveIndexFromComponentOverrides(i);
                        i--;
                    }
                    Debug.LogError($"Ghost Prefab '{name}' has invalid ComponentOverride. Reason: {reason} {removeInfo}", this);
#if UNITY_EDITOR
                    forceSave = true;
                    UnityEditor.EditorUtility.SetDirty(this);
#endif
                }
            }
        }

        /// <remarks>Note that this operation is not saved. Ensure you call <see cref="SavePrefabOverride"/>.</remarks>
        internal ref ComponentOverride GetOrAddPrefabOverride(Type managedType, EntityGuid entityGuid, GhostPrefabType defaultPrefabType, out bool didAdd)
        {
            if (!gameObject || !this)
                throw new ArgumentException($"Attempting to GetOrAddPrefabOverride for entityGuid '{entityGuid}' to '{this}', but GameObject and/or InspectionComponent has been destroyed!");

            if (gameObject.GetInstanceID() != entityGuid.OriginatingId)
            {
                var didMatchChild = TryGetFirstMatchingGameObject(gameObject.transform, entityGuid, out var childGameObject);
                var error = didMatchChild ? $"It matches a child instead ({childGameObject}). Overrides MUST be added to the Inspection component of the GameObject you are modifying!" : "Unknown GameObject.";
                throw new ArgumentException($"Attempting to GetOrAddPrefabOverride for entityGuid '{entityGuid}' to '{this}', but entityGuid does not match our gameObject! {error}");
            }

            if (TryFindExistingOverrideIndex(managedType, entityGuid, out var index))
            {
                didAdd = false;
                return ref ComponentOverrides[index];
            }

            didAdd = true;
            ref var found = ref AddComponentOverrideRaw();
            found = new ComponentOverride
            {
                GameObject = gameObject,
                EntityGuid = entityGuid.b,
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

        /// <summary>Saves this component override. Attempts to remove it if it's default.</summary>
        internal void SavePrefabOverride(ref ComponentOverride componentOverride, string reason)
        {
            forceSave = true;

            // Remove the override entirely if its no longer overriding anything.
            if (!componentOverride.HasOverriden)
            {
                var index = FindExistingOverrideIndex(ref componentOverride);
                RemoveIndexFromComponentOverrides(index);
            }
        }

        void RemoveIndexFromComponentOverrides(int index)
        {
            var nextIndex = (index + 1);
            Array.Copy(ComponentOverrides, nextIndex, ComponentOverrides, index, ComponentOverrides.Length - nextIndex);
            Array.Resize(ref ComponentOverrides, ComponentOverrides.Length - 1);
        }

        int FindExistingOverrideIndex(ref ComponentOverride currentOverride)
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
        /// <param name="childGameObject">First element matching the query. Will be set to null otherwise.</param>
        /// <returns>True if found.</returns>
        static bool TryGetFirstMatchingGameObject(Transform current, EntityGuid entityGuid, out GameObject childGameObject)
        {
            if (current.gameObject.GetInstanceID() == entityGuid.OriginatingId)
            {
                childGameObject = current.gameObject;
                return true;
            }

            if (current.childCount == 0)
            {
                childGameObject = null;
                return false;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (TryGetFirstMatchingGameObject(child, entityGuid, out childGameObject))
                {
                    return true;
                }
            }
            childGameObject = null;
            return false;
        }

        /// <summary>Finds all <see cref="GhostAuthoringInspectionComponent"/>'s on this Ghost Authoring Prefab (including in children), and adds all <see cref="ComponentOverrides"/> to a single list.</summary>
        /// <param name="ghostAuthoring">Root prefab to search from.</param>
        internal static List<ComponentOverride> CollectAllComponentOverridesInInspectionComponents(GhostAuthoringComponent ghostAuthoring)
        {
            var inspectionComponents = new List<GhostAuthoringInspectionComponent>(8);
            ghostAuthoring.gameObject.GetComponents(inspectionComponents);
            ghostAuthoring.GetComponentsInChildren(inspectionComponents);
            var allComponentOverrides = new List<ComponentOverride>(inspectionComponents.Count * 4);
            foreach (var inspectionComponent in inspectionComponents)
            {
                inspectionComponent.ValidateModifiers();
                allComponentOverrides.AddRange(inspectionComponent.ComponentOverrides);
            }

            return allComponentOverrides;
        }

        /// <summary>Saved override values.</summary>
        [Serializable]
        internal struct ComponentOverride : IComparer<ComponentOverride>, IComparable<ComponentOverride>
        {
            public const int NoOverride = -1;

            ///<summary>
            /// For sake of serialization we are using the type fullname because we can't rely on the TypeIndex for the component.
            /// StableTypeHash cannot be used either because layout or fields changes affect the hash too (so is not a good candidate for that).
            /// </summary>
            public string FullTypeName;

            ///<summary>The GameObject Reference (root or child).</summary>
            public GameObject GameObject;

            ///<summary>The entity guid reference.</summary>
            [NonSerialized] public ulong EntityGuid;

            ///<summary>Override what modes are available for that type. If `None`, this component is removed from the prefab/entity instance.</summary>
            /// <remarks>Note that <see cref="VariantHash"/> can clobber this value.</remarks>
            public GhostPrefabType PrefabType;

            ///<summary>Override which client type it will be sent to, if we're able to determine.</summary>
            [FormerlySerializedAs("OwnerPredictedSendType")]
            public GhostSendType SendTypeOptimization;

            ///<summary>Select which variant we would like to use. 0 means the default.</summary>
            public ulong VariantHash;

            public bool HasOverriden => IsPrefabTypeOverriden || IsSendTypeOptimizationOverriden || IsVariantOverriden;

            public bool IsPrefabTypeOverriden => (int)PrefabType != NoOverride;

            public bool IsSendTypeOptimizationOverriden => (int)SendTypeOptimization != NoOverride;

            public bool IsVariantOverriden => VariantHash != 0;

            public void Reset()
            {
                PrefabType = (GhostPrefabType)NoOverride;
                SendTypeOptimization = (GhostSendType)NoOverride;
                VariantHash = 0;
            }

            public override string ToString()
            {
                return $"ComponentOverride['{FullTypeName}', go:'{GameObject}', entityGuid:'{EntityGuid}', prefabType:{PrefabType}, sto:{SendTypeOptimization}, variantH:{VariantHash}]";
            }

            public int Compare(ComponentOverride x, ComponentOverride y)
            {
                var fullTypeNameComparison = string.Compare(x.FullTypeName, y.FullTypeName, StringComparison.Ordinal);
                if (fullTypeNameComparison != 0) return fullTypeNameComparison;
                var entityGuidComparison = x.EntityGuid.CompareTo(y.EntityGuid);
                return entityGuidComparison != 0 ? entityGuidComparison : x.VariantHash.CompareTo(y.VariantHash);
            }

            public int CompareTo(ComponentOverride other)
            {
                return Compare(this, other);
            }
        }

        internal bool TryFindExistingOverrideIndex(Type managedType, in EntityGuid guid, out int index)
        {
            var managedTypeFullName = managedType.FullName;
            return TryFindExistingOverrideIndexViaFullName(managedTypeFullName, guid.b, out index);
        }

        internal bool TryFindExistingOverrideIndexViaFullName(string managedTypeFullName, in ulong entityGuid, out int index)
        {
            for (index = 0; index < ComponentOverrides.Length; index++)
            {
                ref var componentOverride = ref ComponentOverrides[index];
                if (string.Equals(componentOverride.FullTypeName, managedTypeFullName, StringComparison.OrdinalIgnoreCase) && componentOverride.EntityGuid == entityGuid)
                {
                    return true;
                }
            }
            index = -1;
            return false;
        }
    }
}
