using System;
using System.Collections.Generic;
using Unity.Entities.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    // TODO: Undo/redo is broken in the Editor.
    // TODO: Support copy/paste individual meta datas + main components.
    // TODO: Support multi-object-edit.

    /// <summary>UIToolkit drawer for <see cref="GhostAuthoringInspectionComponent"/>.</summary>
    [CustomEditor(typeof(GhostAuthoringInspectionComponent))]
    class GhostAuthoringInspectionComponentEditor : UnityEditor.Editor
    {
        const string k_ExpandKey = "NetCode.Inspection.Expand.";
        const string k_PackageId = "Packages/com.unity.netcode";
        const string k_AutoBakeKey = "AutoBake";

        // Entities ships one icon set per skin. Picking the wrong set gives us a near-invisible icon.
        const string k_EntitiesIconRoot = "Packages/com.unity.entities/Editor Default Resources/icons/";
        const string k_PrefabEntityIconDark = k_EntitiesIconRoot + "dark/Entity/EntityPrefab.png";
        const string k_PrefabEntityIconLight = k_EntitiesIconRoot + "light/Entity/EntityPrefab.png";
        const string k_ComponentIconDark = k_EntitiesIconRoot + "dark/Components/Component.png";
        const string k_ComponentIconLight = k_EntitiesIconRoot + "light/Components/Component.png";

        // TODO - Manually loaded prefabs as uss is not working.
        static Texture2D PrefabEntityIcon => AssetDatabase.LoadAssetAtPath<Texture2D>(EditorGUIUtility.isProSkin ? k_PrefabEntityIconDark : k_PrefabEntityIconLight);
        static Texture2D ComponentIcon => AssetDatabase.LoadAssetAtPath<Texture2D>(EditorGUIUtility.isProSkin ? k_ComponentIconDark : k_ComponentIconLight);

        const string k_StyleSheetPath = k_PackageId + "/Editor/Authoring/GhostAuthoringEditor.uss";
        const string k_VariablesDarkPath = k_PackageId + "/Editor/Authoring/ghost-authoring-vars-dark.uss";
        const string k_VariablesLightPath = k_PackageId + "/Editor/Authoring/ghost-authoring-vars-light.uss";

        const string k_ReplicatedIconClass = "ghost-inspection-replicated-icon";
        const string k_NonReplicatedIconClass = "ghost-inspection-non-replicated-icon";
        const string k_OverrideBarClass = "ghost-inspection-override-bar";
        const string k_PrefabTypeToggleClass = "ghost-inspection-prefabtype-toggle";
        const string k_PrefabTypeToggleStrippedClass = "ghost-inspection-prefabtype-toggle--stripped";
        const string k_BrokenClass = "ghost-inspection-broken";
        const string k_TooltipHostClass = "ghost-inspection-tooltip-host";

        // Tooltip markup is a string parsed by the text engine, so USS cannot style it directly.
        static readonly CustomStyleProperty<Color> k_TooltipMutedProperty = new CustomStyleProperty<Color>("--ghost-inspection-tooltip-muted");
        static readonly CustomStyleProperty<Color> k_TooltipHighlightProperty = new CustomStyleProperty<Color>("--ghost-inspection-tooltip-highlight");
        static readonly CustomStyleProperty<Color> k_TooltipPositiveProperty = new CustomStyleProperty<Color>("--ghost-inspection-tooltip-positive");
        static readonly CustomStyleProperty<Color> k_TooltipNegativeProperty = new CustomStyleProperty<Color>("--ghost-inspection-tooltip-negative");

        internal static EntityPrefabComponentsPreview prefabPreview { get; private set; }
        internal static readonly Dictionary<GhostAuthoringInspectionComponent, BakedResult> cachedBakedResults = new (4);
        internal static bool isPrefabEditable { get; private set; }
        internal static bool hasCachedBakingResult => cachedBakedResults.ContainsKey(inspection);
        internal static GhostAuthoringInspectionComponent inspection { get; private set; }

        VisualElement m_Root;
        VisualElement m_ResultsPane;

        HelpBox m_UnableToFindComponentHelpBox;
        HelpBox m_NoEntityHelpBox;
        private Toggle m_AutoBakeToggle;
        private Button m_BakeButton;
        private int m_NumComponentsOnThisInspection;


        void OnEnable()
        {
            inspection = target as GhostAuthoringInspectionComponent;

            isPrefabEditable = GhostAuthoringComponentEditor.IsPrefabEditable(inspection.gameObject);
            EditorApplication.update += OnUpdate;
            Undo.undoRedoPerformed += RequestRebuildInspector;
            m_NumComponentsOnThisInspection = EntityPrefabComponentsPreview.CountComponents(inspection.gameObject);
        }

        void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            Undo.undoRedoPerformed -= RequestRebuildInspector;
        }

        void OnUpdate()
        {
            inspection = target as GhostAuthoringInspectionComponent;
            if (m_AutoBakeToggle == null || !inspection)
                return;

            // Check for changes:
            if (TryGetEntitiesAssociatedWithAuthoringGameObject(out var bakedGameObjectResult))
            {
                var hasChanged = bakedGameObjectResult.NumComponents != m_NumComponentsOnThisInspection;
                if (hasChanged)
                {
                    bakedGameObjectResult.NumComponents = m_NumComponentsOnThisInspection;

                    if(m_AutoBakeToggle.value)
                        GhostAuthoringInspectionComponent.forceBake = true;
                }
            }

            if (GhostAuthoringInspectionComponent.forceBake && !EditorGUIUtility.editingTextField)
                BakeNetCodePrefab();

            if (GhostAuthoringInspectionComponent.forceSave)
            {
                GhostAuthoringInspectionComponent.forceSave = false;
                GhostAuthoringInspectionComponent.forceRebuildInspector = true;

                EditorSceneManager.MarkSceneDirty(inspection.gameObject.scene);
                Array.Sort(inspection.ComponentOverrides);
                EditorUtility.SetDirty(inspection);
            }

            if (GhostAuthoringInspectionComponent.forceRebuildInspector)
                RebuildWindow();
        }

        internal bool TryGetEntitiesAssociatedWithAuthoringGameObject(out BakedGameObjectResult result)
        {
            if (TryGetBakedResultAssociatedWithAuthoringGameObject(out var bakedResult))
            {
                result = bakedResult.GetInspectionResult(inspection);
                return result != null;
            }

            result = default;
            return false;
        }

        internal bool TryGetBakedResultAssociatedWithAuthoringGameObject(out BakedResult result)
        {
            if (cachedBakedResults.TryGetValue(inspection, out result))
            {
                return true;
            }

            if (GhostAuthoringInspectionComponent.forceBake && !EditorGUIUtility.editingTextField)
            {
                BakeNetCodePrefab();
                if (cachedBakedResults.TryGetValue(inspection, out result))
                {
                    return true;
                }
            }
            return false;
        }

        public void BakeNetCodePrefab()
        {
            var ghostAuthoring = FindRootGhostAuthoringComponent();

            // These allow interop with GhostAuthoringInspectionComponentEditor.
            prefabPreview = new EntityPrefabComponentsPreview();

            try
            {
                prefabPreview.BakeEntireNetcodePrefab(ghostAuthoring, inspection, cachedBakedResults);
            }
            catch
            {
                cachedBakedResults.Remove(inspection);
                throw;
            }
        }

        private static BaseGhostSettings FindRootGhostAuthoringComponent()
        {
            var ghostAuthoring = inspection.GetComponent<BaseGhostSettings>()
                                 ?? PrefabUtility.GetNearestPrefabInstanceRoot(inspection)?.GetComponent<BaseGhostSettings>()
                                 ?? inspection.transform.root.GetComponent<BaseGhostSettings>();
            return ghostAuthoring;
        }

        static void RequestRebuildInspector() => GhostAuthoringInspectionComponent.forceRebuildInspector = true;

        public override VisualElement CreateInspectorGUI()
        {
            if (m_Root != null) return m_Root;

            inspection = target as GhostAuthoringInspectionComponent;

            m_Root = new VisualElement();
            m_Root.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
            m_Root.style.flexShrink = 1;

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_StyleSheetPath);
            if (!styleSheet) return m_Root;
            m_Root.styleSheets.Add(styleSheet);

            var styleSheetVariables = AssetDatabase.LoadAssetAtPath<StyleSheet>(EditorGUIUtility.isProSkin ? k_VariablesDarkPath : k_VariablesLightPath);
            if (styleSheetVariables)
            {
                m_Root.styleSheets.Add(styleSheetVariables);
            }

            m_BakeButton = new Button(HandleBakeButtonClicked);
            m_BakeButton.name = "RefreshButton";
            m_BakeButton.text = "Refresh";
            m_BakeButton.tooltip = "Trigger this prefab to be baked, allowing you to view and edit netcode-related settings on a per-entity and per-component basis.";
            m_BakeButton.style.height = 32;
            m_Root.Add(m_BakeButton);

            m_AutoBakeToggle = new Toggle("Auto-Refresh");
            m_AutoBakeToggle.name = "Auto-Refresh Toggle";
            m_AutoBakeToggle.value = GetShouldExpand(k_AutoBakeKey, true);
            m_AutoBakeToggle.RegisterValueChangedCallback(evt => HandleAutoBakeValueChanged());
            HandleAutoBakeValueChanged();
            m_AutoBakeToggle.tooltip = "When enabled, Unity will automatically bake the selected prefab the first time automatically every time it changes. Disableable as it's a slow operation. Your preference is saved locally.";
            m_Root.Add(m_AutoBakeToggle);

            m_UnableToFindComponentHelpBox = new HelpBox($"Unable to find associated {nameof(GhostAuthoringComponent)} or {nameof(GhostObject)} in root or parent. " +
                                                         $"Either ensure it exists, or remove this component.", HelpBoxMessageType.Error);
            m_Root.Add(m_UnableToFindComponentHelpBox);

            m_NoEntityHelpBox = new HelpBox($"This GameObject does not create any Entities during baking.", HelpBoxMessageType.Info);
            m_Root.Add(m_NoEntityHelpBox);

            // TODO - Support edge-case where user adds an override to a type and then disables it in code.
            // TODO - Explicitly support changing variant but not anything else if the user does not add the `[SupportPrefabOverrides]` attribute.

            m_ResultsPane = new VisualElement();
            m_ResultsPane.name = "ResultsPane";

            m_Root.Add(m_ResultsPane);

            RebuildWindow();

            return m_Root;
        }

        /// <summary>
        /// Builds an element's tooltip only when it is about to be shown, rather than up front.
        /// </summary>
        static void RegisterTooltip(VisualElement element, Func<VisualElement, string> buildTooltip)
        {
            element.AddToClassList(k_TooltipHostClass);
            element.RegisterCallback<TooltipEvent>(evt =>
            {
                if (evt.currentTarget is not VisualElement target)
                {
                    return;
                }

                evt.rect = target.worldBound;
                evt.tooltip = buildTooltip(target);
                evt.StopImmediatePropagation();
            });
        }

        static string Colorize(VisualElement element, CustomStyleProperty<Color> property, string text)
        {
            return element.customStyle.TryGetValue(property, out var color)
                ? $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>"
                : text;
        }

        private void HandleAutoBakeValueChanged()
        {
            SetShouldExpand(k_AutoBakeKey, m_AutoBakeToggle.value);
            SetVisualElementVisibility(m_BakeButton, !m_AutoBakeToggle.value);

            if (m_AutoBakeToggle.value && !hasCachedBakingResult)
                GhostAuthoringInspectionComponent.forceBake = true;
        }

        private void HandleBakeButtonClicked()
        {
            GhostAuthoringInspectionComponent.forceBake = true;
        }

        internal void RebuildWindow()
        {
            inspection = target as GhostAuthoringInspectionComponent;

            if (m_Root == null) CreateInspectorGUI();
            GhostAuthoringInspectionComponent.forceRebuildInspector = false;
            m_ResultsPane.Clear();

            var hasEntitiesForThisGameObject = TryGetEntitiesAssociatedWithAuthoringGameObject(out var bakedGameObjectResult);
            SetVisualElementVisibility(m_UnableToFindComponentHelpBox, hasEntitiesForThisGameObject && !bakedGameObjectResult.SourceGameObject);
            SetVisualElementVisibility(m_NoEntityHelpBox, hasEntitiesForThisGameObject && bakedGameObjectResult.BakedEntities.Count == 0);

            var isEditable = hasCachedBakingResult && isPrefabEditable;
            m_ResultsPane.SetEnabled(isEditable);

            if (!hasEntitiesForThisGameObject)
                return;

            for (var entityIndex = 0; entityIndex < bakedGameObjectResult.BakedEntities.Count; entityIndex++)
            {
                const int arbitraryMaxNumAdditionalEntitiesWeCanDisplay = 20;
                if (entityIndex > arbitraryMaxNumAdditionalEntitiesWeCanDisplay + 1)
                {
                    m_ResultsPane.Add(new HelpBox($"Authoring GameObject '{bakedGameObjectResult.SourceGameObject.name}' creates {bakedGameObjectResult.BakedEntities.Count} \"Additional\" entities ({(bakedGameObjectResult.BakedEntities.Count - entityIndex)} are hidden)." +
                                                  " For performance reasons, we cannot display this many." +
                                                  " If you must add a ComponentOverride for an additional entity, please attempt to do so by modifying the YAML directly. ", HelpBoxMessageType.Warning));
                    break;
                }

                var bakedEntityResult = bakedGameObjectResult.BakedEntities[entityIndex];
                var entityHeader = new FoldoutHeaderElement("EntityLabel", bakedEntityResult.EntityName,
                    $"[{bakedEntityResult.Guid}] {(bakedEntityResult.EntityIndex + 1)} / {bakedGameObjectResult.BakedEntities.Count}",
                    "Displays the entity or entities created during Baking of this GameObject.", false);

                entityHeader.AddToClassList("ghost-inspection-entity-header");
                entityHeader.style.marginLeft = 10;
                //entityLabel.label.AddToClassList("ghost-inspection-entity-header__label");
                entityHeader.icon.AddToClassList("ghost-inspection-entity-header__icon");
                entityHeader.icon.style.backgroundImage = PrefabEntityIcon;
                entityHeader.foldout.text += (bakedEntityResult.IsPrimaryEntity) ? " (Primary)" : " (Additional)";
                m_ResultsPane.Add(entityHeader);

                var allComponents = bakedEntityResult.BakedComponents;
                var replicated = new List<BakedComponentItem>(allComponents.Count);
                var nonReplicated = new List<BakedComponentItem>(allComponents.Count);
                foreach (var component in allComponents)
                {
                    if (component.anyVariantIsSerialized)
                        replicated.Add(component);
                    else nonReplicated.Add(component);
                }

                var toggleKey = bakedEntityResult.Guid.ToString();
                var replicatedContainer = CreateReplicationHeaderElement(entityHeader.foldout.contentContainer, replicated,
                    "ReplicatedLabel", "Meta-data for GhostComponents", "Lists all netcode meta-data for replicated (i.e. synced) component types.",
                    k_ReplicatedIconClass, true, toggleKey);

                // Prefer default variants:
                if (bakedEntityResult.GoParent.SourceInspection.ComponentOverrides.Length > 0)
                {
                    replicatedContainer.contentContainer.Add(
                        new HelpBox($"If you intend to use one Variant across <b>all Ghosts</b> (e.g. `Translation - 2D` for a 2D game), prefer to set it as the \"Default Variant\" by implementing `RegisterDefaultVariants` " +
                                    "in your own system (derived from `DefaultVariantSystemBase`), rather than using these controls. " +
                                    "You can also set defaults for `GhostPrefabTypes` via the `GhostComponentAttribute`.", HelpBoxMessageType.Info));
                }
                else
                {
                    m_ResultsPane.Add(
                        new HelpBox($"Note that this Inspection Component is optional. As you haven't made any overrides, you can safely remove this component.", HelpBoxMessageType.Info));
                }

                // Warn about replicating child components:
                if (!bakedEntityResult.IsRoot)
                {
                    foreach (var item in replicated)
                    {
                        if (item.serializationStrategy.IsSerialized != 0)
                        {
                            replicatedContainer.contentContainer.Add(new HelpBox("Note: Serializing child entities is relatively slow. " +
                                                                                 "Prefer to have multiple Ghosts with faked parenting, if possible.", HelpBoxMessageType.Warning));
                            break;
                        }
                    }
                }

                CreateReplicationHeaderElement(entityHeader.foldout.contentContainer, nonReplicated,
                    "NonReplicatedLabel", "Meta-data for non-replicated Components", "Lists all netcode meta-data for non-replicated component types.",
                    k_NonReplicatedIconClass, false, toggleKey);
            }

            // Display invalid overrides:
            foreach (var componentOverride in inspection.ComponentOverrides)
            {
                if (!componentOverride.DidCorrectlyMap)
                {//.
                    var title = new HelpBox("Detected duplicated or otherwise invalid serialized 'Component Overrides'! You can remove them by pressing the buttons below.", HelpBoxMessageType.Error);
                    title.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(FontStyle.Bold);
                    title.style.overflow = new StyleEnum<Overflow>(Overflow.Visible);
                    m_ResultsPane.Add(title);

                    //.
                    foreach (var @override in inspection.ComponentOverrides)
                    {
                        if (@override.DidCorrectlyMap) { continue; }

                        var button = new Button();

                        void RemoveOverride()
                        {
                            if (inspection.TryFindExistingOverrideIndex(@override.FullTypeName, @override.EntityIndex, out var foundIndex))
                            {
                                inspection.RemoveComponentOverrideByIndex(foundIndex);
                            }
                            else
                            {
                                UnityEngine.Debug.LogError($"Unable to remove ComponentOverride {@override}, as now can no longer find it in the list!");
                            }
                            m_ResultsPane.Remove(button);
                            GhostAuthoringInspectionComponent.forceSave = true;
                        }
                        button.clicked += RemoveOverride;
                        button.name = "ComponentOverrideError";
                        button.text = $"{@override.FullTypeName} [Entity {@override.EntityIndex}]\nPrefab Type [{@override.PrefabType}]\nSend Optimization [{GetNameForGhostSendType(@override.SendTypeOptimization)}]\nVariant [{@override.VariantHash}]\n<i>Click to remove.</i>";
                        button.AddToClassList(k_BrokenClass);
                        button.style.flexGrow = 1;
                        m_ResultsPane.Add(button);
                    }
                    break;
                }
            }
        }

        static void SetVisualElementVisibility(VisualElement visualElement, bool visibleCondition)
        {
            visualElement.style.display = new StyleEnum<DisplayStyle>(visibleCondition ? DisplayStyle.Flex : DisplayStyle.None);
        }

        VisualElement CreateReplicationHeaderElement(VisualElement parentContent, List<BakedComponentItem> bakedComponents, string headerName, string title, string tooltip, string iconTintClass, bool isReplicated, string toggleKey)
        {
            var header = new FoldoutHeaderElement(headerName, title, $"{bakedComponents.Count}", tooltip, true);
            header.AddToClassList("ghost-inspection-replication-header");
            //header.label.AddToClassList("ghost-inspection-replication-header");
            header.icon.AddToClassList("ghost-inspection-entity-header__icon");
            header.icon.AddToClassList(iconTintClass);
            header.icon.style.backgroundImage = ComponentIcon;
            parentContent.Add(header);

            var componentListView = new VisualElement();
            componentListView.AddToClassList("ghost-inspection-entity-content");
            header.foldout.contentContainer.Add(componentListView);

            if (bakedComponents.Count > 0)
            {
                for (var i = 0; i < bakedComponents.Count; i++)
                {
                    var metaData = bakedComponents[i];
                    var metaDataRootElement = CreateMetaDataInspector(metaData);
                    componentListView.Add(metaDataRootElement);
                }
            }
            else
            {
                header.SetEnabled(false);
            }


            toggleKey += (isReplicated ? ".RepToggle" : ".NonRepToggle");
            header.foldout.RegisterCallback<ClickEvent>(OnFoldoutToggled);
            void OnFoldoutToggled(ClickEvent evt)
            {
                SetShouldExpand(toggleKey, header.foldout.value);
            }
            var shouldExpandFoldout = GetShouldExpand(toggleKey, bakedComponents.Count > 0 && isReplicated);
            header.foldout.SetValueWithoutNotify(shouldExpandFoldout);

            return componentListView;
        }

        VisualElement CreateMetaDataInspector(BakedComponentItem bakedComponent)
        {
            static OverrideTracking CreateOverrideTracking(BakedComponentItem bakedComponentItem, VisualElement insertIntoOverrideTracking)
            {
                return new OverrideTracking("MetaDataInspector", insertIntoOverrideTracking, bakedComponentItem.HasPrefabOverride(),
                    "Reset Entire Component", bakedComponentItem.RemoveEntirePrefabOverride, true);
            }

            if (bakedComponent.anyVariantIsSerialized || bakedComponent.HasMultipleVariantsExcludingDontSerializeVariant)
            {
                var componentMetaDataFoldout = new Foldout();
                componentMetaDataFoldout.name = "ComponentMetaDataFoldout";
                componentMetaDataFoldout.text = bakedComponent.managedType.Name;
                componentMetaDataFoldout.style.alignContent = new StyleEnum<Align>(Align.Center);
                componentMetaDataFoldout.style.marginBottom = 3;
                componentMetaDataFoldout.style.flexShrink = 1;
                componentMetaDataFoldout.SetValueWithoutNotify(true);
                componentMetaDataFoldout.focusable = false;

                var toggle = componentMetaDataFoldout.Q<Toggle>();
                toggle.style.flexShrink = 1;
                toggle.style.marginLeft = 0; // Don't -12px.
                var foldoutLabel = toggle.Q<Label>(className: UssClasses.UIToolkit.Toggle.Text); // TODO - DropdownField should expose!
                LabelStyle(foldoutLabel);
                var checkmark = toggle.Q<VisualElement>(className: UssClasses.UIToolkit.Toggle.Checkmark);
                checkmark.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);

                var toggleChild = toggle.Q<VisualElement>(className: UssClasses.UIToolkit.BaseField.Input); // TODO - DropdownField should expose!;
                InsertGhostModeToggles(bakedComponent, toggleChild);

                var sendToOwnerDropdown = CreateSentToOwnerDropdown(bakedComponent);
                componentMetaDataFoldout.Add(sendToOwnerDropdown);

                var sendOptimizationDropdown = CreateSentOptimizationDropdown(bakedComponent);
                componentMetaDataFoldout.Add(sendOptimizationDropdown);

                var variantDropdown = CreateVariantDropdown(bakedComponent);
                variantDropdown.SetEnabled(bakedComponent.DoesAllowVariantModification);
                componentMetaDataFoldout.Add(variantDropdown);

                if (bakedComponent.serializationStrategy.IsInput != 0)
                {
                    var inputComponent = new HelpBox("Sending inputs is handled automatically. These settings denote how a clients inputs are replicated to other clients (e.g. to improve prediction of other players).", HelpBoxMessageType.Info);
                    componentMetaDataFoldout.Add(inputComponent);
                }

                var parent = foldoutLabel.parent;
                var parentIndex = foldoutLabel.parent.IndexOf(foldoutLabel);
                var overrideTracking = CreateOverrideTracking(bakedComponent, foldoutLabel);
                parent.Insert(parentIndex, overrideTracking);
                return componentMetaDataFoldout;
            }

            var componentMetaDataLabel = new Label();
            InsertGhostModeToggles(bakedComponent, componentMetaDataLabel);
            componentMetaDataLabel.name = "ComponentMetaDataLabel";
            componentMetaDataLabel.text = bakedComponent.managedType.Name;
            componentMetaDataLabel.style.alignSelf = new StyleEnum<Align>(Align.Stretch);
            componentMetaDataLabel.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            // TODO - The text here doesn't clip properly because the buttons are CHILDREN of the label. I.e. The buttons are INSIDE the labels rect.
            LabelStyle(componentMetaDataLabel);

            return CreateOverrideTracking(bakedComponent, componentMetaDataLabel);
        }

        static bool GetShouldExpand(string key, bool defaultValue) => EditorPrefs.GetBool(k_ExpandKey + key, defaultValue);

        static void SetShouldExpand(string key, bool value) => EditorPrefs.SetBool(k_ExpandKey + key, value);

        static void LabelStyle(Label label)
        {
            label.style.flexShrink = 1;
            label.style.minWidth = 1;
            label.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
        }

        static VisualElement CreateVariantDropdown(BakedComponentItem bakedComponent)
        {
            var dropdown = new DropdownField
            {
                name = "VariantDropdownField",
                label = "Variant",
            };

            RegisterTooltip(dropdown, BuildTooltip);

            string BuildTooltip(VisualElement element)
            {
                var tooltip = @"Variants change how a components fields are serialized (i.e. replicated).
Use this dropdown to select which variant is used on this component (on this specific ghost entity, and thus; ghost type).

Note that:

 - <b>Components added to the root entity</b> will default to the ""Default Serializer"" (the serializer generated by the SourceGenerators), unless you have modified the default (via a `DefaultVariantSystemBase` derived system).

 - <b>Components added to child (and additional) entities</b> will default to the `DontSerializeVariant` global variant because serializing children involves entity memory random-access, which is expensive.
 
 - <b>Bakers can also contribute overrides</b> by appending to a `GhostVariantBakedOverride` buffer at baking time. Options labeled ""(Baked Default)"" reflect a baker's choice. An inspection component override (set via this dropdown, saved to this prefab) always takes precedence over a baker override.
 
 - <b>GhostObject (GameObject) prefabs apply their own defaults for some components</b> (e.g. 3D scale replication via `PostTransformMatrix`), applied as per-prefab overrides at registration time. Options labeled ""(GhostObject Default)"" reflect these. An inspection component override always takes precedence.
  ";

                if (!bakedComponent.DoesAllowVariantModification)
                    tooltip += "\n\n" + Colorize(element, k_TooltipMutedProperty, "This dropdown is currently disabled as either a) this type has a [DontSupportPrefabOverrides] attribute or b) there are no other variants.");

                // Per-component baker contribution detail, when present. The "supersedes" warning is field-specific:
                // overrides merge field-by-field, so an inspection-set PrefabType does NOT supersede a baker's variant.
                if (bakedComponent.BakerContributedOverrides != null && bakedComponent.BakerContributedOverrides.Count > 0)
                {
                    var hasInspection = bakedComponent.HasPrefabOverride();
                    var inspectionVariantOverridden = hasInspection && bakedComponent.GetPrefabOverride().IsVariantOverriden;
                    var inspectionPrefabTypeOverridden = hasInspection && bakedComponent.GetPrefabOverride().IsPrefabTypeOverriden;
                    var inspectionSendTypeOverridden = hasInspection && bakedComponent.GetPrefabOverride().IsSendTypeOptimizationOverriden;

                    foreach (var ov in bakedComponent.BakerContributedOverrides)
                    {
                        var fields = new List<string>(3);
                        var supersededFields = new List<string>(3);
                        if (ov.VariantHash != 0)
                        {
                            var name = LookupVariantDisplayName(bakedComponent, ov.VariantHash);
                            fields.Add($"Variant <b>{name}</b>");
                            if (inspectionVariantOverridden) supersededFields.Add("Variant");
                        }
                        if (ov.PrefabType != GhostVariantBakedOverride.NoPrefabTypeOverride)
                        {
                            fields.Add($"PrefabType <b>{ov.PrefabType}</b>");
                            if (inspectionPrefabTypeOverridden) supersededFields.Add("PrefabType");
                        }
                        if (ov.SendTypeOptimization != GhostVariantBakedOverride.NoSendTypeOverride)
                        {
                            fields.Add($"SendType <b>{GetNameForGhostSendType(ov.SendTypeOptimization)}</b>");
                            if (inspectionSendTypeOverridden) supersededFields.Add("SendType");
                        }

                        var summary = fields.Count > 0 ? string.Join(", ", fields) : "(no fields set)";
                        tooltip += $"\n\nA baker requests: {summary}.";
                        if (supersededFields.Count > 0)
                            tooltip += "\n" + Colorize(element, k_TooltipHighlightProperty, $"An inspection-component override above supersedes the baker's {string.Join(" / ", supersededFields)}.");
                    }
                }

                return tooltip;
            }

            DropdownStyle(dropdown);

            // Suffix the dropdown choice that matches the context-contributed variant (a baker override, or the
            // GameObject-layer default on GhostObject prefabs) so users can see which option is picked for them.
            var contextVariantHash = bakedComponent.ContextDefaultVariantHash;
            var contextVariantTag = bakedComponent.BakerContributedVariantHash != 0 ? " (Baked Default)" : " (GhostObject Default)";
            for (var i = 0; i < bakedComponent.availableSerializationStrategies.Length; i++)
            {
                var displayName = bakedComponent.availableSerializationStrategyDisplayNames[i];
                if (contextVariantHash != 0 && bakedComponent.availableSerializationStrategies[i].Hash == contextVariantHash)
                    displayName += contextVariantTag;
                dropdown.choices.Add(displayName);
            }

            // Set current value: prefer inspection-resolved serializationStrategy; if no inspection variant
            // override was set but a context-contributed variant exists, show that variant as the current value.
            {
                var displayHash = bakedComponent.serializationStrategy.Hash;
                if (!bakedComponent.HasPrefabOverride() || !bakedComponent.GetPrefabOverride().IsVariantOverriden)
                {
                    if (contextVariantHash != 0)
                        displayHash = contextVariantHash;
                }

                var index = Array.FindIndex(bakedComponent.availableSerializationStrategies, x => x.Hash == displayHash);
                if (index >= 0)
                {
                    var selectedVariantName = dropdown.choices[index];
                    dropdown.SetValueWithoutNotify(selectedVariantName);
                }
                else
                {
                    dropdown.SetValueWithoutNotify($"!! Unknown Variant Hash {displayHash} !! (Fallback: {bakedComponent.serializationStrategy.DisplayName.ToString()})");
                    dropdown.AddToClassList(k_BrokenClass);
                }
            }

            // Handle value changed. Use dropdown.choices (which carries any " (Baked Default)" suffixes), not the
            // raw availableSerializationStrategyDisplayNames, so suffixed entries match.
            dropdown.RegisterValueChangedCallback(evt =>
            {
                var indexOf = dropdown.choices.IndexOf(evt.newValue);
                if (indexOf >= 0)
                {
                    bakedComponent.serializationStrategy = bakedComponent.availableSerializationStrategies[indexOf];
                    bakedComponent.SaveVariant(false, false);
                    // Clear the "broken variant" highlight so we fall back to the skin defaults.
                    dropdown.RemoveFromClassList(k_BrokenClass);
                }
                else
                {
                    Debug.LogError($"Unable to find variant `{evt.newValue}` to select it! Keeping existing! Try modifying this prefabs YAML.");
                }
            });

            var isOverridenFromDefault = bakedComponent.HasPrefabOverride() && bakedComponent.GetPrefabOverride().IsVariantOverriden;
            var overrideTracking = new OverrideTracking("VariantDropdown", dropdown, isOverridenFromDefault, "Reset Variant", x => bakedComponent.ResetVariantToDefault(), true);
            return overrideTracking;
        }

        static string LookupVariantDisplayName(BakedComponentItem item, ulong variantHash)
        {
            for (int i = 0; i < item.availableSerializationStrategies.Length; i++)
            {
                if (item.availableSerializationStrategies[i].Hash == variantHash)
                    return item.availableSerializationStrategyDisplayNames[i];
            }
            return $"(unknown variant, hash {variantHash})";
        }

        /// <summary>Visualizes prefab overrides for custom controls attached to this.</summary>
        class OverrideTracking : VisualElement
        {
            /// <summary>The UI element wrapping the <see cref="ChildRenderingElement"/>, allowing flex-direction:Horizontal.</summary>
            public VisualElement ChildContainer;
            /// <summary>The custom, unknown UI element that we're wrapping this override tracking around.</summary>
            public VisualElement ChildRenderingElement;
            /// <summary>The override widget itself.</summary>
            public VisualElement Override;

            public OverrideTracking(string prefabType, VisualElement mainField, bool defaultOverride, string rightClickResetTitle, Action<DropdownMenuAction> rightClickResetAction, bool shrink)
            {
                name = $"{prefabType}OverrideTracking";
                style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column); // Ensure the override is BELOW the ChildRenderingElement widget.
                style.alignSelf = new StyleEnum<Align>(Align.Stretch);
                style.alignItems = new StyleEnum<Align>(Align.Center);
                style.flexGrow = 0;
                style.flexShrink = shrink ? 1 : 0;
                style.marginLeft = 5;

                mainField.style.flexGrow = 1;
                mainField.style.flexShrink = 1;
                mainField.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
                Add(mainField);

                Override = new VisualElement
                {
                    name = nameof(Override),
                };
                Override.style.height = Override.style.maxHeight = 2;
                Override.style.minWidth = 35;
                Override.style.paddingLeft = Override.style.paddingRight = 2;
                Override.style.paddingTop = Override.style.paddingBottom = 1;

                Override.style.flexGrow = 1;
                Override.style.flexShrink = 1;
                Override.style.alignSelf = new StyleEnum<Align>(Align.Stretch);
                Override.AddToClassList(k_OverrideBarClass);
                Add(Override);

                if (defaultOverride)
                {
                    this.AddManipulator(new ContextualMenuManipulator(evt =>
                    {
                        evt.menu.AppendAction(rightClickResetTitle, rightClickResetAction);
                    }));
                }
                SetOverride(defaultOverride);
            }

            void SetOverride(bool isDefaultOverride)
            {
                Override.style.display = new StyleEnum<DisplayStyle>(isDefaultOverride ? DisplayStyle.Flex : DisplayStyle.None);
                Override.MarkDirtyRepaint();
            }
        }

        static VisualElement CreateSentOptimizationDropdown(BakedComponentItem bakedComponent)
        {
            var doesAllowSendTypeOptimizationModification = bakedComponent.DoesAllowSendTypeOptimizationModification;

            var dropdown = new DropdownField();
            dropdown.name = "SendToDropdownField";
            dropdown.label = "Send Optimization";

            dropdown.SetEnabled(doesAllowSendTypeOptimizationModification);
            RegisterTooltip(dropdown, BuildTooltip);

            DropdownStyle(dropdown);

            if (doesAllowSendTypeOptimizationModification)
            {
                dropdown.choices.Add(GetNameForGhostSendType(GhostSendType.DontSend));
                dropdown.choices.Add(GetNameForGhostSendType(GhostSendType.OnlyPredictedClients));
                dropdown.choices.Add(GetNameForGhostSendType(GhostSendType.OnlyInterpolatedClients));
                dropdown.choices.Add(GetNameForGhostSendType(GhostSendType.AllClients));
                dropdown.RegisterValueChangedCallback(OnSendToChanged);
            }

            UpdateUi(GetNameForGhostSendType(bakedComponent.SendTypeOptimization));

            // Handle value changed.
            void OnSendToChanged(ChangeEvent<string> evt)
            {
                var flag = GetFlagForGhostSendTypeOptimization(evt.newValue);
                bakedComponent.SetSendTypeOptimization(flag);
                UpdateUi(evt.newValue);
            }

            void UpdateUi(string buttonValue)
            {
                dropdown.value = doesAllowSendTypeOptimizationModification || bakedComponent.serializationStrategy.IsSerialized != 0 ? buttonValue : "n/a";
                dropdown.MarkDirtyRepaint();
            }

            string BuildTooltip(VisualElement element)
            {
                var tooltip = $"Optimization that allows you to specify whether or not the server should send (i.e. replicate) the `{bakedComponent.fullname}` component to client ghosts, " +
                    "depending on whether or not a given client is Predicting or Interpolating this ghost." +
                    "\n\nExample: Only send the `PhysicsVelocity` component for \"known always predicted\" ghosts, as interpolated ghosts don't ever need to read the `PhysicsVelocity` Component." +
                    $"\n\nNote: This optimization is only possible when we can infer the GhostMode at compile time: I.e. When the {nameof(GhostAuthoringComponent)} or {nameof(GhostObject)} has `OwnerPredicted` selected, or when `SupportedGhostModes` is set to either `Interpolated` or `Predicted` (but not both).";

                tooltip += "\n\n" + Colorize(element, k_TooltipHighlightProperty, $"The current setting means that {GetTooltipForGhostSendType(bakedComponent.SendTypeOptimization)}");
                if (!doesAllowSendTypeOptimizationModification)
                    tooltip += "\n\n" + Colorize(element, k_TooltipMutedProperty, "This dropdown is currently disabled as either a) this type has a [DontSupportPrefabOverrides] attribute or b) we cannot infer GhostMode.");
                tooltip += "\n\nOther send rules may still apply. See documentation for further details.";

                return tooltip;
            }

            var isOverridenFromDefault = bakedComponent.HasPrefabOverride() && bakedComponent.GetPrefabOverride().IsSendTypeOptimizationOverriden;
            var overrideTracking = new OverrideTracking("SendToDropdown", dropdown, isOverridenFromDefault, "Reset SendType Override", bakedComponent.ResetSendTypeToDefault, true);
            return overrideTracking;
        }

        static VisualElement CreateSentToOwnerDropdown(BakedComponentItem bakedComponent)
        {
            var dropdown = new DropdownField();
            dropdown.name = "SendToDropdownField";
            dropdown.label = "Send To Owner";
            dropdown.tooltip = "<b>Only modifiable via attribute.</b>\n\nDenotes which clients will receive snapshot updates containing this component.\n\nOther send rules may still apply. See documentation for further details.";
            dropdown.SetEnabled(false);
            dropdown.SetValueWithoutNotify(bakedComponent.sendToOwnerType.ToString());
            DropdownStyle(dropdown);

            const bool isOverridenFromDefault = false;
            var overrideTracking = new OverrideTracking("SendToDropdown", dropdown, isOverridenFromDefault, "Reset SendType Override", bakedComponent.ResetSendTypeToDefault, true);
            return overrideTracking;
        }

        static void DropdownStyle(DropdownField dropdownRoot)
        {
            // Root:
            dropdownRoot.style.alignSelf = new StyleEnum<Align>(Align.Stretch);
            dropdownRoot.style.flexGrow = 0;
            dropdownRoot.style.flexShrink = 1;

            // Label:
            var label = dropdownRoot.Q<Label>();
            label.style.alignSelf = new StyleEnum<Align>(Align.Stretch);
            label.style.flexGrow = 0;
            label.style.flexShrink = 1;
            label.style.minWidth = 75;
            label.style.width = 110;

            // Dropdown widget:
            var dropdownWidget = dropdownRoot.Q<VisualElement>(className: UssClasses.UIToolkit.BaseField.Input); // TODO - DropdownField should expose!
            dropdownWidget.style.flexGrow = 1;
            dropdownWidget.style.flexShrink = 1;
            dropdownWidget.style.minWidth = 75;
            dropdownWidget.style.width = 100;
        }

        static string GetTooltipForGhostSendType(GhostSendType ghostSendType)
        {
            switch (ghostSendType)
            {
                case GhostSendType.DontSend: return "this component will <b>not</b> be replicated ever, regardless of what `GhostPrefabType` each ghost is in.";
                case GhostSendType.OnlyInterpolatedClients:return "this component will <b>only</b> be replicated for <b>Interpolated Ghosts</b>.";
                case GhostSendType.OnlyPredictedClients: return "this component will <b>only</b> be replicated for <b>Predicted Ghosts</b>.";
                case GhostSendType.AllClients: return "this component <b>will</b> be replicated for both `Predicted` and `Interpolated` Ghosts.";
                default:
                    throw new ArgumentOutOfRangeException(nameof(ghostSendType), ghostSendType, null);
            }
        }

        static string GetNameForGhostSendType(GhostSendType ghostSendType)
        {
            if((int)ghostSendType == -1)
                return "not set";
            switch (ghostSendType)
            {
                case GhostSendType.DontSend: return "Never Send";
                case GhostSendType.AllClients: return "Always Send";
                case GhostSendType.OnlyInterpolatedClients: return "Only Send when \"Known Interpolated\"";
                case GhostSendType.OnlyPredictedClients: return "Only Send when \"Known Predicted\"";
                default:
                    throw new ArgumentOutOfRangeException(nameof(ghostSendType), ghostSendType, null);
            }
        }
        static GhostSendType GetFlagForGhostSendTypeOptimization(string ghostSendType)
        {
            for (var type = GhostSendType.DontSend; type <= GhostSendType.AllClients; type++)
            {
                var testName = GetNameForGhostSendType(type);
                if (string.Equals(testName, ghostSendType, StringComparison.OrdinalIgnoreCase))
                    return type;
            }

            throw new ArgumentOutOfRangeException(nameof(ghostSendType), ghostSendType, nameof(GetFlagForGhostSendTypeOptimization));
        }

        void InsertGhostModeToggles(BakedComponentItem bakedComponent, VisualElement parent)
        {
            parent.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            parent.style.flexShrink = 1;

            var separator = new VisualElement();
            separator.name = nameof(separator);
            separator.style.flexGrow = 1;
            separator.style.flexShrink = 1;
            parent.Add(separator);

            var buttonContainer = new VisualElement();
            buttonContainer.name = "GhostPrefabTypeButtons";
            buttonContainer.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
            buttonContainer.SetEnabled(bakedComponent.DoesAllowPrefabTypeModification);

            // we only allow component stripping on baked ghosts. GhostObject ghosts should use content selection, which should strip the GhostBehaviour too.
            buttonContainer.visible = bakedComponent.EntityParent.GoParent.RootAuthoring is GhostAuthoringComponent;

            buttonContainer.Add(CreateButton("S",  GhostPrefabType.Server, "Server"));
            buttonContainer.Add(CreateButton("IC", GhostPrefabType.InterpolatedClient, "Interpolated Client"));
            buttonContainer.Add(CreateButton("PC", GhostPrefabType.PredictedClient, "Predicted Client"));

            var isOverridenFromDefault = bakedComponent.HasPrefabOverride() && bakedComponent.GetPrefabOverride().IsPrefabTypeOverriden;
            var overrideTracking = new OverrideTracking("PrefabType", buttonContainer, isOverridenFromDefault, $"Reset PrefabType Override", bakedComponent.ResetPrefabTypeToDefault, false);

            parent.Add(overrideTracking);

            VisualElement CreateButton(string abbreviation, GhostPrefabType type, string prefabType)
            {
                var button = new Button();

                button.text = abbreviation;
                button.style.width = 36;
                button.style.height = 22;
                button.style.marginLeft = 1;
                button.style.marginRight = 1;
                button.style.paddingLeft = 1;
                button.style.paddingRight = 1;

                button.style.alignContent = new StyleEnum<Align>(Align.Center);
                button.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                button.AddToClassList(k_PrefabTypeToggleClass);
                RegisterTooltip(button, BuildTooltip);

                UpdateUi();

                button.clicked += ButtonToggled;

                void ButtonToggled()
                {
                    bakedComponent.TogglePrefabType(type);
                    UpdateUi();
                }
                void UpdateUi()
                {
                    button.EnableInClassList(k_PrefabTypeToggleStrippedClass, (bakedComponent.PrefabType & type) == 0);
                    button.MarkDirtyRepaint();
                }

                string BuildTooltip(VisualElement element)
                {
                    var strategyDefaultPrefabType = bakedComponent.defaultSerializationStrategy.PrefabType;
                    var bakerPrefabType = bakedComponent.BakerContributedPrefabType;
                    var bakerOverridesDefault = bakerPrefabType != GhostVariantBakedOverride.NoPrefabTypeOverride
                                              && bakerPrefabType != strategyDefaultPrefabType;
                    var effectiveDefault = bakerOverridesDefault ? bakerPrefabType : strategyDefaultPrefabType;
                    var defaultValue = (effectiveDefault & type) != 0;
                    var isSet = (bakedComponent.PrefabType & type) != 0;

                    var currentValue = isSet
                        ? Colorize(element, k_TooltipPositiveProperty, "YES")
                        : Colorize(element, k_TooltipNegativeProperty, "NO");

                    var tooltip = $"NetCode creates multiple versions of the '{bakedComponent.EntityParent.EntityName}' ghost prefab (one for each mode [Server, Interpolated Client, PredictedClient])." +
                        $"\n\nThis toggle determines if the `{bakedComponent.fullname}` component should be added to the `{prefabType}` version of this ghost." +
                        $" Current value indicates {currentValue} and thus {Colorize(element, k_TooltipHighlightProperty, $"PrefabType is `{bakedComponent.PrefabType}`")}." +
                        $"\n\nDefault value is: {(defaultValue ? "YES" : "NO")}";

                    if (bakerOverridesDefault)
                        tooltip += " " + Colorize(element, k_TooltipHighlightProperty, $"(Baker Default: a baker has set PrefabType to `{bakerPrefabType}`, overriding the strategy default of `{strategyDefaultPrefabType}`. An inspection override on this toggle would supersede the baker.)");

                    tooltip += "\n\nTo disable write-access to this toggle, add a `DontSupportPrefabOverrides` attribute to your component type." +
                        "\n\nRecommendation: It's better practice to create a custom Variant that sets the desired `PrefabType`. This way, said `PrefabType` will be applied automatically to all ghost prefabs.";

                    if (!bakedComponent.DoesAllowPrefabTypeModification)
                        tooltip += "\n\n" + Colorize(element, k_TooltipMutedProperty, "This dropdown is currently disabled as this type has a [DontSupportPrefabOverrides] attribute.");

                    return tooltip;
                }
                return button;
            }
        }

        class FoldoutHeaderElement : VisualElement
        {
            public readonly Foldout foldout;
            //public readonly Label label;
            public readonly Image icon;
            public readonly VisualElement rowHeader;

            public FoldoutHeaderElement(string headerName, string labelText, string lengthText, string subElementsTooltip, bool displayCheckmark)
            {
                name = $"{headerName}FoldoutHeader";

                foldout = new Foldout();
                foldout.name = $"{headerName}Foldout";
                foldout.text = labelText;
                foldout.contentContainer.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Column);
                Add(foldout);

                var toggle = foldout.Q<Toggle>();
                toggle.tooltip = subElementsTooltip;
                foldout.focusable = false;

                var checkmark = toggle.Q<VisualElement>(className: UssClasses.UIToolkit.Toggle.Checkmark);
                checkmark.style.display = new StyleEnum<DisplayStyle>(displayCheckmark ? DisplayStyle.Flex : DisplayStyle.None);

                icon = new Image();
                icon.name = $"{headerName}Icon";
                icon.tooltip = subElementsTooltip;
                icon.AddToClassList("entity-info__icon");

                rowHeader = toggle[0];
                rowHeader.style.alignItems = new StyleEnum<Align>(Align.Center);
                rowHeader.style.marginTop = new StyleLength(3);
                rowHeader.style.height = new StyleLength(20);
                rowHeader.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(FontStyle.Bold);
                rowHeader.Insert(1, icon);

                var lengthLabel = new Label();
                lengthLabel.name = $"{headerName}LengthLabel";
                lengthLabel.style.flexGrow = new StyleFloat(1);
                lengthLabel.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(FontStyle.Normal);
                lengthLabel.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.MiddleRight);
                lengthLabel.style.justifyContent = new StyleEnum<Justify>(Justify.FlexEnd);
                lengthLabel.style.alignContent = new StyleEnum<Align>(Align.FlexEnd);
                lengthLabel.text = lengthText;
                rowHeader.Add(lengthLabel);
            }
        }
    }
}
