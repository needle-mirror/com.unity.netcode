using System;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode.Tests
{
    public class GhostValueSerializerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostValueSerializer {});
        }
    }

    public enum EnumUntyped
    {
        Value0 = 255,
    }
    public enum EnumS8 : sbyte
    {
        Value0 = 126,
    }
    public enum EnumU8 : byte
    {
        Value0 = 253,
    }
    public enum EnumS16 : short
    {
        Value0 = 0x7AAB
    }
    public enum EnumU16 : ushort
    {
        Value0 = 0xF00D,
    }
    public enum EnumS32
    {
        Value0 = 0x007AD0BE,
    }
    public enum EnumU32 : uint
    {
        Value0 = 0xBAADF00D
    }
    public enum EnumS64 : long
    {
        Value0 = 0x791BBCDC0CCAEDD1,
    }
    public enum EnumU64 : ulong
    {
        Value0 = 0xABBA1970F1809FE2,
    }

    public struct GhostValueSerializer : IComponentData
    {
        [GhostField] public bool BoolValue;
        [GhostField] public int IntValue;
        [GhostField] public uint UIntValue;
        [GhostField] public long LongValue;
        [GhostField] public ulong ULongValue;

        [GhostField] public EnumUntyped EnumUntyped;
        [GhostField] public EnumS8   EnumS08;
        [GhostField] public EnumU8   EnumU08;
        [GhostField] public EnumS16  EnumS16;
        [GhostField] public EnumU16  EnumU16;
        [GhostField] public EnumS32  EnumS32;
        [GhostField] public EnumU32  EnumU32;
        [GhostField] public EnumS64  EnumS64;
        [GhostField] public EnumU64  EnumU64;

        [GhostField(Quantization=10)] public float FloatValue;
        [GhostField(Quantization=0)] public float UnquantizedFloatValue;
        [GhostField(Quantization=1000)] public double DoubleValue;
        [GhostField(Quantization=0)] public double UnquantizedDoubleValue;
        [GhostField(Quantization=10)] public float2 Float2Value;
        [GhostField(Quantization=0)] public float2 UnquantizedFloat2Value;
        [GhostField(Quantization=10)] public float3 Float3Value;
        [GhostField(Quantization=0)] public float3 UnquantizedFloat3Value;
        [GhostField(Quantization=10)] public float4 Float4Value;
        [GhostField(Quantization=0)] public float4 UnquantizedFloat4Value;
        [GhostField(Quantization=1000)] public quaternion QuaternionValue;
        [GhostField(Quantization=0)] public quaternion UnquantizedQuaternionValue;
        [GhostField] public FixedString32Bytes StringValue32;
        [GhostField] public FixedString64Bytes StringValue64;
        [GhostField] public FixedString128Bytes StringValue128;
        [GhostField] public FixedString512Bytes StringValue512;
        [GhostField] public FixedString4096Bytes StringValue4096;
        [GhostField] public NetworkTick InvalidTickValue;
        [GhostField] public NetworkTick TickValue;
        [GhostField] public Entity EntityValue;
    }
    public class GhostSerializationTests
    {
        static void VerifyGhostValues(NetCodeTestWorld testWorld)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);

            Assert.AreNotEqual(Entity.Null, serverEntity);
            Assert.AreNotEqual(Entity.Null, clientEntity);

            var serverValues = testWorld.ServerWorld.EntityManager.GetComponentData<GhostValueSerializer>(serverEntity);
            var clientValues = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity);
            Assert.AreEqual(serverEntity, serverValues.EntityValue);
            Assert.AreEqual(clientEntity, clientValues.EntityValue);
            VerifyGhostValues(serverValues, clientValues);
        }

        static void VerifyGhostValues(GhostValueSerializer serverValues, GhostValueSerializer clientValues)
        {
            Assert.AreEqual(serverValues.BoolValue, clientValues.BoolValue);
            Assert.AreEqual(serverValues.IntValue, clientValues.IntValue);
            Assert.AreEqual(serverValues.UIntValue, clientValues.UIntValue);
            Assert.AreEqual(serverValues.LongValue, clientValues.LongValue);
            Assert.AreEqual(serverValues.ULongValue, clientValues.ULongValue);
            Assert.AreEqual(serverValues.FloatValue, clientValues.FloatValue);
            Assert.AreEqual(serverValues.UnquantizedFloatValue, clientValues.UnquantizedFloatValue);
            Assert.AreEqual(serverValues.UnquantizedDoubleValue, clientValues.UnquantizedDoubleValue);
            Assert.LessOrEqual(math.distance(serverValues.DoubleValue, clientValues.DoubleValue), 1e-3);

            Assert.AreEqual(serverValues.EnumUntyped,clientValues.EnumUntyped);
            Assert.AreEqual(serverValues.EnumS08,clientValues.EnumS08);
            Assert.AreEqual(serverValues.EnumU08,clientValues.EnumU08);
            Assert.AreEqual(serverValues.EnumS16,clientValues.EnumS16);
            Assert.AreEqual(serverValues.EnumU16,clientValues.EnumU16);
            Assert.AreEqual(serverValues.EnumS32,clientValues.EnumS32);
            Assert.AreEqual(serverValues.EnumU32,clientValues.EnumU32);
            Assert.AreEqual(serverValues.EnumS64,clientValues.EnumS64);
            Assert.AreEqual(serverValues.EnumU64,clientValues.EnumU64);

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
            Assert.AreEqual(serverValues.InvalidTickValue, clientValues.InvalidTickValue);
            Assert.AreEqual(serverValues.TickValue, clientValues.TickValue);
        }

        void SetGhostValuesOnServer(NetCodeTestWorld testWorld, int baseValue)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, CreateGhostValues(baseValue, serverEntity));
        }

        private static GhostValueSerializer CreateGhostValues(int baseValue, Entity serverEntity)
        {
            return new GhostValueSerializer
            {
                BoolValue = (baseValue&1) != 0,
                IntValue = baseValue,
                UIntValue = (uint)baseValue + 1u,
                LongValue = baseValue + 0x1234567898763210L,
                ULongValue = ((ulong)baseValue) + 0x8234567898763210UL,
                FloatValue = baseValue + 2,
                UnquantizedFloatValue = baseValue + 3,
                DoubleValue = 1234.456 + baseValue,
                UnquantizedDoubleValue = 123456789.123456789 + baseValue,

                EnumUntyped = EnumUntyped.Value0,
                EnumS08 = EnumS8.Value0,
                EnumU08 = EnumU8.Value0,
                EnumS16 = EnumS16.Value0,
                EnumU16 = EnumU16.Value0,
                EnumS32 = EnumS32.Value0,
                EnumU32 = EnumU32.Value0,
                EnumS64 = EnumS64.Value0,
                EnumU64 = EnumU64.Value0,

                Float2Value = new float2(baseValue + 4, baseValue + 5),
                UnquantizedFloat2Value = new float2(baseValue + 6, baseValue + 7),
                Float3Value = new float3(baseValue + 8, baseValue + 9, baseValue + 10),
                UnquantizedFloat3Value = new float3(baseValue + 11, baseValue + 12, baseValue + 13),
                Float4Value = new float4(baseValue + 14, baseValue + 15, baseValue + 16, baseValue + 17),
                UnquantizedFloat4Value = new float4(baseValue + 18, baseValue + 19, baseValue + 20, baseValue + 21),
                QuaternionValue = math.normalize(new quaternion(baseValue + 22, baseValue + 23, baseValue + 24, baseValue + 25)),
                UnquantizedQuaternionValue = math.normalize(new quaternion(baseValue + 26, baseValue + 27, baseValue + 28, baseValue + 29)),

                StringValue32 = new FixedString32Bytes($"baseValue = {baseValue}"),
                StringValue64 = new FixedString64Bytes($"baseValue = {baseValue*2}"),
                StringValue128 = new FixedString128Bytes($"baseValue = {baseValue*3}"),
                StringValue512 = new FixedString512Bytes($"baseValue = {baseValue*4}"),
                StringValue4096 = new FixedString4096Bytes($"baseValue = {baseValue*5}"),
                InvalidTickValue = NetworkTick.Invalid,
                TickValue = new NetworkTick((uint) baseValue),
                EntityValue = serverEntity
            };
        }

        void SetLargeGhostValues(NetCodeTestWorld testWorld, string baseValue, int size)
        {
            FixedString4096Bytes largeString = "";
            for (int i = 0; i <size; ++i)
            {
                largeString += baseValue;
            }

            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostValueSerializer
            {
                StringValue4096 = largeString,
                EntityValue = serverEntity
            });
        }

        [Test]
        public void ChangeMaskUtilitiesWorks()
        {
            //256 bit mask, the extra bits are for checking any overflow
            NativeArray<uint> mask = new NativeArray<uint>(9, Allocator.Temp);
            IntPtr maskPtr;
            unsafe { maskPtr = (IntPtr)mask.GetUnsafePtr(); }

            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.ResetChangeMask(maskPtr, 10, -1);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyFromChangeMask(maskPtr, -1, 0);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyFromChangeMask(maskPtr, 0, -1);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyToChangeMask(maskPtr, 10, -1, 0);});
            Assert.Catch<UnityEngine.Assertions.AssertionException>(() => { GhostComponentSerializer.CopyToChangeMask(maskPtr, 10, 0, -1);});
            //This will cross the 32 bits boundary and set mulitple bits at the same time
            //There are some annoyince with these methods and in particular the fact the src must have exaclty the
            //required bits set, otherwise the mask is clubbered.
            //That is working fine at the moment given the current use case of them but we would probably make them more
            //robust (at some slighly more cpu cost) if necessary.
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x1, 10, 1);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x7, 14, 3);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x1ff, 20, 9);
            //Expecting to see 0b0001_1111_1111_0001_1100_0100_0000_0000
            var maskValue = GhostComponentSerializer.CopyFromChangeMask(maskPtr, 0, 31);
            Assert.AreEqual(0b0001_1111_1111_0001_1100_0100_0000_0000, maskValue);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 1023, 60, 10);
            maskValue = GhostComponentSerializer.CopyFromChangeMask(maskPtr, 60, 10);
            Assert.AreEqual(1023, maskValue);
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0x1, 255, 1);
            //Should not overflow
            Assert.AreEqual(0, mask[8]);
            //fill with all ones
            for (int i = 0; i < 8; ++i)
                mask[i] = ~0u;
            GhostComponentSerializer.CopyToChangeMask(maskPtr, 0, 60, 9);
            Assert.AreEqual((1u<<(60-32)) -1, mask[1]);
            Assert.AreEqual(~((1u<<5) -1), mask[2]);
            mask[1] = ~0u;
            mask[2] = ~0u;
            GhostComponentSerializer.ResetChangeMask(maskPtr, 60, 9);
            Assert.AreEqual((1u<<(60-32)) -1, mask[1]);
            Assert.AreEqual(~((1u<<5) -1), mask[2]);
            mask[1] = ~0u;
            mask[2] = ~0u;
            GhostComponentSerializer.ResetChangeMask(maskPtr, 10, 73);
            //verify the mask content. we should have 73 zeros
            Assert.AreEqual((1<<10) -1, mask[0]);
            Assert.AreEqual(0, mask[1]);
            Assert.AreEqual((~((1u << 19)-1)), mask[2]);
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
                SetGhostValuesOnServer(testWorld, 42);
                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickUntilClientsHaveAllGhosts();

                VerifyGhostValues(testWorld);
                SetGhostValuesOnServer(testWorld, 43);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

                // Assert that replicated version is correct
                VerifyGhostValues(testWorld);
            }
        }
        [Test]
        public void GhostValuesAreSerialized_RespectsMaxSendRate([Values(1, 20, 100)]byte sendRate)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject($"Ghost_MaxSendRate_{sendRate}");
            var config = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            config.MaxSendRate = sendRate;
            // Use predicted to always get latest values:
            config.SupportedGhostModes = GhostModeMask.Predicted;
            config.DefaultGhostMode = GhostMode.Predicted;
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.SpawnOnServer(ghostGameObject);
            SetGhostValuesOnServer(testWorld, 0);
            testWorld.Connect();
            testWorld.GoInGame();
            testWorld.TickUntilClientsHaveAllGhosts();
            var firstSpawn = NetCodeTestWorld.TickIndex;

            // Replicate changes over N frames.
            var serverValues = new NativeList<(int tick, GhostValueSerializer val)>(64, Allocator.Temp);
            var clientValues = new NativeList<(int tick, GhostValueSerializer val)>(64, Allocator.Temp);
            Assert.IsTrue(AddIfChanged(serverValues, 0, testWorld.ServerWorld));
            Assert.IsTrue(AddIfChanged(clientValues, 0, testWorld.ClientWorlds[0]));
            const int numTicks = 25;
            for (int i = 1; i < numTicks; ++i)
            {
                SetGhostValuesOnServer(testWorld, i);
                testWorld.Tick();
                AddIfChanged(serverValues, i, testWorld.ServerWorld);
                AddIfChanged(clientValues, i, testWorld.ClientWorlds[0]);
            }
            Debug.Log($"firstSpawn:{firstSpawn} ticks, serverValues.Length:{serverValues.Length} vs clientValues.Length:{clientValues.Length}");
            Assert.That(serverValues.Length, Is.EqualTo(numTicks), "Sanity!");
            var numClientValues = clientValues.Length;
            switch (sendRate)
            {
                case 1:
                    Assert.That(numClientValues, Is.EqualTo(1));
                    break;
                case 20:
                    Assert.That(numClientValues, Is.EqualTo(9));
                    break;
                case 100:
                    Assert.That(numClientValues, Is.EqualTo(numTicks));
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(sendRate), sendRate, null);
            }

            // Verify each entry:
            for (int i = 0; i < clientValues.Length; i++)
            {
                var (tick, val) = clientValues[i];
                VerifyGhostValues(serverValues[tick].val, val);
            }

            unsafe bool AddIfChanged(NativeList<(int tick, GhostValueSerializer val)> list, int tick, World world)
            {
                var previous = list.IsEmpty ? default : list[list.Length - 1];
                var current = testWorld.GetSingleton<GhostValueSerializer>(world);
                var memCmp = UnsafeUtility.MemCmp(&current, &previous.val, UnsafeUtility.SizeOf<GhostValueSerializer>());
                //UnityEngine.Debug.Log($"  - TestWorld[{NetCodeTestWorld.TickIndex}]  iteration:{tick} = previous:{previous.val.IntValue}, current:{current.IntValue} = memCmp:{memCmp} ");
                if (list.IsEmpty || memCmp != 0)
                {
                    list.Add((tick, current));
                    return true;
                }
                return false;
            }
        }
        [Test]
        public void GhostValuesAreSerialized_WithPacketDumpsEnabled()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DebugPackets = true;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.SpawnOnServer(ghostGameObject);
                SetGhostValuesOnServer(testWorld, 42);
                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickUntilClientsHaveAllGhosts();
                VerifyGhostValues(testWorld);
                SetGhostValuesOnServer(testWorld, 43);

                for (int i = 0; i < 8; ++i)
                    testWorld.Tick();

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
                var ghostConfig = referencedGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, referencedGameObject));

                testWorld.CreateWorlds(true, 1);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();
                for (int i = 0; i < 4; ++i)
                {
                    testWorld.Tick();
                }

                var serverRefEntity = testWorld.SpawnOnServer(referencedGameObject);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostValueSerializer{EntityValue = serverRefEntity});

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // Make sure the reference always exist if the ghost exists
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                // Verify that we did get the referenced entity at some point
                Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));
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
                var ghostConfig = referencedGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject, referencedGameObject));

                testWorld.CreateWorlds(true, 1);
                ref var ghostRelevancy = ref testWorld.GetSingletonRW<GhostRelevancy>(testWorld.ServerWorld).ValueRW;

                // Connect and make sure the connection could be established
                testWorld.Connect();
                ghostRelevancy.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;

                // Go in-game
                testWorld.GoInGame();
                for (int i = 0; i < 4; ++i)
                {
                    testWorld.Tick();
                }

                var con = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                Assert.AreNotEqual(Entity.Null, con);
                var serverConnectionId = testWorld.ServerWorld.EntityManager.GetComponentData<NetworkId>(con).Value;

                var serverRefEntity = testWorld.SpawnOnServer(referencedGameObject);
                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostValueSerializer{EntityValue = serverRefEntity});

                testWorld.Tick();

                var serverGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEnt).ghostId;
                var serverRefGhostId = testWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverRefEntity).ghostId;

                // only mark the entity with the ref as relevant so that arrived before the referenced entity exists
                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverGhostId), 1);

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // Make sure the reference always exist if the ghost exists
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                // Verify that we did not the referenced entity since it is irrelevant
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));

                ghostRelevancy.GhostRelevancySet.TryAdd(new RelevantGhostForConnection(serverConnectionId, serverRefGhostId), 1);
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                    var clientEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
                    if (clientEntity != Entity.Null)
                    {
                        // Make sure the reference always exist if the ghost exists
                        Assert.AreEqual(clientRefEntity, testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostValueSerializer>(clientEntity).EntityValue);
                    }
                }
                Assert.AreNotEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));

                // Delete the referenced entity and make sure the ref is updated
                testWorld.ServerWorld.EntityManager.DestroyEntity(serverRefEntity);
                int mismatchFrames = 0;
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.Tick();
                    var clientRefEntity = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
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
                Assert.AreEqual(Entity.Null, testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]));
            }
        }
        [Test]
        public void ManyEntitiesCanBeDespawnedSameTick()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
                using (var entities = testWorld.ServerWorld.EntityManager.Instantiate(prefab, 10000, Allocator.Persistent))
                {
                    // Connect and make sure the connection could be established
                    testWorld.Connect();

                    // Go in-game
                    testWorld.GoInGame();

                    // Let the game run for a bit so the ghosts are spawned on the client
                    for (int i = 0; i < 128; ++i)
                        testWorld.Tick();


                    var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                    Assert.AreEqual(10000, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(10000, ghostCount.GhostCountReceivedOnClient);

                    testWorld.ServerWorld.EntityManager.DestroyEntity(entities);

                    for (int i = 0; i < 256; ++i)
                        testWorld.Tick();

                    // Assert that replicated version is correct
                    Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);
                }
            }
        }
        [Test]
        public void SnapshotAckMaskIsReportedCorrectlyByTheClient()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                var ghost = new GameObject("Ghost");
                ghost.AddComponent<GhostAuthoringComponent>();
                testWorld.CreateGhostCollection(new[] {ghost});
                testWorld.CreateWorlds(true, 1);

                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.SpawnOnServer(0);
                var lastReceivedFromClient = default(NetworkTick);
                uint currentMask = 0x1;
                for (int i = 0; i < 64; ++i)
                {
                    //Need to do this after two ticks because the server is receiving data for the last client tick.
                    //first tick  (5) it sent the first snapshot (client receive and ack, but still not send it back)
                    //second tick (6) client update network time (current tick:9). Receive tick 6 (ack), send command for tick 9
                    //third tick  (7) server start seeing command from the client
                    if (i > 2)
                    {
                        var currentServerTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                        var lastTickClientAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
                        var serverAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                        Assert.GreaterOrEqual(serverAck.LastReceivedSnapshotByLocal.TickIndexForValidTick, currentServerTick.TickIndexForValidTick + 2);
                        //if the client has received some data from the server
                        if (lastTickClientAck.LastReceivedSnapshotByLocal.IsValid)
                        {
                            //and the server has received some new data from the client
                            if (!lastReceivedFromClient.IsValid || !lastReceivedFromClient.IsNewerThan(serverAck.LastReceivedSnapshotByLocal))
                                currentServerTick.Decrement();
                            var tickSince = currentServerTick.TicksSince(serverAck.LastReceivedSnapshotByRemote);
                            for (int tick = 0; tick < tickSince; ++tick)
                            {
                                currentServerTick.Decrement();
                                Assert.AreEqual(currentServerTick.TickIndexForValidTick, serverAck.LastReceivedSnapshotByRemote.TickIndexForValidTick);
                                serverAck.IsReceivedByRemote(currentServerTick); // TODO - This is missing an assert?
                            }
                        }
                        lastReceivedFromClient = serverAck.LastReceivedSnapshotByLocal;
                    }
                    testWorld.Tick();
                    {
                        //There is one frame delay first the first received tick before we communicate the ack.
                        //So when i==0 the expectation is that the client actually ack the received messages but the server didn't received them yet,
                        //nor the client
                        var currentServerTick = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                        var clientAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
                        if (i == 0)
                        {
                            Assert.AreEqual(currentServerTick, clientAck.LastReceivedSnapshotByLocal);
                            Assert.AreEqual(1, clientAck.ReceivedSnapshotByLocalMask);
                            currentMask = 1;
                        }
                        else
                        {
                            currentMask <<= 1;
                            currentMask |= 0x1;
                            Assert.AreEqual(currentServerTick, clientAck.LastReceivedSnapshotByLocal);
                            Assert.AreEqual(currentMask, clientAck.ReceivedSnapshotByLocalMask);
                        }
                    }
                }
                //If packet are lost (either because of a reorder or if a real packet loss) there should be holes
                //and the holes should match the expected bits.
                //How to test this?

                //If I receive multiple valid packet in the same frame (increasing ids), there is still an hole, because
                //only the last one is processed
                //Current mask is 1111 1111 1111 1111 1111 1111 1111 1111  1111 1111 1111 1111  1111 1111 1111 1111
                testWorld.TickServerWorld();
                testWorld.TickServerWorld();
                testWorld.TickClientWorld();
                //client should now clobber the last snapshot and report the ack only for the last one.
                //the mask will looks like:  1111 1111 1111 1111 1111 1111 1111 1111  1111 1111 1111 1111  1111 1111 1111 1101
                currentMask <<= 2;
                currentMask |= 0x1;
                var mask = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).ReceivedSnapshotByLocalMask;
                Assert.AreEqual(currentMask,mask);
                testWorld.TickServerWorld();
                testWorld.ServerWorld.EntityManager.CompleteDependencyBeforeRO<NetworkSnapshotAck>();
                var ack = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                var cur = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                cur.Subtract(1);
                Assert.IsTrue(ack.IsReceivedByRemote(cur));
                cur.Subtract(1);
                Assert.IsFalse(ack.IsReceivedByRemote(cur));
                cur.Subtract(1);
                Assert.IsTrue(ack.IsReceivedByRemote(cur));
                cur.Subtract(1);
                Assert.IsTrue(ack.IsReceivedByRemote(cur));
                //Verify that the oldest packet are still considered acked
                for (int i = 4; i < 66; ++i)
                {
                    cur.Subtract(1);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                }
                //And that even older are all 0s
                for (int i = 66; i < 256; ++i)
                {
                    cur.Subtract(1);
                    Assert.IsFalse(ack.IsReceivedByRemote(cur));
                }
                testWorld.TickClientWorld();
                mask = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]).ReceivedSnapshotByLocalMask;
                currentMask <<= 1;
                currentMask |= 0x1;
                Assert.AreEqual(currentMask,mask);
                cur = testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick;
                for (int i = 4; i < 256; ++i)
                {
                    testWorld.Tick();
                    //verify the oldest packets
                    ack = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                    cur.Subtract(1);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                    cur.Subtract(1);
                    Assert.IsFalse(ack.IsReceivedByRemote(cur));
                    cur.Subtract(1);
                    Assert.IsTrue(ack.IsReceivedByRemote(cur));
                    cur.Add(3);
                }
            }
        }
        public void GhostValuesAreSerializedWhenLargerThanMaxMessageSize()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverMaxMessageSize = 548;
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(ghostGameObject);
                SetLargeGhostValues(testWorld, "a", testWorld.DriverMaxMessageSize * 2);

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                VerifyGhostValues(testWorld);
                SetLargeGhostValues(testWorld, "b", testWorld.DriverMaxMessageSize * 2);

                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                // Assert that replicated version is correct
                VerifyGhostValues(testWorld);
            }
        }
    }
}
