using System;
using System.Linq;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;
using System.Reflection;

namespace Unity.NetCode.Editor
{
    class GhostComponentVariantLookup
    {
        private Dictionary<ComponentType, Type> defaultVariants = null;
        private List<GhostComponentSerializer.State> ghostComponentCollection = null;
        private List<VariantType> emptyVariants = null;

        public struct NamedVariant
        {
            public string Name;
            public ulong Hash;
        }

        private void InitVariants()
        {
            using(var world = new World("TempGhostConversion"))
            {
                //Add any ghost default variant assignemnt system to world
                var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.GameObjectConversion);
                var collection = world.GetOrCreateSystem<GhostComponentSerializerCollectionSystemGroup>();
                // Create all registration systems
                foreach (var sys in systems.Where(s => typeof(GhostComponentSerializerRegistrationSystemBase).IsAssignableFrom(s)))
                    world.GetOrCreateSystem(sys);
                var defaultVariantSystem = systems.FirstOrDefault(s => typeof(DefaultVariantSystemBase).IsAssignableFrom(s));
                if(defaultVariantSystem != null)
                    world.GetOrCreateSystem(defaultVariantSystem);
                defaultVariants = collection.DefaultVariants;
                ghostComponentCollection = collection.GhostComponentCollection;
                emptyVariants = collection.EmptyVariants;
            }
        }
        public System.Type GetVariantForComponent(ComponentType componentType, ulong variantHash)
        {
            if (ghostComponentCollection == null)
                InitVariants();
            var variantType = GhostComponentSerializerCollectionSystemGroup.GetVariantType(defaultVariants, ghostComponentCollection, emptyVariants, componentType, variantHash);
            return variantType.Variant;
        }
        public string GetVariantName(string fullTypeName, ulong variantHash)
        {
            if (ghostComponentCollection == null)
                InitVariants();
            foreach (var state in ghostComponentCollection)
            {
                if (state.ComponentType.GetDebugTypeName() == fullTypeName &&
                    state.VariantHash == variantHash)
                {
                    var vt = GhostComponentSerializer.VariantTypes[state.VariantTypeIndex];
                    var name = vt.GetCustomAttribute<GhostComponentVariationAttribute>()?.DisplayName;
                    return (name != null) ? name : vt.Name;
                }
            }
            foreach (var empty in emptyVariants)
            {
                if (empty.Component.GetDebugTypeName() == fullTypeName && empty.Hash == variantHash)
                {
                    var name = empty.Variant.GetCustomAttribute<GhostComponentVariationAttribute>()?.DisplayName;
                    return (name != null) ? name : empty.Variant.Name;
                }
            }
            if (GhostVariantsUtility.IsDoNotSerializeVariant(fullTypeName, variantHash))
                return "DoNotSerialize";
            Debug.LogError($"Cannot find variant with hash {variantHash} for type {fullTypeName}");
            return "INVALID!!";
        }
        public bool GetAllVariants(ComponentType componentType, out List<NamedVariant> list)
        {
            if (ghostComponentCollection == null)
                InitVariants();
            list = null;
            foreach (var state in ghostComponentCollection)
            {
                // SerializerHash == 0 means editor only or test
                if (state.ComponentType == componentType && state.SerializerHash != 0)
                {
                    if (list == null)
                        list = new List<NamedVariant>();
                    var vt = GhostComponentSerializer.VariantTypes[state.VariantTypeIndex];
                    var name = vt.GetCustomAttribute<GhostComponentVariationAttribute>()?.DisplayName;
                    list.Add(new NamedVariant {Name = (name != null) ? name : vt.Name, Hash = state.VariantHash});
                }
            }
            foreach (var empty in emptyVariants)
            {
                if (empty.Component == componentType && !empty.IsEditorOnlyOrTest)
                {
                    if (list == null)
                        list = new List<NamedVariant>();
                    var name = empty.Variant.GetCustomAttribute<GhostComponentVariationAttribute>()?.DisplayName;
                    list.Add(new NamedVariant {Name = (name != null) ? name : empty.Variant.Name, Hash = empty.Hash});
                }
            }
            if (list != null)
            {
                list.Sort((v1, v2) =>
                {
                    if (v1.Hash < v2.Hash)
                        return -1;
                    else if (v1.Hash > v2.Hash)
                        return 1;
                    else return 0;
                });
            }
            return list != null;
        }
    }
}
