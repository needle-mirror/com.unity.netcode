using System;
using System.Diagnostics;
using Unity.Entities;
using UnityEngine;
using Unity.NetCode.Hybrid;

namespace Unity.NetCode
{
    /// <summary>
    /// A baking-only buffer entry that overrides how a ghost component is serialized for the prefab being baked,
    /// without forcing the user to configure a <see cref="GhostAuthoringInspectionComponent"/> by hand.
    /// </summary>
    /// <remarks>
    /// <para>This is a baking-only buffer (see <see cref="BakingTypeAttribute"/>) — it is stripped before runtime
    /// and never reaches the live world.</para>
    /// <para>The buffer must live on the user baker's own primary entity (Unity Entities baking forbids cross-baker
    /// writes to the same primary entity). The aggregation happens during <see cref="GhostAuthoringBakingSystem"/>,
    /// which walks the linked entity group and reads each entity's overrides.</para>
    /// <para>Precedence: any matching override on the prefab's <see cref="GhostAuthoringInspectionComponent"/> wins
    /// over baker-contributed overrides. If multiple bakers happen to target the same (entity, component), the first
    /// entry encountered wins (baking is deterministic so this is stable across runs).</para>
    /// <para>From a baker, call <c>AddBuffer&lt;GhostVariantOverride&gt;(entity)</c> to obtain the buffer, then use
    /// the helper extensions on it (e.g. <see cref="GhostVariantOverrideBakerExtensions.AppendDontSerializeOverride"/>)
    /// to construct and append entries in one call.</para>
    /// </remarks>
    [BakingType]
    public struct GhostVariantBakedOverride : IBufferElementData
    {
        /// <summary>Sentinel value meaning "no override" for <see cref="PrefabType"/> and <see cref="SendTypeOptimization"/>.
        /// Matches <see cref="GhostAuthoringInspectionComponent.ComponentOverride.NoOverride"/>.</summary>
        public const int NoOverride = -1;

        /// <summary>Typed sentinel for an unset <see cref="PrefabType"/>. Use this rather than casting -1 by hand.</summary>
        public const GhostPrefabType NoPrefabTypeOverride = (GhostPrefabType)NoOverride;

        /// <summary>Typed sentinel for an unset <see cref="SendTypeOptimization"/>. Use this rather than casting -1 by hand.</summary>
        public const GhostSendType NoSendTypeOverride = (GhostSendType)NoOverride;

        /// <summary>The id of the GameObject this override targets, mirroring <see cref="EntityGuid.OriginatingId"/>.
        /// Leave as <c>default</c> to mean "the entity this buffer lives on" — the common case.</summary>
        public int TargetGameObjectInstanceId;

        /// <summary>The serial of the target entity, mirroring <see cref="EntityGuid.Serial"/>. Set to 0 to mean
        /// "the primary entity for the target GameObject".</summary>
        public ulong TargetEntitySerial;

        /// <summary>Hash of the target component type's full name. Use
        /// <see cref="TypeManager.CalculateFullNameHash"/> on <see cref="ComponentType.GetManagedType"/>'s
        /// <see cref="Type.FullName"/>.</summary>
        public ulong ComponentTypeFullNameHash;

        /// <summary>The variant hash to apply, or 0 to leave the variant unchanged. Compute via
        /// <see cref="GhostVariantsUtility.ResolveVariantHashFromType"/> (which honors well-known special variants
        /// like <see cref="DontSerializeVariant"/>), or <see cref="GhostVariantsUtility.UncheckedVariantHashNBC(Type, ComponentType)"/>
        /// when you already know the variant is a user-defined struct.</summary>
        public ulong VariantHash;

        /// <summary>Override for <see cref="GhostPrefabType"/>. Cast to <see cref="int"/> and compare against
        /// <see cref="NoOverride"/> (-1) to detect "unset" (mirrors <see cref="GhostAuthoringInspectionComponent.ComponentOverride.PrefabType"/>).</summary>
        public GhostPrefabType PrefabType;

        /// <summary>Override for <see cref="GhostSendType"/>. Cast to <see cref="int"/> and compare against
        /// <see cref="NoOverride"/> (-1) to detect "unset" (mirrors <see cref="GhostAuthoringInspectionComponent.ComponentOverride.SendTypeOptimization"/>).</summary>
        public GhostSendType SendTypeOptimization;

        /// <summary>If <paramref name="entry"/> has the default ("self") targeting, fills both targeting fields
        /// from <paramref name="hostGuid"/>. If the user explicitly retargeted via <c>targetGameObject</c>,
        /// <see cref="TargetEntitySerial"/> stays at 0 (meaning "primary entity for that GameObject") rather than
        /// inheriting the host's serial.</summary>
        /// <param name="entry">Override entry to fill in. Mutated in place when its <see cref="TargetGameObjectInstanceId"/>
        /// is the default (unset).</param>
        /// <param name="hostGuid">The <see cref="EntityGuid"/> of the entity the buffer lives on; used as the source
        /// for both targeting fields when self-targeting is in effect.</param>
        public static void ResolveSelfTargeting(ref GhostVariantBakedOverride entry, in EntityGuid hostGuid)
        {
            if (entry.TargetGameObjectInstanceId != 0) return;
            entry.TargetGameObjectInstanceId = hostGuid.OriginatingId;
            if (entry.TargetEntitySerial == 0)
                entry.TargetEntitySerial = hostGuid.Serial;
        }
    }

    /// <summary>
    /// Convenience extensions on <see cref="DynamicBuffer{T}"/> of <see cref="GhostVariantBakedOverride"/> that build
    /// and append entries in one call.
    /// </summary>
    /// <remarks>
    /// <para>Typical usage from a user <c>Baker</c>:</para>
    /// <code>
    /// public override void Bake(MyAuthoring auth)
    /// {
    ///     var entity = GetEntity(TransformUsageFlags.None);
    ///     var overrides = AddBuffer&lt;GhostVariantOverride&gt;(entity);
    ///     overrides.AppendDontSerializeOverride(typeof(LocalTransform));
    ///     overrides.AppendPrefabTypeOverride(typeof(MyMarker), GhostPrefabType.Server);
    /// }
    /// </code>
    /// <para>To target a different GameObject (e.g. the root from a child baker), pass the <c>targetGameObject</c> parameter.</para>
    /// </remarks>
    public static class GhostVariantOverrideBakerExtensions
    {
        /// <summary>Append an override that forces <paramref name="componentType"/> to use <typeparamref name="TVariant"/>.</summary>
        /// <typeparam name="TVariant">The variant struct type to apply. Resolved to its hash via
        /// <see cref="GhostVariantsUtility.ResolveVariantHashFromType"/>; well-known special variants
        /// (<see cref="DontSerializeVariant"/>, <see cref="ClientOnlyVariant"/>, <see cref="ServerOnlyVariant"/>)
        /// are honored.</typeparam>
        /// <param name="buffer">The baking-only override buffer (extension method receiver).</param>
        /// <param name="componentType">The component type whose serialization is being overridden.</param>
        /// <param name="targetGameObject">Optional retarget. If null, the override targets the primary entity of the
        /// GameObject that hosts the buffer (the common case).</param>
        /// <param name="targetEntitySerial">Optional entity serial within <paramref name="targetGameObject"/>. 0 means
        /// the primary entity for that GameObject.</param>
        /// <exception cref="InvalidOperationException">Thrown if an entry for this (componentType, target) already exists in the buffer.</exception>
        public static void AppendOverride<TVariant>(this DynamicBuffer<GhostVariantBakedOverride> buffer,
            ComponentType componentType, GameObject targetGameObject = null, ulong targetEntitySerial = 0)
            where TVariant : struct
        {
            var entry = new GhostVariantBakedOverride
            {
                ComponentTypeFullNameHash = TypeManager.GetFullNameHash(componentType.TypeIndex),
                VariantHash = GhostVariantsUtility.ResolveVariantHashFromType(typeof(TVariant), componentType),
                PrefabType = GhostVariantBakedOverride.NoPrefabTypeOverride,
                SendTypeOptimization = GhostVariantBakedOverride.NoSendTypeOverride,
                TargetGameObjectInstanceId = targetGameObject != null ? targetGameObject.GetInstanceID() : default,
                TargetEntitySerial = targetEntitySerial,
            };
            AssertNoDuplicate(buffer, entry, componentType);
            buffer.Add(entry);
        }

        /// <summary>Append an override that prevents <paramref name="componentType"/> from being serialized for this prefab.</summary>
        /// <param name="buffer">The baking-only override buffer (extension method receiver).</param>
        /// <param name="componentType">The component type to mark as non-serialized.</param>
        /// <param name="targetGameObject">Optional retarget. If null, the override targets the primary entity of the
        /// GameObject that hosts the buffer (the common case).</param>
        /// <param name="targetEntitySerial">Optional entity serial within <paramref name="targetGameObject"/>. 0 means
        /// the primary entity for that GameObject.</param>
        /// <exception cref="InvalidOperationException">Thrown if an entry for this (componentType, target) already exists in the buffer.</exception>
        public static void AppendDontSerializeOverride(this DynamicBuffer<GhostVariantBakedOverride> buffer,
            ComponentType componentType, GameObject targetGameObject = null, ulong targetEntitySerial = 0)
        {
            var entry = new GhostVariantBakedOverride
            {
                ComponentTypeFullNameHash = TypeManager.GetFullNameHash(componentType.TypeIndex),
                VariantHash = GhostVariantsUtility.DontSerializeHash,
                PrefabType = GhostVariantBakedOverride.NoPrefabTypeOverride,
                SendTypeOptimization = GhostVariantBakedOverride.NoSendTypeOverride,
                TargetGameObjectInstanceId = targetGameObject != null ? targetGameObject.GetInstanceID() : default,
                TargetEntitySerial = targetEntitySerial,
            };
            AssertNoDuplicate(buffer, entry, componentType);
            buffer.Add(entry);
        }

        /// <summary>Append an override that sets the <see cref="GhostPrefabType"/> for <paramref name="componentType"/>
        /// (e.g. <see cref="GhostPrefabType.Server"/> to keep it server-only).</summary>
        /// <param name="buffer">The baking-only override buffer (extension method receiver).</param>
        /// <param name="componentType">The component type whose <see cref="GhostPrefabType"/> is being overridden.</param>
        /// <param name="prefabType">The new <see cref="GhostPrefabType"/> value to apply.</param>
        /// <param name="targetGameObject">Optional retarget. If null, the override targets the primary entity of the
        /// GameObject that hosts the buffer (the common case).</param>
        /// <param name="targetEntitySerial">Optional entity serial within <paramref name="targetGameObject"/>. 0 means
        /// the primary entity for that GameObject.</param>
        /// <exception cref="InvalidOperationException">Thrown if an entry for this (componentType, target) already exists in the buffer.</exception>
        public static void AppendPrefabTypeOverride(this DynamicBuffer<GhostVariantBakedOverride> buffer,
            ComponentType componentType, GhostPrefabType prefabType,
            GameObject targetGameObject = null, ulong targetEntitySerial = 0)
        {
            var entry = new GhostVariantBakedOverride
            {
                ComponentTypeFullNameHash = TypeManager.GetFullNameHash(componentType.TypeIndex),
                VariantHash = 0,
                PrefabType = prefabType,
                SendTypeOptimization = GhostVariantBakedOverride.NoSendTypeOverride,
                TargetGameObjectInstanceId = targetGameObject != null ? targetGameObject.GetInstanceID() : default,
                TargetEntitySerial = targetEntitySerial,
            };
            AssertNoDuplicate(buffer, entry, componentType);
            buffer.Add(entry);
        }

        /// <summary>Append an override that sets the <see cref="GhostSendType"/> optimization for <paramref name="componentType"/>.</summary>
        /// <param name="buffer">The baking-only override buffer (extension method receiver).</param>
        /// <param name="componentType">The component type whose <see cref="GhostSendType"/> is being overridden.</param>
        /// <param name="sendType">The new <see cref="GhostSendType"/> optimization to apply.</param>
        /// <param name="targetGameObject">Optional retarget. If null, the override targets the primary entity of the
        /// GameObject that hosts the buffer (the common case).</param>
        /// <param name="targetEntitySerial">Optional entity serial within <paramref name="targetGameObject"/>. 0 means
        /// the primary entity for that GameObject.</param>
        /// <exception cref="InvalidOperationException">Thrown if an entry for this (componentType, target) already exists in the buffer.</exception>
        public static void AppendSendTypeOverride(this DynamicBuffer<GhostVariantBakedOverride> buffer,
            ComponentType componentType, GhostSendType sendType,
            GameObject targetGameObject = null, ulong targetEntitySerial = 0)
        {
            var entry = new GhostVariantBakedOverride
            {
                ComponentTypeFullNameHash = TypeManager.GetFullNameHash(componentType.TypeIndex),
                VariantHash = 0,
                PrefabType = GhostVariantBakedOverride.NoPrefabTypeOverride,
                SendTypeOptimization = sendType,
                TargetGameObjectInstanceId = targetGameObject != null ? targetGameObject.GetInstanceID() : default,
                TargetEntitySerial = targetEntitySerial,
            };
            AssertNoDuplicate(buffer, entry, componentType);
            buffer.Add(entry);
        }

        /// <summary>Rejects a second <c>Append*</c> call targeting the same
        /// (component, target GameObject, target entity serial) tuple already present in <paramref name="buffer"/>.</summary>
        /// <remarks>Two entries for the same key would be ambiguous — the aggregator in
        /// <see cref="GhostAuthoringBakingSystem"/> picks the first encountered and silently drops the rest.
        /// Throwing here turns that silent drop into a clear baker-time error.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when a duplicate is detected.</exception>
        [Conditional("UNITY_ASSERTIONS")]
        static void AssertNoDuplicate(DynamicBuffer<GhostVariantBakedOverride> buffer,
            in GhostVariantBakedOverride entry, ComponentType componentType)
        {
            for (int i = 0; i < buffer.Length; ++i)
            {
                var existing = buffer[i];
                if (existing.ComponentTypeFullNameHash == entry.ComponentTypeFullNameHash
                    && existing.TargetGameObjectInstanceId == entry.TargetGameObjectInstanceId
                    && existing.TargetEntitySerial == entry.TargetEntitySerial)
                {
                    throw new InvalidOperationException(
                        $"Cannot append a second GhostVariantBakedOverride for component '{componentType}' on the same target — the buffer already contains an entry for this (component, target). " +
                        "If you need to set Variant + PrefabType + SendType together, construct a single GhostVariantBakedOverride struct manually with all fields populated and call buffer.Add(...) directly.");
                }
            }
        }

    }
}
