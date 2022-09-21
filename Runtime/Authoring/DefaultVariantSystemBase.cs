using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;
using System.Reflection;
using Unity.Collections;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// <para>DefaultVariantSystemBase is an abstract base class that should be used to update the default variants in
    /// <see cref="GhostComponentSerializerCollectionData"/>, which contains what serialization variant to use
    /// (<see cref="GhostComponentVariationAttribute"/>) for certain type.
    /// A concrete implementation must implement the <see cref="RegisterDefaultVariants"/> method and add to the dictionary
    /// the desired type-variant pairs.</para>
    ///
    /// <para>The system must (and will be) created in both runtime and conversion worlds. During conversion, in particular,
    /// the GhostComponentSerializerCollectionSystemGroup is used by the `GhostAuthoringBakingSystem` to configure the ghost
    /// prefabs meta-data with the defaults values.</para>
    ///
    /// <para>The abstract base class already has the correct flags / update in world attributes set.
    /// It is not necessary for the concrete implementation to specify the flags, nor the `UpdateInWorld`.</para>
    ///
    /// <para>There is also no particular restriction in which group the system need run in, since all data needed by the
    /// runtime is created inside the `OnCreate` method. As a general rule, if you really need to add an UpdateInGroup
    /// attribute, please use only the SimulationSystemGroup as target.</para>
    ///
    /// <para>This <see cref="WorldSystemFilterAttribute"/> ensures this system is also added to all conversion worlds.</para>
    /// </summary>
    /// <remarks>You may have multiple derived systems. They'll all be read from, and conflicts will output errors at bake time, and the latest values will be used.</remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    public abstract partial class DefaultVariantSystemBase : SystemBase
    {
        /// <summary>When defining default variants for a type, you must denote whether or not this variant will be applied to both parents and children.</summary>
        public readonly struct Rule
        {
            /// <summary>The variant to use for all top-level (i.e. root/parent level) entities.</summary>
            /// <remarks>Parent entities default to send (i.e. serialize all "Ghost Fields" using the settings defined in the <see cref="GhostFieldAttribute"/>).</remarks>
            public readonly System.Type VariantForParents;

            /// <summary>The variant to use for all child entities.</summary>
            /// <remarks>Child entities default to <see cref="DontSerializeVariant"/> for performance reasons.</remarks>
            public readonly System.Type VariantForChildren;

            /// <summary>This rule will only add the variant to parent entities with this component type.
            /// Children with this component will remain <see cref="DontSerializeVariant"/> (which is the default for children).
            /// <b>This is the recommended approach.</b></summary>
            /// <param name="variantForParentOnly"></param>
            /// <returns></returns>
            public static Rule OnlyParents(Type variantForParentOnly) => new Rule(variantForParentOnly, default);

            /// <summary>This rule will add the same variant to all entities with this component type (i.e. both parent and children a.k.a. regardless of hierarchy).
            /// <b>Note: It is not recommended to serialize child entities as it is relatively slow to serialize them!</b></summary>
            /// <param name="variantForBoth"></param>
            /// /// <returns></returns>
            public static Rule ForAll(Type variantForBoth) => new Rule(variantForBoth, variantForBoth);

            /// <summary>This rule will add one variant for parents, and another variant for children, by default.
            /// <b>Note: It is not recommended to serialize child entities as it is relatively slow to serialize them!</b></summary>
            /// <param name="variantForParents"></param>
            /// <param name="variantForChildren"></param>
            /// <returns></returns>
            public static Rule Unique(Type variantForParents, Type variantForChildren) => new Rule(variantForParents, variantForChildren);

            /// <summary>This rule will only add this variant to child entities with this component.
            /// The parent entities with this component will use the default serializer.
            /// <b>Note: It is not recommended to serialize child entities as it is relatively slow to serialize them!</b></summary>
            /// <param name="variantForChildrenOnly"></param>
            /// <returns></returns>
            public static Rule OnlyChildren(Type variantForChildrenOnly) => new Rule(default, variantForChildrenOnly);

            /// <summary>Use the static builder methods instead!</summary>
            /// <param name="variantForParents"><inheritdoc cref="VariantForParents"/></param>
            /// <param name="variantForChildren"><inheritdoc cref="VariantForChildren"/></param>
            private Rule(Type variantForParents, Type variantForChildren)
            {
                VariantForParents = variantForParents;
                VariantForChildren = variantForChildren;
            }

            /// <summary>
            /// The Rule string representation. Print the parent and child variant types.
            /// </summary>
            /// <returns></returns>
            public override string ToString() => $"Rule[parents: `{VariantForParents}`, children: `{VariantForChildren}`]";

            /// <summary>
            /// Compare two rules ana check if their parent and child types are identical.
            /// </summary>
            /// <param name="other"></param>
            /// <returns></returns>
            public bool Equals(Rule other) => VariantForParents == other.VariantForParents && VariantForChildren == other.VariantForChildren;

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((VariantForParents != null ? VariantForParents.GetHashCode() : 0) * 397) ^ (VariantForChildren != null ? VariantForChildren.GetHashCode() : 0);
                }
            }

            internal HashRule CreateHashRule(ComponentType componentType) => new HashRule(TryGetHashElseZero(componentType, VariantForParents), TryGetHashElseZero(componentType, VariantForChildren));

            static ulong TryGetHashElseZero(ComponentType componentType, Type variantType)
            {
                return variantType == null ? 0 : GhostVariantsUtility.UncheckedVariantHash(variantType.FullName, new FixedString512Bytes(componentType.GetDebugTypeName()));
            }
        }

        /// <summary>Hash version of <see cref="Rule"/> to allow it to be BurstCompatible.</summary>
        internal readonly struct HashRule
        {
            /// <summary>Hash version of <see cref="Rule.VariantForParents"/>.</summary>
            public readonly ulong VariantForParents;
            /// <summary>Hash version of <see cref="Rule.VariantForChildren"/>.</summary>
            public readonly ulong VariantForChildren;

            public HashRule(ulong variantForParents, ulong variantForChildren)
            {
                VariantForParents = variantForParents;
                VariantForChildren = variantForChildren;
            }

            public override string ToString() => $"HashRule[parent: `{VariantForParents}`, children: `{VariantForChildren}`]";

            public bool Equals(HashRule other) => VariantForParents == other.VariantForParents && VariantForChildren == other.VariantForChildren;

        }

        protected sealed override void OnCreate()
        {
            //A dictionary of ComponentType -> Type is not sufficient to guarantee correctness.
            //Some sanity check here are necessary
            var defaultVariants = new Dictionary<ComponentType, Rule>();
            RegisterDefaultVariants(defaultVariants);
#if ENABLE_UNITY_COLLECTIONS_CHECKS && !UNITY_DOTSRUNTIME
            ValidateUserSpecifiedDefaultVariants(defaultVariants);
#endif

            World.GetOrCreateSystemManaged<GhostComponentSerializerCollectionSystemGroup>().AppendUserSpecifiedDefaultVariantsToSystem(defaultVariants);
            Enabled = false;
        }

        void ValidateUserSpecifiedDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            foreach (var kvp in defaultVariants)
            {
                var componentType = kvp.Key;
                var rule = kvp.Value;
                if (rule.VariantForParents == default && rule.VariantForChildren == default)
                    throw new System.ArgumentException($"`{componentType}` has an invalid default variant rule ({rule}) defined in `{GetType().FullName}` (in '{World.Name}'), as both are `null`!");

                var managedType = componentType.GetManagedType();
                if (typeof(IInputBufferData).IsAssignableFrom(managedType))
                    throw new System.ArgumentException($"`{managedType}` is of type `IInputBufferData`, which must get its default variants from the `IInputComponentData` that it is code-generated from. Replace this dictionary entry ({rule}) with the `IInputComponentData` type in system `{GetType().FullName}`, in '{World.Name}'!");

                ValidateUserDefinedDefaultVariantRule(componentType, rule.VariantForParents);
                ValidateUserDefinedDefaultVariantRule(componentType, rule.VariantForChildren);
            }
        }

        void ValidateUserDefinedDefaultVariantRule(ComponentType componentType, Type variantType)
        {
            // Nothing to validate if the variant is the "default serializer".
            if (variantType == default || variantType == componentType.GetManagedType())
                return;

            var isInput = typeof(ICommandData).IsAssignableFrom(componentType.GetManagedType());
            if (variantType == typeof(ClientOnlyVariant) || variantType == typeof(DontSerializeVariant))
            {
                if (isInput)
                    throw new System.ArgumentException($"System `{GetType().FullName}` is attempting to set a default variant for an `ICommandData` type: `{componentType}`, but the type of the variant is `{variantType.FullName}`! Ensure you use a serialized variant with `GhostPrefabType.All`!");
                return;
            }

            var variantAttr = variantType.GetCustomAttribute<GhostComponentVariationAttribute>();
            if (variantAttr == null)
                throw new System.ArgumentException($"Invalid type registered as default variant. GhostComponentVariationAttribute not found for type `{variantType.FullName}`, cannot use it as the default variant for `{componentType}`! Defined in system `{GetType().FullName}`!");

            var managedType = componentType.GetManagedType();
            if (variantAttr.ComponentType != managedType)
                throw new System.ArgumentException($"`{variantType.FullName}` is not a variation of component `{componentType}`, cannot use it as a default variant in system `{GetType().FullName}`!");
        }

        protected sealed override void OnUpdate()
        {
        }

        /// <summary>
        /// Implement this method by adding to the <param name="defaultVariants"></param> dictionary your
        /// default type->variant mapping. <seealso cref="Rule"/>
        /// </summary>
        /// <param name="defaultVariants"></param>
        protected abstract void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants);
    }
}
