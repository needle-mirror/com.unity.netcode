using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    public static class GlobalOptions
    {
        /// <summary>
        /// Override the current project path. Used by the generator to flush logs or lookup files.
        /// </summary>
        public const string ProjectPath = "unity.netcode.sourcegenerator.projectpath";
        /// <summary>
        /// Override the output folder where the generator flush logs and generated files.
        /// </summary>
        public const string OutputPath = "unity.netcode.sourcegenerator.outputfolder";
        /// <summary>
        /// Skip validation of missing assmebly references. Mostly used for testing.
        /// </summary>
        public const string DisableRerencesChecks = "unity.netcode.sourcegenerator.disable_references_checks";
        /// <summary>
        /// Enable/Disable support for passing custom templates using additional files. Mostly for testing
        /// </summary>
        public const string TemplateFromAdditionalFiles = "unity.netcode.sourcegenerator.templates_from_additional_files";
        /// <summary>
        /// Enable/Disable writing generated code to output folder
        /// </summary>
        public const string WriteFilesToDisk = "unity.netcode.sourcegenerator.write_files_to_disk";
        /// <summary>
        /// Enable/Disable writing logs to the file (default is Temp/NetCodeGenerated/sourcegenerator.log)
        /// </summary>
        public const string WriteLogsToDisk = "unity.netcode.sourcegenerator.write_logs_to_disk";
        /// <summary>
        /// The minimal log level. Available: Debug, Warning, Error. Default is error. (NOT SUPPORTED YET)
        /// </summary>
        public const string LoggingLevel = "unity.netcode.sourcegenerator.logging_level";
        /// <summary>
        /// Enable/Disable writing logs to the file (default is Temp/NetCodeGenerated/sourcegenerator.log)
        /// </summary>
        public const string EmitTimings = "unity.netcode.sourcegenerator.emit_timing";
        /// <summary>
        /// Enable/Disable writing logs to the file (default is Temp/NetCodeGenerated/sourcegenerator.log)
        /// </summary>
        public const string AttachDebugger = "unity.netcode.sourcegenerator.attach_debugger";

        ///<summary>
        /// return if a flag is set in the GlobalOption dictionary.
        /// A flag is consider set if the key is in the GlobalOptions and its string value is either empty or "1"
        /// Otherwise the flag is considered as not set.
        ///</summary>
        public static bool GetOptionsFlag(this  GeneratorExecutionContext context, string key, bool defaultValue=false)
        {
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue(key, out var stringValue))
                return string.IsNullOrEmpty(stringValue) || (stringValue is "1" or "true");
            return defaultValue;
        }

        /// <summary>
        /// Return the string value associated with the key in the GlobalOptions if the key is present
        /// </summary>
        /// <param name="context"></param>
        /// <param name="key"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public static string GetOptionsString(this GeneratorExecutionContext context, string key, string defaultValue=null)
        {
            if (context.AnalyzerConfigOptions.GlobalOptions.TryGetValue(key, out var stringValue))
                return stringValue;
            return defaultValue;
        }
    }

    /// <summary>
    /// Parse the syntax tree using <see cref="NetCodeSyntaxReceiver"/> and generate for Rpc, Commands and Ghost
    /// serialization code.
    /// Must be stateless and immutable. Can be called from multiple thread or the instance reused
    /// </summary>
    [Generator]
    public class NetCodeSourceGenerator : ISourceGenerator
    {
        internal struct Candidates
        {
            public List<SyntaxNode> Components;
            public List<SyntaxNode> Rpcs;
            public List<SyntaxNode> Commands;
            public List<SyntaxNode> Inputs;
            public List<SyntaxNode> Variants;
        }

        public const string NETCODE_ADDITIONAL_FILE = ".NetCodeSourceGenerator.additionalfile";

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new NetCodeSyntaxReceiver());
            //Initialize the profile here also take in account the internal Unity compilation time not
            //stritcly related to the source generators. This is useful metric to have, since we can then how
            //much we accounts (in %) in respect to the total compilation time.
            Profiler.Initialize();
        }

        static bool ShouldRunGenerator(GeneratorExecutionContext executionContext)
        {
            //Skip running if no references to netcode are passed to the compilation
            return executionContext.Compilation.Assembly.Name.StartsWith("Unity.NetCode", StringComparison.Ordinal) ||
                   executionContext.Compilation.ReferencedAssemblyNames.Any(r=>
                       r.Name.Equals("Unity.NetCode", StringComparison.Ordinal) ||
                       r.Name.Equals("Unity.NetCode.ref", StringComparison.Ordinal));
        }

        /// <summary>
        /// Main entry point called from Roslyn, after the syntax analysis has been completed.
        /// At this point we should have collected all the candidates
        /// </summary>
        /// <param name="executionContext"></param>
        public void Execute(GeneratorExecutionContext executionContext)
        {
            executionContext.CancellationToken.ThrowIfCancellationRequested();

            if (!ShouldRunGenerator(executionContext))
                return;

            Helpers.SetupContext(executionContext);
            var diagnostic = new DiagnosticReporter(executionContext);
            diagnostic.LogInfo($"Begin Processing assembly {executionContext.Compilation.AssemblyName}");

            //If the attach_debugger key is present (but without value) the returned string is the empty string (not null)
            var debugAssembly = executionContext.GetOptionsString(GlobalOptions.AttachDebugger);
            if(debugAssembly != null)
            {
                Debug.LaunchDebugger(executionContext, debugAssembly);
            }
            try
            {
                Generate(executionContext, diagnostic);
            }
            catch (Exception e)
            {
                diagnostic.LogException(e);
            }
            diagnostic.LogInfo($"End Processing assembly {executionContext.Compilation.AssemblyName}.");
            diagnostic.LogInfo(Profiler.PrintStats(executionContext.GetOptionsFlag(GlobalOptions.EmitTimings)));
        }

        private static void Generate(GeneratorExecutionContext executionContext, IDiagnosticReporter diagnostic)
        {
            //Try to dispatch any unknown candidates to the right array by checking what interface the struct is implementing
            var receiver = (NetCodeSyntaxReceiver)executionContext.SyntaxReceiver;
            var candidates = ResolveCandidates(executionContext, receiver, diagnostic);
            var totalCandidates = candidates.Rpcs.Count + candidates.Commands.Count + candidates.Components.Count + candidates.Variants.Count + candidates.Inputs.Count;
            if (totalCandidates == 0)
                return;

            //Initialize template registry and register custom user type definitions
            ImportTemplates(executionContext, diagnostic, out var templateFileProvider);
            var codeGenerationContext = new CodeGenerator.Context(templateFileProvider, diagnostic, executionContext, executionContext.Compilation.AssemblyName);
            // The ghost,commands and rpcs generation start here. Just loop through all the semantic models, check
            // the necessary conditions and pass the extract TypeInformation to our custom code generation system
            // that will build the necessary source code.
            using (new Profiler.Auto("Generate"))
            {
                // Generate command data wrapper for input data and the CopyToBuffer/CopyFromBuffer systems
                using(new Profiler.Auto("InputGeneration"))
                    InputFactory.Generate(candidates.Inputs, codeGenerationContext, executionContext);
                //Generate serializers for components and buffers
                using (new Profiler.Auto("ComponentGeneration"))
                    ComponentFactory.Generate(candidates.Components, candidates.Variants, codeGenerationContext);
                // Generate serializers for rpcs and commands
                using(new Profiler.Auto("CommandsGeneration"))
                    CommandFactory.Generate(candidates.Commands, codeGenerationContext);
                using(new Profiler.Auto("RpcGeneration"))
                    RpcFactory.Generate(candidates.Rpcs, codeGenerationContext);
            }
            if (codeGenerationContext.batch.Count > 0)
            {
                if(!executionContext.GetOptionsFlag(GlobalOptions.DisableRerencesChecks))
                {
                    //Make sure the assembly has the right references and treat them as a fatal error
                    var missingReferences = new HashSet<string>{"Unity.Collections", "Unity.Burst", "Unity.Mathematics"};
                    foreach (var r in executionContext.Compilation.ReferencedAssemblyNames)
                        missingReferences.Remove(r.Name);
                    if (missingReferences.Count > 0)
                    {
                        codeGenerationContext.diagnostic.LogError(
                            $"Assembly {executionContext.Compilation.AssemblyName} contains NetCode replicated types. The serialization code will use " +
                            $"burst, collections, mathematics and network data streams but the assembly does not have references to: {string.Join(",", missingReferences)}. " +
                            $"Please add the missing references in the asmdef for {executionContext.Compilation.AssemblyName}.");
                    }
                }
            }
            AddGeneratedSources(executionContext, codeGenerationContext);
        }

        private static void ImportTemplates(GeneratorExecutionContext executionContext, IDiagnosticReporter diagnostic,
            out TemplateRegistry templateRegistry)
        {
            HashSet<string> generatorTemplates = new()
            {
                CodeGenerator.RpcSerializer,
                CodeGenerator.CommandSerializer,
                CodeGenerator.ComponentSerializer,
                CodeGenerator.RegistrationSystem,
                CodeGenerator.InputSynchronization,
                CodeGenerator.GhostFixedListElement,
                CodeGenerator.GhostFixedListContainer,
                CodeGenerator.GhostFixedListCommandHelper,
                CodeGenerator.GhostFixedListSnapshotHelpers,
            };
            List<TypeRegistryEntry> allFieldTemplates = new List<TypeRegistryEntry>(DefaultTypes.Registry);
            using (new Profiler.Auto("LoadRegistryAndOverrides"))
            {
                allFieldTemplates.AddRange(UserDefinedTemplateRegistryParser.ParseTemplates(executionContext, diagnostic));
            }
            //Additional files always provides the extra templates in 2021.2 and newer. The templates files must end with .netcode.additionalfile extensions.
            templateRegistry = new TemplateRegistry(diagnostic);
            templateRegistry.AddTypeTemplates(allFieldTemplates);
            templateRegistry.AddAdditionalTemplates(executionContext.AdditionalFiles, allFieldTemplates, generatorTemplates);
        }

        /// <summary>
        /// Map ambigous syntax nodes to code-generation type candidates.
        /// </summary>
        /// <param name="executionContext"></param>
        /// <param name="receiver"></param>
        /// <param name="diagnostic"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        private static Candidates ResolveCandidates(GeneratorExecutionContext executionContext, NetCodeSyntaxReceiver receiver, IDiagnosticReporter diagnostic)
        {
            var candidates = new Candidates
            {
                Components = new List<SyntaxNode>(),
                Rpcs = new List<SyntaxNode>(),
                Commands = new List<SyntaxNode>(),
                Inputs = new List<SyntaxNode>(),
                Variants = receiver.Variants
            };

            foreach (var candidate in receiver.Candidates)
            {
                executionContext.CancellationToken.ThrowIfCancellationRequested();

                var symbolModel = executionContext.Compilation.GetSemanticModel(candidate.SyntaxTree);
                var candidateSymbol = symbolModel.GetDeclaredSymbol(candidate) as ITypeSymbol;
                var allComponentTypes = Roslyn.Extensions.GetAllComponentType(candidateSymbol).ToArray();
                //No valid/known interfaces
                if (allComponentTypes.Length == 0)
                    continue;

                //The struct is implementing more than one valid interface. Report the error/warning and skip the code-generation
                if (allComponentTypes.Length > 1)
                {
                    diagnostic.LogError(
                        $"struct {Roslyn.Extensions.GetFullTypeName(candidateSymbol)} cannot implement {string.Join(",", allComponentTypes)} interfaces at the same time",
                        candidateSymbol?.Locations[0]);
                    continue;
                }
                switch (allComponentTypes[0])
                {
                    case ComponentType.Unknown:
                        break;
                    case ComponentType.Component:
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.HybridComponent:
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.Buffer:
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.Rpc:
                        candidates.Rpcs.Add(candidate);
                        break;
                    case ComponentType.CommandData:
                        candidates.Commands.Add(candidate);
                        candidates.Components.Add(candidate);
                        break;
                    case ComponentType.Input:
                        candidates.Inputs.Add(candidate);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            return candidates;
        }

        /// <summary>
        /// Add the generated source files to the current compilation and flush everything on disk (if enabled)
        /// </summary>
        /// <param name="executionContext"></param>
        /// <param name="codeGenContext"></param>
        private static void AddGeneratedSources(GeneratorExecutionContext executionContext, CodeGenerator.Context codeGenContext)
        {
            using (new Profiler.Auto("WriteFile"))
            {
                executionContext.CancellationToken.ThrowIfCancellationRequested();
                //Always delete all the previously generated files
                if (Helpers.CanWriteFiles)
                {
                    var outputFolder = Path.Combine(Helpers.GetOutputPath(), $"{executionContext.Compilation.AssemblyName}");
                    if(Directory.Exists(outputFolder))
                        Directory.Delete(outputFolder, true);
                    if(codeGenContext.batch.Count != 0)
                        Directory.CreateDirectory(outputFolder);
                }
                if (codeGenContext.batch.Count == 0)
                    return;

                foreach (var nameAndSource in codeGenContext.batch)
                {
                    executionContext.CancellationToken.ThrowIfCancellationRequested();
                    var sourceText = SourceText.From(nameAndSource.Code, System.Text.Encoding.UTF8);
                    var sourcePath = Path.Combine($"{executionContext.Compilation.AssemblyName}",
                        nameAndSource.GeneratedFileName);
                    //var hintName = Utilities.TypeHash.FNV1A64(sourcePath).ToString();
                    //With the new version of roslyn, is necessary to add to the generate file
                    //a first line with #line1 "sourcecodefullpath" so that when debugging the right
                    //file is used. IMPORTANT: the #line directive should be not in the file you save on
                    //disk to correct match the debugging line
                    sourcePath = Path.Combine(Helpers.GetOutputPath(), sourcePath);
                    var source = sourceText.WithInitialLineDirective(sourcePath);
                    Debug.LogInfo($"output {nameAndSource.GeneratedFileName} to {sourcePath}");
                    try
                    {
                        if (Helpers.CanWriteFiles)
                            File.WriteAllText(sourcePath, source.ToString());
                    }
                    catch (System.Exception e)
                    {
                        //In the rare event/occasion when this happen, at the very least don't bother the user and move forward
                        Debug.LogWarning($"cannot write file {Path.Combine(Helpers.GetOutputPath(), sourcePath)}. An exception has been thrown:{e}");
                    }
                    //var hintName = Utilities.TypeHash.FNV1A64(sourcePath).ToString();
                    executionContext.AddSource(nameAndSource.GeneratedFileName, source);

                }
            }
        }
    }
}
