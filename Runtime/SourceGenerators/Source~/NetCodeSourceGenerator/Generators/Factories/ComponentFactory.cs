using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.NetCode.Roslyn;

namespace Unity.NetCode.Generators
{
    internal class ComponentFactory
    {
        /// <summary>
        /// Collect and generate component serialization. Is also responsible to generate the registration system.
        /// </summary>
        /// <param name="variantsCandidates"></param>
        /// <param name="context"></param>
        /// <param name="codeGenContext"></param>
        /// <param name="componentsCandidates"></param>
        public static void Generate(
            IReadOnlyList<SyntaxNode> componentsCandidates,
            IReadOnlyList<SyntaxNode> variantsCandidates,
            CodeGenerator.Context codeGenContext)
        {
            GenerateComponents(componentsCandidates, codeGenContext);
            GenerateVariants(variantsCandidates, codeGenContext);
            CodeGenerator.GenerateRegistrationSystem(codeGenContext);
        }

        private static void GenerateComponents(IEnumerable<SyntaxNode> components, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext, TypeInformationBuilder.SerializationMode.Component);
            foreach (var componentCandidate in components)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();

                var syntaxNode = componentCandidate as TypeDeclarationSyntax;
                var hasGhostEnabledBitAttribute = HasGhostEnabledBitAttribute(syntaxNode);
                var hasGhostFields = HasGhostFields(syntaxNode);

                // Warning! These only work if the attribute is not inherited (thus they cannot be inherited from).
                if (!HasGhostComponentAttribute(syntaxNode) && !hasGhostFields && !hasGhostEnabledBitAttribute)
                    continue;

                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(componentCandidate.SyntaxTree);
                var candidateSymbol = model.GetDeclaredSymbol(componentCandidate) as INamedTypeSymbol;
                Profiler.End();
                if (candidateSymbol == null)
                {
                    codeGenContext.diagnostic.LogError($"No INamedTypeSymbol for componentCandidate '{componentCandidate.ToFullString()}'.", syntaxNode.GetLocation());
                    continue;
                }

                var typeNamespace = Roslyn.Extensions.GetFullyQualifiedNamespace(candidateSymbol);
                if (typeNamespace.StartsWith("__COMMAND", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("__GHOST", StringComparison.Ordinal))
                {
                    codeGenContext.diagnostic.LogError($"Invalid namespace {typeNamespace} for {candidateSymbol.Name}. __GHOST and __COMMAND are reserved prefixes and cannot be used in namspace, type and field names",
                        syntaxNode.GetLocation());
                    continue;
                }

                var ghostComponent = TryGetGhostComponent(candidateSymbol);
                var typeInfo = typeBuilder.BuildTypeInformation(candidateSymbol, ghostComponent);
                if (typeInfo == null)
                    continue;

                //This is an error for buffers and commands that require serialization. Is handled later, outside, that way
                //we report first all the errors and then skip the type.
                if (typeBuilder.MissingGhostFields.Count > 0)
                {
                    // These need to be fully annotated  or not at all. So it's ok all fields have missing
                    // annotations (normal CommandData or buffer with ghost component annotation) but not ok
                    // if one is already present (remote player command buffer sync or just a normal dynamic buffer)
                    if ((typeInfo.ComponentType == ComponentType.Buffer || typeInfo.ComponentType == ComponentType.CommandData) &&
                        typeInfo.GhostFields.Count > 0)
                    {
                        foreach (var field in typeBuilder.MissingGhostFields)
                            codeGenContext.diagnostic.LogError(
                                $"GhostField missing on field {field}. Buffers must have all fields annotated. CommandData must have none, for normal client to server command stream, or all, as a normal stream and also as a buffer sent from server to other (non-owner) clients.",
                                componentCandidate.GetLocation());
                        typeBuilder.MissingGhostFields.Clear();
                        continue;
                    }
                    typeBuilder.MissingGhostFields.Clear();
                }

                var variantHash = Helpers.ComputeVariantHash(typeInfo.Symbol, typeInfo.Symbol);
                var isSerialized = hasGhostFields || typeInfo.ShouldSerializeEnabledBit;
                codeGenContext.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                {
                    TypeInfo = typeInfo,
                    VariantTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                    ComponentTypeName = typeInfo.TypeFullName.Replace('+', '.'),
                    Hash = variantHash.ToString(),
                    GhostAttribute = ghostComponent,
                    IsSerialized = isSerialized,
                });

                if (!isSerialized)
                    continue;

                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(typeInfo, ref codeGenContext, candidateSymbol);

                // If the serializer type already exist we can just skip generation
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetGhostSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogDebug($"Skipping code-gen for {candidateSymbol.Name} because a component serializer for it already exists");
                    continue;
                }

                codeGenContext.diagnostic.LogInfo($"Generating ghost for {typeInfo.TypeFullName}");
                codeGenContext.types.Add(typeInfo);
                CodeGenerator.GenerateGhost(codeGenContext, typeInfo);
            }
        }

        private static void GenerateVariants(IEnumerable<SyntaxNode> variants, CodeGenerator.Context codeGenContext)
        {
            var typeBuilder = new TypeInformationBuilder(codeGenContext.diagnostic, codeGenContext.executionContext,
                TypeInformationBuilder.SerializationMode.Component);

            foreach (var componentCandidate in variants)
            {
                codeGenContext.executionContext.CancellationToken.ThrowIfCancellationRequested();
                Profiler.Begin("GetSemanticModel");
                var model = codeGenContext.executionContext.Compilation.GetSemanticModel(componentCandidate.SyntaxTree);
                var variantSymbol = model.GetDeclaredSymbol(componentCandidate) as INamedTypeSymbol;
                Profiler.End();
                if (variantSymbol == null)
                    continue;

                var syntaxNode = componentCandidate as TypeDeclarationSyntax;
                var ghostComponent = TryGetGhostComponent(variantSymbol);
                var variation = Roslyn.Extensions.GetAttribute(variantSymbol, "Unity.NetCode", "GhostComponentVariationAttribute");
                var variantTypeInfo = typeBuilder.BuildVariantTypeInformation(variantSymbol, variation, ghostComponent);
                if (variantTypeInfo == null)
                    continue;

                var variantHash = Helpers.ComputeVariantHash(variantSymbol, (ITypeSymbol) variation.ConstructorArguments[0].Value);
                var hasGhostFields = variantTypeInfo.GhostFields.Count != 0;
                var displayName = variation.ConstructorArguments[1].Value;
                if (displayName is not string name || string.IsNullOrWhiteSpace(name))
                    displayName = default;

                var isSerialized = hasGhostFields || variantTypeInfo.ShouldSerializeEnabledBit;
                codeGenContext.serializationStrategies.Add(new CodeGenerator.Context.SerializationStrategyCodeGen
                {
                    TypeInfo = variantTypeInfo,
                    DisplayName = (string)displayName,
                    VariantTypeName = Roslyn.Extensions.GetFullTypeName(variantSymbol).Replace('+', '.'),
                    ComponentTypeName = variantTypeInfo.TypeFullName.Replace('+', '.'),
                    Hash = variantHash.ToString(),
                    GhostAttribute = ghostComponent,
                    IsSerialized = isSerialized,
                });

                if (!isSerialized)
                    continue;

                //This is an error for buffers and commands that require serialization. Is handled later, outside, that way
                //we report first all the errors and then skip the type.
                if (variantTypeInfo.ComponentType == ComponentType.Buffer)
                {
                    if (typeBuilder.MissingGhostFields.Count > 0)
                    {
                        foreach (var field in typeBuilder.MissingGhostFields)
                            codeGenContext.diagnostic.LogError($"GhostField missing on field {field} on Variant {variantTypeInfo.TypeFullName}. Buffers or CommandData must have all fields annotated!",
                                syntaxNode.GetLocation());
                        typeBuilder.MissingGhostFields.Clear();
                        continue;
                    }
                }

                codeGenContext.ResetState();
                NameUtils.UpdateNameAndNamespace(variantTypeInfo, ref codeGenContext, variantSymbol);
                // If the serializer type already exist we can just skip generation
                if (codeGenContext.executionContext.Compilation.GetSymbolsWithName(GetGhostSerializerName(codeGenContext)).FirstOrDefault() != null)
                {
                    codeGenContext.diagnostic.LogDebug($"Skipping code-gen for {codeGenContext.generatorName} because a variant component serializer for it already exists");
                    continue;
                }

                codeGenContext.types.Add(variantTypeInfo);
                codeGenContext.diagnostic.LogDebug($"Generating serializer for variant {variantSymbol.ToDisplayString()} for type {variantTypeInfo.TypeFullName}.");
                codeGenContext.variantTypeFullName = Roslyn.Extensions.GetFullTypeName(variantSymbol);
                codeGenContext.variantHash = variantHash;
                CodeGenerator.GenerateGhost(codeGenContext, variantTypeInfo);
            }
        }

        /// <summary>
        /// Fast early exit check to determine if we need to serialize a type.
        /// </summary>
        /// <returns></returns>
        static private bool HasGhostFields(TypeDeclarationSyntax structNode)
        {
            using (new Profiler.Auto("HasGhostFields"))
            {
                foreach (var t in structNode.Members
                    .SelectMany(attr => attr.AttributeLists, (attr, list) => list.Attributes)
                    .SelectMany(attributes => attributes))
                {
                    //Remove qualifiers if present
                    var name = t.Name is QualifiedNameSyntax syntax
                        ? syntax.Right.Identifier.ValueText
                        : t.Name.ToString();
                    if (name == "GhostField" || name == "GhostFieldAttribute")
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Fast early exit check to determine if we need to serialize a type.
        /// </summary>
        /// <returns></returns>
        static private bool HasGhostEnabledBitAttribute(TypeDeclarationSyntax structNode)
        {
            using (new Profiler.Auto("HasGhostEnabledBitAttribute"))
            {
                foreach (var t in structNode.AttributeLists
                             .SelectMany(list => list.Attributes))
                {
                    //Remove qualifiers if present
                    var name = t.Name is QualifiedNameSyntax syntax
                        ? syntax.Right.Identifier.ValueText
                        : t.Name.ToString();
                    if (name == "GhostEnabledBit" || name == "GhostEnabledBitAttribute")
                        return true;
                }
                return false;
            }
        }

        static internal bool HasGhostComponentAttribute(TypeDeclarationSyntax structNode)
        {
            using (new Profiler.Auto("HasGhostComponentAttribute"))
            {
                foreach (var t in structNode.AttributeLists
                    .SelectMany(list => list.Attributes))
                {
                    //Remove qualifiers if present
                    var name = t.Name is QualifiedNameSyntax syntax
                        ? syntax.Right.Identifier.ValueText
                        : t.Name.ToString();
                    if (name == "GhostComponent" || name == "GhostComponentAttribute")
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Check if a GhostComponentAttribute is present for the given symbol
        /// </summary>
        /// <returns></returns>
        static internal GhostComponentAttribute TryGetGhostComponent(ISymbol symbol)
        {
            using (new Profiler.Auto("TryGetGhostComponent"))
            {
                var attributeData = Roslyn.Extensions.GetAttribute(symbol, "Unity.NetCode", "GhostComponentAttribute");
                if (attributeData == null)
                    return default;
                var ghostAttribute = new GhostComponentAttribute();
                if (attributeData.NamedArguments.Length <= 0)
                    return ghostAttribute;
                var modifierType = typeof(GhostComponentAttribute);
                foreach (var t in attributeData.NamedArguments)
                    modifierType.GetField(t.Key)?.SetValue(ghostAttribute, t.Value.Value);

                return ghostAttribute;
            }
        }

        static private string GetGhostSerializerName(CodeGenerator.Context context)
        {
            return $"{context.generatorName.Replace(".", "").Replace('+', '_')}GhostComponentSerializer";
        }
    }
}
