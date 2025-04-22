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
            foreach (var syntaxNode in inputCandidates)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                Profiler.End();
                var candidateSymbol = model.GetDeclaredSymbol(syntaxNode) as INamedTypeSymbol;
                if (candidateSymbol == null)
                    continue;
                codeGenContext.ResetState();
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                if (typeInfo == null)
                    continue;
                NameUtils.UpdateNameAndNamespace(typeInfo,  ref codeGenContext, candidateSymbol);
                // If the serializer type already exist we can just skip generation
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetSyncInputName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogDebug($"Skipping code-gen for {codeGenContext.generatorName} because a command data wrapper for it exists already");
                    continue;
                }
                codeGenContext.types.Add(typeInfo);
                codeGenContext.diagnostic.LogInfo($"Generating command data wrapper for ${typeInfo.TypeFullName}");
                CodeGenerator.GenerateCommand(codeGenContext, typeInfo, CommandSerializer.Type.Input);
            }
        }
        static private string GetSyncInputName(CodeGenerator.Context context)
        {
            return $"{context.generatorName.Replace(".", "").Replace('+', '_')}InputBufferData";
        }
    }
}
