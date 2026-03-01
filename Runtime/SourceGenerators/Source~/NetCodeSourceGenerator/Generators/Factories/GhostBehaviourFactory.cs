using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Unity.NetCode.Generators;

internal class GhostBehaviourFactory
{
    public static void Generate(IReadOnlyList<SyntaxNode> ghostBehaviourCandidates, CodeGenerator.Context codeGenContext, GeneratorExecutionContext executionContext)
    {
        var netVarType = codeGenContext.executionContext.Compilation.GetTypeByMetadataName("Unity.NetCode.GhostField`1");
        var netVarBridgeType = codeGenContext.executionContext.Compilation.GetTypeByMetadataName("Unity.NetCode.GhostComponentRef`1");
        var inputType = codeGenContext.executionContext.Compilation.GetTypeByMetadataName("Unity.NetCode.IInputComponentData");

        foreach (var syntaxNode in ghostBehaviourCandidates)
        {
            var model = codeGenContext.executionContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree);
            CodeGenerator.GenerateGhostBehaviour(codeGenContext, syntaxNode, model, netVarType, netVarBridgeType, inputType);
        }
    }
}
