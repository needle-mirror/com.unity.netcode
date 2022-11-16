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

        void TickMultipleFrames()
        {
            for (int i = 0; i < 20; ++i)
            {
                testWorld.Tick(frameTime);
            }
        }

        void SetLinkedBufferValues<T>(int value, bool enabled)
            where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            foreach (var serverEntity in serverEntities)
            {
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
                Assert.AreEqual(2, serverEntityGroup.Length);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[0].Value, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[0].Value), $"{typeof(T)} is set correctly on server, linked[0]");

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[1].Value, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[1].Value), $"{typeof(T)} is set correctly on server, linked[1]");

                SetupBuffer(testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntityGroup[0].Value));
                SetupBuffer(testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntityGroup[1].Value));

                void SetupBuffer(DynamicBuffer<T> buffer)
                {
                    buffer.ResizeUninitialized(kServerBufferSize);
                    for (int i = 0; i < kServerBufferSize; ++i)
                    {
                        var newValue = new T();
                        newValue.SetValue((i + 1) * 1000 + value);
                        buffer[i] = newValue;
                    }
                }
            }
        }

        void SetGhostGroupValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            SetGhostGroupEnabled<T>(enabled);
            for (int i = 0; i < serverEntities.Length; i += 2)
            {
                var rootEntity = serverEntities[i];
                var childEntity = serverEntities[i + 1];
                T newValue = default;
                newValue.SetValue(value);
                testWorld.ServerWorld.EntityManager.SetComponentData(rootEntity, newValue);
                testWorld.ServerWorld.EntityManager.SetComponentData(childEntity, newValue);
            }
        }

        void SetGhostGroupEnabled<T>(bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent
        {
            for (int i = 0; i < serverEntities.Length; i += 2)
            {
                var rootEntity = serverEntities[i];
                var childEntity = serverEntities[i + 1];

                Assert.True(testWorld.ServerWorld.EntityManager.HasComponent<GhostGroupRoot>(rootEntity));
                Assert.True(testWorld.ServerWorld.EntityManager.HasComponent<GhostChildEntityComponent>(childEntity));

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(rootEntity, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(rootEntity), $"{typeof(T)} is set correctly on server, root entity");

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(childEntity, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(childEntity), $"{typeof(T)} is set correctly on server, child entity");
            }
        }

        void SetLinkedComponentValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            SetLinkedComponentEnabled<T>(enabled);
            foreach (var entity in serverEntities)
            {
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                T newValue = default;
                newValue.SetValue(value);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[0].Value, newValue);
                testWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[1].Value, newValue);
            }
        }

        void SetLinkedComponentEnabled<T>(bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent
        {
            foreach (var entity in serverEntities)
            {
                var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                Assert.AreEqual(2, serverEntityGroup.Length);

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[0].Value, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[0].Value), $"{typeof(T)} is set correctly on server, linked[0]");

                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[1].Value, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[1].Value), $"{typeof(T)} is set correctly on server, linked[1]");
            }
        }

        void SetComponentValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            SetComponentEnabled<T>(enabled);
            foreach (var entity in serverEntities)
            {
                T newValue = default;
                newValue.SetValue(value);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, newValue);
            }
        }

        void SetComponentEnabled<T>(bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent
        {
            foreach (var entity in serverEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(entity, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity), $"{typeof(T)} is set correctly on server");
            }
        }

        void SetBufferValues<T>(int value, bool enabled) where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            foreach (var entity in serverEntities)
            {
                testWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(entity, enabled);
                Assert.AreEqual(enabled, testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity), $"{typeof(T)} buffer is set correctly on server");

                var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(entity);

                serverBuffer.ResizeUninitialized(kServerBufferSize);
                for (int i = 0; i < kServerBufferSize; ++i)
                {
                    var newValue = new T();
                    newValue.SetValue((i + 1) * 1000 + value);

                    serverBuffer[i] = newValue;
                }
                Assert.True(testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity) == enabled);
            }
        }

        void VerifyGhostGroupValues<T>(int expectedValue, bool expectedEnabled, SendForChildrenTestCase sendForChildrenTestCase)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            VerifyGhostGroupEnabledBits<T>(expectedEnabled, sendForChildrenTestCase);

            var rootType = ComponentType.ReadOnly<GhostGroupRoot>();
            var childType = ComponentType.ReadOnly<GhostChildEntityComponent>();

            var rootQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(rootType);
            var childQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(childType);

            using (var clientRootEntities = rootQuery.ToEntityArray(Allocator.TempJob))
            using (var clientChildEntities = childQuery.ToEntityArray(Allocator.TempJob))
            {
                for (int i = 0; i < clientRootEntities.Length; i++)
                {
                    var clientGroupRootEntity = clientRootEntities[i];
                    var clientMemberEntity = clientChildEntities[i];
                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true)) // Ghost groups are root entities, by definition.
                    {
                        Assert.AreEqual(expectedValue, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientGroupRootEntity).GetValue(), $"[{typeof(T)}] ghost \"group root\" entity value IS replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(expectedValue, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientMemberEntity).GetValue(), $"[{typeof(T)}] ghost \"group member\" entity value when {sendForChildrenTestCase}");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultValueIfNotReplicated, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientGroupRootEntity).GetValue(), $"[{typeof(T)}] ghost \"group root\" entity value is NOT replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(kDefaultValueIfNotReplicated, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientMemberEntity).GetValue(), $"[{typeof(T)}] ghost \"group member\" entity value is NOT replicated when {sendForChildrenTestCase}");
                    }
                }
            }
        }

        void VerifyGhostGroupEnabledBits<T>(bool expectedEnabled, SendForChildrenTestCase sendForChildrenTestCase)
            where T : unmanaged, IComponentData, IEnableableComponent
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
                    var clientGroupRootEntity = clientRootEntities[i];
                    var clientGroupMemberEntity = clientChildEntities[i];

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true)) // Ghost groups are root entities, by definition.
                    {
                        Assert.AreEqual(expectedEnabled, testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientGroupRootEntity), $"[{typeof(T)}] ghost \"group root\" entity enabled IS replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(expectedEnabled, testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientGroupMemberEntity), $"[{typeof(T)}] ghost \"group member\" entity enabled IS replicated when {sendForChildrenTestCase}");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientGroupRootEntity), $"[{typeof(T)}] ghost \"group root\" entity enabled NOT replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(kDefaultIfNotReplicated, testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientGroupMemberEntity), $"[{typeof(T)}] ghost \"group member\" entity enabled NOT replicated when {sendForChildrenTestCase}");
                    }
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


                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity), "client linked group");

                    var serverEntityGroup = testWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity);
                    var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(2, clientEntityGroup.Length, "client linked group, expecting parent + child");

                    var clientParentEntityComponentEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[0].Value);
                    var clientChildEntityComponentEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[1].Value);

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        Assert.AreEqual(enabled, clientParentEntityComponentEnabled, $"[{typeof(T)}] client parent entity component enabled bit IS replicated when {sendForChildrenTestCase}");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, clientParentEntityComponentEnabled, $"[{typeof(T)}] client parent entity component enabled bit NOT replicated when {sendForChildrenTestCase}");
                    }

                    var serverParentBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntityGroup[0].Value);
                    var serverChildBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntityGroup[1].Value);
                    var clientParentBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntityGroup[0].Value);
                    var clientChildBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntityGroup[1].Value);

                    // TODO: Make parent and child sizes different as further validation!
                    Assert.AreEqual(kServerBufferSize, serverParentBuffer.Length, $"[{typeof(T)}] server parent buffer length");
                    Assert.AreEqual(kServerBufferSize, serverChildBuffer.Length, $"[{typeof(T)}] server child buffer length");

                    // Root:

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        Assert.AreEqual(kServerBufferSize, clientParentBuffer.Length, $"[{typeof(T)}] client parent buffer length IS replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(enabled, clientParentEntityComponentEnabled, $"[{typeof(T)}] client parent buffer enable bit IS replicated when {sendForChildrenTestCase}");
                    }
                    else
                    {
                        Assert.AreEqual(kClientBufferSize, clientParentBuffer.Length, $"[{typeof(T)}] client parent buffer length NOT replicated when {sendForChildrenTestCase}, so using default client buffer length");
                        Assert.AreEqual(kDefaultIfNotReplicated, clientParentEntityComponentEnabled, $"[{typeof(T)}] client parent buffer enable bit NOT replicated when {sendForChildrenTestCase}");
                    }

                    for (int j = 0; j < serverParentBuffer.Length; ++j)
                    {
                        var serverValue = serverParentBuffer[j];
                        var clientValue = clientParentBuffer[j];

                        var bufferValue = ((j + 1) * 1000 + value);
                        Assert.AreEqual(bufferValue, serverValue.GetValue(), $"[{typeof(T)}] server parent value is written [{i}]");
                        if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                        {
                            Assert.AreEqual(bufferValue, clientValue.GetValue(), $"[{typeof(T)}] client parent value [{i}] expecting IS replicated when {sendForChildrenTestCase}");
                        }
                        else
                        {
                            Assert.AreEqual(kDefaultValueIfNotReplicated, clientValue.GetValue(), $"[{typeof(T)}] client parent value [{i}] expecting NOT replicated when {sendForChildrenTestCase}");
                        }
                    }

                    // Children:
                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, false))
                    {
                        Assert.AreEqual(kServerBufferSize, clientChildBuffer.Length, $"[{typeof(T)}] client child buffer length IS replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(enabled, clientChildEntityComponentEnabled, $"[{typeof(T)}] client child buffer enable bit IS replicated when {sendForChildrenTestCase}");
                    }
                    else
                    {
                        Assert.AreEqual(kClientBufferSize, clientChildBuffer.Length, $"[{typeof(T)}] client child buffer length NOT replicated when {sendForChildrenTestCase}, so will use the default client buffer length");
                        Assert.AreEqual(kDefaultIfNotReplicated, clientChildEntityComponentEnabled, $"[{typeof(T)}] client child buffer enable bit NOT replicated when {sendForChildrenTestCase}");
                    }
                    for (int j = 0; j < serverChildBuffer.Length; ++j)
                    {
                        var serverValue = serverChildBuffer[j];
                        var clientValue = clientChildBuffer[j];

                        var bufferValue = ((j + 1) * 1000 + value);
                        Assert.AreEqual(bufferValue, serverValue.GetValue(), $"[{typeof(T)}] client child value is written [{i}]");

                        if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, false))
                        {
                            Assert.AreEqual(bufferValue, clientValue.GetValue(), $"[{typeof(T)}] client child entity buffer value [{i}] expecting IS replicated when {sendForChildrenTestCase}");
                        }
                        else
                        {
                            Assert.AreEqual(kDefaultValueIfNotReplicated, clientValue.GetValue(), $"[{typeof(T)}] client parent value [{i}] expecting NOT replicated when {sendForChildrenTestCase}");
                        }
                    }
                }
            }
        }

        void VerifyLinkedComponentValues<T>(int expectedValue, bool expectedEnabled, SendForChildrenTestCase sendForChildrenTestCase)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            VerifyLinkedComponentEnabled<T>(expectedEnabled, sendForChildrenTestCase);

            var type = ComponentType.ReadOnly<TopLevelGhostEntity>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);
            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(serverEntities.Length, clientEntities.Length);

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var clientEntity = clientEntities[i];

                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity));

                    var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(2, clientEntityGroup.Length, "Client entity count should always be correct.");

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        Assert.AreEqual(expectedValue, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[0].Value).GetValue(), $"[{typeof(T)}] Expected that value on component on root entity [{i}] IS replicated correctly when using this `{sendForChildrenTestCase}`!");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultValueIfNotReplicated, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue(), $"[{typeof(T)}] Expected that value on component on root entity [{i}] is NOT replicated by default (via this `{sendForChildrenTestCase}`)!");
                    }

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, false))
                    {
                        Assert.AreEqual(expectedValue, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue(), $"[{typeof(T)}] Expected that value on component on child entity [{i}] IS replicated when using this `{sendForChildrenTestCase}`!");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultValueIfNotReplicated, testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue(), $"[{typeof(T)}] Expected that value on component on child entity [{i}] is NOT replicated by default (via this `{sendForChildrenTestCase}`)!");
                    }
                }
            }
        }

        void VerifyLinkedComponentEnabled<T>(bool expectedEnabled, SendForChildrenTestCase sendForChildrenTestCase)
            where T : unmanaged, IComponentData, IEnableableComponent
        {
            var type = ComponentType.ReadOnly<TopLevelGhostEntity>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(serverEntities.Length, clientEntities.Length, $"[{typeof(T)}] Client has entities with this component.");

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var clientEntity = clientEntities[i];

                    Assert.IsTrue(testWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity), $"[{typeof(T)}] Client has entities with the LinkedEntityGroup.");

                    var clientEntityGroup = testWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity);
                    Assert.AreEqual(2, clientEntityGroup.Length, $"[{typeof(T)}] Entities in the LinkedEntityGroup!");

                    var rootEntityEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[0].Value);
                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        Assert.AreEqual(expectedEnabled, rootEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on root entity [{i}] is replicated when using `{sendForChildrenTestCase}`!");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, rootEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on root entity [{i}] is NOT replicated by default when using `{sendForChildrenTestCase}`!");
                    }

                    var childEntityEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[1].Value);
                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, false))
                    {
                        Assert.AreEqual(expectedEnabled, childEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on child entity [{i}] is replicated when using `{sendForChildrenTestCase}`!");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, childEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on child entity [{i}] is NOT replicated by default when using `{sendForChildrenTestCase}`!");
                    }
                }
            }
        }

        void VerifyComponentValues<T>(int expectedServerValue, int expectedClientValue, bool expectedServerEnabled, bool expectedClientEnabled, SendForChildrenTestCase sendForChildrenTestCase) where T: unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<T>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(builder);

            using (var clientEntitiesWithoutFiltering = query.ToEntityArray(Allocator.TempJob))
            {
                Assert.AreEqual(serverEntities.Length, clientEntitiesWithoutFiltering.Length, "Client entity count must match server entity count!");

                for (int i = 0; i < clientEntitiesWithoutFiltering.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntitiesWithoutFiltering[i];

                    var isServerEnabled = testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                    var isClientEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                    var serverValue = testWorld.ServerWorld.EntityManager.GetComponentData<T>(serverEntity).GetValue();
                    var clientValue = testWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntity).GetValue();
                    Assert.AreEqual(expectedServerEnabled, isServerEnabled, $"[{typeof(T)}] server enable bit [{i}]");
                    Assert.AreEqual(expectedServerValue, serverValue, $"[{typeof(T)}] server value [{i}]");

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        // Note that values are replicated even if the component is disabled!
                        Assert.AreEqual(expectedClientEnabled, isClientEnabled, $"[{typeof(T)}] client enable bit [{i}] IS replicated");
                        Assert.AreEqual(expectedClientValue, clientValue, $"[{typeof(T)}] client value [{i}] IS replicated");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, isClientEnabled, $"[{typeof(T)}] client enable bit [{i}] NOT replicated");
                        Assert.AreEqual(kDefaultValueIfNotReplicated, clientValue, $"[{typeof(T)}] client value [{i}] NOT replicated");
                    }
                }
            }
        }

        void VerifyFlagComponentEnabledBit<T>(bool expectedServerEnabled, bool expectedClientEnabled, SendForChildrenTestCase sendForChildrenTestCase) where T : unmanaged, IComponentData, IEnableableComponent
        {
            var type = ComponentType.ReadOnly<T>();
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(type);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                var totalEntities = query.CalculateEntityCountWithoutFiltering();
                Assert.AreEqual(serverEntities.Length, totalEntities);

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntities[i];

                    var isServerEnabled = testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                    var isClientEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                    Assert.AreEqual(expectedServerEnabled, isServerEnabled, $"Flag component {typeof(T)} server enabled bit is correct.");

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        Assert.AreEqual(expectedClientEnabled, isClientEnabled, $"{typeof(T)} client enabled bit IS replicated.");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, isClientEnabled, $"{typeof(T)} client enabled bit is NOT replicated.");
                    }
                }
            }
        }

        NativeArray<ArchetypeChunk> chunkArray;

        void VerifyBufferValues<T>(int value, bool enabled, SendForChildrenTestCase sendForChildrenTestCase) where T: unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll<T>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
            var query = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(builder);

            using (var clientEntities = query.ToEntityArray(Allocator.TempJob))
            {
                var totalEntities = query.CalculateEntityCountWithoutFiltering();
                Assert.AreEqual(totalEntities, clientEntities.Length, $"Client entity count should ALWAYS be correct, regardless of setting: {sendForChildrenTestCase} and {typeof(T)}");

                for (int i = 0; i < clientEntities.Length; i++)
                {
                    var serverEntity = serverEntities[i];
                    var clientEntity = clientEntities[i];

                    var isServerEnabled = testWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                    var isClientEnabled = testWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                    var serverBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntity);
                    var clientBuffer = testWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntity);

                    Assert.AreEqual(kServerBufferSize, serverBuffer.Length, $"[{typeof(T)}] server buffer length");
                    Assert.AreEqual(enabled, isServerEnabled, $"[{typeof(T)}] server enable bit");

                    if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                    {
                        Assert.AreEqual(enabled, isClientEnabled, $"[{typeof(T)}] Client enable bit IS replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(kServerBufferSize, clientBuffer.Length, $"[{typeof(T)}] Client buffer length IS replicated when {sendForChildrenTestCase}");
                    }
                    else
                    {
                        Assert.AreEqual(kDefaultIfNotReplicated, isClientEnabled, $"[{typeof(T)}] Client enable bit is NOT replicated when {sendForChildrenTestCase}");
                        Assert.AreEqual(kClientBufferSize, clientBuffer.Length, $"[{typeof(T)}] Client buffer length should NOT be replicated when {sendForChildrenTestCase}, thus should be the default CLIENT value");
                    }

                    for (int j = 0; j < serverBuffer.Length; ++j)
                    {
                        var serverValue = serverBuffer[j];
                        var clientValue = clientBuffer[j];

                        var expectedBufferValue = ((j + 1) * 1000 + value);
                        Assert.AreEqual(expectedBufferValue, serverValue.GetValue(), $"[{typeof(T)}] server buffer value [{i}]");

                        if (BootstrapTests.IsExpectedToBeReplicated(sendForChildrenTestCase, true))
                            Assert.AreEqual(expectedBufferValue, clientValue.GetValue(), $"[{typeof(T)}] client buffer value [{i}] IS replicated when {sendForChildrenTestCase}");
                        else Assert.AreEqual(kDefaultValueIfNotReplicated, clientValue.GetValue(), $"[{typeof(T)}] client buffer value [{i}] is NOT replicated when {sendForChildrenTestCase}");
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
                    SetComponentEnabled<EnableableFlagComponent>(enabled);
                    SetComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, enabled);
                    SetComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, enabled);
                    SetComponentValues<ComponentWithVariant>(value, enabled);
                    SetComponentValues<ComponentWithNonReplicatedVariant>(value, enabled);
                    SetComponentEnabled<NeverReplicatedEnableableFlagComponent>(enabled);
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
                    SetLinkedComponentEnabled<EnableableFlagComponent>(enabled);
                    SetLinkedComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, enabled);
                    SetLinkedComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, enabled);
                    SetLinkedComponentValues<ComponentWithVariant>(value, enabled);
                    SetLinkedComponentValues<ComponentWithNonReplicatedVariant>(value, enabled);
                    SetLinkedComponentEnabled<NeverReplicatedEnableableFlagComponent>(enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildBufferComponent:
                    SetLinkedBufferValues<EnableableBuffer>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.GhostGroup:
                    SetGhostGroupValues<EnableableComponent>(value, enabled);
                    SetGhostGroupEnabled<EnableableFlagComponent>(enabled);
                    SetGhostGroupValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, enabled);
                    SetGhostGroupValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, enabled);
                    SetGhostGroupValues<ComponentWithVariant>(value, enabled);
                    SetGhostGroupValues<ComponentWithNonReplicatedVariant>(value, enabled);
                    SetGhostGroupEnabled<NeverReplicatedEnableableFlagComponent>(enabled);
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
                    VerifyComponentValues<EnableableComponent>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyFlagComponentEnabledBit<EnableableFlagComponent>(enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, value, enabled, kDefaultIfNotReplicated, sendForChildrenTestCase);
                    VerifyComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, kDefaultValueIfNotReplicated, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<ComponentWithVariant>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<ComponentWithNonReplicatedVariant>(value, kDefaultValueIfNotReplicated, enabled, kDefaultIfNotReplicated, sendForChildrenTestCase);
                    VerifyFlagComponentEnabledBit<NeverReplicatedEnableableFlagComponent>(enabled, kDefaultIfNotReplicated, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.MultipleEnableableComponent:
                    VerifyComponentValues<EnableableComponent_1>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_2>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_3>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_4>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_5>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_6>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_7>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_8>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_9>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_10>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_11>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_12>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_13>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_14>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_15>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_16>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_17>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_18>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_19>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_20>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_21>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_22>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_23>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_24>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_25>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_26>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_27>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_28>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_29>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_30>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_31>(value, value, enabled, enabled, sendForChildrenTestCase);
                    VerifyComponentValues<EnableableComponent_32>(value, value, enabled, enabled, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.EnableableBuffer:
                    VerifyBufferValues<EnableableBuffer>(value, enabled, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.MultipleEnableableBuffer:
                    VerifyBufferValues<EnableableBuffer_0>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_1>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_2>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_3>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_4>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_5>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_6>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_7>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_8>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_9>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_10>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_11>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_12>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_13>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_14>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_15>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_16>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_17>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_18>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_19>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_20>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_21>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_22>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_23>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_24>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_25>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_26>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_27>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_28>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_29>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_30>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_31>(value, enabled, sendForChildrenTestCase);
                    VerifyBufferValues<EnableableBuffer_32>(value, enabled, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.ChildComponent:
                    VerifyLinkedComponentValues<EnableableComponent>(value, enabled, sendForChildrenTestCase);
                    VerifyLinkedComponentEnabled<EnableableFlagComponent>(enabled, sendForChildrenTestCase);
                    VerifyLinkedComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, kDefaultIfNotReplicated, sendForChildrenTestCase);
                    VerifyLinkedComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>(kDefaultValueIfNotReplicated, enabled, sendForChildrenTestCase);
                    // We override variants for these two, so cannot test their "default variants" without massive complications.
                    if (sendForChildrenTestCase != SendForChildrenTestCase.Default)
                    {
                        VerifyLinkedComponentValues<ComponentWithVariant>(value, enabled, sendForChildrenTestCase);
                        VerifyLinkedComponentValues<ComponentWithNonReplicatedVariant>(kDefaultValueIfNotReplicated, kDefaultIfNotReplicated, sendForChildrenTestCase);
                    }
                    VerifyLinkedComponentEnabled<NeverReplicatedEnableableFlagComponent>(kDefaultIfNotReplicated, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.ChildBufferComponent:
                    VerifyLinkedBufferValues<EnableableBuffer>(value, enabled, sendForChildrenTestCase);
                    break;
                case GhostTypeConverter.GhostTypes.GhostGroup:
                    // GhostGroup implies all of these are root entities! I.e. No children to worry about, so `sendForChildrenTestCase` is ignored.
                    VerifyGhostGroupValues<EnableableComponent>(value, enabled, sendForChildrenTestCase);
                    VerifyGhostGroupEnabledBits<EnableableFlagComponent>(enabled, sendForChildrenTestCase);
                    VerifyGhostGroupValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, kDefaultIfNotReplicated, sendForChildrenTestCase);
                    VerifyGhostGroupValues<ReplicatedEnableableComponentWithNonReplicatedField>(kDefaultValueIfNotReplicated, enabled, sendForChildrenTestCase);
                    // We override variants for these two, so cannot test their "default variants" without massive complications.
                    if (sendForChildrenTestCase != SendForChildrenTestCase.Default)
                    {
                        VerifyGhostGroupValues<ComponentWithVariant>(value, enabled, sendForChildrenTestCase);
                        VerifyGhostGroupEnabledBits<NeverReplicatedEnableableFlagComponent>(kDefaultIfNotReplicated, sendForChildrenTestCase);
                    }
                    break;
                default:
                    Assert.Fail();
                    break;
            }
        }

        const bool kDefaultIfNotReplicated = true;
        const int kDefaultValueIfNotReplicated = 0;

        const int kServerBufferSize = 13;
        internal const int kClientBufferSize = 29; 

        NetCodeTestWorld testWorld;
        NativeArray<Entity> serverEntities;
        GhostTypeConverter.GhostTypes type;

        enum GhostFlags : int
        {
            None = 0,
            StaticOptimization = 1 << 0,
            PreSerialize = 1 << 2
        };

        void CreateWorldsAndSpawn(int numClients, GhostTypeConverter.GhostTypes type, int entityCount, GhostFlags flags, SendForChildrenTestCase sendForChildrenTestCase)
        {
            switch (sendForChildrenTestCase)
            {
                case SendForChildrenTestCase.YesViaDefaultVariantMap:
                    testWorld.UserBakingSystems.Add(typeof(ForceSerializeSystem));
                    break;
                case SendForChildrenTestCase.NoViaDefaultVariantMap:
                    testWorld.UserBakingSystems.Add(typeof(ForceDontSerializeSystem));
                    break;
            }

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

        [Test]
        public void GhostsAreSerializedWithEnabledBits([Values]GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            CreateWorldsAndSpawn(1, type, count, GhostFlags.None, sendForChildrenTestCase);

            var value = -1;
            var enabled = false;

            SetGhostValues(value, enabled);
            TickMultipleFrames();
                VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            for (int i = 0; i < 8; ++i)
            {
                enabled = !enabled;
                value = i;

                SetGhostValues(value, enabled);
                TickMultipleFrames();
                VerifyGhostValues(value, enabled, sendForChildrenTestCase);
            }
        }

        [DisableAutoCreation]
        class ForceSerializeSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                var typesToOverride = GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride();
                foreach (var tuple in typesToOverride)
                {
                    defaultVariants.Add(tuple.Item1, Rule.ForAll(tuple.Item2 ?? tuple.Item1));
                }
            }
        }
        [DisableAutoCreation]
        class ForceDontSerializeSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                var typesToOverride = GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride();
                foreach (var tuple in typesToOverride)
                {
                    defaultVariants.Add(tuple.Item1, Rule.ForAll(typeof(DontSerializeVariant)));
                }
            }
        }

        static GhostAuthoringInspectionComponent.ComponentOverride[] BuildComponentOverridesForComponents()
        {
            var testTypes = GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride();
            var overrides = testTypes
                .Select(x =>
                {
                    var componentTypeFullName = x.Item1.FullName;
                    var variantTypeName = x.Item2?.FullName ?? componentTypeFullName;
                    return new GhostAuthoringInspectionComponent.ComponentOverride
                    {
                        FullTypeName = componentTypeFullName,
                        PrefabType = GhostPrefabType.All,
                        SendTypeOptimization = GhostSendType.AllClients,
                        VariantHash = GhostVariantsUtility.UncheckedVariantHashNBC(variantTypeName, componentTypeFullName),
                    };
                }).ToArray();
            return overrides;
        }

        [Test]
        public void GhostsAreSerializedWithEnabledBits_PreSerialize([Values]GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            CreateWorldsAndSpawn(1, type, count, GhostFlags.PreSerialize, sendForChildrenTestCase);

            var value = -1;
            var enabled = false;

            SetGhostValues(value, enabled);
            TickMultipleFrames();
            VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            for (int i = 0; i < 8; ++i)
            {
                enabled = !enabled;
                value = i;

                SetGhostValues(value, enabled);
                TickMultipleFrames();
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
            TickMultipleFrames();
            VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            TickMultipleFrames();

            value = 21;

            SetGhostValues(value, enabled);
            TickMultipleFrames();

            VerifyGhostValues(value, enabled, sendForChildrenTestCase);

            for (int i = 0; i < 8; ++i)
            {
                enabled = !enabled;
                value = i;

                SetGhostValues(value, enabled);
                TickMultipleFrames();
                VerifyGhostValues(value, enabled, sendForChildrenTestCase);
            }
        }
    }
}
