using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode
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
    public partial struct GhostDespawnSystem : ISystem
    {
        NativeQueue<DelayedDespawnGhost> m_InterpolatedDespawnQueue;
        NativeQueue<DelayedDespawnGhost> m_PredictedDespawnQueue;
        NativeList<DelayedDespawnGhost> m_AllGameObjectDespawns;
        ComponentLookup<GhostGameObjectLink> m_GameObjectLookup;
        EntityQuery m_GameObjectQuery;

        internal struct DelayedDespawnGhost
        {
            public SpawnedGhost ghost;
            public NetworkTick tick;
        }

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            if (state.WorldUnmanaged.IsHost())
            {
                state.Enabled = false;
                return;
            }
            m_GameObjectQuery = state.GetEntityQuery(ComponentType.ReadOnly<GhostGameObjectLink>());

            var singleton = state.EntityManager.CreateEntity(ComponentType.ReadWrite<GhostDespawnQueues>());
            state.EntityManager.SetName(singleton, "GhostLifetimeComponent-Singleton");
            m_InterpolatedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            m_PredictedDespawnQueue = new NativeQueue<DelayedDespawnGhost>(Allocator.Persistent);
            m_AllGameObjectDespawns = new(allocator: Allocator.Persistent);
            SystemAPI.SetSingleton(new GhostDespawnQueues
            {
                InterpolatedDespawnQueue = m_InterpolatedDespawnQueue,
                PredictedDespawnQueue = m_PredictedDespawnQueue,
            });
            m_GameObjectLookup = state.GetComponentLookup<GhostGameObjectLink>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            state.CompleteDependency();
            m_InterpolatedDespawnQueue.Dispose();
            m_PredictedDespawnQueue.Dispose();
            m_AllGameObjectDespawns.Dispose();
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

            m_AllGameObjectDespawns.Resize(math.min(m_InterpolatedDespawnQueue.Count + m_PredictedDespawnQueue.Count, m_GameObjectQuery.CalculateEntityCount()), NativeArrayOptions.UninitializedMemory);
            m_AllGameObjectDespawns.Clear();

            var spawnedGhostMap = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO.SpawnedGhostMapRW;
            m_GameObjectLookup.Update(ref state);
            state.Dependency = new DespawnJob
            {
                spawnedGhostMap = spawnedGhostMap,
                interpolatedDespawnQueue = m_InterpolatedDespawnQueue,
                predictedDespawnQueue = m_PredictedDespawnQueue,
                interpolatedTick = networkTime.InterpolationTick,
                predictedTick = networkTime.ServerTick,
                commandBuffer = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged),
                isGo = m_GameObjectLookup,
                GODelayedDespawns = m_AllGameObjectDespawns.AsParallelWriter(),
            }.Schedule(state.Dependency);

            if (!m_GameObjectQuery.IsEmpty)
            {
                // Delay the GameObject destruction to a subsequent managed system since we can't burst GameObject Destroy right now TODO-next merge that system back here
                state.Dependency.Complete();
                ref var GODespawnTracking = ref SystemAPI.GetSingletonRW<GameObjectDespawnTracking>().ValueRW;
                GODespawnTracking.allGODespawns = m_AllGameObjectDespawns;
            }
        }

        [BurstCompile]
        struct DespawnJob : IJob
        {
            public NativeQueue<DelayedDespawnGhost> interpolatedDespawnQueue;
            public NativeParallelHashMap<SpawnedGhost, Entity> spawnedGhostMap;
            public NativeQueue<DelayedDespawnGhost> predictedDespawnQueue;
            public NativeList<DelayedDespawnGhost>.ParallelWriter GODelayedDespawns;
            public NetworkTick interpolatedTick, predictedTick;
            public EntityCommandBuffer commandBuffer;
            public ComponentLookup<GhostGameObjectLink> isGo;

            [BurstCompile]
            public void Execute()
            {
                while (interpolatedDespawnQueue.Count > 0 &&
                       !interpolatedDespawnQueue.Peek().tick.IsNewerThan(interpolatedTick))
                {
                    var spawnedGhost = interpolatedDespawnQueue.Dequeue();
                    if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                    {
                        if (isGo.HasComponent(ent))
                        {
                            GODelayedDespawns.AddNoResize(spawnedGhost);
                        }
                        else
                        {
                            commandBuffer.DestroyEntity(ent);
                            spawnedGhostMap.Remove(spawnedGhost.ghost);
                        }
                    }
                }

                while (predictedDespawnQueue.Count > 0 &&
                       !predictedDespawnQueue.Peek().tick.IsNewerThan(predictedTick))
                {
                    var spawnedGhost = predictedDespawnQueue.Dequeue();
                    if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                    {
                        if (isGo.HasComponent(ent))
                        {
                            GODelayedDespawns.AddNoResize(spawnedGhost);
                        }
                        else
                        {
                            commandBuffer.DestroyEntity(ent);
                            spawnedGhostMap.Remove(spawnedGhost.ghost);
                        }
                    }
                }
            }
        }
    }

    internal struct GameObjectDespawnTracking : IComponentData
    {
        public NativeList<GhostDespawnSystem.DelayedDespawnGhost> allGODespawns;
    }

    // TODO-next GO despawn doesn't have APIs for burst compatible GO destruction. Raised this on slack. Disabling burst for now, since this is really just a system that schedules a job that's itself bursted anyway. But should come back to this if/when that's available. Slack thread https://unity.slack.com/archives/C0575F6KEAY/p1757546583041179
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateAfter(typeof(GhostDespawnSystem))]
    internal partial class GhostGameObjectDespawnManagedSystem : SystemBase
    {
        protected override void OnCreate()
        {
            if (World.IsHost())
            {
                this.Enabled = false;
                return;
            }
            RequireForUpdate<GhostGameObjectLink>();
            this.EntityManager.CreateEntity(typeof(GameObjectDespawnTracking));
        }

        protected override void OnUpdate()
        {
            ref var GODespawnTracking = ref SystemAPI.GetSingletonRW<GameObjectDespawnTracking>().ValueRW;
            var spawnedGhostMap = SystemAPI.GetSingletonRW<SpawnedGhostEntityMap>().ValueRO.SpawnedGhostMapRW;
            ProcessGameObjectDespawns(ref this.CheckedStateRef, GODespawnTracking.allGODespawns, spawnedGhostMap);
        }

        [Conditional("UNITY_6000_3_OR_NEWER")]  // Required to use GameObject bridge with EntityID
        void ProcessGameObjectDespawns(ref SystemState state, NativeList<GhostDespawnSystem.DelayedDespawnGhost> allGODespawns, NativeParallelHashMap<SpawnedGhost, Entity> spawnedGhostMap)
        {
            foreach (var spawnedGhost in allGODespawns)
            {
                if (spawnedGhostMap.TryGetValue(spawnedGhost.ghost, out var ent))
                {
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
                    var goIdToDespawn = state.EntityManager.GetComponentData<GhostGameObjectLink>(ent).AssociatedGameObject;

                    GameObject.DestroyImmediate(Resources.EntityIdToObject(goIdToDespawn));
                    spawnedGhostMap.Remove(spawnedGhost.ghost);
                    // This should be the last release, as all the other OnDestroy should have been called by the DestroyImmediate above.
                    // This in turn removes the GhostGameObjectLink cleanup component
                    GhostEntityMapping.ReleaseGameObjectEntityReference(goIdToDespawn, worldIsCreated: true);
#endif
                }
            }
        }
    }
}
