using System;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// Some helpers for debugging purpose. Notable entries:
    /// - Logging and reporting (in file and compiler diagnostics)
    /// </summary>
    internal static class Helpers
    {
        static private ThreadLocal<string> s_OutputFolder;
        static private ThreadLocal<string> s_ProjectPath;
        static private ThreadLocal<bool> s_IsUnity2021_OrNewer;
        static private ThreadLocal<bool> s_SupportTemplatesFromAdditionalFiles;
        static private ThreadLocal<bool> s_WriteLogToDisk;
        static private ThreadLocal<bool> s_CanWriteFiles;
        static private ThreadLocal<LoggingLevel> s_LogLevel;

        public enum LoggingLevel : int
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3,
        }

        static public string ProjectPath
        {
            get => s_ProjectPath.Value;
            private set => s_ProjectPath.Value = value;
        }
        static public string OutputFolder
        {
            get => s_OutputFolder.Value;
            private set => s_OutputFolder.Value = value;
        }

        static public bool WriteLogToDisk
        {
            get => s_WriteLogToDisk.Value;
            private set => s_WriteLogToDisk.Value = value;
        }

        static public bool CanWriteFiles
        {
            get => s_CanWriteFiles.Value;
            private set => s_CanWriteFiles.Value = value;
        }

        static public LoggingLevel CurrentLogLevel => s_LogLevel.Value;

        /// <summary>
        /// Returns true if running as part of csc.exe, otherwise we are likely running in the IDE.
        /// Skipping Source Generation in the IDE can be a considerable performance win as source
        /// generators can be run multiple times per keystroke. If the user doesn't rely on generated types
        /// consider skipping your Generator's Execute method when this returns false
        /// </summary>
        public static bool IsBuildTime
        {
            // We want to be exclusive rather than inclusive here to avoid any issues with unknown processes, Unity changes, and testing
            get
            {
                Assembly assembly = Assembly.GetEntryAssembly();
                if (assembly == null)
                {
                    return false;
                }
                // VS Code
                if (assembly.FullName.StartsWith("Microsoft",
                        StringComparison.InvariantCultureIgnoreCase))
                {
                    return false;
                }
                // Visual Studio
                if (assembly.FullName.StartsWith("ServiceHub",
                        StringComparison.InvariantCultureIgnoreCase))
                {
                    return false;
                }
                // Rider
                if (assembly.FullName.StartsWith("JetBrains",
                        StringComparison.InvariantCultureIgnoreCase))
                {
                    return false;
                }

                return true;
            }
        }

        static Helpers()
        {
            s_OutputFolder = new ThreadLocal<string>(()=> Path.Combine("Temp", "NetCodeGenerated"));
            s_ProjectPath = new ThreadLocal<string>();
            s_IsUnity2021_OrNewer = new ThreadLocal<bool>();
            s_SupportTemplatesFromAdditionalFiles = new ThreadLocal<bool>();
            s_WriteLogToDisk = new ThreadLocal<bool>();
            s_CanWriteFiles = new ThreadLocal<bool>();
            s_LogLevel = new ThreadLocal<LoggingLevel>();
        }

        static public void SetupContext(GeneratorExecutionContext executionContext)
        {
            ProjectPath = null;
            //by default we allow both writing files and logs to disk. It is possible to change the behavior via
            //globalconfig
            CanWriteFiles = true;
            WriteLogToDisk = true;
            if (executionContext.AdditionalFiles.Length > 0 && !string.IsNullOrEmpty(executionContext.AdditionalFiles[0].Path))
                ProjectPath = executionContext.AdditionalFiles[0].GetText()?.ToString();
            //Parse global options and overrides default behaviour. They are used by both tests, and Editor (2021_OR_NEWER)
            ProjectPath = executionContext.GetOptionsString(GlobalOptions.ProjectPath, ProjectPath);
            OutputFolder = executionContext.GetOptionsString(GlobalOptions.OutputPath, OutputFolder);

            //If the project path is not valid, for any reason, we can't write files and/or log to disk
            if (string.IsNullOrEmpty(ProjectPath))
            {
                WriteLogToDisk = false;
                CanWriteFiles = false;
                Debug.LogWarning("Unable to setup/find the project path. Forcibly disable writing logs and files to disk");
            }
            else
            {
                Directory.CreateDirectory(GetOutputPath());
                CanWriteFiles = executionContext.GetOptionsFlag(GlobalOptions.WriteFilesToDisk, CanWriteFiles);
                WriteLogToDisk = executionContext.GetOptionsFlag(GlobalOptions.WriteLogsToDisk, WriteLogToDisk);
            }

            //The default log level is info. User can customise that via debug config. Info level is very light right now.
            s_LogLevel.Value = LoggingLevel.Info;
            var loggingLevel = executionContext.GetOptionsString(GlobalOptions.LoggingLevel);
            if (!string.IsNullOrEmpty(loggingLevel))
            {
                if (Enum.TryParse<LoggingLevel>(loggingLevel, ignoreCase:true, out var logLevel))
                    s_LogLevel.Value = logLevel;
                else throw new InvalidOperationException($"Unable to parse {GlobalOptions.LoggingLevel}:{loggingLevel}!");
            }
        }

        public static string GetOutputPath()
        {
            return Path.Combine(ProjectPath, OutputFolder);
        }

        // This path resolution is necessary for 2020.x where we need to resolve templates from packages
        // and other folders.
        private static string FindProjectFolderFromAdditionalFile(string folder)
        {
            var index = folder.LastIndexOf("/Library/", StringComparison.Ordinal);
            if(index < 0)
                index = folder.LastIndexOf("\\Library\\", StringComparison.Ordinal);
            return index > 0 ? folder.Substring(0, index) : null;
        }

        public static ulong ComputeVariantHash(ITypeSymbol variantType, ITypeSymbol componentType)
        {
            return ComputeVariantHash(
                Roslyn.Extensions.GetMetadataQualifiedName(variantType),
                Roslyn.Extensions.GetMetadataQualifiedName(componentType));
        }

        public static ulong ComputeVariantHash(string variantTypeFullname, string componentTypeFullName)
        {
            var hash = Utilities.TypeHash.FNV1A64("NetCode.GhostNetVariant");
            hash = Utilities.TypeHash.CombineFNV1A64(hash, Utilities.TypeHash.FNV1A64(componentTypeFullName));
            hash = Utilities.TypeHash.CombineFNV1A64(hash, Utilities.TypeHash.FNV1A64(variantTypeFullname));
            return hash;
        }

        public static SourceText WithInitialLineDirective(this SourceText sourceText, string generatedSourceFilePath)
        {
            var firstLine = sourceText.Lines.Count > 0 ? sourceText.Lines[0] : default;
            return sourceText.WithChanges(new TextChange(firstLine.Span, $"#line 1 \"{generatedSourceFilePath}\"" + Environment.NewLine + firstLine));
        }
    }
}
