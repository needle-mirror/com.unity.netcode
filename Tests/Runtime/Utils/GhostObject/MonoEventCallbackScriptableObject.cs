using System;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// To be able to register callbacks before Awake is called. You can't register a callback directly on a prefab, the callback will be lost on the object
    /// that's instantiated. With the SO, you can register the callback on the SO, and then register the SO on the prefab and the callback will be kept.
    /// </summary>
    // [CreateAssetMenu(fileName = "MonoEventCallbackScriptableObject", menuName = "Sam/GameSettingsScriptableObject", order = 2)] // only needed at test writing time when creating prefabs, but else never needed.
    class MonoEventCallbackScriptableObject : ScriptableObject
    {
        public event Action<GameObject> OnAwake;
        public event Action<GameObject> OnStart;
        public event Action<GameObject> OnEnableEvent;
        public event Action<GameObject> OnPrediction;
        public event Action<GameObject> OnFixedPrediction;
        public event Action<GameObject> OnDestroy;


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

        public void TriggerOnFixedPrediction(GameObject self)
        {
            OnFixedPrediction?.Invoke(self);
        }
		
        public void TriggerOnDestroy(GameObject self)
        {
            OnDestroy?.Invoke(self);
        }

        public void ClearEvents()
        {
            OnAwake = null;
            OnStart = null;
            OnEnableEvent = null;
            OnPrediction = null;
            OnDestroy = null;
        }
    }
}
