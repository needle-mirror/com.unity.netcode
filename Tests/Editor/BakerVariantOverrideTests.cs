using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// End-to-end tests for the baking-time <see cref="GhostVariantBakedOverride"/> mechanism. Mirrors the harness
    /// patterns in <see cref="PerPrefabOverridesTests"/> (uses <see cref="NetCodeTestWorld"/> +
    /// <see cref="TestNetCodeAuthoring"/>'s <see cref="TestNetCodeAuthoring.IConverter"/>).
    /// </summary>
    [TestFixture]
    internal class BakerVariantOverrideTests
    {
        /// <summary>
        /// Adds <see cref="GhostOwner"/> + <see cref="GhostGen_IntStruct"/> on the root, and
        /// <see cref="GhostGen_IntStruct"/> on children. Mirrors <c>PerPrefabOverridesTests.GhostConverter</c>
        /// so the prefab shape matches.
        /// </summary>
        internal class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var transform = baker.GetComponent<Transform>();
                baker.DependsOn(transform.parent);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                if (transform.parent == null)
                    baker.AddComponent(entity, new GhostOwner { NetworkId = -1 });
                baker.AddComponent(entity, new GhostGen_IntStruct());
            }
        }

        /// <summary>
        /// Adds <see cref="GhostGen_IntStruct"/> AND a <see cref="GhostVariantBakedOverride"/> pinning it to
        /// <see cref="DontSerializeVariant"/> on the root entity.
        /// </summary>
        internal class DontSerializeConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var transform = baker.GetComponent<Transform>();
                baker.DependsOn(transform.parent);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                if (transform.parent == null)
                    baker.AddComponent(entity, new GhostOwner { NetworkId = -1 });
                baker.AddComponent(entity, new GhostGen_IntStruct());

                var overrides = baker.AddBuffer<GhostVariantBakedOverride>(entity);
                overrides.AppendDontSerializeOverride(typeof(GhostGen_IntStruct));
            }
        }

        /// <summary>Sets <see cref="GhostPrefabType.Server"/> on <see cref="GhostGen_IntStruct"/> via baker.</summary>
        internal class ServerOnlyConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var transform = baker.GetComponent<Transform>();
                baker.DependsOn(transform.parent);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                if (transform.parent == null)
                    baker.AddComponent(entity, new GhostOwner { NetworkId = -1 });
                baker.AddComponent(entity, new GhostGen_IntStruct());

                var overrides = baker.AddBuffer<GhostVariantBakedOverride>(entity);
                overrides.AppendPrefabTypeOverride(typeof(GhostGen_IntStruct), GhostPrefabType.Server);
            }
        }

        /// <summary>
        /// Looks up the <see cref="GhostGen_IntStruct"/> entry in the prefab blob's
        /// <see cref="GhostPrefabBlobMetaData.ServerComponentList"/> for the entity at <paramref name="childIndex"/>.
        /// </summary>
        /// <returns>The variant hash recorded in the blob, or 0 if not found.</returns>
        static ulong GetBlobVariantHash(World world, Entity prefab, int childIndex)
        {
            var blob = world.EntityManager.GetComponentData<GhostPrefabMetaData>(prefab).Value;
            ref var meta = ref blob.Value;

            int compIdx = 0;
            for (int e = 0; e < meta.NumServerComponentsPerEntity.Length; ++e)
            {
                int count = meta.NumServerComponentsPerEntity[e];
                if (e == childIndex)
                {
                    for (int c = 0; c < count; ++c)
                    {
                        var info = meta.ServerComponentList[compIdx + c];
                        if (info.StableHash == TypeManager.GetTypeInfo<GhostGen_IntStruct>().StableTypeHash)
                            return info.Variant;
                    }
                    return 0;
                }
                compIdx += count;
            }
            return 0;
        }

        [Test]
        [Description(@"A baker that pins LocalTransform-equivalent (GhostGen_IntStruct) to DontSerializeVariant
must result in the prefab's GhostPrefabMetaData blob carrying the well-known DontSerializeHash for
that component on the root entity.")]
        public void BakerOverride_DontSerialize_RecordedInPrefabBlob()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var go = new GameObject("DontSerializeRoot");
            go.AddComponent<TestNetCodeAuthoring>().Converter = new DontSerializeConverter();
            var authoring = go.AddComponent<GhostAuthoringComponent>();
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.SupportedGhostModes = GhostModeMask.All;

            Assert.IsTrue(testWorld.CreateGhostCollection(go));
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var ghostCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var prefabList = testWorld.ServerWorld.EntityManager
                .GetBuffer<NetCodeTestPrefab>(ghostCollection).ToNativeArray(Allocator.Temp);

            Assert.AreEqual(1, prefabList.Length);
            var variantHash = GetBlobVariantHash(testWorld.ServerWorld, prefabList[0].Value, 0);
            Assert.AreEqual(GhostVariantsUtility.DontSerializeHash, variantHash,
                "Baker-set DontSerializeVariant should be recorded in the prefab's GhostPrefabMetaData blob.");
        }

        // Tests below assert different component presence on the server vs client prefab. The single-world-host
        // mode merges both into one prefab, so the server-vs-client divergence those tests verify cannot be
        // observed there — same constraint as PerPrefabOverridesTests.OverrideComponentPrefabType_*.
        [Test]
        [DisableSingleWorldHostTest]
        [Description(@"A baker that sets PrefabType=Server on a component must produce a server prefab that still
has the component and a client prefab that has it stripped.")]
        public void BakerOverride_PrefabTypeServer_RemovesComponentOnClient()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var go = new GameObject("ServerOnlyRoot");
            go.AddComponent<TestNetCodeAuthoring>().Converter = new ServerOnlyConverter();
            var authoring = go.AddComponent<GhostAuthoringComponent>();
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.SupportedGhostModes = GhostModeMask.All;

            Assert.IsTrue(testWorld.CreateGhostCollection(go));
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Server keeps the component (PrefabType.Server).
            var serverColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var serverPrefabs = testWorld.ServerWorld.EntityManager
                .GetBuffer<NetCodeTestPrefab>(serverColl).ToNativeArray(Allocator.Temp);
            Assert.AreEqual(1, serverPrefabs.Length);
            Assert.IsTrue(testWorld.ServerWorld.EntityManager
                .HasComponent<GhostGen_IntStruct>(serverPrefabs[0].Value));

            // Client should have it stripped.
            var clientColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
            var clientPrefabs = testWorld.ClientWorlds[0].EntityManager
                .GetBuffer<NetCodeTestPrefab>(clientColl).ToNativeArray(Allocator.Temp);
            Assert.AreEqual(1, clientPrefabs.Length);
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager
                .HasComponent<GhostGen_IntStruct>(clientPrefabs[0].Value));
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description(@"When both an inspection-component override and a baker override target the same
(entity, component), the inspection override wins for each field. Baker says PrefabType.Server,
inspection says PrefabType.All — the component must remain present on the client.")]
        public void InspectionOverride_BeatsBakerOverride()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            // Baker says Server-only; Inspection says All-clients. Inspection should win.
            var go = new GameObject("InspectionWinsRoot");
            go.AddComponent<TestNetCodeAuthoring>().Converter = new ServerOnlyConverter();
            var authoring = go.AddComponent<GhostAuthoringComponent>();
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.SupportedGhostModes = GhostModeMask.All;
            var inspection = go.AddComponent<GhostAuthoringInspectionComponent>();
            inspection.ComponentOverrides = new[]
            {
                new GhostAuthoringInspectionComponent.ComponentOverride
                {
                    FullTypeName = typeof(GhostGen_IntStruct).FullName,
                    PrefabType = GhostPrefabType.All,
                    SendTypeOptimization = GhostSendType.AllClients,
                    VariantHash = 0,
                }
            };

            Assert.IsTrue(testWorld.CreateGhostCollection(go));
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            var clientColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
            var clientPrefab = testWorld.ClientWorlds[0].EntityManager
                .GetBuffer<NetCodeTestPrefab>(clientColl)[0].Value;
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(clientPrefab),
                "Inspection-component PrefabType.All should override baker-set PrefabType.Server, keeping the component on the client.");
        }

        /// <summary>
        /// A baker on a child GameObject sets <see cref="GhostPrefabType.Server"/> on its own entity. The root
        /// retains <see cref="GhostPrefabType.All"/>. We use PrefabType (not Variant) because the child entity's
        /// default variant is already <see cref="DontSerializeVariant"/>, so a DontSerialize override on the
        /// child wouldn't be distinguishable from the default.
        /// </summary>
        internal class ChildOnlyServerOnlyConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var transform = baker.GetComponent<Transform>();
                baker.DependsOn(transform.parent);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                if (transform.parent == null)
                    baker.AddComponent(entity, new GhostOwner { NetworkId = -1 });
                baker.AddComponent(entity, new GhostGen_IntStruct());

                if (transform.parent != null)
                {
                    var overrides = baker.AddBuffer<GhostVariantBakedOverride>(entity);
                    overrides.AppendPrefabTypeOverride(typeof(GhostGen_IntStruct), GhostPrefabType.Server);
                }
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description(@"A baker override on a child GameObject's primary entity must apply only to that child
entity. The root entity must remain unaffected.")]
        public void BakerOverride_OnChildEntity_AffectsOnlyChild()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var root = new GameObject("ChildOverrideRoot");
            root.AddComponent<TestNetCodeAuthoring>().Converter = new ChildOnlyServerOnlyConverter();
            var child = new GameObject("Child");
            child.transform.parent = root.transform;
            child.AddComponent<TestNetCodeAuthoring>().Converter = new ChildOnlyServerOnlyConverter();
            var authoring = root.AddComponent<GhostAuthoringComponent>();
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.SupportedGhostModes = GhostModeMask.All;

            Assert.IsTrue(testWorld.CreateGhostCollection(root));
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Server: root + child both have GhostGen_IntStruct.
            var serverColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var serverPrefab = testWorld.ServerWorld.EntityManager
                .GetBuffer<NetCodeTestPrefab>(serverColl)[0].Value;
            Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(serverPrefab),
                "Root entity on server keeps the component (no baker override on root).");
            var serverLeg = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverPrefab);
            Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(serverLeg[1].Value),
                "Child entity on server keeps the component (PrefabType.Server includes server).");

            // Client: root keeps the component (no override). Child loses it (PrefabType.Server excludes client).
            var clientColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
            var clientPrefab = testWorld.ClientWorlds[0].EntityManager
                .GetBuffer<NetCodeTestPrefab>(clientColl)[0].Value;
            Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(clientPrefab),
                "Root entity on client keeps the component (baker override targeted child only).");
            var clientLeg = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientPrefab);
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(clientLeg[1].Value),
                "Child entity on client should have the component stripped by the baker's PrefabType.Server override.");
        }

        /// <summary>
        /// A baker on a CHILD GameObject retargets the ROOT entity's <see cref="GhostGen_IntStruct"/> via the
        /// <c>targetGameObject</c> optional parameter. The buffer is appended to the child's primary entity
        /// (ownership stays with the child's baker), but the override applies to the root.
        /// </summary>
        internal class CrossTargetServerOnlyConverter : TestNetCodeAuthoring.IConverter
        {
            public GameObject RootTarget;

            public void Bake(GameObject gameObject, IBaker baker)
            {
                baker.DependsOn(RootTarget);
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                var overrides = baker.AddBuffer<GhostVariantBakedOverride>(entity);
                overrides.AppendPrefabTypeOverride(typeof(GhostGen_IntStruct), GhostPrefabType.Server,
                    targetGameObject: RootTarget);
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description(@"Cross-targeting test: a baker running on a child GameObject sets PrefabType=Server on the
ROOT entity's component (via the targetGameObject parameter). The buffer lives on the child's primary
entity but the override must take effect on the root — server keeps it, client strips it.")]
        public void BakerOverride_CrossTargetingRootFromChild()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var root = new GameObject("CrossTargetRoot");
            root.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
            var child = new GameObject("Child");
            child.transform.parent = root.transform;
            child.AddComponent<TestNetCodeAuthoring>().Converter = new CrossTargetServerOnlyConverter
            {
                RootTarget = root,
            };
            var authoring = root.AddComponent<GhostAuthoringComponent>();
            authoring.DefaultGhostMode = GhostMode.OwnerPredicted;
            authoring.SupportedGhostModes = GhostModeMask.All;

            Assert.IsTrue(testWorld.CreateGhostCollection(root));
            testWorld.CreateWorlds(true, 1);

            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Server keeps the root component (PrefabType.Server includes server).
            var serverColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
            var serverPrefab = testWorld.ServerWorld.EntityManager
                .GetBuffer<NetCodeTestPrefab>(serverColl)[0].Value;
            Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<GhostGen_IntStruct>(serverPrefab),
                "Cross-targeted PrefabType.Server should leave root component on server.");

            // Client should have the root component stripped — proving the child's override actually retargeted the root.
            var clientColl = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ClientWorlds[0]);
            var clientPrefab = testWorld.ClientWorlds[0].EntityManager
                .GetBuffer<NetCodeTestPrefab>(clientColl)[0].Value;
            Assert.IsFalse(testWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGen_IntStruct>(clientPrefab),
                "Cross-targeted PrefabType.Server (set by child baker, retargeting root) should strip the root component on client.");
        }

        [Test]
        [Description(@"Calling any AppendXxxOverride helper twice on the same DynamicBuffer for the same
(component, target) tuple must throw InvalidOperationException — duplicates would be silently dropped by the
aggregator and would mask user-baker bugs.")]
        public void AppendOverride_DuplicateForSameComponent_Throws()
        {
            using var world = new World(nameof(AppendOverride_DuplicateForSameComponent_Throws));
            var em = world.EntityManager;
            var entity = em.CreateEntity();
            var buffer = em.AddBuffer<GhostVariantBakedOverride>(entity);

            // First append succeeds.
            buffer.AppendDontSerializeOverride(typeof(GhostGen_IntStruct));
            Assert.AreEqual(1, buffer.Length);

            // Second append for the SAME component on the SAME (default) target must throw.
            Assert.Throws<InvalidOperationException>(
                () => buffer.AppendPrefabTypeOverride(typeof(GhostGen_IntStruct), GhostPrefabType.Server),
                "Second AppendXxxOverride for the same (component, target) should throw.");
            Assert.AreEqual(1, buffer.Length, "Buffer must not have grown after the failed append.");
        }

        [Test]
        [Description(@"The duplicate-detection rule keys on (component, targetGameObject, targetEntitySerial) —
not on component alone. Appending the same component twice with DIFFERENT targets is legitimate and must
succeed.")]
        public void AppendOverride_DifferentTargets_AreNotDuplicates()
        {
            using var world = new World(nameof(AppendOverride_DifferentTargets_AreNotDuplicates));
            var em = world.EntityManager;
            var entity = em.CreateEntity();
            var buffer = em.AddBuffer<GhostVariantBakedOverride>(entity);

            var sideTarget = new GameObject("SideTarget");
            try
            {
                buffer.AppendDontSerializeOverride(typeof(GhostGen_IntStruct));
                buffer.AppendDontSerializeOverride(typeof(GhostGen_IntStruct), targetGameObject: sideTarget);
                Assert.AreEqual(2, buffer.Length,
                    "Two appends for the same component but different targets must both succeed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sideTarget);
            }
        }
    }
}
