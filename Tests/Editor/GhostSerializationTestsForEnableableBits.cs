using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// Used to test different baked values.
    /// </summary>
    internal enum EnabledBitBakedValue
    {
        // No need to test this as it's covered by other tests.
        ///// <summary>Bake the component as ENABLED, then write to it on Server on the first frame.</summary>
        // StartEnabledAndWriteImmediately = 0,

        /// <summary>Bake the component as DISABLED, then write to it on Server on the first frame.</summary>
        StartDisabledAndWriteImmediately = 1,
        /// <summary>Bake the component as ENABLED, wait for the ghost to be created, then validate the baked value is replicated. Then continue the test by modify it.</summary>
        StartEnabledAndWaitForClientSpawn = 3,
        /// <summary>Bake the component as DISABLED, wait for the ghost to be created, then validate the baked value is replicated. Then continue the test by modify it.</summary>
        StartDisabledAndWaitForClientSpawn = 4,
    }

    internal class GhostSerializationTestsForEnableableBits
    {
        void TickMultipleFrames(int numTicksToProperlyReplicate)
        {
            for (int i = 0; i < numTicksToProperlyReplicate; ++i)
            {
                m_TestWorld.Tick();
            }
        }


        void SetLinkedBufferValues<T>(int value, bool enabled)
            where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            for (var i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var serverEntityGroup = m_TestWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity, true);
                Assert.AreEqual(2, serverEntityGroup.Length);

                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[0].Value, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[0].Value), $"{typeof(T)} is set correctly on server, linked[0]");

                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[1].Value, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[1].Value), $"{typeof(T)} is set correctly on server, linked[1]");

                SetupBuffer(m_TestWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntityGroup[0].Value));
                SetupBuffer(m_TestWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntityGroup[1].Value));

                void SetupBuffer(DynamicBuffer<T> buffer)
                {
                    buffer.ResizeUninitialized(kWrittenServerBufferSize);
                    for (int j = 0; j < kWrittenServerBufferSize; ++j)
                    {
                        var newValue = new T();
                        newValue.SetValue((j + 1) * 1000 + value + i);
                        buffer[j] = newValue;
                    }
                }
            }
        }

        void SetGhostGroupValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            SetGhostGroupEnabled<T>(enabled);
            for (int i = 0; i < m_ServerEntities.Length; i += 2)
            {
                var groupRootEntity = m_ServerEntities[i];
                var groupMemberEntity = m_ServerEntities[i + 1];
                T newValue = default;
                newValue.SetValue(value + i);
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(groupRootEntity, newValue);
                newValue.SetValue(value + (i + 1) + kGhostGroupMemberValueOffset);
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(groupMemberEntity, newValue);
            }
        }
        void SetGhostGroupBufferValues<T>(int value, bool enabled)
            where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            SetGhostGroupEnabled<T>(enabled);
            for (int i = 0; i < m_ServerEntities.Length; i += 2)
            {
                var rootEntity = m_ServerEntities[i];
                var childEntity = m_ServerEntities[i + 1];
                WriteBuffer(value + i, m_TestWorld.ServerWorld.EntityManager.GetBuffer<T>(rootEntity));
                WriteBuffer(value + (i + 1) + kGhostGroupMemberValueOffset, m_TestWorld.ServerWorld.EntityManager.GetBuffer<T>(childEntity));
            }
        }

        private static void WriteBuffer<T>(int value, DynamicBuffer<T> buffer) where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            buffer.ResizeUninitialized(kWrittenServerBufferSize);
            for (int i = 0; i < kWrittenServerBufferSize; ++i)
            {
                var newValue = new T();
                newValue.SetValue((i + 1) * 1000 + value);
                buffer[i] = newValue;
            }
        }

        void SetGhostGroupEnabled<T>(bool enabled)
            where T : unmanaged, IEnableableComponent
        {
            for (int i = 0; i < m_ServerEntities.Length; i += 2)
            {
                var rootEntity = m_ServerEntities[i];
                var childEntity = m_ServerEntities[i + 1];

                Assert.True(m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostGroupRoot>(rootEntity));
                Assert.True(m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostChildEntity>(childEntity));

                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(rootEntity, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(rootEntity), $"{typeof(T)} is set correctly on server, group root entity");

                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(childEntity, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(childEntity), $"{typeof(T)} is set correctly on server, group member entity");
            }
        }

        void SetLinkedComponentValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            SetLinkedEnabled<T>(enabled);
            for (var i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntityGroup = m_TestWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(m_ServerEntities[i], true);
                T newValue = default;
                newValue.SetValue(value + i);
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[0].Value, newValue);
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(serverEntityGroup[1].Value, newValue);
            }
        }

        void SetLinkedEnabled<T>(bool enabled)
            where T : unmanaged, IEnableableComponent
        {
            foreach (var entity in m_ServerEntities)
            {
                var serverEntityGroup = m_TestWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity, true);
                Assert.AreEqual(2, serverEntityGroup.Length);

                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[0].Value, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[0].Value), $"{typeof(T)} is set correctly on server, linked[0]");

                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(serverEntityGroup[1].Value, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntityGroup[1].Value), $"{typeof(T)} is set correctly on server, linked[1]");
            }
        }

        void SetLinkedComponentEnabledOnlyOnChildren<T>(bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent
        {
            for (var i = 0; i < m_ServerEntities.Length; i++)
            {
                var entity = m_ServerEntities[i];
                var serverEntityGroup = m_TestWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity, true);
                Assert.AreEqual(2, serverEntityGroup.Length);

                var childEntity = serverEntityGroup[1].Value;
                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(childEntity, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(childEntity), $"{typeof(T)} enabled state is set correctly on server child [{i}]!");
            }
        }
        void SetLinkedComponentValueOnlyOnChildren<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IComponentValue, IEnableableComponent
        {
            SetLinkedComponentEnabledOnlyOnChildren<T>(enabled);
            for (var i = 0; i < m_ServerEntities.Length; i++)
            {
                var entity = m_ServerEntities[i];
                var serverEntityGroup = m_TestWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(entity, true);
                Assert.AreEqual(2, serverEntityGroup.Length);


                T newValue = default;
                newValue.SetValue(value + i);
                var childEntity = serverEntityGroup[1].Value;
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(childEntity, newValue);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(childEntity), $"{typeof(T)} value is set correctly on server child [{i}]!");
            }
        }

        void SetComponentValues<T>(int value, bool enabled)
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            SetEnabled<T>(enabled);
            for (var i = 0; i < m_ServerEntities.Length; i++)
            {
                var entity = m_ServerEntities[i];
                T newValue = default;
                newValue.SetValue(value + i);
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(entity, newValue);
            }
        }

        void SetEnabled<T>(bool enabled)
            where T : unmanaged, IEnableableComponent
        {
            foreach (var entity in m_ServerEntities)
            {
                m_TestWorld.ServerWorld.EntityManager.SetComponentEnabled<T>(entity, enabled);
                Assert.AreEqual(enabled, m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(entity), "Sanity!");
            }
        }

        void SetBufferValues<T>(int value, bool enabled) where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            SetEnabled<T>(enabled);
            for (var i = 0; i < m_ServerEntities.Length; i++)
            {
                WriteBuffer(value + i, m_TestWorld.ServerWorld.EntityManager.GetBuffer<T>(m_ServerEntities[i]));
            }
        }

        private void VerifyGhostGroupValues<T>()
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            VerifyGhostGroupEnabledBits<T>();

            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var isGroupRoot = (i % 2) == 0;
                var isGhostGroupMember = !isGroupRoot;
                var clientOwnsGhost = (i % 4) == 0;
                var clientValue = m_TestWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntity).GetValue();
                if (IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(), true, clientOwnsGhost)) // Ghost groups are root entities, by definition.
                {
                    Assert.AreEqual(m_IsValidatingBakedValues ? kBakedValue : m_ExpectedValueIfReplicated + i + (isGhostGroupMember ? kGhostGroupMemberValueOffset : 0), clientValue, $"[{typeof(T)}] Expect \"group {(isGroupRoot ? "root" : "member")}\" entity value when `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(kBakedValue, clientValue, $"[{typeof(T)}] Expect \"group {(isGroupRoot ? "root" : "member")}\" entity value is NOT replicated when `{m_SendForChildrenTestCase}`!");
                }
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), true);
        }

        private void VerifyGhostGroupBufferValues<T>()
            where T : unmanaged, IBufferElementData, IComponentValue
        {
            // When we iterate over all GhostGroup entries, we iterate:
            // [0] GroupParent1,
            // [1] GroupMember of [0],
            // [2] GroupParent2,
            // [3] GroupMember of [2],
            // ...
            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var isGroupRoot = (i % 2) == 0;
                var isGroupMember = !isGroupRoot;
                var clientOwnsGhost = (i % 4) == 0;
                Assert.AreEqual(isGroupRoot, m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostGroupRoot>(serverEntity));
                Assert.AreEqual(isGroupMember, m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostChildEntity>(serverEntity));
                Assert.AreEqual(isGroupRoot, m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGroupRoot>(clientEntity));
                Assert.AreEqual(isGroupMember, m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<GhostChildEntity>(clientEntity));
                VerifyOneBuffer<T>(serverEntity, clientEntity, i, true, isGroupMember);
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), true);
        }

        private Entity GetClientEntityByGhostId(Entity serverEntity)
        {
            Assert.IsTrue(m_TestWorld.ServerWorld.EntityManager.Exists(serverEntity));
            var serverGhost = m_TestWorld.ServerWorld.EntityManager.GetComponentData<GhostInstance>(serverEntity);
            var found = m_ClientGhostEntityMap.TryGetValue(serverGhost.ghostId, out var clientEntity);
            var message = $"Expected to find client ghost entity via GID:{serverGhost.ghostId}.";
            Assert.IsTrue(found, message);
            Assert.IsTrue(m_TestWorld.ClientWorlds[0].EntityManager.Exists(clientEntity), message);
            return clientEntity;
        }

        private void VerifyGhostGroupEnabledBits<T>()
            where T : unmanaged, IEnableableComponent
        {
            var rootType = ComponentType.ReadWrite<GhostGroupRoot>();
            var childType = ComponentType.ReadWrite<GhostChildEntity>();

            using var rootQuery = m_TestWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(rootType);
            using var childQuery = m_TestWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(childType);
            using var clientRootEntities = rootQuery.ToEntityArray(Allocator.Temp);
            using var clientChildEntities = childQuery.ToEntityArray(Allocator.Temp);
            Assert.AreEqual(clientRootEntities.Length, clientChildEntities.Length);
            Assert.AreEqual(m_ServerEntities.Length, clientChildEntities.Length + clientRootEntities.Length,  $"[{typeof(T)}] Expect client group has entities!");

            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var expectGroupRoot = (i % 2) == 0;
                var clientOwnsGhost = (i % 4) == 0;
                Assert.AreEqual(expectGroupRoot, m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostGroupRoot>(serverEntity));
                Assert.AreEqual(expectGroupRoot, !m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostChildEntity>(serverEntity));
                Assert.AreEqual(expectGroupRoot, m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<GhostGroupRoot>(clientEntity));
                Assert.AreEqual(expectGroupRoot, !m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<GhostChildEntity>(clientEntity));

                var clientEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),true, clientOwnsGhost)) // Ghost groups are root entities, by definition.
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, clientEnabled, $"[{typeof(T)}] Expect ghost group {(expectGroupRoot ? "root" : "member")} entity enabled IS replicated when `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, clientEnabled, $"[{typeof(T)}] Expect ghost group {(expectGroupRoot ? "root" : "member")} entity enabled NOT replicated when `{m_SendForChildrenTestCase}`!");
                }
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), true);
        }

        private void VerifyLinkedBufferValues<T>()
            where T : unmanaged, IBufferElementData, IEnableableComponent, IComponentValue
        {
            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;
                Assert.IsTrue(m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity), "Has client linked group!");

                var serverEntityGroup = m_TestWorld.ServerWorld.EntityManager.GetBuffer<LinkedEntityGroup>(serverEntity, true);
                var clientEntityGroup = m_TestWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity, true);
                Assert.AreEqual(2, clientEntityGroup.Length, "client linked group, expecting parent + child");

                var clientParentEntityComponentEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[0].Value);
                var clientChildEntityComponentEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[1].Value);

                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),true, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, clientParentEntityComponentEnabled, $"[{typeof(T)}] Expect client parent entity component enabled bit IS replicated when `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, clientParentEntityComponentEnabled, $"[{typeof(T)}] Expect client parent entity component enabled bit NOT replicated when `{m_SendForChildrenTestCase}`!");
                }
                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(), false, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, clientChildEntityComponentEnabled, $"[{typeof(T)}] Expect client child entity component enabled bit IS replicated when `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyChildGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, clientChildEntityComponentEnabled, $"[{typeof(T)}] Expect client child entity component enabled bit NOT replicated when `{m_SendForChildrenTestCase}`!");
                }

                VerifyOneBuffer<T>(serverEntityGroup[0].Value, clientEntityGroup[0].Value, i, true, false);
                VerifyOneBuffer<T>(serverEntityGroup[1].Value, clientEntityGroup[1].Value, i, false, false);
            }
        }

        private void VerifyLinkedComponentValues<T>()
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            VerifyLinkedEnabled<T>();

            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;

                Assert.IsTrue(m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity));

                var clientEntityGroup = m_TestWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity, true);
                Assert.AreEqual(2, clientEntityGroup.Length, "Client entity count should always be correct.");

                var clientRootValue = m_TestWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[0].Value).GetValue();
                var clientChildValue = m_TestWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue();
                if (IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(),true, clientOwnsGhost))
                {
                    Assert.AreEqual(m_IsValidatingBakedValues ? kBakedValue : m_ExpectedValueIfReplicated + i, clientRootValue, $"[{typeof(T)}] Expected that value on component on root entity [{i}] IS replicated correctly when using this `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(kBakedValue, clientRootValue, $"[{typeof(T)}] Expected that value on component on root entity [{i}] is NOT replicated by default (via this `{m_SendForChildrenTestCase}`)!");
                }

                if (IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(),false, clientOwnsGhost))
                {
                    Assert.AreEqual(m_IsValidatingBakedValues ? kBakedValue : m_ExpectedValueIfReplicated + i, clientChildValue, $"[{typeof(T)}] Expected that value on component on child entity [{i}] IS replicated when using this `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyChildGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(kBakedValue, clientChildValue, $"[{typeof(T)}] Expected that value on component on child entity [{i}] is NOT replicated by default (via this `{m_SendForChildrenTestCase}`)!");
                }
            }
        }

        void VerifyLinkedComponentValueOnChild<T>()
            where T : unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            VerifyLinkedComponentEnabledOnChild<T>();

            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;

                Assert.IsTrue(m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity));

                var clientEntityGroup = m_TestWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity, true);
                Assert.AreEqual(2, clientEntityGroup.Length, "Client entity count should always be correct.");

                // This method is exclusively to test behaviour of children.

                var value = m_TestWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntityGroup[1].Value).GetValue();
                if (IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(),false, clientOwnsGhost))
                {
                    Assert.AreEqual(m_IsValidatingBakedValues ? kBakedValue : m_ExpectedValueIfReplicated + i, value, $"[{typeof(T)}] Expected that value on component on child entity [{i}] IS replicated when using this `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyChildGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(kBakedValue, value, $"[{typeof(T)}] Expected that value on component on child entity [{i}] is NOT replicated by default (via this `{m_SendForChildrenTestCase}`)!");
                }
            }
        }

        private void VerifyLinkedEnabled<T>()
            where T : unmanaged, IEnableableComponent
        {
            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;

                Assert.IsTrue(m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity), $"[{typeof(T)}] Client has entities with the LinkedEntityGroup.");

                var clientEntityGroup = m_TestWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity, true);
                Assert.AreEqual(2, clientEntityGroup.Length, $"[{typeof(T)}] Entities in the LinkedEntityGroup!");

                var rootEntityEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[0].Value);
                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),true, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, rootEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on root entity [{i}] is replicated when using `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, rootEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on root entity [{i}] is NOT replicated by default when using `{m_SendForChildrenTestCase}`!");
                }

                var childEntityEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[1].Value);
                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),false, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, childEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on child entity [{i}] is replicated when using `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyChildGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, childEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component on child entity [{i}] is NOT replicated by default when using `{m_SendForChildrenTestCase}`!");
                }
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), false);
        }

        private void VerifyLinkedComponentEnabledOnChild<T>()
            where T : unmanaged, IComponentData, IEnableableComponent
        {
            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;

                Assert.IsTrue(m_TestWorld.ClientWorlds[0].EntityManager.HasComponent<LinkedEntityGroup>(clientEntity), $"[{typeof(T)}] Client has entities with the LinkedEntityGroup.");

                var clientEntityGroup = m_TestWorld.ClientWorlds[0].EntityManager.GetBuffer<LinkedEntityGroup>(clientEntity, true);
                Assert.AreEqual(2, clientEntityGroup.Length, $"[{typeof(T)}] Entities in the LinkedEntityGroup!");

                // This method is exclusively to test behaviour of children.

                var childEntityEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntityGroup[1].Value);
                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),false, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, childEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component ONLY on child entity [{i}] is replicated when using `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyChildGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, childEntityEnabled, $"[{typeof(T)}] Expected that the enable-bit on component ONLY on child entity [{i}] is NOT replicated by default when using `{m_SendForChildrenTestCase}`!");
                }
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), false);
        }

        private void VerifyComponentValues<T>() where T: unmanaged, IComponentData, IEnableableComponent, IComponentValue
        {
            VerifyFlagComponentEnabledBit<T>();

            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;

                var isServerEnabled = m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                var isClientEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                var serverValue = m_TestWorld.ServerWorld.EntityManager.GetComponentData<T>(serverEntity).GetValue();
                var clientValue = m_TestWorld.ClientWorlds[0].EntityManager.GetComponentData<T>(clientEntity).GetValue();
                Assert.AreEqual(m_ExpectedEnabledIfReplicated, isServerEnabled, $"[{typeof(T)}] Test expects server enable bit [{i}] to still be same!");
                Assert.AreEqual(m_IsValidatingBakedValues ? kBakedValue : m_ExpectedValueIfReplicated + i, serverValue, $"[{typeof(T)}] Test expects server value [{i}] to still be same!");

                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),true, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, isClientEnabled, $"[{typeof(T)}] Test expects client enable bit [{i}] IS replicated when using `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, isClientEnabled, $"[{typeof(T)}] Test expects client enable bit [{i}] NOT replicated when using `{m_SendForChildrenTestCase}`!");
                }
                if (IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(),true, clientOwnsGhost))
                {
                    // Note that values are replicated even if the component is disabled!
                    Assert.AreEqual(m_IsValidatingBakedValues ? kBakedValue : m_ExpectedValueIfReplicated + i, clientValue, $"[{typeof(T)}] Test expects client value [{i}] IS replicated when using `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(kBakedValue, clientValue, $"[{typeof(T)}] Test expects client value [{i}] NOT replicated when using `{m_SendForChildrenTestCase}`!");
                }
            }
        }

        private void VerifyStaticOptimization(GhostFlags flags, bool? forceExpect)
        {
            Assert.IsTrue(m_DidExpectAnyRootGhostFieldChanges.HasValue, "Sanity!");
            Assert.IsTrue(m_DidExpectAnyChildGhostFieldChanges.HasValue, "Sanity!");
            var isPrefabStatic = (flags & GhostFlags.StaticOptimization) != default;

            var clientEm = m_TestWorld.ClientWorlds[0].EntityManager;
            using var buffersQuery = new EntityQueryBuilder(Allocator.Temp).WithPresent<GhostInstance>()
                .WithOptions(EntityQueryOptions.IncludeDisabledEntities).Build(clientEm);
            var clientGhostEntities = buffersQuery.ToEntityArray(Allocator.Temp);
            var prefabSerializers = m_TestWorld.GetSingletonBuffer<GhostCollectionPrefabSerializer>(m_TestWorld.ClientWorlds[0]);
            Assert.IsTrue(clientGhostEntities.Length == m_ServerEntities.Length, $"Sanity - clientGhostEntities.Length:{clientGhostEntities.Length} == m_ServerEntities.Length:{m_ServerEntities.Length}");
            for (var i = 0; i < clientGhostEntities.Length; i++)
            {
                var clientEntity = clientGhostEntities[i];
                // GhostGroups and ghost's with replicated children don't support static-optimization, so our expectation
                // of them is that they'll behave identically to dynamic ghosts:
                var isRoot = !clientEm.HasComponent<GhostChildEntity>(clientEntity);
                var hasChildEntities = clientEm.GetBuffer<LinkedEntityGroup>(clientEntity).Length > 1;
                var ghostInstance = m_TestWorld.ClientWorlds[0].EntityManager.GetComponentData<GhostInstance>(clientEntity);
                var hasReplicatedChildren = prefabSerializers[ghostInstance.ghostType].NumChildComponents > 0;
                var entityIsForcedDynamic = !isRoot || clientEm.HasComponent<GhostGroup>(clientEntity) || hasReplicatedChildren;
                var isEntityStatic = isPrefabStatic && !entityIsForcedDynamic;
                var didExpectAnyGhostFieldChange = forceExpect ?? (isRoot ? m_DidExpectAnyRootGhostFieldChanges.Value : m_DidExpectAnyChildGhostFieldChanges.Value);
                var didExpectSnapshotArrivedRecently = (!isEntityStatic || didExpectAnyGhostFieldChange);

                if (clientEm.HasBuffer<SnapshotDataBuffer>(clientEntity)) // Child entities cannot be verified!
                {
                    var clientSnapshotBuffer = clientEm.GetBuffer<SnapshotDataBuffer>(clientEntity);
                    var clientSnapshot = clientEm.GetComponentData<SnapshotData>(clientEntity);
                    var latestClientSnapshotTick = clientSnapshot.GetLatestTick(clientSnapshotBuffer);
                    var hasReceivedSnapshotThisSubTest = latestClientSnapshotTick.TicksSince(m_ServerTickBeforeSend) > 0;
                    Assert.AreEqual(didExpectSnapshotArrivedRecently, hasReceivedSnapshotThisSubTest,
                        $"Static optimization not applied correctly - didExpectSnapshotArrivedRecently! forceExpect:{forceExpect}, didExpectAnyGhostFieldChanges:{m_DidExpectAnyRootGhostFieldChanges}, latestClientSnapshotTick:{latestClientSnapshotTick}.TicksSince(tickBeforeSend:{m_ServerTickBeforeSend}) > 0, expectedSnapshotArrivedRecently:{didExpectSnapshotArrivedRecently}, isRoot:{isRoot}, isChild:{clientEm.HasComponent<GhostChildEntity>(clientEntity)}, rootChanged:{m_DidExpectAnyRootGhostFieldChanges}, childChanged:{m_DidExpectAnyChildGhostFieldChanges}, isEntityStatic:{isEntityStatic})");
                }
            }

            m_DidExpectAnyRootGhostFieldChanges = null;
            m_DidExpectAnyChildGhostFieldChanges = null;
            m_ServerTickBeforeSend = NetworkTick.Invalid;
        }

        /// <summary> Tests how Change Filtering works in the <see cref="GhostUpdateSystem"/>.</summary>
        private void ValidateChangeMask(ComponentType componentType, bool isPartOfGhostGroup)
        {
            if (m_IsFirstRun) // On the first run, there will be inconsistencies. Not worth trying to handle.
                return;

            FixedList32Bytes<ComponentType> componentTypeSet = default;
            componentTypeSet.Add(componentType);
            var builder = new EntityQueryBuilder(Allocator.Temp).WithAll(ref componentTypeSet).WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
            var clientEm = m_TestWorld.ClientWorlds[0].EntityManager;
            using var query = clientEm.CreateEntityQuery(builder);
            var chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            for (var chunkIdx = 0; chunkIdx < chunks.Length; chunkIdx++)
            {
                var chunk = chunks[chunkIdx];
                var chunkGhostOwnership = CalculateChunkGhostOwnership(ref chunk, clientEm);

                var dynamicComponentTypeHandle = clientEm.GetDynamicComponentTypeHandle(componentType);
                var componentChangeVersionInChunk = chunk.GetChangeVersion(ref dynamicComponentTypeHandle);
                var didChangeSinceLastVerifyCall = ChangeVersionUtility.DidChange(componentChangeVersionInChunk, m_LastGlobalSystemVersion);

                var isRoot = isPartOfGhostGroup || !chunk.Has<GhostChildEntity>();
                var isReplicatingAnything = IsExpectedToReplicateValue(componentType, isRoot, chunkGhostOwnership) || IsExpectedToReplicateEnabledBit(componentType, isRoot, chunkGhostOwnership);
                if (isRoot) m_DidExpectAnyRootGhostFieldChanges |= isReplicatingAnything;
                else m_DidExpectAnyChildGhostFieldChanges |= isReplicatingAnything;

                if (m_ExpectChangeFilterToChange && isReplicatingAnything)
                    Assert.IsTrue(didChangeSinceLastVerifyCall, $"[{componentType}] [chunkIdx:{chunkIdx}, Count:{chunk.Count}] Expected this component's change version to be updated, but it was not! {componentChangeVersionInChunk} vs {m_LastGlobalSystemVersion}, isRoot:{isRoot}, chunkGhostOwnership:{chunkGhostOwnership}.");
                else if (m_ExpectChangeFilterToChange)
                    Assert.IsFalse(didChangeSinceLastVerifyCall, $"[{componentType}] [chunkIdx:{chunkIdx}, Count:{chunk.Count}] We'd expected this component's change version to be updated, but it's not replicated, so it SHOULDN'T be changed! {componentChangeVersionInChunk} vs {m_LastGlobalSystemVersion}, isRoot:{isRoot}, chunkGhostOwnership:{chunkGhostOwnership}.");
                else
                    Assert.IsFalse(didChangeSinceLastVerifyCall, $"[{componentType}] [chunkIdx:{chunkIdx}, Count:{chunk.Count}] We did not modify this component (nor it's enabled flag), so it SHOULDN'T be changed! {componentChangeVersionInChunk} vs {m_LastGlobalSystemVersion}, isRoot:{isRoot}, chunkGhostOwnership:{chunkGhostOwnership}.");
            }
        }

        static bool? CalculateChunkGhostOwnership(ref ArchetypeChunk chunk, EntityManager clientEm)
        {
            Assert.NotZero(chunk.Count, "Sanity!");
            var entityTypeHandle = clientEm.GetEntityTypeHandle();
            var entities = chunk.GetNativeArray(entityTypeHandle);
            Assert.NotZero(entities.Length, "Sanity!");

            bool allOwned = true;
            bool allNotOwned = true;
            foreach (var ghostEntity in entities)
            {
                // Account for children:
                var rootEntity = clientEm.HasComponent<GhostOwner>(ghostEntity)
                    ? ghostEntity
                    : clientEm.GetComponentData<Parent>(ghostEntity).Value;
                var ghostOwner = clientEm.GetComponentData<GhostOwner>(rootEntity);
                allOwned &= ghostOwner.NetworkId != default;
                allNotOwned &= ghostOwner.NetworkId == default;
            }
            return (allOwned, allNotOwned) switch
            {
                (true, false) => true,
                (false, true) => false,
                _ => null,
            };
        }

        private void VerifyFlagComponentEnabledBit<T>() where T : unmanaged, IComponentData, IEnableableComponent
        {
            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var clientOwnsGhost = (i % 4) == 0;

                var isServerEnabled = m_TestWorld.ServerWorld.EntityManager.IsComponentEnabled<T>(serverEntity);
                var isClientEnabled = m_TestWorld.ClientWorlds[0].EntityManager.IsComponentEnabled<T>(clientEntity);
                Assert.AreEqual(m_ExpectedEnabledIfReplicated, isServerEnabled, $"[{typeof(T)}] Expect flag component server enabled bit is correct.");

                if (IsExpectedToReplicateEnabledBit(ComponentType.ReadWrite<T>(),true, clientOwnsGhost))
                {
                    Assert.AreEqual(m_ExpectedEnabledIfReplicated, isClientEnabled, $"{typeof(T)} Expected client {i} enabled bit IS replicated.");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(m_ExpectedEnabledIfNotReplicated, isClientEnabled, $"{typeof(T)} Expected client {i} enabled bit is NOT replicated.");
                }
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), false);
        }

        NativeArray<ArchetypeChunk> chunkArray;

        private void VerifyBufferValues<T>() where T: unmanaged, IBufferElementData, IComponentValue
        {
            for (int i = 0; i < m_ServerEntities.Length; i++)
            {
                var serverEntity = m_ServerEntities[i];
                var clientEntity = GetClientEntityByGhostId(serverEntity);
                var isRoot = !m_TestWorld.ServerWorld.EntityManager.HasComponent<GhostChildEntity>(serverEntity);
                VerifyOneBuffer<T>(serverEntity, clientEntity, i, isRoot, false);
            }

            ValidateChangeMask(ComponentType.ReadWrite<T>(), false);
        }

        private void VerifyOneBuffer<T>(Entity serverEntity, Entity clientEntity, int i, bool isRoot, bool isGhostGroupMember) where T : unmanaged, IBufferElementData, IComponentValue
        {
            var serverBuffer = m_TestWorld.ServerWorld.EntityManager.GetBuffer<T>(serverEntity, true);
            var clientBuffer = m_TestWorld.ClientWorlds[0].EntityManager.GetBuffer<T>(clientEntity, true);
            var clientOwnsGhost = (i % 4) == 0;

            Assert.AreEqual(m_ExpectedServerBufferSize, serverBuffer.Length, $"[{typeof(T)}] server buffer length");
            if (IsExpectedToReplicateBuffer<T>(m_SendForChildrenTestCase, isRoot) &&
                IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(),isRoot, clientOwnsGhost))
            {
                Assert.AreEqual(m_ExpectedServerBufferSize, clientBuffer.Length, $"[{typeof(T)}] Expect client buffer length IS replicated when `{m_SendForChildrenTestCase}`!");
                if (isRoot)
                    m_DidExpectAnyRootGhostFieldChanges = true;
                else m_DidExpectAnyChildGhostFieldChanges = true;
            }
            else
            {
                Assert.AreEqual(kBakedBufferSize, clientBuffer.Length, $"[{typeof(T)}] Expect client buffer length should NOT be replicated when `{m_SendForChildrenTestCase}`, thus should be the default CLIENT value");
            }

            for (int j = 0; j < serverBuffer.Length; ++j)
            {
                var serverValue = serverBuffer[j];
                var clientValue = clientBuffer[j];

                var expectedBufferValue = m_IsValidatingBakedValues ? kBakedValue : ((j + 1) * 1000 + m_ExpectedValueIfReplicated + i + (isGhostGroupMember ? kGhostGroupMemberValueOffset : 0));
                Assert.AreEqual(expectedBufferValue, serverValue.GetValue(), $"[{typeof(T)}] Expect server buffer value [{i}]");

                if (IsExpectedToReplicateValue(ComponentType.ReadWrite<T>(),isRoot, clientOwnsGhost))
                {
                    Assert.AreEqual(expectedBufferValue, clientValue.GetValue(), $"[{typeof(T)}] Expect client buffer value [{i}] IS replicated when `{m_SendForChildrenTestCase}`!");
                    m_DidExpectAnyRootGhostFieldChanges = true;
                }
                else
                {
                    Assert.AreEqual(kBakedValue, clientValue.GetValue(), $"[{typeof(T)}] Expect client buffer value [{i}] is NOT replicated when `{m_SendForChildrenTestCase}`!");
                }
            }
        }

        void SetGhostValues(GhostTypeConverter.GhostTypes ghostTypes, int value, bool enabled = false)
        {
            m_ExpectedServerBufferSize = kWrittenServerBufferSize;
            m_IsValidatingBakedValues = false;

            Assert.IsTrue(m_ServerEntities.IsCreated);
            switch (ghostTypes)
            {
                case GhostTypeConverter.GhostTypes.EnableableComponents:
                    SetComponentValues<EnableableComponent>(value, enabled);
                    SetComponentValues<EnableableComponentWithNonGhostField>(value, enabled);
                    SetEnabled<EnableableFlagComponent>(enabled);
                    SetComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, enabled);
                    SetComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, enabled);
                    SetComponentValues<ComponentWithReplicatedVariant>(value, enabled);
                    SetComponentValues<ComponentWithDontSendChildrenVariant>(value, enabled);
                    SetComponentValues<ComponentWithNonReplicatedVariant>(value, enabled);
                    SetEnabled<NeverReplicatedEnableableFlagComponent>(enabled);

                    SetComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableComponent>(value, enabled);
                    SetComponentValues<SendForChildren_DontSend_SendToOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);

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
                case GhostTypeConverter.GhostTypes.EnableableBuffers:
                    SetBufferValues<EnableableBuffer>(value, enabled);
                    SetBufferValues<EnableableBufferWithNonGhostField>(value, enabled);
                    SetBufferValues<ReplicatedFieldWithNonReplicatedEnableableBuffer>(value, enabled);
                    SetBufferValues<ReplicatedEnableableBufferWithNonReplicatedField>(value, enabled);
                    SetBufferValues<BufferWithReplicatedVariant>(value, enabled);
                    SetBufferValues<BufferWithDontSendChildrenVariant>(value, enabled);
                    SetBufferValues<BufferWithNonReplicatedVariant>(value, enabled);

                    SetBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableBuffer>(value, enabled);
                    SetBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);

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
                case GhostTypeConverter.GhostTypes.Mixed:
                    SetGhostValues(GhostTypeConverter.GhostTypes.EnableableComponents, value, enabled);
                    SetGhostValues(GhostTypeConverter.GhostTypes.EnableableBuffers, value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildComponents:
                    SetLinkedComponentValues<EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<EnableableComponentWithNonGhostField>(value, enabled);
                    SetLinkedEnabled<EnableableFlagComponent>(enabled);
                    SetLinkedComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, enabled);
                    SetLinkedComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, enabled);
                    SetLinkedComponentValues<ComponentWithReplicatedVariant>(value, enabled);
                    SetLinkedComponentValues<ComponentWithDontSendChildrenVariant>(value, enabled);
                    SetLinkedComponentValues<ComponentWithNonReplicatedVariant>(value, enabled);
                    SetLinkedEnabled<NeverReplicatedEnableableFlagComponent>(enabled);

                    SetLinkedComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<SendForChildren_DontSend_SendToOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetLinkedComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);

                    SetLinkedComponentEnabledOnlyOnChildren<ChildOnlyComponent_1>(enabled);
                    SetLinkedComponentEnabledOnlyOnChildren<ChildOnlyComponent_2>(enabled);
                    SetLinkedComponentValueOnlyOnChildren<ChildOnlyComponent_3>(value, enabled);
                    SetLinkedComponentValueOnlyOnChildren<ChildOnlyComponent_4>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildBufferComponents:
                    SetLinkedBufferValues<EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<EnableableBufferWithNonGhostField>(value, enabled);
                    SetLinkedBufferValues<ReplicatedFieldWithNonReplicatedEnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<ReplicatedEnableableBufferWithNonReplicatedField>(value, enabled);
                    SetLinkedBufferValues<BufferWithReplicatedVariant>(value, enabled);
                    SetLinkedBufferValues<BufferWithDontSendChildrenVariant>(value, enabled);
                    SetLinkedBufferValues<BufferWithNonReplicatedVariant>(value, enabled);

                    SetLinkedBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetLinkedBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.GhostGroup:
                    SetGhostGroupValues<EnableableComponent>(value, enabled);
                    SetGhostGroupValues<EnableableComponentWithNonGhostField>(value, enabled);
                    SetGhostGroupEnabled<EnableableFlagComponent>(enabled);
                    SetGhostGroupValues<ReplicatedFieldWithNonReplicatedEnableableComponent>(value, enabled);
                    SetGhostGroupValues<ReplicatedEnableableComponentWithNonReplicatedField>(value, enabled);
                    SetGhostGroupValues<ComponentWithReplicatedVariant>(value, enabled);
                    SetGhostGroupValues<ComponentWithDontSendChildrenVariant>(value, enabled);
                    SetGhostGroupValues<ComponentWithNonReplicatedVariant>(value, enabled);
                    SetGhostGroupEnabled<NeverReplicatedEnableableFlagComponent>(enabled);

                    SetGhostGroupValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<SendForChildren_DontSend_SendToOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);
                    SetGhostGroupValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>(value, enabled);

                    SetGhostGroupBufferValues<EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<EnableableBufferWithNonGhostField>(value, enabled);
                    SetGhostGroupBufferValues<ReplicatedFieldWithNonReplicatedEnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<ReplicatedEnableableBufferWithNonReplicatedField>(value, enabled);
                    SetGhostGroupBufferValues<BufferWithReplicatedVariant>(value, enabled);
                    SetGhostGroupBufferValues<BufferWithDontSendChildrenVariant>(value, enabled);
                    SetGhostGroupBufferValues<BufferWithNonReplicatedVariant>(value, enabled);

                    SetGhostGroupBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    SetGhostGroupBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>(value, enabled);
                    break;
                default:
                    Assert.True(true);
                    break;
            }
        }

        void VerifyGhostValues(GhostTypeConverter.GhostTypes ghostTypes, int value, bool enabled)
        {
            Assert.IsTrue(m_ServerEntities.IsCreated);
            m_ExpectedValueIfReplicated = value;
            m_ExpectedEnabledIfReplicated = enabled;
            m_DidExpectAnyRootGhostFieldChanges = false;
            m_DidExpectAnyChildGhostFieldChanges = false;
            m_ClientGhostEntityMap = m_TestWorld.GetSingletonRW<SpawnedGhostEntityMap>(m_TestWorld.ClientWorlds[0]).ValueRO.ClientGhostEntityMap;

            switch (ghostTypes)
            {
                case GhostTypeConverter.GhostTypes.EnableableComponents:
                    VerifyComponentValues<EnableableComponent>();
                    VerifyComponentValues<EnableableComponentWithNonGhostField>();
                    VerifyFlagComponentEnabledBit<EnableableFlagComponent>();
                    VerifyComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>();
                    VerifyComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>();
                    VerifyComponentValues<ComponentWithReplicatedVariant>();
                    VerifyComponentValues<ComponentWithDontSendChildrenVariant>();
                    VerifyComponentValues<ComponentWithNonReplicatedVariant>();
                    VerifyFlagComponentEnabledBit<NeverReplicatedEnableableFlagComponent>();

                    VerifyComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableComponent>();
                    VerifyComponentValues<SendForChildren_DontSend_SendToOwner_EnableableComponent>();
                    VerifyComponentValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>();
                    VerifyComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>();
                    VerifyComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>();
                    VerifyComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>();
                    VerifyComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>();

                    VerifyComponentValues<EnableableComponent_1>();
                    VerifyComponentValues<EnableableComponent_2>();
                    VerifyComponentValues<EnableableComponent_3>();
                    VerifyComponentValues<EnableableComponent_4>();
                    VerifyComponentValues<EnableableComponent_5>();
                    VerifyComponentValues<EnableableComponent_6>();
                    VerifyComponentValues<EnableableComponent_7>();
                    VerifyComponentValues<EnableableComponent_8>();
                    VerifyComponentValues<EnableableComponent_9>();
                    VerifyComponentValues<EnableableComponent_10>();
                    VerifyComponentValues<EnableableComponent_11>();
                    VerifyComponentValues<EnableableComponent_12>();
                    VerifyComponentValues<EnableableComponent_13>();
                    VerifyComponentValues<EnableableComponent_14>();
                    VerifyComponentValues<EnableableComponent_15>();
                    VerifyComponentValues<EnableableComponent_16>();
                    VerifyComponentValues<EnableableComponent_17>();
                    VerifyComponentValues<EnableableComponent_18>();
                    VerifyComponentValues<EnableableComponent_19>();
                    VerifyComponentValues<EnableableComponent_20>();
                    VerifyComponentValues<EnableableComponent_21>();
                    VerifyComponentValues<EnableableComponent_22>();
                    VerifyComponentValues<EnableableComponent_23>();
                    VerifyComponentValues<EnableableComponent_24>();
                    VerifyComponentValues<EnableableComponent_25>();
                    VerifyComponentValues<EnableableComponent_26>();
                    VerifyComponentValues<EnableableComponent_27>();
                    VerifyComponentValues<EnableableComponent_28>();
                    VerifyComponentValues<EnableableComponent_29>();
                    VerifyComponentValues<EnableableComponent_30>();
                    VerifyComponentValues<EnableableComponent_31>();
                    VerifyComponentValues<EnableableComponent_32>();
                    break;
                case GhostTypeConverter.GhostTypes.EnableableBuffers:
                    VerifyBufferValues<EnableableBuffer>();
                    VerifyBufferValues<EnableableBuffer_0>();
                    VerifyBufferValues<EnableableBuffer_1>();
                    VerifyBufferValues<EnableableBuffer_2>();
                    VerifyBufferValues<EnableableBuffer_3>();
                    VerifyBufferValues<EnableableBuffer_4>();
                    VerifyBufferValues<EnableableBuffer_5>();
                    VerifyBufferValues<EnableableBuffer_6>();
                    VerifyBufferValues<EnableableBuffer_7>();
                    VerifyBufferValues<EnableableBuffer_8>();
                    VerifyBufferValues<EnableableBuffer_9>();
                    VerifyBufferValues<EnableableBuffer_10>();
                    VerifyBufferValues<EnableableBuffer_11>();
                    VerifyBufferValues<EnableableBuffer_12>();
                    VerifyBufferValues<EnableableBuffer_13>();
                    VerifyBufferValues<EnableableBuffer_14>();
                    VerifyBufferValues<EnableableBuffer_15>();
                    VerifyBufferValues<EnableableBuffer_16>();
                    VerifyBufferValues<EnableableBuffer_17>();
                    VerifyBufferValues<EnableableBuffer_18>();
                    VerifyBufferValues<EnableableBuffer_19>();
                    VerifyBufferValues<EnableableBuffer_20>();
                    VerifyBufferValues<EnableableBuffer_21>();
                    VerifyBufferValues<EnableableBuffer_22>();
                    VerifyBufferValues<EnableableBuffer_23>();
                    VerifyBufferValues<EnableableBuffer_24>();
                    VerifyBufferValues<EnableableBuffer_25>();
                    VerifyBufferValues<EnableableBuffer_26>();
                    VerifyBufferValues<EnableableBuffer_27>();
                    VerifyBufferValues<EnableableBuffer_28>();
                    VerifyBufferValues<EnableableBuffer_29>();
                    VerifyBufferValues<EnableableBuffer_30>();
                    VerifyBufferValues<EnableableBuffer_31>();
                    VerifyBufferValues<EnableableBuffer_32>();
                    break;
                case GhostTypeConverter.GhostTypes.Mixed:
                    VerifyGhostValues(GhostTypeConverter.GhostTypes.EnableableComponents, value, enabled);
                    VerifyGhostValues(GhostTypeConverter.GhostTypes.EnableableBuffers, value, enabled);
                    break;
                case GhostTypeConverter.GhostTypes.ChildComponents:
                    VerifyLinkedComponentValues<EnableableComponent>();
                    VerifyLinkedComponentValues<EnableableComponentWithNonGhostField>();
                    VerifyLinkedEnabled<EnableableFlagComponent>();
                    VerifyLinkedComponentValues<ReplicatedFieldWithNonReplicatedEnableableComponent>();
                    VerifyLinkedComponentValues<ReplicatedEnableableComponentWithNonReplicatedField>();
                    // We override variants for these two, so cannot test their "default variants" without massive complications.
                    VerifyLinkedComponentValues<ComponentWithReplicatedVariant>();
                    VerifyLinkedEnabled<ComponentWithNonReplicatedVariant>();
                    // Note: We don't test the component on the root here.
                    VerifyLinkedComponentValues<ComponentWithDontSendChildrenVariant>();
                    VerifyLinkedEnabled<NeverReplicatedEnableableFlagComponent>();

                    VerifyLinkedComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableComponent>();
                    VerifyLinkedComponentValues<SendForChildren_DontSend_SendToOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyLinkedComponentValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>();

                    VerifyLinkedComponentEnabledOnChild<ChildOnlyComponent_1>();
                    VerifyLinkedComponentEnabledOnChild<ChildOnlyComponent_2>();
                    VerifyLinkedComponentValueOnChild<ChildOnlyComponent_3>();
                    VerifyLinkedComponentValueOnChild<ChildOnlyComponent_4>();
                    break;
                case GhostTypeConverter.GhostTypes.ChildBufferComponents:
                    VerifyLinkedBufferValues<EnableableBuffer>();
                    VerifyLinkedBufferValues<EnableableBufferWithNonGhostField>();
                    VerifyLinkedBufferValues<ReplicatedFieldWithNonReplicatedEnableableBuffer>();
                    VerifyLinkedBufferValues<ReplicatedEnableableBufferWithNonReplicatedField>();
                    // We override variants for these two, so cannot test their "default variants" without massive complications.
                    VerifyLinkedBufferValues<BufferWithReplicatedVariant>();
                    VerifyLinkedEnabled<BufferWithNonReplicatedVariant>();
                    // Note: We don't test the component on the root here.
                    VerifyLinkedBufferValues<BufferWithDontSendChildrenVariant>();

                    VerifyLinkedBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>();
                    VerifyLinkedBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>();

                    //Should be these present? for what I see the setup does not add them.
                    //VerifyLinkedComponentEnabledOnChild<ChildOnlyComponent_1>();
                    //VerifyLinkedComponentEnabledOnChild<ChildOnlyComponent_2>();
                    //VerifyLinkedComponentValueOnChild<ChildOnlyComponent_3>();
                    //VerifyLinkedComponentValueOnChild<ChildOnlyComponent_4>();
                    break;
                case GhostTypeConverter.GhostTypes.GhostGroup:
                    // GhostGroup implies all of these are root entities! I.e. No children to worry about, so `_sendForChildrenTestCase` is ignored.
                    VerifyGhostGroupValues<EnableableComponent>();
                    VerifyGhostGroupValues<EnableableComponentWithNonGhostField>();
                    VerifyGhostGroupEnabledBits<EnableableFlagComponent>();
                    VerifyGhostGroupValues<ReplicatedFieldWithNonReplicatedEnableableComponent>();
                    VerifyGhostGroupValues<ReplicatedEnableableComponentWithNonReplicatedField>();
                    VerifyGhostGroupValues<ComponentWithReplicatedVariant>();
                    VerifyGhostGroupValues<ComponentWithDontSendChildrenVariant>();
                    VerifyGhostGroupValues<ComponentWithNonReplicatedVariant>();
                    VerifyGhostGroupEnabledBits<NeverReplicatedEnableableFlagComponent>();

                    VerifyGhostGroupValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableComponent>();
                    VerifyGhostGroupValues<SendForChildren_DontSend_SendToOwner_EnableableComponent>();
                    VerifyGhostGroupValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>();
                    VerifyGhostGroupValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>();
                    VerifyGhostGroupValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyGhostGroupValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyGhostGroupValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableComponent>();
                    VerifyGhostGroupValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableComponent>();
                    VerifyGhostGroupValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableComponent>();
                    VerifyGhostGroupValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableComponent>();

                    VerifyGhostGroupBufferValues<EnableableBuffer>();
                    VerifyGhostGroupBufferValues<EnableableBufferWithNonGhostField>();
                    VerifyGhostGroupBufferValues<ReplicatedFieldWithNonReplicatedEnableableBuffer>();
                    VerifyGhostGroupBufferValues<ReplicatedEnableableBufferWithNonReplicatedField>();
                    VerifyGhostGroupBufferValues<BufferWithReplicatedVariant>();
                    VerifyGhostGroupBufferValues<BufferWithDontSendChildrenVariant>();
                    VerifyGhostGroupBufferValues<BufferWithNonReplicatedVariant>();

                    VerifyGhostGroupBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNone_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<SendForChildren_DontSend_SendToOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<SendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<SendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<SendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<DontSendForChildren_OnlyPredictedGhosts_SendToNonOwner_EnableableBuffer>();
                    VerifyGhostGroupBufferValues<DontSendForChildren_OnlyInterpolatedGhosts_SendToNonOwner_EnableableBuffer>();

                    Assert.IsTrue(m_DidExpectAnyChildGhostFieldChanges.HasValue);
                    break;
                default:
                    Assert.Fail();
                    break;
            }
        }

        /// <summary>I.e. The expected value if the GhostField is NOT replicated.</summary>
        public const int kBakedValue = -33;
        /// <summary>Ensures non-GhostFields values are not clobbered by replication or backup and restore processes.</summary>
        public const int kDefaultValueForNonGhostFields = -9205;
        /// <summary>GhostGroup MEMBERS get different values (vs GhostGroup ROOT ghosts) to a) ensure test failure if testing the wrong entity and b) ensure replication correctness.</summary>
        public const int kGhostGroupMemberValueOffset = 10;
        /// <summary>Modify the size of the buffer during our first write to ensure the buffer Length change is correctly replicated.</summary>
        const int kWrittenServerBufferSize = 13;
        internal const int kBakedBufferSize = 29;

        private static readonly (Type, Type)[] s_Variants = GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride();

        NetCodeTestWorld m_TestWorld;
        NativeArray<Entity> m_ServerEntities;
        GhostTypeConverter.GhostTypes m_Type;
        private int m_ExpectedServerBufferSize;
        int m_ExpectedValueIfReplicated;
        bool m_ExpectedEnabledIfReplicated;
        bool m_ExpectedEnabledIfNotReplicated;
        SendForChildrenTestCase m_SendForChildrenTestCase;
        PredictionSetting m_PredictionSetting;
        bool m_IsValidatingBakedValues;
        bool m_ExpectChangeFilterToChange;
        uint m_LastGlobalSystemVersion;
        bool m_IsFirstRun;
        bool? m_DidExpectAnyRootGhostFieldChanges;
        bool? m_DidExpectAnyChildGhostFieldChanges;
        NetworkTick m_ServerTickBeforeSend;
        private NativeParallelHashMap<int, Entity> m_ClientGhostEntityMap;

        [Flags]
        public enum GhostFlags : int
        {
            None = 0,
            StaticOptimization = 1 << 0,
            PreSerialize = 1 << 2
        };

        void RunTest(int numClients, GhostTypeConverter.GhostTypes type, int entityCount, GhostFlags flags, SendForChildrenTestCase sendForChildrenTestCase, PredictionSetting predictionSetting, EnabledBitBakedValue enabledBitBakedValue)
        {
            // Save test vars:
            m_ExpectedEnabledIfNotReplicated = GhostTypeConverter.BakedEnabledBitValue(enabledBitBakedValue);
            m_SendForChildrenTestCase = sendForChildrenTestCase;
            m_PredictionSetting = predictionSetting;

            // Create worlds:
            switch (sendForChildrenTestCase)
            {
                case SendForChildrenTestCase.YesViaExplicitVariantRule:
                    m_TestWorld.Bootstrap(true, typeof(ForceSerializeSystem));
                    break;
                case SendForChildrenTestCase.YesViaExplicitVariantOnlyAllowChildrenToReplicateRule:
                    m_TestWorld.Bootstrap(true, typeof(ForceSerializeOnlyChildrenSystem));
                    break;
                case SendForChildrenTestCase.NoViaExplicitDontSerializeVariantRule:
                    m_TestWorld.Bootstrap(true, typeof(ForceDontSerializeSystem));
                    break;
                default:
                    m_TestWorld.Bootstrap(true);
                    break;
            }

            // Create ghosts:
            var prefabCount = 1;
            this.m_Type = type;
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
                    objects[i].AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type, enabledBitBakedValue);
                    i++;
                    objects[i] = new GameObject("ChildGhost");
                    objects[i].AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type, enabledBitBakedValue);

                    continue;
                }

                objects[i] = new GameObject("Root");
                objects[i].AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type, enabledBitBakedValue);

                if (type == GhostTypeConverter.GhostTypes.ChildComponents)
                {
                    var child = new GameObject("ChildComp");
                    child.transform.parent = objects[i].transform;
                    child.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type, enabledBitBakedValue);
                    objectsToAddInspectionsTo.Add(child);
                }
                else if (type == GhostTypeConverter.GhostTypes.ChildBufferComponents)
                {
                    var child = new GameObject("ChildBuffer");
                    child.transform.parent = objects[i].transform;
                    child.AddComponent<TestNetCodeAuthoring>().Converter = new GhostTypeConverter(type, enabledBitBakedValue);
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
                        @override.EntityIndex = default;
                    }
                }
            }

            var ghostConfig = objects[0].AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = predictionSetting == PredictionSetting.WithPredictedEntities ? GhostMode.Predicted : GhostMode.Interpolated;
            ghostConfig.SupportedGhostModes = GhostModeMask.All;
            ghostConfig.HasOwner = true;
            ghostConfig.OptimizationMode = (flags & GhostFlags.StaticOptimization) != 0 ? GhostOptimizationMode.Static : GhostOptimizationMode.Dynamic;
            ghostConfig.UsePreSerialization = (flags & GhostFlags.PreSerialize) != 0;
            if (type == GhostTypeConverter.GhostTypes.GhostGroup)
            {
                // TODO: Ghost with BOTH Children and Group.
                // TODO: Do we want to have the child the same as the root or different? This depend on what we want to test.
                // For now let's make them identical (except for the values themselves) as the test logic right now are designed to work that way,
                // but should be a little more flexible.
                var groupElementGhostConfig = objects[1].AddComponent<GhostAuthoringComponent>();
                groupElementGhostConfig.DefaultGhostMode = predictionSetting == PredictionSetting.WithPredictedEntities ? GhostMode.Predicted : GhostMode.Interpolated;
                groupElementGhostConfig.SupportedGhostModes = GhostModeMask.All;
                groupElementGhostConfig.HasOwner = true;
                // Note: OptimizationMode on ghost group elements is ignored (while they're IN the group).
                groupElementGhostConfig.OptimizationMode = (flags & GhostFlags.StaticOptimization) != 0 ? GhostOptimizationMode.Static : GhostOptimizationMode.Dynamic;
                groupElementGhostConfig.UsePreSerialization = (flags & GhostFlags.PreSerialize) != 0;
            }

            Assert.IsTrue(m_TestWorld.CreateGhostCollection(objects));

            m_TestWorld.SetTestLatencyProfile(NetCodeTestLatencyProfile.RTT16ms_PL5);
            m_TestWorld.CreateWorlds(true, numClients);

            entityCount *= prefabCount;
            m_ServerEntities = new NativeArray<Entity>(entityCount, Allocator.Persistent);

            var step = objects.Length;
            for (int i = 0; i < entityCount; i += step)
            {
                for (int j = 0; j < step; j++)
                {
                    m_ServerEntities[i + j] = m_TestWorld.SpawnOnServer(objects[j]);
                }
            }

            if (type == GhostTypeConverter.GhostTypes.GhostGroup)
            {
                for (int i = 0; i < entityCount; i += 2)
                {
                    m_TestWorld.ServerWorld.EntityManager.GetBuffer<GhostGroup>(m_ServerEntities[i]).Add(new GhostGroup {Value = m_ServerEntities[i + 1]});
                }
            }

            if (type == GhostTypeConverter.GhostTypes.ChildComponents)
            {
                foreach (var entity in m_ServerEntities)
                {
                    Assert.IsTrue(m_TestWorld.ServerWorld.EntityManager.HasComponent<LinkedEntityGroup>(entity));
                }
            }

            m_TestWorld.Connect(maxSteps:16);

            // Set every 4th to be the ghost owner.
            var networkIdOfClient0 = m_TestWorld.GetSingleton<NetworkId>(m_TestWorld.ServerWorld);
            for (int i = 0; i < m_ServerEntities.Length; i += 4)
            {
                m_TestWorld.ServerWorld.EntityManager.SetComponentData(m_ServerEntities[i], new GhostOwner
                {
                    NetworkId = networkIdOfClient0.Value,
                });
            }

            m_TestWorld.GoInGame();

            // Perform test:
            {
                m_IsFirstRun = true;
                m_ExpectChangeFilterToChange = true;

                ValidateBakedValues(flags, enabledBitBakedValue, sendForChildrenTestCase, type, predictionSetting);
                void SingleTest(int value, bool enabled, bool setGhostValues)
                {
                    m_TestWorld.TryLogPacket($"SingleTest(value:{value}, enabled:{enabled}, set:{setGhostValues})");
                    if(setGhostValues)
                        SetGhostValues(m_Type, value, enabled);
                    m_LastGlobalSystemVersion = m_TestWorld.ClientWorlds[0].EntityManager.GlobalSystemVersion;
                    m_ServerTickBeforeSend = m_TestWorld.GetSingleton<NetworkTime>(m_TestWorld.ServerWorld).ServerTick;

                    TickMultipleFrames(64);
                    VerifyGhostValues(m_Type, value, enabled);
                }

                SingleTest(-999, false, true);
                m_IsFirstRun = false;

                SingleTest(999, true, true);
                VerifyStaticOptimization(flags, forceExpect: null);

                // Testing Change Filtering: Expecting no change beyond this point!
                m_ExpectChangeFilterToChange = false;
                SingleTest(999, true, false);
                VerifyStaticOptimization(flags, forceExpect: false); // No value changes means we expect NOT to
                                                                     // have snapshots (in the case of static-optimization).
            }
        }

        /// <summary>
        /// To test whether or not a ghosts baked enabled bit status is properly respected, we need to:
        /// 1. CREATE the entities WITHOUT writing their values on the server.
        /// 2. WAIT for them to be spawned on the client.
        /// 3. VERIFY that the baked value and enabled-bit matches the baked values on the prefab.
        /// </summary>
        private void ValidateBakedValues(GhostFlags flags, EnabledBitBakedValue enabledBitBakedValue, SendForChildrenTestCase sendForChildrenTestCase, GhostTypeConverter.GhostTypes type, PredictionSetting predictionSetting)
        {
            if (GhostTypeConverter.WaitForClientEntitiesToSpawn(enabledBitBakedValue))
            {
                m_IsValidatingBakedValues = true;
                m_LastGlobalSystemVersion = m_TestWorld.ClientWorlds[0].EntityManager.GlobalSystemVersion;
                m_ServerTickBeforeSend = m_TestWorld.GetSingleton<NetworkTime>(m_TestWorld.ServerWorld).ServerTick;
                m_ExpectedServerBufferSize = kBakedBufferSize; // We haven't written to the server buffers yet.
                const int ticks = 32;
                TickMultipleFrames(ticks);
                VerifyGhostValues(m_Type, kBakedValue, GhostTypeConverter.BakedEnabledBitValue(enabledBitBakedValue));
                VerifyStaticOptimization(flags, forceExpect: true);  // It's the first time sending the ghost, so we
                                                                     // expect to receive a snapshot containing this
                                                                     // ghost, even if it has no replicated components.
            }
        }

        [SetUp]
        public void SetupTestsForEnableableBits()
        {
            m_TestWorld = new NetCodeTestWorld();
            //We need a larger payload to handle the test
            m_TestWorld.DriverFragmentedPayloadCapacity = 32 * 1024;
        }

        [TearDown]
        public void TearDownTestsForEnableableBits()
        {
            if (m_ServerEntities.IsCreated)
                m_ServerEntities.Dispose();
            if (chunkArray.IsCreated)
                chunkArray.Dispose();
            m_TestWorld.Dispose();
        }

        [Test]
        public void GhostsAreSerializedWithEnabledBits([Values]PredictionSetting predictionSetting,[Values]GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase)
        {
            RunTest(1, type, count, GhostFlags.None, sendForChildrenTestCase, predictionSetting, EnabledBitBakedValue.StartDisabledAndWriteImmediately);
        }

        [DisableAutoCreation]
        partial class ForceSerializeSystem : DefaultVariantSystemBase
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
        partial class ForceSerializeOnlyChildrenSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                var typesToOverride = GhostTypeConverter.FetchAllTestComponentTypesRequiringSendRuleOverride();
                foreach (var tuple in typesToOverride)
                {
                    defaultVariants.Add(tuple.Item1, Rule.Unique(typeof(DontSerializeVariant), tuple.Item2 ?? tuple.Item1));
                }
            }
        }
        [DisableAutoCreation]
        partial class ForceDontSerializeSystem : DefaultVariantSystemBase
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
        public void GhostsAreSerializedWithEnabledBits_PreSerialize([Values]GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count,
            [Values]SendForChildrenTestCase sendForChildrenTestCase, [Values]EnabledBitBakedValue enabledBitBakedValue)
        {
            RunTest(1, type, count, GhostFlags.PreSerialize, sendForChildrenTestCase, PredictionSetting.WithInterpolatedEntities, enabledBitBakedValue);
        }

        [Test]
        public void GhostsAreSerializedWithEnabledBits_StaticOptimize(
            [Values(GhostFlags.StaticOptimization, GhostFlags.StaticOptimization|GhostFlags.PreSerialize)]GhostFlags flags,
            [Values] GhostTypeConverter.GhostTypes type, [Values(1, 8)]int count, [Values]SendForChildrenTestCase sendForChildrenTestCase, [Values]EnabledBitBakedValue enabledBitBakedValue)
        {
            RunTest(1, type, count, flags, sendForChildrenTestCase, PredictionSetting.WithInterpolatedEntities, enabledBitBakedValue);
        }

        /// <summary>
        /// Checks attributes on component <see cref="T"/> to determine if this buffer's enable bit SHOULD be replicated.
        /// NOTE & FIXME: DOES NOT CHECK GhostComponentAttribute CONFIGURATION!
        /// </summary>
        internal static bool IsExpectedToReplicateBuffer<T>(SendForChildrenTestCase sendForChildrenTestCase, bool isRoot)
            where T : IBufferElementData
        {
            var variantType = FindTestVariantForType(typeof(T));
            var ghostComponentAttribute = GetGhostComponentAttribute(variantType);

            switch (sendForChildrenTestCase)
            {
                case SendForChildrenTestCase.YesViaExplicitVariantRule:
                case SendForChildrenTestCase.YesViaInspectionComponentOverride:
                    return true;
                case SendForChildrenTestCase.NoViaExplicitDontSerializeVariantRule:
                    return false;
                case SendForChildrenTestCase.Default:
                    return isRoot || HasSendForChildrenFlagOnAttribute(ghostComponentAttribute);
                case SendForChildrenTestCase.YesViaExplicitVariantOnlyAllowChildrenToReplicateRule:
                    return !isRoot;
                default:
                    throw new ArgumentOutOfRangeException(nameof(sendForChildrenTestCase), sendForChildrenTestCase, nameof(IsExpectedToReplicateEnabledBit));
            }
        }

        private bool IsExpectedToReplicateEnabledBit(ComponentType type, bool isRoot, bool? clientOwnsGhost)
        {
            var managedType = type.GetManagedType();
            if (!IsEnableableComponent(managedType))
                return false;

            var variantType = FindTestVariantForType(type);
            var ghostComponent = GetGhostComponentAttribute(variantType);

            if (!IsExpectedToReplicateGivenOwnerSendTypeAttribute(ghostComponent, clientOwnsGhost))
                return false;

            //this is a little wonky, and need a better handling. When we override per prefab,
            //the correct value is not the setup of the GhostComponent but the overrides of the
            //authoring that take precedence.
            //As such, technically speaking we would need to get not the ghostComponent value for
            //SendOptimisation but the value of the override for the prefab if present.
            //The assumptions in many tests are anyway that everything is enabled in case of this override
            //so we can simplify by only testing the SendOptimisation if there aren't ComponentOverride.
            if (m_SendForChildrenTestCase != SendForChildrenTestCase.YesViaInspectionComponentOverride
                && !IsExpectedToReplicateGivenSendTypeOptimizationAttribute(ghostComponent))
                return false;
            if (!HasGhostEnabledBitAttribute(variantType))
                return false;

            switch (m_SendForChildrenTestCase)
            {
                case SendForChildrenTestCase.YesViaExplicitVariantRule:
                case SendForChildrenTestCase.YesViaInspectionComponentOverride:
                    return true;
                case SendForChildrenTestCase.Default:
                    return isRoot || HasSendForChildrenFlagOnAttribute(ghostComponent);
                case SendForChildrenTestCase.YesViaExplicitVariantOnlyAllowChildrenToReplicateRule:
                    return !isRoot;
                case SendForChildrenTestCase.NoViaExplicitDontSerializeVariantRule:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(m_SendForChildrenTestCase), m_SendForChildrenTestCase, nameof(IsExpectedToReplicateEnabledBit));
            }
        }

        private static bool IsEnableableComponent(Type type) => typeof(IEnableableComponent).IsAssignableFrom(type);
        private static bool HasGhostEnabledBitAttribute(Type type) => type.GetCustomAttribute<GhostEnabledBitAttribute>() != null;

        private static Type FindTestVariantForType(ComponentType type)
        {
            var managedType = type.GetManagedType();
            var foundPair = s_Variants.FirstOrDefault(x => x.Item1 == managedType);
            if (foundPair.Item1 == null)
                return managedType;
            var variantType = foundPair.Item2 ?? foundPair.Item1;
            return variantType;
        }

        /// <summary>Checks attributes on component <see cref="T"/> to determine if this components <see cref="IComponentValue.GetValue"/> backing field should be replicated.</summary>
        private bool IsExpectedToReplicateValue(ComponentType type, bool isRoot, bool? clientOwnsGhost)
        {
            var variantType = FindTestVariantForType(type);
            var ghostComponent = GetGhostComponentAttribute(variantType);

            if (!IsExpectedToReplicateGivenOwnerSendTypeAttribute(ghostComponent, clientOwnsGhost))
                 return false;
            //this is a little wonky, and need a better handling. When we override per prefab,
            //the correct value is not the setup of the GhostComponent but the overrides of the
            //authoring that take precedence.
            //As such, technically speaking we would need to get not the ghostComponent value for
            //SendOptimisation but the value of the override for the prefab if present.
            //The assumptions in many tests are anyway that everything is enabled in case of this override
            //so we can simplify by only testing the SendOptimisation if there aren't ComponentOverride.
            if (m_SendForChildrenTestCase != SendForChildrenTestCase.YesViaInspectionComponentOverride &&
                !IsExpectedToReplicateGivenSendTypeOptimizationAttribute(ghostComponent))
                return false;
            if (!HasGhostFieldMainValue(variantType))
                return false;

            switch (m_SendForChildrenTestCase)
            {
                case SendForChildrenTestCase.YesViaExplicitVariantRule:
                case SendForChildrenTestCase.YesViaInspectionComponentOverride:
                    return true;
                case SendForChildrenTestCase.Default:
                    return isRoot || HasSendForChildrenFlagOnAttribute(ghostComponent);
                case SendForChildrenTestCase.YesViaExplicitVariantOnlyAllowChildrenToReplicateRule:
                    return !isRoot;
                case SendForChildrenTestCase.NoViaExplicitDontSerializeVariantRule:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(m_SendForChildrenTestCase), m_SendForChildrenTestCase, nameof(IsExpectedToReplicateValue));
            }
        }

        private static GhostComponentAttribute GetGhostComponentAttribute(Type variantType)
        {
            return variantType.GetCustomAttribute(typeof(GhostComponentAttribute)) as GhostComponentAttribute ?? new GhostComponentAttribute();
        }

        private static bool HasSendForChildrenFlagOnAttribute(GhostComponentAttribute attribute) => attribute != null && attribute.SendDataForChildEntity;

        private bool IsExpectedToReplicateGivenOwnerSendTypeAttribute(GhostComponentAttribute attribute, bool? clientOwnsGhost)
        {
            switch (attribute.OwnerSendType)
            {
                case SendToOwnerType.None:
                    return false;
                case SendToOwnerType.SendToOwner:
                    return clientOwnsGhost ?? true;
                case SendToOwnerType.SendToNonOwner:
                    return !clientOwnsGhost ?? true;
                case SendToOwnerType.All:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attribute.OwnerSendType), attribute.OwnerSendType, nameof(IsExpectedToReplicateGivenOwnerSendTypeAttribute));
            }
        }

        private bool IsExpectedToReplicateGivenSendTypeOptimizationAttribute(GhostComponentAttribute attribute)
        {
            switch (attribute.SendTypeOptimization)
            {
                case GhostSendType.DontSend:
                    return false;
                case GhostSendType.OnlyInterpolatedClients:
                    return m_PredictionSetting == PredictionSetting.WithInterpolatedEntities;
                case GhostSendType.OnlyPredictedClients:
                    return m_PredictionSetting == PredictionSetting.WithPredictedEntities;
                case GhostSendType.AllClients:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attribute.SendTypeOptimization), attribute.SendTypeOptimization, nameof(IsExpectedToReplicateGivenSendTypeOptimizationAttribute));
            }
        }

        static bool HasGhostFieldMainValue(Type type)
        {
            var ghostFieldAttribute = type.GetField("value", BindingFlags.Instance | BindingFlags.Public)?.GetCustomAttribute<GhostFieldAttribute>();
            return ghostFieldAttribute != null && ghostFieldAttribute.SendData;
        }

        /// <summary>Ensure the GhostUpdateSystem doesn't corrupt fields without the <see cref="GhostFieldAttribute"/>.</summary>
        public static void EnsureNonGhostFieldValueIsNotClobbered(int nonGhostField)
        {
            Assert.AreEqual(kDefaultValueForNonGhostFields, nonGhostField, $"Expecting `nonGhostField` has not been clobbered by changes to this component!");
        }
    }
}
