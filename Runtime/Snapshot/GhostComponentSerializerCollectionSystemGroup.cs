using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
#if UNITY_EDITOR
using UnityEditor.Compilation;
#endif

namespace Unity.NetCode
{
    public struct VariantType
    {
        public ComponentType Component;
        public Type Variant;
        public ulong Hash;
        public bool IsSerialized;
#if UNITY_EDITOR
        public bool IsEditorOnlyOrTest;
#endif
    }

    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.GameObjectConversion)]
    public partial class GhostComponentSerializerCollectionSystemGroup : ComponentSystemGroup
    {
        struct NeverCreatedSingleton : IComponentData
        {}

        internal bool CollectionInitialized;
        internal List<GhostComponentSerializer.State> GhostComponentCollection = new List<GhostComponentSerializer.State>();
        internal Dictionary<ComponentType, Type> DefaultVariants = new Dictionary<ComponentType, Type>();
        internal List<VariantType> EmptyVariants = new List<VariantType>();
#if UNITY_EDITOR
        static private HashSet<string> EditorOnlyOrTestAssemblies;
#endif

        private ulong HashGhostComponentSerializer(in GhostComponentSerializer.State comp)
        {
            //this will give us a good starting point
            var compHash = TypeManager.GetTypeInfo(comp.ComponentType.TypeIndex).StableTypeHash;
            if (compHash == 0)
                throw new InvalidOperationException($"Unexpected 0 hash for type {comp.ComponentType}");
            compHash = TypeHash.CombineFNV1A64(compHash, comp.GhostFieldsHash);
            //ComponentSize might depend on #ifdef or other compilation/platform rules so it must be not included. we will leave the comment here
            //so it is clear why we don't consider this field
            //compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ComponentSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.SnapshotSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ChangeMaskBits));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64((int)comp.SendToOwner));
            return compHash;
        }

#if UNITY_EDITOR
        private void CreateEditorOnlyOrTestAssemblies()
        {
            if (EditorOnlyOrTestAssemblies != null)
                return;
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            EditorOnlyOrTestAssemblies = new HashSet<string>();
            for (int i = 0; i < assemblies.Length; ++i)
            {
                if((assemblies[i].flags & AssemblyFlags.EditorAssembly) != AssemblyFlags.None)
                {
                    EditorOnlyOrTestAssemblies.Add(assemblies[i].name);
                    continue;
                }
                foreach (var a in assemblies[i].compiledAssemblyReferences)
                {
                    if (a.StartsWith("nunit."))
                    {
                        EditorOnlyOrTestAssemblies.Add(assemblies[i].name);
                        break;
                    }
                }
            }
        }
#endif
        public void AddEmptyVariant(VariantType variantType)
        {
#if UNITY_EDITOR
            CreateEditorOnlyOrTestAssemblies();
            variantType.IsEditorOnlyOrTest = EditorOnlyOrTestAssemblies.Contains(variantType.Variant.Assembly.GetName().Name);
#endif
            EmptyVariants.Add(variantType);
        }

        public void AddSerializer(GhostComponentSerializer.State state)
        {
            //This is always enforced to avoid bad usage of the api
            if (CollectionInitialized)
            {
                throw new InvalidOperationException("Cannot register new GhostComponentSerializer after the RpcSystem has started running");
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (int i = 0; i < GhostComponentCollection.Count; ++i)
            {
                if (GhostComponentCollection[i].ComponentType == state.ComponentType &&
                    GhostComponentCollection[i].VariantHash == state.VariantHash)
                {
                    throw new InvalidOperationException($"GhostComponentSerializer for type {state.ComponentType.GetDebugTypeName()} and variant {state.VariantHash} is already registered");
                }
            }
#endif

            //When the state is registered the serializer hash is computed once. Skip some assemblies if the
            //component is from editor only or test assemblies
#if UNITY_EDITOR
            CreateEditorOnlyOrTestAssemblies();

            //Editor only and test assemblies are excluded from hash computation.
            bool excludeFromCollectionHash = EditorOnlyOrTestAssemblies.Contains(state.GetType().Assembly.GetName().Name);
            excludeFromCollectionHash |= EditorOnlyOrTestAssemblies.Contains(GhostComponentSerializer.VariantTypes[state.VariantTypeIndex].Assembly.GetName().Name);
            state.SerializerHash = excludeFromCollectionHash
                ? 0
                : HashGhostComponentSerializer(state);
#else
            state.SerializerHash = HashGhostComponentSerializer(state);
#endif
            GhostComponentCollection.Add(state);
        }

        private static ulong UncheckedVariantHash(string variantTypeName, ComponentType componentType)
        {
            var hash = TypeHash.FNV1A64("NetCode.GhostNetVariant");
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(componentType.GetDebugTypeName()));
            hash = TypeHash.CombineFNV1A64(hash, TypeHash.FNV1A64(variantTypeName));
            return hash;
        }

        internal static bool IsClientOnlyVariant(ComponentType componentType, ulong variantHash)
        {
            return ClientOnlyHash(componentType) == variantHash;
        }
        internal static bool IsDoNotSerializeVariant(ComponentType componentType, ulong variantHash)
        {
            return DoNotSerializeHash(componentType) == variantHash;
        }

        internal static ulong ClientOnlyHash(ComponentType componentType)
        {
            return UncheckedVariantHash("Unity.NetCode.ClientOnlyVariant", componentType);
        }
        internal static ulong DoNotSerializeHash(ComponentType componentType)
        {
            return UncheckedVariantHash("Unity.NetCode.DontSerializeVariant", componentType);
        }
        // Since multiple variants can be present for any given component there are some important use cases that need to be
        // handled:
        // 1 - A serializer with hash = 0 is present. This is always the default serializer used unless
        //     the user specify otherwise by either selecting a variant on prefab or by provide a mapping.
        // 2 - The component has no default serializer, 1 variant present (hash != 0): this will be the one used.
        //     No user intervention required.
        // 3 - The component has no default serializer, 2+ variant present: Users MUST indicate what is the default one to use.
        //     Not doing that is consider a runtime error. Exception and errors will be reported.
        //
        // ----------------------------------------------------------------------
        // How to specificy what variant is the "default" do you want to use ?
        // ----------------------------------------------------------------------
        // Users should provide an esplicit mapping by creating at runtime a singleton entity with the GhostSerializerDefaultCollection
        // component.
        // The variant selection is done by the GhostCollectionSystem, that based on the user choices will set the
        // correct serializer for the entity, based on prefab or defaults if present.
        public VariantType GetVariantType(ComponentType componentType, ulong variantHash)
        {
            return GetVariantType(DefaultVariants, GhostComponentCollection, EmptyVariants, componentType, variantHash);
        }
        internal static VariantType GetVariantType(Dictionary<ComponentType, Type> DefaultVariants, List<GhostComponentSerializer.State> GhostComponentCollection, List<VariantType> EmptyVariants, ComponentType componentType, ulong variantHash)
        {
            Type variantType;
            // lookup the default variant
            if (variantHash == 0 && DefaultVariants.TryGetValue(componentType, out variantType))
            {
                if (variantType == typeof(ClientOnlyVariant))
                {
                    return new VariantType
                    {
                        Component = componentType,
                        Variant = variantType,
                        Hash = ClientOnlyHash(componentType),
                        IsSerialized = false
                    };
                }
                if (variantType == typeof(DontSerializeVariant))
                {
                    return new VariantType
                    {
                        Component = componentType,
                        Variant = variantType,
                        Hash = DoNotSerializeHash(componentType),
                        IsSerialized = false
                    };
                }
                // Find the variant in the component list
                foreach (var state in GhostComponentCollection)
                {
                    if (state.ComponentType == componentType && GhostComponentSerializer.VariantTypes[state.VariantTypeIndex] == variantType)
                    {
                        return new VariantType
                        {
                            Component = componentType,
                            Variant = variantType,
                            Hash = state.VariantHash,
                            IsSerialized = true,
                            #if UNITY_EDITOR
                            IsEditorOnlyOrTest = (state.SerializerHash == 0)
                            #endif
                        };
                    }
                }
                foreach (var empty in EmptyVariants)
                {
                    if (empty.Component == componentType && empty.Variant == variantType)
                        return empty;
                }
                #if NET_DOTS
                UnityEngine.Debug.LogError($"The default variant for component type {componentType.GetDebugTypeName()} does not exist, ignoring the default");
                #else
                UnityEngine.Debug.LogError($"The default variant {variantType.FullName} for component type {componentType.GetDebugTypeName()} does not exist, ignoring the default");
                #endif
                DefaultVariants.Remove(componentType);
            }
            if (IsClientOnlyVariant(componentType, variantHash))
            {
                return new VariantType
                {
                    Component = componentType,
                    Variant = typeof(ClientOnlyVariant),
                    Hash = variantHash,
                    IsSerialized = false
                };
            }
            if (IsDoNotSerializeVariant(componentType, variantHash))
            {
                return new VariantType
                {
                    Component = componentType,
                    Variant = typeof(DontSerializeVariant),
                    Hash = variantHash,
                    IsSerialized = false
                };
            }
            ulong fallback = 0;
            int numFallbacks = 0;
            #if UNITY_EDITOR
            ulong editorTestFallback = 0;
            int numEditorTestFallbacks = 0;
            #endif
            foreach (var state in GhostComponentCollection)
            {
                if (state.ComponentType == componentType)
                {
                    if (state.VariantHash == variantHash)
                    {
                        return new VariantType
                        {
                            Component = componentType,
                            Variant = GhostComponentSerializer.VariantTypes[state.VariantTypeIndex],
                            Hash = state.VariantHash,
                            IsSerialized = true,
                            #if UNITY_EDITOR
                            IsEditorOnlyOrTest = (state.SerializerHash == 0)
                            #endif
                        };
                    }
                    #if UNITY_EDITOR
                    if (state.SerializerHash == 0)
                    {
                        if (editorTestFallback == 0 || editorTestFallback > state.VariantHash)
                            editorTestFallback = state.VariantHash;
                        ++numEditorTestFallbacks;
                    }
                    else
                    #endif
                    {
                        if (fallback == 0 || fallback > state.VariantHash)
                            fallback = state.VariantHash;
                        ++numFallbacks;
                    }
                }
            }
            foreach (var empty in EmptyVariants)
            {
                if (empty.Component == componentType)
                {
                    if (empty.Hash == variantHash)
                        return empty;
                    #if UNITY_EDITOR
                    if (empty.IsEditorOnlyOrTest)
                    {
                        if (editorTestFallback == 0 || editorTestFallback > empty.Hash)
                            editorTestFallback = empty.Hash;
                        ++numEditorTestFallbacks;
                    }
                    else
                    #endif
                    {
                        if (fallback == 0 || fallback > empty.Hash)
                            fallback = empty.Hash;
                        ++numFallbacks;
                    }
                }
            }
            // There was no default serializer, but there is a variant so use that
            if (variantHash == 0 && fallback != 0)
            {
                if (numFallbacks != 1)
                {
                    UnityEngine.Debug.LogError($"The component type {componentType.GetDebugTypeName()} has multiple potential variants but no default, using the variant with the lowest hash");
                }
                return GetVariantType(DefaultVariants, GhostComponentCollection, EmptyVariants, componentType, fallback);
            }
            #if UNITY_EDITOR
            if (variantHash == 0 && editorTestFallback != 0)
            {
                if (numEditorTestFallbacks != 1)
                {
                    UnityEngine.Debug.LogError($"The component type {componentType.GetDebugTypeName()} has multiple potential variants but no default, using the variant with the lowest hash");
                }
                return GetVariantType(DefaultVariants, GhostComponentCollection, EmptyVariants, componentType, editorTestFallback);
            }
            #endif
            if (variantHash != 0)
            {
                UnityEngine.Debug.LogError($"The explicitly specified variant for component type {componentType.GetDebugTypeName()} does not exist, using the default");
                return GetVariantType(DefaultVariants, GhostComponentCollection, EmptyVariants, componentType, 0);
            }
            return default;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            RequireSingletonForUpdate<NeverCreatedSingleton>();
        }
    }
}
