using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Netcode;
using Unity.Netcode.Tests;

namespace Tests.Editor
{
    /// <summary>
    /// Unit tests for <see cref="WorldDataManagedExtensions"/>: the parent/child grouping helper
    /// (<see cref="WorldDataManagedExtensions.AssignParentExecutionOrders"/>) and the per-component
    /// deserialization the tracing view performs when turning a saved state blob into managed
    /// <see cref="IComponentData"/> instances for display.
    /// Each grouping entry is a single collapsed system carrying the execution-order range it spanned across its
    /// trace positions: [ExecutionOrder, MaxExecutionOrder]. A group's range brackets its children.
    /// </summary>
    class WorldDataManagedExtensionsTests
    {
        const int k_NoParent = WorldDataManagedExtensions.NoParentExecutionOrder;

        // A leaf defaults to a single-point range; groups pass an explicit maxExecutionOrder to bracket children.
        static ManagedSystemData MakeSystem(string name, int executionOrder, int maxExecutionOrder = -1, bool isGroup = false)
        {
            return new ManagedSystemData
            {
                SystemName = name,
                SystemNameShort = name,
                ExecutionOrder = executionOrder,
                MaxExecutionOrder = maxExecutionOrder < 0 ? executionOrder : maxExecutionOrder,
                IsSystemGroup = isGroup,
            };
        }

        static ManagedSystemData MakeGroup(string name, int min, int max) => MakeSystem(name, min, max, isGroup: true);

        static int ParentOf(List<ManagedSystemData> systems, int executionOrder)
        {
            foreach (var s in systems)
                if (s.ExecutionOrder == executionOrder)
                    return s.ParentExecutionOrder;
            Assert.Fail($"No entry with ExecutionOrder {executionOrder}");
            return default;
        }

        [Test]
        public void EmptyList_DoesNotThrow()
        {
            var systems = new List<ManagedSystemData>();
            Assert.DoesNotThrow(() => WorldDataManagedExtensions.AssignParentExecutionOrders(systems));
            Assert.That(systems, Is.Empty);
        }

        [Test]
        public void SingleNonGroupSystem_IsRoot()
        {
            var systems = new List<ManagedSystemData> { MakeSystem("A", 1) };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(systems[0].ParentExecutionOrder, Is.EqualTo(k_NoParent));
        }

        [Test]
        public void SingleGroup_IsRoot()
        {
            var systems = new List<ManagedSystemData> { MakeGroup("G", 1, 3) };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(systems[0].ParentExecutionOrder, Is.EqualTo(k_NoParent));
        }

        [Test]
        public void SystemContainedByGroup_ParentsToGroup()
        {
            // G spans [1..3]; the system at 2 sits inside it.
            var systems = new List<ManagedSystemData>
            {
                MakeGroup("G", 1, 3),
                MakeSystem("S", 2),
            };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(ParentOf(systems, 1), Is.EqualTo(k_NoParent));
            Assert.That(ParentOf(systems, 2), Is.EqualTo(1), "child system parents to the containing group");
        }

        [Test]
        public void NestedGroups_ChildPicksInnermostContainingGroup()
        {
            // Outer [1..7], Inner [2..6]. System at 4 should pick Inner, not Outer.
            var systems = new List<ManagedSystemData>
            {
                MakeGroup("Outer", 1, 7),
                MakeGroup("Inner", 2, 6),
                MakeSystem("C", 4),
            };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(ParentOf(systems, 1), Is.EqualTo(k_NoParent), "Outer is root");
            Assert.That(ParentOf(systems, 2), Is.EqualTo(1), "Inner parents to Outer");
            Assert.That(ParentOf(systems, 4), Is.EqualTo(2), "child parents to Inner (innermost containing group)");
        }

        [Test]
        public void MultipleChildrenOfSameGroup_AllParentToGroup()
        {
            // G [1..5] containing three leaf systems.
            var systems = new List<ManagedSystemData>
            {
                MakeGroup("G", 1, 5),
                MakeSystem("C1", 2),
                MakeSystem("C2", 3),
                MakeSystem("C3", 4),
            };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(ParentOf(systems, 2), Is.EqualTo(1));
            Assert.That(ParentOf(systems, 3), Is.EqualTo(1));
            Assert.That(ParentOf(systems, 4), Is.EqualTo(1));
        }

        [Test]
        public void SiblingGroups_DoNotParentToEachOther()
        {
            // Non-overlapping ranges: A [1..2], B [3..4].
            var systems = new List<ManagedSystemData>
            {
                MakeGroup("A", 1, 2),
                MakeGroup("B", 3, 4),
            };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(ParentOf(systems, 1), Is.EqualTo(k_NoParent));
            Assert.That(ParentOf(systems, 3), Is.EqualTo(k_NoParent));
        }

        [Test]
        public void SystemOutsideGroupRange_IsRoot()
        {
            // G [1..2]; the system at 3 is outside its range.
            var systems = new List<ManagedSystemData>
            {
                MakeGroup("G", 1, 2),
                MakeSystem("S", 3),
            };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(ParentOf(systems, 1), Is.EqualTo(k_NoParent));
            Assert.That(ParentOf(systems, 3), Is.EqualTo(k_NoParent));
        }

        [Test]
        public void NestedGroupAndItsChildren_ResolveCorrectly()
        {
            // Outer [1..9] { Inner [2..6] { C1 3, C2 5 }, C3 8 }
            var systems = new List<ManagedSystemData>
            {
                MakeGroup("Outer", 1, 9),
                MakeGroup("Inner", 2, 6),
                MakeSystem("C1", 3),
                MakeSystem("C2", 5),
                MakeSystem("C3", 8),
            };

            WorldDataManagedExtensions.AssignParentExecutionOrders(systems);

            Assert.That(ParentOf(systems, 2), Is.EqualTo(1), "Inner -> Outer");
            Assert.That(ParentOf(systems, 3), Is.EqualTo(2), "C1 -> Inner");
            Assert.That(ParentOf(systems, 5), Is.EqualTo(2), "C2 -> Inner");
            Assert.That(ParentOf(systems, 8), Is.EqualTo(1), "C3 -> Outer (outside Inner's range)");
        }

        // Distinct, recognisable values across every primitive type so a misaligned read can't accidentally
        // reproduce the original. The leading bool is what trips up Marshal.PtrToStructure.
        static TestComponentB MakeSampleComponent() => new TestComponentB
        {
            valueBool = true,
            valueByte = 0x11,
            valueSbyte = 0x22,
            valueDouble = 1234.5678,
            valueFloat = 9876.5f,
            valueInt = 0x12345678,
            valueUint = 0x9ABCDEF0,
            valueNint = unchecked((nint)0x0123456789AB),
            valueNuint = unchecked((nuint)0x0FEDCBA987654321),
            valueLong = 0x1122334455667788,
            valueUlong = 0x99AABBCCDDEEFF00,
            valueShort = 0x0F0F,
            valueUshort = unchecked((ushort)0xF0F0),
        };

        static bool FieldsEqual(in TestComponentB a, in TestComponentB b) =>
            a.valueBool == b.valueBool &&
            a.valueByte == b.valueByte &&
            a.valueSbyte == b.valueSbyte &&
            a.valueDouble.Equals(b.valueDouble) &&
            a.valueFloat.Equals(b.valueFloat) &&
            a.valueInt == b.valueInt &&
            a.valueUint == b.valueUint &&
            a.valueNint == b.valueNint &&
            a.valueNuint == b.valueNuint &&
            a.valueLong == b.valueLong &&
            a.valueUlong == b.valueUlong &&
            a.valueShort == b.valueShort &&
            a.valueUshort == b.valueUshort;

        // Stores the component on a real entity, then reads its bytes back the exact way the state save job
        // does (GetDynamicComponentDataArrayReinterpret<byte> over the chunk). The given deserializer is run
        // against those DOTS-serialized bytes while the world (and therefore the chunk memory) is still alive.
        static unsafe T DeserializeFromDotsChunk<T>(in T value, Func<IntPtr, TypeIndex, T> deserialize)
            where T : unmanaged, IComponentData
        {
            using var world = new World("WorldDataManagedExtensionsTests");
            var em = world.EntityManager;
            var entity = em.CreateEntity(typeof(T));
            em.SetComponentData(entity, value);

            var typeIndex = TypeManager.GetTypeIndex<T>();
            var sizeInChunk = TypeManager.GetTypeInfo(typeIndex).SizeInChunk;
            var handle = em.GetDynamicComponentTypeHandle(ComponentType.ReadOnly<T>());
            var chunk = em.GetChunk(entity);
            var bytes = chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, sizeInChunk);
            byte* ptr = (byte*)bytes.GetUnsafeReadOnlyPtr();
            return deserialize((IntPtr)ptr, typeIndex);
        }

        [Test, Description("ConstructComponentFromBuffer reads DOTS/ECS-serialized component.")]
        public unsafe void ConstructComponentFromBuffer_TestAllTypes()
        {
            var expected = MakeSampleComponent();

            var actual = DeserializeFromDotsChunk(expected,
                (ptr, typeIndex) => (TestComponentB)TypeManager.ConstructComponentFromBuffer(typeIndex, (void*)ptr));

            Assert.That(actual.valueBool, Is.EqualTo(expected.valueBool));
            Assert.That(actual.valueByte, Is.EqualTo(expected.valueByte));
            Assert.That(actual.valueSbyte, Is.EqualTo(expected.valueSbyte));
            Assert.That(actual.valueDouble, Is.EqualTo(expected.valueDouble));
            Assert.That(actual.valueFloat, Is.EqualTo(expected.valueFloat));
            Assert.That(actual.valueInt, Is.EqualTo(expected.valueInt));
            Assert.That(actual.valueUint, Is.EqualTo(expected.valueUint));
            Assert.That(actual.valueNint, Is.EqualTo(expected.valueNint));
            Assert.That(actual.valueNuint, Is.EqualTo(expected.valueNuint));
            Assert.That(actual.valueLong, Is.EqualTo(expected.valueLong));
            Assert.That(actual.valueUlong, Is.EqualTo(expected.valueUlong));
            Assert.That(actual.valueShort, Is.EqualTo(expected.valueShort));
            Assert.That(actual.valueUshort, Is.EqualTo(expected.valueUshort));
        }
    }
}
