using Microsoft.CodeAnalysis;

namespace Unity.NetCode.Generators;

internal class NameUtils
{
    // Requirements:
    // - X.A and Y.A must generate non-conflicting code
    // - Unity.Netcode can contain X.A and Y.A and so A must be uniquely identifiable
    // - generated file name must not be too long for windows path restrictions
    // - must be easy to access from user code?
    // - must not conflict with subnamespaces (for example, Unity.Netcode.Generated.Unity.Netcode vs Unity.Netcode.X)
    // TODO codegen some accessors for those generated types, so users can use this. See samples' CustomChunkSerializer
    internal static void UpdateNameAndNamespace(TypeInformation typeInfo, ref CodeGenerator.Context codeGenContext, ITypeSymbol candidateSymbol)
    {
        var uniquePrefix = $"{codeGenContext.rootNs}";
        if (!string.IsNullOrEmpty(typeInfo.Namespace))
            uniquePrefix += $".{typeInfo.Namespace}";
        codeGenContext.generatedNs = $"{uniquePrefix.Replace(".", "_")}"; // replace needed so we have a namespace different from the original type and so C# doesn't try to find the original type within the generated namespace. Need the G since you can't have a namespace with only numbers
        var typeName = Roslyn.Extensions.GetTypeNameWithDeclaringTypename(candidateSymbol);
        codeGenContext.generatorName = $"{codeGenContext.generatedNs}_{typeName}";
        codeGenContext.generatedFilePrefix = $"{Utilities.TypeHash.FNV1A64(uniquePrefix).ToString()}_{typeName}";
    }
}
