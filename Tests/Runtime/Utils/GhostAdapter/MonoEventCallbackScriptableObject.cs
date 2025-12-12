#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// To be able to register callbacks before Awake is called. You can't register a callback directly on a prefab, the callback will be lost on the object
    /// that's instantiated. With the SO, you can register the callback on the SO, and then register the SO on the prefab and the callback will be kept.
    /// </summary>
    public class MonoEventCallbackScriptableObject : ScriptableObject
    {
        public event Action<GameObject> OnAwake;
        public event Action<GameObject> OnStart;
        public event Action<GameObject> OnEnableEvent;
        public event Action<GameObject> OnPrediction;

        public void TriggerAwake(GameObject self)
        {
            OnAwake?.Invoke(self);
        }

        public void TriggerStart(GameObject self)
        {
            OnStart?.Invoke(self);
        }

        public void TriggerOnEnable(GameObject self)
        {
            OnEnableEvent?.Invoke(self);
        }

        public void TriggerOnPrediction(GameObject self)
        {
            OnPrediction?.Invoke(self);
        }
    }
}
#endif
