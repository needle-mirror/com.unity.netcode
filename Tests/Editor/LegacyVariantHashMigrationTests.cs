using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Unity.Netcode.Tests
{
    class LegacyVariantHashMigrationTests
    {
        // Serialized-data compatibility: the literals are the deliberate PRE-rename spellings.
        // If someone "fixes" the pinned strings in GhostVariantsUtility, this fails.
        [Test]
        public void SpecialVariantHashes_KeepLegacyCasedInputs()
        {
            var seed = TypeHash.FNV1A64((FixedString32Bytes)"NetCode.GhostNetVariant");
            Assert.AreEqual(TypeHash.CombineFNV1A64(seed, TypeHash.FNV1A64((FixedString64Bytes)"Unity.NetCode.ClientOnlyVariant")), GhostVariantsUtility.ClientOnlyHash);
            Assert.AreEqual(TypeHash.CombineFNV1A64(seed, TypeHash.FNV1A64((FixedString64Bytes)"Unity.NetCode.ServerOnlyVariant")), GhostVariantsUtility.ServerOnlyHash);
            Assert.AreEqual(TypeHash.CombineFNV1A64(seed, TypeHash.FNV1A64((FixedString64Bytes)"Unity.NetCode.DontSerializeVariant")), GhostVariantsUtility.DontSerializeHash);
        }

        [Test]
        public void LegacyDefaultSerializerHash_Migrates()
        {
            var legacy = GhostVariantsUtility.UncheckedVariantHashNBC("Unity.NetCode.GhostOwner", "Unity.NetCode.GhostOwner");
            Assert.IsTrue(GhostVariantsUtility.TryMigrateLegacyVariantHash(legacy, typeof(GhostOwner), out var current));
            Assert.AreEqual(GhostVariantsUtility.CalculateVariantHashForComponent(ComponentType.ReadWrite<GhostOwner>()), current);
        }

        [Test]
        public void LegacyRenamedVariantOnUnrenamedComponent_Migrates()
        {
            var legacy = GhostVariantsUtility.UncheckedVariantHashNBC("Unity.NetCode.TransformDefaultVariant", "Unity.Transforms.LocalTransform");
            Assert.IsTrue(GhostVariantsUtility.TryMigrateLegacyVariantHash(legacy, typeof(LocalTransform), out var current));
            Assert.AreEqual(GhostVariantsUtility.UncheckedVariantHashNBC(typeof(TransformDefaultVariant), ComponentType.ReadWrite<LocalTransform>()), current);
        }

        [Test]
        public void CurrentHash_DoesNotMigrate()
        {
            var current = GhostVariantsUtility.CalculateVariantHashForComponent(ComponentType.ReadWrite<GhostOwner>());
            Assert.IsFalse(GhostVariantsUtility.TryMigrateLegacyVariantHash(current, typeof(GhostOwner), out _));
        }

        [Test]
        public void SpecialVariantHashes_DoNotMigrate()
        {
            Assert.IsFalse(GhostVariantsUtility.TryMigrateLegacyVariantHash(GhostVariantsUtility.DontSerializeHash, typeof(GhostOwner), out var unchanged));
            Assert.AreEqual(GhostVariantsUtility.DontSerializeHash, unchanged);
        }

        [Test]
        public void NonPackagePrefixes_AreNotTouched()
        {
            Assert.AreEqual("Unity.NetcodeSamples.Foo", GhostVariantsUtility.ToLegacyFullName("Unity.NetcodeSamples.Foo"));
            Assert.AreEqual("MyGame.NetCode.Thing", GhostVariantsUtility.ToCurrentFullName("MyGame.NetCode.Thing"));
        }

        // Ground truth: the VariantHash serialized in NetcodeSamples' GhostCube.prefab. Pre-rename it held the legacy
        // MediumPrecision pair hash (the pinned constant); the prefab now holds the migrated value, pinned below too.
        [Test]
        public void GhostRigidbodyData_LegacyHash_MatchesShippedSampleData()
        {
            var legacy = GhostVariantsUtility.UncheckedVariantHashNBC("Unity.NetCode.GhostRigidbodyDataMediumPrecision", "Unity.NetCode.GhostRigidbodyData");
            Assert.AreEqual(18355174433113025223ul, legacy);
            Assert.IsTrue(GhostVariantsUtility.TryMigrateLegacyVariantHash(legacy, typeof(GhostRigidbodyData), out var current));
            Assert.AreEqual(1457019893376372999ul, current); // value serialized in GhostCube.prefab today
            Assert.AreEqual(GhostVariantsUtility.UncheckedVariantHashNBC(typeof(GhostRigidbodyDataMediumPrecision), ComponentType.ReadWrite<GhostRigidbodyData>()), current);
        }
    }
}
