using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using System.Linq;
using System;

namespace Unity.NetCode.Generators
{
    internal class CommandFactory
    {
        /// <summary>
        /// Collect and generate commands serialization.
        /// </summary>
        /// <param name="commandCandidates"></param>
        /// <param name="context"></param>
        /// <param name="codeGenContext"></param>
        public static void Generate(IReadOnlyList<SyntaxNode> commandCandidates, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            var rootNamespace = codeGenContext.generatedNs;

            foreach (var syntaxNode in commandCandidates)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                Profiler.End();
                var candidateSymbol = model.GetDeclaredSymbol(syntaxNode) as INamedTypeSymbol;
                if (candidateSymbol == null)
                    continue;

                var disableCommandCodeGen = Roslyn.Extensions.GetAttribute(candidateSymbol,
                    "Unity.NetCode", "NetCodeDisableCommandCodeGenAttribute");
                if (disableCommandCodeGen != null)
                    continue;
                // If the serializer type already exist we can just skip generation
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetCommandSerializerName(candidateSymbol)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {candidateSymbol.Name} because a command serializer for it already exists");
                    continue;
                }

                var typeNamespace = Roslyn.Extensions.GetFullyQualifiedNamespace(candidateSymbol);
                if(typeNamespace.StartsWith("__COMMAND", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("__GHOST", StringComparison.Ordinal))
                {
                    codeGenContext.diagnostic.LogError($"Invalid namespace {typeNamespace} for {candidateSymbol.Name}. __GHOST and __COMMAND are reserved prefixes and cannot be used in namspace, type and field names",
                        syntaxNode.GetLocation());
                    continue;
                }
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                if (typeInfo == null)
                    continue;

                NameUtils.UpdateNameAndNamespace(ref typeInfo, rootNamespace, ref codeGenContext, ref candidateSymbol);
                codeGenContext.diagnostic.LogInfo($"Generating command for {typeInfo.TypeFullName}");
                codeGenContext.types.Add(typeInfo);
                codeGenContext.ResetState();
                CodeGenerator.GenerateCommand(codeGenContext, typeInfo, CommandSerializer.Type.Command);
            }
            codeGenContext.generatedNs = rootNamespace;
        }
        static private string GetCommandSerializerName(INamedTypeSymbol symbol)
        {
            return $"{symbol.Name}Serializer";
        }
    }
}
