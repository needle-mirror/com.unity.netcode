using System;
using System.Collections.Generic;
using System.Globalization;
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
        public struct GhostComponentField
        {
            public string name;
            public GhostFieldAttribute attribute;
        }

        public struct GhostComponent
        {
            public string name;
            public GhostComponentAttribute attribute;
            public GhostComponentField[] fields;
            public int entityIndex;

            public bool isExpanded;
        }
        GhostComponent[] Components;

        SerializedProperty DefaultGhostMode;
        SerializedProperty SupportedGhostModes;
        SerializedProperty OptimizationMode;
        SerializedProperty Importance;
        SerializedProperty ghostComponents;
        SerializedProperty Name;

        public static Dictionary<string, GhostComponent> GhostDefaultOverrides;
        public static HashSet<string> AssembliesDefaultOverrides;

        static GhostAuthoringComponentEditor()
        {
            InitDefaultOverrides();
        }
        public static void InitDefaultOverrides()
        {
            GhostDefaultOverrides = new Dictionary<string, GhostComponent>();
            AssembliesDefaultOverrides = new HashSet<string>(new []{
                "Unity.NetCode",
                "Unity.Transforms",
            });

            var comp = new GhostComponent
            {
                name = "Unity.Transforms.Translation",
                attribute = new GhostComponentAttribute{PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.All, SendDataForChildEntity = false},
                fields = new GhostComponentField[]
                {
                    new GhostComponentField
                    {
                        name = "Value",
                        attribute = new GhostFieldAttribute{Quantization = 100, Interpolate = true}
                    }
                },
                entityIndex = 0
            };
            GhostDefaultOverrides.Add(comp.name, comp);
            comp = new GhostComponent
            {
                name = "Unity.Transforms.Rotation",
                attribute = new GhostComponentAttribute{PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.All, SendDataForChildEntity = false},
                fields = new GhostComponentField[]
                {
                    new GhostComponentField
                    {
                        name = "Value",
                        attribute = new GhostFieldAttribute{Quantization = 1000, Interpolate = true}
                    }
                },
                entityIndex = 0
            };
            GhostDefaultOverrides.Add(comp.name, comp);
        }

        void OnEnable()
        {
            DefaultGhostMode = serializedObject.FindProperty("DefaultGhostMode");
            SupportedGhostModes = serializedObject.FindProperty("SupportedGhostModes");
            OptimizationMode = serializedObject.FindProperty("OptimizationMode");
            Importance = serializedObject.FindProperty("Importance");
            ghostComponents = serializedObject.FindProperty("Components");
            Name = serializedObject.FindProperty("Name");
            if (Name.stringValue == "")
                Name.stringValue = target.name;
            Name.stringValue = Name.stringValue.Replace(" ", String.Empty);
            Components = null;
        }

        void ShowField(GhostComponentField field)
        {
            EditorGUILayout.TextField("Name", field.name);
            ++EditorGUI.indentLevel;
            EditorGUILayout.IntField("Quantization", field.attribute.Quantization);
            EditorGUILayout.Toggle("Interpolate", field.attribute.Interpolate);
            --EditorGUI.indentLevel;
        }

        void ShowComponent(ref GhostComponent comp)
        {
            GUIStyle style = null;
            if (comp.fields.Length == 0)
            {
                style = new GUIStyle(EditorStyles.foldoutHeader);
                style.fontStyle = FontStyle.Normal;
            }

            var prefabType = comp.attribute!=null?comp.attribute.PrefabType:GhostPrefabType.All;
            var ownerPredictedSendType = comp.attribute!=null?comp.attribute.OwnerPredictedSendType:GhostSendType.All;
            comp.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(comp.isExpanded,
                System.String.Format("{0}{1} ({2}/{3}/{4})",
                    comp.entityIndex != 0 ? "Child " + (comp.entityIndex - 1).ToString() + ": " : "",
                    comp.name, (prefabType&GhostPrefabType.Server)!=0 ? "S" : "-",
                    (prefabType&GhostPrefabType.InterpolatedClient)!=0 ? "IC" : "-",
                    (prefabType&GhostPrefabType.PredictedClient)!=0 ? "PC" : "-"),
                    style);
            if (comp.isExpanded)
            {
                ++EditorGUI.indentLevel;

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Toggle("Server", (prefabType&GhostPrefabType.Server)!=0);
                EditorGUILayout.Toggle("Interpolated Client", (prefabType&GhostPrefabType.InterpolatedClient)!=0);
                EditorGUILayout.Toggle("Predicted Client", (prefabType&GhostPrefabType.PredictedClient)!=0);
                EditorGUI.EndDisabledGroup();
                if (ownerPredictedSendType != GhostSendType.All)
                    EditorGUILayout.EnumPopup("Send Data To", ownerPredictedSendType);
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

                --EditorGUI.indentLevel;
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginDisabledGroup(UnityEditor.PrefabUtility.IsPartOfNonAssetPrefabInstance(target));
            EditorGUILayout.PropertyField(Name);
            EditorGUILayout.PropertyField(Importance);

            EditorGUILayout.PropertyField(SupportedGhostModes);
            if (SupportedGhostModes.intValue == (int)GhostAuthoringComponent.GhostModeMask.All)
            {
                var self = target as GhostAuthoringComponent;
                EditorGUILayout.PropertyField(DefaultGhostMode);
                // Selecting OwnerPredicted on a ghost without a GhostOwnerComponent will cause an exception during conversion - display an error for that case in the inspector
                if (DefaultGhostMode.enumValueIndex == (int)GhostAuthoringComponent.GhostMode.OwnerPredicted && self.gameObject.GetComponent<GhostOwnerComponentAuthoring>() == null)
                {
                    EditorGUILayout.HelpBox("Setting Default Ghost Mode to Owner Predicted requires the ghost to have a Ghost Owner Component", MessageType.Error);
                }
            }
            EditorGUILayout.PropertyField(OptimizationMode);
            EditorGUI.EndDisabledGroup();

            if (Components != null)
            {
                for (int ci = 0; ci < Components.Length; ++ci)
                {
                    ShowComponent(ref Components[ci]);
                }
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Separator();
            if (GUILayout.Button("Update component list"))
            {
                SyncComponentList(target as GhostAuthoringComponent);
            }
        }

        struct ComponentNameComparer : IComparer<ComponentType>
        {
            public int Compare(ComponentType x, ComponentType y) =>
                x.GetManagedType().FullName.CompareTo(y.GetManagedType().FullName);
        }

        static void FillSubFields(FieldInfo field, GhostFieldAttribute attr, List<GhostComponentField> fieldsList, string parentPrefix = "")
        {
            var typeAttribute = new TypeAttribute
            {
                composite = attr.Composite,
                interpolate= attr.Interpolate,
                quantization = attr.Quantization,
                subtype = attr.SubType
            };

            if (!field.FieldType.IsValueType)
                return;

            if (field.FieldType.IsPrimitive || field.FieldType.IsEnum)
            {
                if (CodeGenTypes.Registry.CanGenerateType(new TypeDescription(field.FieldType, typeAttribute)))
                {
                    fieldsList.Add(new GhostComponentField
                    {
                        name = parentPrefix + field.Name,
                        attribute = attr
                    });
                }
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

        static void AddToComponentList(List<GhostComponent> newComponents,
            World tempWorld, Entity convertedEntity, int entityIndex)
        {
            var compTypes = tempWorld.EntityManager.GetComponentTypes(convertedEntity);
            compTypes.Sort(default(ComponentNameComparer));



            for (int i = 0; i < compTypes.Length; ++i)
            {
                var managedType = compTypes[i].GetManagedType();
                if (managedType == typeof(Prefab) || managedType == typeof(LinkedEntityGroup))
                    continue;

                bool hasDefaultComponent = GhostDefaultOverrides.TryGetValue(managedType.FullName, out var defaultComponent);
                var fields = new List<GhostComponentField>();
                foreach (var componentField in managedType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    GhostFieldAttribute attr = null;
                    if (hasDefaultComponent)
                    {
                        attr = defaultComponent.fields.FirstOrDefault(f => f.name == componentField.Name).attribute;
                    }

                    if (attr == null)
                    {
                        var attributes = componentField.GetCustomAttributes<GhostFieldAttribute>().ToArray();
                        if(attributes.Length > 0)
                        {
                            attr = attributes[0];
                        }
                    }

                    if (attr == null || !attr.SendData)
                        continue;

                    FillSubFields(componentField, attr, fields);
                }

                var newComponent = new GhostComponent
                {
                    name = managedType.FullName,
                    attribute = default,
                    fields = fields.ToArray(),
                    entityIndex = entityIndex
                };
                if (hasDefaultComponent)
                {
                    newComponent.attribute = defaultComponent.attribute;
                }
                else
                {
                    var compAttr = managedType.GetCustomAttributes<GhostComponentAttribute>().ToArray();
                    if (compAttr.Length > 0)
                    {
                        newComponent.attribute = compAttr[0];
                    }
                    else
                    {
                        newComponent.attribute = new GhostComponentAttribute();
                    }
                }

                if (entityIndex > 0)
                {
                    // Stripping components on child entities is not supported
                    newComponent.attribute.PrefabType = GhostPrefabType.All;
                    if (!newComponent.attribute.SendDataForChildEntity && newComponent.fields.Length > 0)
                        newComponent.fields = new GhostComponentField[0];
                }

                newComponent.entityIndex = entityIndex;
                newComponents.Add(newComponent);
            }
        }

        public void SyncComponentList(GhostAuthoringComponent self)
        {
            using (var tempWorld = new World("TempGhostConversion"))
            using (var blobAssetStore = new BlobAssetStore())
            {
                var convertedEntity = GameObjectConversionUtility.ConvertGameObjectHierarchy(self.gameObject, GameObjectConversionSettings.FromWorld(tempWorld, blobAssetStore));

                var newComponents = new List<GhostComponent>();
                AddToComponentList(newComponents, tempWorld, convertedEntity, 0);


                if (tempWorld.EntityManager.HasComponent<LinkedEntityGroup>(convertedEntity))
                {
                    var linkedEntityGroup = tempWorld.EntityManager.GetBuffer<LinkedEntityGroup>(convertedEntity);
                    for (int i = 1; i < linkedEntityGroup.Length; ++i)
                    {
                        AddToComponentList(newComponents, tempWorld, linkedEntityGroup[i].Value, i);
                    }
                }

                Components = newComponents.ToArray();
            }
        }
    }
}
