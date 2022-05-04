using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.PrespawnTests;
using UnityEditor;
using UnityEngine;
using Unity.Scenes;
using UnityEngine.SceneManagement;

namespace Unity.NetCode.Tests
{
    public struct RequestUnLoadScene : IRpcCommand
    {
        public ulong SceneHash;
        public uint ServerTick;
    }
    public struct NotifySceneLoaded : IRpcCommand
    {
        public ulong SceneHash;
    }
    public struct NotifyUnloadingScene : IRpcCommand
    {
        public ulong SceneHash;
    }

    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    [DisableAutoCreation]
    partial class ServerSceneNotificationSystem : SystemBase
    {
        private EndSimulationEntityCommandBufferSystem m_Barrier;
        protected override void OnCreate()
        {
            m_Barrier = World.GetExistingSystem<EndSimulationEntityCommandBufferSystem>();
            RequireSingletonForUpdate<PrespawnSceneLoaded>();
        }

        protected override void OnUpdate()
        {
            var ecb = m_Barrier.CreateCommandBuffer();
            var serverTick = World.GetExistingSystem<ServerSimulationSystemGroup>().ServerTick;
            Entities.ForEach((Entity entity, in NotifySceneLoaded streamingReq, in ReceiveRpcCommandRequestComponent requestComponent) =>
            {
                var prespawnSceneAcks = GetBuffer<PrespawnSectionAck>(requestComponent.SourceConnection);
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx == -1)
                    prespawnSceneAcks.Add(new PrespawnSectionAck { SceneHash = streamingReq.SceneHash });
                ecb.DestroyEntity(entity);
            }).Schedule();

            Entities.ForEach((Entity entity, in NotifyUnloadingScene streamingReq, in ReceiveRpcCommandRequestComponent requestComponent) =>
            {
                var prespawnSceneAcks = GetBuffer<PrespawnSectionAck>(requestComponent.SourceConnection);
                int ackIdx = prespawnSceneAcks.IndexOf(streamingReq.SceneHash);
                if (ackIdx != -1)
                {
                    prespawnSceneAcks.RemoveAt(ackIdx);
                    //Send back an rpc to confirm the unload
                    var reqEnt = ecb.CreateEntity();
                    ecb.AddComponent(reqEnt, new RequestUnLoadScene
                    {
                        SceneHash = streamingReq.SceneHash,
                        ServerTick = serverTick
                    });
                    ecb.AddComponent(reqEnt, new SendRpcCommandRequestComponent
                    {
                        TargetConnection = requestComponent.SourceConnection
                    });
                }
                ecb.DestroyEntity(entity);
            }).Schedule();

            m_Barrier.AddJobHandleForProducer(Dependency);
        }
    }

    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    [DisableAutoCreation]
    partial class ClientUnloadSceneSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var hashmap = new NativeParallelHashMap<ulong, Entity>(16, Allocator.TempJob);
            Entities.ForEach((Entity entity, in SubSceneWithPrespawnGhosts sub) =>
            {
                hashmap[sub.SubSceneHash] =  entity;
            }).Run();
            var barrier = World.GetExistingSystem<BeginSimulationEntityCommandBufferSystem>();
            var ecb = barrier.CreateCommandBuffer();
            Entities
                .WithDisposeOnCompletion(hashmap)
                .ForEach((Entity entity, in RequestUnLoadScene unloadScene, in ReceiveRpcCommandRequestComponent requestComponent) =>
                {
                    if(hashmap.TryGetValue(unloadScene.SceneHash, out var sceneEntity))
                    {
                        ecb.RemoveComponent<RequestSceneLoaded>(sceneEntity);
                    }
                }).Schedule();
            barrier.AddJobHandleForProducer(Dependency);
        }
    }

    public partial class SubSceneLoadingTests
    {
        [Test]
        public void CustomSceneAckFlowTest()
        {
            const int numObjects = 5;
            var ghostPrefab = SubSceneHelper.CreateSimplePrefab(ScenePath, "WithData1", typeof(GhostAuthoringComponent),
                typeof(SomeDataAuthoring));
            var parentScene = SubSceneHelper.CreateEmptyScene(ScenePath, "StreamTest");
            var subScenes = new SubScene[4];
            for(int i=0;i<4;++i)
            {
                subScenes[i] = SubSceneHelper.AddSubSceneToParentScene(parentScene, SubSceneHelper.CreateSubSceneWithPrefabs(
                    ScenePath, $"Sub{i}", new[]
                    {
                        ghostPrefab,
                    }, numObjects)
                );
            }
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(LoadingGhostCollectionSystem),
                    typeof(ServerSceneNotificationSystem),
                    typeof(ClientUnloadSceneSystem));
                testWorld.CreateWorlds(true, 1);
                float frameTime = 1.0f / 60.0f;
                //Server load all the scenes
                SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                //Disable the automatic reporting
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(DisableAutomaticPrespawnSectionReporting));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(DisableAutomaticPrespawnSectionReporting));
                testWorld.GoInGame();
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);
                var subSceneList = SubSceneStreamingTestHelper.GetPrespawnLoaded(testWorld, testWorld.ServerWorld);
                Assert.AreEqual(4, subSceneList.Length);
                ulong lastLoadedSceneHash = 0ul;
                for(int scene=0; scene<4; ++scene)
                {
                    var sceneEntity = SubSceneHelper.LoadSubSceneAsync(testWorld.ClientWorlds[0], testWorld, subScenes[scene].SceneGUID, frameTime, 16);
                    //Run a bunch of frame so scene are initialized
                    for (int i = 0; i < 16; ++i)
                        testWorld.Tick(frameTime);

                    var prespawnSection = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(sceneEntity)[1].Value;
                    var loadedScenHash = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SubSceneWithPrespawnGhosts>(prespawnSection).SubSceneHash;
                    //Notify loaded
                    var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                    var notifyLoaded = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(notifyLoaded, new NotifySceneLoaded
                    {
                        SceneHash = loadedScenHash
                    });
                    commandBuffer.AddComponent(notifyLoaded, new SendRpcCommandRequestComponent());
                    commandBuffer.Playback(testWorld.ClientWorlds[0].EntityManager);
                    commandBuffer.Dispose();
                    //Run some frame
                    for (int i = 0; i < 32; ++i)
                        testWorld.Tick(frameTime);
                    //Unload the previous one. Send the rpc.
                    if (lastLoadedSceneHash != 0)
                    {
                        commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                        //Unload the prev loaded scene. Send and an RPC for that
                        var reqUnload = commandBuffer.CreateEntity();
                        commandBuffer.AddComponent(reqUnload, new NotifyUnloadingScene { SceneHash = lastLoadedSceneHash });
                        commandBuffer.AddComponent(reqUnload, new SendRpcCommandRequestComponent());
                        commandBuffer.Playback(testWorld.ClientWorlds[0].EntityManager);
                        commandBuffer.Dispose();
                    }
                    lastLoadedSceneHash = loadedScenHash;
                    for (int i = 0; i < 32; ++i)
                        testWorld.Tick(frameTime);
                    //Only one scene should be active
                    var subSceneEntity = testWorld.TryGetSingletonEntity<PrespawnsSceneInitialized>(testWorld.ClientWorlds[0]);
                    Assert.AreNotEqual(Entity.Null, subSceneEntity);
                    //Only 5 ghost should be present
                    var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PreSpawnedGhostIndex>());
                    Assert.AreEqual(numObjects, query.CalculateEntityCount());

                }
            }
        }
    }
}
