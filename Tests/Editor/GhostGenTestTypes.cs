using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    public struct int3
    {
        public int x;
        public int y;
        public int z;
    }

    public struct uint3
    {
        public uint x;
        public uint y;
        public uint z;
    }
    public struct partialUint3
    {
        public uint x;
        public uint y;
        [GhostField(SendData = false)] public uint z;
    }

    public struct floatX
    {
        public float2 x;
        public float3 y;
        public float4 z;
    }
    public struct GhostGenTestTypeFlat : IComponentData
    {
        [GhostField(Composite=true)] public int3 Composed_Int3;
        [GhostField] public int3 Int3;

        [GhostField(Composite=true)] public uint3 Composed_UInt3;
        [GhostField] public uint3 UInt3;
        [GhostField(Composite=true)] public partialUint3 ComposedPartial_UInt3;
        [GhostField] public partialUint3 Partial_UInt3;

        [GhostField(Quantization=10, Composite=true)] public floatX Composed_FloatX;
        [GhostField(Quantization=10, Smoothing=SmoothingAction.Interpolate)] public floatX FloatX;
        [GhostField] public int IntValue;
        [GhostField] public uint UIntValue;
        [GhostField] public bool BoolValue;

        [GhostField] public float Unquantized_FloatValue;
        [GhostField(Smoothing=SmoothingAction.Interpolate)] public float Unquantized_Interpolated_FloatValue;
        [GhostField(Quantization=10)] public float FloatValue;
        [GhostField(Quantization=10, Smoothing=SmoothingAction.Interpolate)] public float Interpolated_FloatValue;

        [GhostField(Quantization=10)] public float2 Float2Value;
        [GhostField(Quantization=10, Smoothing=SmoothingAction.Interpolate)] public float2 Interpolated_Float2Value;
        [GhostField] public float2 Unquantized_Float2Value;
        [GhostField(Smoothing=SmoothingAction.Interpolate)] public float2 Interpolated_Unquantized_Float2Value;

        [GhostField(Quantization=10)] public float3 Float3Value;
        [GhostField(Quantization=10, Smoothing=SmoothingAction.Interpolate)] public float3 Interpolated_Float3Value;
        [GhostField] public float3 Unquantized_Float3Value;
        [GhostField(Smoothing=SmoothingAction.Interpolate)] public float3 Interpolated_Unquantized_Float3Value;

        [GhostField(Quantization=10)] public float4 Float4Value;
        [GhostField(Quantization=10, Smoothing=SmoothingAction.Interpolate)] public float4 Interpolated_Float4Value;
        [GhostField] public float4 Unquantized_Float4Value;
        [GhostField(Smoothing=SmoothingAction.Interpolate)] public float4 Interpolated_Unquantized_Float4Value;

        [GhostField(Quantization=1000)] public quaternion QuaternionValue;
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.Interpolate)] public quaternion Interpolated_QuaternionValue;
        [GhostField] public quaternion Unquantized_QuaternionValue;
        [GhostField(Smoothing=SmoothingAction.Interpolate)] public quaternion Interpolated_Unquantized_QuaternionValue;

        [GhostField] public FixedString32 String32Value;
        [GhostField] public FixedString64 String64Value;
        [GhostField] public FixedString128 String128Value;
        [GhostField] public FixedString512 String512Value;
        [GhostField] public FixedString4096 String4096Value;
        [GhostField] public Entity EntityValue;
    }

    public class GhostGenTestTypesConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new GhostGenTestTypeFlat {});
        }
    }

    public class GhostGenTestTypes
    {
        void VerifyGhostValues(NetCodeTestWorld testWorld)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostGenTestTypeFlat>(testWorld.ServerWorld);
            var clientEntity = testWorld.TryGetSingletonEntity<GhostGenTestTypeFlat>(testWorld.ClientWorlds[0]);

            Assert.AreNotEqual(Entity.Null, serverEntity);
            Assert.AreNotEqual(Entity.Null, clientEntity);

            var serverValues = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenTestTypeFlat>(serverEntity);
            var clientValues = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGenTestTypeFlat>(clientEntity);

            Assert.AreEqual(serverValues.Int3, clientValues.Int3);
            Assert.AreEqual(serverValues.Composed_Int3, clientValues.Composed_Int3);

            Assert.AreEqual(serverValues.UInt3, clientValues.UInt3);
            Assert.AreEqual(serverValues.Composed_UInt3, clientValues.Composed_UInt3);

            Assert.AreEqual(serverValues.Partial_UInt3.x, clientValues.Partial_UInt3.x);
            Assert.AreEqual(serverValues.Partial_UInt3.y, clientValues.Partial_UInt3.y);
            Assert.AreEqual(0, clientValues.Partial_UInt3.z);
            Assert.AreEqual(serverValues.ComposedPartial_UInt3.x, clientValues.ComposedPartial_UInt3.x);
            Assert.AreEqual(serverValues.ComposedPartial_UInt3.y, clientValues.ComposedPartial_UInt3.y);
            Assert.AreEqual(0, clientValues.ComposedPartial_UInt3.z);

            Assert.AreEqual(serverValues.FloatX, clientValues.FloatX);
            Assert.AreEqual(serverValues.Composed_FloatX, clientValues.Composed_FloatX);

            Assert.AreEqual(serverValues.IntValue, clientValues.IntValue);
            Assert.AreEqual(serverValues.UIntValue, clientValues.UIntValue);
            Assert.AreEqual(serverValues.BoolValue, clientValues.BoolValue);

            Assert.AreEqual(serverValues.FloatValue, clientValues.FloatValue);
            Assert.AreEqual(serverValues.Interpolated_FloatValue, clientValues.Interpolated_FloatValue);
            Assert.AreEqual(serverValues.Unquantized_FloatValue, clientValues.Unquantized_FloatValue);
            Assert.AreEqual(serverValues.Unquantized_Interpolated_FloatValue, clientValues.Unquantized_Interpolated_FloatValue);

            Assert.AreEqual(serverValues.Float2Value, clientValues.Float2Value);
            Assert.AreEqual(serverValues.Interpolated_Float2Value, clientValues.Interpolated_Float2Value);
            Assert.AreEqual(serverValues.Unquantized_Float2Value, clientValues.Unquantized_Float2Value);
            Assert.AreEqual(serverValues.Interpolated_Unquantized_Float2Value, clientValues.Interpolated_Unquantized_Float2Value);

            Assert.AreEqual(serverValues.Float3Value, clientValues.Float3Value);
            Assert.AreEqual(serverValues.Interpolated_Float3Value, clientValues.Interpolated_Float3Value);
            Assert.AreEqual(serverValues.Unquantized_Float3Value, clientValues.Unquantized_Float3Value);
            Assert.AreEqual(serverValues.Interpolated_Unquantized_Float3Value, clientValues.Interpolated_Unquantized_Float3Value);

            Assert.AreEqual(serverValues.Float4Value, clientValues.Float4Value);
            Assert.AreEqual(serverValues.Interpolated_Float4Value, clientValues.Interpolated_Float4Value);
            Assert.AreEqual(serverValues.Unquantized_Float4Value, clientValues.Unquantized_Float4Value);
            Assert.AreEqual(serverValues.Interpolated_Unquantized_Float4Value, clientValues.Interpolated_Unquantized_Float4Value);

            Assert.Less(math.distance(serverValues.QuaternionValue.value, clientValues.QuaternionValue.value), 0.001f);
            Assert.Less(math.distance(serverValues.Interpolated_QuaternionValue.value, clientValues.Interpolated_QuaternionValue.value), 0.001f);
            Assert.AreEqual(serverValues.Unquantized_QuaternionValue, clientValues.Unquantized_QuaternionValue);
            Assert.AreEqual(serverValues.Interpolated_Unquantized_QuaternionValue, clientValues.Interpolated_Unquantized_QuaternionValue);

            Assert.AreEqual(serverValues.String32Value,clientValues.String32Value);
            Assert.AreEqual(serverValues.String64Value,clientValues.String64Value);
            Assert.AreEqual(serverValues.String128Value,clientValues.String128Value);
            Assert.AreEqual(serverValues.String512Value,clientValues.String512Value);
            Assert.AreEqual(serverValues.String4096Value,clientValues.String4096Value);

            Assert.AreEqual(serverEntity, serverValues.EntityValue);
            Assert.AreEqual(clientEntity, clientValues.EntityValue);
        }
        void SetGhostValues(NetCodeTestWorld testWorld, int baseValue)
        {
            var serverEntity = testWorld.TryGetSingletonEntity<GhostGenTestTypeFlat>(testWorld.ServerWorld);
            Assert.AreNotEqual(Entity.Null, serverEntity);
            int i = 0;
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypeFlat
            {
                Int3 = new int3()
                {
                    x = baseValue,
                    y = baseValue + ++i,
                    z = baseValue + ++i
                },
                Composed_Int3 = new int3()
                {
                    x = baseValue + ++i,
                    y = baseValue + ++i,
                    z = baseValue + ++i,
                },
                UInt3 = new uint3()
                {
                    x = (uint)baseValue + (uint)++i,
                    y = (uint)baseValue + (uint)++i,
                    z = (uint)baseValue + (uint)++i
                },
                Composed_UInt3 = new uint3()
                {
                    x = (uint)baseValue + (uint)++i,
                    y = (uint)baseValue + (uint)++i,
                    z = (uint)baseValue + (uint)++i
                },
                Partial_UInt3 = new partialUint3()
                {
                    x = (uint)baseValue + (uint)++i,
                    y = (uint)baseValue + (uint)++i,
                    z = (uint)baseValue + (uint)++i
                },
                ComposedPartial_UInt3 = new partialUint3()
                {
                    x = (uint)baseValue + (uint)++i,
                    y = (uint)baseValue + (uint)++i,
                    z = (uint)baseValue + (uint)++i
                },
                FloatX = new floatX()
                {
                    x = new float2(baseValue + (uint)++i, baseValue + (uint)++i),
                    y = new float3(baseValue + (uint)++i, baseValue + (uint)++i, baseValue + (uint)++i),
                    z = new float4(baseValue + (uint)++i, baseValue + (uint)++i, baseValue + (uint)++i, baseValue + (uint)++i),
                },
                Composed_FloatX = new floatX()
                {
                    x = new float2(baseValue + (uint)++i, baseValue + (uint)++i),
                    y = new float3(baseValue + (uint)++i, baseValue + (uint)++i, baseValue + (uint)++i),
                    z = new float4(baseValue + (uint)++i, baseValue + (uint)++i, baseValue + (uint)++i, baseValue + (uint)++i),
                },
                IntValue = baseValue + ++i,
                UIntValue = (uint)baseValue + (uint)++i,
                BoolValue = (baseValue & ++i) != 0,

                FloatValue = baseValue + ++i,
                Interpolated_FloatValue = baseValue + ++i,
                Unquantized_FloatValue = baseValue + ++i,
                Unquantized_Interpolated_FloatValue = baseValue + ++i,

                Float2Value = new float2(baseValue + ++i, baseValue + ++i),
                Interpolated_Float2Value = new float2(baseValue + ++i, baseValue + ++i),
                Unquantized_Float2Value = new float2(baseValue + ++i, baseValue + ++i),
                Interpolated_Unquantized_Float2Value = new float2(baseValue + ++i, baseValue + ++i),

                Float3Value = new float3(baseValue + ++i, baseValue + ++i, baseValue + ++i),
                Interpolated_Float3Value = new float3(baseValue + ++i, baseValue + ++i, baseValue + ++i),
                Unquantized_Float3Value = new float3(baseValue + ++i, baseValue + ++i, baseValue + ++i),
                Interpolated_Unquantized_Float3Value = new float3(baseValue + ++i, baseValue + ++i, baseValue + ++i),

                Float4Value = new float4(baseValue + ++i, baseValue + ++i, baseValue + ++i, baseValue + ++i),
                Interpolated_Float4Value = new float4(baseValue + ++i, baseValue + ++i, baseValue + ++i, baseValue + ++i),
                Unquantized_Float4Value = new float4(baseValue + ++i, baseValue + ++i, baseValue + ++i, baseValue + ++i),
                Interpolated_Unquantized_Float4Value = new float4(baseValue + ++i, baseValue + ++i, baseValue + ++i, baseValue + ++i),

                QuaternionValue = math.normalize(new quaternion(0.4f, 0.4f, 0.4f, 0.6f)),
                Interpolated_QuaternionValue = math.normalize(new quaternion(0.5f, 0.5f, 0.5f, 0.5f)),
                Unquantized_QuaternionValue = math.normalize(new quaternion(0.6f, 0.6f, 0.6f, 0.6f)),
                Interpolated_Unquantized_QuaternionValue = math.normalize(new quaternion(0.5f, 0.5f, 0.5f, 0.5f)),

                String32Value = new FixedString32($"baseValue = {baseValue + ++i}"),
                String64Value = new FixedString64($"baseValue = {baseValue + ++i}"),
                String128Value = new FixedString128($"baseValue = {baseValue + ++i}"),
                String512Value = new FixedString512($"baseValue = {baseValue + ++i}"),
                String4096Value = new FixedString4096($"baseValue = {baseValue + ++i}"),

                EntityValue = serverEntity,
            });
            Debug.Log($"i is {i}");
        }
        [Test]
        public void GhostValuesAreSerialized()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGenTestTypesConverter();

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


        public struct GhostGenBigStruct : IComponentData
        {
            //Add 100 int fields and check they are serialized correctly
            [GhostField] public int field000;
            [GhostField] public int field001;
            [GhostField] public int field002;
            [GhostField] public int field003;
            [GhostField] public int field004;
            [GhostField] public int field005;
            [GhostField] public int field006;
            [GhostField] public int field007;
            [GhostField] public int field008;
            [GhostField] public int field009;
            [GhostField] public int field010;
            [GhostField] public int field011;
            [GhostField] public int field012;
            [GhostField] public int field013;
            [GhostField] public int field014;
            [GhostField] public int field015;
            [GhostField] public int field016;
            [GhostField] public int field017;
            [GhostField] public int field018;
            [GhostField] public int field019;
            [GhostField] public int field020;
            [GhostField] public int field021;
            [GhostField] public int field022;
            [GhostField] public int field023;
            [GhostField] public int field024;
            [GhostField] public int field025;
            [GhostField] public int field026;
            [GhostField] public int field027;
            [GhostField] public int field028;
            [GhostField] public int field029;
            [GhostField] public int field030;
            [GhostField] public int field031;
            [GhostField] public int field032;
            [GhostField] public int field033;
            [GhostField] public int field034;
            [GhostField] public int field035;
            [GhostField] public int field036;
            [GhostField] public int field037;
            [GhostField] public int field038;
            [GhostField] public int field039;
            [GhostField] public int field040;
            [GhostField] public int field041;
            [GhostField] public int field042;
            [GhostField] public int field043;
            [GhostField] public int field044;
            [GhostField] public int field045;
            [GhostField] public int field046;
            [GhostField] public int field047;
            [GhostField] public int field048;
            [GhostField] public int field049;
            [GhostField] public int field050;
            [GhostField] public int field051;
            [GhostField] public int field052;
            [GhostField] public int field053;
            [GhostField] public int field054;
            [GhostField] public int field055;
            [GhostField] public int field056;
            [GhostField] public int field057;
            [GhostField] public int field058;
            [GhostField] public int field059;
            [GhostField] public int field060;
            [GhostField] public int field061;
            [GhostField] public int field062;
            [GhostField] public int field063;
            [GhostField] public int field064;
            [GhostField] public int field065;
            [GhostField] public int field066;
            [GhostField] public int field067;
            [GhostField] public int field068;
            [GhostField] public int field069;
            [GhostField] public int field070;
            [GhostField] public int field071;
            [GhostField] public int field072;
            [GhostField] public int field073;
            [GhostField] public int field074;
            [GhostField] public int field075;
            [GhostField] public int field076;
            [GhostField] public int field077;
            [GhostField] public int field078;
            [GhostField] public int field079;
            [GhostField] public int field080;
            [GhostField] public int field081;
            [GhostField] public int field082;
            [GhostField] public int field083;
            [GhostField] public int field084;
            [GhostField] public int field085;
            [GhostField] public int field086;
            [GhostField] public int field087;
            [GhostField] public int field088;
            [GhostField] public int field089;
            [GhostField] public int field090;
            [GhostField] public int field091;
            [GhostField] public int field092;
            [GhostField] public int field093;
            [GhostField] public int field094;
            [GhostField] public int field095;
            [GhostField] public int field096;
            [GhostField] public int field097;
            [GhostField] public int field098;
            [GhostField] public int field099;
            [GhostField] public int field100;
        }

        public class GhostGenBigStructConverter : TestNetCodeAuthoring.IConverter
        {
            public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
            {
                dstManager.AddComponentData(entity, new GhostGenBigStruct {});
            }
        }

        [Test]
        public void StructWithLargeNumberOfFields()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new GhostGenBigStructConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);

                var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                //Use reflection.. just because if is faster
                var data = default(GhostGenBigStruct);
                unsafe
                {
                    var values = (int*)UnsafeUtility.AddressOf(ref data);
                    for (int i = 0; i < 100; ++i)
                    {
                        values[i] = i;
                    }
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, data);

                float frameTime = 1.0f / 60.0f;
                // Connect and make sure the connection could be established
                Assert.IsTrue(testWorld.Connect(frameTime, 4));

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(frameTime);

                var clientEntity = testWorld.TryGetSingletonEntity<GhostGenBigStruct>(testWorld.ClientWorlds[0]);
                var clientData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostGenBigStruct>(clientEntity);
                var serverData = testWorld.ServerWorld.EntityManager.GetComponentData<GhostGenBigStruct>(serverEntity);
                Assert.AreEqual(serverData, clientData);
            }
        }
    }
}