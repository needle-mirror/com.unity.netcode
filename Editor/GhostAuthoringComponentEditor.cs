using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    [CustomEditor(typeof(GhostAuthoringComponent))]
    public class GhostAuthoringComponentEditor : UnityEditor.Editor
    {
        public struct SerializedComponentData
        {
            public string name;
            public Type managedType;
            public GhostComponentAttribute attribute;
            public GhostFieldModifier[] fields;
            public int entityIndex;
            public GameObject gameObject;

            public bool isExpanded;
        }
        SerializedComponentData[] Components;

        SerializedProperty DefaultGhostMode;
        SerializedProperty SupportedGhostModes;
        SerializedProperty OptimizationMode;
        SerializedProperty Importance;
        SerializedProperty Name;
        private Vector2 scrollPosition = Vector2.zero;
        private List<GhostAuthoringComponent.ComponentOverride> ComponentOverrides;

        private const int ComponentScrollViewPixelsHeigth = 400;

        void OnEnable()
        {
            var authoring = (GhostAuthoringComponent) target;
            DefaultGhostMode = serializedObject.FindProperty("DefaultGhostMode");
            SupportedGhostModes = serializedObject.FindProperty("SupportedGhostModes");
            OptimizationMode = serializedObject.FindProperty("OptimizationMode");
            Importance = serializedObject.FindProperty("Importance");
            Name = serializedObject.FindProperty("Name");
            if (Name.stringValue == "")
                Name.stringValue = target.name;
            Name.stringValue = Name.stringValue.Replace(" ", String.Empty);
            Components = null;
            ComponentOverrides = authoring.ComponentOverrides;
        }

        void ShowField(GhostFieldModifier field)
        {
            EditorGUILayout.TextField("Name", field.name);
            ++EditorGUI.indentLevel;
            EditorGUILayout.IntField("Quantization", field.attribute.Quantization);
            // EditorGUILayout.Toggle("Interpolate", field.attribute.Interpolate);
            EditorGUILayout.EnumPopup("Interpolation Mode", SmoothingAction.Clamp);
            --EditorGUI.indentLevel;
        }

        /// <summary>
        /// </summary>
        /// <param name="fullTypeName"></param>
        /// <param name="gameObject"></param>
        /// <param name="modifier"></param>
        /// <returns>The index of the modifier and valid modifier instance. -1 if nothing is found. </returns>
        int GetPrefabModifier(string fullTypeName, GameObject gameObject, out GhostAuthoringComponent.ComponentOverride modifier)
        {
            for (int i = 0; i < ComponentOverrides.Count; ++i)
            {
                var element = ComponentOverrides[i];
                if (element.fullTypeName == fullTypeName &&
                    element.gameObject == gameObject)
                {
                    modifier = element;
                    return i;
                }
            }

            modifier = null;
            return -1;
        }

        private GhostAuthoringComponent.ComponentOverride AddModifier(string fullTypeName, GameObject gameObject)
        {
            var modifier = new GhostAuthoringComponent.ComponentOverride
            {
                gameObject = gameObject,
                fullTypeName = fullTypeName,
            };
            ComponentOverrides.Add(modifier);
            return modifier;
        }

        private ulong VariantPopup(SerializedComponentData comp, ulong variantHash)
        {
            if (!GhostAuthoringModifiers.VariantsCache.TryGetValue(comp.managedType.FullName, out var list))
                return 0;

            int index = 0;
            if (variantHash != 0)
            {
                index = list.FindIndex(v => v.Attribute.VariantHash == variantHash);
                if (index < 0)
                {
                    Debug.LogWarning($"Variation with hash {variantHash} not found.");
                    index = 0;
                }
            }
            var names = list.Select(i => i.Attribute.DisplayName).ToArray();
            var idx = EditorGUILayout.Popup(new GUIContent("Serialization Variant"),  index, names);
            GUI.changed |= idx != index;
            return list[idx].Attribute.VariantHash;
        }

        void ShowComponent(ref SerializedComponentData comp)
        {
            GUIStyle style = new GUIStyle(EditorStyles.foldoutHeader);
            var prefabType = comp.attribute!=null?comp.attribute.PrefabType:GhostPrefabType.All;
            var ownerPredictedSendType = comp.attribute!=null?comp.attribute.OwnerPredictedSendType:GhostSendType.All;
            var sendForChildren = comp.attribute != null ? comp.attribute.SendDataForChildEntity : true;

            ulong variantHash = 0;
            int index = GetPrefabModifier(comp.name, comp.gameObject, out var modifier);
            //Apply prefab modifier only if they are meant to be different than the default.
            if (modifier != null)
            {
                if(modifier.PrefabType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                    prefabType = (GhostPrefabType)modifier.PrefabType;
                if(modifier.OwnerPredictedSendType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                    ownerPredictedSendType = (GhostSendType)modifier.OwnerPredictedSendType;
                if(modifier.SendToChild != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                    sendForChildren = modifier.SendToChild == 0 ? false : true;
                variantHash = modifier.ComponentVariant;
            }
            bool hasDataToSend = comp.fields.Length > 0 && (comp.entityIndex == 0 || sendForChildren);
            style.fontStyle =  modifier == null
                ? hasDataToSend ? FontStyle.Bold : FontStyle.Normal
                : hasDataToSend ? FontStyle.BoldAndItalic : FontStyle.Italic ;
            var text = String.Format("{0}{1} ({2}/{3}/{4}){5}",
                comp.entityIndex != 0 ? "Child " + (comp.entityIndex - 1).ToString() + ": " : "",
                comp.name, (prefabType & GhostPrefabType.Server) != 0 ? "S" : "-",
                (prefabType & GhostPrefabType.InterpolatedClient) != 0 ? "IC" : "-",
                (prefabType & GhostPrefabType.PredictedClient) != 0 ? "PC" : "-",
                modifier != null?"*":"");
            comp.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(comp.isExpanded, text, style);
            var foldoutRect = GUILayoutUtility.GetLastRect();
            bool canModifyComponent = comp.managedType.GetCustomAttribute<DontSupportPrefabOverrides>() == null;
            if (comp.isExpanded)
            {
                var modPrefab = modifier != null && modifier.PrefabType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                var modSendChild = modifier != null && modifier.SendToChild != GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                var modSendType = modifier != null && modifier.OwnerPredictedSendType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                GhostPrefabType newPrefab = prefabType;
                EditorGUI.BeginChangeCheck();
                using (new EditorGUI.DisabledGroupScope(!canModifyComponent))
                {
                    modPrefab = AuthoringEditorHelper.ShowPrefabType(modPrefab, prefabType, ref newPrefab);
                    modSendChild = AuthoringEditorHelper.ShowSendChild(comp, modSendChild, ref sendForChildren);
                    modSendType = AuthoringEditorHelper.ShowSendType(modSendType, ref ownerPredictedSendType);
                    //Changing variant will change the defaults values for the ghost attribute for the type
                    variantHash = VariantPopup(comp, variantHash);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    //If the prefab use default values there is not need to have a modifier. Remove
                    bool useAllDefaults = !modPrefab && !modSendType && !modSendChild && variantHash == 0;
                    if (useAllDefaults && modifier != null)
                    {
                        ComponentOverrides.RemoveAt(index);
                        //Refresh the comp field to the theid default variant version
                        ExtractComponentInfo((target as GhostAuthoringComponent)?.gameObject , ref comp);
                        EditorUtility.SetDirty(target);
                    }
                    else if (!useAllDefaults)
                    {
                        if(modifier == null)
                            modifier = AddModifier(comp.name, comp.gameObject);
                        if (modifier.ComponentVariant != variantHash)
                        {
                            //Refresh the component fields values to reflect the current selected variant
                            modifier.ComponentVariant = variantHash;
                            ExtractComponentInfo((target as GhostAuthoringComponent)?.gameObject , ref comp);
                        }
                        modifier.PrefabType = modPrefab
                            ? (int) newPrefab
                            : GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                        modifier.OwnerPredictedSendType = modSendType
                            ? (int) ownerPredictedSendType
                            : GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                        modifier.SendToChild = modSendChild
                            ? sendForChildren ? 1 : 0
                            : GhostAuthoringComponent.ComponentOverride.UseDefaultValue;
                        EditorUtility.SetDirty(target);
                    }
                }

                if ((prefabType&GhostPrefabType.Server)!=0)
                {
                    EditorGUILayout.Separator();
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.LabelField("Fields");
                    for (int fi = 0; fi < comp.fields.Length; ++fi)
                    {
                        ShowField(comp.fields[fi]);
                    }

                    EditorGUI.EndDisabledGroup();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            //Display context menu when user press the left mouse button that have some shortcuts. Options available:
            // - Remove the component from the ghost (set the prefab to type to 0)
            // - Reset the component to its default (remove the overrides)
            if(canModifyComponent && Event.current.type == EventType.MouseDown && Event.current.button == 1)
            {
                if (foldoutRect.Contains(Event.current.mousePosition))
                {
                    var typeFullName = comp.name;
                    var gameObject = comp.gameObject;
                    //Show context menu with some options
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Remove Component"), false, () =>
                    {
                        //Add modifier if not present
                        if (modifier == null)
                            modifier = AddModifier(typeFullName, gameObject);
                        modifier.PrefabType = 0;
                        EditorUtility.SetDirty(target);
                    });
                    if (modifier != null)
                    {
                        menu.AddItem(new GUIContent("Reset Default"), false, () =>
                        {
                            ComponentOverrides.RemoveAt(index);
                            EditorUtility.SetDirty(target);
                        });
                    }
                    else
                    {
                        menu.AddDisabledItem(new GUIContent("Reset Default"));
                    }
                    menu.ShowAsContext();
                }
            }
        }



        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginDisabledGroup(PrefabUtility.IsPartOfNonAssetPrefabInstance(target));
            EditorGUILayout.PropertyField(Name);
            EditorGUILayout.PropertyField(Importance);

            EditorGUILayout.PropertyField(SupportedGhostModes);
            if (SupportedGhostModes.intValue == (int)GhostAuthoringComponent.GhostModeMask.All)
            {
                var self = (GhostAuthoringComponent)target;
                EditorGUILayout.PropertyField(DefaultGhostMode);
                // Selecting OwnerPredicted on a ghost without a GhostOwnerComponent will cause an exception during conversion - display an error for that case in the inspector
                if (DefaultGhostMode.enumValueIndex == (int)GhostAuthoringComponent.GhostMode.OwnerPredicted && self.gameObject.GetComponent<GhostOwnerComponentAuthoring>() == null)
                {
                    EditorGUILayout.HelpBox("Setting Default Ghost Mode to Owner Predicted requires the ghost to have a Ghost Owner Component", MessageType.Error);
                }
            }
            EditorGUILayout.PropertyField(OptimizationMode);
            EditorGUI.EndDisabledGroup();

            GUILayout.Label("Overrides", "box", GUILayout.ExpandWidth(true));
            if (ComponentOverrides.Count > 0)
            {
                EditorGUI.indentLevel++;
                for (int i = 0; i < ComponentOverrides.Count; ++i)
                {
                    var element = ComponentOverrides[i];
                    ShowOverride(element, i);
                }
                EditorGUI.indentLevel--;
            }

            GUILayout.Label("Components", "box", GUILayout.ExpandWidth(true));
            if (Components != null)
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.MaxHeight(ComponentScrollViewPixelsHeigth));
                EditorGUI.indentLevel++;
                for (int ci = 0; ci < Components.Length; ++ci)
                {
                    ShowComponent(ref Components[ci]);
                }
                EditorGUI.indentLevel--;
                EditorGUILayout.EndScrollView();
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Separator();
            if (GUILayout.Button("Update component list"))
            {
                SyncComponentList(target as GhostAuthoringComponent);
            }
        }

        private void ShowOverride(GhostAuthoringComponent.ComponentOverride element, int overrideIndex)
        {
            var fullTypeName = element.fullTypeName;
            element.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(element.isExpanded, new GUIContent(fullTypeName));
            var rect = GUILayoutUtility.GetLastRect();
            if (element.isExpanded)
            {
                // We don't support more that 1 level of object hierarchy so this check is sufficient to retrieve the
                // entity / child index.
                var transform = element.gameObject.transform;
                var childIndex = transform.parent == null ? 0 : transform.GetSiblingIndex() + 1;
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Entity Index", childIndex.ToString());
                EditorGUILayout.ObjectField("gameObject",element.gameObject, typeof(GameObject), true);
                if(element.PrefabType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                    EditorGUILayout.LabelField("PrefabType", ((GhostPrefabType)element.PrefabType).ToString());
                if(element.OwnerPredictedSendType != GhostAuthoringComponent.ComponentOverride.UseDefaultValue)
                    EditorGUILayout.LabelField("OwnerPredictedSendType", ((GhostSendType)element.OwnerPredictedSendType).ToString());
                if (element.ComponentVariant != 0)
                {
                    var value = element.ComponentVariant;
                    string variantName = null;
                    if (GhostAuthoringModifiers.VariantsCache.TryGetValue(fullTypeName, out var variants))
                        variantName = variants.Where(v => v.Attribute.VariantHash == value).Select(v=>v.Attribute.DisplayName).FirstOrDefault();
                    if (variantName != null)
                        EditorGUILayout.LabelField("Variant", variantName);
                    else
                    {
                        Debug.LogError($"Cannot find variant with hash {value}");
                        EditorGUILayout.LabelField("Variant", "INVALID!!");
                    }
                }
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
            {
                if (rect.Contains(Event.current.mousePosition))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Remove Modifier"), false, () =>
                    {
                        //Add modifier if not present
                        ComponentOverrides.RemoveAt(overrideIndex);
                        EditorUtility.SetDirty(target);
                    });
                    menu.ShowAsContext();
                }
            }
        }

        struct ComponentNameComparer : IComparer<ComponentType>
        {
            public int Compare(ComponentType x, ComponentType y) =>
                x.GetManagedType().FullName.CompareTo(y.GetManagedType().FullName);
        }

        static void FillSubFields(FieldInfo field, GhostFieldAttribute attr, List<GhostFieldModifier> fieldsList, string parentPrefix = "")
        {
            var typeAttribute = new TypeAttribute
            {
                composite = attr.Composite,
                smoothing = (uint)attr.Smoothing,
                quantization = attr.Quantization,
                maxSmoothingDist = attr.MaxSmoothingDistance,
                subtype = attr.SubType
            };

            if (!field.FieldType.IsValueType)
                return;

            if (field.FieldType.IsPrimitive || field.FieldType.IsEnum)
            {
                if (CodeGenTypes.Registry.CanGenerateType(new TypeDescription(field.FieldType, typeAttribute)))
                {
                    fieldsList.Add(new GhostFieldModifier
                    {
                        name = parentPrefix + field.Name,
                        attribute = attr
                    });
                }
                return;
            }
            if (CodeGenTypes.Registry.CanGenerateType(new TypeDescription(field.FieldType, typeAttribute)))
            {
                fieldsList.Add(new GhostFieldModifier
                {
                    name = parentPrefix + field.Name,
                    attribute = attr
                });
                return;
            }
            foreach (var f in field.FieldType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var attributes = f.GetCustomAttributes<GhostFieldAttribute>().ToArray();
                if(attributes.Length > 0 && !attributes[0].SendData)
                    continue;
                FillSubFields(f, attr, fieldsList, $"{parentPrefix+field.Name}.");
            }
        }

        void AddToComponentList(List<SerializedComponentData> newComponents, World tempWorld, Entity convertedEntity, int entityIndex, GameObject gameObject)
        {
            var compTypes = tempWorld.EntityManager.GetComponentTypes(convertedEntity);
            compTypes.Sort(default(ComponentNameComparer));

            for (int i = 0; i < compTypes.Length; ++i)
            {
                var managedType = compTypes[i].GetManagedType();
                if (managedType == typeof(Prefab) || managedType == typeof(LinkedEntityGroup))
                    continue;

                var newComponent = new SerializedComponentData
                {
                    name = managedType.FullName,
                    managedType = managedType,
                    attribute = default,
                    entityIndex = entityIndex,
                    gameObject = gameObject
                };
                ExtractComponentInfo(gameObject, ref newComponent);
                newComponents.Add(newComponent);
            }
        }

        private void ExtractComponentInfo(GameObject gameObject, ref SerializedComponentData componentData)
        {
            var variantType = componentData.managedType;
            int modifierIndex = GetPrefabModifier(componentData.managedType.FullName, gameObject, out var prefabModifier);
            if (modifierIndex >= 0 && prefabModifier.ComponentVariant != 0)
            {
                //Get the variant type from the hash and use that retrieve any modifier (if any) for the current
                //component type
                var variant = GhostAuthoringModifiers.VariantsCache[prefabModifier.fullTypeName]
                    .FirstOrDefault(v => v.Attribute.VariantHash == prefabModifier.ComponentVariant);
                if (variant.Attribute != null && variant.VariantType != null)
                {
                    //Use the variant instead of the original type to how the fields are serialized
                    variantType = variant.VariantType;
                }
                else
                {
                    Debug.LogError(
                        $"Cannot find valid variant with hash {prefabModifier.ComponentVariant} but type has a modifier with that variant set.");
                }
            }

            var hasDefaultComponent = GhostAuthoringModifiers.GhostDefaultOverrides.TryGetValue(variantType.FullName, out var defaultComponent);
            componentData.attribute = default(GhostComponentAttribute);
            if (hasDefaultComponent)
            {
                componentData.attribute = defaultComponent.attribute;
            }
            else
            {
                var compAttr = variantType.GetCustomAttributes<GhostComponentAttribute>().ToArray();
                if (compAttr.Length > 0)
                {
                    componentData.attribute = compAttr[0];
                }
                else
                {
                    componentData.attribute = new GhostComponentAttribute();
                }
            }

            var fields = new List<GhostFieldModifier>();
            foreach (var componentField in variantType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                GhostFieldAttribute attr = null;
                if (hasDefaultComponent)
                {
                    attr = defaultComponent.fields.FirstOrDefault(f => f.name == componentField.Name).attribute;
                }

                if (attr == null)
                {
                    var attributes = componentField.GetCustomAttributes<GhostFieldAttribute>().ToArray();
                    if (attributes.Length > 0)
                    {
                        attr = attributes[0];
                    }
                }

                if (attr == null || !attr.SendData)
                    continue;

                FillSubFields(componentField, attr, fields);
            }

            componentData.fields = fields.ToArray();
        }

        public void SyncComponentList(GhostAuthoringComponent self)
        {
            using (var tempWorld = new World("TempGhostConversion"))
            using (var blobAssetStore = new BlobAssetStore())
            {
                self.ForcePrefabConversion = true;
                var convertedEntity = GameObjectConversionUtility.ConvertGameObjectHierarchy(self.gameObject,
                    GameObjectConversionSettings.FromWorld(tempWorld, blobAssetStore));
                self.ForcePrefabConversion = false;

                var newComponents = new List<SerializedComponentData>();
                AddToComponentList(newComponents, tempWorld, convertedEntity, 0, self.gameObject);


                if (tempWorld.EntityManager.HasComponent<LinkedEntityGroup>(convertedEntity))
                {
                    var linkedEntityGroup = tempWorld.EntityManager.GetBuffer<LinkedEntityGroup>(convertedEntity);
                    for (int i = 1; i < linkedEntityGroup.Length; ++i)
                    {
                        AddToComponentList(newComponents, tempWorld, linkedEntityGroup[i].Value, i, self.transform.GetChild(i-1).gameObject);
                    }
                }

                Components = newComponents.ToArray();
            }
        }
    }

    static class AuthoringEditorHelper
    {
        //Bunch of helper functions for editing and toggling modifiers to the various options
        public static bool ShowSendType(bool modSendType, ref GhostSendType ownerPredictedSendType)
        {
            using (var toogle = new EditorGUILayout.ToggleGroupScope("OwnerPredictedSendType", modSendType))
            {
                ++EditorGUI.indentLevel;
                ownerPredictedSendType = (GhostSendType) EditorGUILayout.EnumPopup("Send Data To", ownerPredictedSendType);
                --EditorGUI.indentLevel;
                modSendType = toogle.enabled;
            }

            return modSendType;
        }

        public static bool ShowSendChild(in GhostAuthoringComponentEditor.SerializedComponentData comp,
            bool modSendChild, ref bool sendForChildren)
        {
            using (new EditorGUI.DisabledGroupScope(comp.entityIndex == 0))
            {
                using (var toogle = new EditorGUILayout.ToggleGroupScope("SendToChildEntity", modSendChild))
                {
                    ++EditorGUI.indentLevel;
                    sendForChildren = EditorGUILayout.Toggle("Send For This Child", sendForChildren);
                    --EditorGUI.indentLevel;
                    modSendChild = toogle.enabled;
                }
            }

            return modSendChild;
        }

        public static bool ShowPrefabType(bool modPrefab, GhostPrefabType prefabType, ref GhostPrefabType newPrefab)
        {
            using (var toogle = new EditorGUILayout.ToggleGroupScope("PrefabType", modPrefab))
            {
                ++EditorGUI.indentLevel;
                if (EditorGUILayout.ToggleLeft("Server", (prefabType & GhostPrefabType.Server) != 0,
                    GUILayout.ExpandWidth(false), GUILayout.MaxWidth(90)))
                    newPrefab |= GhostPrefabType.Server;
                EditorGUILayout.BeginHorizontal();
                if (EditorGUILayout.ToggleLeft("Interpolated Client", (prefabType & GhostPrefabType.InterpolatedClient) != 0,
                    GUILayout.ExpandWidth(false), GUILayout.MaxWidth(160)))
                    newPrefab |= GhostPrefabType.InterpolatedClient;
                if (EditorGUILayout.ToggleLeft("Predicted Client", (prefabType & GhostPrefabType.PredictedClient) != 0,
                    GUILayout.ExpandWidth(false), GUILayout.MaxWidth(140)))
                    newPrefab |= GhostPrefabType.PredictedClient;
                EditorGUILayout.EndHorizontal();
                --EditorGUI.indentLevel;
                modPrefab = toogle.enabled;
            }

            return modPrefab;
        }
    }
}
