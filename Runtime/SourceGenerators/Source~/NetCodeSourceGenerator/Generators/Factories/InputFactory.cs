using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using System.Linq;

namespace Unity.NetCode.Generators
{
    internal class InputFactory
    {
        public static void Generate(IReadOnlyList<SyntaxNode> inputCandidates, CodeGenerator.Context codeGenContext, GeneratorExecutionContext executionContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            var rootNamespace = codeGenContext.generatedNs;
            foreach (var syntaxNode in inputCandidates)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                Profiler.End();
                var candidateSymbol = model.GetDeclaredSymbol(syntaxNode) as INamedTypeSymbol;
                if (candidateSymbol == null)
                    continue;
                // If the serializer type already exist we can just skip generation
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetSyncInputName(candidateSymbol)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogDebug($"Skipping code-gen for {candidateSymbol.Name} because a command data wrapper for it exists already");
                    continue;
                }

                codeGenContext.ResetState();
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                NameUtils.UpdateNameAndNamespace(ref typeInfo, rootNamespace, ref codeGenContext, ref candidateSymbol);
                if (typeInfo == null)
                    continue;
                codeGenContext.types.Add(typeInfo);
                codeGenContext.diagnostic.LogInfo($"Generating command data wrapper for ${typeInfo.TypeFullName}");
                CodeGenerator.GenerateCommand(codeGenContext, typeInfo, CommandSerializer.Type.Input);
            }

            codeGenContext.generatedNs = rootNamespace;
        }
        static private string GetSyncInputName(INamedTypeSymbol symbol)
        {
            return $"{symbol.Name}InputBufferData";
        }
    }
}
