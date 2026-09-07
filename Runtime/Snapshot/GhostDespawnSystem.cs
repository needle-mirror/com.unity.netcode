using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Netcode.EntitiesInternalAccess;
using Unity.Netcode.NetcodeTime;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Netcode
{
    /// <summary>
    /// <para>
    /// Present only in client worlds. Responsible for destroying spawned ghosts when a despawn
    /// request/command is received from the server.
    /// </para>
    /// <para>Clients are not responsible for destroying ghost entities (and thus should never). The server is
    /// responsible for notifying the client about which ghosts should be destroyed (as part of the snapshot protocol).
    /// </para>
    /// <para>
    /// When a despawn command is received, the ghost entity is queued into a despawn queue. Two distinct despawn
    /// queues exist: one for interpolated, and one for the predicted ghosts.
    /// </para>
    /// <para>
    /// The above distinction is necessary because interpolated ghosts timeline (<see cref="NetworkTime.InterpolationTick"/>)
    /// is in the past in respect to both the server and client timeline (the current simulated tick).
    /// When a snapshot with a despawn command (for an interpolated ghost) is received, the server tick at which the entity has been destroyed
    /// (on the server) may be still in the future (for this client), and therefore the client must wait until the <see cref="NetworkTime.InterpolationTick"/>
    /// is greater or equal the despawning tick to actually despawn the ghost.
    /// </para>
    /// <para>
    /// Predicted entities, on the other hand, can be despawned only when the current <see cref="NetworkTime.ServerTick"/> is
    /// greater than or equal to the despawn tick of the server. Therefore, if the client is running ahead (as it should be),
    /// predicted ghosts will be destroyed as soon as their despawn request is pulled out of the snapshot
    /// (i.e. later on that same frame).
    /// </para>
    /// </summary>
    [BurstCompile]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [MovedFrom(true, "Unity.NetCode")]
    public partial struct GhostDespawnSystem : ISystem
    {
        NativeQueue<DelayedDespawnGhost> m_InterpolatedDespawnQueue;
        NativeQueue<DelayedDespawnGhost> m_PredictedDespawnQueue;
        ComponentLookup<GhostGameObjectLink> m_GameObjectLookup;
        ComponentLookup<GhostInstance> m_GhostInstanceLookup;
        BufferLookup<GameObjectDespawnTracking> m_DelayedDespawnLookup;
        Entity m_DelayedGODespawnEntity;

        /// <summary>A pending despawn produced by <see cref="GhostReceiveSystem"/> and consumed by <see cref="DespawnJob"/>.</summary>
        internal struct DelayedDespawnGhost
        {
            /// <summary>Identity of the ghost on the server, used to resolve the client entity through <see cref="SpawnedGhostEntityMap"/>.</summary>
            public SpawnedGhost ghost;
            /// <summary>Server tick on which the despawn was issued; the despawn is held until the client's tick reaches it.</summary>
            public NetworkTick tick;
            /// <summary>Client entity captured at receive time. Used as a fallback when the spawn-map lookup misses (despawn arrived before placeholder promotion).</summary>
            public Entity entity;
        }

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }

            var singleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostDespawnQueues>());
            state.EntityManager.SetName(singleton, "GhostLifetimeComponent-Singleton");
            EntitiesStaticInternalAccessBursted.SetHideInHierarchy(state.EntityManager, singleton);
            m_InterpolatedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            m_PredictedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);

            m_DelayedGODespawnEntity = state.EntityManager.CreateEntity(typeof(GameObjectDespawnTracking));
            m_DelayedDespawnLookup = state.GetBufferLookup<GameObjectDespawnTracking>();

            SystemAPI.SetSingleton(new GhostDespawnQueues
            {
                InterpolatedDespawnQueue = m_InterpolatedDespawnQueue,
                PredictedDespawnQueue = m_PredictedDespawnQueue,
            });
            m_GameObjectLookup = state.GetComponentLookup<GhostGameObjectLink>();
            m_GhostInstanceLookup = state.GetComponentLookup<GhostInstance>(true);
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            m_InterpolatedDespawnQueue.Dispose();
            m_PredictedDespawnQueue.Dispose();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<NetworkStreamInGame>())
            {
                state.CompleteDependency();
                m_PredictedDespawnQueue.Clear();
                m_InterpolatedDespawnQueue.Clear();
                return;
            }

            if (state.WorldUnmanaged.IsThinClient())
                return;

            // TODO-release handle hybrid scenario where entity is destroyed first server side. GO needs to react to this and self destruct (or have a system to handle it for us)
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            m_DelayedDespawnLookup.Update(ref state);
            var allGameObjectDespawns = m_DelayedDespawnLookup[m_DelayedGODespawnEntity];
            allGameObjectDespawns.Clear();
            var spawnedGhostMap = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO.SpawnedGhostMapRW;
            m_GameObjectLookup.Update(ref state);
            m_GhostInstanceLookup.Update(ref state);
            state.Dependency = new DespawnJob
            {
                spawnedGhostMap = spawnedGhostMap,
                interpolatedDespawnQueue = m_InterpolatedDespawnQueue,
                predictedDespawnQueue = m_PredictedDespawnQueue,
                interpolatedTick = networkTime.InterpolationTick,
                predictedTick = networkTime.ServerTick,
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged),
                isGOLookup = m_GameObjectLookup,
                ghostInstanceLookup = m_GhostInstanceLookup,
                // Delay the GameObject destruction to a subsequent managed system since we can't burst GameObject Destroy right now TODO-next@domino after domino: merge that system back here
                despawnTrackingLookup = m_DelayedDespawnLookup,
                despawnSingleton = m_DelayedGODespawnEntity,
            }.Schedule(state.Dependency);
        }

        [BurstCompile]
        struct DespawnJob : IJob
        {
            public NativeQueue<DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeParallelHashMap<SpawnedGhost, Entity> spawnedGhostMap;
            public NativeQueue<DelayedDespawnGhost> predictedDespawnQueue;
            public NetworkTick interpolatedTick, predictedTick;
            public EntityCommandBuffer commandBuffer;
            public ComponentLookup<GhostGameObjectLink> isGOLookup;
            [ReadOnly] public ComponentLookup<GhostInstance> ghostInstanceLookup;
            [NativeDisableParallelForRestriction] public BufferLookup<GameObjectDespawnTracking> despawnTrackingLookup;
            public Entity despawnSingleton;

            /// <summary>Destroys a single despawned ghost. When the spawn-map lookup misses, destroys the captured placeholder entity instead (despawn arrived before placeholder promotion).</summary>
            void ProcessDespawn(ref DelayedDespawnGhost spawnedGhost, DynamicBuffer<GameObjectDespawnTracking> despawnTracking)
            {
                if (!spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                {
                    if (spawnedGhost.entity != Entity.Null && ghostInstanceLookup.HasComponent(spawnedGhost.entity))
                        commandBuffer.DestroyEntity(spawnedGhost.entity);
                    return;
                }
                spawnedGhost.entity = ent;
                if (isGOLookup.HasComponent(ent))
                {
                    despawnTracking.Add(new() { oneDespawn = spawnedGhost });
                }
                else
                {
                    commandBuffer.DestroyEntity(ent);
                    spawnedGhostMap.Remove(spawnedGhost.ghost);
                }
            }

            [BurstCompile]
            public void Execute()
            {
                var despawnTracking = despawnTrackingLookup[despawnSingleton];
                while (interpolatedDespawnQueue.Count > 0 &&
                       !interpolatedDespawnQueue.Peek().tick.IsNewerThan(interpolatedTick))
                {
                    var spawnedGhost = interpolatedDespawnQueue.Dequeue();
                    ProcessDespawn(ref spawnedGhost, despawnTracking);
                }

                while (predictedDespawnQueue.Count > 0 &&
                       !predictedDespawnQueue.Peek().tick.IsNewerThan(predictedTick))
                {
                    var spawnedGhost = predictedDespawnQueue.Dequeue();
                    ProcessDespawn(ref spawnedGhost, despawnTracking);
                }
            }
        }
    }

    internal struct GameObjectDespawnTracking : IBufferElementData
    {
        public GhostDespawnSystem.DelayedDespawnGhost oneDespawn;
    }

    // TODO-next@trunk once we're in trunk, check slack thread see if they were able to get to it: GO despawn doesn't have APIs for burst compatible GO destruction. Raised this on slack. Disabling burst for now, since this is really just a system that schedules a job that's itself bursted anyway. But should come back to this if/when that's available. Slack thread https://unity.slack.com/archives/C0575F6KEAY/p1757546583041179
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateAfter(typeof(GhostDespawnSystem))]
    [UpdateAfter(typeof(PredictedGhostDespawnSystem))]
    [UpdateAfter(typeof(GhostReceiveSystem))] // for the despawns on disconnect
    [UpdateBefore(typeof(GhostSpawnClassificationSystemGroup))] // runs before classification jobs reading the ghost map are scheduled. Was previously implicit through creation order.
    internal partial class GhostGameObjectDespawnManagedSystem : SystemBase
    {
        EntityQuery m_DelayedDespawnQuery;
        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                this.Enabled = false;
                return;
            }
            RequireForUpdate<GhostGameObjectLink>();
            m_DelayedDespawnQuery = this.GetEntityQuery(typeof(GameObjectDespawnTracking));
            RequireForUpdate<GameObjectDespawnTracking>();
        }

        protected override void OnUpdate()
        {
            // GetSingletonRW doesn't complete in-flight jobs registered against the singleton type (e.g. classification jobs reading the ghost map), so complete them before the main thread Remove below.
            EntityManager.CompleteDependencyBeforeRW<SpawnedGhostEntityMap>();
            var spawnedGhostMap = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO.SpawnedGhostMapRW;
            var trackingEntity = m_DelayedDespawnQuery.GetSingletonEntity();
            var despawnTrackingBufferRO = EntityManager.GetBuffer<GameObjectDespawnTracking>(trackingEntity, isReadOnly: true);
            var goDespawnTracking = despawnTrackingBufferRO.ToNativeArray(Allocator.Temp); // Using ToNativeArray since there seems to be a bug with DynamicBuffer's safety handles right now. Slack thread https://unity.slack.com/archives/C0575F6KEAY/p1772128053576089

            for (int i = 0; i < goDespawnTracking.Length; i++)
            {
                var spawnedGhost = goDespawnTracking[i];
                spawnedGhostMap.Remove(spawnedGhost.oneDespawn.ghost);

                var goIdToDespawn = EntityManager.GetComponentData<GhostGameObjectLink>(spawnedGhost.oneDespawn.entity).AssociatedGameObject;

                GameObject.DestroyImmediate(Resources.EntityIdToObject(goIdToDespawn));

                // This should be the last release, as all the other OnDestroy should have been called by the DestroyImmediate above.
                // This in turn removes the GhostGameObjectLink cleanup component
                GhostEntityMapping.ReleaseGameObjectEntityReference(goIdToDespawn, worldIsCreated: true);
            }

            // Same reason as above from slack thread, there's a bug right now where we need to get the buffer again in order to clear it.
            var despawnTrackingBufferRW = EntityManager.GetBuffer<GameObjectDespawnTracking>(trackingEntity, isReadOnly: false);
            despawnTrackingBufferRW.Clear();
        }
    }

    /// <summary>
    /// System in charge of destroying the GameObject side of ghosts. This is centralized and will read from a pending list of despawns coming from multiple
    /// entity side destroy points.
    /// You can schedule any systems assuming GameObjects are destroyed after this system.
    /// </summary>
    [CreateAfter(typeof(RegisterGhostTransformTrackingSystem))] // so that the OnDestroy here happens before we dispose the various tracking collections
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.BakingSystem)] // This should run everywhere there's a ghost object, including baking worlds
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
        partial class DespawnGameObjectGhostsOnWorldDestruction : SystemBase
    {
        EntityQuery m_GOGhostsQuery;
        EntityQuery m_GOGhostsQueryWithPrefabs;
        protected override void OnCreate()
        {
            m_GOGhostsQuery = this.GetEntityQuery(typeof(GhostGameObjectLink));
            m_GOGhostsQueryWithPrefabs = this.GetEntityQuery(typeof(GhostGameObjectLink), typeof(Prefab));
            Enabled = false;
        }

        protected override void OnUpdate()
        {
        }

        protected override void OnDestroy()
        {
            var ghosts = m_GOGhostsQuery.ToComponentDataArray<GhostGameObjectLink>(Allocator.Temp);
            foreach (var ghostGameObjectLink in ghosts)
            {
                var gameObject = ghostGameObjectLink.AssociatedGameObject;
                GameObject.DestroyImmediate(Resources.EntityIdToObject(gameObject)); // immediate since user side OnDestroy could call entity related APIs, which wouldn't be accessible anymore after world destruction.
                GhostEntityMapping.ForceReleaseOnWorldDestroy(gameObject);
            }

            var ghostPrefabs = m_GOGhostsQueryWithPrefabs.ToComponentDataArray<GhostGameObjectLink>(Allocator.Temp);
            foreach (var ghostPrefabLink in ghostPrefabs)
            {
                var gameObject = ghostPrefabLink.AssociatedGameObject;
                GhostEntityMapping.ForceReleaseOnWorldDestroy(gameObject);
            }
        }
    }
}
