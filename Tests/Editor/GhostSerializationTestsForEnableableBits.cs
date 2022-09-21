using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    public class GhostSerializationTestsForEnableableBits
    {
        float frameTime = 1.0f / 60.0f;

        int GetClientEntityCount<T>()
        {
            var type = ComponentType.ReadOnly<T>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);
            return query.CalculateEntityCountWithoutFiltering();
        }

        int TickUntilReplicationIsDone(bool enabledBit)
        {
            int framesElapsed = 0;

            var clientCount = 0;
            do
            {
                testWorld.Tick(frameTime);
                framesElapsed++;

                switch (type)
                {
                    case GhostTypeConverter.GhostTypes.EnableableComponent:
                        clientCount = GetClientEntityCount<EnableableComponent>();
                        break;
                    case GhostTypeConverter.GhostTypes.MultipleEnableableComponent:
                        clientCount = GetClientEntityCount<EnableableComponent_0>();
                        clientCount &= GetClientEntityCount<EnableableComponent_1>();
                        clientCount &= GetClientEntityCount<EnableableComponent_2>();
                        clientCount &= GetClientEntityCount<EnableableComponent_3>();
                        clientCount &= GetClientEntityCount<EnableableComponent_4>();
                        clientCount &= GetClientEntityCount<EnableableComponent_5>();
                        clientCount &= GetClientEntityCount<EnableableComponent_6>();
                        clientCount &= GetClientEntityCount<EnableableComponent_7>();
                        clientCount &= GetClientEntityCount<EnableableComponent_8>();
                        clientCount &= GetClientEntityCount<EnableableComponent_9>();
                        clientCount &= GetClientEntityCount<EnableableComponent_10>();
                        clientCount &= GetClientEntityCount<EnableableComponent_11>();
                        clientCount &= GetClientEntityCount<EnableableComponent_12>();
                        clientCount &= GetClientEntityCount<EnableableComponent_13>();
                        clientCount &= GetClientEntityCount<EnableableComponent_14>();
                        clientCount &= GetClientEntityCount<EnableableComponent_15>();
                        clientCount &= GetClientEntityCount<EnableableComponent_16>();
                        clientCount &= GetClientEntityCount<EnableableComponent_17>();
                        clientCount &= GetClientEntityCount<EnableableComponent_18>();
                        clientCount &= GetClientEntityCount<EnableableComponent_19>();
                        clientCount &= GetClientEntityCount<EnableableComponent_20>();
                        clientCount &= GetClientEntityCount<EnableableComponent_21>();
                        clientCount &= GetClientEntityCount<EnableableComponent_22>();
                        clientCount &= GetClientEntityCount<EnableableComponent_23>();
                        clientCount &= GetClientEntityCount<EnableableComponent_24>();
                        clientCount &= GetClientEntityCount<EnableableComponent_25>();
                        clientCount &= GetClientEntityCount<EnableableComponent_26>();
                        clientCount &= GetClientEntityCount<EnableableComponent_27>();
                        clientCount &= GetClientEntityCount<EnableableComponent_28>();
                        clientCount &= GetClientEntityCount<EnableableComponent_29>();
                        clientCount &= GetClientEntityCount<EnableableComponent_30>();
                        clientCount &= GetClientEntityCount<EnableableComponent_31>();
                        clientCount &= GetClientEntityCount<EnableableComponent_32>();
                        break;
                    case GhostTypeConverter.GhostTypes.EnableableBuffer:
                        clientCount = GetClientEntityCount<EnableableBuffer>();
                        break;
                    case GhostTypeConverter.GhostTypes.MultipleEnableableBuffer:
                        clientCount = GetClientEntityCount<EnableableBuffer_0>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_1>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_2>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_3>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_4>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_5>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_6>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_7>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_8>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_9>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_10>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_11>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_12>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_13>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_14>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_15>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_16>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_17>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_18>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_19>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_20>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_21>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_22>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_23>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_24>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_25>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_26>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_27>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_28>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_29>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_30>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_31>();
                        clientCount &= GetClientEntityCount<EnableableBuffer_32>();
                        break;
                    case GhostTypeConverter.GhostTypes.ChildComponent:
                        clientCount = GetClientEntityCount<TopLevelGhostEntity>();
                        break;
                    case GhostTypeConverter.GhostTypes.ChildBufferComponent:
                        clientCount = GetClientEntityCount<TopLevelGhostEntity>();
                        break;
                    case GhostTypeConverter.GhostTypes.GhostGroup:
                        clientCount = GetClientEntityCount<GhostGroupRoot>();
                        clientCount += GetClientEntityCount<GhostChildEntityComponent>();
                        break;
                    default:
                        Assert.True(true);
                        break;
                }
                Assert.AreNotEqual(framesElapsed, 256, "Took way to long");
            } while (clientCount != serverEntities.Length);

            return framesElapsed * 2;
        }

        void TickMultipleFrames(int count)
        {
            Assert.True(count < 256);
            for (int i = 0; i < count; ++i)
            {
                testWorld.Tick(frameTime);
            }
        }

        void SetLinkedBufferValues<T>(int value, bool enabled)
            where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            foreach (var entity in serverEntities)
            {
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                Assert.AreEqual(2, serverEntityGroup.Length);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[0].Value, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[0].Value) == enabled);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[1].Value, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[1].Value) == enabled);

                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(entity);

                serverBuffer.ResizeUninitialized(kBufferSize);
                for (int i = 0; i < kBufferSize; ++i)
                {
                    var newValue = new T();
                    newValue.SetValue((i + 1) * 1000 + value);

                    serverBuffer[i] = newValue;
                }
            }
        }

        void SetGhostGroupValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            for (int i = 0; i < serverEntities.Length; i += 2)
            {
                var rootEntity = serverEntities[i];
                var childEntity = serverEntities[i+1];

                Assert.True(testWorld.ServerWorld.EntityManager.HasComponent<GhostGroupRoot>(rootEntity));
                Assert.True(testWorld.ServerWorld.EntityManager.HasComponent<GhostChildEntityComponent>(childEntity));

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(rootEntity, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(rootEntity) == enabled);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(childEntity, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(childEntity) == enabled);

                var newValue = new T();
                newValue.SetValue(value);

                testWorld.ServerWorld.EntityManager.SetComponentData(rootEntity, newValue);
                testWorld.ServerWorld.EntityManager.SetComponentData(childEntity, newValue);
            }
        }

        void SetLinkedComponentValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            foreach (var entity in serverEntities)
            {
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                Assert.AreEqual(2, serverEntityGroup.Length);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[0].Value, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[0].Value) == enabled);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[1].Value, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[1].Value) == enabled);

                var newValue = new T();
                newValue.SetValue(value);

                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[0].Value, newValue);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[1].Value, newValue);
            }
        }

        void SetComponentValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            foreach (var entity in serverEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(entity, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity) == enabled);

                var newValue = new T();
                newValue.SetValue(value);

                testWorld.ServerWorld.EntityManager.SetComponentData(entity, newValue);
            }
        }

        private void SetBufferValues<T>(int value, bool enabled) where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            foreach (var entity in serverEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(entity, enabled);
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity) == enabled);

                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(entity);

                serverBuffer.ResizeUninitialized(kBufferSize);
                for (int i = 0; i < kBufferSize; ++i)
                {
                    var newValue = new T();
                    newValue.SetValue((i + 1) * 1000 + value);

                    serverBuffer[i] = newValue;
                }
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity) == enabled);
            }
        }

        void VerifyGhostGroupValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            var rootType = ComponentType.ReadOnly<GhostGroupRoot>();
            var childType = ComponentType.ReadOnly<GhostChildEntityComponent>();

            var rootQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(rootType);
            var childQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(childType);

            using (var clientRootEntities = rootQuery.ToEntityArray(Allocator.TempJob))
            using (var clientChildEntities = childQuery.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(clientRootEntities.Length, clientChildEntities.Length);
                Assert.AreEqual(serverEntities.Length, clientChildEntities.Length + clientRootEntities.Length);

                for (int i = 0; i < clientRootEntities.Length; i++)
                {
                    var clientRootEntity = clientRootEntities[i];
                    var clientChildEntity = clientChildEntities[i];

                    var ent0 = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientRootEntity);
                    var ent1 = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientChildEntity);

                    Assert.True(enabled == ent0);
                    Assert.True(enabled == ent1);

                    Assert.AreEqual(value, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientRootEntity).GetValue());
                    Assert.AreEqual(value, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientChildEntity).GetValue());
                }
            }

        }

        void VerifyLinkedBufferValues<T>(int value, bool enabled, SendForChildrenTestCase sendForChildrenTestCase)
            where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            var type = ComponentType.ReadOnly<TopLevelGhostEntity>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(serverEntities.Length, clientEntities.Length);

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntities[i];

                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity));

                    var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(2, clientEntityGroup.Length);

                    var ent0 = testWorld.ClientWorlds[0].EntityManager
                        .IsComponentEnabled<T>(clientEntityGroup[0].Value);
                    var ent1 = testWorld.ClientWorlds[0].EntityManager
                        .IsComponentEnabled<T>(clientEntityGroup[1].Value);

                    Assert.True(enabled == ent0);

                    var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntity);
                    var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntity);

                    Assert.AreEqual(kBufferSize, serverBuffer.Length);
                    Assert.IsTrue(clientBuffer.Length == serverBuffer.Length);

                    if (sendForChildrenTestCase != SendForChildrenTestCase.NoViaDontSerializeVariantDefault)
                    {
                        Assert.True(enabled == ent1);
                        for (int j = 0; j < serverBuffer.Length; ++j)
                        {
                            var serverValue = serverBuffer[j];
                            var clientValue = clientBuffer[j];

                            var bufferValue = ((j + 1) * 1000 + value);
                            Assert.AreEqual(bufferValue, serverValue.GetValue());
                            Assert.AreEqual(bufferValue, clientValue.GetValue());
                        }
                    }
                    else
                    {
                        // TODO: Determine if enable-bits should be serialized when using `DontSerializeAttribute`. Sometimes they are?
                        //Assert.IsFalse(enabled == ent1);
                        for (int j = 0; j < clientBuffer.Length; ++j)
                        {
                            var serverValue = serverBuffer[j];
                            var clientValue = clientBuffer[j];

                            var bufferValue = ((j + 1) * 1000 + value);
                            Assert.AreEqual(bufferValue, serverValue.GetValue());
                            Assert.AreEqual(bufferValue, clientValue.GetValue());
                        }
                    }
                }
            }

        }

        void VerifyLinkedComponentValues<T>(int value, bool enabled, SendForChildrenTestCase sendForChildrenTestCase)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            var type = ComponentType.ReadOnly<TopLevelGhostEntity>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(serverEntities.Length, clientEntities.Length);

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntities[i];

                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity));

                    var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(2, clientEntityGroup.Length);

                    var ent0 = testWorld.ClientWorlds[0].EntityManager
                        .IsComponentEnabled<T>(clientEntityGroup[0].Value);
                    var ent1 = testWorld.ClientWorlds[0].EntityManager
                        .IsComponentEnabled<T>(clientEntityGroup[1].Value);

                    Assert.True(enabled == ent0);
                    Assert.AreEqual(value, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[0].Value).GetValue());

                    if (sendForChildrenTestCase != SendForChildrenTestCase.NoViaDontSerializeVariantDefault)
                    {
                        Assert.True(enabled == ent1, $"Expected that the enable-bit on components on child entities ARE serialized when using `{sendForChildrenTestCase}`!");
                        Assert.AreEqual(value, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue(), "Expected that value on component on child entities ARE serialized when using this `sendForChildrenTestCase`!");
                    }
                    else
                    {
                        // TODO: Determine if enable-bits should be serialized when using `DontSerializeAttribute`. Sometimes they are?
                        //Assert.False(enabled == ent1, $"Expected that the enable-bit on components on child entities are NOT serialized by default (via this `{sendForChildrenTestCase}`)!");
                        Assert.AreEqual(0, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue(), "Expected that value on components on child entities are not serialized by default (via this `sendForChildrenTestCase`)!");
                    }
                }
            }
        }

        void VerifyComponentValues<T>(int value, bool enabled) where T: unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            var type = ComponentType.ReadOnly<T>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                var totalEntities = query.CalculateEntityCountWithoutFiltering();
                Assert.True(clientEntities.Length != totalEntities ? !enabled : enabled);
                Assert.AreEqual(serverEntities.Length, totalEntities);

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntities[i];

                    var isServerEnabled = testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                    var isClientEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                    Assert.AreEqual(enabled, isClientEnabled);
                    Assert.AreEqual(enabled, isServerEnabled);

                    var serverValue = testWorld.ServerWorld.EntityManager.GetComponentData<T>(serverEntity).GetValue();
                    var clientValue = testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntity).GetValue();

                    Assert.AreEqual(value, serverValue);
                    Assert.AreEqual(value, clientValue);
                }
            }
        }

        private NativeArray<ArchetypeChunk> chunkArray;

        void CheckBufferValues<T>(bool checkClients, bool enabledBit)
            where T : struct, IBufferElementData, IEnableableComponent, IComponentValue
        {
            var serverType = ComponentType.ReadOnly<T>();
            var serverQuery  = testWorld.ServerWorld.EntityManager.CreateEntityQuery(serverType);
            var entities = serverQuery.ToEntityArray(Allocator.TempJob);

            int i = 0;
            foreach (var entity in this.serverEntities)
            {
                var mgr = testWorld.ServerWorld.EntityManager;

                if (chunkArray[i++] != mgr.GetChunk(entity))
                    Debug.Log($"the chunk has changed");

                var enabled = mgr.IsComponentEnabled<T>(entity);
                if (enabledBit != enabled)
                    Debug.Log($"component enabled is wrong. should be [{enabledBit}] but is [{enabled}]");

            }

            foreach (var serverEntity in entities)
            {
                if (serverEntity == Entity.Null)
                    Debug.Log($"query found a server entity that was NULL.");
            }
            if (!checkClients)
                return;

            var clientType = ComponentType.ReadOnly<T>();
            var clientQuery  = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(clientType);
            var clientEntities = clientQuery.ToEntityArray(Allocator.TempJob);

            foreach (var clientEntity in clientEntities)
            {
                if (clientEntity == Entity.Null)
                    Debug.Log($"found a server entity that was NULL.");
            }
        }

        void VerifyBufferValues<T>(int value, bool enabled) where T: unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            var type = ComponentType.ReadOnly<T>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                var totalEntities = query.CalculateEntityCountWithoutFiltering();
                Assert.True(clientEntities.Length != totalEntities ? !enabled : enabled);
                Assert.AreEqual(serverEntities.Length, totalEntities);

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntities[i];

                    var isServerEnabled = testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                    var isClientEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                    Assert.AreEqual(enabled, isClientEnabled);
                    Assert.AreEqual(enabled, isServerEnabled);

                    var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntity);
                    var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntity);

                    Assert.AreEqual(kBufferSize, serverBuffer.Length);
                    Assert.AreEqual(serverBuffer.Length, clientBuffer.Length);

                    for (int j = 0; j < serverBuffer.Length; ++j)
                    {
                        var serverValue = serverBuffer[j];
                        var clientValue = clientBuffer[j];

                        var bufferValue = ((j + 1) * 1000 + value);
                        Assert.AreEqual(bufferValue, serverValue.GetValue());
                        Assert.AreEqual(bufferValue, clientValue.GetValue());
                    }
                }
            }
        }

        void SetGhostValues(int value, bool enabled = false)
        {
            Assert.IsTrue(serverEntities.IsCreated);
            switch (type)
            {
                case GhostTypeConverter.GhostTypes.EnableableComponent:
                    SetComponentValues<EnableableComponent>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.MultipleEnableableComponent:
                    SetComponentValues<EnableableComponent_0>(value, enabled);
                    SetComponentValues<EnableableComponent_1>(value, enabled);
                    SetComponentValues<EnableableComponent_2>(value, enabled);
                    SetComponentValues<EnableableComponent_3>(value, enabled);
                    SetComponentValues<EnableableComponent_4>(value, enabled);
                    SetComponentValues<EnableableComponent_5>(value, enabled);
                    SetComponentValues<EnableableComponent_6>(value, enabled);
                    SetComponentValues<EnableableComponent_7>(value, enabled);
                    SetComponentValues<EnableableComponent_8>(value, enabled);
                    SetComponentValues<EnableableComponent_9>(value, enabled);
                    SetComponentValues<EnableableComponent_10>(value, enabled);
                    SetComponentValues<EnableableComponent_11>(value, enabled);
                    SetComponentValues<EnableableComponent_12>(value, enabled);
                    SetComponentValues<EnableableComponent_13>(value, enabled);
                    SetComponentValues<EnableableComponent_14>(value, enabled);
                    SetComponentValues<EnableableComponent_15>(value, enabled);
                    SetComponentValues<EnableableComponent_16>(value, enabled);
                    SetComponentValues<EnableableComponent_17>(value, enabled);
                    SetComponentValues<EnableableComponent_18>(value, enabled);
                    SetComponentValues<EnableableComponent_19>(value, enabled);
                    SetComponentValues<EnableableComponent_20>(value, enabled);
                    SetComponentValues<EnableableComponent_21>(value, enabled);
                    SetComponentValues<EnableableComponent_22>(value, enabled);
                    SetComponentValues<EnableableComponent_23>(value, enabled);
                    SetComponentValues<EnableableComponent_24>(value, enabled);
                    SetComponentValues<EnableableComponent_25>(value, enabled);
                    SetComponentValues<EnableableComponent_26>(value, enabled);
                    SetComponentValues<EnableableComponent_27>(value, enabled);
                    SetComponentValues<EnableableComponent_28>(value, enabled);
                    SetComponentValues<EnableableComponent_29>(value, enabled);
                    SetComponentValues<EnableableComponent_30>(value, enabled);
                    SetComponentValues<EnableableComponent_31>(value, enabled);
                    SetComponentValues<EnableableComponent_32>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.EnableableBuffer:
                    SetBufferValues<EnableableBuffer>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.MultipleEnableableBuffer:
                    SetBufferValues<EnableableBuffer_0>(value, enabled);
                    SetBufferValues<EnableableBuffer_1>(value, enabled);
                    SetBufferValues<EnableableBuffer_2>(value, enabled);
                    SetBufferValues<EnableableBuffer_3>(value, enabled);
                    SetBufferValues<EnableableBuffer_4>(value, enabled);
                    SetBufferValues<EnableableBuffer_5>(value, enabled);
                    SetBufferValues<EnableableBuffer_6>(value, enabled);
                    SetBufferValues<EnableableBuffer_7>(value, enabled);
                    SetBufferValues<EnableableBuffer_8>(value, enabled);
                    SetBufferValues<EnableableBuffer_9>(value, enabled);
                    SetBufferValues<EnableableBuffer_10>(value, enabled);
                    SetBufferValues<EnableableBuffer_11>(value, enabled);
                    SetBufferValues<EnableableBuffer_12>(value, enabled);
                    SetBufferValues<EnableableBuffer_13>(value, enabled);
                    SetBufferValues<EnableableBuffer_14>(value, enabled);
                    SetBufferValues<EnableableBuffer_15>(value, enabled);
                    SetBufferValues<EnableableBuffer_16>(value, enabled);
                    SetBufferValues<EnableableBuffer_17>(value, enabled);
                    SetBufferValues<EnableableBuffer_18>(value, enabled);
                    SetBufferValues<EnableableBuffer_19>(value, enabled);
                    SetBufferValues<EnableableBuffer_20>(value, enabled);
                    SetBufferValues<EnableableBuffer_21>(value, enabled);
                    SetBufferValues<EnableableBuffer_22>(value, enabled);
                    SetBufferValues<EnableableBuffer_23>(value, enabled);
                    SetBufferValues<EnableableBuffer_24>(value, enabled);
                    SetBufferValues<EnableableBuffer_25>(value, enabled);
                    SetBufferValues<EnableableBuffer_26>(value, enabled);
                    SetBufferValues<EnableableBuffer_27>(value, enabled);
                    SetBufferValues<EnableableBuffer_28>(value, enabled);
                    SetBufferValues<EnableableBuffer_29>(value, enabled);
                    SetBufferValues<EnableableBuffer_30>(value, enabled);
                    SetBufferValues<EnableableBuffer_31>(value, enabled);
                    SetBufferValues<EnableableBuffer_32>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildComponent:
                    SetLinkedComponentValues<EnableableComponent>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildBufferComponent:
                    SetLinkedBufferValues<EnableableBuffer>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.GhostGroup:
                    SetGhostGroupValues<EnableableComponent>(value, enabled);
                    break;
                default:
                    Assert.True(true);
                    break;
            }
        }

        void CheckBuffers(bool checkClient, bool enableBit)
        {
            switch (type)
            {
                case GhostTypeConverter.GhostTypes.MultipleEnableableBuffer:
                    CheckBufferValues<EnableableBuffer_0>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_1>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_2>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_3>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_4>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_5>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_6>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_7>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_8>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_9>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_10>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_11>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_12>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_13>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_14>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_15>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_16>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_17>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_18>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_19>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_20>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_21>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_22>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_23>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_24>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_25>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_26>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_27>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_28>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_29>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_30>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_31>(checkClient, enableBit);
                    CheckBufferValues<EnableableBuffer_32>(checkClient, enableBit);
                    break;
                default:
                    Assert.True(true);
                    break;
            }
        }
        void VerifyGhostValues(int value, bool enabled, SendForChildrenTestCase sendForChildrenTestCase)
        {
            Assert.IsTrue(serverEntities.IsCreated);
            switch (type)
            {
                case GhostTypeConverter.GhostTypes.EnableableComponent:
                    VerifyComponentValues<EnableableComponent>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.MultipleEnableableComponent:
                    VerifyComponentValues<EnableableComponent_0>(value, enabled);
                    VerifyComponentValues<EnableableComponent_1>(value, enabled);
                    VerifyComponentValues<EnableableComponent_2>(value, enabled);
                    VerifyComponentValues<EnableableComponent_3>(value, enabled);
                    VerifyComponentValues<EnableableComponent_4>(value, enabled);
                    VerifyComponentValues<EnableableComponent_5>(value, enabled);
                    VerifyComponentValues<EnableableComponent_6>(value, enabled);
                    VerifyComponentValues<EnableableComponent_7>(value, enabled);
                    VerifyComponentValues<EnableableComponent_8>(value, enabled);
                    VerifyComponentValues<EnableableComponent_9>(value, enabled);
                    VerifyComponentValues<EnableableComponent_10>(value, enabled);
                    VerifyComponentValues<EnableableComponent_11>(value, enabled);
                    VerifyComponentValues<EnableableComponent_12>(value, enabled);
                    VerifyComponentValues<EnableableComponent_13>(value, enabled);
                    VerifyComponentValues<EnableableComponent_14>(value, enabled);
                    VerifyComponentValues<EnableableComponent_15>(value, enabled);
                    VerifyComponentValues<EnableableComponent_16>(value, enabled);
                    VerifyComponentValues<EnableableComponent_17>(value, enabled);
                    VerifyComponentValues<EnableableComponent_18>(value, enabled);
                    VerifyComponentValues<EnableableComponent_19>(value, enabled);
                    VerifyComponentValues<EnableableComponent_20>(value, enabled);
                    VerifyComponentValues<EnableableComponent_21>(value, enabled);
                    VerifyComponentValues<EnableableComponent_22>(value, enabled);
                    VerifyComponentValues<EnableableComponent_23>(value, enabled);
                    VerifyComponentValues<EnableableComponent_24>(value, enabled);
                    VerifyComponentValues<EnableableComponent_25>(value, enabled);
                    VerifyComponentValues<EnableableComponent_26>(value, enabled);
                    VerifyComponentValues<EnableableComponent_27>(value, enabled);
                    VerifyComponentValues<EnableableComponent_28>(value, enabled);
                    VerifyComponentValues<EnableableComponent_29>(value, enabled);
                    VerifyComponentValues<EnableableComponent_30>(value, enabled);
                    VerifyComponentValues<EnableableComponent_31>(value, enabled);
                    VerifyComponentValues<EnableableComponent_32>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.EnableableBuffer:
                    VerifyBufferValues<EnableableBuffer>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.MultipleEnableableBuffer:
                    VerifyBufferValues<EnableableBuffer_0>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_1>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_2>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_3>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_4>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_5>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_6>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_7>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_8>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_9>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_10>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_11>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_12>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_13>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_14>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_15>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_16>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_17>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_18>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_19>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_20>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_21>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_22>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_23>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_24>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_25>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_26>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_27>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_28>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_29>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_30>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_31>(value, enabled);
                    VerifyBufferValues<EnableableBuffer_32>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildComponent:
                    VerifyLinkedComponentValues<EnableableComponent>(value, enabled, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.ChildBufferComponent:
                    VerifyLinkedBufferValues<EnableableBuffer>(value, enabled, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.GhostGroup:
                    VerifyGhostGroupValues<EnableableComponent>(value, enabled);
                    break;
                default:
                    Assert.True(true);
                    break;
            }
        }

        NetworkTick[] RetrieveLastTicks()
        {
            EntityQuery query = default;
            var clientManager = testWorld.ClientWorlds[0].EntityManager;
            if (this.type == GhostTypeConverter.GhostTypes.ChildBufferComponent ||
                this.type == GhostTypeConverter.GhostTypes.ChildComponent)
            {
                var type = ComponentType.ReadOnly<TopLevelGhostEntity>();
                query = clientManager.CreateEntityQuery(type);
            }
            else
            {
                var type = ComponentType.ReadOnly<GhostOwnerComponent>();
                query = clientManager.CreateEntityQuery(type);
            }

            if (query.IsEmpty)
                return default;

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(serverEntities.Length, clientEntities.Length);

                var ticks = new NetworkTick[clientEntities.Length];

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var entity = clientEntities[i];
                    var clientSnapshotBuffer = clientManager.GetBuffer<SnapshotDataBuffer>(entity);
                    var clientSnapshot = clientManager.GetComponentData<SnapshotData>(entity);
                    var lastSnapshot = clientSnapshot.GetLatestTick(clientSnapshotBuffer);

                    ticks[i] = lastSnapshot;
                }

                return ticks;
            }
        }

        private const int kBufferSize = 16;
        private NetCodeTestWorld testWorld;
        private NativeArray<Entity> serverEntities;
        private GhostTypeConverter.GhostTypes type;

        enum GhostFlags : int
        {
            None = 0,
            StaticOptimization = 1 << 0,
            PreSerialize = 1 << 2
        };

        void CreateWorldsAndSpawn(int numClients, GhostTypeConverter.GhostTypes type, int entityCount, GhostFlags flags, SendForChildrenTestCase sendForChildrenTestCase)
        {
            if(sendForChildrenTestCase == SendForChildrenTestCase.YesViaDefaultNameDictionary)
                testWorld.UserBakingSystems.Add(typeof(TestDefaultsToDefaultSerializationSystem));

            testWorld.Bootstrap(true);

            var prefabCount = 1;
            this.type = type;
            if (type == GhostTypeConverter.GhostTypes.GhostGroup)
            {
                prefabCount = 2;
            }

            GameObject[] objects = new GameObject[prefabCount];
            var objectsToAddInspectionsTo = new List<GameObject>(8);
            for (int i = 0; i < prefabCount; i++)
            {
                if (type == GhostTypeConverter.GhostTypes.GhostGroup)
                {
                    objects[i] = new GameObject("ParentGhost");
                    objects[i].AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type);
                    i++;
                    objects[i] = new GameObject("ChildGhost");
                    objects[i].AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type);

                    continue;
                }

                objects[i] = new GameObject("Root");
                objects[i].AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type);

                if (type == GhostTypeConverter.GhostTypes.ChildComponent)
                {
                    var child = new GameObject("ChildComp");
                    child.transform.parent = objects[i].transform;
                    child.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type);
                    objectsToAddInspectionsTo.Add(child);
                }
                else if (type == GhostTypeConverter.GhostTypes.ChildBufferComponent)
                {
                    var child = new GameObject("ChildBuffer");
                    child.transform.parent = objects[i].transform;
                    child.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type);
                    objectsToAddInspectionsTo.Add(child);
                }
            }

            objectsToAddInspectionsTo.AddRange(objects);
            if (sendForChildrenTestCase == SendForChildrenTestCase.YesViaInspectionComponentOverride)
            {
                var optionalOverrides = BuildComponentOverridesForComponents();
                foreach (var go in objectsToAddInspectionsTo)
                {
                    var ghostAuthoringInspectionComponent = go.AddComponent<GhostAuthoringInspectionComponent>();
                    foreach (var componentOverride in optionalOverrides)
                    {
                        ref var @override = ref ghostAuthoringInspectionComponent.AddComponentOverrideRaw();
                        @override = componentOverride;
                        @override.GameObject = go;
                        @override.EntityGuid = default;
                    }
                }
            }

            if ((flags & GhostFlags.StaticOptimization) == GhostFlags.StaticOptimization)
            {
                var ghostConfig = objects[0].AddComponent<GhostAuthoringComponent>();
                ghostConfig.OptimizationMode = GhostOptimizationMode.Static;
            }

            if ((flags & GhostFlags.PreSerialize) == GhostFlags.PreSerialize)
            {
                var ghostConfig = objects[0].AddComponent<GhostAuthoringComponent>();
                ghostConfig.UsePreSerialization = true;
            }

            Assert.IsTrue(testWorld.CreateGhostCollection(objects));
            testWorld.CreateWorlds(true, numClients);

            entityCount *= prefabCount;
            serverEntities = new NativeArray<Entity>(entityCount, Allocator.Persistent);

            var step = objects.Length;
            for (int i = 0; i < entityCount; i += step)
            {
                for (int j = 0; j < step; j++)
                {
                    serverEntities[i+j] = testWorld.SpawnOnServer(objects[j]);
                }
            }

            if (type == GhostTypeConverter.GhostTypes.GhostGroup)
            {
                for (int i = 0; i < entityCount; i += 2)
                {
                    testWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(serverEntities[i]).Add(new GhostGroup{Value = serverEntities[i+1]});
                }
            }

            if (type == GhostTypeConverter.GhostTypes.ChildComponent)
            {
                foreach (var entity in serverEntities)
                {
                    Assert.IsTrue(testWorld.ServerWorld.EntityManager.HasComponent<LinkedEntityGroup>(entity));
                }
            }

            // Connect and make sure the connection could be established
            Assert.IsTrue(testWorld.Connect(frameTime, 4));

            // Go in-game
            testWorld.GoInGame();
        }

        [SetUp]
        public void SetupTestsForEnableableBits()
        {
            testWorld = new NetCodeTestWorld();
        }

        [TearDown]
        public void TearDownTestsForEnableableBits()
        {
            if (serverEntities.IsCreated)
                serverEntities.Dispose();
            if (chunkArray.IsCreated)
                chunkArray.Dispose();
            testWorld.Dispose();
        }
        private static void ValidateTicks(NetworkTick[] ticks, NetworkTick[] tickAfterAFewFrames, bool shouldBeEqual)
        {
            Assert.True(ticks.Length == tickAfterAFewFrames.Length);
            for (int i = 0; i < ticks.Length; i++)
            {
                if (shouldBeEqual)
                    Assert.AreEqual(ticks[i], tickAfterAFewFrames[i], "Ticks should be same!");
                else Assert.AreNotEqual(ticks[i], tickAfterAFewFrames[i], "Ticks should NOT be same!");
            }
        }

        [Test]
        public void GhostsAreSerializedWithEnabledBits([Values]GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            CreateWorldsAndSpawn(1, type, count, GhostFlags.None, sendForChildrenTestCase);

            var value = -1;
            var enabled = false;

            SetGhostValues(value, enabled);
            var threshold = TickUntilReplicationIsDone(enabled);
            VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            for (int i = 0; i < 8; ++i)
            {
                enabled = !enabled;
                value = i;

                SetGhostValues(value, enabled);
                TickMultipleFrames(threshold);
                VerifyGhostValues(value, enabled, sendForChildrenTestCase);
            }
        }

        [DisableAutoCreation]
        class TestDefaultsToDefaultSerializationSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                var typesToOverride = FetchAllTestComponentTypes(GetType().Assembly);
                foreach (var type in typesToOverride)
                {
                    defaultVariants.Add(type, Rule.ForAll(type));
                }
            }
        }

        GhostAuthoringInspectionComponent.ComponentOverride[] BuildComponentOverridesForComponents()
        {
            var testTypes = FetchAllTestComponentTypes(GetType().Assembly);
            var overrides = testTypes
                .Select(x =>
                {
                    var componentTypeFullName = x.FullName;
                    return new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = componentTypeFullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = GhostVariantsUtility.UncheckedVariantHashNBC(componentTypeFullName, componentTypeFullName),
                    };
                }).ToArray();

            if (overrides.Length < 50)
                throw new InvalidOperationException("There are loads of override types!");
            return overrides;
        }

        static Type[] FetchAllTestComponentTypes(Assembly assembly)
        {
            return assembly.GetTypes()
                .Where(x => (typeof(IBufferElementData).IsAssignableFrom(x) && x.Name.StartsWith("EnableableBuffer")) || (typeof(IComponentData).IsAssignableFrom(x) && x.Name.StartsWith("EnableableComponent")))
                .ToArray();
        }

        [Test]
        public void GhostsAreSerializedWithEnabledBits_PreSerialize([Values]GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            CreateWorldsAndSpawn(1, type, count, GhostFlags.PreSerialize, sendForChildrenTestCase);

            var value = -1;
            var enabled = false;

            SetGhostValues(value, enabled);
            var threshold = TickUntilReplicationIsDone(enabled);
            VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            for (int i = 0; i < 8; ++i)
            {
                enabled = !enabled;
                value = i;

                SetGhostValues(value, enabled);
                TickMultipleFrames(threshold);
                VerifyGhostValues(value, enabled, sendForChildrenTestCase);
            }
        }

        [Test]
        public void GhostsAreSerializedWithEnabledBits_StaticOptimize(
            [Values (GhostTypeConverter.GhostTypes.EnableableComponent, GhostTypeConverter.GhostTypes.EnableableBuffer,
                    GhostTypeConverter.GhostTypes.MultipleEnableableComponent, GhostTypeConverter.GhostTypes.MultipleEnableableBuffer)]
            GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            // Just making sure we dont run with groups or children as they do not support static optimization.
            if (type == GhostTypeConverter.GhostTypes.GhostGroup ||
                type == GhostTypeConverter.GhostTypes.ChildComponent ||
                type == GhostTypeConverter.GhostTypes.ChildBufferComponent) return;

            CreateWorldsAndSpawn(1, type, count, GhostFlags.StaticOptimization, sendForChildrenTestCase);

            var value = -1;
            var enabled = false;

            SetGhostValues(value, enabled);
            var threshold = TickUntilReplicationIsDone(enabled);
            VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            var ticks = RetrieveLastTicks();

            TickMultipleFrames(threshold);
            ValidateTicks(ticks, RetrieveLastTicks(), true);

            value = 21;

            SetGhostValues(value, enabled);
            TickMultipleFrames(threshold);

            VerifyGhostValues(value, enabled, sendForChildrenTestCase);
            ValidateTicks(ticks, RetrieveLastTicks(), false);

            for (int i = 0; i < 8; ++i)
            {
                ticks = RetrieveLastTicks();
                enabled = !enabled;
                value = i;

                SetGhostValues(value, enabled);
                TickMultipleFrames(threshold);
                VerifyGhostValues(value, enabled, sendForChildrenTestCase);

                ValidateTicks(ticks, RetrieveLastTicks(), false);
            }
        }
    }
}
