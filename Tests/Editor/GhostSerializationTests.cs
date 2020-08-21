using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode.Tests;
using Unity.Jobs;
using UnityEngine;
using Unity.NetCode;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Collections;

namespace Unity.NetCode.Tests
{
    public class GhostValueSerializerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostValueSerializer {});
        }
    }

    public struct GhostValueSerializer : IComponentData
    {
        [GhostField]
        public bool BoolValue;
        [GhostField]
        public int IntValue;
        [GhostField]
        public uint UIntValue;
        [GhostField(Quantization=10)]
        public float FloatValue;
        [GhostField(Quantization=0)]
        public float UnquantizedFloatValue;

        [GhostField(Quantization=10)]
        public float2 Float2Value;
        [GhostField(Quantization=0)]
        public float2 UnquantizedFloat2Value;
        [GhostField(Quantization=10)]
        public float3 Float3Value;
        [GhostField(Quantization=0)]
        public float3 UnquantizedFloat3Value;
        [GhostField(Quantization=10)]
        public float4 Float4Value;
        [GhostField(Quantization=0)]
        public float4 UnquantizedFloat4Value;
        [GhostField(Quantization=1000)]
        public quaternion QuaternionValue;
        [GhostField(Quantization=0)]
        public quaternion UnquantizedQuaternionValue;

        [GhostField]
        public FixedString32 StringValue32;
        [GhostField]
        public FixedString64 StringValue64;
        [GhostField]
        public FixedString128 StringValue128;
        [GhostField]
        public FixedString512 StringValue512;
        [GhostField]
        public FixedString4096 StringValue4096;

        [GhostField]
        public Entity EntityValue;
    }
    public class GhostSerializationTests
    {
        void VerifyGhostValues(NetCodeTestWorld testWorld)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);

            Assert.AreNotEqual(Entity.Null, serverEntity);
            Assert.AreNotEqual(Entity.Null, clientEntity);

            var serverValues = testWorld.ServerWorld.EntityManager.GetComponentData<GhostValueSerializer>(serverEntity);
            var clientValues = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity);
            Assert.AreEqual(serverValues.BoolValue, clientValues.BoolValue);
            Assert.AreEqual(serverValues.IntValue, clientValues.IntValue);
            Assert.AreEqual(serverValues.FloatValue, clientValues.FloatValue);
            Assert.AreEqual(serverValues.UnquantizedFloatValue, clientValues.UnquantizedFloatValue);

            Assert.AreEqual(serverValues.Float2Value, clientValues.Float2Value);
            Assert.AreEqual(serverValues.UnquantizedFloat2Value, clientValues.UnquantizedFloat2Value);
            Assert.AreEqual(serverValues.Float3Value, clientValues.Float3Value);
            Assert.AreEqual(serverValues.UnquantizedFloat3Value, clientValues.UnquantizedFloat3Value);
            Assert.AreEqual(serverValues.Float4Value, clientValues.Float4Value);
            Assert.AreEqual(serverValues.UnquantizedFloat4Value, clientValues.UnquantizedFloat4Value);
            Assert.Less(math.distance(serverValues.QuaternionValue.value, clientValues.QuaternionValue.value), 0.001f);
            Assert.AreEqual(serverValues.UnquantizedQuaternionValue, clientValues.UnquantizedQuaternionValue);

            Assert.AreEqual(serverValues.StringValue32, clientValues.StringValue32);
            Assert.AreEqual(serverValues.StringValue64, clientValues.StringValue64);
            Assert.AreEqual(serverValues.StringValue128, clientValues.StringValue128);
            Assert.AreEqual(serverValues.StringValue512, clientValues.StringValue512);
            Assert.AreEqual(serverValues.StringValue4096, clientValues.StringValue4096);

            Assert.AreEqual(serverEntity, serverValues.EntityValue);
            Assert.AreEqual(clientEntity, clientValues.EntityValue);
        }
        void SetGhostValues(NetCodeTestWorld testWorld, int baseValue)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostValueSerializer
            {
                BoolValue = (baseValue&1) != 0,
                IntValue = baseValue,
                UIntValue = (uint)baseValue + 1u,
                FloatValue = baseValue + 2,
                UnquantizedFloatValue = baseValue + 3,

                Float2Value = new float2(baseValue + 4, baseValue + 5),
                UnquantizedFloat2Value = new float2(baseValue + 6, baseValue + 7),
                Float3Value = new float3(baseValue + 8, baseValue + 9, baseValue + 10),
                UnquantizedFloat3Value = new float3(baseValue + 11, baseValue + 12, baseValue + 13),
                Float4Value = new float4(baseValue + 14, baseValue + 15, baseValue + 16, baseValue + 17),
                UnquantizedFloat4Value = new float4(baseValue + 18, baseValue + 19, baseValue + 20, baseValue + 21),
                QuaternionValue = math.normalize(new quaternion(baseValue + 22, baseValue + 23, baseValue + 24, baseValue + 25)),
                UnquantizedQuaternionValue = math.normalize(new quaternion(baseValue + 26, baseValue + 27, baseValue + 28, baseValue + 29)),

                StringValue32 = new FixedString32($"baseValue = {baseValue}"),
                StringValue64 = new FixedString64($"baseValue = {baseValue*2}"),
                StringValue128 = new FixedString128($"baseValue = {baseValue*3}"),
                StringValue512 = new FixedString512($"baseValue = {baseValue*4}"),
                StringValue4096 = new FixedString4096($"baseValue = {baseValue*5}"),

                EntityValue = serverEntity
            });
        }
        [Test]
        public void GhostValuesAreSerialized()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                SetGhostValues(testWorld, 42);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick(frameTime);

                VerifyGhostValues(testWorld);
                SetGhostValues(testWorld, 43);

                for (int i = 0; i < 64; ++i)
                    testWorld.Tick(frameTime);

                // Assert that replicated version is correct
                VerifyGhostValues(testWorld);
            }
        }
        [Test]
        public void EntityReferenceSetAtSpawnIsResolved()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                var referencedGameObject = new GameObject();
                referencedGameObject.AddComponent<GhostOwnerComponentAuthoring>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, referencedGameObject));

                testWorld.CreateWorlds(true, 1);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();
                for (int i = 0; i < 4; ++i)
                {
                    testWorld.Tick(frameTime);
                }

                var serverRefEntity = testWorld.SpawnOnServer(referencedGameObject);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostValueSerializer{EntityValue = serverRefEntity});

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick(frameTime);
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // Make sure the reference always exist if the ghost exists
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                // Verify that we did get the referenced entity at some point
                Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]));
            }
        }
        [Test]
        public void EntityReferenceUnavailableGhostIsResolved()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                var referencedGameObject = new GameObject();
                referencedGameObject.AddComponent<GhostOwnerComponentAuthoring>();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, referencedGameObject));

                testWorld.CreateWorlds(true, 1);
                var ghostSendSystem = testWorld.ServerWorld.GetExistingSystem<GhostSendSystem>();

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                ghostSendSystem.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                // Go in-game
                testWorld.GoInGame();
                for (int i = 0; i < 4; ++i)
                {
                    testWorld.Tick(frameTime);
                }

                var con = testWorld.TryGetSingletonEntity<NetworkIdComponent>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, con);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkIdComponent>(con).Value;

                var serverRefEntity = testWorld.SpawnOnServer(referencedGameObject);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostValueSerializer{EntityValue = serverRefEntity});

                testWorld.Tick(frameTime);

                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverEnt).ghostId;
                var serverRefGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostComponent>(serverRefEntity).ghostId;

                // only mark the entity with the ref as relevant so that arrived before the referenced entity exists
                ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick(frameTime);
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // Make sure the reference always exist if the ghost exists
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                // Verify that we did not the referenced entity since it is irrelevant
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]));

                ghostSendSystem.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverRefGhostId), 1);
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick(frameTime);
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // Make sure the reference always exist if the ghost exists
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]));

                // Delete the referenced entity and make sure the ref is updated
                testWorld.ServerWorld.EntityManager.DestroyEntity(serverRefEntity);
                int mismatchFrames = 0;
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick(frameTime);
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // The desapwn order might not be the same between client and server, if the server has despawned the entity there will be no reference,
                        // but the client despawns at the end of the frame it was destroyed so it might still exist for one frame
                        Assert.IsFalse(clientRefEntity == Entity.Null && testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue != Entity.Null);
                        if (clientRefEntity != testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue)
                            ++mismatchFrames;
                    }
                }
                Assert.LessOrEqual(mismatchFrames, 1);
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwnerComponent>(testWorld.ClientWorlds[0]));
            }
        }
    }
}