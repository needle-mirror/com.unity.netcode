using System;
using System.Collections.Generic;
using System.Linq;

namespace Unity.NetCode.Editor
{
    public class CodeGenerator
    {
        private TypeRegistry Registry;

        public CodeGenerator(TypeRegistry registry)
        {
            Registry = registry;
        }

        public GhostCodeGen.Batch Generate(string outputFolder, string assemblyGeneratedName, string assemblyName,
            bool isRuntimeAssembly, IEnumerable<Mono.Cecil.TypeDefinition> validTypes, IEnumerable<Mono.Cecil.TypeDefinition> commandTypes,
            Dictionary<string, GhostCodeGen> codeGenCache)
        {
            var context = new Context(assemblyName,
                CodeGenNamespaceUtils.GenerateNamespaceFromAssemblyName(assemblyGeneratedName),
                isRuntimeAssembly,
                outputFolder,
                codeGenCache);
            // generate ghost types
            foreach (var type in validTypes)
            {
                context.ResetState();
                GenerateType(context, type);
            }
            // generate registration system
            GenerateRegistrationSystem(context);

            //Generate Rpcs
            foreach (var type in commandTypes)
            {
                context.ResetState();
                GenerateCommand(context, type);
            }


            // generate asmdef
            GenerateAsmDefs(context, assemblyGeneratedName);
            return context.batch;
        }

        private void GenerateRegistrationSystem(Context context)
        {
            //Generate the ghost registration

            var regTemplate = "Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostComponentSerializerRegistrationSystem.cs";
            if (!context.typeCodeGenCache.TryGetValue(regTemplate, out var registrationSystemCodeGen))
            {
                registrationSystemCodeGen = new GhostCodeGen(regTemplate);

                context.typeCodeGenCache.Add(regTemplate, registrationSystemCodeGen);
            }
            registrationSystemCodeGen = registrationSystemCodeGen.Clone();
            var replacements = new Dictionary<string, string>();
            foreach (var t in context.generatedTypes)
            {
                replacements["GHOST_NAME"] = t;
                registrationSystemCodeGen.GenerateFragment("GHOST_COMPONENT_LIST", replacements);
            }
            replacements.Clear();
            replacements["GHOST_USING"] = context.generatedNs;
            registrationSystemCodeGen.GenerateFragment("GHOST_USING_STATEMENT", replacements);

            replacements.Clear();
            replacements.Add("GHOST_NAMESPACE", context.generatedNs);
            registrationSystemCodeGen.GenerateFile("", context.outputFolder,
                $"GhostComponentSerializerCollection.cs", replacements, context.batch);
        }

        private void GenerateAsmDefs(Context context, string assemblyGeneratedName)
        {
            var asmdefPath = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(context.AssemblyName);
            var includePlatforms = "";
            var excludePlatforms = "";
            var defineConstraints = "";
            if (!string.IsNullOrEmpty(asmdefPath))
            {
                var assemblyDefinition = UnityEngine.JsonUtility.FromJson<GhostCompiler.UnityAssemblyDefinition>(System.IO.File.ReadAllText(asmdefPath));
                string EscapeStrings(string[] list)
                {
                    if (list == null)
                        return "";
                    for (var i = 0; i < list.Length; ++i)
                        list[i] = $"\"{list[i]}\"";
                    return string.Join(",", list);
                }
                includePlatforms = EscapeStrings(assemblyDefinition.includePlatforms);
                excludePlatforms = EscapeStrings(assemblyDefinition.excludePlatforms);
                defineConstraints = EscapeStrings(assemblyDefinition.defineConstraints);

                foreach (var r in assemblyDefinition.references)
                {
                    context.collectionAssemblies.Add(r);
                }
            }

            //Remove unwanted references to basic assemblies
            context.collectionAssemblies.Remove("mscorlib");
            context.collectionAssemblies.Remove("netstandard");

            // Use guid references instead of assembly name references
            // Some references comes from the assembly which we are generating code for,
            // if those are guids and we add by name too we can get duplicate references
            var nonGuids = new List<string>();
            foreach (var asm in context.collectionAssemblies)
            {
                if (!asm.StartsWith("GUID:"))
                    nonGuids.Add(asm);
            }
            foreach (var nonGuid in nonGuids)
            {
                var path = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(nonGuid);
                if (path != null)
                {
                    var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
                    if (!String.IsNullOrEmpty(guid))
                    {
                        context.collectionAssemblies.Remove(nonGuid);
                        context.collectionAssemblies.Add(UnityEditor.Compilation.CompilationPipeline.GUIDToAssemblyDefinitionReferenceGUID(guid));
                    }
                }
            }
            //Remove duplicate (already added)
            context.collectionAssemblies.Remove("Unity.Networking.Transport");
            var transportPath = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("Unity.Networking.Transport");
            if (transportPath != null)
            {
                var guid = UnityEditor.AssetDatabase.AssetPathToGUID(transportPath);
                if (!String.IsNullOrEmpty(guid))
                    context.collectionAssemblies.Remove(UnityEditor.Compilation.CompilationPipeline.GUIDToAssemblyDefinitionReferenceGUID(guid));
            }

            var replacements = new Dictionary<string, string>();
            var asmdefTemplate = "Packages/com.unity.netcode/Editor/CodeGenTemplates/NetCode.Generated.asmdef.template";
            if (!context.typeCodeGenCache.TryGetValue(asmdefTemplate, out var asmdefCodeGen))
            {
                asmdefCodeGen = new GhostCodeGen(asmdefTemplate);

                context.typeCodeGenCache.Add(asmdefTemplate, asmdefCodeGen);
            }
            asmdefCodeGen = asmdefCodeGen.Clone();

            replacements.Add("ASSEMBLY_NAME", assemblyGeneratedName);
            asmdefCodeGen.GenerateFragment("GHOST_ASSEMBLY_NAME", replacements);
            foreach (var asm in context.collectionAssemblies)
            {
                replacements["GHOST_ASSEMBLY"] = asm;
                asmdefCodeGen.GenerateFragment("GHOST_ASSEMBLIES", replacements);
            }
            replacements.Add("GHOST_INCLUDE_PLATFORMS", includePlatforms);
            replacements.Add("GHOST_EXCLUDE_PLATFORMS", excludePlatforms);
            replacements.Add("GHOST_DEFINE_CONSTRAINTS", defineConstraints);
            asmdefCodeGen.GenerateFile("", context.outputFolder, $"{assemblyGeneratedName}.asmdef",
                replacements, context.batch);
        }

        #region Internal for Types

        void GenerateType(Context context, Mono.Cecil.TypeDefinition type)
        {
            TypeInformation typeTree;
            Mono.Cecil.TypeDefinition componentType;
            GhostComponentAttribute ghostAttribute;
            var variantAttr = type.GetAttribute<GhostComponentVariationAttribute>();
            if (variantAttr != null)
            {
                ghostAttribute = CecilExtensions.GetGhostComponentAttribute(type);
                context.VariantHash = CecilExtensions.ComputeVariantHash(type, variantAttr);
                var typeReference = variantAttr.ConstructorArguments.First().Value as Mono.Cecil.TypeReference;
                typeTree = ParseVariantTypeFields(type, typeReference);
                componentType = typeReference.Resolve();
            }
            else
            {
                if (GhostAuthoringModifiers.GhostDefaultOverrides.TryGetValue(type.FullName.Replace('/', '+'), out var newComponent))
                    ghostAttribute = newComponent.attribute;
                else
                    ghostAttribute = CecilExtensions.GetGhostComponentAttribute(type);
                typeTree = ParseTypeFields(type);
                componentType = type;
            }

            var generator = InternalGenerateType(context, typeTree, type.FullName);
            generator.GenerateMasks(context);

            var serializeGenerator = new TypeGenerator(context);
            generator.AppendTarget(serializeGenerator);
            serializeGenerator.GenerateSerializer(context, type, componentType, ghostAttribute);
        }

        private void GenerateCommand(Context context, Mono.Cecil.TypeDefinition type)
        {
            void BuildGenerator(Context ctx, TypeInformation typeInfo, CommandGenerator parentGenerator)
            {
                if (!typeInfo.IsValid)
                    return;

                var description = typeInfo.Description;
                var fieldGen = new CommandGenerator(context, parentGenerator.CommandType, typeInfo);
                if (Registry.Templates.TryGetValue(description, out var template))
                {
                    if (!template.SupportCommand)
                        return;

                    fieldGen  = new CommandGenerator(context, parentGenerator.CommandType, typeInfo, GetGeneratorTemplate(typeInfo));
                    if (!template.Composite)
                    {
                        fieldGen.GenerateFields(ctx, typeInfo.Parent);
                        fieldGen.AppendTarget(parentGenerator);
                        return;
                    }
                }
                foreach (var field in typeInfo.Fields)
                {
                    BuildGenerator(ctx, field, fieldGen);
                }
                fieldGen.AppendTarget(parentGenerator);
            }

            var typeTree = ParseTypeFields(type, false);
            var serializeGenerator = new CommandGenerator(context, type.IsIRpcCommand() ? CommandGenerator.Type.Rpc : CommandGenerator.Type.Input);
            BuildGenerator(context, typeTree, serializeGenerator);
            serializeGenerator.GenerateSerializer(context, type);
        }

        #endregion

        #region Internal for Parsing

        static internal TypeInformation ParseVariantTypeFields(Mono.Cecil.TypeDefinition variantType, Mono.Cecil.TypeReference typeReference)
        {
            var type = typeReference.Resolve();

            var root = new TypeInformation(type);
            if (!variantType.HasFields)
                return root;

            foreach (var field in variantType.Fields)
            {
                if (!field.IsPublic || field.IsStatic)
                    continue;

                if (type.Fields.FirstOrDefault(f=> f.Name == field.Name) == null)
                {
                    UnityEngine.Debug.LogError($"Variant {variantType.Name}. field {field.Name} not present in original type {type.Name}");
                    continue;
                }

                //Avoid use ghost fields modifiers for the variant type. passing null prevent that
                var ghostField = CecilExtensions.GetGhostFieldAttribute(null, field);
                if (ghostField != null && ghostField.SendData)
                {
                    root.Add(ParseTypeField(field, field.FieldType, ghostField, TypeAttribute.Empty(), root.AttributeMask));
                }
            }
            foreach (var prop in type.Properties)
            {
                if (prop.GetMethod == null || !prop.GetMethod.IsPublic || prop.GetMethod.IsStatic)
                    continue;
                if (prop.SetMethod == null || !prop.SetMethod.IsPublic || prop.SetMethod.IsStatic)
                    continue;

                if (type.Properties.FirstOrDefault(f=> f.Name == prop.Name) == null)
                {
                    UnityEngine.Debug.LogError($"Variant {variantType.Name}. field {prop.Name} not present in original type {type.Name}");
                    continue;
                }

                var ghostField = CecilExtensions.GetGhostFieldAttribute(type, prop);
                if (ghostField != null && ghostField.SendData)
                {
                    root.Add(ParseTypeField(prop, prop.PropertyType, ghostField, TypeAttribute.Empty(), root.AttributeMask));
                }
            }
            return root;
        }

        static internal TypeInformation ParseTypeFields(Mono.Cecil.TypeDefinition type, bool onlyFieldWithGhostField = true)
        {
            bool isBuffer = CecilExtensions.IsBufferElementData(type);
            bool isCommandData = CecilExtensions.IsICommandData(type);
            var root = new TypeInformation(type);
            if (isBuffer)
                root.AttributeMask &= ~(TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated);

            if (!type.HasFields)
                return root;
            foreach (var field in type.Fields)
            {
                if (!field.IsPublic || field.IsStatic)
                    continue;

                var ghostField = CecilExtensions.GetGhostFieldAttribute(type, field);
                if (!onlyFieldWithGhostField && isCommandData &&
                    field.HasAttributeOnMemberOImplementedInterfaces<DontSerializeForCommand>())
                    continue;
                if (!onlyFieldWithGhostField || (ghostField != null && ghostField.SendData))
                {
                    root.Add(ParseTypeField(field, field.FieldType, ghostField,TypeAttribute.Empty(), root.AttributeMask));
                }
            }
            foreach (var prop in type.Properties)
            {
                if (prop.GetMethod == null || !prop.GetMethod.IsPublic || prop.GetMethod.IsStatic)
                    continue;
                if (prop.SetMethod == null || !prop.SetMethod.IsPublic || prop.SetMethod.IsStatic)
                    continue;

                var ghostField = CecilExtensions.GetGhostFieldAttribute(type, prop);
                if (!onlyFieldWithGhostField && isCommandData &&
                    prop.HasAttributeOnMemberOImplementedInterfaces<DontSerializeForCommand>())
                    continue;

                if (!onlyFieldWithGhostField || (ghostField != null && ghostField.SendData))
                {
                    root.Add(ParseTypeField(prop, prop.PropertyType, ghostField, TypeAttribute.Empty(), root.AttributeMask));
                }
            }


            return root;
        }

        static private TypeInformation ParseTypeField(Mono.Cecil.IMemberDefinition fieldInfo, Mono.Cecil.TypeReference fieldType,
            GhostFieldAttribute ghostField, TypeAttribute inheritedAttribute, TypeAttribute.AttributeFlags inheriteAttributedMask, string parent = "")
        {
            var information = new TypeInformation(fieldInfo, fieldType, ghostField, inheritedAttribute, inheriteAttributedMask, parent);
            //blittable also contains bool, but does not contains enums
            if (fieldType.IsBlittable())
                return information;

            var fieldDef = fieldType.Resolve();
            if (fieldDef.IsEnum)
                return information;

            if (!fieldDef.IsStruct())
                return default;

            parent = string.IsNullOrEmpty(parent)
                ? fieldInfo.Name
                : parent + "." + fieldInfo.Name;

            foreach (var field in fieldDef.Fields)
            {
                if (!field.IsPublic || field.IsStatic)
                    continue;

                ghostField = CecilExtensions.GetGhostFieldAttribute(fieldType, field);
                if (ghostField == null || ghostField.SendData)
                    information.Add(ParseTypeField(field, field.FieldType, ghostField, information.Attribute, information.AttributeMask, parent));
            }
            return information;
        }

        TypeTemplate GetGeneratorTemplate(TypeInformation information)
        {
            var typeDescription = information.Description;
            if (!Registry.Templates.TryGetValue(typeDescription, out var template))
            {
                throw new InvalidOperationException($"Could't not find the Generator for type: {information.Type.Name}");
            }

            if (string.IsNullOrEmpty(template.TemplatePath))
            {
                if (!Registry.Templates.TryGetValue(typeDescription, out var defaultTemplate))
                {
                    throw new InvalidOperationException($"Could't not find the default Generator for type: {information.Type.Name}");
                }
                template.TemplatePath = defaultTemplate.TemplatePath;
            }
            return template;
        }

        #endregion

        #region Internal for Code Generation

        TypeGenerator InternalGenerateType(Context context, TypeInformation type, string fullFieldName)
        {
            if (!type.IsValid)
                return null;

            bool canGenerate = Registry.CanGenerateType(type.Description);
            if (canGenerate)
            {
                var generator  = new TypeGenerator(context, type, GetGeneratorTemplate(type));
                return generator;
            }

            var typeGenerator = new TypeGenerator(context, type);

            bool composite = type.Attribute.composite;
            int index = 0;

            foreach (var field in type.Fields)
            {
                var generator = InternalGenerateType(context, field, $"{field.DeclaringType.FullName}.{field.FieldName}");
                if (generator.Composite)
                {
                    int fieldIt = 0;
                    // Find and apply the composite overrides, then skip those fragments when processing the composite fields
                    var overrides = generator.GenerateCompositeOverrides(context, field.Parent);
                    if (overrides != null)
                        generator.AppendTarget(typeGenerator);
                    foreach (var f in generator.TypeInformation.Fields)
                    {
                        var g = InternalGenerateType(context, f, $"{f.DeclaringType.FullName}.{f.FieldName}");
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
                UnityEngine.Debug.LogError($"Couldn't find the TypeDescriptor for the type {type.Description.TypeFullName} (quantized={type.Description.Attribute.quantization} composite={type.Description.Attribute.composite} smoothing={type.Description.Attribute.smoothing} subtype={type.Description.Attribute.subtype}) when processing {fullFieldName}");

            if (composite)
            {
                ++context.FieldState.numFields;
                ++context.FieldState.curChangeMask;
            }

            typeGenerator.GenerateMasks(context);

            return typeGenerator;
        }

        #endregion

        #region Context

        public class Context
        {
            public string AssemblyName;
            public GhostCodeGen.Batch batch;
            public Dictionary<string, GhostCodeGen> typeCodeGenCache;
            public bool IsRuntimeAssembly;
            public HashSet<string> imports;
            public HashSet<string> collectionAssemblies;
            public HashSet<string> generatedTypes;
            public string outputFolder;
            public string generatedNs;
            public ulong VariantHash;

            public struct CurrentFieldState
            {
                public int numFields;
                public int curChangeMask;
                public ulong ghostfieldHash;
                public bool isComposite;
                public int currentQuantizationFactor;
            }
            public CurrentFieldState FieldState;
            public void ResetState()
            {
                FieldState.numFields = 0;
                FieldState.curChangeMask = 0;
                FieldState.ghostfieldHash = 0;
                FieldState.currentQuantizationFactor = -1;
                FieldState.isComposite = false;
                VariantHash = 0;
                imports.Clear();
                imports.Add("Unity.Entities");
                imports.Add("Unity.Collections");
                imports.Add("Unity.NetCode");
                imports.Add("Unity.Transforms");
                imports.Add("Unity.Mathematics");
            }

            public Context(string assembly, string generatedAssemblyNs, bool isRuntimeAssembly, string outputDir, Dictionary<string, GhostCodeGen> codeGenCache)
            {
                AssemblyName = assembly;
                IsRuntimeAssembly = isRuntimeAssembly;
                FieldState.numFields = 0;
                FieldState.curChangeMask = 0;
                FieldState.currentQuantizationFactor = -1;
                generatedNs = generatedAssemblyNs;
                outputFolder = outputDir;
                VariantHash = 0;

                batch = new GhostCodeGen.Batch();
                typeCodeGenCache = codeGenCache;

                imports = new HashSet<string>();
                generatedTypes = new HashSet<string>();
                collectionAssemblies = new HashSet<string>();

                collectionAssemblies.Add("Unity.Entities");
                collectionAssemblies.Add("Unity.Burst");
                collectionAssemblies.Add("Unity.NetCode");
                collectionAssemblies.Add("Unity.Transforms");
                collectionAssemblies.Add("Unity.Mathematics");
                collectionAssemblies.Add("Unity.Collections");
                collectionAssemblies.Add(assembly);
            }
        }
        #endregion
    }
}

