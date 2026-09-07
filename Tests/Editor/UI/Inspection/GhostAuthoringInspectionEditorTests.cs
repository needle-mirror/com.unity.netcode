using NUnit.Framework;
using Unity.Entities;
using Unity.Netcode.Tests;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using Object = UnityEngine.Object;

namespace Unity.Netcode.Editor.Tests.UI.Inspection
{
    /// <summary>
    /// A tag-style component with no <see cref="GhostFieldAttribute"/>s. The default serializer for it is
    /// non-serializing, which used to confuse the inspector when a <see cref="GhostComponentVariationAttribute"/>
    /// was added on top.
    /// </summary>
    /// <remarks>
    /// Public because <see cref="GhostComponentVariationAttribute"/> requires the targeted component type to
    /// be publicly accessible.
    /// </remarks>
    public struct NonReplicatedInspectionTag : IComponentData
    {
    }

    /// <summary>
    /// A custom variant for <see cref="NonReplicatedInspectionTag"/>. It also has no serialized fields, so
    /// selecting it should still produce a saved <c>ComponentOverride</c> (regression for: variant on a
    /// non-replicated component).
    /// </summary>
    [GhostComponentVariation(typeof(NonReplicatedInspectionTag), nameof(NonReplicatedInspectionTagVariantA))]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
    public struct NonReplicatedInspectionTagVariantA
    {
    }

    /// <summary>
    /// A second variant for <see cref="NonReplicatedInspectionTag"/>. The inspector only renders the variant
    /// dropdown when there are at least two non-<c>DontSerialize</c> strategies, and source gen does not
    /// emit a default serializer for a tag component with no <see cref="GhostFieldAttribute"/>s, so we need
    /// two custom variants to make the dropdown appear.
    /// </summary>
    [GhostComponentVariation(typeof(NonReplicatedInspectionTag), nameof(NonReplicatedInspectionTagVariantB))]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
    public struct NonReplicatedInspectionTagVariantB
    {
    }

    /// <summary>
    /// UITK tests for <see cref="GhostAuthoringInspectionComponentEditor"/>. Each test bakes the prefab
    /// through the real <see cref="EntityPrefabComponentsPreview"/> pipeline (via the editor's
    /// <see cref="GhostAuthoringInspectionComponentEditor.BakeNetCodePrefab"/> entry-point), then asserts
    /// against the actual VisualElement tree the editor builds.
    /// </summary>
    /// <remarks>
    /// The editor uses several static fields (<c>inspection</c>, <c>cachedBakedResults</c>,
    /// <c>forceBake</c>/<c>forceSave</c>/<c>forceRebuildInspector</c>). SetUp/TearDown reset them to keep
    /// tests independent.
    /// </remarks>
    class GhostAuthoringInspectionEditorTests : UITestFixture
    {
        /// <summary>
        /// Adds a replicated component (<see cref="GhostGen_IntStruct"/>) and a non-replicated tag
        /// (<see cref="NonReplicatedInspectionTag"/>) so the inspector renders both a "Meta-data for
        /// GhostComponents" section and a "Meta-data for non-replicated" section, with the tag exposing a
        /// variant dropdown thanks to <see cref="NonReplicatedInspectionTagVariantA"/> and
        /// <see cref="NonReplicatedInspectionTagVariantB"/>.
        /// </summary>
        class GhostConverter : TestNetCodeAuthoring.IConverter
        {
            public void Bake(GameObject gameObject, IBaker baker)
            {
                var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
                baker.AddComponent(entity, new GhostOwner { NetworkId = -1 });
                baker.AddComponent(entity, new GhostGen_IntStruct());
                baker.AddComponent(entity, new NonReplicatedInspectionTag());
            }
        }

        GameObject m_GhostRoot;
        GhostAuthoringInspectionComponent m_Inspection;
        GhostAuthoringInspectionComponentEditor m_Editor;
        VisualElement m_InspectorRoot;
        /// <summary>Set when a test wrote a prefab asset under <see cref="NetCodeTestWorld.k_GeneratedFolderBasePath"/>.
        /// The synchronous <c>NetCodeTestWorld.Dispose</c> does not clean that folder (only <c>DisposeAsync</c> does), and
        /// a leftover ghost prefab there would auto-register itself in later tests.</summary>
        bool m_WroteGeneratedPrefab;

        [SetUp]
        public void SetUp()
        {
            ResetEditorStatics();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Editor != null)
            {
                Object.DestroyImmediate(m_Editor); // fires OnDisable, unhooks EditorApplication.update
                m_Editor = null;
            }
            if (m_GhostRoot != null)
            {
                Object.DestroyImmediate(m_GhostRoot);
                m_GhostRoot = null;
            }
            m_Inspection = null;
            m_InspectorRoot = null;
            if (m_WroteGeneratedPrefab)
            {
                m_WroteGeneratedPrefab = false;
                AssetDatabase.DeleteAsset(NetCodeTestWorld.k_GeneratedFolderBasePath.TrimEnd('/'));
                AssetDatabase.Refresh();
            }
            ResetEditorStatics();
            rootVisualElement.Clear();
        }

        static void ResetEditorStatics()
        {
            GhostAuthoringInspectionComponentEditor.cachedBakedResults.Clear();
            GhostAuthoringInspectionComponent.forceBake = false;
            GhostAuthoringInspectionComponent.forceSave = false;
            GhostAuthoringInspectionComponent.forceRebuildInspector = false;
        }

        /// <summary>
        /// Builds a single-root ghost GameObject with both a replicated and a non-replicated component,
        /// creates the inspector editor, runs the bake, and forces the inspector to rebuild against the
        /// baked data. Any pre-existing <c>ComponentOverrides</c> are written before the bake so the
        /// inspector sees them.
        /// </summary>
        void CreateAndBakeInspector(GhostAuthoringInspectionComponent.ComponentOverride[] overrides = null)
        {
            using var _ = new NetCodeTestWorld(); // useful to setup/teardown global test states, even if not using worlds
            m_GhostRoot = new GameObject("InspectorTestGhost");
            m_GhostRoot.AddComponent<TestNetCodeAuthoring>().Converter = new GhostConverter();
            var auth = m_GhostRoot.AddComponent<GhostAuthoringComponent>();
            auth.SupportedGhostModes = GhostModeMask.All;
            auth.DefaultGhostMode = GhostMode.OwnerPredicted;
            // The baker's `String.IsNullOrEmpty(prefabId)` check rejects in-memory GameObjects.
            auth.prefabId = NetCodeTestWorld.NewFakePrefabId();

            m_Inspection = m_GhostRoot.AddComponent<GhostAuthoringInspectionComponent>();
            if (overrides != null)
                m_Inspection.ComponentOverrides = overrides;

            m_Editor = (GhostAuthoringInspectionComponentEditor)UnityEditor.Editor.CreateEditor(m_Inspection);
            m_InspectorRoot = m_Editor.CreateInspectorGUI();
            rootVisualElement.Add(m_InspectorRoot);
            simulate.FrameUpdate();

            m_Editor.BakeNetCodePrefab();
            m_Editor.RebuildWindow();
            simulate.FrameUpdate();
        }

        /// <summary>
        /// Finds the per-component foldout for a given managed type by walking down to the Toggle's label
        /// text. <c>CreateMetaDataInspector</c> names the foldout "ComponentMetaDataFoldout" and sets the
        /// text to the type's <c>Name</c>.
        /// </summary>
        Foldout FindComponentFoldout(System.Type managedType)
        {
            foreach (var foldout in m_InspectorRoot.Query<Foldout>(name: "ComponentMetaDataFoldout").ToList())
            {
                if (foldout.text == managedType.Name)
                    return foldout;
            }
            return null;
        }

        [Test]
        public void Inspector_BakesAndShowsBothReplicatedAndNonReplicatedSections()
        {
            CreateAndBakeInspector();

            // Both section foldouts are created by CreateReplicationHeaderElement.
            var replicated = m_InspectorRoot.Q(name: "ReplicatedLabelFoldoutHeader");
            var nonReplicated = m_InspectorRoot.Q(name: "NonReplicatedLabelFoldoutHeader");
            Assert.That(replicated, Is.Not.Null, "Replicated section should be present after bake.");
            Assert.That(nonReplicated, Is.Not.Null, "Non-replicated section should be present after bake.");

            // The replicated GhostGen_IntStruct should have a per-component foldout (it has GhostFields).
            Assert.That(FindComponentFoldout(typeof(GhostGen_IntStruct)), Is.Not.Null,
                "Expected a foldout for the replicated GhostGen_IntStruct component.");
        }

        [Test]
        public void VariantDropdown_OnNonReplicatedComponent_IsRenderedBecauseOfVariation()
        {
            CreateAndBakeInspector();

            // NonReplicatedInspectionTag has no GhostFields, but two custom variants are declared so
            // HasMultipleVariantsExcludingDontSerializeVariant is true and CreateMetaDataInspector
            // renders the full foldout with the variant dropdown.
            var foldout = FindComponentFoldout(typeof(NonReplicatedInspectionTag));
            Assert.That(foldout, Is.Not.Null,
                "Expected a foldout for the non-replicated tag because it has a [GhostComponentVariation].");

            var variantDropdown = foldout.Q<DropdownField>(name: "VariantDropdownField");
            Assert.That(variantDropdown, Is.Not.Null, "Variant dropdown should be rendered.");
            Assert.That(variantDropdown.choices.Count, Is.GreaterThanOrEqualTo(2),
                "Dropdown should list both the default serializer and the custom variant.");
        }

        /// <summary>
        /// Regression: selecting a variant for a component with no <see cref="GhostFieldAttribute"/>s used
        /// to drop the override because <c>SavePrefabOverride</c> saw <c>HasOverriden==false</c>. The
        /// override must be saved with the variant hash AND the <c>FullTypeName</c> populated.
        /// </summary>
        [Test]
        public void Inspector_SelectingVariant_OnNonReplicatedComponent_PersistsOverride()
        {
            CreateAndBakeInspector();

            var foldout = FindComponentFoldout(typeof(NonReplicatedInspectionTag));
            var variantDropdown = foldout.Q<DropdownField>(name: "VariantDropdownField");

            // Pick the option that isn't currently selected — guaranteed to be the custom variant when
            // exactly two strategies are available.
            string targetChoice = null;
            foreach (var choice in variantDropdown.choices)
            {
                if (choice != variantDropdown.value)
                {
                    targetChoice = choice;
                    break;
                }
            }
            Assert.That(targetChoice, Is.Not.Null, "Expected at least one non-default variant choice.");

            variantDropdown.value = targetChoice;
            simulate.FrameUpdate();

            Assert.That(m_Inspection.ComponentOverrides.Length, Is.EqualTo(1),
                "Selecting a variant on a non-replicated component must persist a ComponentOverride.");
            ref var saved = ref m_Inspection.ComponentOverrides[0];
            Assert.That(saved.FullTypeName, Is.EqualTo(typeof(NonReplicatedInspectionTag).FullName),
                "FullTypeName must be set so the override can be re-resolved on next load.");
            Assert.That(saved.VariantHash, Is.Not.EqualTo(0UL),
                "VariantHash must be non-zero for a non-default variant.");
        }

        /// <summary>
        /// Regression for d884e7a1 (auto-remove half): a <c>ComponentOverride</c> targeting a missing or
        /// renamed type gets logged AND removed during bake (via <c>LogErrorIfComponentOverrideIsInvalid</c>
        /// called from the baker).
        /// </summary>
        [Test]
        public void Inspector_OverrideForUnknownType_IsRemovedDuringBake()
        {
            var unknown = new[]
            {
                new GhostAuthoringInspectionComponent.ComponentOverride
                {
                    FullTypeName = "Unity.Netcode.Tests.NonExistentType_RemovedDuringRename",
                    EntityIndex = 0,
                    PrefabType = GhostPrefabType.Server,
                    SendTypeOptimization = GhostSendType.AllClients,
                    VariantHash = 0,
                },
            };

            UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("invalid 'Component Override'"));
            CreateAndBakeInspector(unknown);

            Assert.That(m_Inspection.ComponentOverrides.Length, Is.EqualTo(0),
                "Override for an unknown type should be removed by the baker's validate pass.");
        }

        /// <summary>
        /// Common operation: changing the Send-Optimization dropdown writes a SendType override.
        /// </summary>
        /// <remarks>
        /// There are TWO controls named "SendToDropdownField" per component (one for SendToOwner, one for
        /// SendOptimization); we look up by label to disambiguate.
        /// </remarks>
        [Test]
        public void Inspector_ChangingSendOptimization_CreatesOverride()
        {
            CreateAndBakeInspector();

            var foldout = FindComponentFoldout(typeof(GhostGen_IntStruct));
            DropdownField sendDropdown = null;
            foreach (var dd in foldout.Query<DropdownField>(name: "SendToDropdownField").ToList())
            {
                if (dd.label == "Send Optimization")
                {
                    sendDropdown = dd;
                    break;
                }
            }
            Assert.That(sendDropdown, Is.Not.Null, "Send Optimization dropdown should exist.");
            Assert.That(sendDropdown.enabledSelf, Is.True,
                "Send-optimization dropdown should be enabled for a replicated component on an OwnerPredicted ghost.");

            string target = null;
            foreach (var choice in sendDropdown.choices)
            {
                if (choice != sendDropdown.value)
                {
                    target = choice;
                    break;
                }
            }
            Assert.That(target, Is.Not.Null);
            sendDropdown.value = target;
            simulate.FrameUpdate();

            Assert.That(m_Inspection.ComponentOverrides.Length, Is.EqualTo(1));
            Assert.That(m_Inspection.ComponentOverrides[0].IsSendTypeOptimizationOverriden, Is.True);
        }

        /// <summary>
        /// Check that a <see cref="GhostObject"/> instance that was instantiated by an already registered prefab works.
        /// The inspection must be displayed from its existing prefab entity and not re-baked with the runtime instance.
        /// </summary>
        [Test]
        public void Inspector_RegisteredGhostObjectPrefab_ReusesRegisteredEntityInsteadOfBaking()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true);
            testWorld.CreateWorlds(server: true, numClients: 0);

            m_WroteGeneratedPrefab = true;

            var go = new GameObject("NestedBehaviour");
            // Disable the prefab to prevent Awake from triggering during initialization and logging null refs
            go.AddComponent<GhostObject>();
            go.AddComponent<TestMoveCube>();
            go.AddComponent<GhostAuthoringInspectionComponent>();

            var prefab = SubSceneHelper.CreatePrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, go);
            Netcode.RegisterPrefab(prefab);

            // Re-enable the prefab, this should trigger auto-registration

            var serverWorld = testWorld.ServerWorld;
            var registeredEntity = prefab.EntityExt(isPrefab: true, serverWorld.Unmanaged);
            Assert.That(registeredEntity, Is.Not.EqualTo(Entity.Null), "Sanity check failed: the prefab should be registered in the server world.");

            var worldCountBeforeBake = World.All.Count;

            m_Inspection = prefab.GetComponent<GhostAuthoringInspectionComponent>();
            m_Editor = (GhostAuthoringInspectionComponentEditor)UnityEditor.Editor.CreateEditor(m_Inspection);
            m_InspectorRoot = m_Editor.CreateInspectorGUI();
            rootVisualElement.Add(m_InspectorRoot);
            simulate.FrameUpdate();

            m_Editor.BakeNetCodePrefab();
            m_Editor.RebuildWindow();
            simulate.FrameUpdate();

            Assert.That(World.All.Count, Is.EqualTo(worldCountBeforeBake), "No preview world should be created when the prefab is already registered.");

            Assert.That(GhostAuthoringInspectionComponentEditor.cachedBakedResults.TryGetValue(m_Inspection, out var bakedResult), Is.True,
                "Expected a baked result for the registered prefab.");

            var goResult = bakedResult.GetInspectionResult(m_Inspection);
            Assert.That(goResult, Is.Not.Null);
            Assert.That(goResult.BakedEntities.Count, Is.EqualTo(1), "The root GameObject should map to exactly one entity.");
            Assert.That(goResult.BakedEntities[0].Entity, Is.EqualTo(registeredEntity),
                "The displayed entity must be the one created during prefab registration.");

            // The prefab's netcode settings are frozen once registered, so the overrides must be read-only.
            var resultsPane = m_InspectorRoot.Q(name: "ResultsPane");
            Assert.That(resultsPane, Is.Not.Null);
            Assert.That(resultsPane.enabledSelf, Is.False, "Overrides must not be editable while displaying a live registered prefab.");
        }

        /// <summary>
        /// GhostObject with nested GhostBehaviour works and renders as expected.
        /// </summary>
        [Test]
        public void Inspector_HandlesGhostObjectWithChildGhostBehaviour()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(includeNetCodeSystems: true);
            testWorld.CreateWorlds(server: true, numClients: 0);

            m_WroteGeneratedPrefab = true;
            var root = new GameObject("InspectorNestedGhostObject");
            root.SetActive(false);
            root.AddComponent<GhostObject>();
            root.AddComponent<GhostAuthoringInspectionComponent>();

            // The nested GhostBehaviour lives on the child only — the child has no GhostObject of its own.
            var childSource = new GameObject("NestedBehaviour");
            childSource.transform.SetParent(root.transform);
            childSource.AddComponent<TestMoveCube>();

            var prefab = SubSceneHelper.CreatePrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, root);
            Netcode.RegisterPrefab(prefab);

            var registeredEntity = prefab.EntityExt(isPrefab: true, testWorld.ServerWorld.Unmanaged);
            Assert.That(registeredEntity, Is.Not.EqualTo(Entity.Null), "Sanity check failed: the prefab should be registered in the server world.");

            var child = prefab.transform.GetChild(0).gameObject;
            Assert.That(child.GetComponent<TestMoveCube>().Ghost, Is.SameAs(prefab.GetComponent<GhostObject>()), "Sanity check failed: Nested GhostBehaviour should have been linked to the root GhostObject during prefab registration.");

            m_Inspection = prefab.GetComponent<GhostAuthoringInspectionComponent>();
            m_Editor = (GhostAuthoringInspectionComponentEditor)UnityEditor.Editor.CreateEditor(m_Inspection);
            m_InspectorRoot = m_Editor.CreateInspectorGUI();
            rootVisualElement.Add(m_InspectorRoot);
            simulate.FrameUpdate();

            m_Editor.BakeNetCodePrefab();
            m_Editor.RebuildWindow();
            simulate.FrameUpdate();

            Assert.That(GhostAuthoringInspectionComponentEditor.cachedBakedResults.TryGetValue(m_Inspection, out var bakedResult), Is.True);

            var rootResult = bakedResult.GameObjectResults[prefab];
            Assert.That(rootResult.BakedEntities.Count, Is.EqualTo(1), "The root maps to the registered prefab entity.");
            Assert.That(rootResult.BakedEntities[0].Entity, Is.EqualTo(registeredEntity));

            // The child is skipped entirely, so it never gets a BakedGameObjectResult.
            Assert.That(bakedResult.GameObjectResults.ContainsKey(child), Is.False, "A child of a GhostObject must not be baked as its own GameObject: a GhostObject ghost is a single entity.");
            Assert.That(bakedResult.GameObjectResults.Count, Is.EqualTo(1), "Only the root GhostObject should have been baked.");
            Assert.That(child.EntityExt(isPrefab: true, testWorld.ServerWorld.Unmanaged), Is.EqualTo(Entity.Null), "A child GhostBehaviour must not get its own prefab entity.");
        }
    }
}
