using System;
using System.Collections.Generic;
using System.Reflection;
using JetBrains.Annotations;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    /// <summary>Internal class used by the GhostComponentInspector to store post-conversion (i.e. Baked) data.</summary>
    struct BakedGameObjectResult
    {
        public GameObject SourceGameObject;
        [CanBeNull] public GhostAuthoringInspectionComponent SourceInspection => SourceGameObject.GetComponent<GhostAuthoringInspectionComponent>();
        public GhostAuthoringComponent RootAuthoring;
        public List<BakedEntityResult> BakedEntities;
    }

    /// <inheritdoc cref="BakedGameObjectResult"/>
    struct BakedEntityResult
    {
        public BakedGameObjectResult GoParent;
        public Entity Entity;
        public string EntityName;
        public int EntityIndex;
        public bool IsPrimaryEntity => EntityIndex == 0;
        public List<BakedComponentItem> BakedComponents;
        public bool IsLinkedEntity;
        public bool IsRoot;
    }

    /// <inheritdoc cref="BakedGameObjectResult"/>
    class BakedComponentItem
    {
        public BakedEntityResult EntityParent;
        public string fullname;
        public Type managedType;
        public GhostComponentAttribute ghostComponentAttribute;
        /// <summary>Fallback is to use the managed type if not found.</summary>
        public VariantType variant;
        public VariantType[] availableVariants;
        public string[] availableVariantReadableNames;

        public int entityIndex;
        public EntityGuid entityGuid;
        public bool anyVariantIsSerialized;

        public CodeGenTypeMetaData metaData;
        /// <summary>Cache the default variant so we can mark it up as such in the Inspection UI.</summary>
        public VariantType defaultVariant;

        public bool isDontSerializeVariant => variant.Hash == GhostVariantsUtility.DontSerializeHash;

        public GhostPrefabType PrefabType => HasPrefabOverride() && GetPrefabOverride().IsPrefabTypeOverriden
            ? GetPrefabOverride().PrefabType
            : DefaultPrefabType;

        /// <summary>Note that variant.PrefabType has higher priority than attribute.PrefabType.</summary>
        GhostPrefabType DefaultPrefabType => variant.PrefabType != GhostPrefabType.All ? variant.PrefabType : ghostComponentAttribute.PrefabType;

        public GhostSendType SendTypeOptimization => HasPrefabOverride() && GetPrefabOverride().IsSendTypeOptimizationOverriden
            ? GetPrefabOverride().SendTypeOptimization
            : ghostComponentAttribute.SendTypeOptimization;

        public ulong VariantHash
        {
            get
            {
                if (HasPrefabOverride())
                {
                    ref var componentOverride = ref GetPrefabOverride();
                    if (componentOverride.IsVariantOverriden)
                        return componentOverride.VariantHash;
                }
                return 0;
            }
        }

        /// <summary>
        /// Checks attributes to denote if users are allowed to create an override of this via a <see cref="GhostAuthoringInspectionComponent"/>.
        /// We support prefab overrides "implicitly" if we have multiple variant types.
        /// </summary>
        public bool DoesAllowModification => !metaData.HasDontSupportPrefabOverridesAttribute && (metaData.HasSupportsPrefabOverridesAttribute || HasMultipleVariants);

        /// <summary>I.e. Implicitly supports prefab overrides.</summary>
        internal bool HasMultipleVariants => availableVariants.Length > 1;

        /// <summary>Returns by ref. Throws if not found. Use <see cref="HasPrefabOverride"/>.</summary>
        public ref GhostAuthoringInspectionComponent.ComponentOverride GetPrefabOverride()
        {
            if (EntityParent.GoParent.SourceInspection.TryFindExistingOverrideIndex(managedType, entityGuid, out var index))
                return ref EntityParent.GoParent.SourceInspection.ComponentOverrides[index];
            throw new InvalidOperationException("No override declared.");
        }

        /// <summary>Returns true if this Inspection Component has a prefab override for this Baked Component Type.</summary>
        public bool HasPrefabOverride()
        {
            return EntityParent.GoParent.SourceInspection != null && EntityParent.GoParent.SourceInspection.TryFindExistingOverrideIndex(managedType, entityGuid, out _);
        }

        /// <summary>Returns the current override if it exists, or a new one, by ref.</summary>
        public ref GhostAuthoringInspectionComponent.ComponentOverride GetOrAddPrefabOverride()
        {
            var setPrefabType = (variant.PrefabType != GhostPrefabType.All);
            var defaultPrefabType = setPrefabType ? DefaultPrefabType : (GhostPrefabType)GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;
            EntityParent.GoParent.SourceInspection.GetOrAddPrefabOverride(managedType, entityGuid, defaultPrefabType, out bool created);
            ref var @override = ref GetPrefabOverride();
            if (created)
                @override.PrefabType = defaultPrefabType;
            return ref @override;
        }

        /// <summary>
        /// Called during initialization and whenever a variant is modified by the user.
        /// Ensures we actually save any custom variants if we need to.
        /// </summary>
        public void SaveVariant(bool warnIfChosenIsNotAlreadySaved, bool allowSettingDefaultToRevertOverride)
        {
            if (variant.Hash != 0 && !VariantIsTheDefault && !HasPrefabOverride())
            {
                if(warnIfChosenIsNotAlreadySaved)
                    Debug.LogError($"Discovered on ghost '{EntityParent.GoParent.SourceGameObject.name}' that in-use variant ({variant}) was not saved as a prefabOverride! Fixed.");

                GetOrAddPrefabOverride();
            }

            if (HasPrefabOverride())
            {
                ref var @override = ref GetPrefabOverride();
                var hash = allowSettingDefaultToRevertOverride && VariantIsTheDefault ? 0 : variant.Hash;
                if (@override.VariantHash != hash)
                {
                    @override.VariantHash = hash;
                    EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Confirmed Variant on {fullname} is {variant}");
                }
            }

            // Prioritize fetching the GhostComponentAttribute from the variant (if we have one),
            // otherwise fallback to the "main" type (which is already set).
            var attributeOnVariant = variant.Variant.GetCustomAttribute<GhostComponentAttribute>();
            if (attributeOnVariant != null)
                ghostComponentAttribute = attributeOnVariant;
        }

        internal bool VariantIsTheDefault => variant.Hash == defaultVariant.Hash;

        /// <remarks>Note that this is an "override" action. Reverting to default is a different action.</remarks>
        public void TogglePrefabType(GhostPrefabType type)
        {
            var newValue = PrefabType ^ type;
            ref var @override = ref GetOrAddPrefabOverride();
            @override.PrefabType = newValue;
            EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Toggled GhostPrefabType.{type} on {fullname}, set type flag to GhostPrefabType.{newValue}");
        }

        /// <remarks>Note that this is an "override" action. Reverting to default is a different action.</remarks>
        public void SetSendTypeOptimization(GhostSendType newValue)
        {
            ref var @override = ref GetOrAddPrefabOverride();
            @override.SendTypeOptimization = newValue;
            EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Set GhostSendType.{newValue} on {fullname}, set value to GhostSendType.{newValue}");
        }

        public void RemoveEntirePrefabOverride(DropdownMenuAction action)
        {
            if (HasPrefabOverride())
            {
                variant = defaultVariant;
                ref var @override = ref GetPrefabOverride();
                @override.Reset();
                SaveVariant(false, true);
                EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Removed entire prefab override on {fullname}");
            }
            else GhostAuthoringInspectionComponent.forceSave = true;
        }

        public void ResetPrefabTypeToDefault(DropdownMenuAction action)
        {
            if (HasPrefabOverride())
            {
                ref var @override = ref GetPrefabOverride();
                @override.PrefabType = (GhostPrefabType) GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;
                EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Reset PrefabType on {fullname}");
            }
        }

        public void ResetSendTypeToDefault(DropdownMenuAction action)
        {
            if (HasPrefabOverride())
            {
                ref var @override = ref GetPrefabOverride();
                @override.SendTypeOptimization = (GhostSendType) GhostAuthoringInspectionComponent.ComponentOverride.NoOverride;
                EntityParent.GoParent.SourceInspection.SavePrefabOverride(ref @override, $"Reset SendTypeOptimization on {fullname}");
            }
        }

        public void ResetVariantToDefault()
        {
            if (HasPrefabOverride())
            {
                variant = defaultVariant;
                SaveVariant(false, true);
            }
        }

        public override string ToString() => $"BakedComponentItem[{fullname} with {variant}, {availableVariants.Length} variants available, entityGuid: {entityGuid}]";
    }
}
