using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Collection of utility that are used by the editor and runtime to compute and check ghost
    /// component variants hashes.
    /// </summary>
    internal static class GhostVariantsUtility
    {
        public const string k_DefaultVariantName = "Default";
        public const string k_ClientOnlyVariant = nameof(ClientOnlyVariant);
        public const string k_ServerOnlyVariant = nameof(ServerOnlyVariant);
        public const string k_DontSerializeVariant = nameof(DontSerializeVariant);
        static readonly FixedString32Bytes k_NetCodeGhostNetVariant = "NetCode.GhostNetVariant";
        static readonly ulong k_NetCodeGhostNetVariantHash = TypeHash.FNV1A64(k_NetCodeGhostNetVariant);

        internal static readonly ulong ClientOnlyHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_ClientOnlyVariant}"));
        internal static readonly ulong ServerOnlyHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_ServerOnlyVariant}"));
        internal static readonly ulong DontSerializeHash = TypeHash.CombineFNV1A64(k_NetCodeGhostNetVariantHash, TypeHash.FNV1A64((FixedString64Bytes)$"Unity.NetCode.{k_DontSerializeVariant}"));

        /// <summary>Calculates a stable hash for a variant via <see cref="TypeManager.GetTypeNameFixed"/>.</summary>
        /// <param name="variantTypeName">Full type name of the variant type.</param>
        /// <param name="componentType">The ComponentType that this variant applies to.</param>
        /// <returns>The calculated hash.</returns>
        [GenerateTestsForBurstCompatibility]
        internal static ulong UncheckedVariantHash(in FixedString512Bytes variantTypeName, ComponentType componentType)
        {
            var componentTypeFullName = componentType.GetDebugTypeName();
            return UncheckedVariantHash(variantTypeName, new FixedString512Bytes(componentTypeFullName));
        }

        /// <summary>Calculates the "variant hash" for the variant + component pair.</summary>
        [GenerateTestsForBurstCompatibility]
        internal static ulong UncheckedVariantHash(in FixedString512Bytes variantTypeName, in FixedString512Bytes componentTypeFullName)
        {
            var hash = k_NetCodeGhostNetVariantHash;
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(componentTypeFullName));
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(variantTypeName));
            return hash;
        }

        /// <summary>Calculates the "variant hash" for the component type itself, so that we can fetch the meta-data.</summary>
        /// <remarks>It's a little odd, but the default serializer for a Component is the ComponentType itself. I.e. It is its own variant.</remarks>
        public static ulong CalculateVariantHashForComponent(ComponentType componentType)
        {
            var baseComponentTypeName =  componentType.GetDebugTypeName();
            var fs = new FixedString512Bytes(baseComponentTypeName);
            return UncheckedVariantHash(fs, fs);
        }

        /// <summary>Calculates the "variant hash" for the variant + component pair. Non-Burst Compatible version.</summary>
        internal static ulong UncheckedVariantHashNBC(string variantTypeName, string componentTypeFullName)
        {
            var hash = k_NetCodeGhostNetVariantHash;
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(componentTypeFullName));
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(variantTypeName));
            return hash;
        }
    }
}
