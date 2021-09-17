using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

using System.Reflection;

namespace Unity.NetCode
{
    /// <summary>
    /// DefaultVariantSystemBase is an abstract base class that should be used to update the default variants in
    /// <see cref="GhostComponentSerializerCollectionSystemGroup"/>, witch contains what serialization variant to use
    /// (<see cref="GhostComponentVariationAttribute"/>) for certain type.
    /// A concrete implementation must implements the <see cref="RegisterDefaultVariants"/> method and add to the dictionary
    /// the desired type-variant pairs.
    ///
    /// The system must and will be created in both runtime and conversion worlds. During conversion, in particular,
    /// the GhostComponentSerializerCollectionSystemGroup is used by the <see cref="GhostAuthoringConversion"/> to configure the ghost
    /// prefabs metadata with the defaults values.
    ///
    /// The abstract base class already has the correct flags / update in world attributes set.
    /// It is not necessary for the concrete implementation to specify neither the flags or the update in world.
    ///
    /// There is also no particular restriction in witch group the system need run into since all data needed by the
    /// runtime is created inside the OnCreate method. As a general rule, if you really need to add an UpdateInGroup
    /// attribute, please use only the SimulationSystemGroup as target.
    /// </summary>

    //This make the system also be created in the conversion world
    [WorldSystemFilter(WorldSystemFilterFlags.GameObjectConversion| WorldSystemFilterFlags.Default)]
    [UpdateInWorld(TargetWorld.ClientAndServer)]
    abstract public class DefaultVariantSystemBase : ComponentSystem
    {
        sealed protected override void OnCreate()
        {
            //A dictionary of ComponentType -> Type is not sufficient to guarantee correctness.
            //Some sanity check here are necessary
            var defaultVariants = new Dictionary<ComponentType, System.Type>();
            RegisterDefaultVariants(defaultVariants);
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
            foreach (var v in defaultVariants)
            {
                if(v.Value == typeof(ClientOnlyVariant) ||
                   v.Value == typeof(DontSerializeVariant))
                    continue;
                var variantAttr = v.Value.GetCustomAttribute<GhostComponentVariationAttribute>();
                if (variantAttr == null)
                    throw new System.ArgumentException($"$Invalid type registered as default variant. GhostComponentVariation attribute not found for type {v.Value}");
                var componentType = ComponentType.FromTypeIndex(v.Key.TypeIndex);
                if(variantAttr.ComponentType != componentType.GetManagedType())
                    throw new System.ArgumentException($"{v} is not a variation for component {v.Key}");
            }
#endif
            World.GetExistingSystem<GhostComponentSerializerCollectionSystemGroup>().DefaultVariants = defaultVariants;
        }

        sealed protected override void OnUpdate()
        {
        }

        /// <summary>
        /// Implement this method by adding to the <param name="defaultVariants"></param> dictionary your
        /// default type->variant mapping
        /// </summary>
        /// <param name="defaultVariants"></param>
        protected abstract void RegisterDefaultVariants(Dictionary<ComponentType, System.Type> defaultVariants);
    }
}
