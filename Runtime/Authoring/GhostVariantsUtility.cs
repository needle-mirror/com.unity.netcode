using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// Collection of utility that are used by the editor and runtime to compute and check ghost
    /// component variants hashes
    /// </summary>
    public static class GhostVariantsUtility
    {
        private static ulong UncheckedVariantHash(string variantTypeName, ComponentType componentType)
        {
            var hash = TypeHash.FNV1A64("NetCode.GhostNetVariant");
            hash = TypeHash.CombineFNV1A64(hash, Entities.TypeHash.FNV1A64(componentType.GetDebugTypeName()));
            hash = TypeHash.CombineFNV1A64(hash, Entities.TypeHash.FNV1A64(variantTypeName));
            return hash;
        }
        private static ulong UncheckedVariantHash(string variantTypeName, string componentTypeName)
        {
            var hash = TypeHash.FNV1A64("NetCode.GhostNetVariant");
            hash = TypeHash.CombineFNV1A64(hash, Entities.TypeHash.FNV1A64(componentTypeName));
            hash = TypeHash.CombineFNV1A64(hash, Entities.TypeHash.FNV1A64(variantTypeName));
            return hash;
        }

        internal static bool IsClientOnlyVariant(ComponentType componentType, ulong variantHash)
        {
            return ClientOnlyHash(componentType) == variantHash;
        }
        internal static bool IsDoNotSerializeVariant(ComponentType componentType, ulong variantHash)
        {
            return DoNotSerializeHash(componentType) == variantHash;
        }
        internal static bool IsDoNotSerializeVariant(string componentTypeName, ulong variantHash)
        {
            return DoNotSerializeHash(componentTypeName) == variantHash;
        }

        internal static ulong ClientOnlyHash(ComponentType componentType)
        {
            return UncheckedVariantHash("Unity.NetCode.ClientOnlyVariant", componentType);
        }
        internal static ulong DoNotSerializeHash(ComponentType componentType)
        {
            return UncheckedVariantHash("Unity.NetCode.DontSerializeVariant", componentType);
        }
        internal static ulong DoNotSerializeHash(string componentTypeName)
        {
            return UncheckedVariantHash("Unity.NetCode.DontSerializeVariant", componentTypeName);
        }
    }
}
