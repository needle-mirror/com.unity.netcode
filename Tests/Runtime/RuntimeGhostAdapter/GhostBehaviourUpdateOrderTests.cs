#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class GhostBehaviourUpdateOrderTests
    {
        public enum EventType
        {
            Awake,
            Start,
            Update,
            PredictionUpdate,
            InputUpdate,
            LateUpdate
        }
        //Validate that PredictionUpdate and InputUpdate are called only after Start
        [Test]
        public async Task PreditionAndInputUpdateRespectStartOrder()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var predictedPrefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("Predicted", autoRegister: false);
            var runnerPrefab = GhostAdapterUtils.CreatePredictionCallbackHelperPrefab("Runner", autoRegister: false);
            Netcode.RegisterPrefab(runnerPrefab.gameObject);
            var runnerObj = UnityEngine.Object.Instantiate(runnerPrefab);
            runnerObj.CallbackHolder = null; // make sure the following callbacks execute only on the target object, not the runner.
            var authoringComponent = predictedPrefab.GetComponent<GhostAdapter>();
            authoringComponent.DefaultGhostMode = GhostMode.Predicted;
            authoringComponent.HasOwner = true; // for InputBehaviour
            Netcode.RegisterPrefab(predictedPrefab.gameObject);
            List<EventType> clientEvents = new();
            List<EventType> serverEvents = new();

            List<EventType> GetEventList(GameObject go)
            {
                return go.GetComponent<GhostAdapter>().World.IsServer() ? serverEvents : clientEvents;
            }
            predictedPrefab.CallbackHolder.OnAwake += go =>
            {
                GetEventList(go).Add(EventType.Awake);
                go.GetComponent<PredictionCallbackHelper>().OnStart += instance =>
                {
                    GetEventList(instance).Add(EventType.Start);
                };
                go.GetComponent<PredictionCallbackHelper>().OnUpdate += instance =>
                {
                    GetEventList(instance).Add(EventType.Update);
                };
                go.GetComponent<PredictionCallbackHelper>().OnPredictionEvent += instance =>
                {
                    var eventList = GetEventList(instance);
                    if (eventList.Last() != EventType.PredictionUpdate) // makes it easier to test, we don't care about all the replay prediction updates, only one entry is enough
                        eventList.Add(EventType.PredictionUpdate);
                };
                go.GetComponent<PredictionCallbackHelper>().OnInputEvent += instance =>
                {
                    GetEventList(instance).Add(EventType.InputUpdate);
                };
                go.GetComponent<PredictionCallbackHelper>().OnLateUpdate += instance =>
                {
                    GetEventList(instance).Add(EventType.LateUpdate);
                };
            };

            await testWorld.ConnectAsync(enableGhostReplication:true);
            await testWorld.TickMultipleAsync(8);

            void RunOnceOnUpdate(GameObject _)
            {
                var instance = UnityEngine.Object.Instantiate(predictedPrefab);
                instance.GetComponent<GhostAdapter>().OwnerNetworkId = new NetworkId() { Value = 1 };
            }

            runnerObj.OnUpdate += RunOnceOnUpdate; // make sure the order of events is realistic. We don't care about Awake being called from a test runner, we care about being called from an actual Update
            await testWorld.TickMultipleAsync(1);
            runnerObj.OnUpdate -= RunOnceOnUpdate;
            await testWorld.TickMultipleAsync(7);

            Debug.Log("client: " + string.Join(", ", clientEvents));
            Debug.Log("server: " + string.Join(", ", serverEvents));

            // OnUpdate should be called after start
            // PredictionUpdate should be called after start
            // PredictionUpdate should be called after Update
            // InputUpdate should be called after start
            // Start need to be after Awake, since server side in Update loop we can't insert PredictionUpdate in between (so we can't use Start as a "predicted Start")
            var expectedOrderServer = new List<EventType>() { EventType.Awake, EventType.Start, EventType.PredictionUpdate, EventType.LateUpdate };
            var expectedOrderClient = new List<EventType>() { EventType.Awake, EventType.Start, EventType.LateUpdate}; // server side, the spawn is done in Update(), so the Awake, Start order is different
            var expectedOrder = new List<EventType>() { EventType.Update, EventType.InputUpdate, EventType.PredictionUpdate, EventType.LateUpdate, EventType.Update, EventType.InputUpdate, EventType.PredictionUpdate, EventType.LateUpdate, EventType.Update, EventType.InputUpdate, EventType.PredictionUpdate, EventType.LateUpdate, EventType.Update, EventType.InputUpdate, EventType.PredictionUpdate, EventType.LateUpdate, EventType.Update,

            };
            expectedOrderClient.AddRange(expectedOrder);
            expectedOrderServer.AddRange(expectedOrder);
            CollectionAssert.AreEqual(expectedOrderClient, clientEvents, $"expected {string.Join(", ", expectedOrderClient)}\n\nbut got {string.Join(", ", clientEvents)}");
            expectedOrderServer.RemoveAll(e => e == EventType.InputUpdate);
            for (int i = 0; i < 2; i++) // server did a few ticks more before the client spawned
            {
                // TODO-hack revert changes WaitForEndOfFrame
                expectedOrderServer.Add(EventType.PredictionUpdate);
                expectedOrderServer.Add(EventType.LateUpdate);
                expectedOrderServer.Add(EventType.Update);
            }

            CollectionAssert.AreEqual(expectedOrderServer, serverEvents, $"expected {string.Join(", ", expectedOrderServer)}\n\nbut was {string.Join(", ", serverEvents)}");
        }
    }
}
#endif
