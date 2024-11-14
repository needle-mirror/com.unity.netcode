using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// Parse the UserDefinedTemplate.RegisterTemplates partial method implementation and build a list of templates
    /// entry used later to generate the type serialization.
    /// String interpolation, like $"{TemplatePath}TheTemplate" or with more params supported.
    /// </summary>
    internal struct UserDefinedTemplateRegistryParser
    {
        public static List<TypeRegistryEntry> ParseTemplates(GeneratorExecutionContext context, IDiagnosticReporter reporter)
        {
            var templates = new List<TypeRegistryEntry>();
            //This is only true for NetCode assembly. All the other don't have any symbols (but only metadata refs)
            var symbol = context.Compilation.GetSymbolsWithName("UserDefinedTemplates").FirstOrDefault();
            if (symbol != null)
            {
                foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var method = syntaxRef.GetSyntax().DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m => m.Identifier.ToString() == "RegisterTemplates");

                    //Get ther right reference (the one with the body)
                    if (method?.Body != null && method.Body.Statements.Count > 0)
                    {
                        ParseMethod(context, method, templates, reporter);
                        break;
                    }
                }
            }
            else
            {
                ParseTemplatesFromMetadata(context, templates);
            }
            return templates;
        }

        static private void ParseTemplatesFromMetadata(GeneratorExecutionContext context, IList<TypeRegistryEntry> templates)
        {
            string netCode = null;
            netCode = context.Compilation.ExternalReferences.FirstOrDefault(r =>
            {
                return r.Properties.Kind == MetadataImageKind.Assembly &&
                       r.Display != null && r.Display.EndsWith("Unity.NetCode.dll", StringComparison.Ordinal);
            })?.Display;
            if (netCode == null)
            {
                var netCodeRef = context.Compilation.ExternalReferences.FirstOrDefault(r =>
                {
                    return r.Properties.Kind == MetadataImageKind.Assembly &&
                           r.Display != null && r.Display.EndsWith("Unity.NetCode.ref.dll", StringComparison.Ordinal);
                })?.Display;
                if (netCodeRef != null)
                    netCode = Path.Combine(Path.GetDirectoryName(netCodeRef), "Unity.NetCode.dll");
            }
            if (netCode == null)
                throw new InvalidOperationException($"Cannot find Unity.NetCode metadata reference for assembly {context.Compilation.AssemblyName}");

            //The dlls must be loaded in the main execution context since we need to execute the constructor code
            var bytes = File.ReadAllBytes(netCode);
            var assembly = Assembly.Load(bytes);
            var type = assembly.GetType("Unity.NetCode.Generators.UserDefinedTemplates");
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            var tmpl = type.GetField("Templates", BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            foreach (var l in (IList)tmpl)
            {
                TypeRegistryEntry e = new TypeRegistryEntry
                {
                    Composite = (bool)l.GetType().GetField("Composite").GetValue(l),
                    Quantized = (bool)l.GetType().GetField("Quantized").GetValue(l),
                    SupportCommand = (bool)l.GetType().GetField("SupportCommand").GetValue(l),
                    Smoothing = (SmoothingAction)l.GetType().GetField("Smoothing").GetValue(l),
                    Template = (string)l.GetType().GetField("Template").GetValue(l),
                    TemplateOverride = (string)l.GetType().GetField("TemplateOverride").GetValue(l),
                    SubType = (int)l.GetType().GetField("SubType").GetValue(l),
                    Type = (string)l.GetType().GetField("Type").GetValue(l),
                };
                templates.Add(e);
            }
        }

        static private void ParseMethod(GeneratorExecutionContext context, MethodDeclarationSyntax method, List<TypeRegistryEntry> templates, IDiagnosticReporter reporter)
        {
            var model = context.Compilation.GetSemanticModel(method.SyntaxTree);

            var entryType = typeof(TypeRegistryEntry);
            if (method.Body != null)
                foreach (var s in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var templatesList = s.ArgumentList.Arguments[0].Expression
                        .DescendantNodes()
                        .OfType<ObjectCreationExpressionSyntax>().ToArray();
                    foreach (var template in templatesList)
                    {
                        var entry = new TypeRegistryEntry();
                        if (template.Initializer != null)
                        {
                            foreach (var e in template.Initializer.Expressions)
                            {
                                if (e is AssignmentExpressionSyntax assignment)
                                {
                                    var field = ((IdentifierNameSyntax) assignment.Left).Identifier;
                                    if (assignment.Right.IsKind(SyntaxKind.InterpolatedStringExpression))
                                    {
                                        var text = ResolveInterpolatedString(
                                            assignment.Right as InterpolatedStringExpressionSyntax, model);
                                        entryType.GetField(field.Text).SetValue(entry, text);
                                    }
                                    else
                                    {
                                        var text = model.GetConstantValue(assignment.Right);
                                        entryType.GetField(field.Text).SetValue(entry, text.Value);
                                    }
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(entry.Type))
                        {
                            reporter.LogError($"UserDefinedTemplate '{method.Identifier.SyntaxTree?.FilePath}' defines a `TypeRegistryEntry` with a missing `Type`. Cannot add it to the list of Templates. [{entry}]!");
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(entry.Template))
                        {
                            reporter.LogError($"UserDefinedTemplate '{method.Identifier.SyntaxTree?.FilePath}' defines a `TypeRegistryEntry` (Type: {entry.Type}) with a missing `Template` path. Cannot add it to the list of Templates. [{entry}]!");
                            continue;
                        }
                        templates.Add(entry);
                    }
                }
        }

        // Resolve and return the interpolated string Don't support super complex interpolation, like with function or expression,
        // but only the one witch use variables.
        private static string ResolveInterpolatedString(InterpolatedStringExpressionSyntax interpolatedExpression, SemanticModel model)
        {
            var stringBuilder = new StringBuilder();
            foreach(var content in interpolatedExpression.Contents)
            {
                if (content.Kind() == SyntaxKind.Interpolation)
                {
                    var symbolInfo = model.GetSymbolInfo(((InterpolationSyntax) content).Expression);
                    //Just play safe here but report an error just in case.
                    if (symbolInfo.Symbol != null)
                    {
                        //assuming one declaration here
                        var fieldDeclaration = symbolInfo.Symbol.DeclaringSyntaxReferences[0].GetSyntax() as VariableDeclaratorSyntax;
                        if (fieldDeclaration != null)
                        {
                            stringBuilder.Append((fieldDeclaration.Initializer?
                                    .Value as LiteralExpressionSyntax)?.Token.ValueText);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Cannot resolve field declaration: {symbolInfo.ToString()}");
                    }
                }
                else if (content.Kind() == SyntaxKind.InterpolatedStringText)
                {
                    stringBuilder.Append(((InterpolatedStringTextSyntax) content).TextToken.ValueText);
                }
            }
            return stringBuilder.ToString();
        }
    }
}
