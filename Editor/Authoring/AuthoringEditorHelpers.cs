using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    //Bunch of helper functions for editing and toggling modifiers to the various options
    static class AuthoringEditorHelper
    {
        public static void FieldDrawer(ComponentItem item)
        {
            var prefabType = item.PrefabType;
            if ((prefabType & GhostPrefabType.Server) == 0)
                return;

            EditorGUILayout.Separator();
            EditorGUILayout.LabelField("Fields");
            EditorGUI.BeginDisabledGroup(true);
            //FIXME : this is not correct. We should display the current serialized fields (and we need the serializer for that)
            //along with the correct values (that depend on the serialization template too)
            for (int fi = 0; fi < item.comp.fields?.Length; ++fi)
            {
                EditorGUILayout.TextField("Name", item.comp.fields[fi].name);
                ++EditorGUI.indentLevel;
                EditorGUILayout.IntField("Quantization", item.comp.fields[fi].quantization);
                EditorGUILayout.EnumPopup("Interpolation Mode", item.comp.fields[fi].smoothing);
                --EditorGUI.indentLevel;
            }
            EditorGUI.EndDisabledGroup();
        }

        public static ulong VariantDrawer(ComponentItem item, GhostComponentVariantLookup variantLookup)
        {
            var variantHash = item.Variant;
            bool isSerialized = variantLookup.GetAllVariants(ComponentType.ReadWrite(item.comp.managedType), out var list);

            using (new EditorGUI.DisabledGroupScope(!isSerialized))
            {
                using var toogle = new EditorGUILayout.ToggleGroupScope("Variant", item.modifyVariant);
                item.modifyVariant = toogle.enabled;
                if (!item.modifyVariant)
                {
                    EditorGUILayout.LabelField("Serialization Variant",
                        !isSerialized ? "Not Serialized" : list[0].Name);
                    GUI.changed |= variantHash != 0;
                    return 0;
                }

                //Cache and patch the list of available variants for the types here by
                //adding by default also the DoNotSerialize special one.
                //Is added here and not to the list of variants for the type because the assumptions is that
                //the varians cache only contains generated variants / serializer. Since the DoNotSerialize
                //does not, that would have broke a lot of logics that rely on that.
                if (item.availableVariants == null)
                {
                    var names = list.Select(i => i.Name).ToList();
                    names.Add("DoNotSerialize");
                    item.variantNames = names.ToArray();
                    item.availableVariants = new ulong[list.Count + 1];
                    for (int i = 0; i < list.Count; ++i)
                        item.availableVariants[i] = list[i].Hash;
                    item.availableVariants[list.Count] = GhostVariantsUtility.DoNotSerializeHash(item.comp.managedType);
                }

                int index = System.Array.IndexOf(item.availableVariants, variantHash);
                if (index < 0)
                {
                    if (variantHash != 0)
                        Debug.LogWarning($"Variation with hash {variantHash} not found.");
                    index = 0;
                }
                var idx = EditorGUILayout.Popup(new GUIContent("Serialization Variant"), index, item.variantNames);
                GUI.changed |= idx != index;
                return item.availableVariants[idx];
            }
        }

        public static GhostSendType SendMaskDrawer(ComponentItem item)
        {
            var sendType = item.SendType;
            using var toogle = new EditorGUILayout.ToggleGroupScope("SendMask", item.modifySendType);
            item.modifySendType = toogle.enabled;
            ++EditorGUI.indentLevel;
            sendType = (GhostSendType) EditorGUILayout.EnumPopup("Send Data To", sendType);
            --EditorGUI.indentLevel;
            return sendType;
        }

        public static bool SendChildDrawer(ComponentItem item)
        {
            using (new EditorGUI.DisabledGroupScope(item.entityIndex == 0))
            {
                bool sendForChild = item.SendForChild;
                using var toogle = new EditorGUILayout.ToggleGroupScope("SendToChildEntity", item.modifySendForChild);
                item.modifySendForChild = toogle.enabled;
                ++EditorGUI.indentLevel;
                sendForChild = EditorGUILayout.Toggle("Send For This Child", sendForChild);
                --EditorGUI.indentLevel;
                return sendForChild;
            }
        }

        public static GhostPrefabType PrefabTypeDrawer(ComponentItem item)
        {
            var prefabType = item.PrefabType;
            using var toogle = new EditorGUILayout.ToggleGroupScope("PrefabType", item.modifyPrefabType);
            item.modifyPrefabType = toogle.enabled;
            var newPrefab = GhostPrefabType.None;
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
            return newPrefab;
        }
    }
}
