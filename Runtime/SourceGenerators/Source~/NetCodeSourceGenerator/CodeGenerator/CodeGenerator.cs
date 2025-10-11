using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    //This class must not contains state and must be immutable. All necessary data must come from arguments and context
    internal static class CodeGenerator
    {
        public const string RpcSerializer = "NetCode.RpcCommandSerializer.cs";
        public const string CommandSerializer = "NetCode.CommandDataSerializer.cs";
        public const string ComponentSerializer = "NetCode.GhostComponentSerializer.cs";
        public const string RegistrationSystem = "NetCode.GhostComponentSerializerRegistrationSystem.cs";
        public const string InputSynchronization = "NetCode.InputSynchronization.cs";
        public const string GhostFixedListElement = "NetCode.GhostFixedListElement.cs";
        public const string GhostFixedListContainer = "NetCode.GhostFixedListContainer.cs";
        public const string GhostFixedListCommandHelper = "NetCode.GhostFixedListCommandHelper.cs";
        public const string GhostFixedListSnapshotHelpers = "NetCode.GhostFixedListSnapshotHelpers.cs";

        //Namespace generation can be a little tricky.
        //Given the current generated NS: AssemblyName.Generated
        //I assumed the following rules:
        // 1) if the type has no namespace => nothing to consider, is global NS
        // 2) if the type ns namespace has as common ancestor AssemblyName => use type NS
        // 3) if the type ns namespace doesn't have a common prefix with AssemblyName => use type NS
        // 4) if the type ns namespace and AssemblyName has some common prefix => prepend global::
        internal static string GetValidNamespaceForType(string generatedNs, string ns)
        {
            //if it is 0 is still part of the ancestor
            if(generatedNs.IndexOf(ns, StringComparison.Ordinal) <= 0)
                return ns;

            //need to use global to avoid confusion
            return "global::" + ns;
        }

        public static ReadOnlySpan<byte> Log2DeBruijn => // 32
        [
            00, 09, 01, 10, 13, 21, 02, 29,
            11, 14, 16, 18, 22, 25, 03, 30,
            08, 12, 20, 28, 15, 17, 24, 07,
            19, 27, 23, 06, 26, 05, 04, 31
        ];
        public static int lzcnt(uint value)
        {
            if(value == 0)
                return 32;
            //fill trailing 0
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return 31 ^ Log2DeBruijn[(int)((value * 0x07C4ACDDu) >> 27)];
        }

        /// <summary>
        ///     True if we can generate this EXACT type via a known Template.
        ///     If false, we will check its children, and see if we can generate any of those.
        /// </summary>
        /// <remarks>
        /// A type failing this check will NOT prevent it from being serialized.
        /// I.e. This is ONLY to check whether or not we have a template for this EXACT type.
        /// </remarks>
        static bool TryGetTypeTemplate(TypeInformation typeInfo, Context context, out TypeTemplate template)
        {
            template = default;
            var description = typeInfo.Description;
            if (!context.templateProvider.TypeTemplates.TryGetValue(description, out template))
            {
                bool foundSubType = false;
                TypeDescription? foundDesc = null;
                if (description.Attribute.subtype == 0)
                {
                    // Try to find the closest valid anyway, so we can err on the fact that it doesn't meet all criteria.
                    // NOTE: We should really find ALL templates for a given type, then select the best, where possible.
                    foreach (var kvp in context.templateProvider.TypeTemplates)
                    {
                        if (description.Key == kvp.Key.Key)
                        {
                            // If the `kvp` entry has the exact subtype, but our best match doesn't, prefer the other one.
                            if (template == null || (foundDesc.Value.Attribute.subtype != 0 && kvp.Key.Attribute.subtype == 0))
                            {
                                foundDesc = kvp.Key;
                                template = kvp.Value;
                            }
                        }
                    }
                    if (template == null)
                    {
                        context.diagnostic.LogDebug($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' but no template found.",
                            typeInfo.Location);
                        return false;
                    }
                    context.diagnostic.LogWarning($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}', but no exact template match found. Thus, using best-match similar template {template.TemplatePath} with description {foundDesc}! To remove this warning, specify this exact description in the GhostField attribute of this field, or, alternatively, provide a new ghost field template that matches this exact description.",
                        typeInfo.Location);
                }
                else
                {
                    // Try to find the same subtype manually, ignoring other description settings:
                    foreach (var kvp in context.templateProvider.TypeTemplates)
                    {
                        if (description.Attribute.subtype == kvp.Key.Attribute.subtype)
                        {
                            if (description.Key == kvp.Key.Key)
                            {
                                foundSubType = true;
                                break;
                            }

                            context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' with a subtype, but subType '{description.Attribute.subtype}' is registered to a different type ('{kvp.Key.TypeFullName}'). Thus, ignoring this field. Did you mean to use a different subType?",
                                typeInfo.Location);
                            return false;
                        }
                    }

                    if (!foundSubType)
                    {
                        context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' with subtype:{description.Attribute.subtype}, but this subType has not been registered. Known subTypes are {context.templateProvider.FormatAllKnownSubTypes()}. Please register your SubType Template in the `UserDefinedTemplates` `TypeRegistry` via an `.additionalfile` (see docs).",
                            typeInfo.Location);
                        return false;
                    }
                    context.diagnostic.LogDebug($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' -- found its subtype!",
                        typeInfo.Location);
                    return false;
                }
            }

            if (template.SupportsQuantization && description.Attribute.quantization <= 0)
            {
                const int defaultQuantizationValue = 1000;
                context.diagnostic.LogWarning($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' which matches template '{template.TemplatePath}'. However, this template requires a quantization value to be specified, but it has not been. Using {defaultQuantizationValue} for now. To remove this warning, add a quantization value to the GhostField attribute constructor.",
                    typeInfo.Location);
                description.Attribute.quantization = defaultQuantizationValue;
            }
            else if (!template.SupportsQuantization && description.Attribute.quantization > 0)
            {
                context.diagnostic.LogWarning($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' which matches template '{template.TemplatePath}'. However, this template does not support quantization, but has a quantization value of {description.Attribute.quantization} specified. Thus, the quantization value will be ignored. To remove this warning, either remove the quantization value from the GhostField attribute constructor, or, if the GhostFieldAttribute is inherited, add a Quantization=-1 to this field.",
                    typeInfo.Location);
            }

            // TODO: subtype + composite doesn't work atm, we don't pass the subtype=x info down
            // when processing the nested types, so default variant will be used, and given template in the variant
            // will be ignored. Also you might have a normal template and set the composite=true by mistake, but
            // we can't detect this atm
            if (template.Composite && description.Attribute.subtype > 0)
            {
                context.diagnostic.LogError($"'{context.generatorName}' defines a field '{typeInfo.FieldName}' with GhostField configuration '{description}' using an invalid configuration: Subtyped types cannot also be defined as composite, as it is assumed your Template given is the one in use for the whole type. I.e. If you'd like to implement change-bit composition yourself on this type, modify the template directly (at '{template.TemplatePath}').",
                    typeInfo.Location);
                return false;
            }

            // TODO: Ensure all Log's have typeInfo.Location.
            context.diagnostic.LogDebug($"'{context.generatorName}' found Template for field '{typeInfo.FieldName}' with GhostField configuration '{description}': '{template}'.",
                typeInfo.Location);
            return true;
        }

        public static void GenerateRegistrationSystem(Context context)
        {
            //There is nothing to generate in that case. Skip creating an empty system
            if(context.generatedGhosts.Count == 0 && context.serializationStrategies.Count == 0)
                return;

            using (new Profiler.Auto("GenerateRegistrationSystem"))
            {
                //Generate the ghost registration
                var registrationSystemCodeGen = context.codeGenCache.GetTemplate(CodeGenerator.RegistrationSystem);
                registrationSystemCodeGen = registrationSystemCodeGen.Clone();
                var replacements = new Dictionary<string, string>(16);

                foreach (var t in context.generatedGhosts)
                {
                    replacements["GHOST_NAME"] = t;
                    registrationSystemCodeGen.GenerateFragment("GHOST_COMPONENT_LIST", replacements);
                }

                int selfIndex = 0;
                foreach (var ss in context.serializationStrategies)
                {
                    var typeInfo = ss.TypeInfo;

                    if (typeInfo == null)
                        throw new InvalidOperationException("Must define TypeInfo when using `serializationStrategies.Add`!");

                    if(ss.Hash == "0")
                        context.diagnostic.LogError($"Setting invalid hash on variantType {ss.VariantTypeName} to {ss.Hash}!");

                    var displayName = ss.DisplayName ?? ss.VariantTypeName;
                    displayName = SmartTruncateDisplayNameForFs64B(displayName);

                    var isDefaultSerializer = string.IsNullOrWhiteSpace(ss.VariantTypeName) || ss.VariantTypeName == ss.ComponentTypeName;

                    replacements["VARIANT_TYPE"] = ss.VariantTypeName;
                    replacements["GHOST_COMPONENT_TYPE"] = ss.ComponentTypeName;
                    replacements["GHOST_VARIANT_DISPLAY_NAME"] = displayName;
                    replacements["GHOST_VARIANT_HASH"] = ss.Hash;
                    replacements["SELF_INDEX"] = selfIndex++.ToString();
                    replacements["VARIANT_IS_SERIALIZED"] = ss.IsSerialized ? "1" : "0";
                    replacements["GHOST_IS_DEFAULT_SERIALIZER"] = isDefaultSerializer ? "1" : "0";
                    replacements["GHOST_SEND_CHILD_ENTITY"] = typeInfo.GhostAttribute != null && typeInfo.GhostAttribute.SendDataForChildEntity ? "1" : "0";
                    replacements["TYPE_IS_INPUT_COMPONENT"] = typeInfo.ComponentType == ComponentType.Input ? "1" : "0";
                    replacements["TYPE_IS_INPUT_BUFFER"] = typeInfo.ComponentType == ComponentType.CommandData ? "1" : "0";
                    replacements["TYPE_IS_TEST_VARIANT"] = typeInfo.IsTestVariant ? "1" : "0";
                    replacements["TYPE_HAS_DONT_SUPPORT_PREFAB_OVERRIDES_ATTRIBUTE"] = typeInfo.HasDontSupportPrefabOverridesAttribute ? "1" : "0";
                    replacements["GHOST_PREFAB_TYPE"] = ss.GhostAttribute != null ? $"GhostPrefabType.{ss.GhostAttribute.PrefabType.ToString().Replace(",", "|GhostPrefabType.")}" : "GhostPrefabType.All";

                    if (typeInfo.GhostAttribute != null)
                    {
                        if ((typeInfo.GhostAttribute.PrefabType & GhostPrefabType.Client) == GhostPrefabType.InterpolatedClient)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyInterpolatedClients";
                        else if ((typeInfo.GhostAttribute.PrefabType & GhostPrefabType.Client) == GhostPrefabType.PredictedClient)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyPredictedClients";
                        else if (typeInfo.GhostAttribute.PrefabType == GhostPrefabType.Server)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.DontSend";
                        else if (typeInfo.GhostAttribute.SendTypeOptimization == GhostSendType.OnlyInterpolatedClients)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyInterpolatedClients";
                        else if (typeInfo.GhostAttribute.SendTypeOptimization == GhostSendType.OnlyPredictedClients)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.OnlyPredictedClients";
                        else if (typeInfo.GhostAttribute.SendTypeOptimization == GhostSendType.AllClients)
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.AllClients";
                        else
                            replacements["GHOST_SEND_MASK"] = "GhostSendType.DontSend";
                    }
                    else
                    {
                        replacements["GHOST_SEND_MASK"] = "GhostSendType.AllClients";
                    }

                    registrationSystemCodeGen.GenerateFragment("GHOST_SERIALIZATION_STRATEGY_LIST", replacements);

                    if (typeInfo.ComponentType == ComponentType.Input && !String.IsNullOrEmpty(ss.InputBufferComponentTypeName))
                    {
                        replacements["GHOST_INPUT_BUFFER_COMPONENT_TYPE"] = ss.InputBufferComponentTypeName;

                        registrationSystemCodeGen.GenerateFragment("GHOST_INPUT_COMPONENT_LIST", replacements);
                    }
                }

                replacements.Clear();
                replacements["GHOST_USING"] = context.rootNs;
                registrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);

                replacements.Clear();
                replacements.Add("GHOST_NAMESPACE", context.rootNs);
                registrationSystemCodeGen.GenerateFile("GhostComponentSerializerCollection.cs", replacements, context.batch);
            }
        }

        /// <summary>Long display names like "Some.Very.Long.Namespace.WithAMassiveStructNameAtTheEnd" will be truncated from the back.
        /// E.g. Removing "Some", then "Very" etc. It must fit into the FixedString capacity, otherwise we'll get runtime exceptions during Registration.</summary>
        internal static string SmartTruncateDisplayNameForFs64B(string displayName)
        {
            int indexOf = 0;
            const int fixedString64BytesCapacity = 61;
            while (displayName.Length - indexOf > fixedString64BytesCapacity && indexOf < displayName.Length)
            {
                int newIndexOf = displayName.IndexOf('.', indexOf);
                if (newIndexOf < 0) newIndexOf = displayName.IndexOf(',', indexOf);

                // We may have to just truncate in the middle of a word.
                if (newIndexOf < 0 || newIndexOf >= displayName.Length - 1)
                    indexOf = Math.Max(0, displayName.Length - fixedString64BytesCapacity);
                else indexOf = newIndexOf + 1;
            }
            return displayName.Substring(indexOf, displayName.Length - indexOf);
        }

        public static void GenerateGhost(Context context, TypeInformation typeTree)
        {
            using(new Profiler.Auto("CodeGen"))
            {
                var generator = new ComponentSerializer(context, typeTree);
                context.root = typeTree;
                GenerateType(context, typeTree, generator, null,typeTree.TypeFullName, 0);
                generator.GenerateSerializer(context, typeTree);
            }
        }

        public static void GenerateCommand(Context context, TypeInformation typeInfo, CommandSerializer.Type commandType)
        {
            void BuildGenerator(Context ctx, TypeInformation fieldType, string root, CommandSerializer parentGenerator)
            {
                if (!fieldType.IsValid)
                    return;

                var fieldGen = new CommandSerializer(context, parentGenerator.CommandType, fieldType);
                if (fieldType.Kind == GenTypeKind.FixedSizeArray)
                {
                    var elementType = fieldType.PointeeType;
                    for (int index = 0; index < fieldType.ElementCount; ++index)
                    {
                        elementType.FieldName = $"{fieldType.FieldName}Ref({index})";
                        BuildGenerator(ctx, elementType, root, fieldGen);
                    }
                    fieldGen.AppendTarget(parentGenerator);
                    return;
                }
                if (TryGetTypeTemplate(fieldType, context, out var template))
                {
                    if (!template.SupportCommand)
                        return;

                    fieldGen = new CommandSerializer(context, parentGenerator.CommandType, fieldType, template);
                    if (fieldType.Kind == GenTypeKind.FixedList && parentGenerator.CommandType != Generators.CommandSerializer.Type.Input)
                    {
                        //build the commands read and write for the argument in temporary container.
                        var fixedListArgType = fieldType.PointeeType;
                        //The argument does not have a path. The full path should be retrieved from
                        var fixedListArgGen = new CommandSerializer(context, parentGenerator.CommandType, fixedListArgType);
                        BuildGenerator(ctx, fixedListArgType, String.Empty, fixedListArgGen);
                        fieldGen.GenerateFixedListField(context, fixedListArgGen, fieldType, root);
                        fieldGen.AppendTarget(parentGenerator);
                        return;
                    }
                    if (!template.Composite)
                    {
                        fieldGen.GenerateFields(ctx, root, fieldType);
                        fieldGen.AppendTarget(parentGenerator);
                        return;
                    }
                }

                foreach (var field in fieldType.GhostFields)
                {
                    BuildGenerator(ctx, field, root, fieldGen);
                }

                fieldGen.AppendTarget(parentGenerator);
            }

            using(new Profiler.Auto("CodeGen"))
            {
                context.root = typeInfo;
                var serializeGenerator = new CommandSerializer(context, commandType);
                BuildGenerator(context, typeInfo, "", serializeGenerator);
                serializeGenerator.GenerateSerializer(context, typeInfo);
                if (commandType == Generators.CommandSerializer.Type.Input)
                {
                    // The input component needs to be registered as an empty type variant so that the
                    // ghost component attributes placed on it can be parsed during ghost conversion
                    var inputGhostAttributes = ComponentFactory.TryGetGhostComponent(typeInfo.Symbol);
                    if (inputGhostAttributes == null)
                        inputGhostAttributes = new GhostComponentAttribute();
                    var variantHash = Helpers.ComputeVariantHash(typeInfo.Symbol, typeInfo.Symbol);
                    context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                    {
                        TypeInfo = typeInfo,
                        VariantTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                        ComponentTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                        Hash = variantHash.ToString(),
                        GhostAttribute = inputGhostAttributes
                    });

                    TypeInformation bufferTypeTree;
                    ITypeSymbol bufferSymbol;
                    string bufferName;
                    using (new Profiler.Auto("GenerateInputBufferType"))
                    {
                        if (!GenerateInputBufferType(context, typeInfo, out bufferTypeTree,
                                out bufferSymbol, out bufferName))
                            return;
                    }

                    var tmp = context.serializationStrategies[context.serializationStrategies.Count-1];
                    tmp.InputBufferComponentTypeName = bufferSymbol.ToDisplayString();
                    context.serializationStrategies[context.serializationStrategies.Count-1] = tmp;

                    using (new Profiler.Auto("GenerateInputCommandData"))
                    {
                        serializeGenerator = new CommandSerializer(context, Generators.CommandSerializer.Type.Command);
                        BuildGenerator(context, bufferTypeTree, "", serializeGenerator);
                        serializeGenerator.GenerateSerializer(context, bufferTypeTree);
                    }

                    using (new Profiler.Auto("GenerateInputBufferGhostComponent"))
                    {
                        // Check if the input type has any GhostField attributes, needs to first
                        // lookup the symbol from the candidates list and get the field members from there
                        bool hasGhostFields = false;
                        foreach (var member in typeInfo.Symbol.GetMembers())
                        {
                            foreach (var attribute in member.GetAttributes())
                            {
                                if (attribute.AttributeClass != null &&
                                    attribute.AttributeClass.Name is "GhostFieldAttribute" or "GhostField")
                                    hasGhostFields = true;
                            }
                        }


                        // Parse the generated input buffer as a component so it will be included in snapshot replication.
                        // This only needs to be done if the input struct has ghost fields inside as the generated input
                        // buffer should then be replicated to remote players.
                        if (hasGhostFields) // Ignore GhostEnabledBit here as inputs cannot have them.
                        {
                            GenerateInputBufferGhostComponent(context, typeInfo, bufferName, bufferSymbol);
                        }
                        else
                        {
                            // We must add the serialization strategy even if there are no ghost fields, as empty variants
                            // still save the ghost component attributes.
                            var bufferVariantHash = Helpers.ComputeVariantHash(bufferTypeTree.Symbol, bufferTypeTree.Symbol);
                            context.diagnostic.LogDebug($"Adding SerializationStrategy for input buffer {bufferTypeTree.TypeFullName}, which doesn't have any GhostFields, as we still need to store the GhostComponentAttribute data.");
                            context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                            {
                                TypeInfo = typeInfo,
                                IsSerialized = false,
                                VariantTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                                ComponentTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                                Hash = bufferVariantHash.ToString(),
                                GhostAttribute = inputGhostAttributes
                            });
                        }
                    }
                }
            }
        }

        #region Internal for Code Generation

        private static bool GenerateInputBufferType(Context context, TypeInformation typeTree, out TypeInformation bufferTypeTree, out ITypeSymbol bufferSymbol, out string bufferName)
        {
            // TODO - Code gen should handle throwing an exception for a zero-sized buffer with [GhostEnabledBit].

            // Add the generated code for the command type symbol to the compilation for further processing
            // first lookup from the metadata cache. If it is present there, we are done.
            var bufferType = context.executionContext.Compilation.GetTypeByMetadataName("Unity.NetCode.InputBufferData`1");
            var inputType = typeTree.Symbol;
            if (bufferType == null)
            {
                //Search in current compilation unit. This is slow path but only happen for the NetCode assembly itself (where we don't have any IInputComponentData, so fine).
                var inputBufferType = context.executionContext.Compilation.GetSymbolsWithName("InputBufferData", SymbolFilter.Type).First() as INamedTypeSymbol;
                bufferSymbol = inputBufferType.Construct(inputType);
            }
            else
            {
                bufferSymbol = bufferType.Construct(inputType);
            }
            if (bufferSymbol == null)
            {
                context.diagnostic.LogError($"Failed to construct input buffer symbol InputBufferData<{typeTree.TypeFullName}>!");
                bufferTypeTree = null;
                bufferName = null;
                return false;
            }
            // FieldTypeName includes the namespace, strip that away when generating the buffer type name
            bufferName = $"{typeTree.FieldTypeName}InputBufferData";

            if (typeTree.Namespace.Length != 0 && typeTree.FieldTypeName.Length > typeTree.Namespace.Length)
                bufferName = $"{typeTree.FieldTypeName.Substring(typeTree.Namespace.Length + 1)}InputBufferData";
            // If the type is nested inside another class/type the parent name will be included in the type name separated by an underscore
            bufferName = bufferName.Replace('.', '_');

            var typeBuilder = new TypeInformationBuilder(context.diagnostic, context.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            // Parse input generated code as command data
            context.ResetState();
            context.generatedFilePrefix += bufferName;
            bufferName = context.generatorName + bufferName;
            context.generatorName = bufferName;

            bufferTypeTree = typeBuilder.BuildTypeInformation(bufferSymbol, null);
            if (bufferTypeTree == null)
            {
                context.diagnostic.LogError($"Failed to generate type information for symbol ${bufferSymbol.ToDisplayString()}!");
                return false;
            }
            context.types.Add(bufferTypeTree);
            context.diagnostic.LogDebug($"Generating input buffer command data for ${bufferTypeTree.TypeFullName}!");
            return true;
        }

        private static void GenerateInputBufferGhostComponent(Context context, TypeInformation inputTypeTree, string bufferName, ITypeSymbol bufferSymbol)
        {
            // Add to generatedType list so it is included in the serializer registration system
            context.generatedGhosts.Add($"global::{context.generatedNs}.{bufferName}");

            var ghostFieldOverride = new GhostField();
            // Type information needs to be rebuilt and this time interpreting the type as a component instead of command
            var typeBuilder = new TypeInformationBuilder(context.diagnostic, context.executionContext, TypeInformationBuilder.SerializationMode.Component);
            context.ResetState();
            var bufferTypeTree = typeBuilder.BuildTypeInformation(bufferSymbol, null, ghostFieldOverride);
            if (bufferTypeTree == null)
            {
                context.diagnostic.LogError($"Failed to generate type information for symbol ${bufferSymbol.ToDisplayString()}!");
                return;
            }
            // Set ghost component attribute from values set on the input component source, or defaults
            // if not present, except for the OwnerSendType which can only be SendToNonOwner since it's
            // a dynamic buffer
            var inputGhostAttributes = ComponentFactory.TryGetGhostComponent(inputTypeTree.Symbol);
            if (inputGhostAttributes != null)
            {
                bufferTypeTree.GhostAttribute = new GhostComponentAttribute
                {
                    PrefabType = inputGhostAttributes.PrefabType,
                    SendDataForChildEntity = inputGhostAttributes.SendDataForChildEntity,
                    SendTypeOptimization = inputGhostAttributes.SendTypeOptimization,
                    OwnerSendType = SendToOwnerType.SendToNonOwner
                };
            }
            else
                bufferTypeTree.GhostAttribute = new GhostComponentAttribute { OwnerSendType = SendToOwnerType.SendToNonOwner };

            var variantHash = Helpers.ComputeVariantHash(bufferTypeTree.Symbol, bufferTypeTree.Symbol);
            context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
            {
                TypeInfo = bufferTypeTree,
                VariantTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                ComponentTypeName = bufferTypeTree.TypeFullName.Replace('+', '.'),
                Hash = variantHash.ToString(),
                GhostAttribute = bufferTypeTree.GhostAttribute,
                IsSerialized = true,
            });

            context.types.Add(bufferTypeTree);
            context.diagnostic.LogDebug($"Generating ghost for input buffer {bufferTypeTree.TypeFullName}!");
            GenerateGhost(context, bufferTypeTree);
        }

        private static void GenerateType(Context context, TypeInformation type,
            ComponentSerializer parentContainer, string rootPath, string fullFieldName, int fieldIndex)
        {
            context.executionContext.CancellationToken.ThrowIfCancellationRequested();
            if (TryGetTypeTemplate(type, context, out var template))
            {
                if (type.Kind == GenTypeKind.FixedList)
                {
                    GenerateFixedListField(context, type, parentContainer, template, fieldIndex, rootPath);
                    context.curChangeMaskBits += 2;
                    context.changeMaskBitCount += 2;
                }
                else if (template.Composite) // TODO: Remove this special flow and prefer `context.forceComposite`.
                {
                    if(!GenerateCompositeField(context, type, parentContainer, template, rootPath))
                        return;
                }
                else
                {
                    var generator = new ComponentSerializer(context, type, template);
                    generator.GenerateFields(context, rootPath);
                    //TODO: we don't support yet multibits templates. FixedList is an exception ot this rule and handle things slightly differently
                    generator.GenerateMasks(context, 1, type.Attribute.aggregateChangeMask, fieldIndex);
                    generator.AppendTarget(parentContainer);
                }

                if (!parentContainer.TypeInformation.Attribute.aggregateChangeMask && !context.forceComposite)
                {
                    //We need to increment both the total current and total changemask bit counter if the
                    //parent class does not aggregate field.
                    parentContainer.m_TargetGenerator.AppendFragment("GHOST_AGGREGATE_WRITE", parentContainer.m_TargetGenerator, "GHOST_WRITE_COMBINED");
                    parentContainer.m_TargetGenerator.Fragments["__GHOST_AGGREGATE_WRITE__"].Content = "";
                    ++context.curChangeMaskBits;
                    ++context.changeMaskBitCount;
                }
                return;
            }
            if (type.Kind == GenTypeKind.FixedSizeArray)
            {
                var elementType = type.PointeeType;
                for (var index = 0; index < type.ElementCount; index++)
                {
                    //We need to differentiate the accessor path in this case. For fixed buffer we are using a simplified logic to serialized
                    //element X directly, given that the argument must be necessarily a primitive.
                    //When C#11 will be avaiable and finally we can have fixed buffer for structs we may need this more similar to the fixed list approach (so extra helper class)
                    elementType.FieldName = $"{type.FieldName}Ref({index})";
                    elementType.SnapshotFieldName = $"{type.FieldName}_{index}";
                    GenerateType(context, elementType, parentContainer, rootPath,$"{elementType.ContainingTypeFullName}.{elementType.FieldName}", index);
                }
            }
            else
            {
                // If it's a primitive type and we still have not found a template to use, we can't go any deeper and it's an error
                var isErrorBecausePrimitive = type.Kind == GenTypeKind.Primitive;
                var isErrorBecauseMustFindSubType = type.Description.Attribute.subtype != 0;
                if (isErrorBecausePrimitive || isErrorBecauseMustFindSubType)
                {
                    context.diagnostic.LogError($"Inside type '{context.generatorName}', we could not find the exact template for field '{type.FieldName}' with configuration '{type.Description}', which means that netcode cannot serialize this type (with this configuration), as it does not know how. " +
                                                $"To rectify, either a) define your own template for this type (and configuration), b) resolve any other code-gen errors, or c) modify your GhostField(...) configuration (Quantization, SubType, SmoothingAction etc) to use a known, already existing template. Known templates are {context.templateProvider.FormatAllKnownTypes()}. All known subTypes are {context.templateProvider.FormatAllKnownSubTypes()}!", type.Location);
                    return;
                }
                if (type.GhostFields.Count == 0 && !type.ShouldSerializeEnabledBit)
                {
                    context.diagnostic.LogError($"Couldn't find the TypeDescriptor for GhostField '{context.generatorName}.{type.FieldName}' the type {type.Description} when processing {fullFieldName}! Types must have either valid [GhostField] attributes, or a [GhostEnabledBit] (on an IEnableableComponent).", type.Location);
                    return;
                }

                //Make a temporary container that is used to copy the current generated code.
                var temp = new ComponentSerializer(context, type);
                for (var index = 0; index < type.GhostFields.Count; index++)
                {
                    var field = type.GhostFields[index];
                    GenerateType(context, field, temp, rootPath,$"{field.ContainingTypeFullName}.{field.FieldName}", index);
                }
                temp.AppendTarget(parentContainer);
            }
            //increment the mask bits if the current aggregation scope is completed.
            if (type.Attribute.aggregateChangeMask && !parentContainer.TypeInformation.Attribute.aggregateChangeMask && !context.forceComposite)
            {
                parentContainer.m_TargetGenerator.AppendFragment("GHOST_AGGREGATE_WRITE", parentContainer.m_TargetGenerator, "GHOST_WRITE_COMBINED");
                parentContainer.m_TargetGenerator.Fragments["__GHOST_AGGREGATE_WRITE__"].Content = "";
                ++context.curChangeMaskBits;
                ++context.changeMaskBitCount;
            }
        }

        //how many bits we have for the fixedlist change mask? There at least two way to do this.
        //The most obvious is to use 1 bit for the length and then 1 bit for each element field. However it can easily
        //make explode the number of necessary bits for nothing.
        //The common use case we are trying to optimize for is that users will usually:
        //- add/remove element to the buffer
        //- they rarely (if ever change) only a single field
        //- eventually the element get either removed or changed completely (rarely partially).
        //As such we are using:
        // - 1 bit for each element (all field aggregated)
        // - 1 bit for the length

        //TODO: even with this, while we are mostly (actually more efficient) on par with buffer reading/writing performance there is too much shifting in many cases.
        // How to improve:
        // - limit the allowed max capacity to 64 element (or 128). This is a fine limit in my opinion and simplify the logic.
        // - read in one single step up to the Capacity bits worth of mask in one shot for the fixedlist
        //
        // As a generic principle, by capping the number of allowed field in a component (i.e 128 or 64) it is possible to simplify our serialization loop even further
        // at virtuall not flexibility cost (128 field seems to me a very high and reasonable limit)
        // Also, resettin all masks all to 0 all the time (because it make sense in the end) will remove a lot of shifting by in general (done lots more time than the reset of the mask)
        private static void GenerateFixedListField(Context context, TypeInformation fixedListType, ComponentSerializer parentContainer,
            TypeTemplate template, int fieldIndex, string rootPath)
        {
            //First: generate the argument type as it was the root  This is used to construct
            //a temporary (for the assembly) struct that hold the field data.
            var fixedListFieldType = fixedListType.PointeeType;
            var argumentContainer = new ComponentSerializer(context, fixedListFieldType);

            context.PushState();
            // CAVEAT 1: FixedList's use a Composite change-mask (i.e. 1 bit per list element), so they don't write to the changeMask.
            // CAVEAT 2: AND FixedList's write to their own change-mask (instead of the main one).
            // Thus, because they're writing a composite mask (which is reset to 0), we never want to generate the `changeMaskFragZero` field.
            context.changeMaskBitCount = 0;
            context.curChangeMaskBits = 0;
            context.forceComposite = true;
            fixedListFieldType.Attribute.aggregateChangeMask = true;

            //We first generate the argument type in its own temporary template container.
            //That allow us to know how many bits are needed to serialize the element.
            GenerateType(context, fixedListFieldType,  argumentContainer, null, fixedListFieldType.TypeFullName,0);
            context.PopState();

            //Generate the serialization helper (in all cases, also for primitive types)
            var fixedListElementHelperGen = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListSnapshotHelpers).Clone();
            //Generate a unique fixed list generic argument name based on the type description hash, the type fullname, and fieldName.
            //Why using type description hash? Because the struct depend on the parameter applied to the field (i.e quantization).
            //Ideally, we want this to be generated only once per struct and ghost field option combinations.
            //Unfortunately, because we do code-generation on per assembly basis, we get one per FixedList field!

            //One possible solution for this is to use the Metadata and generate instead of code some serialization schema,
            //then and employ a generic serialization mechanism that does not require any code-generation at all.
            //The big question in that sense is then to understand how good or bad it is the bursted generated code for such serializer.
            //But from maintainability perspective and handling, it will be MUCH easier to add custom types and make checks consistently across-assemblies.
            //The struct name is like MyType_FixedListFieldName_ArgumentTypeName
            //TODO: optimize this be unique per type, not per field and type. Too much code bloat and duplication
            string elementHelperPrefix = $"_{argumentContainer.TypeInformation.Description.GetHashCode():x}_{argumentContainer.TypeInformation.TypeFullName}".Replace('+','.').Replace('.', '_');
            string elementTypeName;
            //if the type is struct we need an extra struct that holds the element in snapshot format
            //we also need to generate a ghost serializer for that struct that provide:
            // - Copy to/from snapshot
            // - Serialize
            // - Deserialize
            // - Calculate the change mask
            // otherwise the generic argument itself can be used
            if (argumentContainer.TypeInformation.Kind == GenTypeKind.Struct)
            {
                elementTypeName = elementHelperPrefix;
                //Add the ghost fields to the fixed list element generator fragment
                var fixedListElementGen = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListElement).Clone();
                fixedListElementGen.Replacements["GHOST_FIELD_TYPE"] = elementTypeName;
                fixedListElementGen.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
                argumentContainer.m_TargetGenerator.AppendFragment("GHOST_FIELD", fixedListElementGen);
                fixedListElementHelperGen.Fragments["__GHOST_FIXEDLIST_ELEMENT__"].Content = fixedListElementGen.GenerateContent(fixedListElementGen.Replacements);
            }
            else
            {
                //this depends upon quantization or user template in general. So we need to generate directly the GHOST FIELD fragment
                //and extract the type information from there. We do so by parsing the generated replacement to find the type information
                elementTypeName = FindGhostFieldType();
            }

            fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_ELEMENT_SERIALIZER"] = $"{elementHelperPrefix}_Serializer";
            fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_SERIALIZER"] = $"{elementHelperPrefix}_FixedList_Serializer";
            fixedListElementHelperGen.Replacements["GHOST_NAME"] = context.root.TypeFullName;
            fixedListElementHelperGen.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
            fixedListElementHelperGen.Replacements["GHOST_FIELD_TYPE"] = elementTypeName;
            fixedListElementHelperGen.Replacements["GHOST_COMPONENT_TYPE"] = argumentContainer.TypeInformation.FieldTypeName;
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_COPY_TO_SNAPSHOT", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_COPY_FROM_SNAPSHOT", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_CALCULATE_CHANGE_MASK", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_READ", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_WRITE", fixedListElementHelperGen);
            argumentContainer.m_TargetGenerator.AppendFragment("GHOST_AGGREGATE_WRITE", fixedListElementHelperGen);

            if (!context.generatedTypes.Contains(elementHelperPrefix))
            {
                fixedListElementHelperGen.GenerateFile(elementHelperPrefix + "_GhostElement.cs",
                    fixedListElementHelperGen.Replacements, context.batch);
                context.generatedTypes.Add(elementHelperPrefix);
            }
            //Generate the fixedlist snapshot struct that represent the snapshot format for the list. Something like:
            //struct ParentPath_FieldName {
            // GenArgSnapshot Element0;
            // GenArgSnapshot Element1;
            // GenArgSnapshot Element2;
            // }
            //TODO: this need a way to share the same struct in case multiple component use the same field type (avoid code bloat)
            var fixedListSnapshotField = context.codeGenCache.GetTemplate(CodeGenerator.GhostFixedListContainer).Clone();
            //Generate all the elements first using the generated struct or the argument type name
            fixedListSnapshotField.Replacements["GHOST_ELEMENT_TYPENAME"] = elementTypeName;
            for (int i = 0; i < fixedListType.ElementCount; ++i)
            {
                fixedListSnapshotField.Replacements["GHOST_ELEMENT_FIELD_NAME"] = $"Element{i}";
                fixedListSnapshotField.GenerateFragment("GHOST_FIXEDLIST_ELEMENTS", fixedListSnapshotField.Replacements);
            }
            //TODO: this can also be shared per "type"
            var fixedListStructName = $"_{argumentContainer.TypeInformation.Description.GetHashCode():x}_{fixedListType.ContainingTypeFullName}.{fixedListType.FieldName}".Replace('+','_').Replace('.','_');
            fixedListSnapshotField.Replacements["GHOST_NAME"] = context.root.TypeFullName;
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_ELEMENT_SERIALIZER"] = fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_ELEMENT_SERIALIZER"];
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_SERIALIZER"] = fixedListElementHelperGen.Replacements["GHOST_FIXEDLIST_SERIALIZER"];
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_NAME"] = fixedListStructName;
            fixedListSnapshotField.Replacements["GHOST_FIXEDLIST_CAPACITY"] = fixedListType.ElementCount.ToString();
            fixedListSnapshotField.Replacements["GHOST_NAMESPACE"] = context.generatedNs;
            fixedListSnapshotField.Replacements["GHOST_COMPONENT_TYPE"] = fixedListType.FieldTypeName;
            fixedListSnapshotField.GenerateFile(fixedListStructName + "_GhostData.cs", fixedListSnapshotField.Replacements, context.batch);

            //Now that we have the helpers ready we can generate the remaining fixed list regions normally
            //TODO: we don't really fully support yet multibits templates. FixedList is an exception ot this rule and handle things slightly differently
            //The fixed list is a multi-field and multi bits template.
            // 1 bit for the length
            // 1 bit for the content
            // in the snapshot: 1 bit for each element, stored in the field data not inside the change mask to simplify the logic a bit.
            //TODO: Fixed list never aggregate, thus the "aggregation" run stop before and resume after this field.
            var fixedListGenerator = new ComponentSerializer(context, fixedListType, template);
            fixedListGenerator.GenerateFields(context, rootPath, replacements: fixedListSnapshotField.Replacements);
            fixedListGenerator.GenerateMasks(context, 2, false, fieldIndex);
            fixedListGenerator.AppendTarget(parentContainer);

            string FindGhostFieldType()
            {
                var argField = argumentContainer.m_TargetGenerator.GetFragmentContent("GHOST_FIELD");
                var args = argField.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "public", StringComparison.Ordinal))
                    {
                        if (i + 1 < args.Length)
                        {
                            return args[i + 1];
                        }
                        break;
                    }
                }
                context.diagnostic.LogWarning($"The __GHOST_FIELD__ region for template {template?.TemplatePath ?? "null"} used for primitive type {fixedListType.TypeFullName} does not comply with the `public {{c# type}} __GHOST_FIELD_NAME__` format. Please ensure that the region is in this format!");
                return argumentContainer.TypeInformation.FieldTypeName;
            }
        }

        private static bool GenerateCompositeField(Context context, TypeInformation type, ComponentSerializer parentContainer,
            TypeTemplate template, string rootPath)
        {
            var compositeGenerator = new ComponentSerializer(context, type, template);
            // Find and apply the composite overrides, then skip those fragments when processing the composite fields
            var overrides = compositeGenerator.GenerateCompositeOverrides(context, rootPath);
            if (overrides != null)
                compositeGenerator.AppendTarget(parentContainer);
            var fieldIt = 0;
            //Verify the assumptions: all generator must be primitive types
            if (compositeGenerator.TypeInformation.GhostFields.Count > 0)
            {
                var areAllPrimitive = type.GhostFields.TrueForAll(f => f.Kind == GenTypeKind.Primitive);
                var field = type.GhostFields[0];
                if (!areAllPrimitive)
                {
                    context.diagnostic.LogError(
                        $"Can't generate a composite serializer for {type.Description}. The struct fields must be all primitive types but are {field.TypeFullName}!",
                        type.Location);
                    return false;
                }
                var areAllSameType = type.GhostFields.TrueForAll(f => f.FieldTypeName == field.FieldTypeName);
                if (!areAllSameType)
                {
                    context.diagnostic.LogError($"Can't generate a composite serializer for {type.Description}. The struct fields must be all of the same type! " +
                                                $"Check the template assignment in your UserDefinedTemplate class implementation. " +
                                                $"Composite templates should be used only for generating types that has all the same fields (i.e float3)", type.Location);
                    return false;
                }
            }
            foreach (var childGhostField in compositeGenerator.TypeInformation.GhostFields)
            {
                //Composite templates forcibly aggregate the change masks. You can't override this behaviour with the
                //GhostField.Composite flags (at least the way it designed today).
                //Given also how they currently work, only basic fields types in practice can be supported. This is very limiting.
                //So, for now we restrict ourself to support only template here that generate always 1 bit change mask.
                //TODO: For these reasons, removing the concept of composite (from the template) make sense in my opinion.
                if (!TryGetTypeTemplate(type, context, out var fieldTemplate))
                {
                    context.diagnostic.LogError(
                        $"Inside type '{context.generatorName}', we could not find the exact template for field '{type.FieldName}' with configuration '{type.Description}', which means that netcode cannot serialize this type (with this configuration), as it does not know how. " +
                        $"To rectify, either a) define your own template for this type (and configuration), b) resolve any other code-gen errors, or c) modify your GhostField(...) configuration (Quantization, SubType, SmoothingAction etc) to use a known, already existing template. Known templates are {context.templateProvider.FormatAllKnownTypes()}. All known subTypes are {context.templateProvider.FormatAllKnownSubTypes()}!",
                        type.Location);
                    context.diagnostic.LogError(
                        $"Unable to generate serializer for GhostField '{type.TypeFullName}.{childGhostField.TypeFullName}.{childGhostField.TypeFullName}' (description: {childGhostField.Description}) while building the composite!",
                        type.Location);
                }
                var g = new ComponentSerializer(context, childGhostField, fieldTemplate);
                g.GenerateFields(context, rootPath, overrides);
                g.GenerateMasks(context, 1, true, fieldIt);
                g.AppendTarget(parentContainer);
                ++fieldIt;
            }
            return true;
        }

        #endregion

        public struct GeneratedFile
        {
            public string GeneratedFileName;
            public string Code;
        }

        public class CodeGenCache
        {
            private Dictionary<string, GhostCodeGen> cache;
            private TemplateRegistry provider;
            private Context context;

            public CodeGenCache(TemplateRegistry templateRegistry, Context context)
            {
                this.provider = templateRegistry;
                this.context = context;
                this.cache = new Dictionary<string, GhostCodeGen>(128);
            }

            public GhostCodeGen GetTemplate(string templatePath)
            {
                if (!cache.TryGetValue(templatePath, out var codeGen))
                {
                    var templateData = provider.GetTemplateData(templatePath);
                    codeGen = new GhostCodeGen(templatePath, templateData, context);
                    cache.Add(templatePath, codeGen);
                }
                return codeGen;
            }

            public GhostCodeGen GetTemplateWithOverride(string templatePath, string templateOverride)
            {
                var key = templatePath + templateOverride;
                if (!cache.TryGetValue(key, out var codeGen))
                {
                    var templateData = provider.GetTemplateData(templatePath);
                    codeGen = new GhostCodeGen(templatePath, templateData, context);
                    if (!string.IsNullOrEmpty(templateOverride))
                    {
                        var overrideTemplateData = provider.GetTemplateData(templateOverride);
                        var codeGenOverride = new GhostCodeGen(templateOverride, overrideTemplateData, context);
                        foreach (var f in codeGenOverride.Fragments)
                            codeGen.Fragments[f.Key].Template = f.Value.Template;
                    }
                    cache.Add(key, codeGen);
                }
                return codeGen;
            }
        }

        //Contains all the state for the current serialization. Generators must be stateless and immutable, only the
        //Context should contains mutable data
        public class Context
        {
            internal GeneratorExecutionContext executionContext;
            public string rootNs;
            public string generatedNs;
            public readonly TemplateRegistry templateProvider;
            public readonly IDiagnosticReporter diagnostic;
            public readonly CodeGenCache codeGenCache;
            public readonly List<GeneratedFile> batch;
            public readonly List<TypeInformation> types;
            public readonly HashSet<string> imports;
            public readonly HashSet<string> generatedGhosts;
            public readonly HashSet<string> generatedTypes;
            public readonly HashSet<string> generatedSerializers;
            public struct SerializationStrategyCodeGen
            {
                public TypeInformation TypeInfo;
                public string DisplayName;
                public string ComponentTypeName;
                public string VariantTypeName;
                public string Hash;
                public bool IsSerialized;
                public GhostComponentAttribute GhostAttribute;
                public string InputBufferComponentTypeName;

            }
            public readonly List<SerializationStrategyCodeGen> serializationStrategies;

            //Follow the Rolsyn convention for inner classes (so Namespace.ClassName[+DeclaringClass]+Class
            public string variantTypeFullName;
            public ulong variantHash;
            public string generatorName;
            public string generatedFilePrefix;
            //Total number of changeMaskBits bits
            public int changeMaskBitCount;
            //The current used mask bits
            public int curChangeMaskBits;
            public bool forceComposite;
            public ulong ghostFieldHash;
            public TypeInformation root;

            struct FieldState
            {
                public int changeMaskBitCount;
                public int curChangeMaskBits;
                public bool forceComposite;
                public string generatorName;
                public string generatedFilePrefix;
                public string generatedNs;
            }

            private Stack<FieldState> m_FieldStateStack = new Stack<FieldState>();

            public void PushState()
            {
                m_FieldStateStack.Push(new FieldState
                {
                    changeMaskBitCount = changeMaskBitCount,
                    curChangeMaskBits =  curChangeMaskBits,
                    forceComposite = forceComposite,
                    generatorName =  generatorName,
                    generatedFilePrefix =  generatedFilePrefix,
                    generatedNs =  generatedNs,
                });
            }
            public void PopState()
            {
                var state = m_FieldStateStack.Pop();
                changeMaskBitCount = state.changeMaskBitCount;
                curChangeMaskBits =  state.curChangeMaskBits;
                forceComposite = state.forceComposite;
                generatorName =  state.generatorName;
                generatedFilePrefix =  state.generatedFilePrefix;
                generatedNs =  state.generatedNs;
            }
            public void ResetState()
            {
                m_FieldStateStack.Clear();
                changeMaskBitCount = 0;
                curChangeMaskBits = 0;
                forceComposite = false;
                ghostFieldHash = 0;
                variantTypeFullName = null;
                variantHash = 0;
                imports.Clear();
                imports.Add("Unity.Entities");
                imports.Add("Unity.Collections");
                imports.Add("Unity.NetCode");
                imports.Add("Unity.Transforms");
                imports.Add("Unity.Mathematics");
            }

            string GenerateNamespaceFromAssemblyName(string assemblyName)
            {
                return $"{Regex.Replace(assemblyName, @"[^\w\.]", "_", RegexOptions.Singleline)}.Generated";
            }

            public Context(TemplateRegistry templateRegistry,
                IDiagnosticReporter reporter, GeneratorExecutionContext context, string assemblyName)
            {
                executionContext = context;
                types = new List<TypeInformation>(16);
                serializationStrategies = new List<SerializationStrategyCodeGen>(32);
                templateProvider = templateRegistry;
                codeGenCache = new CodeGenCache(templateRegistry, this);
                batch = new List<GeneratedFile>(256);
                imports = new HashSet<string>();
                generatedGhosts = new HashSet<string>();
                generatedTypes = new HashSet<string>();
                generatedSerializers = new HashSet<string>();
                diagnostic = reporter;
                rootNs = GenerateNamespaceFromAssemblyName(assemblyName);
                generatedNs = null;
                ResetState();
            }
        }
    }
}
