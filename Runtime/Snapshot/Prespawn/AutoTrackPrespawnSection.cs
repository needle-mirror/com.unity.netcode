#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;

namespace Unity.NetCode
{
    // RPCs to control the prespawn streaming. Sent by the client to the server when a scene is loaded/unloaded.
    // Server react by not sending any new snapshot udpdate for the pre-spawned ghosts that belong to the scenes for
    // witch streaming is disabled
    public struct StartStreamingSceneGhosts : IRpcCommand
    {
        public ulong SceneHash;
    }
    public struct StopStreamingSceneGhosts : IRpcCommand
    {
        public ulong SceneHash;
    }

    /// <summary>
    /// Track prespawn section load/unload events and send rpc to server to ack the loaded scene for that client
    /// </summary>
    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateBefore(typeof(ClientTrackLoadedPrespawnSections))]
    partial class ClientPrespawnAckSystem : SystemBase
    {
        private EndSimulationEntityCommandBufferSystem m_Barrier;
        private SceneSystem m_SceneSystem;
        private NetDebugSystem m_NetDebug;
        //Initialized by the Entities.WithStoreEntityQueryInField
        private EntityQuery m_InitializedSections;
        protected override void OnCreate()
        {
            m_Barrier = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
            m_SceneSystem = World.GetExistingSystem<SceneSystem>();
            m_NetDebug = World.GetExistingSystem<NetDebugSystem>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadOnly<NetworkIdComponent>(),
                ComponentType.Exclude<NetworkStreamDisconnected>(), ComponentType.Exclude<NetworkStreamRequestDisconnect>()));
            RequireForUpdate(m_InitializedSections);
        }

        protected override void OnUpdate()
        {
            if (HasSingleton<DisableAutomaticPrespawnSectionReporting>())
            {
                Enabled = false;
                return;
            }
            var entityCommandBuffer = m_Barrier.CreateCommandBuffer();
            var netDebug = m_NetDebug.NetDebug;
            var sceneSystem = m_SceneSystem;
            var inititalizedSceneCount = m_InitializedSections.CalculateEntityCount();
            var loadedScenes = new NativeList<bool>(inititalizedSceneCount, Allocator.TempJob);
            Entities
                .WithStoreEntityQueryInField(ref m_InitializedSections)
                .WithAll<SubSceneWithGhostStateComponent>()
                .WithoutBurst()
                .ForEach((Entity entity) =>
                {
                    loadedScenes.Add(sceneSystem.IsSectionLoaded(entity));
                }).Run();

            Entities
                .WithDisposeOnCompletion(loadedScenes)
                .WithReadOnly(loadedScenes)
                .ForEach((Entity entity, int entityInQueryIndex, ref SubSceneWithGhostStateComponent stateComponent) =>
                {
                    bool isLoaded = loadedScenes[entityInQueryIndex];
                    if (!isLoaded && stateComponent.Streaming != 0)
                    {
                        var reqUnload = entityCommandBuffer.CreateEntity();
                        entityCommandBuffer.AddComponent(reqUnload, new StopStreamingSceneGhosts
                        {
                            SceneHash = stateComponent.SubSceneHash,
                        });
                        entityCommandBuffer.AddComponent(reqUnload, new SendRpcCommandRequestComponent());
                        stateComponent.Streaming = 0;
                        LogStopStreaming(netDebug, stateComponent);
                    }
                    else if (isLoaded && stateComponent.Streaming == 0)
                    {
                        var reqUnload = entityCommandBuffer.CreateEntity();
                        entityCommandBuffer.AddComponent(reqUnload, new StartStreamingSceneGhosts
                        {
                            SceneHash = stateComponent.SubSceneHash
                        });
                        entityCommandBuffer.AddComponent(reqUnload, new SendRpcCommandRequestComponent());
                        stateComponent.Streaming = 1;
                        LogStartStreaming(netDebug, stateComponent);
                    }
                }).Schedule();
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        [Conditional("NETCODE_DEBUG")]
        private static void LogStopStreaming(in NetDebug netDebug, in SubSceneWithGhostStateComponent stateComponent)
        {
            netDebug.DebugLog(FixedString.Format("Request stop streaming scene {0}",
                NetDebug.PrintHex(stateComponent.SubSceneHash)));
        }
        [Conditional("NETCODE_DEBUG")]
        private static void LogStartStreaming(in NetDebug netDebug, in SubSceneWithGhostStateComponent stateComponent)
        {
            netDebug.DebugLog(FixedString.Format("Request start streaming scene {0}",
                NetDebug.PrintHex(stateComponent.SubSceneHash)));
        }
    }

    /// <summary>
    /// Handle the StartStreaming/StopStreaming rpcs from the client and update the list of streamin/acked scenes.
    /// It is possible to add user-defined behaviors by consuming or reading the rpc before that system runs.
    /// </summary>
    [UpdateInWorld(TargetWorld.Server)]
    [UpdateInGroup(typeof(PrespawnGhostSystemGroup))]
    [UpdateBefore(typeof(ServerTrackLoadedPrespawnSections))]
    partial class ServerPrespawnAckSystem : SystemBase
    {
        private EndSimulationEntityCommandBufferSystem m_Barrier;
        private NetDebugSystem m_NetDebug;
        protected override void OnCreate()
        {
            m_Barrier = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
            m_NetDebug = World.GetExistingSystem<NetDebugSystem>();
        }

        protected override void OnUpdate()
        {
            if (HasSingleton<DisableAutomaticPrespawnSectionReporting>())
            {
                Enabled = false;
                return;
            }
            var ecb = m_Barrier.CreateCommandBuffer();
            var netDebug = m_NetDebug.NetDebug;
            Entities.ForEach((Entity entity, in StartStreamingSceneGhosts streamingReq, in ReceiveRpcCommandRequestComponent requestComponent) =>
            {
                var prespawnSceneAcks = GetBuffer<PrespawnSectionAck>(requestComponent.SourceConnection);
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx == -1)
                {
                    LogStartStreaming(netDebug, GetComponent<NetworkIdComponent>(requestComponent.SourceConnection).Value, streamingReq.SceneHash);
                    prespawnSceneAcks.Add(new PrespawnSectionAck { SceneHash = streamingReq.SceneHash });
                }
                ecb.DestroyEntity(entity);
            }).Schedule();

            Entities.ForEach((Entity entity, in StopStreamingSceneGhosts streamingReq, in ReceiveRpcCommandRequestComponent requestComponent) =>
            {
                var prespawnSceneAcks = GetBuffer<PrespawnSectionAck>(requestComponent.SourceConnection);
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx != -1)
                {
                    LogStopStreaming(netDebug, GetComponent<NetworkIdComponent>(requestComponent.SourceConnection).Value, streamingReq.SceneHash);
                    prespawnSceneAcks.RemoveAtSwapBack(ackIdx);
                }
                ecb.DestroyEntity(entity);
            }).Schedule();
            m_Barrier.AddJobHandleForProducer(Dependency);
        }

        [Conditional("NETCODE_DEBUG")]
        private static void LogStopStreaming(in NetDebug netDebug, int connection, ulong sceneHash)
        {
            netDebug.DebugLog(FixedString.Format("Connection {0} stop streaming scene {1}", connection, NetDebug.PrintHex(sceneHash)));
        }
        [Conditional("NETCODE_DEBUG")]
        private static void LogStartStreaming(in NetDebug netDebug, int connection, ulong sceneHash)
        {
            netDebug.DebugLog(FixedString.Format("Connection {0} start streaming scene {1}", connection, NetDebug.PrintHex(sceneHash)));
        }
    }
}
