using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using System.Reflection;

namespace Unity.NetCode
{
    [DisallowMultipleComponent]
    public class GhostAuthoringComponent : MonoBehaviour, IDeclareReferencedPrefabs, ISerializationCallbackReceiver
    {
        public void DeclareReferencedPrefabs(List<GameObject> referencedPrefabs)
        {
#if UNITY_EDITOR
            bool isPrefab = !gameObject.scene.IsValid() || ForcePrefabConversion;
            if (!isPrefab)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(prefabId);
                GameObject prefab = null;
                if (!String.IsNullOrEmpty(path))
                    prefab = (GameObject) UnityEditor.AssetDatabase.LoadAssetAtPath(path, typeof(GameObject));
                if (prefab != null)
                    referencedPrefabs.Add(prefab);
            }
#endif
        }
#if UNITY_EDITOR
        //Not the fastest way but on average is taking something like 10-50us or less to find the type,
        //so seem reasonably fast even with tens of components per prefab
        Type GetTypeFromFullTypeName(string fullName)
        {
            Type type = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(fullName, false);
                type = t;
                if (type != null)
                    break;
            }

            return type;
        }

        // Remove modifiers is one of the conditions is true:
        // - If a gameobject children is removed
        // - If the component type has been removed or as an attribute that invalidate prefab override
        void ValideModifiers()
        {
            bool RemoveInvalidOverride(in ComponentOverride mod, Transform root)
            {
                if (mod.gameObject == null || !mod.gameObject.transform.IsChildOf(root))
                    return true;
                var compType = GetTypeFromFullTypeName(mod.fullTypeName);
                if (compType == null || compType.GetCustomAttribute<DontSupportPrefabOverrides>() != null)
                    return true;
                return false;
            }

            var parent = transform;
            for (int i = 0; i < ComponentOverrides.Count;)
            {
                var mod = ComponentOverrides[i];
                if (RemoveInvalidOverride(mod, parent))
                {
                    ComponentOverrides.RemoveAt(i);
                    //Prefab should be marked as dirty in that case
                    if (!gameObject.scene.IsValid())
                        UnityEditor.EditorUtility.SetDirty(gameObject);
                }
                else
                {
                    ++i;
                }
            }
        }

        void OnValidate()
        {
            // Modifiers validation must be done also if scene is valid
            ValideModifiers();

            if (gameObject.scene.IsValid())
                return;

            var path = UnityEditor.AssetDatabase.GetAssetPath(gameObject);
            if (String.IsNullOrEmpty(path))
                return;
            var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
            if (guid != prefabId)
            {
                UnityEditor.Undo.RecordObject(this, "");
                prefabId = guid;
            }
        }


        // return the index of the modifier and valid modifier instance. -1 if nothing is found. </returns>
        internal ComponentOverride GetPrefabModifier(string fullTypeName, EntityGuid guid)
        {
            for (int i = 0; i < ComponentOverrides.Count; ++i)
            {
                var element = ComponentOverrides[i];
                if (element.fullTypeName == fullTypeName &&
                    element.gameObject.GetInstanceID() == guid.OriginatingId &&
                    element.entityGuid == guid.b)
                {
                    return element;
                }
            }
            return null;
        }

        /// <summary>
        /// Does a depth first search to find an element in the transform hierarchy matching the query
        /// </summary>
        /// <param name="current">root element to search from</param>
        /// <param name="query">used for matching every transform element when walking the hierarchy</param>
        /// <param name="childGameObject">variable for first element matching the query. Will be set to null otherwise.</param>
        /// <returns>Returns the first element in the transform hierarchy matching the query</returns>
        private static bool TryGetFirstMatching(Transform current, Func<Transform, bool> query, out GameObject childGameObject)
        {
            if (query(current))
            {
                childGameObject = current.gameObject;
                return true;
            }

            if (current.childCount == 0)
            {
                childGameObject = null;
                return false;
            }

            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (TryGetFirstMatching(child, query, out childGameObject))
                {
                    return true;
                }
            }
            childGameObject = null;
            return false;
        }

        internal ComponentOverride AddPrefabOverride(string fullTypeName, EntityGuid entityGuid)
        {
            bool MatchesOriginatingGameObject(Transform t) => t.gameObject.GetInstanceID() == entityGuid.OriginatingId;
            if (!TryGetFirstMatching(gameObject.transform, MatchesOriginatingGameObject, out var childGameObject))
            {
                throw new ArgumentException($"{entityGuid.OriginatingId}: didn't match any game object in {gameObject.name}");
            }
            var modifier = new ComponentOverride
            {
                gameObject = childGameObject,
                entityGuid = entityGuid.b,
                fullTypeName = fullTypeName,
            };
            ComponentOverrides.Add(modifier);
            return modifier;
        }

        internal void RemovePrefabOverride(ComponentOverride modifier)
        {
            ComponentOverrides.Remove(modifier);
        }

#endif

        public enum GhostModeMask
        {
            Interpolated = 1,
            Predicted = 2,
            All = 3
        }

        public enum GhostMode
        {
            Interpolated,
            Predicted,
            OwnerPredicted
        }

        public enum GhostOptimizationMode
        {
            Dynamic,
            Static
        }

        internal class ComponentOverride
        {
            public const int UseDefaultValue = -1;

            //For sake of serialization we are using the type fullname because we can't rely on the TypeIndex for the component.
            //StableTypeHash cannot be used either because layout or fields changes affect the hash too (so is not a good candidate for that)
            public string fullTypeName;
            //The gameObject reference (root or child)
            public GameObject gameObject;
            //The entity guid reference
            [NonSerialized]public ulong entityGuid;
            //Override what mode are available for that type. if 0, the component is removed from the prefab/entity instance
            public int PrefabType = UseDefaultValue;
            //Override to witch client it will be sent to.
            public int OwnerPredictedSendType = UseDefaultValue;
            public int SendForChild = UseDefaultValue;
            //Select witch variant we would like to use. 0 means the default
            public ulong ComponentVariant;
            //Editor only thing
            public bool isExpanded { get; set; }
        }

        /// <summary>
        /// Force the ghost conversion to treat this GameObject as if it was a prefab. This is used if you want to programmatically create
        /// a ghost prefab as a GameObject and convert it to an Entity prefab with ConvertGameObjectHierarchy.
        /// </summary>
        [NonSerialized] public bool ForcePrefabConversion;

        [Tooltip("The ghost mode used if you do not manually change it using a GhostSpawnClassificationSystem. If set to OwnerPredicted the ghost will be predicted on hte client which owns it and interpolated elsewhere. You must not change the mode using a classification system if using owner predicted.")]
        public GhostMode DefaultGhostMode = GhostMode.Interpolated;
        [Tooltip("The ghost modes supported by this ghost. This will perform some more optimizations at authoring time but make it impossible to change ghost mode at runtime.")]
        public GhostModeMask SupportedGhostModes = GhostModeMask.All;
        [Tooltip("This setting is only for optimization, the ghost will be sent when modified regardless of this setting. Optimizing for static makes snapshots slightly larger when the change, but smaller when they do not change.")]
        public GhostOptimizationMode OptimizationMode = GhostOptimizationMode.Dynamic;
        [Tooltip("If not all ghosts can fit in a snapshot only the most important ghosts will be sent. Higher importance means the ghost is more likely to be sent.")]
        public int Importance = 1;
        public string prefabId = "";
        public string Name;
        /// <summary>
        /// Add a GhostOwnerComponent tracking which connection owns this component.
        /// You must set the GhostOwnerComponent to a valid NetworkIdComponent.Value at runtime.
        /// </summary>
        [Tooltip("Add a GhostOwnerComponent tracking which connection owns this component. You must set the GhostOwnerComponent to a valid NetworkIdComponent.Value at runtime.")]
        public bool HasOwner;
        /// <summary>
        /// Automatically send all ICommandData buffers if the ghost is owned by the current connection,
        /// AutoCommandTarget.Enabled is true and the ghost is predicted.
        /// </summary>
        [Tooltip("Automatically send all ICommandData buffers if the ghost is owned by the current connection, AutoCommandTarget.Enabled is true and the ghost is predicted.")]
        public bool SupportAutoCommandTarget;
        /// <summary>
        /// Force this ghost to be quantized and copied to the snapshot format once for all connections instead
        /// of once per connection. This can save CPU time in the ghost send system if the ghost is
        /// almost always sent to at least one connection, and it contains many serialized components, serialized
        /// components on child entities or serialized buffers. A common case where this can be useful is the ghost
        /// for the character / player.
        /// </summary>
        [Tooltip("Force this ghost to be quantized and copied to the snapshot format once for all connections instead of once per connection. This can save CPU time in the ghost send system if the ghost is almost always sent to at least one connection, and it contains many serialized components, serialized components on child entities or serialized buffers. A common case where this can be useful is the ghost for the character / player.")]
        public bool UsePreSerialization;
        internal List<ComponentOverride> ComponentOverrides = new List<ComponentOverride>();

        [Serializable]
        private struct OverrideValue
        {
            public ulong Value;
            public int OverrideIndex;

            public OverrideValue(int key, ulong value)
            {
                OverrideIndex = key;
                Value = value;
            }
        }

        [SerializeField] private GameObject[] RefGameObjects;
        [SerializeField] private ulong[] RefGuids;
        [SerializeField] private string[] ComponentNames;
        [SerializeField] private OverrideValue[] PrefabTypeOverrides;
        [SerializeField] private OverrideValue[] SendTypeOverrides;
        [SerializeField] private OverrideValue[] VariantOverrides;
        [SerializeField] private OverrideValue[] SendForChild;

        //Custom serialization hook for the overrides.
        public void OnBeforeSerialize()
        {
            var t1 = new List<OverrideValue>(ComponentOverrides.Count);
            var t2 = new List<OverrideValue>(ComponentOverrides.Count);
            var t3 = new List<OverrideValue>(ComponentOverrides.Count);
            var t4 = new List<OverrideValue>(ComponentOverrides.Count);
            RefGameObjects = new GameObject[ComponentOverrides.Count];
            RefGuids = new ulong[ComponentOverrides.Count];
            ComponentNames = new string[ComponentOverrides.Count];
            int index = 0;
            foreach (var m in ComponentOverrides)
            {
                RefGameObjects[index] = m.gameObject;
                ComponentNames[index] = m.fullTypeName;
                RefGuids[index] = m.entityGuid;
                if (m.PrefabType != ComponentOverride.UseDefaultValue)
                    t1.Add(new OverrideValue(index, (ulong) m.PrefabType));
                if (m.OwnerPredictedSendType != ComponentOverride.UseDefaultValue)
                    t2.Add(new OverrideValue(index, (ulong) m.OwnerPredictedSendType));
                if (m.ComponentVariant != 0)
                    t3.Add(new OverrideValue(index, m.ComponentVariant));
                if (m.SendForChild != ComponentOverride.UseDefaultValue)
                    t4.Add(new OverrideValue(index, (ulong)m.SendForChild));
                ++index;
            }

            PrefabTypeOverrides = t1.ToArray();
            SendTypeOverrides = t2.ToArray();
            VariantOverrides = t3.ToArray();
            SendForChild = t4.ToArray();

        }

        public void OnAfterDeserialize()
        {
            if (RefGameObjects == null)
                return;

            int count = RefGameObjects.Length;
            ComponentOverrides.Clear();
            ComponentOverrides.Capacity = RefGameObjects.Length;
            RefGuids ??= new ulong[RefGameObjects.Length];

            for (int i = 0; i < count; ++i)
            {
                var refGameObject = RefGameObjects[i];
                var typeFullName = ComponentNames[i];
                var guid = RefGuids[i];
                ComponentOverrides.Add(new ComponentOverride
                {
                    gameObject = refGameObject,
                    fullTypeName = typeFullName,
                    entityGuid = guid
                });
            }

            foreach (var p in PrefabTypeOverrides)
            {
                ComponentOverrides[p.OverrideIndex].PrefabType = (int) p.Value;
            }

            foreach (var p in SendTypeOverrides)
            {
                ComponentOverrides[p.OverrideIndex].OwnerPredictedSendType = (int) p.Value;
            }

            foreach (var p in VariantOverrides)
            {
                ComponentOverrides[p.OverrideIndex].ComponentVariant = p.Value;
            }

            if(SendForChild !=null)
            {
                foreach (var p in SendForChild)
                {
                    ComponentOverrides[p.OverrideIndex].SendForChild = (int)p.Value;
                }
            }
        }
    }
}
