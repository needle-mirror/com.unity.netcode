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
        public static string DefaultNamespace = "";
        public static string DefaultRootPath = "";
        public static string DefaultSnapshotDataPrefix = "";
        public static string DefaultUpdateSystemPrefix = "";
        public static string DefaultSerializerPrefix = "";

        SerializedProperty DefaultClientInstantiationType;
        SerializedProperty RootPath;
        SerializedProperty SnapshotDataPath;
        SerializedProperty UpdateSystemPath;
        SerializedProperty SerializerPath;
        SerializedProperty Importance;
        SerializedProperty PredictingPlayerNetworkId;
        SerializedProperty ghostComponents;

        public static Dictionary<string, GhostAuthoringComponent.GhostComponent> GhostDefaultOverrides;

        static GhostAuthoringComponentEditor()
        {
            GhostDefaultOverrides = new Dictionary<string, GhostAuthoringComponent.GhostComponent>();
            var comp = new GhostAuthoringComponent.GhostComponent
            {
                name = "Unity.NetCode.CurrentSimulatedPosition",
                server = false,
                interpolatedClient = true,
                predictedClient = true,
                sendDataTo = GhostAuthoringComponent.ClientSendType.All,
                manualFieldList = false,
                fields = new GhostAuthoringComponent.GhostComponentField[0]
            };
            GhostDefaultOverrides.Add(comp.name, comp);
            comp = new GhostAuthoringComponent.GhostComponent
            {
                name = "Unity.NetCode.CurrentSimulatedRotation",
                server = false,
                interpolatedClient = true,
                predictedClient = true,
                sendDataTo = GhostAuthoringComponent.ClientSendType.All,
                manualFieldList = false,
                fields = new GhostAuthoringComponent.GhostComponentField[0]
            };
            GhostDefaultOverrides.Add(comp.name, comp);
            comp = new GhostAuthoringComponent.GhostComponent
            {
                name = "Unity.Transforms.Translation",
                interpolatedClient = true,
                predictedClient = true,
                server = true,
                fields = new GhostAuthoringComponent.GhostComponentField[]
                {
                    new GhostAuthoringComponent.GhostComponentField
                    {
                        name = "Value",
                        quantization = 100,
                        interpolate = true
                    }
                },
                manualFieldList = false,
                entityIndex = 0
            };
            GhostDefaultOverrides.Add(comp.name, comp);
            comp = new GhostAuthoringComponent.GhostComponent
            {
                name = "Unity.Transforms.Rotation",
                interpolatedClient = true,
                predictedClient = true,
                server = true,
                fields = new GhostAuthoringComponent.GhostComponentField[]
                {
                    new GhostAuthoringComponent.GhostComponentField
                    {
                        name = "Value",
                        quantization = 1000,
                        interpolate = true
                    }
                },
                manualFieldList = false,
                entityIndex = 0
            };
            GhostDefaultOverrides.Add(comp.name, comp);
        }

        void OnEnable()
        {
            DefaultClientInstantiationType = serializedObject.FindProperty("DefaultClientInstantiationType");
            RootPath = serializedObject.FindProperty("RootPath");
            SnapshotDataPath = serializedObject.FindProperty("SnapshotDataPath");
            UpdateSystemPath = serializedObject.FindProperty("UpdateSystemPath");
            SerializerPath = serializedObject.FindProperty("SerializerPath");
            Importance = serializedObject.FindProperty("Importance");
            PredictingPlayerNetworkId = serializedObject.FindProperty("PredictingPlayerNetworkId");
            ghostComponents = serializedObject.FindProperty("Components");

            bool initRootPath = true;
            var namePrefix = target.name.Replace(" ", String.Empty);
            if (SnapshotDataPath.stringValue == "")
                SnapshotDataPath.stringValue =
                    String.Format("{0}{1}SnapshotData.cs", DefaultSnapshotDataPrefix, namePrefix);
            else
                initRootPath = false;
            if (UpdateSystemPath.stringValue == "")
                UpdateSystemPath.stringValue =
                    String.Format("{0}{1}GhostUpdateSystem.cs", DefaultUpdateSystemPrefix, namePrefix);
            else
                initRootPath = false;
            if (SerializerPath.stringValue == "")
                SerializerPath.stringValue =
                    String.Format("{0}{1}GhostSerializer.cs", DefaultSerializerPrefix, namePrefix);
            else
                initRootPath = false;
            if (initRootPath)
                RootPath.stringValue = DefaultRootPath;
            serializedObject.ApplyModifiedProperties();
        }

        bool ShowField(SerializedProperty field, bool enabled)
        {
            EditorGUILayout.PropertyField(field.FindPropertyRelative("name"));
            ++EditorGUI.indentLevel;
            var keep = !enabled || !GUILayout.Button("Delete Component Field");
            EditorGUILayout.PropertyField(field.FindPropertyRelative("quantization"));
            EditorGUILayout.PropertyField(field.FindPropertyRelative("interpolate"));
            --EditorGUI.indentLevel;
            return keep;
        }

        bool ShowComponent(SerializedProperty comp)
        {
            bool keep = true;
            var fields = comp.FindPropertyRelative("fields");
            var fieldName = comp.FindPropertyRelative("name");
            var entityIndex = comp.FindPropertyRelative("entityIndex");
            var interpolatedClient = comp.FindPropertyRelative("interpolatedClient");
            var predictedClient = comp.FindPropertyRelative("predictedClient");
            var server = comp.FindPropertyRelative("server");
            var sendDataTo = comp.FindPropertyRelative("sendDataTo");
            var manualFieldList = comp.FindPropertyRelative("manualFieldList");
            GUIStyle style = null;
            if (fields.arraySize == 0)
            {
                style = new GUIStyle(EditorStyles.foldoutHeader);
                style.fontStyle = FontStyle.Normal;
            }

            comp.isExpanded = EditorGUILayout.BeginFoldoutHeaderGroup(comp.isExpanded,
                System.String.Format("{5}{0}{1} ({2}/{3}/{4})",
                    entityIndex.intValue != 0 ? "Child " + (entityIndex.intValue - 1).ToString() + ": " : "",
                    fieldName.stringValue, server.boolValue ? "S" : "-",
                    interpolatedClient.boolValue ? "IC" : "-",
                    predictedClient.boolValue ? "PC" : "-",
                    manualFieldList.boolValue ? "* " : ""),
                    style);
            if (comp.isExpanded)
            {
                ++EditorGUI.indentLevel;

                EditorGUI.BeginDisabledGroup(entityIndex.intValue != 0);
                EditorGUILayout.PropertyField(server);
                EditorGUILayout.PropertyField(interpolatedClient);
                EditorGUILayout.PropertyField(predictedClient);
                EditorGUI.EndDisabledGroup();
                if (DefaultClientInstantiationType.intValue ==
                    (int) GhostAuthoringComponent.ClientInstantionType.OwnerPredicted &&
                    fields.arraySize > 0)
                    EditorGUILayout.PropertyField(sendDataTo);
                if (server.boolValue)
                {
                    EditorGUILayout.Separator();
                    EditorGUILayout.PropertyField(manualFieldList);
                    EditorGUI.BeginDisabledGroup(!manualFieldList.boolValue);
                    EditorGUILayout.LabelField("Fields");
                    int removeIdx = -1;
                    for (int fi = 0; fi < fields.arraySize; ++fi)
                    {
                        if (!ShowField(fields.GetArrayElementAtIndex(fi), manualFieldList.boolValue))
                            removeIdx = fi;
                    }

                    EditorGUI.EndDisabledGroup();
                    if (removeIdx >= 0)
                        fields.DeleteArrayElementAtIndex(removeIdx);
                    if (manualFieldList.boolValue && GUILayout.Button("Add Component Field"))
                    {
                        fields.InsertArrayElementAtIndex(fields.arraySize);
                        var field = fields.GetArrayElementAtIndex(fields.arraySize - 1);
                        field.FindPropertyRelative("name").stringValue = "";
                        field.FindPropertyRelative("quantization").intValue = 1;
                        field.FindPropertyRelative("interpolate").boolValue = false;
                    }
                }

                --EditorGUI.indentLevel;
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
            return keep;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(DefaultClientInstantiationType);
            if (DefaultClientInstantiationType.intValue ==
                (int) GhostAuthoringComponent.ClientInstantionType.OwnerPredicted)
            {
                var intFields = new List<string>();
                int index = -1;
                foreach (var comp in (target as GhostAuthoringComponent).Components)
                {
                    foreach (var field in comp.fields)
                    {
                        var name = comp.name + "." + field.name;
                        if (name == PredictingPlayerNetworkId.stringValue)
                            index = intFields.Count;
                        intFields.Add(name);
                    }
                }

                // Display a dropdown with all int fields for the user to choose an owning player
                var newIndex = EditorGUILayout.Popup("Predicting player network id", index >= 0 ? index : 0,
                    intFields.ToArray());
                if (newIndex != index)
                {
                    PredictingPlayerNetworkId.stringValue = intFields[newIndex];
                }
            }

            EditorGUILayout.PropertyField(RootPath);
            EditorGUILayout.PropertyField(SnapshotDataPath);
            EditorGUILayout.PropertyField(UpdateSystemPath);
            EditorGUILayout.PropertyField(SerializerPath);
            EditorGUILayout.PropertyField(Importance);

            int removeIdx = -1;
            for (int ci = 0; ci < ghostComponents.arraySize; ++ci)
            {
                if (!ShowComponent(ghostComponents.GetArrayElementAtIndex(ci)))
                    removeIdx = ci;
            }

            if (removeIdx >= 0)
            {
                ghostComponents.DeleteArrayElementAtIndex(removeIdx);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Separator();
            if (GUILayout.Button("Update component list"))
            {
                SyncComponentList(target as GhostAuthoringComponent);
            }

            if (GUILayout.Button("Generate code"))
            {
                GenerateGhost(target as GhostAuthoringComponent);
            }
        }

        struct ComponentNameComparer : IComparer<ComponentType>
        {
            public int Compare(ComponentType x, ComponentType y) =>
                x.GetManagedType().FullName.CompareTo(y.GetManagedType().FullName);
        }

        static void AddToComponentList(GhostAuthoringComponent self,
            List<GhostAuthoringComponent.GhostComponent> newComponents, Dictionary<string, int> toDelete,
            World tempWorld, Entity convertedEntity, int entityIndex)
        {
            var typeProviders = new List<GhostSnapshotValue>();
            typeProviders.AddRange(GhostSnapshotValue.GameSpecificTypes);
            typeProviders.AddRange(GhostSnapshotValue.DefaultTypes);

            var compTypes = tempWorld.EntityManager.GetComponentTypes(convertedEntity);
            compTypes.Sort(default(ComponentNameComparer));
            for (int i = 0; i < compTypes.Length; ++i)
            {
                var managedType = compTypes[i].GetManagedType();
                if (managedType == typeof(Prefab) || managedType == typeof(LinkedEntityGroup))
                    continue;
                GhostAuthoringComponent.GhostComponent newComponent;
                if (GhostDefaultOverrides.TryGetValue(managedType.FullName, out newComponent))
                {
                    newComponent.fields = (GhostAuthoringComponent.GhostComponentField[]) newComponent.fields.Clone();
                }
                else
                {
                    var fields = new List<GhostAuthoringComponent.GhostComponentField>();
                    foreach (var componentField in managedType.GetFields())
                    {
                        var attr = componentField.GetCustomAttributes<GhostDefaultFieldAttribute>().ToArray();
                        if (attr.Length > 0)
                        {
                            bool valid = true;
                            foreach (var valueType in typeProviders)
                            {
                                if (valueType.CanProcess(componentField, managedType.FullName,
                                    componentField.Name))
                                {
                                    if (attr[0].Quantization < 0 && valueType.SupportsQuantization)
                                    {
                                        Debug.LogError(String.Format(
                                            "{0}.{1} is of type {2} which requires quantization factor to be specified - ignoring field",
                                            managedType.FullName, componentField.Name, componentField.FieldType));
                                        valid = false;
                                    }

                                    if (attr[0].Quantization > 1 && !valueType.SupportsQuantization)
                                    {
                                        Debug.LogError(String.Format(
                                            "{0}.{1} is of type {2} which does not support quantization - ignoring field",
                                            managedType.FullName, componentField.Name, componentField.FieldType));
                                        valid = false;
                                    }

                                    break;
                                }
                            }

                            if (valid)
                            {
                                // If type requires quantization not specifying quantization is an error (log + ignore field)
                                fields.Add(new GhostAuthoringComponent.GhostComponentField
                                {
                                    name = componentField.Name,
                                    quantization = attr[0].Quantization,
                                    interpolate = attr[0].Interpolate
                                });
                            }
                        }
                    }

                    newComponent = new GhostAuthoringComponent.GhostComponent
                    {
                        name = managedType.FullName,
                        interpolatedClient = true,
                        predictedClient = true,
                        server = true,
                        fields = fields.ToArray(),
                        manualFieldList = false
                    };
                    var compAttr = managedType.GetCustomAttributes<GhostDefaultComponentAttribute>().ToArray();
                    if (compAttr.Length > 0)
                    {
                        newComponent.server = (compAttr[0].TargetType & GhostDefaultComponentAttribute.Type.Server) != 0;
                        newComponent.interpolatedClient =
                            (compAttr[0].TargetType & GhostDefaultComponentAttribute.Type.InterpolatedClient) != 0;
                        newComponent.predictedClient =
                            (compAttr[0].TargetType & GhostDefaultComponentAttribute.Type.PredictedClient) != 0;
                    }
                }

                if (toDelete.TryGetValue(entityIndex + managedType.FullName, out var compIdx))
                {
                    var fields = newComponent.fields;
                    newComponent = self.Components[compIdx];
                    if (!self.Components[compIdx].manualFieldList)
                        newComponent.fields = fields;

                    toDelete.Remove(entityIndex + managedType.FullName);
                }

                if (entityIndex > 0)
                {
                    // Stripping components on child entities is not supported
                    newComponent.server = true;
                    newComponent.interpolatedClient = true;
                    newComponent.predictedClient = true;
                }
                newComponent.entityIndex = entityIndex;
                newComponents.Add(newComponent);
            }
        }

        public static void SyncComponentList(GhostAuthoringComponent self)
        {
            using (var tempWorld = new World("TempGhostConversion"))
            using (var blobAssetStore = new BlobAssetStore())
            {
                self.doNotStrip = true;
                var convertedEntity = GameObjectConversionUtility.ConvertGameObjectHierarchy(self.gameObject, GameObjectConversionSettings.FromWorld(tempWorld, blobAssetStore));
                self.doNotStrip = false;

                // Build list of existing components
                var toDelete = new Dictionary<string, int>();
                for (int i = 0; i < self.Components.Length; ++i)
                {
                    try
                    {
                        toDelete.Add(self.Components[i].entityIndex + self.Components[i].name, i);

                    }
                    catch (Exception e)
                    {
                        Debug.LogAssertion(e);
                    }
                }

                var newComponents = new List<GhostAuthoringComponent.GhostComponent>();
                AddToComponentList(self, newComponents, toDelete, tempWorld, convertedEntity, 0);


                if (tempWorld.EntityManager.HasComponent<LinkedEntityGroup>(convertedEntity))
                {
                    var linkedEntityGroup = tempWorld.EntityManager.GetBuffer<LinkedEntityGroup>(convertedEntity);
                    for (int i = 1; i < linkedEntityGroup.Length; ++i)
                    {
                        AddToComponentList(self, newComponents, toDelete, tempWorld, linkedEntityGroup[i].Value, i);
                    }
                }

                self.Components = newComponents.ToArray();
            }
            EditorUtility.SetDirty(self);
        }

        public static void GenerateGhost(GhostAuthoringComponent ghostInfo)
        {
            var tempWorld = new World("GhostEnsureECSLoaded");
            tempWorld.Dispose();

            var allTypes = TypeManager.GetAllTypes();
            var typeLookup = new Dictionary<string, Type>();
            foreach (var compType in allTypes)
            {
                if (compType.Type != null)
                    typeLookup.Add(compType.Type.FullName, compType.Type);
            }

            string ownerField = "";
            // Update type of all fields
            for (int comp = 0; comp < ghostInfo.Components.Length; ++comp)
            {
                if (!typeLookup.TryGetValue(ghostInfo.Components[comp].name, out var componentType))
                {
                    Debug.LogError($"Could not find the type {ghostInfo.Components[comp].name}");
                    return;
                }
                ghostInfo.Components[comp].NamespaceName = componentType.Namespace;
                ghostInfo.Components[comp].ShortName = (String.IsNullOrEmpty(componentType.Namespace))
                    ? componentType.FullName
                    : componentType.FullName.Substring(componentType.Namespace.Length + 1);
                for (int field = 0; field < ghostInfo.Components[comp].fields.Length; ++field)
                {
                    var fieldInfo = componentType.GetField(ghostInfo.Components[comp].fields[field].name);
                    if (fieldInfo == null)
                    {
                        Debug.LogError("Could not find field: " + ghostInfo.Components[comp].fields[field].name +
                                       " in componentType: " + ghostInfo.Components[comp].name);
                        return;
                    }

                    ghostInfo.Components[comp].fields[field].Field = fieldInfo;

                    if (ghostInfo.DefaultClientInstantiationType ==
                        GhostAuthoringComponent.ClientInstantionType.OwnerPredicted &&
                        ghostInfo.PredictingPlayerNetworkId == ghostInfo.Components[comp].name + "." +
                        ghostInfo.Components[comp].fields[field].name)
                    {
                        ownerField = GetShortName(ghostInfo.Components[comp]) +
                                     ghostInfo.Components[comp].fields[field].name;
                    }
                }
            }

            var assetPath = GhostCodeGen.GetPrefabAssetPath(ghostInfo.gameObject);

            var batch = new GhostCodeGen.Batch();
            GenerateSnapshotData(ghostInfo, ownerField, assetPath, batch);
            GenerateSerializer(ghostInfo, assetPath, batch);
            GenerateUpdateSystem(ghostInfo, ownerField, assetPath, batch);
            batch.Flush();
            AssetDatabase.Refresh();
        }

        static string GetShortName(GhostAuthoringComponent.GhostComponent comp)
        {
            var shortName = comp.ShortName.Replace("+", "");
            if (comp.entityIndex != 0)
                shortName = "Child" + (comp.entityIndex - 1) + shortName;
            return shortName;

        }

        static string GetFieldTypeName(Type fieldType)
        {
            if (fieldType == typeof(int))
                return "int";
            if (fieldType == typeof(uint))
                return "uint";
            if (fieldType == typeof(short))
                return "short";
            if (fieldType == typeof(ushort))
                return "ushort";
            if (fieldType == typeof(sbyte))
                return "sbyte";
            if (fieldType == typeof(byte))
                return "byte";
            return fieldType.ToString().Replace("+", ".");
        }
        static void GenerateSnapshotData(GhostAuthoringComponent ghostInfo, string ownerField, string assetPath, GhostCodeGen.Batch batch)
        {
            var codeGen = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostSnapshotData.cs");
            var replacements = new Dictionary<string, string>();

            var typeProviders = new List<GhostSnapshotValue>();
            typeProviders.AddRange(GhostSnapshotValue.GameSpecificTypes);
            typeProviders.AddRange(GhostSnapshotValue.DefaultTypes);
            var typeCodeGenCache = new Dictionary<string, GhostCodeGen>();

            HashSet<string> imports = new HashSet<string>();
            imports.Add("Unity.Mathematics");
            bool hasTypeSpecificFields = false;
            bool hasReadWritePredicted = false;
            bool hasReadWriteInterpolated = false;
            if (ghostInfo.DefaultClientInstantiationType == GhostAuthoringComponent.ClientInstantionType.OwnerPredicted)
            {
                for (int comp = 0; comp < ghostInfo.Components.Length; ++comp)
                {
                    if (ghostInfo.Components[comp].server &&
                        ghostInfo.Components[comp].fields.Length > 0 &&
                        ghostInfo.Components[comp].sendDataTo != GhostAuthoringComponent.ClientSendType.All)
                        hasTypeSpecificFields = true;
                }
            }

            replacements.Add("GHOST_OWNER_FIELD", ownerField);
            if (hasTypeSpecificFields)
            {
                codeGen.GenerateFragment("GHOST_READ_IS_PREDICTED", replacements);
                codeGen.GenerateFragment("GHOST_WRITE_IS_PREDICTED", replacements);
            }

            int changeMaskIndex = 0;
            for (int comp = 0; comp < ghostInfo.Components.Length; ++comp)
            {
                if (!ghostInfo.Components[comp].server)
                    continue;
                for (int field = 0; field < ghostInfo.Components[comp].fields.Length; ++field)
                {
                    bool processed = false;
                    foreach (var value in typeProviders)
                    {
                        if (value.CanProcess(ghostInfo.Components[comp].fields[field].Field,
                            ghostInfo.Components[comp].name,
                            ghostInfo.Components[comp].fields[field].name))
                        {
                            var currentChangeMask = changeMaskIndex;
                            value.AddImports(imports);
                            var quantization = ghostInfo.Components[comp].fields[field].quantization;
                            var shortName = GetShortName(ghostInfo.Components[comp]);
                            var fullFieldName = shortName + ghostInfo.Components[comp].fields[field].name;

                            var typeCodeGenPath = value.GetTemplatePath(quantization);
                            if (!typeCodeGenCache.TryGetValue(typeCodeGenPath, out var typeCodeGen))
                            {
                                typeCodeGen = new GhostCodeGen(typeCodeGenPath);
                                typeCodeGenCache.Add(typeCodeGenPath, typeCodeGen);
                            }
                            replacements.Clear();
                            replacements.Add("GHOST_FIELD_NAME", fullFieldName);
                            replacements.Add("GHOST_FIELD_TYPE_NAME", GetFieldTypeName(ghostInfo.Components[comp].fields[field].Field.FieldType));
                            if (quantization > 0)
                            {
                                replacements.Add("GHOST_QUANTIZE_SCALE", quantization.ToString());
                                replacements.Add("GHOST_DEQUANTIZE_SCALE", $"{(1.0f / quantization).ToString(CultureInfo.InvariantCulture)}f");
                            }
                            replacements.Add("GHOST_MASK_BATCH", (currentChangeMask/32).ToString());
                            replacements.Add("GHOST_MASK_INDEX", (currentChangeMask%32).ToString());

                            typeCodeGen.GenerateFragment("GHOST_FIELD", replacements, codeGen);
                            typeCodeGen.GenerateFragment("GHOST_FIELD_GET_SET", replacements, codeGen);
                            typeCodeGen.GenerateFragment("GHOST_PREDICT", replacements, codeGen);
                            if (currentChangeMask%32 == 0)
                                typeCodeGen.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK_ZERO", replacements, codeGen, "GHOST_CALCULATE_CHANGE_MASK");
                            else
                                typeCodeGen.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK", replacements, codeGen);

                            if (ghostInfo.Components[comp].fields[field].interpolate)
                                typeCodeGen.GenerateFragment("GHOST_INTERPOLATE", replacements, codeGen);

                            if (ghostInfo.Components[comp].server &&
                                ghostInfo.Components[comp].fields.Length > 0 &&
                                ghostInfo.Components[comp].sendDataTo == GhostAuthoringComponent.ClientSendType.Predicted)
                            {
                                if (!hasReadWritePredicted)
                                {
                                    codeGen.GenerateFragment("GHOST_BEGIN_READ_PREDICTED", replacements);
                                    codeGen.GenerateFragment("GHOST_END_READ_PREDICTED", replacements);
                                    codeGen.GenerateFragment("GHOST_BEGIN_WRITE_PREDICTED", replacements);
                                    codeGen.GenerateFragment("GHOST_END_WRITE_PREDICTED", replacements);
                                    hasReadWritePredicted = true;
                                }
                                typeCodeGen.GenerateFragment("GHOST_READ", replacements, codeGen, "GHOST_READ_PREDICTED", "    ");
                                typeCodeGen.GenerateFragment("GHOST_WRITE", replacements, codeGen, "GHOST_WRITE_PREDICTED", "    ");
                            }
                            else if (ghostInfo.Components[comp].server &&
                                ghostInfo.Components[comp].fields.Length > 0 &&
                                ghostInfo.Components[comp].sendDataTo == GhostAuthoringComponent.ClientSendType.Interpolated)
                            {
                                if (!hasReadWriteInterpolated)
                                {
                                    codeGen.GenerateFragment("GHOST_BEGIN_READ_INTERPOLATED", replacements);
                                    codeGen.GenerateFragment("GHOST_END_READ_INTERPOLATED", replacements);
                                    codeGen.GenerateFragment("GHOST_BEGIN_WRITE_INTERPOLATED", replacements);
                                    codeGen.GenerateFragment("GHOST_END_WRITE_INTERPOLATED", replacements);
                                    hasReadWriteInterpolated = true;
                                }
                                typeCodeGen.GenerateFragment("GHOST_READ", replacements, codeGen, "GHOST_READ_INTERPOLATED", "    ");
                                typeCodeGen.GenerateFragment("GHOST_WRITE", replacements, codeGen, "GHOST_WRITE_INTERPOLATED", "    ");
                            }
                            else
                            {
                                typeCodeGen.GenerateFragment("GHOST_READ", replacements, codeGen);
                                typeCodeGen.GenerateFragment("GHOST_WRITE", replacements, codeGen);
                            }
                            ++changeMaskIndex;

                            processed = true;
                            break;
                        }
                    }

                    if (!processed)
                    {
                        Debug.LogError("Unhandled type " + ghostInfo.Components[comp].fields[field].Field.FieldType);
                    }
                }
            }

            var numMasks = (changeMaskIndex + 31) / 32;
            for (int i = 0; i < numMasks; ++i)
            {
                replacements["GHOST_MASK_BATCH"] = i.ToString();
                codeGen.GenerateFragment("GHOST_CHANGE_MASK", replacements);
                codeGen.GenerateFragment("GHOST_WRITE_CHANGE_MASK", replacements);
                codeGen.GenerateFragment("GHOST_READ_CHANGE_MASK", replacements);
            }

            replacements.Clear();
            foreach (var ns in imports)
            {
                if (ns != null && ns != "")
                {
                    replacements["GHOST_USING"] = ns;
                    codeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);
                }
            }

            replacements.Clear();
            replacements.Add("GHOST_NAME", ghostInfo.name);
            codeGen.GenerateFile(assetPath, ghostInfo.RootPath, ghostInfo.SnapshotDataPath, replacements, batch);
        }

        static void GenerateSerializer(GhostAuthoringComponent ghostInfo, string assetPath, GhostCodeGen.Batch batch)
        {
            var codeGen = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostSerializer.cs");
            var replacements = new Dictionary<string, string>();

            int serverComponentCount = 0;
            bool needsLinkedEntityGroup = false;
            HashSet<string> imports = new HashSet<string>();
            imports.Add("Unity.Entities");
            imports.Add("Unity.Collections");
            imports.Add("Unity.NetCode");
            for (int comp = 0; comp < ghostInfo.Components.Length; ++comp)
            {
                var entityIndex = ghostInfo.Components[comp].entityIndex;
                if (!ghostInfo.Components[comp].server)
                    continue;
                imports.Add(ghostInfo.Components[comp].NamespaceName);
                var componentTypeName = GetShortName(ghostInfo.Components[comp]);
                replacements.Clear();
                replacements.Add("GHOST_COMPONENT_TYPE_NAME", componentTypeName);
                replacements.Add("GHOST_COMPONENT_TYPE", ghostInfo.Components[comp].ShortName.Replace("+", "."));
                replacements.Add("GHOST_ENTITY_INDEX", entityIndex.ToString());
                if (entityIndex == 0)
                {
                    ++serverComponentCount;
                    codeGen.GenerateFragment("GHOST_COMPONENT_TYPE_CHECK", replacements);
                    codeGen.GenerateFragment("GHOST_COMPONENT_TYPE", replacements);
                    codeGen.GenerateFragment("GHOST_ASSIGN_COMPONENT_TYPE", replacements);
                }

                if (ghostInfo.Components[comp].fields.Length > 0)
                {
                    if (entityIndex == 0)
                    {
                        codeGen.GenerateFragment("GHOST_COMPONENT_TYPE_DATA", replacements);
                        codeGen.GenerateFragment("GHOST_ASSIGN_COMPONENT_TYPE_DATA", replacements);
                        codeGen.GenerateFragment("GHOST_ASSIGN_CHUNK_ARRAY", replacements);
                    }
                    else
                    {
                        needsLinkedEntityGroup = true;
                        codeGen.GenerateFragment("GHOST_COMPONENT_TYPE_CHILD_DATA", replacements);
                        codeGen.GenerateFragment("GHOST_ASSIGN_COMPONENT_TYPE_CHILD_DATA", replacements);
                    }

                    for (int field = 0; field < ghostInfo.Components[comp].fields.Length; ++field)
                    {
                        replacements["GHOST_FIELD_NAME"] = ghostInfo.Components[comp].fields[field].name;
                        if (entityIndex == 0)
                        {
                            codeGen.GenerateFragment("GHOST_ASSIGN_SNAPSHOT", replacements);
                        }
                        else
                        {
                            codeGen.GenerateFragment("GHOST_ASSIGN_CHILD_SNAPSHOT", replacements);
                        }
                    }
                }
            }

            if (needsLinkedEntityGroup)
            {
                ++serverComponentCount;
                replacements.Clear();
                replacements.Add("GHOST_COMPONENT_TYPE_NAME", "LinkedEntityGroup");
                replacements.Add("GHOST_COMPONENT_TYPE", "LinkedEntityGroup");
                codeGen.GenerateFragment("GHOST_COMPONENT_TYPE_CHECK", replacements);
                codeGen.GenerateFragment("GHOST_COMPONENT_TYPE", replacements);
                codeGen.GenerateFragment("GHOST_ASSIGN_COMPONENT_TYPE", replacements);

                codeGen.GenerateFragment("GHOST_BUFFER_COMPONENT_TYPE_DATA", replacements);
                codeGen.GenerateFragment("GHOST_ASSIGN_BUFFER_COMPONENT_TYPE_DATA", replacements);
                codeGen.GenerateFragment("GHOST_ASSIGN_CHUNK_BUFFER_ARRAY", replacements);
            }

            replacements.Clear();
            foreach (var ns in imports)
            {
                if (ns != null && ns != "")
                {
                    replacements["GHOST_USING"] = ns;
                    codeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);
                }
            }

            replacements.Clear();
            replacements.Add("GHOST_NAME", ghostInfo.name);
            replacements.Add("GHOST_IMPORTANCE", ghostInfo.Importance);
            replacements.Add("GHOST_COMPONENT_COUNT", serverComponentCount.ToString());
            codeGen.GenerateFile(assetPath, ghostInfo.RootPath, ghostInfo.SerializerPath, replacements, batch);
        }

        static void GenerateUpdateSystem(GhostAuthoringComponent ghostInfo, string ownerField, string assetPath, GhostCodeGen.Batch batch)
        {
            var codeGen = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostUpdateSystem.cs");
            var replacements = new Dictionary<string, string>();

            var ghostInterpolatedComponentFromEntitySet = new HashSet<string>();
            var ghostPredictedComponentFromEntitySet = new HashSet<string>();
            bool interpolatedNeedsLinkedEntityGroup = false;
            bool predictedNeedsLinkedEntityGroup = false;
            HashSet<string> imports = new HashSet<string>();
            imports.Add("Unity.Entities");
            HashSet<string> entityGroupTypes = new HashSet<string>();
            for (int comp = 0; comp < ghostInfo.Components.Length; ++comp)
            {
                if (ghostInfo.Components[comp].interpolatedClient && ghostInfo.Components[comp].entityIndex > 0 && ghostInfo.Components[comp].fields.Length > 0)
                    entityGroupTypes.Add(ghostInfo.Components[comp].name);
            }

            for (int comp = 0; comp < ghostInfo.Components.Length; ++comp)
            {
                if (!ghostInfo.Components[comp].server)
                    continue;
                if (ghostInfo.Components[comp].fields.Length == 0)
                    continue;
                var componentTypeName = GetShortName(ghostInfo.Components[comp]);

                var type = ghostInfo.Components[comp].ShortName.Replace("+", ".");
                var fromEntityName = type.Replace(".", "");
                replacements.Clear();
                replacements.Add("GHOST_COMPONENT_TYPE", type);
                replacements.Add("GHOST_COMPONENT_TYPE_NAME", componentTypeName);
                replacements.Add("GHOST_COMPONENT_FROM_ENTITY_NAME", fromEntityName);
                replacements.Add("GHOST_ENTITY_INDEX", ghostInfo.Components[comp].entityIndex.ToString());

                if (ghostInfo.Components[comp].interpolatedClient &&
                    (ghostInfo.DefaultClientInstantiationType != GhostAuthoringComponent.ClientInstantionType.OwnerPredicted ||
                     ghostInfo.Components[comp].sendDataTo != GhostAuthoringComponent.ClientSendType.Predicted))
                {
                    imports.Add(ghostInfo.Components[comp].NamespaceName);

                    // When there are nested entities (or linked group entities) all of the components of the type
                    // we need from the group of entities (parent+children) should be accessed the same way, or we'll get native array aliasing
                    if (ghostInfo.Components[comp].entityIndex == 0 &&
                        !entityGroupTypes.Contains(ghostInfo.Components[comp].name))
                    {
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_COMPONENT_TYPE", replacements);
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_COMPONENT_REF", replacements);
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_ASSIGN_COMPONENT_REF", replacements);
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_COMPONENT_ARRAY", replacements);
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_BEGIN_ASSIGN", replacements);
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_END_ASSIGN", replacements);
                    }
                    else
                    {
                        interpolatedNeedsLinkedEntityGroup = true;
                        if (!ghostInterpolatedComponentFromEntitySet.Contains(type))
                        {
                            codeGen.GenerateFragment("GHOST_INTERPOLATED_COMPONENT_CHILD_REF", replacements);
                            codeGen.GenerateFragment("GHOST_INTERPOLATED_ASSIGN_COMPONENT_CHILD_REF", replacements);
                            ghostInterpolatedComponentFromEntitySet.Add(type);
                        }

                        codeGen.GenerateFragment("GHOST_INTERPOLATED_BEGIN_ASSIGN_CHILD", replacements);
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_END_ASSIGN_CHILD", replacements);
                    }

                    for (int field = 0; field < ghostInfo.Components[comp].fields.Length; ++field)
                    {
                        replacements["GHOST_FIELD_NAME"] = ghostInfo.Components[comp].fields[field].name;
                        codeGen.GenerateFragment("GHOST_INTERPOLATED_ASSIGN", replacements);
                    }
                }

                if (ghostInfo.Components[comp].predictedClient &&
                    (ghostInfo.DefaultClientInstantiationType != GhostAuthoringComponent.ClientInstantionType.OwnerPredicted ||
                     ghostInfo.Components[comp].sendDataTo != GhostAuthoringComponent.ClientSendType.Interpolated))
                {
                    imports.Add(ghostInfo.Components[comp].NamespaceName);
                    if (ghostInfo.Components[comp].entityIndex == 0)
                    {
                        codeGen.GenerateFragment("GHOST_PREDICTED_COMPONENT_TYPE", replacements);
                        codeGen.GenerateFragment("GHOST_PREDICTED_COMPONENT_REF", replacements);
                        codeGen.GenerateFragment("GHOST_PREDICTED_ASSIGN_COMPONENT_REF", replacements);
                        codeGen.GenerateFragment("GHOST_PREDICTED_COMPONENT_ARRAY", replacements);
                        codeGen.GenerateFragment("GHOST_PREDICTED_BEGIN_ASSIGN", replacements);
                        codeGen.GenerateFragment("GHOST_PREDICTED_END_ASSIGN", replacements);
                    }
                    else
                    {
                        predictedNeedsLinkedEntityGroup = true;
                        if (!ghostPredictedComponentFromEntitySet.Contains(type))
                        {
                            codeGen.GenerateFragment("GHOST_PREDICTED_COMPONENT_CHILD_REF", replacements);
                            codeGen.GenerateFragment("GHOST_PREDICTED_ASSIGN_COMPONENT_CHILD_REF", replacements);
                            ghostPredictedComponentFromEntitySet.Add(type);
                        }

                        codeGen.GenerateFragment("GHOST_PREDICTED_BEGIN_ASSIGN_CHILD", replacements);
                        codeGen.GenerateFragment("GHOST_PREDICTED_END_ASSIGN_CHILD", replacements);
                    }

                    for (int field = 0; field < ghostInfo.Components[comp].fields.Length; ++field)
                    {
                        replacements["GHOST_FIELD_NAME"] = ghostInfo.Components[comp].fields[field].name;
                        codeGen.GenerateFragment("GHOST_PREDICTED_ASSIGN", replacements);
                    }
                }
            }
            if (interpolatedNeedsLinkedEntityGroup)
            {
                replacements.Clear();
                replacements.Add("GHOST_COMPONENT_TYPE", "LinkedEntityGroup");
                replacements.Add("GHOST_COMPONENT_TYPE_NAME", "LinkedEntityGroup");
                codeGen.GenerateFragment("GHOST_INTERPOLATED_READ_ONLY_COMPONENT_TYPE", replacements);
                codeGen.GenerateFragment("GHOST_INTERPOLATED_BUFFER_REF", replacements);
                codeGen.GenerateFragment("GHOST_INTERPOLATED_ASSIGN_BUFFER_REF", replacements);
                codeGen.GenerateFragment("GHOST_INTERPOLATED_BUFFER_ARRAY", replacements);
            }
            if (predictedNeedsLinkedEntityGroup)
            {
                replacements.Clear();
                replacements.Add("GHOST_COMPONENT_TYPE", "LinkedEntityGroup");
                replacements.Add("GHOST_COMPONENT_TYPE_NAME", "LinkedEntityGroup");
                codeGen.GenerateFragment("GHOST_PREDICTED_READ_ONLY_COMPONENT_TYPE", replacements);
                codeGen.GenerateFragment("GHOST_PREDICTED_BUFFER_REF", replacements);
                codeGen.GenerateFragment("GHOST_PREDICTED_ASSIGN_BUFFER_REF", replacements);
                codeGen.GenerateFragment("GHOST_PREDICTED_BUFFER_ARRAY", replacements);
            }

            replacements.Clear();
            replacements.Add("GHOST_NAME", ghostInfo.name);
            replacements.Add("GHOST_OWNER_FIELD", ownerField);
            if (ghostInfo.DefaultClientInstantiationType == GhostAuthoringComponent.ClientInstantionType.OwnerPredicted)
            {
                codeGen.GenerateFragment("GHOST_OWNER_PREDICTED_DEFAULT", replacements);
            }
            else if (ghostInfo.DefaultClientInstantiationType == GhostAuthoringComponent.ClientInstantionType.Predicted)
            {
                codeGen.GenerateFragment("GHOST_PREDICTED_DEFAULT", replacements);
            }

            replacements.Clear();
            foreach (var ns in imports)
            {
                if (ns != null && ns != "")
                {
                    replacements["GHOST_USING"] = ns;
                    codeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);
                }
            }

            replacements.Clear();
            replacements.Add("GHOST_NAME", ghostInfo.name);
            codeGen.GenerateFile(assetPath, ghostInfo.RootPath, ghostInfo.UpdateSystemPath, replacements, batch);
        }
    }
}
