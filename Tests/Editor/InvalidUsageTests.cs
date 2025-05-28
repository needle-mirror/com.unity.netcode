using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace Unity.NetCode.Tests
{
    internal class InvalidUsageConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class DeleteGhostOnClientSystem : SystemBase
    {
        public static int s_DeleteCount;
        protected override void OnCreate()
        {
            RequireForUpdate<GhostOwner>();
        }
        protected override void OnUpdate()
        {
            if (s_DeleteCount > 0)
            {
                --s_DeleteCount;
                EntityManager.DestroyEntity(SystemAPI.GetSingletonEntity<GhostOwner>());
            }
        }
    }
    internal class InvalidUsageTests
    {
        [Test]
        public void CanRecoverFromDeletingGhostOnClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                DeleteGhostOnClientSystem.s_DeleteCount = 1;
                testWorld.Bootstrap(true, typeof(DeleteGhostOnClientSystem));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new InvalidUsageConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);

                var serverEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner{NetworkId = 42});

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                LogAssert.Expect(LogType.Error, new Regex(@"Found a ghost (.*) in the ghost map which does not have an entity connected to it(.*)This can happen if you delete ghost entities"));
                LogAssert.Expect(LogType.Error, new Regex("Ghost ID \\d+ has already been added to the spawned ghost map"));
                // Let the game run for a bit so the ghosts are spawned on the client
                // There will be a bunch of "Received baseline for a ghost we do not have ghostId=0 baselineTick=1 serverTick=2" messages, ignore them
                LogAssert.ignoreFailingMessages = true;
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();
                LogAssert.ignoreFailingMessages = false;

                // Validate that the ghost was deleted on the cliet
                Assert.AreEqual(0, DeleteGhostOnClientSystem.s_DeleteCount);

                // Check that the client world has the right thing and value
                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                Assert.AreEqual(42, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostOwner>(clientEnt).NetworkId);

                // Delete on server
                testWorld.ServerWorld.EntityManager.DestroyEntity(serverEnt);
                for (int i = 0; i < 5; ++i)
                    testWorld.Tick();
                clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(Entity.Null, clientEnt);
            }
        }
        [Test]
        public void UnintializedGhostOwnerThrowsException()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new InvalidUsageConverter();
                ghostGameObject.AddComponent<GhostAuthoringComponent>().DefaultGhostMode = GhostMode.OwnerPredicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                LogAssert.Expect(LogType.Error, new Regex("Trying to spawn an owner predicted ghost which does not have a valid owner set. When using owner prediction you must set GhostOwner.NetworkId when spawning the ghost. If the ghost is not owned by a player you can set NetworkId to -1."));
                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();
            }
        }
    }
}
