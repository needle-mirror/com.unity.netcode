#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class GhostAdapterUtils
    {
#if UNITY_EDITOR
        public static PredictionCallbackHelper CreatePredictionCallbackHelperPrefab(string name, bool skipAutoRegistration = false)
        {
            PredictionCallbackHelper prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, name, skipAutoRegistration, typeof(PredictionCallbackHelper)).GetComponent<PredictionCallbackHelper>();
            prefab.GetComponent<GhostAdapter>().DefaultGhostMode = GhostMode.Predicted;
            prefab.CallbackHolder = ScriptableObject.CreateInstance<MonoEventCallbackScriptableObject>(); // to be able to register callback on Awake before Instantiation

            return prefab;
        }
#endif
    }
}
#endif
