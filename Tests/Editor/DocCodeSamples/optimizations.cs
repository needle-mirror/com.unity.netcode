using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace DocumentationCodeSamples
{
    [BurstCompile]
    partial class optimizations
    {
        #region OffFrames
        [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
        [UpdateInGroup(typeof(InitializationSystemGroup))]
        public partial struct DoExtraWorkSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                var networkTime = state.World.EntityManager.CreateEntityQuery(typeof(NetworkTime)).GetSingleton<NetworkTime>();
                if (!networkTime.IsOffFrame)
                    DoExtraWork();
            }

            void DoExtraWork()
            {
                // We know this frame will be less busy, we can do extra work
            }
        }
        #endregion

        private struct AsteroidScore : IComponentData { }

        public partial struct RelevencyExampleSystem : ISystem
        {
            #region Relevancy
            public void OnCreate(ref SystemState state)
            {
                var relevancy = SystemAPI.GetSingletonRW<GhostRelevancy>();
                relevancy.ValueRW.DefaultRelevancyQuery = SystemAPI.QueryBuilder().WithAllRW<AsteroidScore>().Build();
            }
            #endregion
        }

        public partial struct DistanceBasedImportanceExampleSystem : ISystem
        {
            private const int tileSize = 256;

            public void OnUpdate(ref SystemState state)
            {
                #region DistanceBasedImportance
                var gridSingleton = state.EntityManager.CreateSingleton(new GhostDistanceData
                {
                    TileSize = new int3(tileSize, tileSize, 256),
                    TileCenter = new int3(0, 0, 128),
                    TileBorderWidth = new float3(1f, 1f, 1f),
                });
                state.EntityManager.AddComponentData(gridSingleton, new GhostImportance
                {
                    BatchScaleImportanceFunction = GhostDistanceImportance.BatchScaleFunctionPointer,
                    GhostConnectionComponentType = ComponentType.ReadOnly<GhostConnectionPosition>(),
                    GhostImportanceDataType = ComponentType.ReadOnly<GhostDistanceData>(),
                    GhostImportancePerChunkDataType = ComponentType.ReadOnly<GhostDistancePartitionShared>(),
                });
                #endregion
            }
        }

        public struct PlayerStateComponentData : IComponentData { }

        [BurstCompile]
        public struct RpcExample : IComponentData, IRpcCommandSerializer<RpcExample>
        {
            public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in RpcExample data)
            {
            }

            public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref RpcExample data)
            {
            }

            #region GhostDistancePartitioning
            [BurstCompile(DisableDirectCall = true)]
            [AOT.MonoPInvokeCallback(typeof(RpcExecutor.ExecuteDelegate))]
            private static void InvokeExecute(ref RpcExecutor.Parameters parameters)
            {
                var rpcData = default(RpcExample);
                rpcData.Deserialize(ref parameters.Reader, parameters.DeserializerState, ref rpcData);

                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, new PlayerStateComponentData());
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, default(NetworkStreamInGame));
                parameters.CommandBuffer.AddComponent(parameters.JobIndex, parameters.Connection, default(GhostConnectionPosition)); // <-- Here.
            }
            #endregion

            static readonly PortableFunctionPointer<RpcExecutor.ExecuteDelegate> InvokeExecuteFunctionPointer =
                new PortableFunctionPointer<RpcExecutor.ExecuteDelegate>(InvokeExecute);
            public PortableFunctionPointer<RpcExecutor.ExecuteDelegate> CompileExecute()
            {
                return InvokeExecuteFunctionPointer;
            }
        }

        #region SetImportancePosition
        [BurstCompile]
        partial struct UpdateConnectionPositionSystemJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<LocalTransform> transformFromEntity;
            public void Execute(ref GhostConnectionPosition conPos, in CommandTarget target)
            {
                if (!transformFromEntity.HasComponent(target.targetEntity))
                    return;
                conPos = new GhostConnectionPosition
                {
                    Position = transformFromEntity[target.targetEntity].Position
                };
            }
        }
        #endregion
    }
}
