using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// Helper builder that let you to construct a TypeInformation tree from a Rosylin ITypeSymbol
    /// </summary>
    internal struct TypeInformationBuilder
    {
        public enum SerializationMode
        {
            Component,
            Commands,
            Variant,
        }

        private GeneratorExecutionContext m_context;
        private IDiagnosticReporter m_Reporter;
        private SerializationMode m_SerializationMode;
        private List<string> m_MissingGhostFields;

        public List<string> MissingGhostFields => m_MissingGhostFields;

        /// <summary>
        /// Used to control the level of accessibility required by struct used for serialization.
        /// Components and Buffers must have GhostFields only on public members.
        /// But for variant declaration this is not necessary, since the variant is never used (is only a proxy type)
        /// </summary>
        private bool m_RequiresPublicFields;

        public TypeInformationBuilder(IDiagnosticReporter reporter, GeneratorExecutionContext context, SerializationMode mode)
        {
            m_context = context;
            m_Reporter = reporter;
            m_SerializationMode = mode;
            m_MissingGhostFields = new List<string>();
            m_RequiresPublicFields = mode != SerializationMode.Variant;
        }

        /// <summary>
        /// Build a code-generation specific semantic tree model for the <paramref name="symbol"/> type
        /// </summary>
        /// <returns></returns>
        public TypeInformation BuildTypeInformation(ITypeSymbol symbol, GhostComponentAttribute ghostAttribute, GhostField ghostFieldOverride = null)
        {
            m_context.CancellationToken.ThrowIfCancellationRequested();
            m_Reporter.LogDebug($"Building type info for {symbol}");
            var isEnableableComponent = Roslyn.Extensions.ImplementsInterface(symbol, "Unity.Entities.IEnableableComponent");
            var hasGhostEnabledBitAttribute = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "GhostEnabledBitAttribute") != null;
            var fullTypeName = Roslyn.Extensions.GetFullTypeName(symbol);

            if (hasGhostEnabledBitAttribute && !isEnableableComponent)
            {
                m_Reporter.LogError($"'{fullTypeName}' has attribute `[GhostEnabledBit]` (denoting that its enabled bit will be replicated), but the component is not implementing the `IEnableableComponent` interface! Either remove the attribute, or implement the interface.");
                return null;
            }

            var typeInfo = new TypeInformation
            {
                Kind = Roslyn.Extensions.GetTypeKind(symbol),
                ComponentType = Roslyn.Extensions.GetComponentType(symbol),
                TypeFullName = fullTypeName,
                Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(symbol),
                FieldName = string.Empty,
                FieldTypeName = Roslyn.Extensions.GetFieldTypeName(symbol),
                UnderlyingTypeName = String.Empty,
                Attribute = TypeAttribute.Empty(),
                AttributeMask = m_SerializationMode != SerializationMode.Commands
                    ? TypeAttribute.AttributeFlags.All
                    : TypeAttribute.AttributeFlags.None,
                GhostAttribute = ghostAttribute,
                Location = symbol.Locations[0],
                Symbol = symbol,
                ShouldSerializeEnabledBit = isEnableableComponent && hasGhostEnabledBitAttribute,
                HasDontSupportPrefabOverridesAttribute = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "DontSupportPrefabOverridesAttribute") != null,
                IsTestVariant = false,
            };
            //Mask out inherited attributes that does not apply. SubType is also never inherited, buffer fields are never interpolated
            if (typeInfo.ComponentType != ComponentType.Component)
                typeInfo.AttributeMask &= ~TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated;

            //This can be a little expensive sometime (up to tents of ms)
            var members = symbol.GetMembers();
            using (new Profiler.Auto("ParseMembers"))
            {
                foreach (var member in members.OfType<IFieldSymbol>())
                {
                    m_context.CancellationToken.ThrowIfCancellationRequested();
                    if (typeInfo.ComponentType is ComponentType.CommandData or ComponentType.Rpc &&
                        m_SerializationMode == SerializationMode.Commands &&
                        ShouldDiscardCommandField(member))
                        continue;

                    //This is a little expensive operation (up to some ms)
                    var memberType = member.Type;
                    var field = ParseFieldType(member, memberType, typeInfo, string.Empty, 1, ghostFieldOverride);
                    if (field != null)
                        typeInfo.GhostFields.Add(field);
                }
            }

            using (new Profiler.Auto("ParseProperties"))
            {
                foreach (var prop in members.OfType<IPropertySymbol>())
                {
                    m_context.CancellationToken.ThrowIfCancellationRequested();
                    if (!CheckIsSerializableProperty(prop))
                        continue;

                    if (typeInfo.ComponentType is ComponentType.CommandData or ComponentType.Rpc &&
                        m_SerializationMode == SerializationMode.Commands &&
                        ShouldDiscardCommandField(prop))
                        continue;

                    var field = ParseFieldType(prop, prop.Type, typeInfo, string.Empty, 1, ghostFieldOverride);
                    if (field != null)
                        typeInfo.GhostFields.Add(field);
                }
            }

            return typeInfo;
        }

        /// <summary>
        /// Build a TypeDescriptor tree model for the variant type <paramref name="variantSymbol"/> type.
        /// </summary>
        /// <returns></returns>
        public TypeInformation BuildVariantTypeInformation(ITypeSymbol variantSymbol, AttributeData variantAttribute, GhostComponentAttribute ghostAttribute)
        {
            m_context.CancellationToken.ThrowIfCancellationRequested();
            //Fetch the argument from the template declaration. This is the type for witch we want to inject serialization
            if (variantAttribute.ConstructorArguments.Length == 0)
            {
                m_Reporter.LogError($"{variantSymbol.Name} does not have constructor arguments", variantSymbol.Locations[0]);
                return null;
            }

            var adapteeType = (ITypeSymbol)variantAttribute.ConstructorArguments[0].Value;
            if (adapteeType == null)
            {
                m_Reporter.LogError($"{variantSymbol} constructed with a null type", variantSymbol.Locations[0]);
                return null;
            }
            if (adapteeType.DeclaredAccessibility == Accessibility.NotApplicable)
            {
                m_Reporter.LogError($"{variantSymbol.Name}: problem parsing this type, make sure the compilation unit compiles", variantSymbol.Locations[0]);
                return null;
            }
            if (adapteeType.DeclaredAccessibility != Accessibility.Public)
            {
                m_Reporter.LogError($"{variantSymbol.Name}: the component type must be public accessible", variantSymbol.Locations[0]);
                return null;
            }
            if (Roslyn.Extensions.GetAttribute(adapteeType, "Unity.NetCode", "DontSupportPrefabOverridesAttribute") != null)
            {
                m_Reporter.LogError($"{variantSymbol.Name}: the target component does not support variation because it has the DontSupportPrefabOverridesAttribute", variantSymbol.Locations[0]);
                return null;
            }
            var adapteeComponentType = Roslyn.Extensions.GetComponentType(adapteeType);
            if (adapteeComponentType != ComponentType.Component && adapteeComponentType != ComponentType.Buffer &&
                !Roslyn.Extensions.InheritsFromBase(adapteeType, "UnityEngine.Component"))
            {
                m_Reporter.LogError($"{variantSymbol.Name}: the component type must be IComponentData, IBufferElementData or UnityEngine.Component", variantSymbol.Locations[0]);
                return null;
            }

            // TODO - Write test for parsing this.
            var isTestVariant = false;
            if (variantAttribute.ConstructorArguments.Length == 2)
            {
                // Arg 2 MIGHT be the bool.
                if (variantAttribute.ConstructorArguments[1].Value is bool testVariant)
                {
                    isTestVariant = testVariant;
                }
                // Else assume it's the string name.
            }
            else if (variantAttribute.ConstructorArguments.Length == 3)
            {
                if (variantAttribute.ConstructorArguments[2].Value is bool testVariant)
                {
                    isTestVariant = testVariant;
                }
                else
                {
                    m_Reporter.LogError($"{variantSymbol.Name}: `variantAttribute.ConstructorArguments[2]` is somehow not a bool, but expected it to be `IsTestVariant`.");
                    return null;
                }
            }

            //Validation and member collection step: loop over the field in the variant declarion. Only fields that are also prensent int original component are considered.
            //Any private or missing field are considered an error.
            var declaredMembers = new List<ValueTuple<ISymbol, ITypeSymbol>>(32);
            bool hasErrors = false;
            using (new Profiler.Auto("ValidationAndExtraction"))
            {
                //This check should be part of a RoslynAnalyzer for net code that detect that problem at editing time in an IDE
                //However, we should still do the check here for robustness.
                foreach (var member in variantSymbol.GetMembers().OfType<IFieldSymbol>())
                {
                    var originalMember = adapteeType.GetMembers(member.Name).FirstOrDefault();
                    if(originalMember == null ||
                       (originalMember as IFieldSymbol)?.Type.GetFullTypeName() != member.Type.GetFullTypeName())
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: Cannot find member {member.Name} type: {member.Type.Name} in {adapteeType.Name}", member.Locations[0]);
                        continue;
                    }
                    if (originalMember.DeclaredAccessibility != Accessibility.Public)
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: member {member.Name} type: {member.Type.Name} in {adapteeType.Name} must be public", member.Locations[0]);
                        continue;
                    }
                    declaredMembers.Add((member, member.Type));
                }
                foreach (var prop in variantSymbol.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!CheckIsSerializableProperty(prop))
                        continue;

                    var originalMember = adapteeType.GetMembers(prop.Name).FirstOrDefault();
                    if(originalMember == null ||
                       (originalMember as IPropertySymbol)?.Type.GetFullTypeName() != prop.Type.GetFullTypeName())
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: Cannot find property {prop.Name} type: {prop.Type.Name} in {adapteeType.Name}", prop.Locations[0]);
                        continue;
                    }
                    if (originalMember.DeclaredAccessibility != Accessibility.Public)
                    {
                        hasErrors = true;
                        m_Reporter.LogError($"{variantSymbol.Name}: property {prop.Name} type: {prop.Type.Name} in {adapteeType.Name} must be public", prop.Locations[0]);
                        continue;
                    }
                    declaredMembers.Add((prop, prop.Type));
                }
            }
            //In case of errors, it is safer to just skip
            if (hasErrors)
                return null;

            m_context.CancellationToken.ThrowIfCancellationRequested();
            var fullTypeName = Roslyn.Extensions.GetFullTypeName(adapteeType);
            var hasGhostEnabledBitAttribute = Roslyn.Extensions.GetAttribute(variantSymbol, "Unity.NetCode", "GhostEnabledBitAttribute") != null;
            var adapteeIsEnableableComponent = Roslyn.Extensions.ImplementsInterface(adapteeType, "Unity.Entities.IEnableableComponent");

            // TODO - Tests for `[GhostEnabledBit]`s on variants.
            if (hasGhostEnabledBitAttribute && !adapteeIsEnableableComponent)
            {
                m_Reporter.LogError($"'{fullTypeName}' (a variant) has attribute `[GhostEnabledBit]` (denoting that we intend to replicate the enabled bit on the source type), but the source type (`{variantSymbol.Name}`) is not implementing the `IEnableableComponent` interface! Either remove the attribute from the variant, or implement the interface.");
                return null;
            }

            var typeInfo = new TypeInformation
            {
                Kind = Roslyn.Extensions.GetTypeKind(adapteeType),
                ComponentType = adapteeComponentType,
                TypeFullName = fullTypeName,
                UnderlyingTypeName = String.Empty,
                Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(adapteeType),
                FieldName = string.Empty,
                FieldTypeName = Roslyn.Extensions.GetFieldTypeName(adapteeType),
                Attribute = TypeAttribute.Empty(),
                GhostAttribute = ghostAttribute,
                Location = variantSymbol.Locations[0],
                Symbol = variantSymbol,
                IsTestVariant = isTestVariant,
                ShouldSerializeEnabledBit = adapteeIsEnableableComponent && hasGhostEnabledBitAttribute,
            };

            //Mask out inherited attributes that does not apply. SubType is also never inherited, buffer fields are never interpolated
            if (typeInfo.ComponentType != ComponentType.Component)
                typeInfo.AttributeMask &= ~TypeAttribute.AttributeFlags.Interpolated;

            using (new Profiler.Auto("ParseMembers"))
            {
                foreach (var member in declaredMembers)
                {
                    m_Reporter.LogDebug($"Parsing field {member}");
                    var field = ParseFieldType(member.Item1, member.Item2, typeInfo, string.Empty);
                    if (field != null)
                        typeInfo.GhostFields.Add(field);
                }
            }
            return typeInfo;
        }

        /// <summary>
        /// Build a TypeInformation tree for a field <paramref name="member"/> if fhe field should be serialized.
        /// A member of as struct is serialized if the following conditions are true:
        /// - The member must have public accessibilty.
        /// - The member must be not static
        /// - The member must have either a [GhostField] annotation or a custom ghost override.
        /// - The member type must be one of the supported type: Primitive, Enum, Struct. Class members are considered invalid.
        /// The function is recursive.
        /// </summary>
        /// <returns>A valid TypeInformation instance if the member fulfills all the requirement. Null otherwise</returns>
        public TypeInformation ParseFieldType(ISymbol member, ITypeSymbol memberType, TypeInformation parent, string fieldPath, int level=1, GhostField ghostFieldOverride = null)
        {
            m_context.CancellationToken.ThrowIfCancellationRequested();
            var ghostField = default(GhostField);
            if (m_SerializationMode == SerializationMode.Component)
            {
                if (ghostFieldOverride != null)
                {
                    // Only apply overrides to members which are valid  as ghost fields
                    if (!member.IsStatic && member.DeclaredAccessibility == Accessibility.Public)
                        ghostField = ghostFieldOverride;
                }
                else
                    ghostField = TryGetGhostField(member);
            }
            if(m_SerializationMode != SerializationMode.Commands)
            {
                if (member.IsStatic || (m_RequiresPublicFields && member.DeclaredAccessibility != Accessibility.Public))
                {
                    if(ghostField != null)
                        m_Reporter.LogError($"GhostField present on a non public or non instance field '{parent.TypeFullName}.{member.Name}'! GhostFields must be public, instance fields.");
                    return null;
                }

                //Skip fields who don't have any [GhostField] attribute or the SendData is set to false
                if ((ghostField == null && string.IsNullOrEmpty(fieldPath)))
                {
                    //Buffer need some further validation, and we collect here any missing field
                    if ((parent.ComponentType == ComponentType.Buffer || parent.ComponentType == ComponentType.CommandData))
                    {
                        m_MissingGhostFields.Add($"{parent.TypeFullName}.{member.Name}");
                    }
                    return null;
                }
                if ((ghostField != null && !ghostField.SendData))
                    return null;
            }
            else if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
                return null;

            //Add some validation and skip irrelevant fields too
            var typeKind = Roslyn.Extensions.GetTypeKind(memberType);
            if (typeKind == GenTypeKind.Invalid)
            {
                m_Reporter.LogError($"GhostField annotation present on non serializable field '{parent.TypeFullName}.{member.Name}'.");
                return null;
            }

            if ((typeKind != GenTypeKind.Struct) && (ghostField != null && ghostField.Composite.HasValue && ghostField.Composite.Value))
                m_Reporter.LogError($"GhostField for field '{parent.TypeFullName}.{member.Name}' set Composite=True, but this is invalid on primitive types.");

            var typeInfo = new TypeInformation
            {
                Kind = typeKind,
                TypeFullName = Roslyn.Extensions.GetFullTypeName(memberType),
                Namespace = Roslyn.Extensions.GetFullyQualifiedNamespace(memberType),
                FieldName = member.Name,
                UnderlyingTypeName = Roslyn.Extensions.GetUnderlyingTypeName(memberType),
                DeclaringTypeFullName = Roslyn.Extensions.GetFullTypeName(member.ContainingType),
                FieldTypeName = Roslyn.Extensions.GetFieldTypeName(memberType),
                Attribute = parent.Attribute,
                AttributeMask = parent.AttributeMask,
                Parent = fieldPath,
                Location = member.Locations[0],
                CanBatchPredict = CanBatchPredict(member),
                Symbol = member as ITypeSymbol
            };

            if(typeInfo.FieldName.StartsWith("__COMMAND", StringComparison.Ordinal) ||
               typeInfo.FieldName.StartsWith("__GHOST", StringComparison.Ordinal))
            {
                m_Reporter.LogError($"Invalid field name '{parent.TypeFullName}.{typeInfo.FieldName}'. __GHOST and __COMMAND are reserved prefixes and cannot be used in namespace, type and field names!",
                    member.Locations[0]);
                return null;
            }
            if(typeInfo.FieldTypeName.StartsWith("__COMMAND", StringComparison.Ordinal) ||
               typeInfo.FieldTypeName.StartsWith("__GHOST", StringComparison.Ordinal))
            {
                m_Reporter.LogError($"Invalid typename '{typeInfo.FieldTypeName}' for '{parent.TypeFullName}.{typeInfo.FieldName}'. __GHOST and __COMMAND are reserved prefixes and cannot be used in namespace, type and field names!",
                    member.Locations[0]);
                return null;
            }

            //Always reset the subtype (is not inherited)
            typeInfo.Attribute.subtype = 0;
            //Read the subfield if present
            if (ghostField != null)
            {
                if (ghostField.Quantization >= 0) typeInfo.Attribute.quantization = ghostField.Quantization;
                if (ghostField.Smoothing > 0) typeInfo.Attribute.smoothing = (uint)ghostField.Smoothing;
                if (ghostField.SubType != 0) typeInfo.Attribute.subtype = ghostField.SubType;
                // the inheritance rules say the child attribute has higher priority
                // in particular for the composite, the rule is the follow
                //  child/parent  N/A    False   True
                //    N/A         false   false  true
                //    False       false   false  true
                //    True        true    true   true
                if (ghostField.Composite.HasValue && !typeInfo.Attribute.aggregateChangeMask)
                {
                    typeInfo.Attribute.aggregateChangeMask = ghostField.Composite.Value;
                    if (typeKind != GenTypeKind.Struct && typeInfo.Attribute.aggregateChangeMask)
                    {
                        m_Reporter.LogInfo($"GhostField composite set to true for primitive field '{fieldPath} {parent.TypeFullName}.{member.Name}', which will be ignored. We assume this is fine as the parent having Composite is valid.");
                        typeInfo.Attribute.aggregateChangeMask = false;
                    }
                }

                if (ghostField.MaxSmoothingDistance > 0) typeInfo.Attribute.maxSmoothingDist = ghostField.MaxSmoothingDistance;
            }
            //And then reset based on the mask
            typeInfo.Attribute.smoothing &= (uint)(typeInfo.AttributeMask & TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated);
            typeInfo.Attribute.aggregateChangeMask &= (typeInfo.AttributeMask & TypeAttribute.AttributeFlags.Composite) != 0;

            if((typeInfo.AttributeMask & TypeAttribute.AttributeFlags.Quantized) == 0)
                typeInfo.Attribute.quantization = -1;

            if (typeKind != GenTypeKind.Struct)
                return typeInfo;

            var members = memberType.GetMembers();
            foreach (var f in members.OfType<IFieldSymbol>())
            {
                var path = string.IsNullOrEmpty(fieldPath)
                    ? member.Name
                    : string.Concat(fieldPath, ".", member.Name);

                var field = ParseFieldType(f, f.Type, typeInfo, path,level + 1);
                if (field != null)
                    typeInfo.GhostFields.Add(field);
            }

            //We support field member properties but with some restrictions: Only if they return primitive types
            //- Because we don't have enough control about what kind of property we may get for a given member.
            //  ex: for float3 we don't want for example xyz and other swizzle combination be serialized.
            //- Member like this[int index] or properties that return the same type can cause recursion.
            //Also properties like this[] are not supported either
            foreach (var prop in members.OfType<IPropertySymbol>())
            {
                if (!CheckIsSerializableProperty(prop))
                    continue;
                var path = string.IsNullOrEmpty(fieldPath)
                    ? member.Name
                    : string.Concat(fieldPath, ".", member.Name);

                var field = ParseFieldType(prop, prop.Type, typeInfo, path, level + 1);
                if (field != null)
                    typeInfo.GhostFields.Add(field);
            }
            return typeInfo;
        }

        private bool CheckIsSerializableProperty(IPropertySymbol f)
        {
            string GetErrorReason()
            {
                //This prevent any indexer like accessor
                if (f.IsIndexer)
                    return "Indexer.";
                if (f.GetMethod == null)
                    return "No getter.";
                if (f.GetMethod.DeclaredAccessibility != Accessibility.Public || f.GetMethod.IsStatic)
                    return "Getter is not public.";
                if (f.SetMethod == null)
                    return "No setter.";
                if (f.SetMethod.DeclaredAccessibility != Accessibility.Public || f.GetMethod.IsStatic)
                    return "Setter is not public.";
                //I only support things that return primitive type and that are not compiler generated
                var typeKind = Roslyn.Extensions.GetTypeKind(f.GetMethod.ReturnType);
                if (typeKind == GenTypeKind.Invalid)
                    return "Invalid type kind.";
                if (typeKind != GenTypeKind.Struct)
                    return null;
                // Exception for NetworkTick, which is the only supported struct property.
                if (Roslyn.Extensions.GetFullTypeName(f.GetMethod.ReturnType) == "Unity.NetCode.NetworkTick")
                    return null;
                return "Property structs are not supported.";
            }

            var errorReason = GetErrorReason();
            var isValid = string.IsNullOrEmpty(errorReason);
            if (!isValid)
            {
                var ghostField = TryGetGhostField(f);
                if (ghostField != null && ghostField.SendData)
                {
                    m_Reporter.LogError($"GhostField present on an invalid property {f}: {errorReason}");
                }
            }
            return isValid;
        }

        private bool ShouldDiscardCommandField(ISymbol symbol)
        {
            var attribute = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "DontSerializeForCommandAttribute");
            if (attribute != null)
                return true;
            //Since that can be true only for properties, I should not run this for field type
            if (symbol is IPropertySymbol)
            {
                foreach (var iface in symbol.ContainingType.Interfaces)
                {
                    var member = iface.GetMembers(symbol.Name);
                    if (member == null || member.Length == 0)
                        continue;
                    if(Roslyn.Extensions.GetAttribute(member[0], "Unity.NetCode", "DontSerializeForCommandAttribute") != null)
                        return true;
                }
            }
            return false;
        }


        /// <summary>
        /// Check for the presence of GhostFieldAttribute on the given field <paramref name="fieldSymbol"/>.
        /// </summary>
        /// <returns>
        /// A valid instance of a GhostFieldAttribute if the annotation is present or an override exist. Null otherwise.
        /// </returns>
        private GhostField TryGetGhostField(ISymbol fieldSymbol)
        {
            var ghostField = Roslyn.Extensions.GetAttribute(fieldSymbol, "Unity.NetCode", "GhostFieldAttribute");
            if (ghostField == null)
                ghostField = Roslyn.Extensions.GetAttribute(fieldSymbol, "", "GhostField");
            if (ghostField != null)
            {
                var fieldDescriptor = new GhostField();
                if (ghostField.NamedArguments.Length > 0)
                    foreach (var a in ghostField.NamedArguments)
                    {
                        typeof(GhostField).GetProperty(a.Key)?.SetValue(fieldDescriptor, a.Value.Value);
                    }

                return fieldDescriptor;
            }

            return default;
        }
        private bool CanBatchPredict(ISymbol fieldSymbol)
        {
            return Roslyn.Extensions.GetAttribute(fieldSymbol, "Unity.NetCode", "BatchPredictAttribute") != null;
        }
    }
}
