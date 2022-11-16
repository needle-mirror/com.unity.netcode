using System;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
// ReSharper disable InconsistentNaming
// ReSharper disable ParameterHidesMember

namespace Unity.NetCode.Tests
{
    public class GhostTypeConverter : TestNetCodeAuthoring.IConverter
    {
        public enum GhostTypes
        {
            EnableableComponent,
            MultipleEnableableComponent,
            EnableableBuffer,
            MultipleEnableableBuffer,
            ChildComponent,
            ChildBufferComponent,
            GhostGroup,
            // TODO: Support GhostGroupBuffers!
        }

        GhostTypes type;
        public GhostTypeConverter(GhostTypes ghostType)
        {
            type = ghostType;
        }
        public void Bake(GameObject gameObject, IBaker baker)
        {
            switch (type)
            {
                case GhostTypes.EnableableComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    AddTestEnableableComponents(baker);
                    break;
                case GhostTypes.MultipleEnableableComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    SetupMultipleEnableableComponents(baker);
                    break;
                case GhostTypes.EnableableBuffer:
                    baker.AddComponent(new GhostOwnerComponent());
                    AddBufferWithLength<EnableableBuffer>(baker);
                    // TODO - Same tests for buffers.
                    break;
                case GhostTypes.MultipleEnableableBuffer:
                    baker.AddComponent(new GhostOwnerComponent());
                    SetupMultipleEnableableBuffer(baker);
                    break;
                case GhostTypes.ChildComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    AddTestEnableableComponents(baker);
                    var transform = baker.GetComponent<Transform>();
                    baker.DependsOn(transform.parent);
                    if (transform.parent == null)
                        baker.AddComponent(new TopLevelGhostEntity());
                    break;
                case GhostTypes.ChildBufferComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    AddBufferWithLength<EnableableBuffer>(baker);
                    if (gameObject.transform.parent == null)
                        baker.AddComponent(new TopLevelGhostEntity());
                    break;
                case GhostTypes.GhostGroup:
                    baker.AddComponent(new GhostOwnerComponent());
                    // Dependency on the name
                    baker.DependsOn(gameObject);
                    if (gameObject.name.StartsWith("ParentGhost"))
                    {
                        baker.AddBuffer<GhostGroup>();
                        baker.AddComponent(default(GhostGroupRoot));
                        AddTestEnableableComponents(baker);
                    }
                    else
                    {
                        baker.AddComponent(default(GhostChildEntityComponent));
                        AddTestEnableableComponents(baker);
                    }
                    break;
                default:
                    Assert.True(false);
                    break;
            }
        }

        /// <returns>Item1 is the ComponentType. Item2 is the VariantType (or null if same as ComponentType).</returns>
        internal static ValueTuple<Type, Type>[] FetchAllTestComponentTypesRequiringSendRuleOverride()
        {
            return new[]
            {
                (typeof(EnableableComponent), null),
                (typeof(EnableableFlagComponent), null),
                (typeof(ReplicatedFieldWithNonReplicatedEnableableComponent), null),
                (typeof(ReplicatedEnableableComponentWithNonReplicatedField), null),
                (typeof(ComponentWithVariant), typeof(ComponentWithVariantVariation)),
                (typeof(ComponentWithNonReplicatedVariant), typeof(ComponentWithNonReplicatedVariantVariation)),
                // Skipped as never replicated. (typeof(NeverReplicatedEnableableFlagComponent), null),

                (typeof(EnableableComponent_0), null),
                (typeof(EnableableComponent_1), null),
                (typeof(EnableableComponent_2), null),
                (typeof(EnableableComponent_3), null),
                (typeof(EnableableComponent_4), null),
                (typeof(EnableableComponent_5), null),
                (typeof(EnableableComponent_6), null),
                (typeof(EnableableComponent_7), null),
                (typeof(EnableableComponent_8), null),
                (typeof(EnableableComponent_9), null),
                (typeof(EnableableComponent_10), null),
                (typeof(EnableableComponent_11), null),
                (typeof(EnableableComponent_12), null),
                (typeof(EnableableComponent_13), null),
                (typeof(EnableableComponent_14), null),
                (typeof(EnableableComponent_15), null),
                (typeof(EnableableComponent_16), null),
                (typeof(EnableableComponent_17), null),
                (typeof(EnableableComponent_18), null),
                (typeof(EnableableComponent_19), null),
                (typeof(EnableableComponent_20), null),
                (typeof(EnableableComponent_21), null),
                (typeof(EnableableComponent_22), null),
                (typeof(EnableableComponent_23), null),
                (typeof(EnableableComponent_24), null),
                (typeof(EnableableComponent_25), null),
                (typeof(EnableableComponent_26), null),
                (typeof(EnableableComponent_27), null),
                (typeof(EnableableComponent_28), null),
                (typeof(EnableableComponent_29), null),
                (typeof(EnableableComponent_30), null),
                (typeof(EnableableComponent_31), null),
                (typeof(EnableableComponent_32), null),

                (typeof(EnableableBuffer), null),
                (typeof(EnableableBuffer_0), null),
                (typeof(EnableableBuffer_1), null),
                (typeof(EnableableBuffer_2), null),
                (typeof(EnableableBuffer_3), null),
                (typeof(EnableableBuffer_4), null),
                (typeof(EnableableBuffer_5), null),
                (typeof(EnableableBuffer_6), null),
                (typeof(EnableableBuffer_7), null),
                (typeof(EnableableBuffer_8), null),
                (typeof(EnableableBuffer_9), null),
                (typeof(EnableableBuffer_10), null),
                (typeof(EnableableBuffer_11), null),
                (typeof(EnableableBuffer_12), null),
                (typeof(EnableableBuffer_13), null),
                (typeof(EnableableBuffer_14), null),
                (typeof(EnableableBuffer_15), null),
                (typeof(EnableableBuffer_16), null),
                (typeof(EnableableBuffer_17), null),
                (typeof(EnableableBuffer_18), null),
                (typeof(EnableableBuffer_19), null),
                (typeof(EnableableBuffer_20), null),
                (typeof(EnableableBuffer_21), null),
                (typeof(EnableableBuffer_22), null),
                (typeof(EnableableBuffer_23), null),
                (typeof(EnableableBuffer_24), null),
                (typeof(EnableableBuffer_25), null),
                (typeof(EnableableBuffer_26), null),
                (typeof(EnableableBuffer_27), null),
                (typeof(EnableableBuffer_28), null),
                (typeof(EnableableBuffer_29), null),
                (typeof(EnableableBuffer_30), null),
                (typeof(EnableableBuffer_31), null),
                (typeof(EnableableBuffer_32), null),
            };
        }

        static void AddTestEnableableComponents(IBaker baker)
        {
            baker.AddComponent<EnableableComponent>();
            baker.AddComponent<EnableableFlagComponent>();
            baker.AddComponent<ReplicatedFieldWithNonReplicatedEnableableComponent>();
            baker.AddComponent<ReplicatedEnableableComponentWithNonReplicatedField>();
            baker.AddComponent<NeverReplicatedEnableableFlagComponent>();
            baker.AddComponent<ComponentWithVariant>();
            baker.AddComponent<ComponentWithNonReplicatedVariant>();
        }

        static void SetupMultipleEnableableComponents(IBaker baker)
        {
            baker.AddComponent<EnableableComponent_0>();
            baker.AddComponent<EnableableComponent_1>();
            baker.AddComponent<EnableableComponent_2>();
            baker.AddComponent<EnableableComponent_3>();
            baker.AddComponent<EnableableComponent_4>();
            baker.AddComponent<EnableableComponent_5>();
            baker.AddComponent<EnableableComponent_6>();
            baker.AddComponent<EnableableComponent_7>();
            baker.AddComponent<EnableableComponent_8>();
            baker.AddComponent<EnableableComponent_9>();
            baker.AddComponent<EnableableComponent_10>();
            baker.AddComponent<EnableableComponent_11>();
            baker.AddComponent<EnableableComponent_12>();
            baker.AddComponent<EnableableComponent_13>();
            baker.AddComponent<EnableableComponent_14>();
            baker.AddComponent<EnableableComponent_15>();
            baker.AddComponent<EnableableComponent_16>();
            baker.AddComponent<EnableableComponent_17>();
            baker.AddComponent<EnableableComponent_18>();
            baker.AddComponent<EnableableComponent_19>();
            baker.AddComponent<EnableableComponent_20>();
            baker.AddComponent<EnableableComponent_21>();
            baker.AddComponent<EnableableComponent_22>();
            baker.AddComponent<EnableableComponent_23>();
            baker.AddComponent<EnableableComponent_24>();
            baker.AddComponent<EnableableComponent_25>();
            baker.AddComponent<EnableableComponent_26>();
            baker.AddComponent<EnableableComponent_27>();
            baker.AddComponent<EnableableComponent_28>();
            baker.AddComponent<EnableableComponent_29>();
            baker.AddComponent<EnableableComponent_30>();
            baker.AddComponent<EnableableComponent_31>();
            baker.AddComponent<EnableableComponent_32>();
        }

        static void AddBufferWithLength<T>(IBaker baker)
            where T : unmanaged, IBufferElementData
        {
            var enableableBuffers = baker.AddBuffer<T>();
            enableableBuffers.Length = GhostSerializationTestsForEnableableBits.kClientBufferSize;
        }

        static void SetupMultipleEnableableBuffer(IBaker baker)
        {
            AddBufferWithLength<EnableableBuffer_0>(baker);
            AddBufferWithLength<EnableableBuffer_1>(baker);
            AddBufferWithLength<EnableableBuffer_2>(baker);
            AddBufferWithLength<EnableableBuffer_3>(baker);
            AddBufferWithLength<EnableableBuffer_4>(baker);
            AddBufferWithLength<EnableableBuffer_5>(baker);
            AddBufferWithLength<EnableableBuffer_6>(baker);
            AddBufferWithLength<EnableableBuffer_7>(baker);
            AddBufferWithLength<EnableableBuffer_8>(baker);
            AddBufferWithLength<EnableableBuffer_9>(baker);
            AddBufferWithLength<EnableableBuffer_10>(baker);
            AddBufferWithLength<EnableableBuffer_11>(baker);
            AddBufferWithLength<EnableableBuffer_12>(baker);
            AddBufferWithLength<EnableableBuffer_13>(baker);
            AddBufferWithLength<EnableableBuffer_14>(baker);
            AddBufferWithLength<EnableableBuffer_15>(baker);
            AddBufferWithLength<EnableableBuffer_16>(baker);
            AddBufferWithLength<EnableableBuffer_17>(baker);
            AddBufferWithLength<EnableableBuffer_18>(baker);
            AddBufferWithLength<EnableableBuffer_19>(baker);
            AddBufferWithLength<EnableableBuffer_20>(baker);
            AddBufferWithLength<EnableableBuffer_21>(baker);
            AddBufferWithLength<EnableableBuffer_22>(baker);
            AddBufferWithLength<EnableableBuffer_23>(baker);
            AddBufferWithLength<EnableableBuffer_24>(baker);
            AddBufferWithLength<EnableableBuffer_25>(baker);
            AddBufferWithLength<EnableableBuffer_26>(baker);
            AddBufferWithLength<EnableableBuffer_27>(baker);
            AddBufferWithLength<EnableableBuffer_28>(baker);
            AddBufferWithLength<EnableableBuffer_29>(baker);
            AddBufferWithLength<EnableableBuffer_30>(baker);
            AddBufferWithLength<EnableableBuffer_31>(baker);
            AddBufferWithLength<EnableableBuffer_32>(baker);
        }
    }

    public interface IComponentValue
    {
        void SetValue(int value);
        int GetValue();
    }

    [GhostComponent(SendDataForChildEntity = false)]
    [GhostEnabledBit]
    public struct EnableableBuffer : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    [GhostEnabledBit]
    public struct EnableableComponent: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    /// <summary>Enable flag SHOULD BE replicated.</summary>
    [GhostEnabledBit]
    public struct EnableableFlagComponent : IComponentData, IEnableableComponent
    {
    }

    /// <summary>Enable flag should NOT BE replicated.</summary>
    public struct NeverReplicatedEnableableFlagComponent : IComponentData, IEnableableComponent
    {
    }

    /// <summary>Enable flag should NOT BE replicated, but the field A SHOULD BE.</summary>
    public struct ReplicatedFieldWithNonReplicatedEnableableComponent : IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField]
        public int value;

        public void SetValue(int value) => this.value = value;

        public int GetValue() => value;
    }

    /// <summary>Enable flag SHOULD BE replicated, but the field B should NOT BE.</summary>
    [GhostEnabledBit]
    public struct ReplicatedEnableableComponentWithNonReplicatedField : IComponentData, IEnableableComponent, IComponentValue
    {
        public int value;

        public void SetValue(int value) => this.value = value;

        public int GetValue() => value;
    }

    public struct ComponentWithVariant  : IComponentData, IEnableableComponent, IComponentValue
    {
        public int value;

        public void SetValue(int value) => this.value = value;

        public int GetValue() => value;
    }

    // As this is the only variant, it becomes the default variant.
    [GhostComponentVariation(typeof(ComponentWithVariant), "ReplicatedVariation")]
    [GhostEnabledBit]
    public struct ComponentWithVariantVariation
    {
        [GhostField]
        public int value;
    }

    [GhostEnabledBit]
    public struct ComponentWithNonReplicatedVariant : IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField]
        public int value;

        public void SetValue(int value) => this.value = value;

        public int GetValue() => value;
    }

    // As this is the only variant, it becomes the default variant.
    [GhostComponentVariation(typeof(ComponentWithNonReplicatedVariant), "NonReplicatedVariation")]
    public struct ComponentWithNonReplicatedVariantVariation
    {
        public int value;
    }

    ////////////////////////////////////////////////////////////////////////////

    [GhostEnabledBit]
    public struct EnableableComponent_0: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_1: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_2: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_3: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_4: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_5: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    [GhostEnabledBit]
    public struct EnableableComponent_6: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_7: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_8: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_9: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_10: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_11: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    [GhostEnabledBit]
    public struct EnableableComponent_12: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_13: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_14: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_15: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    [GhostEnabledBit]
    public struct EnableableComponent_16: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_17: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_18: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_19: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_20: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_21: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    [GhostEnabledBit]
    public struct EnableableComponent_22: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_23: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_24: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_25: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    [GhostEnabledBit]
    public struct EnableableComponent_26: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_27: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_28: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_29: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_30: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_31: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableComponent_32: IComponentData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;

        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_0 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_1 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_2 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_3 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_4 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_5 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_6 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_7 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_8 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_9 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_10 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_11 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_12 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_13 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_14 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_15 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_16 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_17 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_18 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_19 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_20 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_21 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_22 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_23 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_24 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_25 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_26 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_27 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_28 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_29 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_30 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_31 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }
    [GhostEnabledBit]
    public struct EnableableBuffer_32 : IBufferElementData, IEnableableComponent, IComponentValue
    {
        [GhostField] public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }

        public int GetValue()
        {
            return value;
        }
    }

    ////////////////////////////////////////////////////////////////////////////
}
