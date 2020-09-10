using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.NetCode.Editor.GhostCompiler
{
    internal struct GhostCompilerOptions
    {
        public string tempOutputFolderPath;
        public bool alwaysGenerateFiles;
        public HashSet<string> excludeTypes;
    }

    internal class GhostCompiler
    {
        private GeneratedFileGuidCache _fileCache;

        public GeneratedFileGuidCache fileCache => _fileCache;
        public int buildErrors = 0;

        private string _cachePath;

        public GhostCompiler(string cachePath)
        {
            _cachePath = cachePath;
        }

        public GhostCompiler(GeneratedFileGuidCache fileCache, string cachePath)
        {
            _fileCache = fileCache;
            _cachePath = cachePath;
        }

        private void LoadFileCache()
        {
            if (_fileCache != null)
                return;

            _fileCache = GhostCompilerServiceUtils.Load<GeneratedFileGuidCache>(_cachePath);
            if (_fileCache == null)
            {
                _fileCache = ScriptableObject.CreateInstance<GeneratedFileGuidCache>();
            }
        }

        public void Generate(IGhostAssemblyProvider provider, GhostCompilerOptions options)
        {
            Generate(provider.GetAssemblies(), options);
        }

        public void Generate(IEnumerable<Assembly> assemblyEnumerable, GhostCompilerOptions options)
        {
            Directory.CreateDirectory(options.tempOutputFolderPath);
            LoadFileCache();

            var assemblies = assemblyEnumerable.ToArray();
            var postProcessor =
                new GhostCodeGeneratorPostProcessor(options.tempOutputFolderPath, options.alwaysGenerateFiles);

            buildErrors = 0;
            var progress = 0.0f;

            void UpdateProgressBar(string step)
            {
                progress += 1.0f / assemblies.Length;
                EditorUtility.DisplayProgressBar("Generating Ghosts", step, progress);
            }

            //Removing any orphan assembly
            var toRemove = fileCache.assemblies.Keys.Where(key =>
                !assemblies.Any(a => CompilationContext.AssemblyNameGenerated(a.name) == key));
            foreach (var key in toRemove.ToArray())
            {
                GhostCompilerServiceUtils.DebugLog($"Assembly {key} not present. Removing from cache");
                fileCache.assemblies.Remove(key);
                FileUtil.DeleteFileOrDirectory(Path.Combine(options.tempOutputFolderPath, key));
            }

            GhostCompilerServiceUtils.DebugLog($"--> Generating netcode files");
            var codeGenCache = new Dictionary<string, GhostCodeGen>();
            foreach (var assembly in assemblies)
            {
                var context = new CompilationContext(assembly, _fileCache, codeGenCache);
                context.excludeTypeFilter = options.excludeTypes;
                context.outputFolder = Path.Combine(options.tempOutputFolderPath, context.assemblyNameGenerated);
                if (_fileCache.assemblies.TryGetValue(context.assemblyNameGenerated, out var assemblyEntry))
                {
                    context.generatedAssembly = assemblyEntry;
                }

                UpdateProgressBar($"Generating {context.assemblyNameGenerated}");
                try
                {
                    postProcessor.Generate(context);
                    //if there is nothing to generate for this assembly, remove it from the file cache if it is present
                    if (context.generatedBatch == null || context.generatedBatch.m_PendingOperations.Count == 0)
                    {
                        _fileCache.assemblies.Remove(context.assemblyNameGenerated);
                        FileUtil.DeleteFileOrDirectory(Path.Combine(options.tempOutputFolderPath,
                            context.assemblyNameGenerated));
                        continue;
                    }

                    postProcessor.FlushBatch(context);
                    _fileCache.assemblies[context.assemblyNameGenerated] = context.generatedAssembly;
                }
                catch (Exception e)
                {
                    ++buildErrors;
                    Debug.LogException(e);
                }
            }

            GhostCompilerServiceUtils.Save(_fileCache, _cachePath);
            EditorUtility.ClearProgressBar();
        }
    }
}