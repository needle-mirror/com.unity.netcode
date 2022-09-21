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
            if(context.generatedTypes.Count == 0 && context.emptyVariantTypes.Count == 0)
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
                foreach (var t in context.emptyVariantTypes)
                {
                    if(t.Hash == "0")
                        context.diagnostic.LogError($"Setting invalid hash on variantType {t.VariantType} to {t.Hash}!");

                    replacements["VARIANT_TYPE"] = t.VariantType;
                    replacements["GHOST_COMPONENT_TYPE"] = t.ComponentType;
                    replacements["GHOST_VARIANT_HASH"] = t.Hash;
                    if (t.GhostAttribute != null)
                        replacements["GHOST_PREFAB_TYPE"] = $"GhostPrefabType.{t.GhostAttribute.PrefabType.ToString()}";
                    else
                        replacements["GHOST_PREFAB_TYPE"] = "GhostPrefabType.All";
                    registrationSystemCodeGen.GenerateFragment("GHOST_EMPTY_VARIANT_LIST", replacements);
                }

                replacements.Clear();
                replacements["GHOST_USING"] = context.generatedNs;
                registrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);

                replacements.Clear();
                replacements.Add("GHOST_NAMESPACE", context.generatedNs);
                registrationSystemCodeGen.GenerateFile("GhostComponentSerializerCollection.cs", string.Empty,replacements, context.batch);
            }
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

        public static void GenerateCommand(Context context, TypeInformation typeTree, CommandSerializer.Type commandType)
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
                foreach (var field in typeInfo.Fields)
                    BuildGenerator(ctx, field, fieldGen);
                fieldGen.AppendTarget(parentGenerator);
            }

            using(new Profiler.Auto("CodeGen"))
            {
                var serializeGenerator = new CommandSerializer(context, commandType);
                BuildGenerator(context, typeTree, serializeGenerator);
                serializeGenerator.GenerateSerializer(context, typeTree);
                if (commandType == Generators.CommandSerializer.Type.Input)
                {
                    // The input component needs to be registered as an empty type variant so that the
                    // ghost component attributes placed on it can be parsed during ghost conversion
                    var inputGhostAttributes = ComponentFactory.TryGetGhostComponent(typeTree.Symbol);
                    if (inputGhostAttributes == null)
                        inputGhostAttributes = new GhostComponentAttribute();
                    var variantHash = Helpers.ComputeVariantHash(typeTree.Symbol, typeTree.Symbol);
                    context.emptyVariantTypeInfo.Add(typeTree);
                    context.emptyVariantTypes.Add(new CodeGenerator.Context.EmptyVariant
                    {
                        VariantType = typeTree.TypeFullName.Replace("+", "."),
                        ComponentType = typeTree.TypeFullName.Replace("+", "."),
                        Hash = variantHash.ToString(),
                        GhostAttribute = inputGhostAttributes
                    });

                    TypeInformation bufferTypeTree;
                    ITypeSymbol bufferSymbol;
                    string bufferName;
                    using (new Profiler.Auto("GenerateInputBufferType"))
                    {
                        if (!GenerateInputBufferType(context, typeTree, out bufferTypeTree,
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
                        bool hasGhostField = false;
                        foreach (var member in typeTree.Symbol.GetMembers())
                        {
                            foreach (var attribute in member.GetAttributes())
                            {
                                if (attribute.AttributeClass != null &&
                                    attribute.AttributeClass.Name is "GhostFieldAttribute" or "GhostField")
                                    hasGhostField = true;
                            }
                        }

                        // Parse the generated input buffer as a component so it will be included in snapshot replication
                        // This only needs to be done if the input struct has ghost fields inside as the generated input
                        // buffer should then be replicated to remote players
                        if (hasGhostField)
                        {
                            GenerateInputBufferGhostComponent(context, typeTree, bufferName, bufferSymbol);
                        }
                        else
                        {
                            // If there are no ghost fields we need to add the buffer to the empty variant
                            // list to save the ghost component attributes
                            var bufferVariantHash = Helpers.ComputeVariantHash(bufferTypeTree.Symbol, bufferTypeTree.Symbol);
                            context.emptyVariantTypeInfo.Add(typeTree);
                            context.emptyVariantTypes.Add(new CodeGenerator.Context.EmptyVariant
                            {
                                VariantType = bufferTypeTree.TypeFullName.Replace("+", "."),
                                ComponentType = bufferTypeTree.TypeFullName.Replace("+", "."),
                                Hash = bufferVariantHash.ToString(),
                                GhostAttribute = inputGhostAttributes
                            });

                        }
                    }
                }
            }
        }

        public static void GenerateMetaData(Context context, TypeInformation[] allTypes)
        {
            using (new Profiler.Auto("GenerateMetaDataRegistrationSystem"))
            {
                context.ResetState();
                var assemblyNameSanitized = context.executionContext.Compilation.Assembly.Name.Replace(".", "").Replace("+", "_").Replace("-", "_");
                context.generatorName = $"NetCodeTypeMetaDataRegistrationSystem_{assemblyNameSanitized}";
                context.diagnostic.LogInfo($"Begun generation of '{context.generatorName}', checking {allTypes.Length} types.");

                var metaDataRegistrationSystemCodeGen = context.codeGenCache.GetTemplate(MetaDataRegistrationSystem);
                metaDataRegistrationSystemCodeGen = metaDataRegistrationSystemCodeGen.Clone();

                var replacements = new Dictionary<string, string>(8);
                replacements["REGISTRATION_SYSTEM_FILE_NAME"] = context.generatorName;

                var alreadyAddedNamespaces = new HashSet<string>
                {
                    // Rule out the erroneous ones.
                    string.Empty,
                    null,
                    " ",
                };

                const string unityCodeGenNamespace = "Unity.NetCode.Generated";
                replacements["GHOST_NAMESPACE"] = unityCodeGenNamespace;

                int numTypesAdded = 0;
                foreach (var ns in context.imports)
                {
                    var validNamespaceForType = GetValidNamespaceForType(context.generatedNs, ns);
                    if (!alreadyAddedNamespaces.Contains(validNamespaceForType))
                    {
                        alreadyAddedNamespaces.Add(validNamespaceForType);
                        replacements["GHOST_USING"] = validNamespaceForType;
                        metaDataRegistrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);
                    }
                }

                foreach (var typeInfo in allTypes)
                {
                    context.executionContext.CancellationToken.ThrowIfCancellationRequested();

                    if (!typeInfo.IsValid)
                    {
                        context.diagnostic.LogInfo($"NOT generating meta-data for ${typeInfo.TypeFullName} as not a valid NetCode type.");
                        continue;
                    }
                    context.diagnostic.LogInfo($"Generating meta-data for ${typeInfo.TypeFullName}");

                    if (!alreadyAddedNamespaces.Contains(typeInfo.Namespace))
                    {
                        alreadyAddedNamespaces.Add(typeInfo.Namespace);
                        replacements["GHOST_USING"] = typeInfo.Namespace;
                        metaDataRegistrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);
                    }

                    // If this is a variant, we need to parse out the type it's for, and ensure we use that in the hash below.
                    var variantTypeFullName = typeInfo.TypeFullName.Replace("+", ".");
                    var componentTypeFullName = typeInfo.Symbol.GetFullTypeName().Replace("+", ".");

                    replacements["VARIANT_TYPE_HASH"] = Helpers.ComputeVariantHash(variantTypeFullName, componentTypeFullName).ToString();
                    replacements["TYPE_IS_INPUT_COMPONENT"] = typeInfo.ComponentType == ComponentType.Input ? "true" : "false";
                    replacements["TYPE_IS_INPUT_BUFFER"] = typeInfo.ComponentType == ComponentType.CommandData ? "true" : "false";
                    replacements["TYPE_IS_TEST_VARIANT"] = typeInfo.IsTestVariant ? "true" : "false";
                    replacements["TYPE_HAS_DONT_SUPPORT_PREFAB_OVERRIDES_ATTRIBUTE"] = typeInfo.HasDontSupportPrefabOverridesAttribute ? "true" : "false";
                    replacements["TYPE_HAS_SUPPORTS_PREFAB_OVERRIDES_ATTRIBUTE"] = typeInfo.HasSupportsPrefabOverridesAttribute ? "true" : "false";
                    metaDataRegistrationSystemCodeGen.GenerateFragment("GHOST_META_DATA_LIST", replacements);
                    numTypesAdded++;
                }

                context.diagnostic.LogInfo($"Completed generation of meta-data registration system, {numTypesAdded} of {allTypes.Length} types total.");

                if(numTypesAdded > 0)
                    metaDataRegistrationSystemCodeGen.GenerateFile(context.generatorName + ".cs", unityCodeGenNamespace, replacements, context.batch);
                else context.diagnostic.LogInfo("Meta-data registration file will not be created as no types to register!");
            }
        }

        #region Internal for Code Generation

        private static bool GenerateInputBufferType(Context context, TypeInformation typeTree, out TypeInformation bufferTypeTree, out ITypeSymbol bufferSymbol, out string bufferName)
        {
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
                context.diagnostic.LogError($"Failed to generate type information for symbol ${bufferSymbol.ToDisplayString()}");
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
                return;
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

            context.types.Add(bufferTypeTree);
            context.diagnostic.LogInfo($"Generating ghost for {bufferTypeTree.TypeFullName}");
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

            foreach (var field in type.Fields)
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
                    foreach (var f in generator.TypeInformation.Fields)
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

            if (type.Fields.Count == 0)
                context.diagnostic.LogError($"Couldn't find the TypeDescriptor for the type {type.Description} when processing {fullFieldName}", type.Location);

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
            public struct EmptyVariant
            {
                public string ComponentType;
                public string VariantType;
                public string Hash;
                public GhostComponentAttribute GhostAttribute;
            }
            public List<TypeInformation> emptyVariantTypeInfo;
            public HashSet<EmptyVariant> emptyVariantTypes;
            public string variantType;
            public ulong variantHash;
            public string generatorName;

            public struct CurrentFieldState
            {
                public int numFields;
                public int curChangeMask;
                public ulong ghostfieldHash;
            }
            public CurrentFieldState FieldState;
            public void ResetState()
            {
                FieldState.numFields = 0;
                FieldState.curChangeMask = 0;
                FieldState.ghostfieldHash = 0;
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
                emptyVariantTypeInfo = new List<TypeInformation>(16);
                codeGenCache = new CodeGenCache(templateFileProvider, reporter);
                batch = new List<GeneratedFile>(256);
                imports = new HashSet<string>();
                generatedTypes = new HashSet<string>();
                emptyVariantTypes = new HashSet<EmptyVariant>();
                diagnostic = reporter;
                generatedNs = GenerateNamespaceFromAssemblyName(assemblyName);
                registry = typeRegistry;
                ResetState();
            }
        }
    }
}
