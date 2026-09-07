using System.Collections.Generic;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    internal abstract class GhostBehaviourWithPriority : GhostBehaviour
    {
        public List<MonoBehaviour> update;
        public List<MonoBehaviour> predictionUpdate;
        public delegate void OnPredictionUpdate(GameObject go);
        public OnPredictionUpdate OnPredictionEvent;

        public void Update()
        {
            update?.Add(this);
        }
        public override void PredictionUpdate(float tickedDeltaTime)
        {
            predictionUpdate?.Add(this);
            OnPredictionEvent?.Invoke(this.gameObject);
        }
    }
}
