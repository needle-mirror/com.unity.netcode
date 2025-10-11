#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode.Tests
{
    internal class GhostValueSerializerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostValueSerializer {});
            baker.AddBuffer<GhostValueBufferSerializer>(entity);
        }
    }

    internal enum EnumUntyped
    {
        Value0 = 255,
    }
    internal enum EnumS8 : sbyte
    {
        Value0 = 126,
    }
    internal enum EnumU8 : byte
    {
        Value0 = 253,
    }
    internal enum EnumS16 : short
    {
        Value0 = 0x7AAB
    }
    internal enum EnumU16 : ushort
    {
        Value0 = 0xF00D,
    }
    internal enum EnumS32
    {
        Value0 = 0x007AD0BE,
    }
    internal enum EnumU32 : uint
    {
        Value0 = 0xBAADF00D
    }
    internal enum EnumS64 : long
    {
        Value0 = 0x791BBCDC0CCAEDD1,
    }
    internal enum EnumU64 : ulong
    {
        Value0 = 0xABBA1970F1809FE2,
    }

    internal struct GhostValueBufferSerializer : IBufferElementData
    {
        [GhostField] public GhostValueSerializer Values;
        public override string ToString() => $"BUF[{Values}]";
    }

    internal struct GhostValueSerializer : IComponentData
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

        public override string ToString()
        {
            return $"{nameof(BoolValue)}: {BoolValue}, {nameof(IntValue)}: {IntValue}, {nameof(UIntValue)}: {UIntValue}, {nameof(LongValue)}: {LongValue}, {nameof(ULongValue)}: {ULongValue}, {nameof(EnumUntyped)}: {EnumUntyped}, {nameof(EnumS08)}: {EnumS08}, {nameof(EnumU08)}: {EnumU08}, {nameof(EnumS16)}: {EnumS16}, {nameof(EnumU16)}: {EnumU16}, {nameof(EnumS32)}: {EnumS32}, {nameof(EnumU32)}: {EnumU32}, {nameof(EnumS64)}: {EnumS64},\n{nameof(EnumU64)}: {EnumU64}, {nameof(FloatValue)}: {FloatValue}, {nameof(UnquantizedFloatValue)}: {UnquantizedFloatValue}, {nameof(DoubleValue)}: {DoubleValue}, {nameof(UnquantizedDoubleValue)}: {UnquantizedDoubleValue}, {nameof(Float2Value)}: {Float2Value}, {nameof(UnquantizedFloat2Value)}: {UnquantizedFloat2Value}, {nameof(Float3Value)}: {Float3Value}, {nameof(UnquantizedFloat3Value)}: {UnquantizedFloat3Value}, {nameof(Float4Value)}: {Float4Value},\n{nameof(UnquantizedFloat4Value)}: {UnquantizedFloat4Value}, {nameof(QuaternionValue)}: {QuaternionValue}, {nameof(UnquantizedQuaternionValue)}: {UnquantizedQuaternionValue}, {nameof(StringValue32)}: L{StringValue32.Length}, {nameof(StringValue64)}: L{StringValue64.Length}, {nameof(StringValue128)}: L{StringValue128.Length}, {nameof(StringValue512)}: L{StringValue512.Length}, {nameof(StringValue4096)}: L{StringValue4096.Length},\n {nameof(InvalidTickValue)}: {InvalidTickValue.SerializedData}, {nameof(TickValue)}: {TickValue.SerializedData}, {nameof(EntityValue)}: {EntityValue}";
        }

        [GhostField(Composite = true)] public Union UnionValue;
        [StructLayout(LayoutKind.Explicit)]
        internal struct Union
        {
            [FieldOffset(0)] [GhostField(SendData = false)] public StructA State1;
            [FieldOffset(0)] [GhostField(Quantization = 0, Smoothing = SmoothingAction.Clamp, Composite = true)] public StructB State2;
            [FieldOffset(0)] [GhostField(SendData = false)] public StructC State3;
            internal struct StructA
            {
                public int A, B;
                public float C;
            }
            internal struct StructB
            {
                public ulong A, B, C, D;
            }
            internal struct StructC
            {
                public double A, B;
            }
            public static void Assertions()
            {
                UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructA>());
                UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructC>());
            }
        }
    }

    internal class GhostSerializationTests
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

            var serverBufferValues = testWorld.ServerWorld.EntityManager.GetBuffer<GhostValueBufferSerializer>(serverEntity);
            var clientBufferValues = testWorld.ClientWorlds[0].EntityManager.GetBuffer<GhostValueBufferSerializer>(clientEntity);
            Assert.AreEqual(serverBufferValues.Length, clientBufferValues.Length);

            for (int i = 0; i < serverBufferValues.Length; i++)
            {
                VerifyGhostValues(serverBufferValues[i].Values, clientBufferValues[i].Values);
            }
        }

        static void VerifyGhostValues(GhostValueSerializer serverValues, GhostValueSerializer clientValues)
        {
            //Debug.Log($"VerifyGhostValues | ServerValues:{serverValues.ToString()}\nClientValues:{clientValues.ToString()}");
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
            Assert.AreEqual(serverValues.InvalidTickValue, clientValues.InvalidTickValue, $"{serverValues.InvalidTickValue.SerializedData} vs {clientValues.InvalidTickValue.SerializedData}");
            Assert.AreEqual(serverValues.TickValue, clientValues.TickValue);

            GhostValueSerializer.Union.Assertions();
            Assert.AreEqual(serverValues.UnionValue.State1.A,clientValues.UnionValue.State1.A);
            Assert.AreEqual(serverValues.UnionValue.State1.B,clientValues.UnionValue.State1.B);
            Assert.AreEqual(serverValues.UnionValue.State1.C,clientValues.UnionValue.State1.C);
            Assert.AreEqual(serverValues.UnionValue.State2.A,clientValues.UnionValue.State2.A);
            Assert.AreEqual(serverValues.UnionValue.State2.B,clientValues.UnionValue.State2.B);
            Assert.AreEqual(serverValues.UnionValue.State2.C,clientValues.UnionValue.State2.C);
            Assert.AreEqual(serverValues.UnionValue.State2.D,clientValues.UnionValue.State2.D);
            Assert.AreEqual(serverValues.UnionValue.State3.A,clientValues.UnionValue.State3.A);
            Assert.AreEqual(serverValues.UnionValue.State3.B,clientValues.UnionValue.State3.B);
        }
        void SetGhostValuesOnServer(NetCodeTestWorld testWorld, int baseValue, int length = 2)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, CreateGhostValues(baseValue, serverEntity));
            var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<GhostValueBufferSerializer>(serverEntity);
            buffer.Length = length;
            for (int i = 0; i < length; i++)
            {
                buffer.ElementAt(i) = new GhostValueBufferSerializer {Values = CreateGhostValues(baseValue + i, serverEntity),};
            }
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
                EntityValue = serverEntity,

                UnionValue = new GhostValueSerializer.Union
                {
                    // Don't write union State1 or State2.
                    State3 =
                    {
                        A = baseValue * 11.5,
                        B = baseValue * 12.5,
                    },
                },
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
        [Category(NetcodeTestCategories.Foundational)]
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
        [Category(NetcodeTestCategories.Foundational)]
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

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                // Assert that replicated version is correct
                VerifyGhostValues(testWorld);
            }
        }

        internal enum SetMode
        {
            ConstantChanges,
            OnlyOneChange,
        }

#if !NETCODE_SNAPSHOT_HISTORY_SIZE_6
        // TODO: Really we should add test coverage to ensure we're actually hitting the MaxSendRate condition of `GhostSendSystem.GatherGhostChunks`,
        // but that requires better analytics.
        [Test]
        public void GhostValuesAreSerialized_RespectsMaxSendRate([Values]SetMode setMode, [Values]GhostOptimizationMode optMode,
            [Values(1, 20, 100, 0)]int maxSendRate)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.SetTestLatencyProfile(NetCodeTestLatencyProfile.RTT60ms);
            const int snapshotAckLatencyInTicks = 2;

            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject($"Ghost_MaxSendRate_{maxSendRate}");
            var config = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            config.MaxSendRate = (byte)maxSendRate;
            // Use predicted to always get latest values:
            config.SupportedGhostModes = GhostModeMask.Predicted;
            config.OptimizationMode = optMode;
            config.HasOwner = true;
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            var serverGhost = testWorld.SpawnOnServer(ghostGameObject);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, new GhostOwner { NetworkId = 1,});
            SetGhostValuesOnServer(testWorld, 0);
            testWorld.Connect(maxSteps:16);
            testWorld.GoInGame();
            testWorld.TickUntilClientsHaveAllGhosts();
            var firstSpawn = NetCodeTestWorld.TickIndex;

            // Replicate changes over N frames.
            var serverValues = new NativeList<(int tick, GhostValueSerializer val)>(64, Allocator.Temp);
            var clientValues = new NativeList<(int tick, GhostValueSerializer val)>(64, Allocator.Temp);
            var clientEnt = testWorld.TryGetSingletonEntity<GhostValueSerializer>(testWorld.ClientWorlds[0]);
            NetworkTick lastSnapshotTick = NetworkTick.Invalid;
            int numSnapshotsArrivedForGhost = 0;
            const int numTicksInTest = 25;
            for (int tick = 0; tick < numTicksInTest; ++tick)
            {
                if(setMode == SetMode.ConstantChanges || tick == 0) // Make 1 change in the OneChange case.
                    SetGhostValuesOnServer(testWorld, tick);
                testWorld.Tick();
                AddIfChanged(serverValues, tick, testWorld.ServerWorld);
                AddIfChanged(clientValues, tick - snapshotAckLatencyInTicks, testWorld.ClientWorlds[0]);

                var clientSnapshotBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<SnapshotDataBuffer>(clientEnt);
                var clientSnapshot = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SnapshotData>(clientEnt);
                var snapshotTick = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                if (snapshotTick != lastSnapshotTick)
                {
                    lastSnapshotTick = snapshotTick;
                    numSnapshotsArrivedForGhost++;
                }
            }
            Debug.Log($"firstSpawn:{firstSpawn} ticks, serverValues.Length:{serverValues.Length} vs clientValues.Length:{clientValues.Length}, numSnapshotsArrivedForGhost:{numSnapshotsArrivedForGhost}!");
            if(setMode == SetMode.ConstantChanges)
                Assert.That(serverValues.Length, Is.EqualTo(numTicksInTest), "Sanity!");
            else Assert.That(serverValues.Length, Is.GreaterThan(0), "Sanity!");

            var expectedNumChanges = maxSendRate switch
            {
                20 => 9,
                1 => 1,
                0 or 100 => numTicksInTest,
                _ => throw new ArgumentOutOfRangeException(nameof(maxSendRate), maxSendRate, null),
            };

            // The number of snapshots the receives (for this ghost) is slightly variable, as static optimization takes
            // a couple ticks to ack, so it'll try to resend (which itself is rate-limited by MaxSendRate).
            var (expectedMinSnapshots, expectedMaxSnapshots) = setMode == SetMode.ConstantChanges || optMode == GhostOptimizationMode.Dynamic
                ? (expectedNumChanges, expectedNumChanges)
                : (1, 3); // It can be as high as 3 here as the server is STILL waiting for the ack of the SPAWN of the ghost.
            Assert.That(numSnapshotsArrivedForGhost, Is.InRange(expectedMinSnapshots, expectedMaxSnapshots), nameof(numSnapshotsArrivedForGhost));

            var (expectedMinNumDistinct, expectedMaxNumDistinct) = (setMode, optMode, sendRate: maxSendRate) switch
            {
                (_, _, 1) or (SetMode.OnlyOneChange, _, _) => (1, 1),
                (SetMode.ConstantChanges, _, _) => (expectedNumChanges - snapshotAckLatencyInTicks, expectedNumChanges),
                _ => throw new ArgumentOutOfRangeException(),
            };
            var numClientValues = clientValues.Length;
            Assert.That(numClientValues, Is.InRange(expectedMinNumDistinct, expectedMaxNumDistinct));

            // Verify each entry:
            for (int i = 0; i < clientValues.Length; i++)
            {
                var (tick, val) = clientValues[i];
                if(tick >= 0 && tick < serverValues.Length)
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
#endif

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
        public void ManyEntitiesCanBeDespawnedSameTick([Values(NetCodeTestLatencyProfile.PL33, NetCodeTestLatencyProfile.RTT16ms_PL5)]NetCodeTestLatencyProfile profile)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.SetTestLatencyProfile(profile);
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var prefabCollection = testWorld.TryGetSingletonEntity<NetCodeTestPrefabCollection>(testWorld.ServerWorld);
                var prefab = testWorld.ServerWorld.EntityManager.GetBuffer<NetCodeTestPrefab>(prefabCollection)[0].Value;
                using (var entities = testWorld.ServerWorld.EntityManager.Instantiate(prefab, 10000, Allocator.Persistent))
                {
                    testWorld.Connect(maxSteps:32);
                    testWorld.GoInGame();

                    // Let the game run for a bit so the ghosts are spawned on the client:
                    for (int i = 0; i < 200; ++i)
                        testWorld.Tick();

                    var ghostCount = testWorld.GetSingleton<GhostCount>(testWorld.ClientWorlds[0]);
                    Assert.AreEqual(10000, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(10000, ghostCount.GhostCountReceivedOnClient);

                    testWorld.ServerWorld.EntityManager.DestroyEntity(entities);

                    for (int i = 0; i < 12; ++i)
                        testWorld.Tick();

                    // Assert that replicated version is correct
                    Assert.AreEqual(0, ghostCount.GhostCountInstantiatedOnClient);
                    Assert.AreEqual(0, ghostCount.GhostCountReceivedOnClient);
                }
            }
        }
        [Test]
        [Category(NetcodeTestCategories.Foundational)]
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
                        Assert.That(serverAck.LastReceivedSnapshotByLocal.TickIndexForValidTick, Is.GreaterThanOrEqualTo(currentServerTick.TickIndexForValidTick + 2));
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
                                Assert.IsTrue(serverAck.IsReceivedByRemote(currentServerTick));
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
        [Test]
        public void GhostValuesAreSerializedWhenLargerThanMaxMessageSize()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.LogLevel = NetDebug.LogLevelType.Debug; // PERFORMANCE warnings need this.
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

#if NETCODE_DEBUG
                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
#endif
            }
        }

        [Test]
        public void TooSmall_SnapshotPacketSize_FailsGracefully_ViaMaxSnapshotSendAttempts([Values]bool useNetworkStreamSnapshotTargetSize)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.LogLevel = NetDebug.LogLevelType.Debug; // PERFORMANCE warnings need this.
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            const int maxMessageSize = GhostSystemConstants.MinSnapshotPacketSize;
            testWorld.SpawnOnServer(ghostGameObject);
            var maxTheoreticalSizeGhostSendSystemCanSend = (int)(maxMessageSize * math.pow(2, GhostSystemConstants.MaxSnapshotSendAttempts-1)); // Ignoring headers etc.
            SetGhostValuesOnServer(testWorld, 43, (int) (maxTheoreticalSizeGhostSendSystemCanSend * 0.01f)); // It's a huge struct inside the buffer.

            testWorld.Connect();
            testWorld.GoInGame();

            // Configure Snapshot Packet Size limit:
            ref var ghostSendSystemData = ref testWorld.GetSingletonRW<GhostSendSystemData>(testWorld.ServerWorld).ValueRW;
            ghostSendSystemData.TempStreamInitialSize *= 32; // Prevents the other overflow error regarding temp stream size!
            if (useNetworkStreamSnapshotTargetSize)
            {
                var ent = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.AddComponentData(ent, new NetworkStreamSnapshotTargetSize
                {
                    Value = maxMessageSize,
                });
            }
            else
            {
                ghostSendSystemData.DefaultSnapshotPacketSize = maxMessageSize;
            }

            testWorld.Tick();
            testWorld.Tick();
            testWorld.Tick();
#if NETCODE_DEBUG // These warnings only appear when NETCODE_DEBUG is defined, but the fatal error makes it through.
            for(int i = 0; i < GhostSystemConstants.MaxSnapshotSendAttempts - 1; i++)
                LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE(.*)NID\[1\](.*)fit even one ghost"));
#endif
            LogAssert.Expect(LogType.Error, new Regex(@$"FATAL(.*){nameof(GhostSystemConstants.MaxSnapshotSendAttempts)}(.*)NID\[1\]"));
        }

#if NETCODE_SNAPSHOT_HISTORY_SIZE_6
        [Test(Description = "When the snapshot history is small, users can fill up the snapshot history buffer with in-flight snapshot packets. This test ensures we gracefully process this case.")]
        public void SnapshotHistorySize6_TriggersHistoryBufferSaturation_Gracefully()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 100; // Causes snapshots to remain 'in-flight' for more ticks.
            testWorld.LogLevel = NetDebug.LogLevelType.Debug; // PERFORMANCE warnings need this.
            testWorld.Bootstrap(true);
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostValueSerializerConverter();
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.SpawnOnServer(ghostGameObject);
            testWorld.Connect(maxSteps:32);
            testWorld.GoInGame();

            for(int i = 0; i < 24; i++)
                testWorld.Tick();
    #if NETCODE_DEBUG
            LogAssert.Expect(LogType.Warning, new Regex(@"PERFORMANCE\: Snapshot history is saturated for ghost chunk:(\d*), ghostType\:0, 4\/6 in\-flight \(TSLR\:15\<\=16\), sent anyway\:(true|false)\!"));
    #endif
        }
#endif
    }
}
