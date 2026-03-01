#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using System.Diagnostics;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using UnityEngine;
using Unity.NetCode.EntitiesInternalAccess;
using Assert = UnityEngine.Assertions.Assert;

namespace Unity.NetCode
{
    /// <summary>
    /// To support serializing a generic type like ghost vars, in the inspector, we need a few non-generic utilities
    /// </summary>
    interface IGhostFieldInspectorDrawerUtilities
    {
        bool Initialized();
        void CopyEntityValueToInspectorValue();
        void CopyInspectorValueToEntityValue();
        bool ShouldApplyInspectorValue(IGhostFieldInspectorDrawerUtilities previous);
    }

    // TODO-release@potentialUX how do we expose this in the inspector to allow live editing server side?
    // TODO-release@potentialOptim make sure to merge the generated components together, for netcode perf reasons (better one big comp, than multiple small ones for serialization). We'd need to be careful around the FieldOffset(0) we have and have a way to read offsetted fields. We'd also need to see if the profiler can still be as useful vs having a per field component and see each field's bandwidth consumption
    /// <summary>
    /// The main way to sync state. This data will be replicated to other ghosts in an eventually consistent way. This means a client doesn't have a guarantee to receive
    /// all the different state changes, only that it'll eventually have the latest state.
    /// Your data will be rolled back automatically when running prediction.
    /// You can specify various serialization options using the <see cref="FieldConfig"/> parameter in the <see cref="GhostField{InternalTypeT}"/> constructor.
    /// Please refer to RPCs for volatile networked events. TODO-next@RPCs link to RPC doc
    /// </summary>
    /// <typeparam name="InternalTypeT"></typeparam>
    // TODO-release@potentialOptim test with a big project, make sure sourcegen doesn't take too much time. So test with non-GhostBehaviours classes, with hundreds of GhostBehaviours classes
    [Serializable]
    [DebuggerDisplay("{GetDebugName()}")] // to avoid having Rider complete job dependencies with the Value property
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    unsafe struct GhostField<InternalTypeT> : IGhostFieldInspectorDrawerUtilities
        where InternalTypeT : unmanaged
    {
        // design notes
        // Doing a GetComponentData everytime you access a GhostField state can have overhead. A solution is to cache a pointer to the chunk data.
        // The two risks around this are structural changes and race conditions. Those two can be mitigated with two light checks.
        // - CompleteForRead will do a simple version check if there's no jobs accessing this component type. Worst case, the first GhostField access does a job.Complete, then all subsequent ones have simple equality check
        // - Version check for structural changes. The assumption is that users using the generated components won't trigger much structural changes. This means a simple int check most of the time.
        // If the structural check fails, then we need to re-cache the pointer, which reintroduces the overhead we had originally anyway.
        //
        // IMPORTANT
        // For writes, an additional change version bump needs to be done, to allow other queries doing change filtering to work properly. Else if you modify the pointed value directly, it bypasses that mechanic. There's no API in entities right allowing to only bump the version, so we need to fallback on SetComponentData to do this for us.
        //
        // Entities had plans for a ComponentRef, we can potentially use this instead when it's available.

        internal Entity m_Entity;
        internal WorldUnmanaged m_World;
        internal ComponentType m_GeneratedWrapperComponentType;

        // caching
        void* m_CachedPtr;
        EntityStorageInfo m_CachedChunkInfo;
        int m_LastComponentTypeVersion;

        /// <summary>
        /// initial value on prefabs and also used to display the value in the inspector at runtime. (see the refresh method) // allows for no-GC alloc custom inspector, just reusing Unity's property drawing capabilities
        /// </summary>
        [SerializeField] InternalTypeT m_Value;

        // TODO-release have a way to pre-register components on prefab baking (since prefab's serialization is fixed) without having those components defined in GhostBehaviours on the prefab? This way you can add those GhostBehaviour on the ghost at runtime and the underlying entity would already have those components? something like "here's the possible set of GhostBehaviours for this prefab, pre-register the underlying components so they work out of the box when adding those GhostBehaviours later".
        /// <summary>
        /// <see cref="GhostFieldAttribute"/>
        /// </summary>
        /// <param name="initialValue"></param>
        /// <param name="fieldConfig"></param>
        // fieldConfig is a single param that inherits from GhostFieldAttribute allows for better maintainability, no need to remember to add more to the list of params here
        public GhostField(InternalTypeT initialValue = default, FieldConfig fieldConfig = default)
        {
            // fieldConfig is used by sourcegen and don't need to be stored here
            m_Entity = Entity.Null;
            m_World = default;
            m_GeneratedWrapperComponentType = default;
            m_CachedPtr = null;
            m_CachedChunkInfo = default;
            m_LastComponentTypeVersion = 0;
            m_Value = initialValue;
        }

        // TODO-release can code stripping remove this? Sourcegen files count as references to this no?
        // TODO-next@domino can make this internal, have a static public method in a netcode.internal namespace? for better user signaling? there's potentially som pvp exception we could get with that namespace?
        /// <summary>
        /// Initializes this GhostField. This is called by source generators and shouldn't have to be called manually
        /// </summary>
        /// <param name="world"></param>
        /// <param name="entity"></param>
        /// <param name="t"></param>
        /// <param name="withInitialValue">determines if Initialization should use the serialized InitialValue</param>
        public void Initialize(World world, Entity entity, ComponentType t, bool withInitialValue)
        {
            m_World = world.Unmanaged;
            m_Entity = entity;
            m_GeneratedWrapperComponentType = t;
            m_CachedPtr = EntitiesStaticInternalAccessBursted.GetComponentDataRawRO(ref m_World, m_Entity, m_GeneratedWrapperComponentType.TypeIndex);
            if (withInitialValue)
            {
                // m_Value is the value serialized by unity in the prefab. So we apply this value when appropriate
                Value = m_Value;
            }
            RefreshChunkStorage();
            m_LastComponentTypeVersion = m_World.EntityManager.GetComponentOrderVersion(m_GeneratedWrapperComponentType);
        }

        const string k_NoEntityError = "Data required to access " + nameof(GhostField<InternalTypeT>) + " doesn't exist or doesn't have the required underlying component. Make sure to not use GhostFields if the associated ghost is destroyed.";

        void RefreshChunkStorage()
        {
            m_CachedChunkInfo = m_World.EntityManager.GetStorageInfo(m_Entity);
        }
        bool ShouldRefreshCachedChunkInfo(EntityManager em)
        {
            // refreshing the cached pointer makes GhostField reads about 5x-6x slower, so making sure we only do it as needed
            var currentComponentTypeVersion = em.GetComponentOrderVersion(m_GeneratedWrapperComponentType);

            // This can happen when a component has been added/removed to any other entity or when another entity with this component has been destroyed (leading to all entities with that component to bump their version
            if (currentComponentTypeVersion != m_LastComponentTypeVersion)
            {
                // ComponentType version changes for ALL entities containing this type, even if a single entity has changed. So before refreshing, we first check whether this is a false positive by also checking the chunk version
                // Since this can happen even in normal GO flows, where a ghost is destroyed (triggering the component version change) we make sure this is not a false positive
                // This allows a 2-3x speed improvement on ALL reads when there's a single ghost getting destroyed
                m_LastComponentTypeVersion = currentComponentTypeVersion;

                if (m_CachedChunkInfo.Chunk.Invalid()) // can happen if all ghosts have been moved out
                {
                    RefreshChunkStorage();
                    if (m_CachedChunkInfo.Chunk.Invalid())
                        throw new ArgumentException(k_NoEntityError);
                    return true;
                }

                // Chunk version check optim isn't doable. chunk order version only ever gets updated once in the same update of the same system (to the global system version) and so if there's more than one structural change in the same frame, it'll only detect the first one. Slack thread  here https://unity.slack.com/archives/C0575F6KEAY/p1771013404785409
                // So instead we check the entity directly

                if (
                        m_CachedChunkInfo.IndexInChunk >= m_CachedChunkInfo.Chunk.Count ||
                        m_CachedChunkInfo.Chunk.GetEntityDataPtrRO(em.GetEntityTypeHandle())[m_CachedChunkInfo.IndexInChunk] != m_Entity // Had a chat with Fabrice, there's potentially ways to make GetEntityDataPtrRO more efficient, knowing this is accessed from the main thread. We'll have to see how the type dependency and safety system evolves to maybe make this faster.
                        // m_CachedEntitiesInChunk[m_CachedChunkInfo.IndexInChunk] != m_Entity // Can't cache the ptr to the entities array. Entities potentially has an issue with their Invalid() check above that could be wrong (it's based on a chunk index that could be reused by another chunk potentially), making that pointer caching flaky. It's an issue with semantics, "Invalid" tells if the chunk is unused. But in the case where a chunk has been repurposed, it is technically "valid" even though it's not what it used to be anymore. Fabrice will look into it.
                        )
                {
                    // This could be some movement in the chunk, but not for this entity, so checking for false positives here as well.
                    // if another entity gets destroyed in the same chunk, with the current entities package version, this will trigger a structural
                    // change. This won't be the case with entities' sparse chunks changes coming later. But we'll still get false positives if there's a structural change in the chunk related to another unrelated entity

                    // since the above might be a different chunk now, we need to refresh with the version of the chunk we're currently in;
                    RefreshChunkStorage();

                    return true;
                }
            }

            return false;
        }


        /// <summary>
        /// Access your stored networked value.
        /// Optional: To access ECS Component data from ECS systems, please use <see cref="GhostComponentRef{TComponentType}"/>
        /// </summary>
        // TODO-release@potentialUX add a new version of this for prediction with cached values only? And refresh ptrs in calling ECS system? Could also be UnsafeValueRef. See discussion here https://github.cds.internal.unity3d.com/unity/dots/pull/14796#discussion_r889872
        [DebuggerBrowsable(DebuggerBrowsableState.Never)] // we want to avoid completing jobs while debugging
        public InternalTypeT Value
        {
            get
            {
                // requirements when caching pointers to chunk data
                // check for structural change
                // check for jobs race condition
                // for writes, bump the version number for each write. (needed for systems that queries only entities that have changed) (can be done by doing any GetRW even if the change has already been done, just to trigger the version change)

                // TODO-release@potentialOptim burst this logic

                // TODO-release@potentialOptim : We should measure real use cases with those and see if the per-field cached pointer refresh is too much (vs doing a single one per ghost). could cache instead the GhostObject or GhostBehaviour and use it as our holder of the lookup to the chunk and index. This way we only refresh once instead of 20x if there's 20 fields on the same ghost.
                // TODO-release@potentialOptim: look into having sync points before the prediction update and just have users write in cached values instead of the component data directly. Since we control the PredictionUpdate loop, we know no other ECS systems could write to those components.

                var em = m_World.EntityManager;
                em.CompleteDependencyBeforeROExtension(m_GeneratedWrapperComponentType.TypeIndex);

                Assert.IsTrue(em.HasComponent(m_Entity, m_GeneratedWrapperComponentType), k_NoEntityError); // doing an assert that'll be stripped in releases. Unity as a whole doesn't guarantee safety in release builds. Physics.Raycast and Time.deltaTime for example have their "main thread only exceptions" stripped in release builds

                if (ShouldRefreshCachedChunkInfo(em))
                {
                    m_CachedPtr = EntitiesStaticInternalAccessBursted.GetComponentDataRawRO(ref m_World, m_Entity, m_GeneratedWrapperComponentType.TypeIndex);
                }

                Assert.IsTrue(m_CachedPtr == EntitiesStaticInternalAccessBursted.GetComponentDataRawRO(ref m_World, m_Entity, m_GeneratedWrapperComponentType.TypeIndex), "sanity check failed, cached pointer is not up to date, please raise a bug"); // TODO-release@potentialOptim remove this once we're sure this hasn't been triggered for a while, this way editor side perf will be better.

                return *(InternalTypeT*)m_CachedPtr; // cast works because we're assuming the generated component's value field is at 0 offset. We're enforcing this in sourcegen with a field offset so hopefully this shouldn't be too evil.
            }
            set
            {
                // since we also want to do change version bumps, which is stored in the chunk, there's no advantage to bypass the chunk access like with the Get,
                // so just using higher level APIs from entities.

                var em = m_World.EntityManager;
                Assert.IsTrue(em.HasComponent(m_Entity, m_GeneratedWrapperComponentType), k_NoEntityError);
                Assert.IsTrue(em.HasComponent<PredictedGhost>(m_Entity), $"Trying to write to a ghost field when not allowed. Make sure to check {nameof(GhostBehaviour.CanWriteState)} before writing to it or make sure to only write from {nameof(GhostBehaviour)}.{nameof(GhostBehaviour.PredictionUpdate)}. Your ghost must either be predicted client side or you must write to it server side only.");

                var currentPtr = EntitiesStaticInternalAccessBursted.GetComponentDataRawRW(ref m_World, m_Entity, m_GeneratedWrapperComponentType.TypeIndex);
                if (currentPtr != m_CachedPtr)
                {
                    m_CachedPtr = currentPtr;
                    RefreshChunkStorage();
                    m_LastComponentTypeVersion = em.GetComponentOrderVersion(m_GeneratedWrapperComponentType);
                }
                *(InternalTypeT*)m_CachedPtr = value;
            }
        }

        // /// <summary>
        // /// TODO-release@potentialUX wait for entities integration and see if they provide anything for this. should we expose this to users? This seems very dangerous. Any APIs called could trigger a structural change and cause weird memory access shenanigans. To consider: if we want some form of dirty check, this would have potentially a few false positives. We could return some form of RefRW or ComponentLookup instead of a ref directly? Ref is better than pointer, since you can't store a ref.
        // /// Important: this should only be used locally and shouldn't be stored. This only does a single version bump on write
        // /// Direct access to the internal buffer containing the stored data. Calling this has a small amount of overhead, as each time it'll make a call to the world's EntityManager
        // /// For high performance cases, it's recommended to manipulate this data as a ref var and do a single query for that data, rather than manipulating this data directly
        // /// The returned ref is only valid for the current context. It's dangerous to use this ref long term as it might get invalidated.
        // /// e.g.
        // /// <code>
        // /// // Less performant
        // /// myGhostField.AsUnsafeRef = 1;
        // /// myGhostField.AsUnsafeRef = 2;
        // /// myGhostField.AsUnsafeRef = 3;
        // /// myGhostField.AsUnsafeRef = 4;
        // /// // Better
        // /// ref var val = ref myGhostField.AsUnsafeRef;
        // /// val = 1;
        // /// val = 2;
        // /// val = 3;
        // /// val = 4;
        // /// </code>
        // /// Do note this also completes all write job dependencies on the internal component. If you only plan to read this value, you should use <see cref="Value"/>.
        // /// Calling methods that could make a structural change or invalidate this value after getting this ref can lead to hard to debug issues. It'd be recommended to refetch the ref if you're unsure of its integrity.
        // /// <code>
        // /// // example
        // /// ref var val = ref myField.AsUnsafeRef;
        // /// DoSomethingThatChangesThePositionInMemory();
        // /// val = 123; // bad. this can silently fail or crash your game.
        // /// val = ref myField.AsUnsafeRef;
        // /// val = 123; // good. now its safe
        // /// </code>
        // /// </summary>
        // internal ref InternalTypeT AsUnsafeRef => ref UnsafeUtility.AsRef<InternalTypeT>(GetValuePtr(readOnly: false));

        /// <summary>
        /// For inspector purposes
        /// </summary>
        /// <returns></returns>
        bool IGhostFieldInspectorDrawerUtilities.Initialized()
        {
            try
            {
                return this.m_Entity != Entity.Null && m_World.IsCreated && m_World.EntityManager.World.IsCreated
                    && m_World.EntityManager.HasComponent(m_Entity, m_GeneratedWrapperComponentType); // while destroying a ghost, because of ICleanupComponent there's a short period where a ghost doesn't contain the component but still exists.
            }
            catch (ObjectDisposedException) // This isn't super performant, but since this is not on the hot path it should be fine
            {
                return false;
            }
        }

        /// <summary>
        /// For inspector purposes
        /// </summary>
        /// <returns></returns>
        void IGhostFieldInspectorDrawerUtilities.CopyEntityValueToInspectorValue()
        {
            if (((IGhostFieldInspectorDrawerUtilities)this).Initialized())
                m_Value = Value;
        }

        void IGhostFieldInspectorDrawerUtilities.CopyInspectorValueToEntityValue()
        {
            if (((IGhostFieldInspectorDrawerUtilities)this).Initialized())
                Value = m_Value;
        }

        bool Equals(InternalTypeT left, InternalTypeT right)
        {
            return UnsafeUtility.MemCmp(UnsafeUtility.AddressOf(ref left), UnsafeUtility.AddressOf(ref right), sizeof(InternalTypeT)) == 0;
        }

        bool IGhostFieldInspectorDrawerUtilities.ShouldApplyInspectorValue(IGhostFieldInspectorDrawerUtilities previous)
        {
            var previousGhostField = (GhostField<InternalTypeT>)previous;
            return !Equals(m_Value, previousGhostField.m_Value)
                && Equals(Value, previousGhostField.m_Value) // should only apply inspector value if the underlying pointed at value hasn't changed somewhere else in user code
                && m_World.EntityManager.WorldUnmanaged.IsServer(); // should only be dirty server side, it's invalid to be dirty client side
        }

        // For debug purposes
        internal string GetDebugName()
        {
            var em = m_World.EntityManager;

            var jobDep = EntitiesStaticInternalAccessBursted.GetDependencyForType(em, this.m_GeneratedWrapperComponentType.TypeIndex, true);

            // Rider doesn't like completing jobs while showing a debug value. This shouldn't be valid anyway, reading debug values shouldn't have side effects like this. So we're preventing those side effects with this custom debug view.
            if (!jobDep.Equals(default) && !jobDep.IsCompleted)
                return "Debugger Error: There's a job still not completed writing to this value. Since reading this value from the debugger would automatically complete this job, changing execution behaviour, this was prevented. To inspect the value, stepping in your code until a Get is done organically on this value should be enough to complete the depending job.";

            return $"{this.Value}";
        }
    }


    /// <summary>
    /// This is an optional way to access ECS component data associated with the underlying entity.
    /// For GameObject only development, it's simpler to just use <see cref="GhostField{InternalTypeT}"/>
    /// Can be used to share a component type across different <see cref="GhostBehaviour"/> as they'll be stored on the same shared entity.
    /// Can be used to access replicated state from both ECS and GameObject land, since it uses a user defined IComponentData that's not source generated.
    /// </summary>
    /// <typeparam name="TComponentType"></typeparam>
    // Design note: See InternalDocumentation on why this needs to be a separate container from GhostField
    // TODO-release Add Code Example
    // TODO-release close to 6.7 release, check where the entities is at with their per-component access and check if we should just remove this. If we don't, it could be worth adding a bit of custom inspector code to make sure the value of the component is visible in the inspector.
    // TODO-release we can also potentially just go with Option 3 from this doc https://docs.google.com/document/d/1bphz5rgCFqHGBgP44bhmlYoZbT8EjcMr_C8-ynqDJM0/edit?tab=t.0#heading=h.a115vmpc4mgz to make it really clear those are different ways to interact with the component. We'll need to see where the entities team is at with that.
    // TODO-polish add support for SharedComponents? and enableables?
    [Serializable]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct GhostComponentRef<TComponentType> : IGhostFieldInspectorDrawerUtilities
        where TComponentType : unmanaged, IComponentData
    {
        Entity m_Entity;
        WorldUnmanaged m_World;
        [SerializeField] TComponentType m_Value;

        /// <summary>
        /// Use this constructor to provide an in-code initial value.
        /// <code>
        /// GhostComponentRef&lt;MyComponent&gt; myComp = new (new MyComponent(){ val = 123 });
        /// </code>
        /// </summary>
        /// <param name="initialValue"></param>
        public GhostComponentRef(TComponentType initialValue = default)
        {
            // quantization and other serialization settings are used by sourcegen and don't need to be stored here
            // netcode dev note: the parameter names must match GhostField property names, as sourcegen will directly copy those
            m_Entity = Entity.Null;
            m_World = default;

            m_Value = initialValue;
        }

        /// <summary>
        /// Internal. Called by sourcegen. Please do not use.
        /// </summary>
        /// <param name="world"></param>
        /// <param name="entity"></param>
        /// <param name="withInitialValue">determines if Initialization should use the serialized InitialValue</param>
        public void Initialize(World world, Entity entity, bool withInitialValue)
        {
            m_World = world.Unmanaged;
            m_Entity = entity;

            var def = default(TComponentType);
            if (!TypeManager.Equals(ref m_Value, ref def) && withInitialValue)
                // only call heavy Set operation if required
                ValueAsRef = m_Value;
        }

        internal unsafe ref TComponentType ValueAsRef
        {
            get
            {
                // TODO-release@potentialOptim just use RefRW?
                // RefRW<TComponentType> r = RefRW<TComponentType>.Optional(m_World.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<TComponentType>()).ToComponentDataArray<TComponentType>(Allocator.Temp), new EntityStorageInfoLookup()[m_Entity].IndexInChunk); // RefRW can't be stored long term, doesn't check for structural changes, only job safety checks.

                var ptr = EntitiesStaticInternalAccessBursted.GetComponentDataRawRW(ref m_World, m_Entity, TypeManager.GetTypeIndex<TComponentType>());
                return ref UnsafeUtility.AsRef<TComponentType>(ptr);
            }
        }

        /// <summary>
        /// Returns a copy of the <see cref="IComponentData"/> this <see cref="GhostComponentRef{TComponentType}"/> points to.
        /// Don't forget to set it back if writing to it.
        /// </summary>
        // TODO-release@potentialOptim: switch to the same pointer caching pattern as GhostField? wait to see what the entities team is providing.
        // TODO-release@potentialOptim: Could also provide AsUnsafeRefRO and AsUnsafeRefRW ? And could return some form of ComponentLookup or RefRW?
        // TODO-release@potentialOptim: burst those calls
        public TComponentType Value
        {
            get => m_World.EntityManager.GetComponentData<TComponentType>(m_Entity);
            set => m_World.EntityManager.SetComponentData(m_Entity, value);
        }

        void IGhostFieldInspectorDrawerUtilities.CopyEntityValueToInspectorValue()
        {
            if (((IGhostFieldInspectorDrawerUtilities)this).Initialized())
                m_Value = Value;
        }

        public void CopyInspectorValueToEntityValue()
        {
            throw new NotImplementedException();
        }

        bool IGhostFieldInspectorDrawerUtilities.Initialized()
        {
            return this.m_Entity != Entity.Null;
        }

        bool IGhostFieldInspectorDrawerUtilities.ShouldApplyInspectorValue(IGhostFieldInspectorDrawerUtilities previous)
        {
            throw new NotImplementedException();
        }
    }
}
#endif
