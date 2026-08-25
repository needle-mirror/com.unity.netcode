using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace Unity.NetCode.Generators
{
    internal static class Debug
    {
        public static void LaunchDebugger()
        {
            if (Debugger.IsAttached)
                return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Debugger.Launch();
            }
            else
            {
                string text = $"Attach to {Process.GetCurrentProcess().Id} netcode generator";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    StartProcess("/usr/bin/osascript", $"-e \"display dialog \\\"{text}\\\" with icon note buttons {{\\\"OK\\\"}}\"");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    StartProcess("/usr/bin/zenity", $@"--info --title=""Attach Debugger"" --text=""{text}"" --no-wrap");
                }
            }
        }

        public static void LaunchDebugger(GeneratorExecutionContext context, string assembly)
        {
            if(string.IsNullOrEmpty(assembly)
               || string.IsNullOrEmpty(context.Compilation.AssemblyName)
               || context.Compilation.AssemblyName.Equals(assembly, StringComparison.InvariantCultureIgnoreCase))
            {
                LaunchDebugger();
            }
        }

        private static void StartProcess(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = true,
                CreateNoWindow = false
            };
            var processTemp = new Process {StartInfo = startInfo, EnableRaisingEvents = true};
            processTemp.Start();
            processTemp.WaitForExit();
        }

        private const string k_LogFile = "SourceGenerator.log";
        private static string GetLogFilePath()
        {
            return Path.Combine(Helpers.GetOutputPath(), k_LogFile);
        }
        private static TextWriter GetOutputStream()
        {
            return File.AppendText(GetLogFilePath());
        }

        internal static Action<LogType, string> s_OnLogAction;
        static void LogToDebugStream(LogType level, string message)
        {
            s_OnLogAction?.Invoke(level, message);

            if (!Helpers.WriteLogToDisk)
            {
                return;
            }
            try
            {
                using var writer = GetOutputStream();
                writer.WriteLine($"[{level}] {message}");
            }
            catch (Exception flushEx)
            {
                Console.WriteLine($"Exception while writing to log: {flushEx.Message}");
            }
        }
        public static void LogException(Exception exception)
        {
            s_OnLogAction?.Invoke(LogType.Exception, exception.ToString());

            if (!Helpers.WriteLogToDisk)
            {
                return;
            }
            try
            {
                using var writer = GetOutputStream();
                writer.WriteLine($"[Exception] {exception.Message}\nCallstack: {exception.StackTrace}");
            }
            catch (Exception flushEx)
            {
                Console.WriteLine($"Exception while writing to log: {flushEx.Message}");
            }
        }
        public static void LogDebug(string message)
        {
            if(Helpers.CurrentLogLevel > Helpers.LoggingLevel.Debug)
                return;
            LogToDebugStream(LogType.Debug, message);
        }
        public static void LogInfo(string message)
        {
            if(Helpers.CurrentLogLevel > Helpers.LoggingLevel.Info)
                return;
            LogToDebugStream(LogType.Info, message);
        }
        public static void LogWarning(string message)
        {
            if(Helpers.CurrentLogLevel > Helpers.LoggingLevel.Warning)
                return;
            LogToDebugStream(LogType.Warning, message);
        }
        public static void LogError(string message, string additionalInfo)
        {
            message += $"\n\nAdditional info:\n{additionalInfo}\n\nStacktrace:\n";
            message += Environment.StackTrace;
            LogToDebugStream(LogType.Error, message);
        }
    }

    internal enum LogType
    {
        Debug,
        Info,
        Warning,
        Error,
        Exception,
    }
}
