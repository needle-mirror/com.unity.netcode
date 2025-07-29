using Microsoft.CodeAnalysis;

namespace NetCodeAnalyzer
{
    public static class NetcodeDiagnostics
    {
        public const string ID_NETC0001 = "NETC0001";
        public static readonly DiagnosticDescriptor k_NetC0001Descriptor
            = new DiagnosticDescriptor(ID_NETC0001, "Ignore Component Enabled State with Simulate",
                "You are ignoring the enabled state of components in a query that includes the Simulate component. This can lead to unexpected behavior in NetCode systems, such as mispredictions as predictions are run based on the enabled state of Simulate.",
                "EntityQuery", DiagnosticSeverity.Warning, true);
    }
}
