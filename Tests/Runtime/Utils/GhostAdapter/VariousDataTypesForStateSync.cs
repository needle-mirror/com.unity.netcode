#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using Unity.Assertions;
using Unity.Entities;

namespace Unity.NetCode.Tests
{
    internal struct SimpleStruct : IComponentData
    {
        [GhostField] public int someField;
    }

    internal struct SimpleStructForInit : IComponentData
    {
        [GhostField] public int someField;
    }

    internal struct NonGhostStruct : IComponentData
    {
        public int nonGhostField;
    }

    internal struct EmptyStruct : IComponentData
    {

    }

    internal struct ComposedComponent : IComponentData
    {
        internal struct SomeChildStruct : IComponentData
        {
            [GhostField] public int intField;
        }

        public SomeChildStruct someChildStruct;
        [GhostField] public int someIntField;
    }

    internal struct NonComponentStruct
    {
        public int someField;
    }

    struct SomeComponent1 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent2 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent3 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent4 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent5 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent6 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent7 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent8 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent9 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent10 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent11 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent12 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent13 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent14 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent15 : IComponentData { [GhostField] public int someValue; }
    struct SomeComponent16 : IComponentData { [GhostField] public int someValue; }

    internal partial class VariousDataTypesForStateSync : GhostBehaviour
    {
        // test variables
        public GhostField<int> someInt;
        public GhostField<float> someFloat;
        public GhostField<long> someLong;

        // different accesses
        internal GhostField<int> someInternalField;
        private GhostField<int> m_SomePrivateField;
        public GhostField<int> SomeProperty => m_SomePrivateField; // test that this isn't included in source gen, since it's only a property

        // different structs
        public GhostField<NonComponentStruct> nonComponentStruct;
        public GhostField<SimpleStruct> simpleStruct;
        public GhostField<NonGhostStruct> nonGhostStruct;
        public GhostField<ComposedComponent> composedComponent;
        // public GhostField<GhostAdapter> ghostLink; // TODO-release

        // test bridges
        public GhostComponentRef<SimpleStruct> SimpleStructBridged;
        public GhostComponentRef<NonGhostStruct> NonGhostStructBridged;
        public GhostComponentRef<EmptyStruct> EmptyStructBridged;
        public GhostComponentRef<ComposedComponent> ComposedComponentBridged;

        // test initial value
        public GhostField<int> SomeValueInit = new(100);
        public GhostComponentRef<SimpleStructForInit> SimpleStructInit = new(new SimpleStructForInit() { someField = 100 });

        // test settings
        public GhostField<float> quantizedVariable = new(fieldConfig: new() { Quantization = 1 });

        // test many components on the same entity, above 15 (for the ComponentTypeSet limit)
        // Shouldn't use GhostField for this, since the goal eventually is to have some smart merging of generated components
        public GhostComponentRef<SomeComponent1> bridgeVar1;
        public GhostComponentRef<SomeComponent2> bridgeVar2;
        public GhostComponentRef<SomeComponent3> bridgeVar3;
        public GhostComponentRef<SomeComponent4> bridgeVar4;
        public GhostComponentRef<SomeComponent5> bridgeVar5;
        public GhostComponentRef<SomeComponent6> bridgeVar6;
        public GhostComponentRef<SomeComponent7> bridgeVar7;
        public GhostComponentRef<SomeComponent8> bridgeVar8;
        public GhostComponentRef<SomeComponent9> bridgeVar9;
        public GhostComponentRef<SomeComponent10> bridgeVar10;
        public GhostComponentRef<SomeComponent11> bridgeVar11;
        public GhostComponentRef<SomeComponent12> bridgeVar12;
        public GhostComponentRef<SomeComponent13> bridgeVar13;
        public GhostComponentRef<SomeComponent14> bridgeVar14;
        public GhostComponentRef<SomeComponent15> bridgeVar15;
        public GhostComponentRef<SomeComponent16> bridgeVar16;

        public void ValidateValues(int expected, float expectedQuantized, int nonReplicatedExpected)
        {
            Assert.AreEqual(expected, someInt.Value);
            Assert.AreEqual(expected, someFloat.Value);
            Assert.AreEqual(expected, someLong.Value);
            Assert.AreEqual(expected, someInternalField.Value);
            Assert.AreEqual(expected, m_SomePrivateField.Value);
            Assert.AreEqual(expected, SomeProperty.Value); // test that this isn't included in source gen, since it's only a property
            Assert.AreEqual(expected, nonComponentStruct.Value.someField);
            Assert.AreEqual(expected, simpleStruct.Value.someField);
            Assert.AreEqual(expected, nonGhostStruct.Value.nonGhostField);
            Assert.AreEqual(expected, composedComponent.Value.someIntField);
            Assert.AreEqual(expected, composedComponent.Value.someChildStruct.intField);
            Assert.AreEqual(expected, SimpleStructBridged.Value.someField);
            Assert.AreEqual(nonReplicatedExpected, NonGhostStructBridged.Value.nonGhostField);
            Assert.AreEqual(expected, ComposedComponentBridged.Value.someIntField);
            Assert.AreEqual(expected+100, SomeValueInit.Value);
            Assert.AreEqual(expected+100, SimpleStructInit.Value.someField);
            Assert.AreApproximatelyEqual(expectedQuantized, quantizedVariable.Value, 0.000_01f);
            Assert.IsTrue(this.Ghost.World.EntityManager.HasComponent<EmptyStruct>(this.Ghost.Entity), "check for the presence of the empty struct, should still have a component for it");

            Assert.AreEqual(expected, bridgeVar1.Value.someValue);
            Assert.AreEqual(expected, bridgeVar2.Value.someValue);
            Assert.AreEqual(expected, bridgeVar3.Value.someValue);
            Assert.AreEqual(expected, bridgeVar4.Value.someValue);
            Assert.AreEqual(expected, bridgeVar5.Value.someValue);
            Assert.AreEqual(expected, bridgeVar6.Value.someValue);
            Assert.AreEqual(expected, bridgeVar7.Value.someValue);
            Assert.AreEqual(expected, bridgeVar8.Value.someValue);
            Assert.AreEqual(expected, bridgeVar9.Value.someValue);
            Assert.AreEqual(expected, bridgeVar10.Value.someValue);
            Assert.AreEqual(expected, bridgeVar11.Value.someValue);
            Assert.AreEqual(expected, bridgeVar12.Value.someValue);
            Assert.AreEqual(expected, bridgeVar13.Value.someValue);
            Assert.AreEqual(expected, bridgeVar14.Value.someValue);
            Assert.AreEqual(expected, bridgeVar15.Value.someValue);
            Assert.AreEqual(expected, bridgeVar16.Value.someValue);
        }

        public void IncrementValues()
        {
            someInt.Value += 1;
            someFloat.Value += 1;
            someLong.Value += 1;
            someInternalField.Value += 1;
            var prop = SomeProperty;
            prop.Value += 1;

            var value = nonComponentStruct.Value;
            value.someField += 1;
            nonComponentStruct.Value = value;
            var simpleStructValue = simpleStruct.Value;
            simpleStructValue.someField += 1;
            simpleStruct.Value = simpleStructValue;
            var ghostStruct = nonGhostStruct.Value;
            ghostStruct.nonGhostField += 1;
            nonGhostStruct.Value = ghostStruct;
            var component = composedComponent.Value;
            component.someIntField += 1;
            composedComponent.Value = component;
            var valueSomeChildStruct = composedComponent.Value.someChildStruct;
            valueSomeChildStruct.intField += 1;
            var componentValue = composedComponent.Value;
            componentValue.someChildStruct = valueSomeChildStruct;
            composedComponent.Value = componentValue;

            var simpleStructBridgedValue = SimpleStructBridged.Value;
            simpleStructBridgedValue.someField += 1;
            SimpleStructBridged.Value = simpleStructBridgedValue;
            var nonGhostStructBridged = NonGhostStructBridged.Value;
            nonGhostStructBridged.nonGhostField += 1;
            NonGhostStructBridged.Value = nonGhostStructBridged;
            var composedValueBridged = ComposedComponentBridged.Value;
            composedValueBridged.someIntField += 1;
            ComposedComponentBridged.Value = composedValueBridged;
            var childStructBridged = ComposedComponentBridged.Value.someChildStruct;
            childStructBridged.intField += 1;
            var value1 = ComposedComponentBridged.Value;
            value1.someChildStruct = childStructBridged;
            ComposedComponentBridged.Value = value1;

            SomeValueInit.Value += 1;
            var structForInitCheck = SimpleStructInit.Value;
            structForInitCheck.someField += 1;
            SimpleStructInit.Value = structForInitCheck;

            quantizedVariable.Value += 0.1f;

            var component1 = bridgeVar1.Value;
            component1.someValue += 1;
            bridgeVar1.Value = component1;
            var component2 = bridgeVar2.Value;
            component2.someValue += 1;
            bridgeVar2.Value = component2;
            var component3 = bridgeVar3.Value;
            component3.someValue += 1;
            bridgeVar3.Value = component3;
            var component4 = bridgeVar4.Value;
            component4.someValue += 1;
            bridgeVar4.Value = component4;
            var component5 = bridgeVar5.Value;
            component5.someValue += 1;
            bridgeVar5.Value = component5;
            var component6 = bridgeVar6.Value;
            component6.someValue += 1;
            bridgeVar6.Value = component6;
            var component7 = bridgeVar7.Value;
            component7.someValue += 1;
            bridgeVar7.Value = component7;
            var component8 = bridgeVar8.Value;
            component8.someValue += 1;
            bridgeVar8.Value = component8;
            var component9 = bridgeVar9.Value;
            component9.someValue += 1;
            bridgeVar9.Value = component9;
            var component10 = bridgeVar10.Value;
            component10.someValue += 1;
            bridgeVar10.Value = component10;
            var component11 = bridgeVar11.Value;
            component11.someValue += 1;
            bridgeVar11.Value = component11;
            var component12 = bridgeVar12.Value;
            component12.someValue += 1;
            bridgeVar12.Value = component12;
            var component13 = bridgeVar13.Value;
            component13.someValue += 1;
            bridgeVar13.Value = component13;
            var component14 = bridgeVar14.Value;
            component14.someValue += 1;
            bridgeVar14.Value = component14;
            var component15 = bridgeVar15.Value;
            component15.someValue += 1;
            bridgeVar15.Value = component15;
            var component16 = bridgeVar16.Value;
            component16.someValue += 1;
            bridgeVar16.Value = component16;
        }
    }
}
#endif
