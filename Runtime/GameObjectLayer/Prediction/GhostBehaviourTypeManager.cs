#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
#if !UNITY_DISABLE_MANAGED_COMPONENTS
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Assertions;
#endif

namespace Unity.NetCode
{
    /// <summary>
    /// Contains per <see cref="GhostBehaviour"/> metadata that's known at compile time, like whether this GhostBehaviour has a PredictionUpdate method (so
    /// we can skip it if it doesn't without having to dereference a managed GhostBehaviour uselessly)
    /// Design Note: we can cache even more info here, can really be serialization metadata as well.
    /// We may even have on per-ghost-prefab metadata (in another struct).
    /// </summary>
    [Serializable]
    internal struct GhostBehaviourTypeInfo : IComparable<GhostBehaviourTypeInfo>
    {
        /// <summary>
        /// The Burst.Runtime.Hash64 of the fully qualified type name.
        /// </summary>
        public ulong TypeHash;
        /// <summary>
        /// The bucket in which the behaviour update (respects the ScriptSortOrder).
        /// This is static information that doesn't change in a build and only needs to be refreshed on playmode in editor
        /// </summary>
        public short UpdateBucket;
        /// <summary>
        /// Determined at build time (or when entering playmode in editor), check if the prediction update method is non empty (or the class define it)
        /// and should be invoked.
        /// By default, GhostBehaviour don't get their PredictionUpdate called, unless overridden by a user child class.
        /// </summary>
        public bool HasPredictionUpdate;
        public bool HasInputUpdate;
        public bool HasNetworkedFixedUpdate;
        public bool HasStart;

        public override string ToString()
        {
            return $"hash: {this.TypeHash} bucket:{UpdateBucket} sortOrder: {this.ScriptSortOrder} hasPrediction:{this.HasPredictionUpdate}";
        }

        public bool AnyHasUpdate()
        {
            return HasPredictionUpdate | HasInputUpdate | HasNetworkedFixedUpdate;
        }

        /// <summary>
        /// The script sort order of the behaviour
        /// </summary>
        public int ScriptSortOrder;

        public int CompareTo(GhostBehaviourTypeInfo other)
        {
            return TypeHash.CompareTo(other.TypeHash);
        }
    }

    #if !UNITY_DISABLE_MANAGED_COMPONENTS

    /// <summary>
    /// Our main point of access for prebuilt GhostBehaviour type info, like sort order.
    /// Uses <see cref="GhostBehaviourSortOrder"/> to serialize data at build time.
    ///
    /// Design note: The <see cref="GhostBehaviourSortOrder"/> data can be the type manager data itself.
    /// For now it only keep and store the necessary information for the script order (that it is what we need).
    /// We don't keep the instance because it is not necessary, we are already re-mapping from type -> GhostBehaviourTypeInfo
    /// </summary>
    internal class GhostBehaviourTypeManager
    {
        public int UpdateBucketCount;
        //Implementation note: this can be made burst compatible in case necessary via double lookup (type-hash, hash->GhostBehaviourTypeInfo)
        public Dictionary<Type, GhostBehaviourTypeInfo> GhostBehaviourInfos;

        internal const string m_AssetName = "script-order";
        internal static readonly string m_AssetPath = $"Assets/{Netcode.kTempBuildFolder}/{m_AssetName}.asset";

        internal GhostBehaviourSortOrder SerializedData;

        public GhostBehaviourTypeManager()
        {
            TypeManager.Initialize();
            GhostBehaviourInfos = new Dictionary<Type, GhostBehaviourTypeInfo>(1024);
        }

        public ulong GetGhostBehaviourStableTypeHash(Type type)
        {
            var typeIndex = TypeManager.GetTypeIndex(type);
            var hash = TypeManager.GetTypeInfo(typeIndex).StableTypeHash;

            return hash;
        }

        internal void InitializeGhostBehaviourInfos()
        {
            if (Application.isEditor)
            {
                //Potentially the GhostBehaviourSortOrder an be the manager itself or the data we care about
                //pre-computed at build time and exported.
                SerializedData = ScriptableObject.CreateInstance<GhostBehaviourSortOrder>();
                SerializedData.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild | HideFlags.DontUnloadUnusedAsset;
                SerializedData.InitializeSortOrderFromScriptSortOrder(withExportLog: NetDebugSystem.GetDefaultNetDebug().LogLevel == NetDebug.LogLevelType.Debug);
            }
            else
            {
                // This is preloaded by a IPreprocessBuildWithReport which sets it as a preloaded asset (see PlayerSettings.GetPreloadedAssets());
                var savedSortOrders = Resources.FindObjectsOfTypeAll<GhostBehaviourSortOrder>();
                Assert.IsTrue(savedSortOrders.Length == 1, $"sanity check failed, invalid count for saved {nameof(GhostBehaviourSortOrder)}, found {savedSortOrders.Length} instances.");
                SerializedData = savedSortOrders[0];
                Assert.IsTrue(SerializedData != null, $"sanity check failed, SerializedData for {nameof(GhostBehaviourSortOrder)} is null");

                Debug.Log($"Netcode: {nameof(GhostBehaviourSortOrder)} loaded successfully, contains {SerializedData.Behaviours.Length} items");
            }
            UpdateBucketCount = SerializedData.NumScriptSortOrder;
            for (var index = 0; index < SerializedData.Behaviours.Length; index++)
            {
                var t = SerializedData.Behaviours[index];

                var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(t.TypeHash);
                if (typeIndex == TypeIndex.Null)
                {
                    Debug.LogError($"Cannot find behaviour with hash: {t.TypeHash} index:{index}.");
                    continue;
                }

                // Note: entities TypeManager won't support managed types in the future. However, from slack convo with ChrisR, there should be an engine side alternative that would. We can replace our current call when it does
                var type = TypeManager.GetType(typeIndex);
                GhostBehaviourInfos.Add(type, t);
            }
        }
    }
    #endif
}
#endif
