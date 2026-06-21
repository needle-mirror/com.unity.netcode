using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Collection of utility that are used by the editor and runtime to compute and check ghost
    /// component variants hashes.
    /// </summary>
    public static class GhostVariantsUtility
    {
        internal const string k_DefaultVariantName = "Default";
        internal const string k_ClientOnlyVariant = nameof(ClientOnlyVariant);
        internal const string k_ServerOnlyVariant = nameof(ServerOnlyVariant);
        internal const string k_DontSerializeVariant = nameof(DontSerializeVariant);
        static readonly FixedString32Bytes k_NetCodeGhostNetVariant = "NetCode.GhostNetVariant";
        static readonly ulong k_NetCodeGhostNetVariantHash = TypeHash.FNV1A64(k_NetCodeGhostNetVariant);

        /// <summary>Stable hash of the built-in <see cref="ClientOnlyVariant"/>. Use this from a baker to force a component to be client-only.</summary>
        public static readonly ulong ClientOnlyHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_ClientOnlyVariant}"));
        /// <summary>Stable hash of the built-in <see cref="ServerOnlyVariant"/>. Use this from a baker to force a component to be server-only.</summary>
        public static readonly ulong ServerOnlyHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_ServerOnlyVariant}"));
        /// <summary>Stable hash of the built-in <see cref="DontSerializeVariant"/>. Use this from a baker to mark a component as non-serialized.</summary>
        public static readonly ulong DontSerializeHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_DontSerializeVariant}"));

        static ulong CalculateVariantHash(ulong variantTypeHash, ulong componentTypeHash)
        {
            var hash = k_NetCodeGhostNetVariantHash;
            hash = TypeHash.CombineFNV1A64(hash, componentTypeHash);
            hash = TypeHash.CombineFNV1A64(hash, variantTypeHash);
            return hash;
        }
        /// <summary>Calculates the "variant hash" for the component type itself, so that we can fetch the meta-data.</summary>
        /// <remarks>It's a little odd, but the default serializer for a Component is the ComponentType itself. I.e. It is its own variant.</remarks>
        /// <param name="componentType">The ComponentType to be used for both the component, and the variant.</param>
        /// <returns>The calculated hash.</returns>
        public static ulong CalculateVariantHashForComponent(ComponentType componentType)
        {
            var componentTypeHash = TypeManager.GetFullNameHash(componentType.TypeIndex);
            return CalculateVariantHash(componentTypeHash, componentTypeHash);
        }

        /// <summary>Calculates a stable hash for a variant via <see cref="TypeManager.GetTypeNameFixed"/>.</summary>
        /// <param name="variantTypeFullName">The Variant Type's <see cref="Type.FullName"/>.</param>
        /// <param name="componentType">The ComponentType that this variant applies to.</param>
        /// <returns>The calculated hash.</returns>
        public static ulong UncheckedVariantHash(in FixedString512Bytes variantTypeFullName, ComponentType componentType)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantTypeFullName), TypeManager.GetFullNameHash(componentType.TypeIndex));
        }

        /// <summary>Calculates the "variant hash" for the variant + component pair.</summary>
        /// <param name="variantTypeFullName">The Variant Type's System.Type.FullName.</param>
        /// <param name="componentTypeFullName">The Component Type's System.Type.FullName that this variant applies to.</param>
        /// <returns>The calculated hash.</returns>
        public static ulong UncheckedVariantHash(in FixedString512Bytes variantTypeFullName, in FixedString512Bytes componentTypeFullName)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantTypeFullName), TypeHash.FNV1A64(componentTypeFullName));
        }

        /// <summary>
        /// Calculates the "variant hash" for the variant + component pair. Non-Burst Compatible version.
        /// </summary>
        /// <param name="variantTypeFullName">The Variant Type's System.Type.FullName.</param>
        /// <param name="componentTypeFullName">The Component Type's System.Type.FullName that this variant applies to.</param>
        /// <returns>The calculated hash.</returns>
        /// <remarks>This method is not Burst Compatible.</remarks>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public static ulong UncheckedVariantHashNBC(string variantTypeFullName, string componentTypeFullName)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantTypeFullName), TypeHash.FNV1A64(componentTypeFullName));
        }

        /// <summary>Calculates a stable hash for a variant by combining the variant Type.Fullname and
        /// <see cref="ComponentType"/> name hash <see cref="TypeManager.GetFullNameHash"/>.</summary>
        /// <param name="variantStructDeclaration">The Variant struct declaration type.</param>
        /// <param name="componentType">The ComponentType that this variant applies to.</param>
        /// <returns>The calculated hash.</returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public static ulong UncheckedVariantHashNBC(Type variantStructDeclaration, ComponentType componentType)
        {
            return CalculateVariantHash(TypeHash.FNV1A64(variantStructDeclaration.FullName), TypeManager.GetFullNameHash(componentType.TypeIndex));
        }

        /// <summary>Calculates the "variant hash" for the variant + component pair.</summary>
        /// <param name="variantTypeHash">The hash of the Variant Type's System.Type.FullName.</param>
        /// <param name="componentType">The ComponentType that this variant applies to.</param>
        /// <returns>The calculated hash.</returns>
        public static ulong UncheckedVariantHash(ulong variantTypeHash, ComponentType componentType)
        {
            return CalculateVariantHash(variantTypeHash, TypeManager.GetFullNameHash(componentType.TypeIndex));
        }

        /// <summary>Resolves a variant <see cref="Type"/> to its <see cref="ulong"/> hash for a given component,
        /// honoring the well-known special variants (<see cref="DontSerializeVariant"/>, <see cref="ClientOnlyVariant"/>,
        /// <see cref="ServerOnlyVariant"/>) and falling through to <see cref="UncheckedVariantHashNBC(Type, ComponentType)"/>.
        /// Returns 0 when <paramref name="variantType"/> is null.</summary>
        /// <remarks>Single-source-of-truth for converting a managed variant type into its on-disk hash; used by both
        /// <c>DefaultVariantSystemBase.Rule</c> and the baker-side <c>AppendOverride&lt;TVariant&gt;</c> extension.</remarks>
        /// <param name="variantType">The managed variant type to resolve. Pass null to receive 0; pass one of the
        /// well-known special variants to receive the precomputed hash; pass any other type to fall through to
        /// <see cref="UncheckedVariantHashNBC(Type, ComponentType)"/>.</param>
        /// <param name="componentType">The component type the variant applies to. Used as the second hash input for
        /// the fall-through path; ignored for the well-known special variants.</param>
        /// <returns>The resolved variant hash, or 0 if <paramref name="variantType"/> is null.</returns>
        [ExcludeFromBurstCompatTesting("Use managed types")]
        public static ulong ResolveVariantHashFromType(Type variantType, ComponentType componentType)
        {
            if (variantType == null)
                return 0;
            if (variantType == typeof(DontSerializeVariant))
                return DontSerializeHash;
            if (variantType == typeof(ClientOnlyVariant))
                return ClientOnlyHash;
            if (variantType == typeof(ServerOnlyVariant))
                return ServerOnlyHash;
            return UncheckedVariantHashNBC(variantType, componentType);
        }

    }
}
