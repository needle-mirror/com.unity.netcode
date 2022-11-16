using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// A component - variant - root tuple,
    /// used for caching the <see cref="GhostComponentSerializerCollectionData.GetAllAvailableSerializationStrategiesForType"/> result
    /// and speed-up successive query of the same component-variant combination.
    /// </summary>
    internal struct SerializationStrategyQuery : IComparable<SerializationStrategyQuery>, IEquatable<SerializationStrategyQuery>
    {
        public ComponentType ComponentType;
        public ulong variantHash;
        /// <summary>
        /// 0 = No.
        /// 1 = Yes.
        /// 2 to 255 = Special cases: Variant added to the map to be searchable.
        /// </summary>
        public byte IsRoot;

        public SerializationStrategyQuery(ComponentType type, ulong hash, byte isRoot)
        {
            ComponentType = type;
            variantHash = hash;
            IsRoot = isRoot;
        }

        public int CompareTo(SerializationStrategyQuery other)
        {
            var componentTypeComparison = ComponentType.CompareTo(other.ComponentType);
            if (componentTypeComparison != 0) return componentTypeComparison;
            var variantHashComparison = variantHash.CompareTo(other.variantHash);
            if (variantHashComparison != 0) return variantHashComparison;
            return IsRoot.CompareTo(other.IsRoot);
        }

        public bool Equals(SerializationStrategyQuery other)
        {
            return ComponentType.Equals(other.ComponentType) && variantHash == other.variantHash && IsRoot == other.IsRoot;
        }

        public override bool Equals(object obj)
        {
            return obj is SerializationStrategyQuery other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hashCode = ComponentType.GetHashCode();
            hashCode = (hashCode * 397) ^ variantHash.GetHashCode();
            hashCode = (hashCode * 397) ^ IsRoot.GetHashCode();
            return hashCode;
        }
    }

    // TODO - Make internal if possible.
    /// <summary>
    /// <para>
    /// For internal use only. Stores individual "serialization strategies" (and meta-data) for all netcode-informed components,
    /// as well as all variants of these components (<see cref="GhostComponentVariationAttribute"/>).
    /// Thus, maps to the code-generated <see cref="GhostComponentSerializer"/> ("Default Serializers") as well as
    /// all user-created Variants (<see cref="GhostComponentVariationAttribute"/>).
    /// This type also stores instances of the <see cref="DontSerializeVariant"/> and <see cref="ClientOnlyVariant"/>.
    /// </para>
    /// <para>
    /// Note: Serializers are considered "optional". It is perfectly valid for a types "serialization strategy" to be: "Do nothing".
    /// An example of this is a component for which a variant has been declared (using the <see cref="GhostComponentVariationAttribute"/>)
    /// but for which serialization is not generated, i.e: the <see cref="GhostComponent"/> attribute is specified in
    /// the base component declaration, but not in a variant. We call these "Empty Variants".
    /// </para>
    /// </summary>
    /// <remarks>This type was renamed from "VariantType" for 1.0.</remarks>
    public struct ComponentTypeSerializationStrategy : IComparable<ComponentTypeSerializationStrategy>
    {
        /// <summary>Denotes why this strategy is the default (or not). Higher value = more important.</summary>
        /// <remarks>This is a flags enum, so there may be multiple reasons why a strategy is considered the default.</remarks>
        [Flags]
        public enum DefaultType : byte
        {
            /// <summary>This is not the default.</summary>
            NotDefault = 0,
            /// <summary>It's an editor test variant, so we should default to it if we literally don't have any other defaults.</summary>
            YesAsEditorDefault = 1 << 1,
            /// <summary>This is the default variant only because we could not find a suitable one.</summary>
            YesAsIsFallback = 1 << 2,
            /// <summary>Child entities default to <see cref="DontSerializeVariant"/>.</summary>
            YesAsIsChildDefaultingToDontSerializeVariant = 1 << 3,
            /// <summary>The default serializer should be used if we're a root.</summary>
            YesAsIsDefaultSerializerAndIsRoot = 1 << 4,
            /// <summary>Yes via <see cref="GhostComponentAttribute"/>.</summary>
            YesAsAttributeAllowingChildSerialization = 1 << 5,
            /// <summary>If the developer has created only 1 variant for a type, it becomes the default.</summary>
            YesAsOnlyOneVariantBecomesDefault = 1 << 6,
            /// <summary>This is a default variant because the user has marked it as such via <see cref="DefaultVariantSystemBase"/>. Highest priority.</summary>
            YesViaUserSpecifiedNamedDefault = 1 << 7,
        }

        /// <summary>Indexer into <see cref="GhostComponentSerializerCollectionData.SerializationStrategies"/> list.</summary>
        public short SelfIndex;
        /// <summary>Indexes into the <see cref="GhostComponentSerializerCollectionData.Serializers"/>.</summary>
        /// <remarks>Serializers are optional. Thus, 0 if this type does not serialize component data.</remarks>
        public short SerializerIndex;
        /// <summary>Component that this Variant is associated with.</summary>
        public ComponentType Component;
        /// <summary>Hash identifier for the strategy. Should be non-zero by the time it's used in <see cref="GhostComponentSerializerCollectionData.SelectSerializationStrategyForComponentWithHash"/>.</summary>
        public ulong Hash;
        /// <summary>
        /// The <see cref="GhostPrefabType"/> value set in <see cref="GhostComponent"/> present in the variant declaration.
        /// Some variants modify the serialization rules. Default is <see cref="GhostPrefabType.All"/>
        /// </summary>
        public GhostPrefabType PrefabType;
        ///<summary>Override which client type it will be sent to, if we're able to determine.</summary>
        public GhostSendType SendTypeOptimization;
        /// <summary><see cref="DefaultType"/></summary>
        public DefaultType DefaultRule;
        // TODO - Create a flag byte enum for all of these.
        /// <summary>
        /// True if this is the "default" serializer for this component type.
        /// I.e. The one generated from the component definition itself (see <see cref="GhostFieldAttribute"/> and <see cref="GhostComponentAttribute"/>).
        /// </summary>
        /// <remarks>Types like `Translation` don't have a default serializer as the type itself doesn't define any GhostFields, but they do have serialized variants.</remarks>
        public byte IsDefaultSerializer;
        /// <summary><inheritdoc cref="GhostComponentVariationAttribute.IsTestVariant"/></summary>
        /// <remarks>True if this is an editor test variant. Forces this variant to be considered a "default" which makes writing tests easier.</remarks>
        public byte IsTestVariant;
        /// <summary>True if the <see cref="GhostComponentAttribute.SendDataForChildEntity"/> flag is true on this variant (if it has one), or this type (if not).</summary>
        public byte SendForChildEntities;
        /// <summary>True if the code-generator determined that this is an input component (or a variant of one).</summary>
        public byte IsInputComponent;
        /// <summary>True if the code-generator determined that this is an input buffer.</summary>
        public byte IsInputBuffer;
        /// <summary>Does this component explicitly opt-out of overrides (regardless of variant count)?</summary>
        public byte HasDontSupportPrefabOverridesAttribute;
        /// <summary>Does this component explicitly opt-in to overrides (regardless of variant count)?</summary>
        public byte HasSupportsPrefabOverridesAttribute;
        /// <summary><see cref="IsInputComponent"/> and <see cref="IsInputBuffer"/>.</summary>
        internal byte IsInput => (byte) (IsInputComponent | IsInputBuffer);
        /// <summary>The type name, unless it has a Variant (in which case it'll use the Variant Display name... assuming that is not null).</summary>
        public FixedString64Bytes DisplayName;
        /// <summary>True if this variant serializes its data.</summary>
        /// <remarks>Note that this will also be true if the type has the attribute <see cref="GhostEnabledBitAttribute"/>.</remarks>
        public byte IsSerialized => (byte) (SerializerIndex >= 0 ? 1 : 0);
        /// <summary>True if this variant is the <see cref="DontSerializeVariant"/>.</summary>
        public bool IsDontSerializeVariant => Hash == GhostVariantsUtility.DontSerializeHash;
        /// <summary>True if this variant is the <see cref="ClientOnlyVariant"/>.</summary>
        public bool IsClientOnlyVariant => Hash == GhostVariantsUtility.ClientOnlyHash;

        /// <summary>
        /// Check if two VariantType are identical.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(ComponentTypeSerializationStrategy other)
        {
            if (IsSerialized != other.IsSerialized)
                return IsSerialized - other.IsSerialized;
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
        public override string ToString() => ToFixedString().ToString();

        /// <summary>Logs a burst compatible debug string (if in burst), otherwise logs even more info.</summary>
        /// <returns>A debug string.</returns>
        [GenerateTestsForBurstCompatibility]
        public FixedString512Bytes ToFixedString()
        {
            var fs = new FixedString512Bytes((FixedString32Bytes) $"SS<");
            fs.Append(Component.GetDebugTypeName());
            fs.Append((FixedString128Bytes) $">[{DisplayName}, H:{Hash}, DR:{(int) DefaultRule}, SI:{SerializerIndex}, PT:{(int) PrefabType}, self:{SelfIndex}]");
            return fs;
        }
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
        /// <summary>HashSets and HashTables have a fixed capacity.</summary>
        /// <remarks>Increase this if you have lots of variants. Hardcoded multiplier is due to DontSerializeVariants.</remarks>
        public static int CollectionDefaultCapacity = (int) (DynamicTypeList.MaxCapacity * 2.2);

        /// <summary>Hacky workaround for GetSingleton not working on frame 0 (not sure why, as creation order is correct).</summary>
        internal GhostComponentSerializerCollectionData ghostComponentSerializerCollectionDataCache { get; private set; }

        /// <summary>
        /// Used to store the default ghost component variation mapping during the world creation.
        /// </summary>
        internal GhostVariantRules DefaultVariantRules { get; private set; }

        struct NeverCreatedSingleton : IComponentData
        {}

        protected override void OnCreate()
        {
            base.OnCreate();
            RequireForUpdate<NeverCreatedSingleton>();
            var worldNameShortened = new FixedString32Bytes();
            FixedStringMethods.CopyFromTruncated(ref worldNameShortened, World.Unmanaged.Name);
            ghostComponentSerializerCollectionDataCache = new GhostComponentSerializerCollectionData
            {
                WorldName = worldNameShortened,
                Serializers = new NativeList<GhostComponentSerializer.State>(CollectionDefaultCapacity, Allocator.Persistent),
                SerializationStrategies = new NativeList<ComponentTypeSerializationStrategy>(CollectionDefaultCapacity, Allocator.Persistent),
                SerializationStrategiesComponentTypeMap = new NativeMultiHashMap<ComponentType, short>(CollectionDefaultCapacity, Allocator.Persistent),
                DefaultVariants = new NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule>(CollectionDefaultCapacity, Allocator.Persistent),
                SerializationStrategiesCache = new NativeHashMap<SerializationStrategyQuery, short>(CollectionDefaultCapacity, Allocator.Persistent),
            };
            DefaultVariantRules = new GhostVariantRules(ghostComponentSerializerCollectionDataCache.DefaultVariants);
            //ATTENTION! this entity is destroyed in the BakingWorld, because in the first import this is what it does, it clean all the Entities in the world when you
            //open a scene.
            //For that reason, is the current world is a Baking word. this entity is "lazily" recreated by the GhostAuthoringBakingSystem if missing.
            EntityManager.CreateSingleton(ghostComponentSerializerCollectionDataCache);
        }

        protected override void OnDestroy()
        {
            ghostComponentSerializerCollectionDataCache.Dispose();
            ghostComponentSerializerCollectionDataCache = default;
            DefaultVariantRules = null;
        }
    }

    /// <summary><see cref="GhostComponentSerializerCollectionSystemGroup"/>. Blittable. For internal use only.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GhostComponentSerializerCollectionData : IComponentData
    {
        internal byte CollectionInitialized;
        /// <summary>
        /// All the Serializers. Allows us to serialize <see cref="ComponentType"/>'s to the snapshot.
        /// </summary>
        internal NativeList<GhostComponentSerializer.State> Serializers;
        /// <summary>
        /// Stores all known code-forced default variants.
        /// </summary>
        internal NativeHashMap<ComponentType, DefaultVariantSystemBase.HashRule> DefaultVariants;
        /// <summary>
        /// Every netcode-related ComponentType needs a "strategy" for serializing it. This stores all of them.
        /// </summary>
        internal NativeList<ComponentTypeSerializationStrategy> SerializationStrategies;
        /// <summary>
        /// Cache and lookup into the <see cref="SerializationStrategies"/> list for the <see cref="GetAllAvailableSerializationStrategiesForType"/> call.
        /// </summary>
        internal NativeHashMap<SerializationStrategyQuery, short> SerializationStrategiesCache;
        /// <summary>
        /// Maps a given <see cref="ComponentType"/> to an entry in the <see cref="SerializationStrategies"/> collection.
        /// </summary>
        internal NativeMultiHashMap<ComponentType, short> SerializationStrategiesComponentTypeMap;
        /// <summary>
        /// For debugging and exception strings.
        /// </summary>
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
        /// <param name="serializationStrategy"></param>
        public void AddSerializationStrategy(ref ComponentTypeSerializationStrategy serializationStrategy)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ThrowIfNoHash(serializationStrategy.Hash, serializationStrategy.ToFixedString());
            if (serializationStrategy.DisplayName.IsEmpty)
            {
                UnityEngine.Debug.LogError($"{serializationStrategy.ToFixedString()} doesn't have a valid DisplayName! Ensure you set it, even if it's just to the ComponentType name.");
                serializationStrategy.DisplayName.CopyFromTruncated(serializationStrategy.Component.ToFixedString());
            }

            foreach (var existingSSIndex in SerializationStrategiesComponentTypeMap.GetValuesForKey(serializationStrategy.Component))
            {
                var existingSs = SerializationStrategies[existingSSIndex];
                if (existingSs.Hash == serializationStrategy.Hash || existingSs.DisplayName == serializationStrategy.DisplayName)
                {
                    UnityEngine.Debug.LogError($"{serializationStrategy.ToFixedString()} has the same Hash or DisplayName as already-added one (below)! Likely error in code-generation, must fix!\n{existingSs.ToFixedString()}!");
                }
            }
#endif

            serializationStrategy.SelfIndex = (short)SerializationStrategies.Length;
            SerializationStrategies.Add(serializationStrategy);
            SerializationStrategiesComponentTypeMap.Add(serializationStrategy.Component, serializationStrategy.SelfIndex);

            //if (serializationStrategy.SerializeEnabledBit != 0)
            //    GhostComponentsWithReplicatedEnabledBit.Add(serializationStrategy.Component);
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
                throw new InvalidOperationException($"'{WorldName}': Cannot register new GhostComponentSerializer for type {state.ComponentType} after the RpcSystem has started running!");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            ThrowIfNoHash(state.VariantHash, $"'{WorldName}': AddSerializer for '{state.ComponentType}'.");
#endif

            // Map to SerializationStrategy:
            MapSerializerToStrategy(ref state, (short) Serializers.Length);
            state.SerializerHash = HashGhostComponentSerializer(state);
            Serializers.Add(state);
        }

        internal unsafe void MapSerializerToStrategy(ref GhostComponentSerializer.State state, short serializerIndex)
        {
            foreach (var ssIndex in SerializationStrategiesComponentTypeMap.GetValuesForKey(state.ComponentType))
            {
                ref var ss = ref SerializationStrategies.ElementAt(ssIndex);
                if (ss.Hash == state.VariantHash)
                {
                    state.SerializationStrategyIndex = ssIndex;
                    ss.SerializerIndex = serializerIndex;
                    return;
                }
            }

            throw new InvalidOperationException($"No SerializationStrategy found for Serializer with Hash: {state.VariantHash}!");
        }

        /// <summary>
        /// Finds the current variant for this ComponentType via <see cref="GetAllAvailableSerializationStrategiesForType"/>, and updates
        /// the internal variant query cache to speed-up subsequent queries.
        /// </summary>
        [GenerateTestsForBurstCompatibility]
        [BurstCompile]
        internal ComponentTypeSerializationStrategy GetCurrentSerializationStrategyForComponentCached(ComponentType componentType, ulong variantHash, bool isRoot)
        {
            var q = new SerializationStrategyQuery(componentType, variantHash, (byte) (isRoot ? 1 : 0));
            if (!SerializationStrategiesCache.TryGetValue(q, out var ssIndex))
            {
                var serializationStrategy = GetCurrentSerializationStrategyForComponentInternal(componentType, variantHash, isRoot);
                ssIndex = serializationStrategy.SelfIndex;
                SerializationStrategiesCache.Add(q, ssIndex);
            }
            if(ssIndex < 0)
                BurstCompatibleErrorWithAggregate(componentType, default, $"{componentType.GetDebugTypeName()} is -1!");

            return SerializationStrategies[ssIndex];
        }

        /// <summary>
        /// Finds the current variant for this ComponentType using available variants via <see cref="GetAllAvailableSerializationStrategiesForType"/>.
        /// </summary>
        /// <param name="componentType">The type we're finding the SS for.</param>
        /// <param name="variantHash">The hash to use to lookup with. 0 implies "use default",
        /// which is the default serializer for components on the root entity,
        /// and <see cref="DontSerializeVariant"/> for components on children.</param>
        /// <param name="isRoot">True if the entity is a root entity, false if it's a child.
        /// This distinction is because child entities default to <see cref="DontSerializeVariant"/>.</param>
        ComponentTypeSerializationStrategy GetCurrentSerializationStrategyForComponentInternal(ComponentType componentType, ulong variantHash, bool isRoot)
        {
            using var available = GetAllAvailableSerializationStrategiesForType(componentType, isRoot);
            return SelectSerializationStrategyForComponentWithHash(componentType, variantHash, in available, isRoot);
        }

        /// <inheritdoc cref="GetCurrentSerializationStrategyForComponentInternal"/>
        [GenerateTestsForBurstCompatibility]
        internal ComponentTypeSerializationStrategy SelectSerializationStrategyForComponentWithHash(ComponentType componentType, ulong serializationStrategyHash, in NativeList<ComponentTypeSerializationStrategy> available, bool isRoot)
        {
            if (available.Length != 0)
            {
                if (serializationStrategyHash == 0)
                {
                    // Find the best default ss:
                    var bestIndex = 0;
                    for (var i = 1; i < available.Length; i++)
                    {
                        var bestSs = available[bestIndex];
                        var availableSs = available[i];
                        if (availableSs.DefaultRule > bestSs.DefaultRule)
                        {
                            bestIndex = i;
                        }
                        else if (availableSs.DefaultRule == bestSs.DefaultRule)
                        {
                            if (availableSs.DefaultRule != ComponentTypeSerializationStrategy.DefaultType.NotDefault)
                            {
                                BurstCompatibleErrorWithAggregate(componentType, in available, $"Type `{componentType.ToFixedString()}` (isRoot: {isRoot}) has 2 or more default serialization strategies with the same `DefaultRule` ({(int) availableSs.DefaultRule})! Using the first. DefaultVariants: {DefaultVariants.Count}.");
                            }
                        }
                    }

                    var finalVariant = available[bestIndex];
                    if (finalVariant.DefaultRule != ComponentTypeSerializationStrategy.DefaultType.NotDefault)
                        return finalVariant;

                    // We failed, so get the safest fallback:
                    var fallback = GetSafestFallbackVariantUponError(available);
                    BurstCompatibleErrorWithAggregate(componentType, in available, $"Type `{componentType.ToFixedString()}` (isRoot: {isRoot}) has NO default serialization strategies! Calculating the safest fallback guess ('{fallback.ToFixedString()}'). DefaultVariants: {DefaultVariants.Count}.");
                    return fallback;
                }

                // Find the EXACT variant by hash.
                foreach (var variant in available)
                    if (variant.Hash == serializationStrategyHash)
                        return variant;

                // Couldn't find any, so try to get the safest fallback:
                if (available.Length != 0)
                {
                    var fallback = GetSafestFallbackVariantUponError(available);
                    BurstCompatibleErrorWithAggregate(componentType, in available, $"Failed to find serialization strategy for `{componentType.ToFixedString()}` (isRoot: {isRoot}) with hash '{serializationStrategyHash}'! There are {available.Length} serialization strategies available, so calculating the safest fallback guess ('{fallback.ToFixedString()}'). DefaultVariants: {DefaultVariants.Count}.");
                    return fallback;
                }
            }

            // Failed to find anything, so fallback:
            BurstCompatibleErrorWithAggregate(componentType, in available, $"Unable to find serializationStrategyHash '{serializationStrategyHash}' for `{componentType.ToFixedString()}` (isRoot: {isRoot}) as no serialization strategies available for type! Fallback is `DontSerializeVariant`.");
            return ConstructDontSerializeVariant(componentType, ComponentTypeSerializationStrategy.DefaultType.YesAsIsFallback);
        }

        /// <summary>When we are unable to find the requested variant, this method finds the best fallback.</summary>
        static ComponentTypeSerializationStrategy GetSafestFallbackVariantUponError(in NativeList<ComponentTypeSerializationStrategy> available)
        {
            // Prefer to serialize all data on the ghost. Potentially wasteful, but "safest" as data will be replicated.
            for (var i = 0; i < available.Length; i++)
            {
                if (available[i].IsSerialized != 0 && available[i].IsDefaultSerializer != 0)
                    return available[i];
            }

            // Otherwise fallback to a serialized variant.
            for (var i = 0; i < available.Length; i++)
            {
                if (available[i].IsSerialized != 0)
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
        public NativeList<ComponentTypeSerializationStrategy> GetAllAvailableSerializationStrategiesForType(ComponentType componentType, bool isRoot)
        {
            var availableVariants = new NativeList<ComponentTypeSerializationStrategy>(4, Allocator.Temp);
            var numCustomVariants = 0;
            var customVariantIndex = -1;

            // Code-gen: "Serialization Strategies" are generated and mapped here.
            foreach (var strategyLookup in SerializationStrategiesComponentTypeMap.GetValuesForKey(componentType))
            {
                var strategy = SerializationStrategies[strategyLookup];

                if (strategy.IsSerialized != 0)
                    strategy.DefaultRule |= CalculateDefaultTypeForSerializer(componentType, isRoot, strategy.IsDefaultSerializer, strategy.IsInput, strategy.Hash, strategy.SendForChildEntities);
                else
                    strategy.DefaultRule |= CalculateDefaultTypeForNonSerializedType(componentType, strategy.Hash, isRoot, availableVariants.Length > 0);

                AddAndCount(ref strategy);
            }

            // `ClientOnlyVariant` special case:
            if (VariantIsUserSpecifiedDefaultRule(componentType, GhostVariantsUtility.ClientOnlyHash, isRoot))
            {
                var clientOnlyVariant = new ComponentTypeSerializationStrategy
                {
                    Component = componentType,
                    DefaultRule = ComponentTypeSerializationStrategy.DefaultType.YesViaUserSpecifiedNamedDefault,
                    SerializerIndex = -1, // Client only so non-serialized. No need to warn, as this is expected behaviour for all GhostEnabledBits, when using `ClientOnlyVariant`.
                    SelfIndex = -1, // Hardcoded index lookup.
                    PrefabType = GhostPrefabType.Client,
                    Hash = GhostVariantsUtility.ClientOnlyHash,
                    DisplayName = nameof(ClientOnlyVariant),
                };
                AddSerializationStrategy(ref clientOnlyVariant);

                AddAndCount(ref clientOnlyVariant);
            }

            // `DontSerializeVariant` special case:
            if (!IsInput(availableVariants) && AllVariantsAreSerialized(in availableVariants))
            {
                var defaultTypeForDontSerializeVariant = CalculateDefaultTypeForNonSerializedType(componentType, GhostVariantsUtility.DontSerializeHash, isRoot, availableVariants.Length > 0);
                var dontSerializeVariant = ConstructDontSerializeVariant(componentType, defaultTypeForDontSerializeVariant);

                AddAndCount(ref dontSerializeVariant);
            }

            // If the type only has one custom variant, that is now the default:
            if (numCustomVariants == 1)
            {
                var customVariantFallback = availableVariants[customVariantIndex];
                customVariantFallback.DefaultRule |= ComponentTypeSerializationStrategy.DefaultType.YesAsOnlyOneVariantBecomesDefault;
                availableVariants[customVariantIndex] = customVariantFallback;
            }

            availableVariants.Sort();

            return availableVariants;

            void AddAndCount(ref ComponentTypeSerializationStrategy variant)
            {
                if (IsUserCreatedVariant(variant.Hash, variant.IsDefaultSerializer))
                {
                    numCustomVariants++;
                    customVariantIndex = availableVariants.Length;
                }

                if (variant.IsTestVariant != 0)
                {
                    variant.DefaultRule |= ComponentTypeSerializationStrategy.DefaultType.YesAsEditorDefault;
                }

                availableVariants.Add(variant);
            }

            static bool IsUserCreatedVariant(ulong variantTypeHash, byte isDefaultSerializer)
            {
                return isDefaultSerializer == 0 && variantTypeHash != GhostVariantsUtility.DontSerializeHash && variantTypeHash != GhostVariantsUtility.ClientOnlyHash;
            }
        }

        static bool IsInput(NativeList<ComponentTypeSerializationStrategy> availableVariants)
        {
            foreach (var ss in availableVariants)
                if(ss.IsInput != 0)
                    return true;
            return false;
        }

        ComponentTypeSerializationStrategy ConstructDontSerializeVariant(ComponentType componentType, ComponentTypeSerializationStrategy.DefaultType defaultType)
        {
            var dontSerializeVariant = new ComponentTypeSerializationStrategy
            {
                Component = componentType,
                DefaultRule = defaultType,
                SerializerIndex = -1,
                SelfIndex = -1,
                PrefabType = GhostPrefabType.All,
                Hash = GhostVariantsUtility.DontSerializeHash,
                DisplayName = nameof(DontSerializeVariant),
            };
            AddSerializationStrategy(ref dontSerializeVariant);
            return dontSerializeVariant;
        }

        static bool AllVariantsAreSerialized(in NativeList<ComponentTypeSerializationStrategy> availableVariants)
        {
            foreach (var x in availableVariants)
            {
                if (x.IsSerialized == 0)
                    return false;
            }

            return true;
        }

        internal static bool AnyVariantsAreSerialized(in NativeList<ComponentTypeSerializationStrategy> availableVariants)
        {
            foreach (var x in availableVariants)
            {
                if (x.IsSerialized != 0)
                    return true;
            }

            return false;
        }

        void BurstCompatibleErrorWithAggregate(ComponentType componentType, in NativeList<ComponentTypeSerializationStrategy> availableVariants, FixedString4096Bytes error)
        {
            error.Append(WorldName);
            error.Append(' ');
            error.Append(componentType.ToFixedString());
            if (availableVariants.IsCreated)
            {
                error.Append((FixedString64Bytes) $", {availableVariants.Length} variants available: ");
                for (var i = 0; i < availableVariants.Length; i++)
                {
                    var availableVariant = availableVariants[i];
                    error.Append('\n');
                    error.Append(i);
                    error.Append(':');
                    error.Append(availableVariant.ToFixedString());
                }
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
        ComponentTypeSerializationStrategy.DefaultType CalculateDefaultTypeForSerializer(ComponentType componentType, bool isRoot, byte isDefaultSerializer, byte isInput, ulong ssHash, byte sendForChildEntities)
        {
            if (VariantIsUserSpecifiedDefaultRule(componentType, ssHash, isRoot))
                return ComponentTypeSerializationStrategy.DefaultType.YesViaUserSpecifiedNamedDefault;

            // The user did NOT specify this as a default, so infer defaults from rules:
            if (isRoot || isInput != 0)
                return isDefaultSerializer != 0 ? ComponentTypeSerializationStrategy.DefaultType.YesAsIsDefaultSerializerAndIsRoot : ComponentTypeSerializationStrategy.DefaultType.NotDefault;

            // Child entities default to DontSerializeVariant:
            // But that may have been changed via attribute:
            if (sendForChildEntities != 0)
                return isDefaultSerializer != 0 ? ComponentTypeSerializationStrategy.DefaultType.YesAsAttributeAllowingChildSerialization : ComponentTypeSerializationStrategy.DefaultType.NotDefault;

            return ssHash == GhostVariantsUtility.DontSerializeHash ? ComponentTypeSerializationStrategy.DefaultType.YesAsIsChildDefaultingToDontSerializeVariant : ComponentTypeSerializationStrategy.DefaultType.NotDefault;
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
        ComponentTypeSerializationStrategy.DefaultType CalculateDefaultTypeForNonSerializedType(ComponentType componentType, ulong variantTypeHash, bool isRoot, bool hasAnyAvailableVariants)
        {
            if (VariantIsUserSpecifiedDefaultRule(componentType, variantTypeHash, isRoot))
                return ComponentTypeSerializationStrategy.DefaultType.YesViaUserSpecifiedNamedDefault;
            return isRoot && hasAnyAvailableVariants ? ComponentTypeSerializationStrategy.DefaultType.NotDefault : ComponentTypeSerializationStrategy.DefaultType.YesAsIsChildDefaultingToDontSerializeVariant;
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

        /// <summary>Release the allocated resources used to store the ghost serializer strategies and mappings.</summary>
        public void Dispose()
        {
            Serializers.Dispose();
            SerializationStrategies.Dispose();
            DefaultVariants.Dispose();
            SerializationStrategiesCache.Dispose();
            SerializationStrategiesComponentTypeMap.Dispose();
        }

        /// <summary>
        /// Validate that all the serialization strategies have a valid <see cref="ComponentTypeSerializationStrategy.SerializerIndex"/>
        /// and that all the <see cref="GhostComponentSerializer.State.SerializationStrategyIndex"/> have been set.
        /// </summary>
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void Validate()
        {
            for (var i = 0; i < SerializationStrategies.Length; i++)
            {
                var serializationStrategy = SerializationStrategies[i];
                UnityEngine.Assertions.Assert.AreEqual(i, serializationStrategy.SelfIndex, "SerializationStrategies[i]");
                if (serializationStrategy.SerializerIndex >= 0)
                {
                    UnityEngine.Assertions.Assert.IsTrue(serializationStrategy.SerializerIndex < Serializers.Length, "SerializationStrategies > Serializer Index in Range");
                    UnityEngine.Assertions.Assert.AreEqual(i, Serializers[serializationStrategy.SerializerIndex].SerializationStrategyIndex, "SerializationStrategies > Serializer > SerializationStrategies backwards lookup!");
                }
            }
            foreach (var serializer in Serializers)
            {
                UnityEngine.Assertions.Assert.IsTrue(serializer.SerializationStrategyIndex >= 0 && serializer.SerializationStrategyIndex < SerializationStrategies.Length, "Serializer > SerializationStrategies Index in Range");
            }
        }
    }
}
