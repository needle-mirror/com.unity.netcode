using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using System.Linq;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    internal class RpcFactory
    {
        /// <summary>
        /// Collect and generate rpcs serialization.
        /// </summary>
        /// <param name="rpcCandidates"></param>
        /// <param name="compilation"></param>
        /// <param name="codeGenContext"></param>
        public static void Generate(IReadOnlyList<SyntaxNode> rpcCandidates, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            foreach (var syntaxNode in rpcCandidates)
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
                if (candidateSymbol.ImplementsGenericInterface("Unity.NetCode.IRpcCommandSerializer"))
                {
                    codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {candidateSymbol.Name} because an IRpcCommandSerializer for it already exists");
                    continue;
                }
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                if (typeInfo == null)
                    continue;
                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(typeInfo, ref codeGenContext, candidateSymbol);
                // If the serializer type already exist we can just skip generation
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetRpcSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {codeGenContext.generatorName} because an rpc serializer for it already exists");
                    continue;
                }

                codeGenContext.types.Add(typeInfo);
                codeGenContext.diagnostic.LogInfo($"Generating rpc for ${typeInfo.TypeFullName}");
                CodeGenerator.GenerateCommand(codeGenContext, typeInfo, CommandSerializer.Type.Rpc);
            }
        }
        static private string GetRpcSerializerName(CodeGenerator.Context context)
        {
            return $"{context.generatorName.Replace(".", "").Replace('+', '_')}Serializer";
        }
    }
}
