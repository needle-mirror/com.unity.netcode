using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Unity.Netcode.Generators
{
    internal class RemotesFactory
    {
        /// <summary>
        /// Collect and generate remotes.
        /// </summary>
        /// <param name="remotesCandidates"></param>
        /// <param name="compilation"></param>
        /// <param name="codeGenContext"></param>
        public static void Generate(IReadOnlyList<SyntaxNode> remotesCandidates, CodeGenerator.Context codeGenContext)
        {
            GenerateRemotes(remotesCandidates, codeGenContext);
            GenerateRemotesRegistrationSystem(remotesCandidates, codeGenContext);
        }

        private static void GenerateRemotes(IReadOnlyList<SyntaxNode> remotesCandidates, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Commands);
            var rootNamespace = codeGenContext.generatedNs;
            foreach (var syntaxNode in remotesCandidates)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree);
                Profiler.End();

                // Allow us to generate for methods too
                if (model.GetDeclaredSymbol(syntaxNode) is not INamedTypeSymbol && model.GetDeclaredSymbol(syntaxNode as BaseMethodDeclarationSyntax) is not IMethodSymbol)
                    continue;

                TypeInformation typeInfo = null;

                var candidateTypeSymbol = model.GetDeclaredSymbol(syntaxNode) as INamedTypeSymbol;

                // Remote can be either on a type or on a method
                if ( candidateTypeSymbol != null )
                {
                    var disableCommandCodeGen = Roslyn.Extensions.GetAttribute(candidateTypeSymbol,
                    "Unity.Netcode", "NetcodeDisableCommandCodeGenAttribute");
                    if (disableCommandCodeGen != null)
                        continue;

                    codeGenContext.ResetState();
                    codeGenContext.RemotesContext.originalRemoteComponentTypeName = Roslyn.Extensions.GetTypeNameWithDeclaringTypename(candidateTypeSymbol);
                    codeGenContext.RemotesContext.BackingRemoteComponentName = codeGenContext.RemotesContext.originalRemoteComponentTypeName;
                    typeInfo = typeBuilder.BuildTypeInformation(candidateTypeSymbol, null);
                }

                IMethodSymbol candidateMethodSymbol = null;
                if (typeInfo == null)
                {
                    candidateMethodSymbol = model.GetDeclaredSymbol(syntaxNode as BaseMethodDeclarationSyntax) as IMethodSymbol;

                    if (candidateMethodSymbol != null)
                    {
                        candidateTypeSymbol = candidateMethodSymbol.ContainingType;
                        codeGenContext.ResetState();

                        // generated backing RPC component
                        codeGenContext.RemotesContext.BackingRemoteComponentName = NameUtils.GetRemoteGeneratedTypeName(candidateTypeSymbol, candidateMethodSymbol);
                        codeGenContext.RemotesContext.originalRemoteComponentTypeName = null;
                        typeInfo = typeBuilder.BuildRemoteMethodTypeInformation(codeGenContext,candidateMethodSymbol, null);
                    }
                }

                if (typeInfo == null)
                    continue;

                codeGenContext.types.Add(typeInfo);
                codeGenContext.diagnostic.LogInfo($"Generating remote for ${typeInfo.TypeFullName}");
                NameUtils.UpdateNameAndNamespace(ref typeInfo, ref codeGenContext, candidateTypeSymbol, candidateMethodSymbol);
                
                CodeGenerator.GenerateRemote(codeGenContext, typeInfo);
            }

            codeGenContext.generatedNs = rootNamespace;
        }

        private static void GenerateRemotesRegistrationSystem(IReadOnlyList<SyntaxNode> remotesCandidates, CodeGenerator.Context context)
        {
            //There is nothing to generate in that case. Skip creating an empty system
            if (context.types.Count == 0 || !ContainsRemotesTypes(context.types))
                return;

            using (new Profiler.Auto("GenerateRemotesRegistrationSystem"))
            {
                //Generate the ghost registration
                var registrationSystemCodeGen = context.codeGenCache.GetTemplate(CodeGenerator.RemotesRegistrationSystem);
                registrationSystemCodeGen = registrationSystemCodeGen.Clone();
                var replacements = new Dictionary<string, string>(16);

                foreach (var t in context.types)
                {
                    if (t.IsAutoInvokeRemote)
                    {
                        replacements["REMOTES_NAME"] = t.TypeFullName;
                        registrationSystemCodeGen.GenerateFragment("REMTOES_AUTO_INVOKE_LIST", replacements);
                    }
                }

                if (context.generatedNs != null)
                {
                    replacements.Clear();
                    replacements["REMOTES_USING"] = context.generatedNs;
                    registrationSystemCodeGen.GenerateFragment("REMOTES_USING_STATEMENT", replacements);
                }

                replacements.Clear();
                replacements.Add("REMOTES_NAMESPACE", context.generatedNs != null ? context.generatedNs : "Unity.Netcode.Generated.Remotes");

                registrationSystemCodeGen.GenerateFile("RemotesAutoInvokeCollection.cs", replacements, context.batch);
            }
        }

        private static bool ContainsRemotesTypes(List<TypeInformation> types)
        {
            foreach (var t in types)
            {
                if ( t.IsAutoInvokeRemote )
                {
                    return true;
                }
            }

            return false;
        }
    }
}
