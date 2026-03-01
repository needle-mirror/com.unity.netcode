
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// The base class for all Monobehaviours in need to access Ghost features.
    /// </summary>
    /// TODO-release@CodeUXOptim We could potentially have code analyzers warning about missing partial on a GhostBehaviour, similar to partial on systems in entities. If we don't it might not be too bad, since users would still get compile errors telling them they need to add the partial keyword
    // TODO-release come back to this, we might not need this
    [RequireComponent(typeof(GhostAdapter))]
    // [MultiplayerRoleRestricted]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    abstract class GhostBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Per ghost component, tracking all GhostBehaviours attached to this ghost
        /// This is here for performance reasons, to batch operations before updating PredictionUpdate
        /// </summary>
        internal struct GhostBehaviourTracking : IComponentData // using an IComponentData so we can do a batch get on all GhostBehaviourTracking (there's no query.ToBufferArray :( )
        {
            public NativeArray<GhostBehaviourTypeInfo> allBehaviourTypeInfo;
            public bool AnyHasUpdate;
        }

        /*
        // TODO-next@prediction when working for new design for prediction: move this to an ADR
        Design notes on how to expose N4E's deltaTime. Note: this is where I'm at so far and don't need to solve this with this PR. We can come back to this in a second PR once we have backported things. We should also discuss potential breaking changes to NetworkTime when folks are back from vacation.

        // Context: as a GO user, I have
        Update()
        {
            Time.deltaTime;
            Time.time;
            // etc.
        }
        FixedUpdate()
        {
            Time.fixedDeltaTime;
            Time.fixedTime;
            // etc.
        }
        N4E side:
        Prediction needs its own deltaTime for each tick. It'll usually be 1/tickRate but will be adjusted by netcode depending on various conditions, so not constant and will change at runtime.
        We push and pop World.Time when in the prediction loop. So a user calling World.Time.deltaTime in a system in the prediction group would see a different deltaTime than a system in the normal SimulationSystemGroup.
        Other tick info is accessible from a singleton SystemAPI.GetSingleton<NetworkTime>();
        // Options:
        // 1. Netcode.Time.TickedDeltaTime. Accessed statically, but can be harder to discover? For binary world setup, NetcodeWorld could also have wrappers for those singletons. So you could call something like this.Ghost.World.TickedDeltaTime? As a user, since you're using binary world setup, you're already familiar with worlds and so know you should use the respective world versions of APIs instead of the static one?
        // 2. this.NetworkTime.deltaTime as a GhostBehaviour property (can't be accessed from non-GhostBehaviour).
        // 3. NetworkTime as a parameter to PredictionUpdate(NetworkTime time) (can't access it from Awake or Start)
        // 4. A combination of 1 and 3
        // 5. change the engine and allow setting Time.deltaTime. For the Prediction scope, this deltaTime would be overriden and reset back outside of this scope. This is close to N4E's flow, where entities allow "pushing and popping" time in a world (they don't use UnityEngine.Time.time, it's World.Time). This would be a big change in behaviour though. And is different from normal GameObject flow. Normal flow is to have a second deltaTime depending on context (FixedUpdate has Time.fixedDeltaTime)
        // 6. change the engine and create a new Time.tickedDeltaTime or Time.networkedDeltaTime (too bad we can't do static extension methods :( --> should be available with C# 14 ). If Netcode is an optional package, then offline users would have fields in their Time class that are useless. --> you could argue that's already the case for users not using physics and fixedDeltaTime.

         Should we just be engine side for 6.7? Why wait. --> being a core package already brings nice things like being able to have "InternalVisibleTo". Adding a core module is more finicky, according to Joel, we need to be careful about increasing build sizes if we introduce too much new unstrippable code. Entities are still adding module things piece by piece.
         */


        /// <summary>
        /// Override this to implement your own prediction logic.
        /// To access current predicted deltaTime, use <see cref="tickedDeltaTime"/>.
        /// To access other time related information, use <see cref="Netcode.NetworkTime"/>.
        /// See "Introduction to Prediction" and other various articles in the manual for more details on prediction.
        /// </summary>
        // Internal Note: if we change the signature of this method, don't forget to update GhostBehaviourSortOrder, it has reflection logic that includes number of parameters and their types
        public virtual void PredictionUpdate(float tickedDeltaTime) { } // TODO-next@domino after backport PRs: name could be TickedUpdate --> not consistent with PredictionSystemGroup for users that want to switch to ECS flows?

        /// <summary>
        /// Override this method to gather your inputs for prediction.
        /// Only gather inputs outside this method if you know what you're doing.
        /// </summary>
        /// <remarks>
        /// This runs in your frame from the <see cref="GhostInputSystemGroup"/> group.
        ///
        /// Here are some reasons why gathering inputs from this method is recommended:
        /// - This method will only get called on clients and on the ghosts the client owns, before the prediction loop. This ensures there's minimal latency between
        /// when an input is detected and the associated network command is sent to the server. It also helps reduce the potential for mispredictions.
        /// - Prediction will modify your inputs to adapt to the current tick it's simulating when rolling back and replaying. You can't gather inputs from prediction.
        /// - If your owner switches, that owner switch gets applied in a system executing before input gathering. If you try to update your inputs from Update()
        /// (which executes before all SimulationGroup systems), you'd get a value for GhostOwnerIsLocal that's not updated yet,
        /// but will be for prediction when it runs. Same for new spawns.
        /// - Your inputs' data is reset automatically for InputEvents right before GatherInput is called TODO-release@doc validate this doc is accurate
        /// - If you use forced input latency (see <see cref="ClientTickRate.ForcedInputLatencyTicks"/>) you MUST use <see cref="GatherInput"/> since Netcode will manipulate
        /// your input's values outside the scope of this method.
        /// </remarks>
        /// <param name="tickedDeltaTime"></param>
        public virtual void GatherInput(float tickedDeltaTime) { }
        internal GhostAdapter m_Ghost;

        /// <summary>
        /// Access to the <see cref="Ghost"/> for this GhostBehaviour. Only valid after the object is fully spawned and initialized.
        /// This is the "bridge" between GameObject and entities and the main point of access for Netcode features.
        /// </summary>
        // TODO-release with this being public, should it really be named "Adapter"? Using Ghost for now. GhostAdapter feels like it should be named "Ghost" or "GhostInstance" or something like that. EntityBehaviour has "EntityProxy"?
        // GhostInstance isn't great, since there's a component named like this :(.
        // this way, from a GhostBehaviour, I would call this.Ghost.GhostId for example. "Adapter" doesn't sound "unified", it sounds like it's a "helper" class
        // and not a first class citizen. For GO users, that'll be their main point of access to Netcode features.
        public GhostAdapter Ghost
        {
            get
            {
                if (m_Ghost == null)
                {
                    m_Ghost = GetComponent<GhostAdapter>();
                }

                return m_Ghost;
            }
        }

        public virtual void Awake()
        {
            // TODO-release with entities integration, this shouldn't be needed anymore, the lifecycle would be controlled by the engine
            Ghost.InternalAcquireEntityReference();
        }

        public virtual void OnDestroy()
        {
            Ghost.InternalReleaseEntityReference();
        }

        public bool IsServer => Ghost.IsServer;
        public bool IsClient => Ghost.IsClient;

        // /// <summary>
        // /// Whether this ghost is on the client which owns it. False on server and on other clients.
        // /// </summary>
        // /// <returns></returns>
        // // TODO-next@inputImprovements after domino: switch to "CanWriteInput" and "CanReadInput" instead
        // public bool IsOnClientOwner => Ghost.HasOwner && IsClient && Ghost.OwnerNetworkId.Value == Netcode.Client.Connection.NetworkId.Value;
        // // TODO-next@inputImprovements why not just GhostOwnerIsLocal?? for the above ^^^

        /// <summary>
        /// Check to make sure you can write to your ghost's data. Should be true when on the authority (server) or when predicting on any client. This can be on your local owner client or on other clients predicting you.
        /// </summary>
        // this would be more dynamic if/when we have distributed authority
        // Note: if you update this logic, make sure to also update GhostField's Setter logic as well
        public bool CanWriteState => Ghost.IsPredictedGhost;

        internal void InitializePrefabWithEntityComponents()
        {
            var prefabEntity = this.Ghost.EntityLink.Entity;
            var World = this.Ghost.EntityLink.World;

            var typesFixedList = new FixedList128Bytes<ComponentType>();

            // ComponentTypeSet only supports 15 components, so we need to slice this up if we have more than this
            void AddBatchToPrefab()
            {
                if (typesFixedList.Length == 0) return;

                var compSet = new ComponentTypeSet(typesFixedList);
                World.EntityManager.AddComponent(prefabEntity, compSet);
            }

            foreach (var generatedType in GetComponentTypes())
            {
                if (generatedType == default) continue;
                typesFixedList.Add(generatedType);
                if (typesFixedList.Length == 15)
                {
                    AddBatchToPrefab();
                    typesFixedList.Clear();
                }
            }

            // add the last batch of components
            AddBatchToPrefab();
        }

        #region source generated

        protected virtual IEnumerable<ComponentType> GetComponentTypes()
        {
            return Array.Empty<ComponentType>();
        }
        protected virtual void InitializeRuntimeGameObject(bool withInitialValue) { }

        #endregion // source generated

        internal void InitializeRuntime(bool withInitialValue) // needed since "protected internal" is weird with source gen and InternalVisibleTo
        {
            InitializeRuntimeGameObject(withInitialValue);
        }

        /// <summary>
        /// Retrieve the input set at the previous <see cref="NetworkTick"/>. Can be used inside the prediction
        /// loop, to calculate deltas or detect changes (i.e triggers up/down).
        /// </summary>
        /// <param name="input">The value of the input at that tick or default if the input data is not present.</param>
        /// <returns>True if a input for the previous tick is present. False, otherwise. </returns>
        public bool TryGetPreviousInputData<T>(out T input) where T : unmanaged, IInputComponentData
        {
            var networkTime = Ghost.NetworkTime;
            if (!networkTime.ServerTick.IsValid)
            {
                input = default;
                return false;
            }

            var currentTick = networkTime.ServerTick;
            currentTick.Subtract((uint)networkTime.SimulationStepBatchSize);
            return GetInputDataAtTick(currentTick, out input);
        }

        /// <summary>
        /// Retrieve the input set at the given <see cref="NetworkTick"/>.
        /// </summary>
        /// <param name="tick"></param>
        /// <param name="input">The value of the input at that tick or default if the input data is not present.</param>
        /// <returns>True if a input for the previous tick is present. False, otherwise. </returns>
        public bool TryGetInputDataAtTick<T>(in NetworkTick tick, out T input) where T : unmanaged, IInputComponentData
        {
            if (!tick.IsValid)
            {
                input = default;
                return false;
            }
            return GetInputDataAtTick(tick, out input);
        }

        /// <summary>
        /// Retrieve the <see cref="IInputComponentData"/> for the target tick <param name="targetTick"></param>
        /// from the input command buffer.
        /// </summary>
        /// <param name="targetTick"></param>
        /// <param name="input"></param>
        /// <returns>False, if the buffer is empty and no input can be found for the requested tick.
        /// Otherwise, it will return either the input that correspond to the requested tick or input data before the request tick.
        /// </returns>
        private bool GetInputDataAtTick<T>(in NetworkTick targetTick, out T input) where T : unmanaged, IInputComponentData
        {
            var inputBuffer = Ghost.World.EntityManager.GetBuffer<InputBufferData<T>>(Ghost.Entity, true);
            if (!inputBuffer.GetDataAtTick(targetTick, out var inputBufferData))
            {
                input = default;
                return false;
            }
            input = inputBufferData.InternalInput;
            return true;
        }
    }
}
#endif
