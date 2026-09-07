using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.Netcode.Roslyn;

namespace Unity.Netcode.Generators
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

                if (model.GetDeclaredSymbol(syntaxNode) is not INamedTypeSymbol && model.GetDeclaredSymbol(syntaxNode as BaseMethodDeclarationSyntax) is not IMethodSymbol)
                    continue;

                TypeInformation typeInfo = null;

                var candidateSymbol = model.GetDeclaredSymbol(syntaxNode) as INamedTypeSymbol;

                if (candidateSymbol != null)
                {
                    var disableCommandCodeGen = Roslyn.Extensions.GetAttribute(candidateSymbol,
                    "Unity.Netcode", "NetcodeDisableCommandCodeGenAttribute");
                    if (disableCommandCodeGen != null)
                        continue;
                    // If the serializer type already exist we can just skip generation
                    if (candidateSymbol.ImplementsGenericInterface("Unity.Netcode.IRpcCommandSerializer"))
                    {
                        codeGenContext.diagnostic.LogInfo($"Skipping code-gen for {candidateSymbol.Name} because an IRpcCommandSerializer for it already exists");
                        continue;
                    }

                    codeGenContext.ResetState();
                    codeGenContext.generatorName = Roslyn.Extensions.GetTypeNameWithDeclaringTypename(candidateSymbol);
                    typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, null);
                }

                IMethodSymbol candidateMethodSymbol = null;
                if (typeInfo == null)
                {
                    candidateMethodSymbol = model.GetDeclaredSymbol(syntaxNode as BaseMethodDeclarationSyntax) as IMethodSymbol;

                    if (candidateMethodSymbol != null)
                    {
                        candidateSymbol = candidateMethodSymbol.ContainingType;
                        codeGenContext.ResetState();
                        //codeGenContext.generatorName = RemotesFactory.GetRemoteMethodTypeName(candidateMethodSymbol);

                        typeInfo = typeBuilder.BuildRemoteMethodTypeInformation(codeGenContext, candidateMethodSymbol, null);
                    }
                }

                if (typeInfo == null)
                    continue;
                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(ref typeInfo, ref codeGenContext, candidateSymbol,candidateMethodSymbol);
                // If the serializer type already exist we can just skip generation
                if (new List<ISymbol>(codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetRpcSerializerName(codeGenContext))).Count > 0)
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
