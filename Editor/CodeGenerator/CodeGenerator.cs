using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    public class CodeGenerator
    {
        public DebugInformation Debug;
        private TypeRegistry Registry;

        public CodeGenerator(TypeRegistry registry)
        {
            Debug = new DebugInformation();
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
                var assemblyDefinition = JsonUtility.FromJson<GhostCompiler.UnityAssemblyDefinition>(System.IO.File.ReadAllText(asmdefPath));
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
            var typeTree = ParseTypeFields(type);

            var generator = InternalGenerateType(context, typeTree, type.FullName);
            generator.GenerateMasks(context);

            var serializeGenerator = new TypeGenerator(context);
            generator.AppendTarget(serializeGenerator);
            serializeGenerator.GenerateSerializer(context, type);
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
        TypeInformation ParseTypeFields(Mono.Cecil.TypeDefinition type, bool onlyFieldWithGhostField = true)
        {
            var root = new TypeInformation(type);
            if (!type.HasFields)
                return root;

            foreach (var field in type.Fields)
            {
                if (!field.IsPublic || field.IsStatic)
                    continue;

                if (!onlyFieldWithGhostField || CecilExtensions.HasGhostFieldAttribute(type, field))
                {
                    if (!CecilExtensions.HasGhostFieldAttribute(type, field) || CecilExtensions.GetGhostFieldAttribute(type, field).SendData)
                        root.Add(ParseTypeField(type, field, TypeAttribute.Empty()));
                }
            }
            return root;
        }

        TypeInformation ParseTypeField(Mono.Cecil.TypeReference typeDefinition, Mono.Cecil.FieldDefinition fieldInfo, TypeAttribute inheritedAttribute, string parent = "")
        {
            var information = new TypeInformation(typeDefinition, fieldInfo, inheritedAttribute, parent);

            //blittable also contains bool, but does not contains enums
            if (fieldInfo.FieldType.IsBlittable())
            {
                return information;
            }

            var fieldTypeDef = fieldInfo.FieldType.Resolve();

            if (fieldTypeDef.IsEnum)
                return information;

            if (!fieldTypeDef.IsStruct())
            {
                return default;
            }

            parent = string.IsNullOrEmpty(parent)
                ? fieldInfo.Name
                : parent + "." + fieldInfo.Name;

            foreach (var field in fieldTypeDef.Fields)
            {
                if (!field.IsPublic || field.IsStatic)
                    continue;
                if (!CecilExtensions.HasGhostFieldAttribute(fieldTypeDef, field) || CecilExtensions.GetGhostFieldAttribute(fieldTypeDef, field).SendData)
                    information.Add(ParseTypeField(fieldTypeDef,  field, information.Attribute, parent));
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
                var generator = InternalGenerateType(context, field, $"{field.FieldInfo.DeclaringType.FullName}.{field.FieldInfo.Name}");
                if (generator.Composite)
                {
                    int fieldIt = 0;
                    // Find and apply the composite overrides, then skip those fragments when processing the composite fields
                    var overrides = generator.GenerateCompositeOverrides(context, field.Parent);
                    if (overrides != null)
                        generator.AppendTarget(typeGenerator);
                    foreach (var f in generator.TypeInformation.Fields)
                    {
                        var g = InternalGenerateType(context, f, $"{f.FieldInfo.DeclaringType.FullName}.{f.FieldInfo.Name}");
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
                    generator?.GenerateFields(context, field.Parent);
                    generator?.GenerateMasks(context, composite, index++);
                    if (!composite && !(generator?.IsContainerType ?? false))
                    {
                        ++context.FieldState.numFields;
                        ++context.FieldState.curChangeMask;
                    }
                    generator?.AppendTarget(typeGenerator);
                }
            }

            if (type.Fields.Count == 0 && !canGenerate)
                UnityEngine.Debug.LogError($"Couldn't find the TypeDescriptor for the type {type.Description.TypeFullName} (quantized={type.Description.Attribute.quantization} composite={type.Description.Attribute.composite} interpolated={type.Description.Attribute.interpolate} subtype={type.Description.Attribute.subtype}) when processing {fullFieldName}");

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

