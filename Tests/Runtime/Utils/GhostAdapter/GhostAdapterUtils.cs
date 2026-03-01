#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class GhostAdapterUtils
    {
#if UNITY_EDITOR
        /// <summary>
        /// Creates a prefab with <see cref="PredictionCallbackHelper"/> attached to it and prediction mode already set.
        /// See the <see cref="autoRegister"/> param for modifying the returned generated prefab.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="autoRegister">In order to modify the generated prefab, you need to prevent it from registering automatically, modify it, then register it yourself using <see cref="Netcode.RegisterPrefab(GameObject,World)"/></param>
        /// <returns></returns>
        public static PredictionCallbackHelper CreatePredictionCallbackHelperPrefab(string name, bool autoRegister = true)
        {
            PredictionCallbackHelper prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, name, autoRegister: false, typeof(PredictionCallbackHelper)).GetComponent<PredictionCallbackHelper>();
            prefab.GetComponent<GhostAdapter>().DefaultGhostMode = GhostMode.Predicted;
            prefab.CallbackHolder = ScriptableObject.CreateInstance<MonoEventCallbackScriptableObject>(); // to be able to register callback on Awake before Instantiation

            if (autoRegister) Netcode.RegisterPrefab(prefab.gameObject);
            return prefab;
        }
#endif
    }
}
#endif
