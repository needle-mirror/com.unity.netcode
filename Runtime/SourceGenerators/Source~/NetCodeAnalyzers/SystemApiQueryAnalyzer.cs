using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NetCodeAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class SystemApiQueryAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(NetcodeDiagnostics.k_NetC0001Descriptor);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeSystemApiInvocationExpression, SyntaxKind.InvocationExpression);
        }

        private void AnalyzeSystemApiInvocationExpression(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            if (!IsSystemApiQuery(invocation))
                return;

            AnalyzeNetC0001(context, invocation);
        }

        private void AnalyzeNetC0001(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
        {
            if (!EntitiesHelpers.IsInvocationInPredictionSystemGroup(invocation, context))
                return;

            var methodChain = GetMethodChain(invocation);

            bool hasSimulate = false;
            bool hasIgnoreComponentEnabledState = false;
            Location ignoreLocation = invocation.GetLocation();

            foreach (var methodCall in methodChain)
            {
                if (EntitiesHelpers.IsWithSimulate(methodCall, context, true))
                {
                    hasSimulate = true;
                }

                if (EntitiesHelpers.IsWithOptionsIgnoreEnabled(methodCall, ref ignoreLocation))
                {
                    hasIgnoreComponentEnabledState = true;
                }
            }

            if (hasSimulate && hasIgnoreComponentEnabledState)
            {
                context.ReportDiagnostic(Diagnostic.Create(NetcodeDiagnostics.k_NetC0001Descriptor, ignoreLocation));
            }
        }

        private bool IsSystemApiQuery(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.ValueText == "Query" &&
                memberAccess.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.ValueText == "SystemAPI")
            {
                return true;
            }

            return false;
        }

        private List<InvocationExpressionSyntax> GetMethodChain(InvocationExpressionSyntax startInvocation)
        {
            var chain = new List<InvocationExpressionSyntax>();
            var current = startInvocation;

            chain.Add(current);

            // Walk up the chain to find all method calls
            while (current.Parent is MemberAccessExpressionSyntax parentMember &&
                parentMember.Parent is InvocationExpressionSyntax parentInvocation)
            {
                chain.Add(parentInvocation);
                current = parentInvocation;
            }

            return chain;
        }
    }
}
