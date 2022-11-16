using System;
using System.Collections.Generic;
using System.Linq;
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
        public const string MetaDataRegistrationSystem = "NetCode.GhostComponentMetaDataRegistrationSystem.cs";
        public const string InputSynchronization = "NetCode.InputSynchronization.cs";

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

        static bool CanGenerateType(TypeInformation typeInfo, Context context)
        {
            // TODO: Are the subtypes registered somewhere for easier lookup?
            bool foundSubType = false;
            var description = typeInfo.Description;
            foreach (var myType in context.registry.Templates)
            {
                if (description.Attribute.subtype == myType.Key.Attribute.subtype)
                {
                    foundSubType = true;
                    break;
                }
            }
            if (!foundSubType)
            {
                context.diagnostic.LogError($"Did not find {description.TypeFullName} with subtype {description.Attribute.subtype}. It has not been registered.",
                    typeInfo.Location);
                return false;
            }

            if (!context.registry.Templates.TryGetValue(description, out var template))
                return false;

            if (template.SupportsQuantization && description.Attribute.quantization < 0)
            {
                context.diagnostic.LogError($"{description.TypeFullName} is of type {description.TypeFullName} which requires quantization factor to be specified - ignoring field",
                    typeInfo.Location);
                return false;
            }

            if (!template.SupportsQuantization && description.Attribute.quantization > 0)
            {
                context.diagnostic.LogError($"{description.TypeFullName} is of type {description.TypeFullName} which does not support quantization - ignoring field",
                    typeInfo.Location);
                return false;
            }

            return true;
        }

        public static void GenerateRegistrationSystem(Context context)
        {
            //There is nothing to generate in that case. Skip creating an empty system
            if(context.generatedTypes.Count == 0 && context.serializationStrategies.Count == 0)
                return;

            using (new Profiler.Auto("GenerateRegistrationSystem"))
            {
                //Generate the ghost registration
                var registrationSystemCodeGen = context.codeGenCache.GetTemplate(CodeGenerator.RegistrationSystem);
                registrationSystemCodeGen = registrationSystemCodeGen.Clone();
                var replacements = new Dictionary<string, string>(16);

                foreach (var t in context.generatedTypes)
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
                    displayName = SmartTruncateDisplayName(displayName);

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
                    replacements["TYPE_HAS_SUPPORTS_PREFAB_OVERRIDES_ATTRIBUTE"] = typeInfo.HasSupportsPrefabOverridesAttribute ? "1" : "0";
                    replacements["GHOST_PREFAB_TYPE"] = ss.GhostAttribute != null ? $"GhostPrefabType.{ss.GhostAttribute.PrefabType.ToString()}" : "GhostPrefabType.All";

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
                            replacements["GHOST_SEND_MASK"] = "GhostComponentSerializer.SendMask.DontSend";
                    }
                    else
                    {
                        replacements["GHOST_SEND_MASK"] = "GhostSendType.AllClients";
                    }

                    registrationSystemCodeGen.GenerateFragment("GHOST_SERIALIZATION_STRATEGY_LIST", replacements);
                }

                replacements.Clear();
                replacements["GHOST_USING"] = context.generatedNs;
                registrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);

                replacements.Clear();
                replacements.Add("GHOST_NAMESPACE", context.generatedNs);
                registrationSystemCodeGen.GenerateFile("GhostComponentSerializerCollection.cs", string.Empty, replacements, context.batch);
            }
        }

        /// <summary>Long display names like "Some.Very.Long.Namespace.WithAMassiveStructNameAtTheEnd" will be truncated from the back.
        /// E.g. Removing "Some", then "Very" etc. It must fit into the FixedString capacity, otherwise we'll get runtime exceptions during Registration.</summary>
        static string SmartTruncateDisplayName(string displayName)
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
                var generator = InternalGenerateType(context, typeTree, typeTree.TypeFullName);
                generator.GenerateMasks(context);

                var serializeGenerator = new ComponentSerializer(context);
                generator.AppendTarget(serializeGenerator);
                serializeGenerator.GenerateSerializer(context, typeTree);
            }
        }

        public static void GenerateCommand(Context context, TypeInformation typeInfo, CommandSerializer.Type commandType)
        {
            void BuildGenerator(Context ctx, TypeInformation typeInfo, CommandSerializer parentGenerator)
            {
                if (!typeInfo.IsValid)
                    return;

                var description = typeInfo.Description;
                var fieldGen = new CommandSerializer(context, parentGenerator.CommandType, typeInfo);
                if (context.registry.Templates.TryGetValue(description, out var template))
                {
                    if (!template.SupportCommand)
                        return;

                    fieldGen = new CommandSerializer(context, parentGenerator.CommandType, typeInfo,GetGeneratorTemplate(context, typeInfo));
                    if (!template.Composite)
                    {
                        fieldGen.GenerateFields(ctx, typeInfo.Parent);
                        fieldGen.AppendTarget(parentGenerator);
                        return;
                    }
                }
                foreach (var field in typeInfo.GhostFields)
                    BuildGenerator(ctx, field, fieldGen);
                fieldGen.AppendTarget(parentGenerator);
            }

            using(new Profiler.Auto("CodeGen"))
            {
                var serializeGenerator = new CommandSerializer(context, commandType);
                BuildGenerator(context, typeInfo, serializeGenerator);
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
                        VariantTypeName = typeInfo.TypeFullName.Replace("+", "."),
                        ComponentTypeName = typeInfo.TypeFullName.Replace("+", "."),
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

                    using (new Profiler.Auto("GenerateInputCommandData"))
                    {
                        serializeGenerator = new CommandSerializer(context, Generators.CommandSerializer.Type.Command);
                        BuildGenerator(context, bufferTypeTree, serializeGenerator);
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
                            context.diagnostic.LogInfo($"Adding SerializationStrategy for input buffer {bufferTypeTree.TypeFullName}, which doesn't have any GhostFields, as we still need to store the GhostComponentAttribute data.");
                            context.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                            {
                                TypeInfo = typeInfo,
                                IsSerialized = false,
                                VariantTypeName = bufferTypeTree.TypeFullName.Replace("+", "."),
                                ComponentTypeName = bufferTypeTree.TypeFullName.Replace("+", "."),
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

            // Add the generated code for the command type to the compilation syntax tree and
            // fetch its symbol for further processing
            var nameAndSource = context.batch[context.batch.Count - 1];
            var syntaxTree = CSharpSyntaxTree.ParseText(nameAndSource.Code,
                options: context.executionContext.ParseOptions as CSharpParseOptions);
            var newCompilation = context.executionContext.Compilation.AddSyntaxTrees(syntaxTree);
            // FieldTypeName includes the namespace, strip that away when generating the buffer type name
            bufferName = $"{typeTree.FieldTypeName}InputBufferData";
            if (typeTree.Namespace.Length != 0 && typeTree.FieldTypeName.Length > typeTree.Namespace.Length)
                bufferName = $"{typeTree.FieldTypeName.Substring(typeTree.Namespace.Length + 1)}InputBufferData";
            bufferSymbol = newCompilation.GetSymbolsWithName(bufferName).FirstOrDefault() as INamedTypeSymbol;
            if (bufferSymbol == null)
            {
                context.diagnostic.LogError($"Failed to fetch input buffer symbol as ${bufferName}");
                bufferTypeTree = null;
                return false;
            }

            var typeBuilder = new TypeInformationBuilder(context.diagnostic, context.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            // Parse input generated code as command data
            context.ResetState();
            context.generatorName = Roslyn.Extensions.GetTypeNameWithDeclaringTypename(bufferSymbol);
            bufferTypeTree = typeBuilder.BuildTypeInformation(bufferSymbol, null);
            if (bufferTypeTree == null)
            {
                context.diagnostic.LogError($"Failed to generate type information for symbol ${bufferSymbol.ToDisplayString()}!");
                return false;
            }
            context.types.Add(bufferTypeTree);
            context.diagnostic.LogInfo($"Generating input buffer command data for ${bufferTypeTree.TypeFullName}");
            return true;
        }

        private static void GenerateInputBufferGhostComponent(Context context, TypeInformation inputTypeTree, string bufferName, ITypeSymbol bufferSymbol)
        {
            // Add to generatedType list so it is included in the serializer registration system
            context.generatedTypes.Add(bufferName);

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
                VariantTypeName = bufferTypeTree.TypeFullName.Replace("+", "."),
                ComponentTypeName = bufferTypeTree.TypeFullName.Replace("+", "."),
                Hash = variantHash.ToString(),
                GhostAttribute = bufferTypeTree.GhostAttribute,
                IsSerialized = true,
            });

            context.types.Add(bufferTypeTree);
            context.diagnostic.LogInfo($"Generating ghost for input buffer {bufferTypeTree.TypeFullName}");
            GenerateGhost(context, bufferTypeTree);
        }

        private static TypeTemplate GetGeneratorTemplate(Context context, TypeInformation information)
        {
            var typeDescription = information.Description;
            if (!context.registry.Templates.TryGetValue(typeDescription, out var template))
                throw new InvalidOperationException($"Could not find the Generator for type: {information.TypeFullName}");

            if (string.IsNullOrEmpty(template.TemplatePath))
            {
                if (!context.registry.Templates.TryGetValue(typeDescription, out var defaultTemplate))
                    throw new InvalidOperationException($"Could not find the default Generator for type: {information.TypeFullName}");
                template.TemplatePath = defaultTemplate.TemplatePath;
            }

            // TODO: subtype + composite doesn't work atm, we don't pass the subtype=x info down
            // when processing the nested types, so default variant will be used, and given template in the variant
            // will be ignored. Also you might have a normal template and set the composite=true by mistake, but
            // we can't detect this atm
            // TODO: Would also be nice to show where the template is registered
            if (template.Composite && typeDescription.Attribute.subtype > 0)
                throw new InvalidOperationException($"{typeDescription.TypeFullName}: Subtype types should not also be defined as composite. Subtypes need to be explicitly defined in a template {template.TemplatePath}.");

            return template;
        }

        private static ComponentSerializer InternalGenerateType(Context context, TypeInformation type, string fullFieldName)
        {
            context.executionContext.CancellationToken.ThrowIfCancellationRequested();

            if (!type.IsValid)
                return null;

            if (CanGenerateType(type, context))
            {
                var generator = new ComponentSerializer(context, type, GetGeneratorTemplate(context, type));
                return generator;
            }
            // If it's a primitive type and we still have not found a template to use, we can't go any deeper and it's an error
            if (type.Kind == GenTypeKind.Primitive)
            {
                context.diagnostic.LogError(
                    $"Could not find template for type {type.FieldTypeName} with parameters quantization={type.Attribute.quantization} smoothing={type.Attribute.smoothing} subtype={type.Attribute.subtype}. Default parameters can be omitted (non-quantized, no subtype, no interpolation/extrapolation).",
                    type.Location);
                return null;
            }

            if (type.Description.Attribute.subtype != 0)
                throw new InvalidOperationException($"Cannot find subtype for {type}");

            var typeGenerator = new ComponentSerializer(context, type);

            bool composite = type.Attribute.composite;
            int index = 0;

            foreach (var field in type.GhostFields)
            {
                var generator = InternalGenerateType(context, field, $"{field.DeclaringTypeFullName}.{field.FieldName}");
                //Type not found. (error should be already logged.
                if (generator == null)
                    continue;
                if (generator.Composite)
                {
                    var fieldIt = 0;
                    // Find and apply the composite overrides, then skip those fragments when processing the composite fields
                    var overrides = generator.GenerateCompositeOverrides(context, field.Parent);
                    if (overrides != null)
                        generator.AppendTarget(typeGenerator);
                    foreach (var f in generator.TypeInformation.GhostFields)
                    {
                        var g = InternalGenerateType(context, f, $"{f.DeclaringTypeFullName}.{f.FieldName}");
                        g?.GenerateFields(context, f.Parent, overrides);
                        g?.GenerateMasks(context, true, fieldIt++);
                        g?.AppendTarget(typeGenerator);
                    }
                    ++context.FieldState.numFields;
                    ++context.FieldState.curChangeMask;
                    typeGenerator.GenerateMasks(context);
                }
                else
                {
                    generator.GenerateFields(context, field.Parent);
                    generator.GenerateMasks(context, composite, index++);
                    if (!composite && !generator.IsContainerType)
                    {
                        ++context.FieldState.numFields;
                        ++context.FieldState.curChangeMask;
                    }
                    generator.AppendTarget(typeGenerator);
                }
            }

            if (type.GhostFields.Count == 0 && !type.ShouldSerializeEnabledBit)
                context.diagnostic.LogError($"Couldn't find the TypeDescriptor for the type {type.Description} when processing {fullFieldName}! Types must have either valid [GhostField] attributes, or a [GhostEnabledBit] (on an IEnableableComponent).", type.Location);

            if (composite)
            {
                ++context.FieldState.numFields;
                ++context.FieldState.curChangeMask;
            }

            typeGenerator.GenerateMasks(context);

            return typeGenerator;
        }

        #endregion

        public struct GeneratedFile
        {
            public string Namespace;
            public string GeneratedClassName;
            public string Code;
        }

        public interface ITemplateFileProvider
        {
            string GetTemplateData(string filename);
        }

        public class CodeGenCache
        {
            private Dictionary<string, GhostCodeGen> cache;
            private ITemplateFileProvider provider;
            private IDiagnosticReporter reporter;

            public CodeGenCache(ITemplateFileProvider templateFileProvider, IDiagnosticReporter diagnostic)
            {
                this.provider = templateFileProvider;
                this.reporter = diagnostic;
                this.cache = new Dictionary<string, GhostCodeGen>(128);
            }

            public GhostCodeGen GetTemplate(string templatePath)
            {
                if (!cache.TryGetValue(templatePath, out var codeGen))
                {
                    var templateData = provider.GetTemplateData(templatePath);
                    codeGen = new GhostCodeGen(templatePath, templateData, reporter);
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
                    codeGen = new GhostCodeGen(templatePath, templateData, reporter);
                    if (!string.IsNullOrEmpty(templateOverride))
                    {
                        var overrideTemplateData = provider.GetTemplateData(templateOverride);
                        codeGen.AddTemplateOverrides(templateOverride, overrideTemplateData);
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
            public readonly string generatedNs;
            public readonly TypeRegistry registry;
            public readonly IDiagnosticReporter diagnostic;
            public CodeGenCache codeGenCache;
            public List<GeneratedFile> batch;
            public List<TypeInformation> types;
            public HashSet<string> imports;
            public HashSet<string> generatedTypes;
            public struct SerializationStrategyCodeGen
            {
                public TypeInformation TypeInfo;
                public string DisplayName;
                public string ComponentTypeName;
                public string VariantTypeName;
                public string Hash;
                public bool IsSerialized;
                public GhostComponentAttribute GhostAttribute;

            }
            public List<SerializationStrategyCodeGen> serializationStrategies;
            public string variantType;
            public ulong variantHash;
            public string generatorName;

            public struct CurrentFieldState
            {
                public int numFields;
                public int curChangeMask;
                public ulong ghostFieldHash;
            }
            public CurrentFieldState FieldState;

            public void ResetState()
            {
                FieldState.numFields = 0;
                FieldState.curChangeMask = 0;
                FieldState.ghostFieldHash = 0;
                variantType = null;
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
                return Regex.Replace(assemblyName, @"[^\w\.]", "_", RegexOptions.Singleline) + ".Generated";
            }

            public Context(TypeRegistry typeRegistry, ITemplateFileProvider templateFileProvider,
                IDiagnosticReporter reporter, GeneratorExecutionContext context, string assemblyName)
            {
                executionContext = context;
                types = new List<TypeInformation>(16);
                serializationStrategies = new List<SerializationStrategyCodeGen>(32);
                codeGenCache = new CodeGenCache(templateFileProvider, reporter);
                batch = new List<GeneratedFile>(256);
                imports = new HashSet<string>();
                generatedTypes = new HashSet<string>();
                diagnostic = reporter;
                generatedNs = GenerateNamespaceFromAssemblyName(assemblyName);
                registry = typeRegistry;
                ResetState();
            }
        }
    }
}
