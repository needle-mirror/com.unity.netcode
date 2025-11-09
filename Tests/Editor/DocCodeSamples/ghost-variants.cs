using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace DocumentationCodeSamples
{
    partial class ghost_variants
    {
        private struct SomeClientOnlyComponent : IComponentData { }
        private struct SomeServerOnlyComponent : IComponentData { }
        private struct NoNeedToSyncComponent : IComponentData { }

        #region SpecialVariantTypes
        sealed partial class SpecialVariantSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(SomeClientOnlyComponent), Rule.ForAll(typeof(ClientOnlyVariant)));
                defaultVariants.Add(typeof(SomeServerOnlyComponent), Rule.ForAll(typeof(ServerOnlyVariant)));
                defaultVariants.Add(typeof(NoNeedToSyncComponent), Rule.ForAll(typeof(DontSerializeVariant)));
            }
        }
        #endregion

        #region DefiningVariants
        sealed partial class DefaultVariantSystem : DefaultVariantSystemBase
        {
            protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
            {
                defaultVariants.Add(typeof(LocalTransform), Rule.OnlyParents(typeof(TransformDefaultVariant)));
            }
        }
        #endregion
    }
}
