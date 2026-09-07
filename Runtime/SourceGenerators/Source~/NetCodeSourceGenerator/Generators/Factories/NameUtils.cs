using Microsoft.CodeAnalysis;

namespace Unity.Netcode.Generators;

internal class NameUtils
{
    // Requirements:
    // - X.A and Y.A must generate non-conflicting code
    // - Unity.Netcode can contain X.A and Y.A and so A must be uniquely identifiable
    // - generated file name must not be too long for windows path restrictions
    // - must be easy to access from user code?
    // - must not conflict with subnamespaces (for example, Unity.Netcode.Generated.Unity.Netcode vs Unity.Netcode.X)
    // TODO codegen some accessors for those generated types, so users can use this. See samples' CustomChunkSerializer
    internal static void UpdateNameAndNamespace(ref TypeInformation typeInfo, ref CodeGenerator.Context codeGenContext, ITypeSymbol candidateTypeSymbol, IMethodSymbol methodSymbol = null)
    {
        var uniquePrefix = $"{codeGenContext.rootNs}";
        if (!string.IsNullOrEmpty(typeInfo.Namespace))
            uniquePrefix += $".{typeInfo.Namespace}";
        codeGenContext.generatedNs = $"{uniquePrefix.Replace(".", "_")}"; // replace needed so we have a namespace different from the original type and so C# doesn't try to find the original type within the generated namespace. Need the G since you can't have a namespace with only numbers
        // Roslyn nested types use '+' (metadata convention). Generated C# identifiers and file names must not contain '+'
        var typeName = Roslyn.Extensions.GetTypeNameWithDeclaringTypename(candidateTypeSymbol).Replace('+', '_');
        codeGenContext.generatorName = $"{codeGenContext.generatedNs}_{typeName}";
        codeGenContext.generatedFilePrefix = $"{Utilities.TypeHash.FNV1A64(uniquePrefix).ToString()}_{typeName}";
        if (methodSymbol != null)
        {
            codeGenContext.generatorName = GetRemoteGeneratedFullTypeName(codeGenContext.generatedNs, candidateTypeSymbol, methodSymbol);
            codeGenContext.generatedFilePrefix += "_" + methodSymbol.Name;
        }
    }

    internal static string GetRemoteGeneratedTypeName(ITypeSymbol typeSymbol, IMethodSymbol methodSymbol)
    {
        return $"{Roslyn.Extensions.GetTypeNameWithDeclaringTypename(typeSymbol)}_{methodSymbol.Name}_BackingRemote";
    }

    internal static string GetRemoteGeneratedFullTypeName(string namespaceString, ITypeSymbol typeSymbol, IMethodSymbol methodSymbol)
    {
        return $"{namespaceString}_{Roslyn.Extensions.GetTypeNameWithDeclaringTypename(typeSymbol)}_{methodSymbol.Name}_BackingRemote";
    }
}
