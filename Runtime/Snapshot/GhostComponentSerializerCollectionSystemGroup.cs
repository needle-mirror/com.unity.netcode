using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// A component - variant - root tuple,
    /// used for caching the <see cref="GhostComponentSerializerCollectionData.GetAllAvailableVariantsForType"/> result
    /// and speed-up successive query of the same component-variant combination.
    /// </summary>
    internal struct VariantQuery : IComparable<VariantQuery>, IEquatable<VariantQuery>
    {
        public ComponentType ComponentType;
        public ulong variantHash;

        private byte _isRoot;
        public bool isRoot => _isRoot != 0;

        public VariantQuery(ComponentType type, ulong hash, bool root)
        {
            ComponentType = type;
            variantHash = hash;
            _isRoot = (byte) (root ? 1 : 0);
        }

        public int CompareTo(VariantQuery other)
        {
            var componentTypeComparison = ComponentType.CompareTo(other.ComponentType);
            if (componentTypeComparison != 0) return componentTypeComparison;
            var variantHashComparison = variantHash.CompareTo(other.variantHash);
            if (variantHashComparison != 0) return variantHashComparison;
            return isRoot.CompareTo(other.isRoot);
        }

        public bool Equals(VariantQuery other)
        {
            return ComponentType.Equals(other.ComponentType) && variantHash == other.variantHash && isRoot == other.isRoot;
        }

        public override bool Equals(object obj)
        {
            return obj is VariantQuery other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hashCode = ComponentType.GetHashCode();
            hashCode = (hashCode * 397) ^ variantHash.GetHashCode();
            hashCode = (hashCode * 397) ^ isRoot.GetHashCode();
            return hashCode;
        }
    }

    /// <summary>
    /// Stores all attribute and reflection data for NetCode GhostComponents and NetCode Variants.
    /// Populated via the "Source Generators" code-generation.
    /// </summary>
    public struct CodeGenTypeMetaData
    {
        /// <summary>"Variant Type Hash". Set via <see cref="GhostVariantsUtility.UncheckedVariantHash(Unity.Collections.NativeText.ReadOnly,Unity.Entities.ComponentType)"/>.</summary>
        public ulong TypeHash;

        /// <summary>True if the code-generator determined that this is an input component (or a variant of one).</summary>
        public bool IsInputComponent;
        /// <summary>True if the code-generator determined that this is an input buffer.</summary>
        public bool IsInputBuffer;

        /// <summary>Does this component explicitly opt-out of overrides (regardless of variant count)?</summary>
        public bool HasDontSupportPrefabOverridesAttribute;

        /// <summary>Does this component explicitly opt-in to overrides (regardless of variant count)?</summary>
        public bool HasSupportsPrefabOverridesAttribute;

        /// <summary>True if this is an editor test variant. Forces this variant to be considered a "default" which makes writing tests easier.</summary>
        public bool IsTestVariant;

        /// <summary><see cref="IsInputComponent"/> and <see cref="IsInputBuffer"/>.</summary>
        public bool IsInput => IsInputComponent || IsInputBuffer;

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal void ThrowIfNoHash()
        {
            GhostComponentSerializerCollectionData.ThrowIfNoHash(TypeHash, nameof(TypeHash));
        }
    }

    // TODO - Make internal if possible.
    /// <summary>
    /// <para>
    /// For internal use only. Encapsulates ghost serialization variant (see <see cref="GhostComponentVariationAttribute"/>)
    /// type information.
    /// </para>
    /// <para>
    /// The struct is also used as interop struct by code-gen to register "empty" variants (component for which a
    /// variant has been declared using the <see cref="GhostComponentVariationAttribute"/> but for which serialization
    /// is not generated, i.e: only the <see cref="GhostComponent"/> attribute is specified in the variant declaration).
    /// </para>
    /// </summary>
    public struct VariantType : IComparable<VariantType>
    {
        /// <summary>Denotes why this variant is the default (or not). Higher value = more important.</summary>
        /// <remarks>This is a flags enum, so there may be multiple reasons why a variant is considered the default.</remarks>
        [Flags]
        public enum DefaultType : byte
        {
            /// <summary>This is not the default.</summary>
            NotDefault = 0,
            /// <summary>This is a default variant either due to error or no other variants existing.</summary>
            YesViaFallbackRule = 1 << 1,
            /// <summary>Child entities default to <see cref="DontSerializeVariant"/>.</summary>
            YesAsIsChildDefaultingToDontSerializeVariant = 1 << 2,
            /// <summary>It's an editor test variant.</summary>
            YesAsEditorDefault = 1 << 3,
            /// <summary>The default serializer should be used if we're a root.</summary>
            YesAsIsDefaultSerializerAndIsRoot = 1 << 4,
            /// <summary>Yes via <see cref="GhostComponentAttribute"/>.</summary>
            YesAsAttributeAllowingChildSerialization = 1 << 5,
            /// <summary>If the developer has only specified one variant, it becomes the default.</summary>
            YesAsOnlyOneVariantBecomesDefault = 1 << 6,
            /// <summary>This is a default variant because the user has marked it as such via <see cref="DefaultVariantSystemBase"/>. Highest priority.</summary>
            YesViaUserSpecifiedNamedDefault = 1 << 7,
        }

        /// <summary>Denotes the source of the variant. I.e. How was it added to this type?</summary>
        internal enum VariantSource : byte
        {
            SourceGeneratorSerializers,
            SourceGeneratorEmptyVariants,
            ManualClientOnlyVariant,
            ManualDefaultSerializer,
            ManualDontSerializeVariant,
        }

        /// <summary>Component that this Variant is associated with.</summary>
        public ComponentType Component;
        /// <summary>Hash of variant. Should be non-zero by the time it's used in <see cref="GhostComponentSerializerCollectionSystemGroup.GetCurrentVariantTypeForComponent(Unity.Entities.ComponentType,ulong,bool)"/>.</summary>
        public ulong Hash;
        /// <summary>
        /// The <see cref="GhostPrefabType"/> value set in <see cref="GhostComponent"/> present in the variant declaration.
        /// Some variants modify the serialization rules. Default is <see cref="GhostPrefabType.All"/>
        /// </summary>
        public GhostPrefabType PrefabType;
        /// <summary><see cref="DefaultType"/></summary>
        public DefaultType DefaultRule;

        private byte _IsDefaultSerializer;
        /// <summary>True if this variant is actually just the type (thus it's the default serializer).</summary>
        public bool IsDefaultSerializer { get { return _IsDefaultSerializer != 0; } set { _IsDefaultSerializer = (byte)(value ? 1 : 0); } }

        private byte _IsSerialized;
        /// <summary>True if this variant serializes its data.</summary>
        public bool IsSerialized { get { return _IsSerialized != 0; } set { _IsSerialized = (byte)(value ? 1 : 0); } }

        private byte _IsTestVariant;
        /// <summary><inheritdoc cref="GhostComponentVariationAttribute.IsTestVariant"/></summary>
        public bool IsTestVariant { get { return _IsTestVariant != 0; } set { _IsTestVariant = (byte)(value ? 1 : 0); } }

        /// <summary><inheritdoc cref="VariantSource"/></summary>
        internal VariantSource Source;
        /// <summary>Lookup into the <see cref="GhostComponentSerializer.VariantTypes"/> array.</summary>
        public int VariantTypeIndex;

        /// <summary>The variant type. It'll return the component type itself if not able to resolve.</summary>
        public Type Variant => VariantTypeIndex >= 0 && VariantTypeIndex < GhostComponentSerializer.VariantTypes.Count ? GhostComponentSerializer.VariantTypes[VariantTypeIndex] : Component.GetManagedType();

        /// <summary>
        /// Check if two VariantType are identical.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(VariantType other)
        {
            if (IsSerialized != other.IsSerialized)
                return !IsSerialized ? -1 : 1;
            if (DefaultRule != other.DefaultRule)
                return DefaultRule - other.DefaultRule;
            if (Hash != other.Hash)
                return Hash < other.Hash ? -1 : 1;
            return 0;
        }

        /// <summary>
        /// Convert the instance to its string representation.
        /// </summary>
        /// <returns></returns>
        public override string ToString() => $"VT<{Component}>[H:{Hash}, Variant: `{Variant.FullName}` (VTI:'{VariantTypeIndex}'), DR:{DefaultRule}, Serialized:{(IsSerialized ? '1' : '0')}, PT:{PrefabType}, Source:'{Source}']";

        [BurstDiscard]
        void NonBurstedBetterLog(ref FixedString512Bytes fs) => fs.Append(ToString());

        /// <summary>Logs a burst compatible debug string (if in burst), otherwise logs even more info.</summary>
        /// <returns>A debug string.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString()
        {
            var fs = new FixedString512Bytes();
            NonBurstedBetterLog(ref fs);
            if (fs.Length == 0)
            {
                var isSerialized = IsSerialized ? 1 : 0;
                fs = new FixedString512Bytes((FixedString32Bytes)$"VT<");
                fs.Append(Component.GetDebugTypeName());
                fs.Append((FixedString128Bytes)$">[H:{Hash}, VTI:'{VariantTypeIndex}', DR:{(int)DefaultRule}, Serialized:{isSerialized}, PT:{(int)PrefabType}, Source:'{(int)Source}']");
            }
            return fs;
        }

#if UNITY_EDITOR
        /// <summary>Returns a readable name for the variant.</summary>
        /// <param name="metaData">Meta-data of this ComponentType.</param>
        /// <returns>A readable name string.</returns>
        public string CreateReadableName(CodeGenTypeMetaData metaData) => Variant.GetCustomAttribute<GhostComponentVariationAttribute>()?.DisplayName ?? Variant.Name;
#endif
    }

    /// <summary>
    /// Parent group of all code-generated systems that registers the ghost component serializers to the <see cref="GhostCollection"/>,
    /// more specifically to the <see cref="GhostComponentSerializer.State"/> collection) at runtime.
    /// For internal use only, don't add systems to this group.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem,
        WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    public partial class GhostComponentSerializerCollectionSystemGroup : ComponentSystemGroup
    {
        /// <summary>Increase this if you have thousands of types.</summary>
        public static int CollectionCapacity = 1024;

        /// <summary>
        /// Only required because, in some strange cases, a class derived from <see cref="DefaultVariantSystemBase"/> (which has a `CreateAfter` attribute)
        /// will have its `OnCreate` called before this groups OnCreate.
        /// </summary>
        Dictionary<ComponentType, DefaultVariantSystemBase.Rule> m_DefaultVariantsManaged = new Dictionary<ComponentType, DefaultVariantSystemBase.Rule>(32);

        /// <summary>Hacky workaround for GetSingleton not working on frame 0 (not sure why, as creation order is correct).</summary>
        internal GhostComponentSerializerCollectionData ghostComponentSerializerCollectionDataCache { get; private set; }

        struct NeverCreatedSingleton : IComponentData
        {}

        protected override void OnCreate()
        {
            base.OnCreate();
            RequireForUpdate<NeverCreatedSingleton>();

            // Convert the managed values into a burst-compatible format.
            var defaultVariants = new NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule>(CollectionCapacity, Allocator.Persistent);

            foreach (var kvp in m_DefaultVariantsManaged)
                defaultVariants[kvp.Key] = kvp.Value.CreateHashRule(kvp.Key);

            var worldNameShortened = new FixedString32Bytes();
            FixedStringMethods.CopyFromTruncated(ref worldNameShortened, World.Unmanaged.Name);
            ghostComponentSerializerCollectionDataCache = new GhostComponentSerializerCollectionData
            {
                WorldName = worldNameShortened,
                GhostComponentCollection = new NativeMultiHashMap<ComponentType, GhostComponentSerializer.State>(CollectionCapacity, Allocator.Persistent),
                CodeGenTypeMetaData = new NativeHashMap<ulong, CodeGenTypeMetaData>(CollectionCapacity, Allocator.Persistent),
                EmptyVariants = new NativeMultiHashMap<ComponentType, VariantType>(CollectionCapacity, Allocator.Persistent),
                DefaultVariants = defaultVariants,
                TypeVariantCache = new NativeHashMap<VariantQuery, VariantType>(CollectionCapacity, Allocator.Persistent),
            };
            EntityManager.CreateSingleton(ghostComponentSerializerCollectionDataCache);
        }

        protected override void OnDestroy()
        {
            ghostComponentSerializerCollectionDataCache.GhostComponentCollection.Dispose();
            ghostComponentSerializerCollectionDataCache.CodeGenTypeMetaData.Dispose();
            ghostComponentSerializerCollectionDataCache.EmptyVariants.Dispose();
            ghostComponentSerializerCollectionDataCache.DefaultVariants.Dispose();
            ghostComponentSerializerCollectionDataCache.TypeVariantCache.Dispose();
            ghostComponentSerializerCollectionDataCache = default;
            m_DefaultVariantsManaged = default;
        }

        public void AppendUserSpecifiedDefaultVariantsToSystem(Dictionary<ComponentType, DefaultVariantSystemBase.Rule> newDefaultVariants)
        {
            foreach (var kvp in newDefaultVariants)
            {
                var componentType = kvp.Key;
                var newRule = kvp.Value;
                var newRuleHash = newRule.CreateHashRule(componentType);
                if (m_DefaultVariantsManaged.TryGetValue(componentType, out var existingRule))
                {
                    var rulesAreTheSame = existingRule.Equals(newRule);
                    if (!rulesAreTheSame)
                    {
                        var useNew = newRule.GetHashCode() < existingRule.GetHashCode();
                        UnityEngine.Debug.LogError($"`{this}` is attempting to add a default variant rule '{newRule}' ('{newRuleHash}') for type `{componentType}` but one already " +
                            $"exists ('{existingRule}' ('{existingRule.CreateHashRule(componentType)}') in this world ('{World.Name}'), likely from a previous system! Using the rule with the smallest HashCode, which is rule '{(useNew ? newRule : existingRule)}'.");

                        if (!useNew)
                            continue;
                    }
                }

                m_DefaultVariantsManaged[componentType] = newRule;
            }

            var cache = ghostComponentSerializerCollectionDataCache;
            if (cache.DefaultVariants.IsCreated)
            {
                foreach (var kvp in newDefaultVariants)
                    cache.DefaultVariants[kvp.Key] = kvp.Value.CreateHashRule(kvp.Key);
            }
        }
    }

    /// <summary><see cref="GhostComponentSerializerCollectionSystemGroup"/>. Blittable. For internal use only.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GhostComponentSerializerCollectionData : IComponentData
    {
        internal byte CollectionInitialized;
        internal NativeMultiHashMap<ComponentType, GhostComponentSerializer.State> GhostComponentCollection;
        internal NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule> DefaultVariants;
        internal NativeMultiHashMap<ComponentType, VariantType> EmptyVariants;
        internal NativeHashMap<ulong, CodeGenTypeMetaData> CodeGenTypeMetaData;
        internal NativeHashMap<VariantQuery, VariantType> TypeVariantCache;
        internal FixedString32Bytes WorldName;

        ulong HashGhostComponentSerializer(in GhostComponentSerializer.State comp)
        {
            //this will give us a good starting point
            var compHash = TypeManager.GetTypeInfo(comp.ComponentType.TypeIndex).StableTypeHash;
            if(compHash == 0)
                throw new InvalidOperationException($"'{WorldName}': Unexpected 0 hash for type {comp.ComponentType}!");
            compHash = TypeHash.CombineFNV1A64(compHash, comp.GhostFieldsHash);
            //ComponentSize might depend on #ifdef or other compilation/platform rules so it must be not included. we will leave the comment here
            //so it is clear why we don't consider this field
            //compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ComponentSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.SnapshotSize));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64(comp.ChangeMaskBits));
            compHash = TypeHash.CombineFNV1A64(compHash, TypeHash.FNV1A64((int)comp.SendToOwner));
            return compHash;
        }

        /// <summary>
        /// Used by code-generated systems and meant For internal use only.
        /// Register an empty variant to the empty variants list.
        /// </summary>
        /// <param name="variantType"></param>
        public void AddEmptyVariant(VariantType variantType)
        {
            ThrowIfNoHash(variantType.Hash, $"AddEmptyVariant for '{variantType}'");
            EmptyVariants.Add(variantType.Component, variantType);
        }

        /// <summary>
        /// Used by code-generated systems and meant for internal use only.
        /// Adds the generated ghost serializer to <see cref="GhostComponentSerializer.State"/> collection.
        /// </summary>
        /// <param name="state"></param>
        public void AddSerializer(GhostComponentSerializer.State state)
        {
            //This is always enforced to avoid bad usage of the api
            if (CollectionInitialized != 0)
            {
                throw new InvalidOperationException($"'{WorldName}': Cannot register new GhostComponentSerializer for `{GetType().FullName}` after the RpcSystem has started running!");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            foreach (var existing in GhostComponentCollection.GetValuesForKey(state.ComponentType))
            {
                if (existing.VariantHash == state.VariantHash)
                {
                    throw new InvalidOperationException($"'{WorldName}': GhostComponentSerializer for `{GetType().FullName}` is already registered for type {state.ComponentType} and variant {state.VariantHash}!");
                }
            }

            ThrowIfNoHash(state.VariantHash, $"'{WorldName}': AddSerializer for '{state.ComponentType}'.");
#endif

            state.SerializerHash = HashGhostComponentSerializer(state);
            GhostComponentCollection.Add(state.ComponentType, state);
        }

        /// <summary>
        /// Used by code-generated systems and meant for internal use only.
        /// Add reflection meta-data for a "variant type hash".
        /// </summary>
        /// <remarks>Note that it's valid that this may clobber existing values.
        /// This is due to a quirk with source-generators: Multiple assemblies can define a variant for the same type (e.g. Translation)
        /// thus they'll both attempt to add meta-data for the Translation. A little wasteful, but generally harmless.</remarks>
        /// <param name="metaData"></param>
        public void AddCodeGenTypeMetaData(CodeGenTypeMetaData metaData)
        {
            CodeGenTypeMetaData[metaData.TypeHash] = metaData;
        }

        /// <summary>
        /// Finds the current variant for this ComponentType via <see cref="GetAllAvailableVariantsForType"/>, and updates
        /// the internal variant query cache to speed-up subsequent queries.
        /// </summary>
        [GenerateTestsForBurstCompatibility]
        [BurstCompile]
        internal VariantType GetCurrentVariantTypeForComponentCached(ComponentType componentType, ulong variantHash, bool isRoot)
        {
            var t = new VariantQuery(componentType, variantHash, isRoot);
            if (!TypeVariantCache.TryGetValue(t, out var variantType))
            {
                variantType = GetCurrentVariantTypeForComponentInternal(componentType, variantHash, isRoot);
                TypeVariantCache.Add(t, variantType);
            }

            return variantType;
        }

        /// <summary>
        /// Finds the current variant for this ComponentType using available variants via <see cref="GetAllAvailableVariantsForType"/>.
        /// If this is a component on a root entity, defaults to the default serializer, otherwise defaults to <see cref="DontSerializeVariant"/>.
        /// </summary>
        VariantType GetCurrentVariantTypeForComponentInternal(ComponentType componentType, ulong variantHash, bool isRoot)
        {
            using var available = GetAllAvailableVariantsForType(componentType, isRoot);
            return GetCurrentVariantTypeForComponent(componentType, variantHash, in available, isRoot);
        }

        /// <inheritdoc cref="GetCurrentVariantTypeForComponent"/>
        [GenerateTestsForBurstCompatibility]
        internal VariantType GetCurrentVariantTypeForComponent(ComponentType componentType, ulong variantHash, in NativeList<VariantType> available, bool isRoot)
        {
            if (available.Length != 0)
            {
                if (variantHash == 0)
                {
                    // Find the best default variant:
                    var bestIndex = 0;
                    for (var i = 1; i < available.Length; i++)
                    {
                        var bestV = available[bestIndex];
                        var availableV = available[i];
                        if (availableV.DefaultRule > bestV.DefaultRule)
                        {
                            bestIndex = i;
                        }
                        else if (availableV.DefaultRule == bestV.DefaultRule)
                        {
                            if (availableV.DefaultRule != VariantType.DefaultType.NotDefault)
                            {
                                BurstCompatibleErrorWithAggregate(in available, $"Type `{componentType}` (isRoot: {isRoot}) has 2 or more default variants with the same `DefaultRule` ({(int) availableV.DefaultRule})! Using the first. DefaultVariants: {DefaultVariants.Count}.");
                            }
                        }
                    }

                    var finalVariant = available[bestIndex];
                    if (finalVariant.DefaultRule != VariantType.DefaultType.NotDefault)
                        return finalVariant;

                    // We failed, so get the safest fallback:
                    var fallback = GetSafestFallbackVariantUponError(available);
                    BurstCompatibleErrorWithAggregate(in available, $"Type `{componentType}` (isRoot: {isRoot}) has NO default variants! Calculating the safest fallback guess ('{fallback.ToFixedString()}'). DefaultVariants: {DefaultVariants.Count}.");
                    return fallback;
                }

                // Find the EXACT variant by hash.
                foreach (var variant in available)
                    if (variant.Hash == variantHash)
                        return variant;

                // Couldn't find any, so try to get the safest fallback:
                if (available.Length != 0)
                {
                    var fallback = GetSafestFallbackVariantUponError(available);
                    BurstCompatibleErrorWithAggregate(in available, $"Failed to find variant for `{componentType}` (isRoot: {isRoot}) with hash '{variantHash}'! There are {available.Length} variants available, so calculating the safest fallback guess ('{fallback.ToFixedString()}'). DefaultVariants: {DefaultVariants.Count}.");
                    return fallback;
                }
            }

            // Failed to find anything, so fallback:
            BurstCompatibleErrorWithAggregate(in available, $"Unable to find variantHash '{variantHash}' for `{componentType}` (isRoot: {isRoot}) as no variants available for type! Fallback is `DontSerializeVariant`.");
            return ConstructDontSerializeVariant(componentType, VariantType.DefaultType.YesViaFallbackRule);
        }

        /// <summary>When we are unable to find the requested variant, this method finds the best fallback.</summary>
        static VariantType GetSafestFallbackVariantUponError(in NativeList<VariantType> available)
        {
            // Prefer to serialize all data on the ghost. Potentially wasteful, but "safest" as data will be replicated.
            for (var i = 0; i < available.Length; i++)
            {
                if (available[i].IsSerialized && available[i].IsDefaultSerializer)
                    return available[i];
            }

            // Otherwise fallback to a serialized variant.
            for (var i = 0; i < available.Length; i++)
            {
                if (available[i].IsSerialized)
                    return available[i];
            }

            // Otherwise fallback to the last in the list (most likely to be custom).
            return available[available.Length - 1];
        }

        /// <summary>
        /// <para><b>Finds all available variants for a given type, applying all variant rules at once.</b></para>
        /// <para>Since multiple variants can be present for any given component there are some important use cases that need to be
        /// handled.</para>
        /// <para> Note that, for <see cref="IInputBufferData"/>s, they'll return the variants available to their <see cref="IInputComponentData"/> authoring struct.</para>
        /// <para> Note that the number of default variants returned may not be 1 (it could be more or less).</para>
        /// </summary>
        /// <param name="componentType">Type to find the variant for.</param>
        /// <param name="isRoot">True if this component is on the root entity.</param>
        /// <returns>A list of all available variants for this `componentType`.</returns>
        [GenerateTestsForBurstCompatibility]
        [BurstCompile]
        public NativeList<VariantType> GetAllAvailableVariantsForType(ComponentType componentType, bool isRoot)
        {
            var availableVariants = new NativeList<VariantType>(4, Allocator.Temp);
            var numCustomVariants = 0;
            var customVariantIndex = -1;

            var metaData = GetOrCreateMetaData(componentType);

            // Code-gen: "Default Serializers".
            foreach (var state in GhostComponentCollection.GetValuesForKey(componentType))
            {
                if (state.ComponentType == componentType)
                {
                    var defaultType = CalculateDefaultTypeForSerializer(componentType, metaData, in state, isRoot);
                    var variantMetaData = GetOrCreateMetaData(state.VariantHash);
                    var variant = new VariantType
                    {
                        Component = componentType,
                        VariantTypeIndex = state.VariantTypeIndex,
                        Hash = state.VariantHash,
                        DefaultRule = defaultType,
                        IsSerialized = true,
                        IsTestVariant = variantMetaData.IsTestVariant,
                        IsDefaultSerializer = state.IsDefaultSerializer,
                        PrefabType = state.PrefabType,
                        Source = VariantType.VariantSource.SourceGeneratorSerializers,
                    };

                    AddAndCount(ref variant);
                }
            }

            // Code-gen: Empty variants are "Default Serializers" but without any GhostFields. Thus, "empty" serializers.
            foreach (var variant in EmptyVariants.GetValuesForKey(componentType))
            {
                if (variant.Component == componentType)
                {
                    var copyOfVariant = variant;
                    copyOfVariant.IsSerialized = false;
                    copyOfVariant.DefaultRule |= CalculateDefaultTypeForNonSerializedType(componentType, variant.Hash, isRoot, availableVariants.Length > 0);
                    copyOfVariant.Source = VariantType.VariantSource.SourceGeneratorEmptyVariants;

                    AddAndCount(ref copyOfVariant);
                }
            }

            // `ClientOnlyVariant` special case:
            if (VariantIsUserSpecifiedDefaultRule(componentType, GhostVariantsUtility.ClientOnlyHash, isRoot))
            {
                var clientOnlyVariant = new VariantType
                {
                    Component = componentType,
                    DefaultRule = VariantType.DefaultType.YesViaUserSpecifiedNamedDefault,
                    IsSerialized = false,
                    VariantTypeIndex = 1, // Hardcoded index lookup.
                    PrefabType = GhostPrefabType.Client,
                    Hash = GhostVariantsUtility.ClientOnlyHash,
                    Source = VariantType.VariantSource.ManualClientOnlyVariant,
                };
                ThrowIfNoHash(clientOnlyVariant.Hash, $"'{WorldName}': ClientOnlyVariant for '{componentType}'");

                AddAndCount(ref clientOnlyVariant);
            }

            // `DontSerializeVariant` special case:
            if (!metaData.IsInput && AllVariantsAreSerialized(in availableVariants))
            {
                var defaultTypeForDontSerializeVariant = CalculateDefaultTypeForNonSerializedType(componentType, GhostVariantsUtility.DontSerializeHash, isRoot, availableVariants.Length > 0);
                var dontSerializeVariant = ConstructDontSerializeVariant(componentType, defaultTypeForDontSerializeVariant);

                AddAndCount(ref dontSerializeVariant);
            }

            // If the type only has one custom variant, that is now the default:
            if (numCustomVariants == 1)
            {
                var customVariantFallback = availableVariants[customVariantIndex];
                customVariantFallback.DefaultRule |= VariantType.DefaultType.YesAsOnlyOneVariantBecomesDefault;
                availableVariants[customVariantIndex] = customVariantFallback;
            }

            availableVariants.Sort();

            return availableVariants;

            void AddAndCount(ref VariantType variant)
            {
                if (IsUserCreatedVariant(variant.Hash, variant.IsDefaultSerializer))
                {
                    numCustomVariants++;
                    customVariantIndex = availableVariants.Length;
                }

                if (variant.IsTestVariant)
                {
                    variant.DefaultRule |= VariantType.DefaultType.YesAsEditorDefault;
                }

                availableVariants.Add(variant);
            }

            static bool IsUserCreatedVariant(ulong variantTypeHash, bool isDefaultSerializer)
            {
                return !isDefaultSerializer && variantTypeHash != GhostVariantsUtility.DontSerializeHash && variantTypeHash != GhostVariantsUtility.ClientOnlyHash;
            }
        }

        VariantType ConstructDontSerializeVariant(ComponentType componentType, VariantType.DefaultType defaultType)
        {
            var dontSerializeVariant = new VariantType
            {
                Component = componentType,
                DefaultRule = defaultType,
                IsSerialized = false,
                VariantTypeIndex = 0, // Hardcoded index lookup.
                PrefabType = GhostPrefabType.All,
                Hash = GhostVariantsUtility.DontSerializeHash,
                Source = VariantType.VariantSource.ManualDontSerializeVariant,
            };
            ThrowIfNoHash(dontSerializeVariant.Hash, $"'{WorldName}': ConstructDontSerializeVariant for '{componentType}'");
            return dontSerializeVariant;
        }

        /// <summary>Fetch meta-data for any component. Used to avoid reflection.</summary>
        internal CodeGenTypeMetaData GetOrCreateMetaData(ComponentType componentType)
        {
            var hash = GhostVariantsUtility.CalculateVariantHashForComponent(componentType);
            return GetOrCreateMetaData(hash);
        }

        /// <summary>Fetches the meta-data from the cache <see cref="CodeGenTypeMetaData"/> (or builds one if one does not exist).</summary>
        internal CodeGenTypeMetaData GetOrCreateMetaData(ulong variantTypeHash)
        {
            ThrowIfNoHash(variantTypeHash, $"'{WorldName}': GetOrCreateMetaData for {variantTypeHash}");

            if (!CodeGenTypeMetaData.TryGetValue(variantTypeHash, out var metaData))
            {
                CodeGenTypeMetaData[variantTypeHash] = metaData = new CodeGenTypeMetaData
                {
                    TypeHash = variantTypeHash,
                    IsInputComponent = false,
                    IsInputBuffer = false,
                    IsTestVariant = false,
                    HasDontSupportPrefabOverridesAttribute = false,
                    HasSupportsPrefabOverridesAttribute = false,
                };
            }
            return metaData;
        }

        static bool AllVariantsAreSerialized(in NativeList<VariantType> availableVariants)
        {
            foreach (var x in availableVariants)
            {
                if (!x.IsSerialized)
                    return false;
            }

            return true;
        }

        internal static bool AnyVariantsAreSerialized(in NativeList<VariantType> availableVariants)
        {
            foreach (var x in availableVariants)
            {
                if (x.IsSerialized)
                    return true;
            }

            return false;
        }

        void BurstCompatibleErrorWithAggregate(in NativeList<VariantType> availableVariants, FixedString4096Bytes error)
        {
            error.Append(WorldName);
            error.Append((FixedString64Bytes)$", {availableVariants.Length} variants available: ");
            for (var i = 0; i < availableVariants.Length; i++)
            {
                var availableVariant = availableVariants[i];
                error.Append('\n');
                error.Append(i);
                error.Append(':');
                error.Append(availableVariant.ToFixedString());
            }

            UnityEngine.Debug.LogError(error);
        }

        /// <summary>
        /// <para>Variants have nested "is default" rules, checked in the following order:</para>
        /// <para> 1. If the user specified a <see cref="DefaultVariantSystemBase.Rule"/> via <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/>,
        /// we use that.</para>
        /// <para> 2. Otherwise, if this is component is on the root entity,
        ///        OR it's an input component,
        ///        return true ONLY IF it's the default serializer (i.e. variantType == componentType).</para>
        /// <para> 3. Otherwise, if this is component is on a child entity, return true if it's the <see cref="DontSerializeVariant"/>.</para>
        /// </summary>
        VariantType.DefaultType CalculateDefaultTypeForSerializer(ComponentType componentType, CodeGenTypeMetaData metaData, in GhostComponentSerializer.State state, bool isRoot)
        {
            if (VariantIsUserSpecifiedDefaultRule(componentType, state.VariantHash, isRoot))
                return VariantType.DefaultType.YesViaUserSpecifiedNamedDefault;

            // The user did NOT specify this as a default, so infer defaults from rules:
            if (isRoot || metaData.IsInput)
                return state.IsDefaultSerializer ? VariantType.DefaultType.YesAsIsDefaultSerializerAndIsRoot : VariantType.DefaultType.NotDefault;

            // Child entities default to DontSerializeVariant:
            // But that may have been changed via attribute:
            if (state.SendForChildEntities)
                return state.IsDefaultSerializer ? VariantType.DefaultType.YesAsAttributeAllowingChildSerialization : VariantType.DefaultType.NotDefault;

            return state.VariantHash == GhostVariantsUtility.DontSerializeHash ? VariantType.DefaultType.YesViaFallbackRule : VariantType.DefaultType.NotDefault;
        }

        /// <summary>
        /// <para>Variants have nested "is default" rules, checked in the following order:</para>
        /// <para> 1. If the user specified a <see cref="DefaultVariantSystemBase.Rule"/> via <see cref="DefaultVariantSystemBase.RegisterDefaultVariants"/>,
        /// we use that.</para>
        /// <para> 2. Otherwise, if this is component is on the root entity,
        ///        OR it's an input component,
        ///        return true ONLY IF it's the default serializer (i.e. variantType == componentType).</para>
        /// <para> 3. Otherwise, if this is component is on a child entity, return true if it's the <see cref="DontSerializeVariant"/>.</para>
        /// </summary>
        VariantType.DefaultType CalculateDefaultTypeForNonSerializedType(ComponentType componentType, ulong variantTypeHash, bool isRoot, bool hasAnyAvailableVariants)
        {
            if (VariantIsUserSpecifiedDefaultRule(componentType, variantTypeHash, isRoot))
                return VariantType.DefaultType.YesViaUserSpecifiedNamedDefault;
            return isRoot && hasAnyAvailableVariants ? VariantType.DefaultType.NotDefault : VariantType.DefaultType.YesAsIsChildDefaultingToDontSerializeVariant;
        }

        bool VariantIsUserSpecifiedDefaultRule(ComponentType componentType, ulong variantTypeHash, bool isRoot)
        {
            if (DefaultVariants.TryGetValue(componentType, out var existingRule))
            {
                var variantRule = (isRoot ? existingRule.VariantForParents : existingRule.VariantForChildren);
                if (variantRule != default)
                {
                    // The user DID SPECIFY a default, which invalidates all other defaults.
                    return variantRule == variantTypeHash;
                }
            }

            return false;
        }

        /// <summary>Validation that the SourceGenerators return valid hashes for "default serializers".</summary>
        /// <param name="hash">Hash to check.</param>
        /// <param name="context"></param>
        /// <exception cref="InvalidOperationException"></exception>
        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void ThrowIfNoHash(ulong hash, FixedString512Bytes context)
        {
            if (hash == 0)
                throw new InvalidOperationException($"Cannot add variant for context '{context}' as hash is zero! Set hashes for all variants via `GhostVariantsUtility` and ensure you've rebuilt NetCode 'Source Generators'.");
        }
    }
}
