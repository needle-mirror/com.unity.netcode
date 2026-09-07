using System;
using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    internal class GhostObjectUtils
    {
        /// <summary>
        /// Creates a prefab with <see cref="PredictionCallbackHelper"/> attached to it and prediction mode already set.
        /// See the <see cref="autoRegister"/> param for modifying the returned generated prefab.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="autoRegister">In order to modify the generated prefab, you need to prevent it from registering automatically, modify it, then register it yourself using <see cref="Netcode.RegisterPrefab(GameObject,World)"/></param>
        /// <returns>The <see cref="PredictionCallbackHelper"/> component on the created and registered prefab</returns>
        public static PredictionCallbackHelper CreatePredictionCallbackHelperPrefab(string name, bool autoRegister = true)
        {
            PredictionCallbackHelper prefab = GhostObjectPrefabHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, name, autoRegister: false, typeof(PredictionCallbackHelper)).GetComponent<PredictionCallbackHelper>();
            prefab.GetComponent<GhostObject>().DefaultGhostMode = GhostMode.Predicted;
            prefab.CallbackHolder = ScriptableObject.CreateInstance<MonoEventCallbackScriptableObject>(); // to be able to register callback on Awake before Instantiation

            if (autoRegister) Netcode.RegisterPrefab(prefab.gameObject);
            return prefab;
        }

        /// <summary>
        /// Creates a prefab with <see cref="PredictionCallbackHelper"/> attached to a child GameObject and prediction mode already set.
        /// </summary>
        /// <param name="name">What to set as the <see cref="UnityEngine.Object.name"/>. The child prefab will be set with "{name}_Child".</param>
        /// <param name="autoRegister">In order to modify the generated prefab, you need to prevent it from registering automatically, modify it, then register it yourself using <see cref="Netcode.RegisterPrefab(GameObject,World)"/></param>
        /// <returns>The root GameObject</returns>
        public static GameObject CreateNestedPredictionCallbackHelperPrefab(string name, bool autoRegister = true)
        {
            var prefabRoot = GhostObjectPrefabHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, name, autoRegister: false, isNested: true, componentTypes: typeof(PredictionCallbackHelper));
            prefabRoot.GetComponent<GhostObject>().DefaultGhostMode = GhostMode.Predicted;

            var callbackHelper = prefabRoot.GetComponentInChildren<PredictionCallbackHelper>();
            callbackHelper.CallbackHolder = ScriptableObject.CreateInstance<MonoEventCallbackScriptableObject>(); // to be able to register callback on Awake before Instantiation

            if (autoRegister) Netcode.RegisterPrefab(prefabRoot.gameObject);
            return prefabRoot;
        }
    }
}
