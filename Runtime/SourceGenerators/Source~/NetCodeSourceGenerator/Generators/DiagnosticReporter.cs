using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    sealed class DiagnosticReporter : IDiagnosticReporter
    {
        readonly private GeneratorExecutionContext context;
        const int k_MaxMessageSize = 800; // unity has parsing issues with lines that are more than 1000 char long

        public DiagnosticReporter(GeneratorExecutionContext ctx)
        {
            context = ctx;
        }

        void LogInternalError(string message)
        {
            var noneLocation = Location.Create("Netcode Source Generator", new TextSpan(0, 0),
                new LinePositionSpan(LinePosition.Zero, LinePosition.Zero));
            var singleLineMessage = message.Replace("\n", "");
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticHelper.CreateErrorDescriptor(singleLineMessage.Substring(0, Math.Min(k_MaxMessageSize, singleLineMessage.Length))), noneLocation));
            Debug.LogError(message, noneLocation.ToString());
        }

        // Unity won't display messages whose line is bigger than a certain char count. This is to validate our own messages so that we don't reach that length
        string ValidateMessageLength(string message)
        {
            var truncatedMessage = message;
            if (message.Length > k_MaxMessageSize)
            {
                var internalErrorMessage =
                    "Internal error, message too long! The following messages will be truncated. Please enable log file dumping to get the full log. Stacktrace: ";
                var st = new System.Diagnostics.StackTrace();
                for (int i = 2; i < st.FrameCount; i++)
                {
                    // skip first 2 methods to actually get where this is called from
                    var frame = st.GetFrame(i);
                    internalErrorMessage += $" -- {frame.GetMethod()} at {frame.GetFileName()}:{frame.GetFileLineNumber()}";
                }
                LogInternalError(internalErrorMessage);
                truncatedMessage = message.Substring(0, Math.Min(k_MaxMessageSize, message.Length));
            }

            return truncatedMessage;
        }

        public void LogDebug(string message, Location location)
        {
            if (location == null || (location.SourceTree != null && !context.Compilation.ContainsSyntaxTree(location.SourceTree)))
                location = Location.None;
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticHelper.CreateInfoDescriptor(truncatedMessage), location));
            Debug.LogDebug(message);
        }

        public void LogDebug(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticHelper.CreateInfoDescriptor(truncatedMessage),
                DiagnosticHelper.GenerateExtenalLocation(sourceFilePath, sourceLineNumber)));
            Debug.LogDebug(message);
        }

        public void LogInfo(string message, Location location)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticHelper.CreateInfoDescriptor(truncatedMessage), location));
            Debug.LogInfo(message);
        }

        public void LogInfo(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticHelper.CreateInfoDescriptor(truncatedMessage),
                DiagnosticHelper.GenerateExtenalLocation(sourceFilePath, sourceLineNumber)));
            Debug.LogInfo(message);
        }

        public void LogWarning(string message, Location location)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticHelper.CreateWarningDescriptor(truncatedMessage), location));
            Debug.LogWarning(message);
        }

        public void LogWarning(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticHelper.CreateWarningDescriptor(truncatedMessage),
                DiagnosticHelper.GenerateExtenalLocation(sourceFilePath, sourceLineNumber)));
            Debug.LogWarning(message);
        }

        public void LogError(string message, Location location)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticHelper.CreateErrorDescriptor(truncatedMessage), location));
            Debug.LogError(message, location.ToString()); // will write the full non-truncated version
        }

        public void LogError(string message,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0)
        {
            var truncatedMessage = ValidateMessageLength(message);
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticHelper.CreateErrorDescriptor(truncatedMessage),
                DiagnosticHelper.GenerateExtenalLocation(sourceFilePath, sourceLineNumber)));
            Debug.LogError(message, $"{sourceFilePath}:{sourceLineNumber}");
        }

        public void LogException(Exception e,
            [System.Runtime.CompilerServices.CallerFilePath]
            string sourceFilePath = "",
            [System.Runtime.CompilerServices.CallerLineNumber]
            int sourceLineNumber = 0)
        {
            LogError(e.Message + ". Full exception is logged in netcode's source generator logs.", sourceFilePath, sourceLineNumber);
            Debug.LogException(e);
            // Can't use exception diagnostic with full stacktrace as a message that's too large will fail to be parsed by unity
        }

        public void LogException(Exception e, Location location)
        {
            LogError(e.Message + ". Full exception is logged in netcode's source generator logs.", location);
            Debug.LogException(e);
            // Can't use exception diagnostic with full stacktrace as a message that's too large will fail to be parsed by unity
        }
    }

    internal static class DiagnosticHelper
    {
        static public DiagnosticDescriptor CreateErrorDescriptor(string message)
        {
            return new DiagnosticDescriptor(
                "NetCode",
                "NetCode Generator Error",
                message,
                "SourceGenerator",
                DiagnosticSeverity.Error, true,
                "an error occurred while generating serializers");
        }
        static public DiagnosticDescriptor CreateWarningDescriptor(string message)
        {
            return new DiagnosticDescriptor("NetCode", "NetCode Generator", message, "SourceGenerator", DiagnosticSeverity.Warning, true);
        }
        static public DiagnosticDescriptor CreateInfoDescriptor(string message)
        {
            return new DiagnosticDescriptor("NetCode", "NetCode Generator", message, "SourceGenerator", DiagnosticSeverity.Info, true);
        }
        static public DiagnosticDescriptor CreateException(Exception e)
        {
            var b = new StringBuilder();
            b.Append(e.Message);
            b.Append(e.StackTrace);
            return new DiagnosticDescriptor("NetCode", "Unhandled Exception", b.ToString(), "SourceGenerator", DiagnosticSeverity.Error, true);
        }

        static public Location GenerateExtenalLocation(string sourceFile, int lineNo)
        {
            if (string.IsNullOrEmpty(sourceFile))
                return Location.None;
            return Location.Create(sourceFile,
                TextSpan.FromBounds(0, 0),
                new LinePositionSpan(
                    new LinePosition(lineNo, 0),
                    new LinePosition(lineNo, 0)));
        }
    }


}
