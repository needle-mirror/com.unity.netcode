using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    partial class ghost_spawning
    {
        public partial struct PredictedSpawnExampleSystem : ISystem
        {
            #region IsFirstTimeFullyPredictingTick
            public void OnUpdate(ref SystemState state)
            {
                // Other input like movement handled here or in another system...

                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                if (!networkTime.IsFirstTimeFullyPredictingTick)
                    return;
                // Handle the input for instantiating a bullet for example here
                // ...
            }
            #endregion
        }

        [DisableAutoCreation]
        #region SpawnGhostSystem
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        public partial class SpawnGhost : SystemBase
        {
            protected override void OnUpdate()
            {
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                //Only do that once. When the client re-simulate the same tick, the flag is false.
                if (!networkTime.IsFirstTimeFullyPredictingTick)
                {
                    return;
                }
                var prefab = GetPrefabToSpawn();
                var typeToCollection = SystemAPI.GetSingleton<GhostCollection>().GhostTypeToColletionIndex;
                var type = World.EntityManager.GetComponentData<GhostType>(prefab);
                //Can't spawn yet. The prefab is not registered.
                if (!typeToCollection.ContainsKey(type))
                {
                    return;
                }
                //it is now valid to spawn. That does not means the ghost will be initialized properly yet
                //that can be still the case if the
            }
        }
        #endregion

        public static Entity GetPrefabToSpawn()
        {
            return new Entity();
        }

        private struct MySpawner : IComponentData
        {
            public Entity Prefab;
        }

        #region ClassificationSystem
        [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
        [UpdateInGroup(typeof(GhostSpawnClassificationSystemGroup))]
        [UpdateAfter(typeof(GhostSpawnClassificationSystem))]
        [BurstCompile]
        public partial struct ClassificationSystem : ISystem
        {
            public Entity spawnListEntity;
            public BufferLookup<PredictedGhostSpawn> PredictedSpawnListLookup;
            int m_GhostType;

            [BurstCompile]
            public void OnCreate(ref SystemState state)
            {
                PredictedSpawnListLookup = state.GetBufferLookup<PredictedGhostSpawn>();
                state.RequireForUpdate<MySpawner>();
                state.RequireForUpdate<GhostSpawnQueue>();
                state.RequireForUpdate<PredictedGhostSpawnList>();
                state.RequireForUpdate<NetworkId>();
                state.RequireForUpdate<GhostCollection>();
                m_GhostType = -1;
            }

            [BurstCompile]
            public void OnUpdate(ref SystemState state)
            {
                if (m_GhostType == -1)
                {
                    // Lookup the prefab you want to use for classification
                    var prefabEntity = SystemAPI.GetSingleton<MySpawner>().Prefab;
                    var collectionEntity = SystemAPI.GetSingletonEntity<GhostCollection>();
                    var ghostPrefabTypes = state.EntityManager.GetBuffer<GhostCollectionPrefab>(collectionEntity);

                    for (int i = 0; i < ghostPrefabTypes.Length; i++)
                    {
                        if (ghostPrefabTypes[i].GhostPrefab == prefabEntity)
                        {
                            m_GhostType = i;
                            break;
                        }
                    }
                }

                PredictedSpawnListLookup.Update(ref state);

                state.Dependency = new ClassificationJob
                {
                    PredictedSpawnListLookup = PredictedSpawnListLookup,
                    spawnListEntity = spawnListEntity,
                    ghostType = m_GhostType
                }.Schedule(state.Dependency);
            }
        }
        #endregion

        #region ClassificationJob
        [BurstCompile]
        public partial struct ClassificationJob : IJobEntity
        {
            public BufferLookup<PredictedGhostSpawn> PredictedSpawnListLookup;
            public Entity spawnListEntity;
            public int ghostType;

            public void Execute(DynamicBuffer<GhostSpawnBuffer> ghosts, DynamicBuffer<SnapshotDataBuffer> data)
            {
                var predictedSpawnList = PredictedSpawnListLookup[spawnListEntity];
                for (int i = 0; i < ghosts.Length; ++i)
                {
                    var newGhostSpawn = ghosts[i];
                    if (newGhostSpawn.SpawnType != GhostSpawnBuffer.Type.Predicted ||
                        newGhostSpawn.HasClassifiedPredictedSpawn || newGhostSpawn.PredictedSpawnEntity != Entity.Null)
                        continue;

                    // Mark all the spawns of this type as classified even if not our own predicted spawns
                    // otherwise spawns from other players might be picked up by the default classification system when
                    // it runs.
                    if (newGhostSpawn.GhostType == ghostType)
                        newGhostSpawn.HasClassifiedPredictedSpawn = true;

                    // Find new ghost spawns (from ghost snapshot) which match the predict spawned ghost type handled by
                    // this classification system. You can use the SnapshotDataBufferLookup to inspect components in the
                    // received snapshot in your matching function
                    for (int j = 0; j < predictedSpawnList.Length; ++j)
                    {
                        if (newGhostSpawn.GhostType != predictedSpawnList[j].ghostType)
                            continue;

                        if (YOUR_FUZZY_MATCH(newGhostSpawn, predictedSpawnList[j]))
                        {
                            newGhostSpawn.PredictedSpawnEntity = predictedSpawnList[j].entity;
                            predictedSpawnList[j] = predictedSpawnList[predictedSpawnList.Length - 1];
                            predictedSpawnList.RemoveAt(predictedSpawnList.Length - 1);
                            break;
                        }
                    }

                    ghosts[i] = newGhostSpawn;
                }
            }

            private bool YOUR_FUZZY_MATCH(GhostSpawnBuffer newGhostSpawn, PredictedGhostSpawn predictedGhostSpawn)
            {
                // Note: The below is similar to the default implementation that is used in the netcode package.
                // You can use this as a starting point and modify it to suit your needs.
                // In this case, we just check whether the type is the same and the spawn tick is close.
                // This is adequate in most cases, but you might need to add more checks depending on your game.
                return math.abs(newGhostSpawn.ServerSpawnTick.TicksSince(predictedGhostSpawn.spawnTick)) < 5;
            }
        }
        #endregion
    }
}
