using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal struct FixedListComplexData
    {
        public int Value1;
        public uint Value2;
        public short Value3;
        public ushort Value4;
        public sbyte Value5;
        public byte Value6;
        public long Value7;
        public ulong Value8;
        public float Value9;
        public double Value10;
        public float2 Value11;
        public float3 Value12;
        public float4 Value13;
        public quaternion Value14;
        public NetworkTick Value15;
    }

    struct FixedListPrimitive
    {
        public FixedList32Bytes<int> FixedList1;
        public FixedList64Bytes<float> FixedList2;
        public const int FixedIntArrayLength = 7;
        public unsafe fixed int FixedIntArray[FixedIntArrayLength];
        public FixedList128Bytes<uint> FixedList3;
        public FixedList512Bytes<ulong> FixedList4;

        /// <summary>This method must be implemented by the user, when using fixed arrays, to allow code-gen to access it safely.</summary>
        /// <param name="index">The array index.</param>
        /// <returns>A ref to the array element.</returns>
        /// <exception cref="InvalidOperationException">When <see cref="index"/> is out of bounds.</exception>
        public unsafe ref int FixedIntArrayRef(int index)
        {
            if (index < 0 || index >= FixedIntArrayLength)
                throw new InvalidOperationException($"FixedIntArray[{index}] is out of bounds (Length:{FixedIntArrayLength})!");
            return ref FixedIntArray[index];
        }
    }

    struct Axis
    {
        public FixedList64Bytes<float> Values;

        public void WriteAxisForTick(NetworkTick tick)
        {
            var t = tick.TickIndexForValidTick % 5;
            if (Values.Length == 0) t = 0;
            switch (t)
            {
                case 0:
                    //reset both len and axis
                    Values.Length = 4;
                    for(int i=0; i<Values.Length; i++)
                        Values[i] = tick.SerializedData;
                    break;
                case 1:
                    //reset the len to 2 but don't re-assing data
                    Values.Length = 2;
                    break;
                case 2:
                    //keep the length, different  Axis is selected using the
                    var ax = (int)(t/4) % Values.Length;
                    Values[ax] = tick.SerializedData;
                    break;
                case 3:
                    //increase the length, assign some data
                    if (Values.Length < Values.Capacity)
                    {
                        ++Values.Length;
                        Values[Values.Length-1] = tick.SerializedData;
                    }
                    break;
                case 4:
                    //decrease the length, don't assign data
                    if (Values.Length > 1)
                        --Values.Length;
                    break;
                default:
                    break;
            }
        }
    }

    class FixedListInputDataConv : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            baker.AddComponent<FixedListInputData>(baker.GetEntity(TransformUsageFlags.Dynamic));
        }
    }

    class MultiComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public MultiComponentConverter(params ComponentType[] componentTypes)
        {
            this.componentTypes = componentTypes;
        }
        public ComponentType[] componentTypes;
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var ent = baker.GetEntity(TransformUsageFlags.None);
            foreach (var t in componentTypes)
                baker.AddComponent(ent, t);
        }
    }

    struct FixedListCommand : ICommandData
    {
        public NetworkTick Tick { get; set; }
        public Axis Axis;
    }

    struct FixedListInputData : IInputComponentData
    {
        public Axis Axis;
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class FixedListCommandSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<FixedListCommand>()));
        }
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            foreach (var inputBuffer in SystemAPI.Query<DynamicBuffer<FixedListCommand>>())
            {
                var cmd = new FixedListCommand { Tick = tick };
                tick.Decrement();
                inputBuffer.GetDataAtTick(tick, out var oldCmd);
                cmd.Axis = oldCmd.Axis;
                cmd.Axis.WriteAxisForTick(tick);
                inputBuffer.AddCommandData(cmd);
            }
        }
    }

    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class FixedListInputCommandSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamInGame>();
            RequireForUpdate(GetEntityQuery(ComponentType.ReadWrite<FixedListInputData>()));
        }
        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            foreach (var inputCmd in SystemAPI.Query<RefRW<FixedListInputData>>())
            {
                inputCmd.ValueRW.Axis.WriteAxisForTick(tick);
            }
        }
    }

    //Rpcs
    struct RPC_FixedListStruct : IRpcCommand
    {
        public FixedList512Bytes<FixedListComplexData> Value;
    }
    struct RPC_FixedListPrimitive : IRpcCommand
    {
        public FixedListPrimitive Value;
    }

    //Components
    struct SimpleFixedListData
    {
        [GhostField(Quantization=0)]public int Value1;
        public float Value2;
        [GhostField(SendData = false)]public int Value3;
        [GhostField(Quantization=100)]public float Value4;
        /// <remarks>
        /// <c>Quantization = 0</c> cancels out the <c>Quantization = 1000</c> in the outer collection,
        /// which would have emitted a compiler error (as no suitable template could be found).
        /// </remarks>
        [GhostField(Quantization=0)]public Entity Value5;
        public const int Value6FixedArrayLength = 3;
        [GhostField(Quantization = 100)]public unsafe fixed float Value6FixedArray[Value6FixedArrayLength];

        /// <summary>This method must be implemented by the user, when using fixed arrays, to allow code-gen to access it safely.</summary>
        /// <param name="index">The array index.</param>
        /// <returns>A ref to the array element.</returns>
        /// <exception cref="InvalidOperationException">When <see cref="index"/> is out of bounds.</exception>
        public unsafe ref float Value6FixedArrayRef(int index)
        {
            if (index < 0 || index >= Value6FixedArrayLength)
                throw new InvalidOperationException($"FixedIntArray[{index}] is out of bounds (Length:{Value6FixedArrayLength})!");
            return ref Value6FixedArray[index];
        }
    }

    internal struct FixedElement8
    {
        public int Value1;
        public int Value2;
        public int Value3;
        public int Value4;
        public int Value5;
        public int Value6;
        public int Value7;
        public int Value8;
    }

    internal struct NestedFixedList
    {
        [GhostField(Quantization=0)]public int Value1;
        [GhostField(Quantization=0)]public int Value2;
        public FixedList32Bytes<NestedFixedListNestedData> FixedList;

        /// <summary>Nested SimpleData with the same name as the above!</summary>
        public struct NestedFixedListNestedData
        {
            /// <remarks>
            /// <c>Quantization = 0</c> cancels out the <c>Quantization = 1000</c> in the outer collection,
            /// which would have emitted a compiler error (as no suitable template could be found).
            /// </remarks>
            [GhostField(Quantization = 0)]public Entity Value1;
            [GhostField(Quantization = 0)]public long Value2;
            [GhostField(SendData = false)]public int Value3;
            [GhostField(Quantization=100)]public float Value4;
            public float Value5;
        }
    }
    internal struct Primitive : IComponentData
    {
        [GhostField]public int Value1;
        [GhostField]public int Value2;
        [GhostField]public int Value3;
        [GhostField(Quantization = 1000)]
        public FixedList32Bytes<float> Value4;
    }
    internal struct WithStruct : IComponentData
    {
        [GhostField]public int Value1;
        [GhostField]public int Value2;
        [GhostField]public int Value3;
        [GhostFixedListCapacity] // An empty value here should use the FixedList capacity upper-bound!
        [GhostField(Quantization = 1000)]public FixedList512Bytes<SimpleFixedListData> Value4;
        [GhostField(Quantization = 1000)]public NestedFixedList Value5;
    }
    internal struct MoreThan64Elements : IComponentData
    {
        [GhostField]public FixedElement8 Value1;
        [GhostField]
        [GhostFixedListCapacity(Capacity = 32)]
        public FixedList512Bytes<uint> Value2;
        [GhostField]public FixedElement8 Value3;

        public bool Equals(in MoreThan64Elements other)
        {
            return Value1.Equals(other.Value1) &&
                   Value2.Equals(other.Value2) &&
                   Value3.Equals(other.Value3);
        }
    }

    //Sent to remote players
    [GhostComponent(OwnerSendType = SendToOwnerType.SendToNonOwner)]
    internal struct CappedInput : IInputComponentData
    {
        [GhostField]
        [GhostFixedListCapacity(Capacity = 8)]
        public FixedList128Bytes<uint> Capped8;
        [GhostField]
        [GhostFixedListCapacity(Capacity = 64)]
        public FixedList128Bytes<byte> CappedDefault;

        public bool Equals(in CappedInput other)
        {
            return Capped8.Equals(other.Capped8) &&
                   CappedDefault.Equals(other.CappedDefault);
        }
    }

    //Sent to remote players
    internal struct CappedRpc : IRpcCommand
    {
        //maximum allowed size is 1024 (this is the upper limit for RPC)
        [GhostFixedListCapacity(Capacity = 1024)]
        public FixedList4096Bytes<byte> LargeList;
        [GhostFixedListCapacity(Capacity = 8)]
        public FixedList128Bytes<uint> Values;

        public bool Equals(in CappedRpc other)
        {
            return LargeList.Equals(other.LargeList) &&
                   Values.Equals(other.Values);
        }
    }

    internal class FixedListSupportTests
    {
        [Test]
        public void RPC_SupportFixedLists_WithStruct()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1, false);
            testWorld.Connect();

            var rpc = new RPC_FixedListStruct();
            rpc.Value.Length = 4;
            for (int i = 1; i <= rpc.Value.Length; ++i)
            {
                rpc.Value[i - 1] = new FixedListComplexData
                {
                    Value1 = i*10,
                    Value2 = (uint)(i*10),
                    Value3 = (short)(i*10),
                    Value4 = (ushort)(i*10),
                    Value5 = (sbyte)(i*10),
                    Value6 = (byte)(i*10),
                    Value7 = (long)(i*10),
                    Value8 = (ulong)(i*10),
                    Value9 = (float)(i*10),
                    Value10 = (double)(i*10),
                    Value11 = new float2(i, i*2f),
                    Value12 = new float3(i, i*2f, i*3f),
                    Value13 = new float4(i, i*2f, i*3f, i*4f),
                    Value14 = quaternion.AxisAngle(new float3(i,0,i), math.PI/7),
                    Value15 = new NetworkTick {SerializedData = 1001}
                };
            }
            var rcpEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(
                typeof(RPC_FixedListStruct), typeof(SendRpcCommandRequest));
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(rcpEntity, rpc);

            for (int i = 0; i < 3; ++i)
                testWorld.Tick();

            //check received data
            var rpcs = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(RPC_FixedListStruct))
                .ToComponentDataArray<RPC_FixedListStruct>(Allocator.Temp);
            Assert.AreEqual(1, rpcs.Length);
            {
                foreach(var recvRpc in rpcs)
                {
                     Assert.AreEqual(rpc.Value.Length, recvRpc.Value.Length);
                     for (int i = 0; i < rpc.Value.Length; ++i)
                    {
                        Assert.AreEqual(rpc.Value[i].Value1, recvRpc.Value[i].Value1);
                        Assert.AreEqual(rpc.Value[i].Value2, recvRpc.Value[i].Value2);
                        Assert.AreEqual(rpc.Value[i].Value3, recvRpc.Value[i].Value3);
                        Assert.AreEqual(rpc.Value[i].Value4, recvRpc.Value[i].Value4);
                        Assert.AreEqual(rpc.Value[i].Value5, recvRpc.Value[i].Value5);
                        Assert.AreEqual(rpc.Value[i].Value6, recvRpc.Value[i].Value6);
                        Assert.AreEqual(rpc.Value[i].Value7, recvRpc.Value[i].Value7);
                        Assert.AreEqual(rpc.Value[i].Value8, recvRpc.Value[i].Value8);
                        Assert.AreEqual(rpc.Value[i].Value9, recvRpc.Value[i].Value9);
                        Assert.AreEqual(rpc.Value[i].Value10, recvRpc.Value[i].Value10);
                        Assert.AreEqual(rpc.Value[i].Value11, recvRpc.Value[i].Value11);
                        Assert.AreEqual(rpc.Value[i].Value12, recvRpc.Value[i].Value12);
                        Assert.AreEqual(rpc.Value[i].Value13, recvRpc.Value[i].Value13);
                        Assert.AreEqual(rpc.Value[i].Value14, recvRpc.Value[i].Value14);
                        Assert.AreEqual(rpc.Value[i].Value15, recvRpc.Value[i].Value15);
                    }
                }
            }
        }

        [Test]
        public void RPC_SupportFixedLists_Primitives()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1, false);
            testWorld.Connect();

            var rpc = new RPC_FixedListPrimitive();
            rpc.Value.FixedList1.Length = 6;
            for (int i = 0; i < rpc.Value.FixedList1.Length; ++i)
            {
                rpc.Value.FixedList1[i] = 102032 ^ 8392*(i+1);
            }
            rpc.Value.FixedList2.Length = 8;
            for (int i = 0; i < rpc.Value.FixedList2.Length; ++i)
            {
                rpc.Value.FixedList2[i] = (1+i) * 0.23123e-4f;
            }
            rpc.Value.FixedList3.Length = 16;
            for (int i = 0; i < rpc.Value.FixedList3.Length; ++i)
            {
                rpc.Value.FixedList3[i] = (uint)((1u+i) * 23123u);
            }
            rpc.Value.FixedList4.Length = 8;
            for (int i = 0; i < rpc.Value.FixedList4.Length; ++i)
            {
                rpc.Value.FixedList4[i] = 0xfFaEfful ^ (ulong)(102032 * i);
            }
            unsafe
            {
                for (int i = 0; i < FixedListPrimitive.FixedIntArrayLength; ++i)
                {
                    rpc.Value.FixedIntArray[i] = 8;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                var rcpEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(
                    typeof(RPC_FixedListPrimitive), typeof(SendRpcCommandRequest));
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(rcpEntity, rpc);
            }
            for (int i = 0; i < 4; ++i)
                testWorld.Tick();

            //check received data
            var rpcs = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(RPC_FixedListPrimitive))
                .ToComponentDataArray<RPC_FixedListPrimitive>(Allocator.Temp);
            Assert.AreEqual(3, rpcs.Length);
            {
                foreach(var recvRpc in rpcs)
                {
                    Assert.AreEqual(rpc.Value.FixedList1.Length, recvRpc.Value.FixedList1.Length);
                    for (int i = 0; i < rpc.Value.FixedList1.Length; ++i)
                        Assert.AreEqual(rpc.Value.FixedList1[i], recvRpc.Value.FixedList1[i]);
                    Assert.AreEqual(rpc.Value.FixedList2.Length, recvRpc.Value.FixedList2.Length);
                    for (int i = 0; i < rpc.Value.FixedList2.Length; ++i)
                        Assert.AreEqual(rpc.Value.FixedList2[i], recvRpc.Value.FixedList2[i]);
                    Assert.AreEqual(rpc.Value.FixedList3.Length, recvRpc.Value.FixedList3.Length);
                    for (int i = 0; i < rpc.Value.FixedList3.Length; ++i)
                        Assert.AreEqual(rpc.Value.FixedList3[i], recvRpc.Value.FixedList3[i]);
                    Assert.AreEqual(rpc.Value.FixedList4.Length, recvRpc.Value.FixedList4.Length);
                    for (int i = 0; i < rpc.Value.FixedList4.Length; ++i)
                        Assert.AreEqual(rpc.Value.FixedList4[i], recvRpc.Value.FixedList4[i]);
                    unsafe
                    {
                        for (int i = 0; i < FixedListPrimitive.FixedIntArrayLength; ++i)
                        {
                            Assert.AreEqual(rpc.Value.FixedIntArray[i], recvRpc.Value.FixedIntArray[i]);
                        }
                    }
                }
            }
        }

        [Test]
        public void CommandDataAndIInputComponent_SupportFixedList([Values]bool useInputData)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = new GameObject();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                if (useInputData)
                    ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new FixedListInputDataConv();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;

                if (useInputData)
                    testWorld.Bootstrap(true, typeof(FixedListInputCommandSystem));
                else
                    testWorld.Bootstrap(true, typeof(FixedListCommandSystem));
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = netId });

                //wait for the client to spawn
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);
                if (!useInputData)
                    testWorld.ServerWorld.EntityManager.AddComponent<FixedListCommand>(serverEnt);
                    testWorld.ClientWorlds[0].EntityManager.AddComponent<FixedListCommand>(clientEnt);

                for (int i = 0; i < 60; ++i)
                    testWorld.Tick();

                if (useInputData)
                {
                    var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<InputBufferData<FixedListInputData>>(clientEnt);
                    var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<InputBufferData<FixedListInputData>>(serverEnt);
                    //1 less because the client just sent the last command
                    Assert.AreEqual(61, serverBuffer.Length);
                    //client has more commands (1 ticks ahead)
                    Assert.AreEqual(62, clientBuffer.Length);
                    for (int cmd = 0; cmd < serverBuffer.Length; ++cmd)
                    {
                        var serverCmdTick = serverBuffer[cmd].Tick;
                        Assert.IsTrue(serverBuffer.GetDataAtTick(serverCmdTick, out var serverCommand));
                        Assert.AreEqual(serverCmdTick, serverCommand.Tick);
                        //verify command data
                        Assert.IsTrue(clientBuffer.GetDataAtTick(serverCmdTick, out var clientCommand));
                        Assert.AreEqual(serverCmdTick, clientCommand.Tick);
                        Assert.AreEqual(clientCommand.InternalInput.Axis.Values.Length, serverCommand.InternalInput.Axis.Values.Length);
                        for (int ax = 0; ax < clientCommand.InternalInput.Axis.Values.Length; ++ax)
                            Assert.AreEqual(clientCommand.InternalInput.Axis.Values[ax], serverCommand.InternalInput.Axis.Values[ax]);
                    }
                }
                else
                {
                    var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<FixedListCommand>(clientEnt);
                    var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<FixedListCommand>(serverEnt);
                    Assert.AreEqual(59, serverBuffer.Length);
                    //client has more commands (1 tick ahead)
                    Assert.AreEqual(60, clientBuffer.Length);
                    for (int cmd = 0; cmd < serverBuffer.Length; ++cmd)
                    {
                        var serverCmdTick = serverBuffer[cmd].Tick;
                        Assert.IsTrue(serverBuffer.GetDataAtTick(serverCmdTick, out var serverCommand));
                        Assert.AreEqual(serverCmdTick, serverCommand.Tick);
                        //verify command data
                        Assert.IsTrue(clientBuffer.GetDataAtTick(serverCmdTick, out var clientCommand));
                        Assert.AreEqual(serverCmdTick, clientCommand.Tick);
                        Assert.AreEqual(clientCommand.Axis.Values.Length, serverCommand.Axis.Values.Length);
                        for (int ax = 0; ax < clientCommand.Axis.Values.Length; ++ax)
                            Assert.AreEqual(clientCommand.Axis.Values[ax], serverCommand.Axis.Values[ax]);
                    }
                }
            }
        }

        [Test]
        public void Snapshot_SupportFixedLists_Primitive()
        {
            Entity CreateGhost(World world)
            {
                var entity = world.EntityManager.CreateEntity();
                world.EntityManager.AddComponentData(entity, new Primitive
                {
                    Value1 = 100,
                    Value2 = 200,
                    Value3 = 300,
                    Value4 = new FixedList32Bytes<float>
                    {
                        1.0f,
                        2.0f,
                        3.0f,
                        4.0f,
                        5.0f,
                        6.0f,
                        7.0f,
                    },
                });

                GhostPrefabCreation.ConvertToGhostPrefab(world.EntityManager, entity, new GhostPrefabCreation.Config
                {
                    Name = "PrimitiveGhost",
                    Importance = 1,
                    SupportedGhostModes = GhostModeMask.All,
                    DefaultGhostMode = GhostMode.Interpolated,
                    OptimizationMode = GhostOptimizationMode.Dynamic
                });
                return entity;
            }

            using var testWorld = new NetCodeTestWorld();
            testWorld.DebugPackets = true;
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1, false);
            testWorld.CreateGhostCollection();
            testWorld.Connect();
            var ghostPrefab = CreateGhost(testWorld.ServerWorld);
            CreateGhost(testWorld.ClientWorlds[0]);
            testWorld.GoInGame();
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
            var serverGhost = testWorld.ServerWorld.EntityManager.Instantiate(ghostPrefab);
            for (int i = 0; i < 4; ++i)
            {
                testWorld.Tick();
            }
            var serverData = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(Primitive)).GetSingleton<Primitive>();
            var serverValues = new Primitive[32];
            serverValues[0] = serverData;
            serverValues[1] = serverData;
            serverValues[2] = serverData;
            serverValues[3] = serverData;
            var lengthInc = 0;
            for (int i = 0; i < 24; ++i)
            {
                testWorld.Tick();
                var clientData = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(Primitive)).GetSingleton<Primitive>();
                Assert.AreEqual(serverValues[i].Value1, clientData.Value1);
                Assert.AreEqual(serverValues[i].Value2, clientData.Value2);
                Assert.AreEqual(serverValues[i].Value3, clientData.Value3);
                Assert.AreEqual(serverValues[i].Value4.Length, clientData.Value4.Length);
                for (int el = 0; el < serverValues[i].Value4.Length; ++el)
                {
                    UnityEngine.Assertions.Assert.AreApproximatelyEqual(serverValues[i].Value4[el], clientData.Value4[el], 1e-3f);
                }
                serverData = serverValues[i];
                serverData.Value1 = serverValues[i].Value1 + 1;
                serverData.Value2 = serverValues[i].Value1 + 3;
                serverData.Value3 = serverValues[i].Value1 + 5;
                if (serverValues[i].Value4.Length == 0 || serverValues[i].Value4.Length == serverValues[i].Value4.Capacity)
                    lengthInc = - lengthInc;
                switch ((i % 3))
                {
                    //modify only length (and the new added if increasing)
                    case 0:
                        serverData.Value4.Length = serverValues[i].Value4.Length + lengthInc;
                        if(lengthInc > 0)
                            serverData.Value4[^1] = 1000*i;
                        break;
                    //modify only the elements. Only the odd ones
                    case 1:
                        for (int el = 0; el < serverData.Value4.Length; el+=2)
                            serverData.Value4[el] = 100*i;
                        break;
                    // //modify both
                    case 2:
                        serverData.Value4.Length = serverValues[i].Value4.Length + lengthInc;
                        for (int el = 0; el < serverData.Value4.Length; ++el)
                            serverData.Value4[el] = 100*i;
                        break;
                    default:
                        break;
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, serverData);
                serverValues[i + 4] = serverData;
            }
        }
        [Test]
        public void Snapshot_SupportFixedLists_WithStruct()
        {
            Entity CreateGhost(World world)
            {
                var entity = world.EntityManager.CreateEntity();

                var data = new WithStruct
                {
                    Value1 = 100,
                    Value2 = 200,
                    Value3 = 300,
                };
                data.Value4.Length = 3;
                for (int i = 0; i < data.Value4.Length; ++i)
                {
                    ref var entryRef = ref data.Value4.ElementAt(i);
                    data.Value4[i] = new SimpleFixedListData
                    {
                        Value1 = 10 + i,
                        Value2 = 20 + i,
                        Value3 = 30 + i,
                        Value4 = 40 + i,
                        Value5 = default,
                    };
                    entryRef.Value6FixedArrayRef(0) = i - 25059;
                    entryRef.Value6FixedArrayRef(1) = i + 23;
                    entryRef.Value6FixedArrayRef(2) = i + 3335;
                }

                data.Value5.Value1 = 500;
                data.Value5.Value2 = 1000;
                data.Value5.FixedList = new FixedList32Bytes<NestedFixedList.NestedFixedListNestedData>();
                data.Value5.FixedList.Length = data.Value5.FixedList.Capacity - data.Value5.FixedList.Capacity/2;
                for (int i = 0; i < data.Value5.FixedList.Length; ++i)
                {
                    data.Value5.FixedList[i] = new NestedFixedList.NestedFixedListNestedData
                    {
                        Value1 = default,
                        Value2 = -20 - i,
                        Value3 = -30 - i,
                        Value4 = -40 - i,
                        Value5 = -50 - i,
                    };
                }
                world.EntityManager.AddComponentData(entity, data);
                GhostPrefabCreation.ConvertToGhostPrefab(world.EntityManager, entity, new GhostPrefabCreation.Config
                {
                    Name = "StructGhost",
                    Importance = 1,
                    SupportedGhostModes = GhostModeMask.All,
                    DefaultGhostMode = GhostMode.Interpolated,
                    OptimizationMode = GhostOptimizationMode.Dynamic
                });
                return entity;
            }

            using var testWorld = new NetCodeTestWorld();
            testWorld.DebugPackets = true;
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1, false);
            testWorld.CreateGhostCollection();
            testWorld.Connect();
            var ghostPrefab = CreateGhost(testWorld.ServerWorld);
            CreateGhost(testWorld.ClientWorlds[0]);
            testWorld.GoInGame();
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();

            // Spawn server instance, and set up the Entity field on the instance:
            var serverGhost = testWorld.ServerWorld.EntityManager.Instantiate(ghostPrefab);
            {
                var serverWithStruct = testWorld.ServerWorld.EntityManager.GetComponentData<WithStruct>(serverGhost);
                for (int i = 0; i < serverWithStruct.Value4.Length; i++)
                    serverWithStruct.Value4.ElementAt(i).Value5 = i % 3 == 0 ? serverGhost : default;
                for (int i = 0; i < serverWithStruct.Value5.FixedList.Length; i++)
                    serverWithStruct.Value5.FixedList.ElementAt(i).Value1 = i % 2 == 0 ? serverGhost : default;
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, serverWithStruct);
            }

            for (int i = 0; i < 4; ++i)
            {
                testWorld.Tick();
            }
            var serverData = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(WithStruct)).GetSingleton<WithStruct>();
            var serverValues = new WithStruct[32];
            serverValues[0] = serverData;
            serverValues[1] = serverData;
            serverValues[2] = serverData;
            serverValues[3] = serverData;
            var lengthInc0 = 0;
            var lengthInc1 = 0;
            const float quantizationError = 0.002f;
            for (int i = 0; i < 24; ++i)
            {
                testWorld.Tick();
                var clientData = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(WithStruct)).GetSingleton<WithStruct>();
                Assert.AreEqual(serverValues[i].Value1, clientData.Value1);
                Assert.AreEqual(serverValues[i].Value2, clientData.Value2);
                Assert.AreEqual(serverValues[i].Value3, clientData.Value3);
                Assert.AreEqual(serverValues[i].Value4.Length, clientData.Value4.Length);
                for (int el = 0; el < serverValues[i].Value4.Length; ++el)
                {
                    Assert.That(clientData.Value4[el].Value1, Is.EqualTo(serverValues[i].Value4[el].Value1));
                    Assert.That(clientData.Value4[el].Value2, Is.EqualTo(serverValues[i].Value4[el].Value2).Within(quantizationError));
                    Assert.That(clientData.Value4[el].Value3, Is.EqualTo(serverValues[i].Value4[el].Value3));
                    Assert.That(clientData.Value4[el].Value4, Is.EqualTo(serverValues[i].Value4[el].Value4).Within(quantizationError));
                    if (el % 3 == 0)
                    {
                        // Both should be pointing to their respective valid entities.
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.Exists(serverValues[i].Value4[el].Value5));
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.Exists(clientData.Value4[el].Value5));
                    }
                    else
                    {
                        // Both should be Entity.Null.
                        Assert.AreEqual(Entity.Null, serverValues[i].Value4[el].Value5);
                        Assert.AreEqual(Entity.Null, clientData.Value4[el].Value5);
                    }


                    Assert.That(clientData.Value4[el].Value6FixedArrayRef(0), Is.EqualTo(serverValues[i].Value4[el].Value6FixedArrayRef(0)).Within(quantizationError));
                    Assert.That(clientData.Value4[el].Value6FixedArrayRef(1), Is.EqualTo(serverValues[i].Value4[el].Value6FixedArrayRef(1)).Within(quantizationError));
                    Assert.That(clientData.Value4[el].Value6FixedArrayRef(2), Is.EqualTo(serverValues[i].Value4[el].Value6FixedArrayRef(2)).Within(quantizationError));
                }

                Assert.AreEqual(serverValues[i].Value5.Value1, clientData.Value5.Value1);
                Assert.AreEqual(serverValues[i].Value5.Value2, clientData.Value5.Value2);
                for (int el = 0; el < serverValues[i].Value5.FixedList.Length; ++el)
                {
                    Assert.AreEqual(serverValues[i].Value5.FixedList[el].Value1, clientData.Value5.FixedList[el].Value1);
                    Assert.AreEqual(serverValues[i].Value5.FixedList[el].Value2, clientData.Value5.FixedList[el].Value2);
                    Assert.AreEqual(serverValues[i].Value5.FixedList[el].Value3, clientData.Value5.FixedList[el].Value3);
                    Assert.That(clientData.Value5.FixedList[el].Value5, Is.EqualTo(serverValues[i].Value5.FixedList[el].Value5).Within(quantizationError));
                    if(el % 2 == 0)
                    {
                        // Both should be pointing to their respective valid entities.
                        Assert.IsTrue(testWorld.ServerWorld.EntityManager.Exists(serverValues[i].Value5.FixedList[el].Value1));
                        Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.Exists(clientData.Value5.FixedList[el].Value1));
                    }
                    else
                    {
                        // Both should be Entity.Null.
                        Assert.AreEqual(Entity.Null, serverValues[i].Value5.FixedList[el].Value1);
                        Assert.AreEqual(Entity.Null, clientData.Value5.FixedList[el].Value1);
                    }
                }
                serverData = serverValues[i];
                serverData.Value1 = serverValues[i].Value1 + 1;
                serverData.Value2 = serverValues[i].Value1 + 3;
                serverData.Value3 = serverValues[i].Value1 + 5;
                if (serverValues[i].Value4.Length == 0 || serverValues[i].Value4.Length == serverValues[i].Value4.Capacity)
                    lengthInc0 = - lengthInc0;
                if (serverValues[i].Value5.FixedList.Length == 0 || serverValues[i].Value5.FixedList.Length == serverValues[i].Value5.FixedList.Capacity)
                    lengthInc1 = - lengthInc1;

                serverData.Value4.Length = serverValues[i].Value4.Length + lengthInc0;
                for (int el = 0; el < serverData.Value4.Length; ++el)
                {
                    serverValues[i].Value4.ElementAt(el).Value1 = serverData.Value4[el].Value1 + lengthInc0;
                    serverValues[i].Value4.ElementAt(el).Value2 = serverData.Value4[el].Value2 + lengthInc0;
                    serverValues[i].Value4.ElementAt(el).Value3 = serverData.Value4[el].Value3 + lengthInc0;
                    serverValues[i].Value4.ElementAt(el).Value4 = serverData.Value4[el].Value4 + lengthInc0;
                    serverValues[i].Value4.ElementAt(el).Value5 = serverData.Value4[el].Value5;
                    serverValues[i].Value4.ElementAt(el).Value6FixedArrayRef(0) = serverData.Value4[el].Value6FixedArrayRef(0);
                    serverValues[i].Value4.ElementAt(el).Value6FixedArrayRef(1) = serverData.Value4[el].Value6FixedArrayRef(1);
                    serverValues[i].Value4.ElementAt(el).Value6FixedArrayRef(2) = serverData.Value4[el].Value6FixedArrayRef(2);
                }
                serverData.Value5.Value1 = serverData.Value5.Value1 + 11;
                serverData.Value5.Value2 = serverData.Value5.Value1 + 17;
                serverData.Value5.FixedList.Length = serverValues[i].Value5.FixedList.Length + lengthInc1;
                for (int el = 0; el < serverData.Value5.FixedList.Length; ++el)
                {
                    serverValues[i].Value5.FixedList.ElementAt(el).Value1 = serverData.Value5.FixedList[el].Value1;
                    serverValues[i].Value5.FixedList.ElementAt(el).Value2 = serverData.Value5.FixedList[el].Value2 + lengthInc0;
                    serverValues[i].Value5.FixedList.ElementAt(el).Value3 = serverData.Value5.FixedList[el].Value3 + lengthInc0;
                    serverValues[i].Value5.FixedList.ElementAt(el).Value4 = serverData.Value5.FixedList[el].Value4 + lengthInc0;
                    serverValues[i].Value5.FixedList.ElementAt(el).Value5 = serverData.Value5.FixedList[el].Value5 + lengthInc0;
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, serverData);
                serverValues[i + 4] = serverData;
            }
        }
    }

    internal class FixedList_CapTests
    {

        [Test]
        public void RPC_1024CapacityCap()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);
            testWorld.CreateWorlds(true, 1, false);
            testWorld.Connect();

            //TODO we can't easily verify 1024 element cap because we always serialize 4 bytes for a byte (silly)
            var rpc = new CappedRpc();
            rpc.LargeList.Length = 1024;
            for (int i = 0; i < rpc.LargeList.Length; ++i)
                rpc.LargeList[i] = (byte)i;
            var rcpEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(
                typeof(CappedRpc), typeof(SendRpcCommandRequest));
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(rcpEntity, rpc);

            for (int i = 0; i < 3; ++i)
                testWorld.Tick();

            //check received data
            var rpcs = testWorld.ServerWorld.EntityManager.CreateEntityQuery(typeof(CappedRpc))
                .ToComponentDataArray<CappedRpc>(Allocator.Temp);
            Assert.AreEqual(1, rpcs.Length);
            foreach (var recvRpc in rpcs)
            {
                Assert.AreEqual(rpc.LargeList.Length, recvRpc.LargeList.Length);
                for (int i = 0; i < rpc.LargeList.Length; ++i)
                {
                    Assert.AreEqual((byte)i, rpc.LargeList[i]);
                }
            }
            //verify exceptions are throwns as expected. if we go over capacity (sender side)
            rpc = new CappedRpc();
            rpc.LargeList.Length = 1029;
            rcpEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(CappedRpc), typeof(SendRpcCommandRequest));
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(rcpEntity, rpc);
            LogAssert.Expect(LogType.Error, new Regex("^Fixed list field .LargeList"));
            testWorld.Tick();
        }

        [Test]
        public void FixedList_Command_Capacity_Cap()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new MultiComponentConverter(ComponentType.ReadWrite<CappedInput>());
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.HasOwner = true;
                ghostConfig.SupportAutoCommandTarget = true;
                ghostConfig.DefaultGhostMode = GhostMode.OwnerPredicted;
                testWorld.Bootstrap(true);
                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
                testWorld.CreateWorlds(true, 1);
                testWorld.Connect();
                testWorld.GoInGame();

                for (int i = 0; i < 32; ++i)
                    testWorld.Tick();

                testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ServerWorld);
                var clientConnectionEnt = testWorld.TryGetSingletonEntity<NetworkId>(testWorld.ClientWorlds[0]);
                var netId = testWorld.ClientWorlds[0].EntityManager.GetComponentData<NetworkId>(clientConnectionEnt).Value;

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, new GhostOwner { NetworkId = netId });

                //wait for the client to spawn
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                //First: verify the limit up to the capacity
                var input = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CappedInput>(clientEnt);
                input.Capped8.Length = 8;
                input.CappedDefault.Length = 64;
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientEnt, input);
                for (int i = 0; i < 4; ++i)
                    testWorld.Tick();

                var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<InputBufferData<CappedInput>>(clientEnt);
                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<InputBufferData<CappedInput>>(serverEnt);
                //check that all received commands are fine
                for (int cmd = 0; cmd < serverBuffer.Length; ++cmd)
                {
                    var serverCmdTick = serverBuffer[cmd].Tick;
                    Assert.IsTrue(serverBuffer.GetDataAtTick(serverCmdTick, out var serverCmd));
                    Assert.IsTrue(clientBuffer.GetDataAtTick(serverCmdTick, out var clienCmd));
                    serverCmd.InternalInput.Equals(clienCmd.InternalInput);
                }

                //For input buffer is somewhat more tricky: the exceptions is thrown for every
                input.Capped8.Length = 10;
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientEnt, input);
                LogAssert.Expect(LogType.Error, new Regex("^Fixed list field .InternalInput.Capped8"));
                testWorld.Tick();
                input.Capped8.Length = 8;
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientEnt, input);
                for (int i = 0; i < 3; ++i)
                {
                    LogAssert.Expect(LogType.Error, new Regex("^Fixed list field .InternalInput.Capped8"));
                    testWorld.Tick();
                }
                input.CappedDefault.Length = 66;
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(clientEnt, input);
                for (int i = 0; i < 3; ++i)
                {
                    LogAssert.Expect(LogType.Error, new Regex("^Fixed list field .InternalInput.CappedDefault"));
                    testWorld.Tick();
                }
            }
        }

        void VerifyLargeData(in MoreThan64Elements serveData, in MoreThan64Elements clientData)
        {
            Assert.AreEqual(serveData.Value1, clientData.Value1);
            Assert.AreEqual(serveData.Value2.Length, clientData.Value2.Length);
            for (int el = 0; el < serveData.Value2.Length; ++el)
                Assert.AreEqual(serveData.Value2[el], clientData.Value2[el]);
            Assert.AreEqual(serveData.Value3, clientData.Value3);
        }

        MoreThan64Elements CreateMoreThan64ElementsData()
        {
            var data = new MoreThan64Elements
            {
                Value1 = new FixedElement8
                {
                    Value1 = 10,
                    Value2 = 20,
                    Value3 = 30,
                    Value4 = 40,
                    Value5 = 50,
                    Value6 = 60,
                    Value7 = 70,
                    Value8 = 80
                },
                Value3 = new FixedElement8
                {
                    Value1 = 100,
                    Value2 = 200,
                    Value3 = 300,
                    Value4 = 400,
                    Value5 = 500,
                    Value6 = 600,
                    Value7 = 700,
                    Value8 = 800
                },
            };
            data.Value2 = new FixedList32Bytes<uint>();
            data.Value2.Length = 16;
            for (int i = 0; i < data.Value2.Length; ++i)
            {
                data.Value2[i] = (uint)(0xABC0FF00u + 0xBDBD4eu * i);
            }

            return data;
        }

        [Test]
        public void FixedList_Snapshot_Capacity_Cap([Values]bool interpolated)
        {
            //return the last full interpolated tick or the last applied based on the ghost mode
            NetworkTick GetLastAppliedTick(NetCodeTestWorld testWorld, Entity entity, bool interpolated)
            {
                if (interpolated)
                {
                    var nt = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    var tick = nt.InterpolationTick;
                    if(nt.InterpolationTickFraction < 1)
                        tick.Decrement();
                    return tick;
                }
                return testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedGhost>(entity).AppliedTick;
            }

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new MultiComponentConverter(ComponentType.ReadWrite<MoreThan64Elements>());
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = interpolated ? GhostMode.Interpolated : GhostMode.Predicted;
            using var testWorld = new NetCodeTestWorld();
            testWorld.DebugPackets = true;
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection(ghostGameObject);
            testWorld.CreateWorlds(true, 1, false);
            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 16; ++i)
                testWorld.Tick();
            var serverGhost = testWorld.SpawnOnServer(0);
            var data = CreateMoreThan64ElementsData();
            testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, data);
            var serverData = testWorld.ServerWorld.EntityManager.GetComponentData<MoreThan64Elements>(serverGhost);
            var serverValues = new Dictionary<NetworkTick, MoreThan64Elements>(64);
            for (int i = 0; i < 5; ++i)
            {
                testWorld.Tick();
                serverValues[testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick] = serverData;
            }
            var clientEntity = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(MoreThan64Elements)).GetSingletonEntity();
            for (int i = 0; i < 16; ++i)
            {
                var clientData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<MoreThan64Elements>(clientEntity);
                //in prediction, the client is ahead. So the only thing we can check is the last applied tick (not the current server tick).
                var tick = GetLastAppliedTick(testWorld, clientEntity, interpolated);
                VerifyLargeData(serverValues[tick], clientData);
                ++serverData.Value1.Value1;
                ++serverData.Value1.Value2;
                ++serverData.Value1.Value3;
                ++serverData.Value1.Value4;
                ++serverData.Value1.Value5;
                ++serverData.Value1.Value6;
                ++serverData.Value1.Value7;
                serverData.Value3.Value1 += 5;
                serverData.Value3.Value2 += 5;
                serverData.Value3.Value3 += 5;
                serverData.Value3.Value4 += 5;
                serverData.Value3.Value5 += 5;
                serverData.Value3.Value6 += 5;
                serverData.Value3.Value7 += 5;
                serverData.Value3.Value7 += 5;
                switch ((i % 3))
                {
                    //modify both length and the newly added element
                    case 0:
                        serverData.Value2.Length = serverData.Value2.Length + 1;
                        serverData.Value2[^1] = (uint)(100*i);
                        break;
                    //modify only the elements. Only the odd ones
                    case 1:
                        for (int el = 0; el < serverData.Value2.Length; el+=2)
                            serverData.Value2[el] = (uint)(1000*i);
                        break;
                    default:
                        break;
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, serverData);
                testWorld.Tick();
                serverValues[testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick] = serverData;
            }
            //try to shrink the list over limit. This should trigger an exception caught by try-finally (so we track the log)
            for (int i = 0; i < 8; ++i)
            {
                if ((i % 4) == 0)
                {
                    serverData.Value2.Length = 35;
                    LogAssert.Expect(LogType.Error, new Regex("The Unity\\.NetCode\\.Tests.MoreThan64Elements.Value2 length \\(35\\) exceed that fixed list serializable "));
                }
                else
                {
                    serverData.Value2.Length = i*2;
                    for (int el = 0; el < serverData.Value2.Length; ++el)
                        serverData.Value2[el] = (uint)(1000*i);
                }
                testWorld.ServerWorld.EntityManager.SetComponentData(serverGhost, serverData);
                testWorld.Tick();
                serverValues[testWorld.GetNetworkTime(testWorld.ServerWorld).ServerTick] = serverData;
                var clientData = testWorld.ClientWorlds[0].EntityManager.GetComponentData<MoreThan64Elements>(clientEntity);
                var tick = GetLastAppliedTick(testWorld, clientEntity, interpolated);
                var val = serverValues[tick];
                if (val.Value2.Length > 32)
                {
                    Assert.AreEqual(32, clientData.Value2.Length);
                    for (int el = 0; el < 32; ++el)
                        Assert.AreEqual(val.Value2[el], clientData.Value2[el]);
                }
                else
                {
                    Assert.IsTrue(val.Value1.Equals(clientData.Value1));
                    Assert.IsTrue(val.Value2.Equals(clientData.Value2));
                    Assert.IsTrue(val.Value3.Equals(clientData.Value3));
                }
            }
        }
    }
}
