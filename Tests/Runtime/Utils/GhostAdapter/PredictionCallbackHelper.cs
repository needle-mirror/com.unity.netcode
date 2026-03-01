#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal struct DummyInput : IInputComponentData
    {
        public int value;
    }

    internal struct SomeBridgedValue : IComponentData
    {
        [GhostField] public int value;
    }
    /// <summary>
    /// Test helper so we don't have to write tons of different GhostBehaviours for prediction tests.
    /// </summary>
    internal partial class PredictionCallbackHelper : GhostBehaviour
    {
        public static List<PredictionCallbackHelper> ServerInstances;
        public static List<PredictionCallbackHelper> ClientInstances;
        public event Action<GameObject> OnPredictionEvent;
        public event Action<GameObject> OnInputEvent;
        public event Action<GameObject> OnStart;
        public event Action<GameObject> OnUpdate;
        public event Action<GameObject> OnFixedUpdate;
        public event Action<GameObject> OnLateUpdate;
        public event Action<GameObject> OnEnableEvent;
        public event Action<GameObject> OnDisableEvent;
        public event Action<GameObject> OnDestroyEvent;

        public MonoEventCallbackScriptableObject CallbackHolder;

        public GhostComponentRef<DummyInput> Input;
        public GhostField<int> SomeGhostField;
        public GhostComponentRef<SomeBridgedValue> SomeBridgedVar;

        static PredictionCallbackHelper()
        {
            Reset();
        }
        public static void Reset()
        {
            ServerInstances = new List<PredictionCallbackHelper>();
            ClientInstances = new List<PredictionCallbackHelper>();
        }

        public override void Awake()
        {
            base.Awake();
            if (!Ghost.World.IsServer()) ClientInstances.Add(this);
            if (Ghost.World.IsServer()) ServerInstances.Add(this);
            if (CallbackHolder != null) CallbackHolder.TriggerAwake(gameObject);
        }

        protected void Start()
        {
            OnStart?.Invoke(gameObject);
            if (CallbackHolder != null) CallbackHolder.TriggerStart(gameObject);
        }

        public void Update()
        {
            OnUpdate?.Invoke(gameObject);
        }

        public void FixedUpdate()
        {
            OnFixedUpdate?.Invoke(gameObject);
        }

        public void LateUpdate()
        {
            OnLateUpdate?.Invoke(gameObject);
        }

        public void OnEnable()
        {
            OnEnableEvent?.Invoke(gameObject);
            if (CallbackHolder != null) CallbackHolder.TriggerOnEnable(gameObject);
        }

        public void OnDisable()
        {
            OnDisableEvent?.Invoke(gameObject);
        }

        public override void OnDestroy()
        {
            ServerInstances.Remove(this);
            ClientInstances.Remove(this);
            OnDestroyEvent?.Invoke(gameObject);
            base.OnDestroy(); // TODO-release this flow is still not great... easy to forget to call base.Awake and base.OnDestroy? Is it that bad?
            // TODO-release it's also tricky, since base.OnDestroy needs to be called at the end of the OnDestroy, so that the rest of the OnDestroy above can still access entity things
        }

        public override void GatherInput(float tickedDeltaTime)
        {
            OnInputEvent?.Invoke(gameObject);
        }

        public override void PredictionUpdate(float tickedDeltaTime)
        {
            OnPredictionEvent?.Invoke(gameObject);
            if (CallbackHolder != null) CallbackHolder.TriggerOnPrediction(gameObject);
        }

        public void Dispose()
        {
            ClearEvents();
            CallbackHolder = null;
        }

        public void ClearEvents()
        {
            // Note: those two are the most important to dispose, as they'll be called while the containing scene is unloaded in Teardown(),
            // potentially executing test logic while tearing down the test.
            OnDestroyEvent = null;
            OnDisableEvent = null;
            // Other events just for good measure
            OnPredictionEvent = null;
            OnInputEvent = null;
            OnUpdate = null;
            OnFixedUpdate = null;
            OnLateUpdate = null;
            OnEnableEvent = null;
            CallbackHolder?.ClearEvents();
        }
    }
}
#endif
