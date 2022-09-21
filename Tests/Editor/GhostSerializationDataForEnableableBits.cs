using NUnit.Framework;
using Unity.Entities;
using UnityEngine;

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
            GhostGroup
        }

        private GhostTypes type;
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
                    baker.AddComponent(new EnableableComponent{});
                    break;
                case GhostTypes.MultipleEnableableComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    SetupMultipleEnableableComponents(baker);
                    break;
                case GhostTypes.EnableableBuffer:
                    baker.AddComponent(new GhostOwnerComponent());
                    baker.AddBuffer<EnableableBuffer>();
                    break;
                case GhostTypes.MultipleEnableableBuffer:
                    baker.AddComponent(new GhostOwnerComponent());
                    SetupMultipleEnableableBuffer(baker);
                    break;
                case GhostTypes.ChildComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    baker.AddComponent(new EnableableComponent{});
                    var transform = baker.GetComponent<Transform>();
                    baker.DependsOn(transform.parent);
                    if (transform.parent == null)
                        baker.AddComponent(new TopLevelGhostEntity());
                    break;
                case GhostTypes.ChildBufferComponent:
                    baker.AddComponent(new GhostOwnerComponent());
                    baker.AddBuffer<EnableableBuffer>();
                    if (gameObject.transform.parent == null)
                        baker.AddComponent(new TopLevelGhostEntity());
                    break;
                case GhostTypes.GhostGroup:
                    baker.AddComponent(new GhostOwnerComponent());
                    // Dependency on the name
                    baker.DependsOn(gameObject);
                    if (gameObject.name == "ParentGhost")
                    {
                        baker.AddBuffer<GhostGroup>();
                        baker.AddComponent(default(GhostGroupRoot));
                        baker.AddComponent(default(EnableableComponent));
                    }
                    else
                    {
                        baker.AddComponent(default(GhostChildEntityComponent));
                        baker.AddComponent(default(EnableableComponent));
                    }
                    break;
                default:
                    Assert.True(false);
                    break;
            }
        }

        static void SetupMultipleEnableableComponents(Entity entity, EntityManager dstManager)
        {
            dstManager.AddComponentData(entity, new EnableableComponent_0());
            dstManager.AddComponentData(entity, new EnableableComponent_1());
            dstManager.AddComponentData(entity, new EnableableComponent_2());
            dstManager.AddComponentData(entity, new EnableableComponent_3());
            dstManager.AddComponentData(entity, new EnableableComponent_4());
            dstManager.AddComponentData(entity, new EnableableComponent_5());
            dstManager.AddComponentData(entity, new EnableableComponent_6());
            dstManager.AddComponentData(entity, new EnableableComponent_7());
            dstManager.AddComponentData(entity, new EnableableComponent_8());
            dstManager.AddComponentData(entity, new EnableableComponent_9());
            dstManager.AddComponentData(entity, new EnableableComponent_10());
            dstManager.AddComponentData(entity, new EnableableComponent_11());
            dstManager.AddComponentData(entity, new EnableableComponent_12());
            dstManager.AddComponentData(entity, new EnableableComponent_13());
            dstManager.AddComponentData(entity, new EnableableComponent_14());
            dstManager.AddComponentData(entity, new EnableableComponent_15());
            dstManager.AddComponentData(entity, new EnableableComponent_16());
            dstManager.AddComponentData(entity, new EnableableComponent_17());
            dstManager.AddComponentData(entity, new EnableableComponent_18());
            dstManager.AddComponentData(entity, new EnableableComponent_19());
            dstManager.AddComponentData(entity, new EnableableComponent_20());
            dstManager.AddComponentData(entity, new EnableableComponent_21());
            dstManager.AddComponentData(entity, new EnableableComponent_22());
            dstManager.AddComponentData(entity, new EnableableComponent_23());
            dstManager.AddComponentData(entity, new EnableableComponent_24());
            dstManager.AddComponentData(entity, new EnableableComponent_25());
            dstManager.AddComponentData(entity, new EnableableComponent_26());
            dstManager.AddComponentData(entity, new EnableableComponent_27());
            dstManager.AddComponentData(entity, new EnableableComponent_28());
            dstManager.AddComponentData(entity, new EnableableComponent_29());
            dstManager.AddComponentData(entity, new EnableableComponent_30());
            dstManager.AddComponentData(entity, new EnableableComponent_31());
            dstManager.AddComponentData(entity, new EnableableComponent_32());
        }

        static void SetupMultipleEnableableComponents(IBaker baker)
        {
            baker.AddComponent(new EnableableComponent_0());
            baker.AddComponent(new EnableableComponent_1());
            baker.AddComponent(new EnableableComponent_2());
            baker.AddComponent(new EnableableComponent_3());
            baker.AddComponent(new EnableableComponent_4());
            baker.AddComponent(new EnableableComponent_5());
            baker.AddComponent(new EnableableComponent_6());
            baker.AddComponent(new EnableableComponent_7());
            baker.AddComponent(new EnableableComponent_8());
            baker.AddComponent(new EnableableComponent_9());
            baker.AddComponent(new EnableableComponent_10());
            baker.AddComponent(new EnableableComponent_11());
            baker.AddComponent(new EnableableComponent_12());
            baker.AddComponent(new EnableableComponent_13());
            baker.AddComponent(new EnableableComponent_14());
            baker.AddComponent(new EnableableComponent_15());
            baker.AddComponent(new EnableableComponent_16());
            baker.AddComponent(new EnableableComponent_17());
            baker.AddComponent(new EnableableComponent_18());
            baker.AddComponent(new EnableableComponent_19());
            baker.AddComponent(new EnableableComponent_20());
            baker.AddComponent(new EnableableComponent_21());
            baker.AddComponent(new EnableableComponent_22());
            baker.AddComponent(new EnableableComponent_23());
            baker.AddComponent(new EnableableComponent_24());
            baker.AddComponent(new EnableableComponent_25());
            baker.AddComponent(new EnableableComponent_26());
            baker.AddComponent(new EnableableComponent_27());
            baker.AddComponent(new EnableableComponent_28());
            baker.AddComponent(new EnableableComponent_29());
            baker.AddComponent(new EnableableComponent_30());
            baker.AddComponent(new EnableableComponent_31());
            baker.AddComponent(new EnableableComponent_32());
        }

        static void SetupMultipleEnableableBuffer(Entity entity, EntityManager dstManager)
        {
            dstManager.AddBuffer<EnableableBuffer_0>(entity);
            dstManager.AddBuffer<EnableableBuffer_1>(entity);
            dstManager.AddBuffer<EnableableBuffer_2>(entity);
            dstManager.AddBuffer<EnableableBuffer_3>(entity);
            dstManager.AddBuffer<EnableableBuffer_4>(entity);
            dstManager.AddBuffer<EnableableBuffer_5>(entity);
            dstManager.AddBuffer<EnableableBuffer_6>(entity);
            dstManager.AddBuffer<EnableableBuffer_7>(entity);
            dstManager.AddBuffer<EnableableBuffer_8>(entity);
            dstManager.AddBuffer<EnableableBuffer_9>(entity);
            dstManager.AddBuffer<EnableableBuffer_10>(entity);
            dstManager.AddBuffer<EnableableBuffer_11>(entity);
            dstManager.AddBuffer<EnableableBuffer_12>(entity);
            dstManager.AddBuffer<EnableableBuffer_13>(entity);
            dstManager.AddBuffer<EnableableBuffer_14>(entity);
            dstManager.AddBuffer<EnableableBuffer_15>(entity);
            dstManager.AddBuffer<EnableableBuffer_16>(entity);
            dstManager.AddBuffer<EnableableBuffer_17>(entity);
            dstManager.AddBuffer<EnableableBuffer_18>(entity);
            dstManager.AddBuffer<EnableableBuffer_19>(entity);
            dstManager.AddBuffer<EnableableBuffer_20>(entity);
            dstManager.AddBuffer<EnableableBuffer_21>(entity);
            dstManager.AddBuffer<EnableableBuffer_22>(entity);
            dstManager.AddBuffer<EnableableBuffer_23>(entity);
            dstManager.AddBuffer<EnableableBuffer_24>(entity);
            dstManager.AddBuffer<EnableableBuffer_25>(entity);
            dstManager.AddBuffer<EnableableBuffer_26>(entity);
            dstManager.AddBuffer<EnableableBuffer_27>(entity);
            dstManager.AddBuffer<EnableableBuffer_28>(entity);
            dstManager.AddBuffer<EnableableBuffer_29>(entity);
            dstManager.AddBuffer<EnableableBuffer_30>(entity);
            dstManager.AddBuffer<EnableableBuffer_31>(entity);
            dstManager.AddBuffer<EnableableBuffer_32>(entity);
        }

        static void SetupMultipleEnableableBuffer(IBaker baker)
        {
            baker.AddBuffer<EnableableBuffer_0>();
            baker.AddBuffer<EnableableBuffer_1>();
            baker.AddBuffer<EnableableBuffer_2>();
            baker.AddBuffer<EnableableBuffer_3>();
            baker.AddBuffer<EnableableBuffer_4>();
            baker.AddBuffer<EnableableBuffer_5>();
            baker.AddBuffer<EnableableBuffer_6>();
            baker.AddBuffer<EnableableBuffer_7>();
            baker.AddBuffer<EnableableBuffer_8>();
            baker.AddBuffer<EnableableBuffer_9>();
            baker.AddBuffer<EnableableBuffer_10>();
            baker.AddBuffer<EnableableBuffer_11>();
            baker.AddBuffer<EnableableBuffer_12>();
            baker.AddBuffer<EnableableBuffer_13>();
            baker.AddBuffer<EnableableBuffer_14>();
            baker.AddBuffer<EnableableBuffer_15>();
            baker.AddBuffer<EnableableBuffer_16>();
            baker.AddBuffer<EnableableBuffer_17>();
            baker.AddBuffer<EnableableBuffer_18>();
            baker.AddBuffer<EnableableBuffer_19>();
            baker.AddBuffer<EnableableBuffer_20>();
            baker.AddBuffer<EnableableBuffer_21>();
            baker.AddBuffer<EnableableBuffer_22>();
            baker.AddBuffer<EnableableBuffer_23>();
            baker.AddBuffer<EnableableBuffer_24>();
            baker.AddBuffer<EnableableBuffer_25>();
            baker.AddBuffer<EnableableBuffer_26>();
            baker.AddBuffer<EnableableBuffer_27>();
            baker.AddBuffer<EnableableBuffer_28>();
            baker.AddBuffer<EnableableBuffer_29>();
            baker.AddBuffer<EnableableBuffer_30>();
            baker.AddBuffer<EnableableBuffer_31>();
            baker.AddBuffer<EnableableBuffer_32>();
        }
    }

    public interface IComponentValue
    {
        void SetValue(int value);
        int GetValue();
    }

    [GhostComponent(SendDataForChildEntity = false)]
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

    ////////////////////////////////////////////////////////////////////////////

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
