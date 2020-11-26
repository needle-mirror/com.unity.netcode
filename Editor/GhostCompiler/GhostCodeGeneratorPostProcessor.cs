using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Unity.NetCode.Editor.GhostCompiler
{
    internal class CompilationContext
    {
        public string assemblyName;
        public string assemblyNameGenerated;
        public string outputFolder;
        public bool isRuntimeAssembly;
        public GeneratedFileGuidCache compilerCache;
        public GeneratedFileGuidCache.AssemblyEntry generatedAssembly;
        public GhostCodeGen.Batch generatedBatch;
        public HashSet<string> excludeTypeFilter = null;
        public Dictionary<string, GhostCodeGen> codeGenCache;

        public CompilationContext(UnityEditor.Compilation.Assembly assembly, GeneratedFileGuidCache cache, Dictionary<string, GhostCodeGen> genCache)
        {
            assemblyName = assembly.name;
            assemblyNameGenerated = AssemblyNameGenerated(assembly.name);
            compilerCache = cache;
            generatedAssembly = default;
            generatedBatch = default;
            isRuntimeAssembly = !assembly.flags.HasFlag(AssemblyFlags.EditorAssembly) &&
                                !assembly.compiledAssemblyReferences.Any(a => a.EndsWith("nunit.framework.dll"));
            codeGenCache = genCache;
        }
        public static string AssemblyNameGenerated(string assembly)
        {
            return $"{assembly}.Generated";
        }
    }
    internal interface IGhostCompilerPostProcessor
    {
        void Generate(CompilationContext context);
        bool FlushBatch(CompilationContext context);
    }

    //In presence of compilation errors, successfully recompiled assemblies are not reloaded and the appdomain is left untouched.
    //So we cannot rely on .NET assembly reflection to retrieve up-to-date type information after recompilation. That rule always
    //apply, regardless of what part of the codebase the problem occurs. A full domain reload is required, and Unity don't do that in those cases.
    //That lead to the inability to regenerate the code that, in some cases, is causing the issue itself, because we didn't update the
    //serializer code to match the changes in an component data.
    //The only viable workaround I found is to read the re-compiled assemblies, directly from their location, and then
    //use Cecil to retrieve all the runtime type info.
    //This is by no means is similar to what a ILPostProcessing solution would do anyway (probably), so it is somewhat in
    //the right direction to implement an ILPostProcessor for that.
    internal class GhostCodeGeneratorPostProcessor : IGhostCompilerPostProcessor
    {
        public readonly string kOutputFolderPath;
        private bool _alwaysRegenerateAllFiles;
        private readonly GhostPostProcessorResolver _assemblyResolver;

        public GhostCodeGeneratorPostProcessor(string outputPath, bool regenerateAllFiles)
        {
            kOutputFolderPath = outputPath;
            _alwaysRegenerateAllFiles = regenerateAllFiles;
            _assemblyResolver = new GhostPostProcessorResolver();

            foreach (var assembly in CompilationPipeline.GetAssemblies())
            {
                _assemblyResolver.resolvePaths.Add(Path.GetDirectoryName(assembly.outputPath));
            }
        }

        //NOTE: the code generation will generate an asmdef also for Assembly-CSharp in the temp folder. While un-necessary,
        //it simplify the logic to retrieve and setup of all the dependencies to compile the assembly.
        //When the project is synced, the Assembly-CSharp.Generated asmdef will not be copied over, since it is only used
        //temporarily for post-processing purpose
        public void Generate(CompilationContext context)
        {
            var assemblyFolder = Path.Combine(kOutputFolderPath, context.assemblyNameGenerated);
            var assemblyDefinition = _assemblyResolver.Resolve(context.assemblyName, new Mono.Cecil.ReaderParameters
            {
                ReadSymbols = false,
                ReadWrite = false,
            });
            var ghostTypes = new GhostComponentFilter {excludeList = context.excludeTypeFilter}
                .Filter(assemblyDefinition).ToArray();
            //Make a validation pass over the ghost types. Filter out invalid configuration and log errors
            ghostTypes = CheckAndFilterInvalidGhostTypes(ghostTypes);
            var commandTypes = new CommandComponentFilter { excludeList = context.excludeTypeFilter }
                .Filter(assemblyDefinition).ToArray();

            if (ghostTypes.Length == 0 && commandTypes.Length == 0)
                return;

            var generator = new CodeGenerator(CodeGenTypes.Registry);
            context.generatedBatch = generator.Generate(assemblyFolder, context.assemblyNameGenerated, context.assemblyName,
                context.isRuntimeAssembly, ghostTypes, commandTypes, context.codeGenCache);
        }

        //Check and enforce some restriction for IBufferElementData:
        // "Buffer element MUST have all fields annotated with the GhostFieldAttribute"
        //
        //This rule must be enforced to avoid having uninitialized data when a dynamic buffer is restored from the history
        //(see GhostPredictionHistorySystem.cs).
        //When a buffer is restored it might get resized and in that case, since we don't clear the buffer memory (for performance reason),
        //some portion of the data could be left untouched by the RestoreFromBackup function if some element fields are are not replicated.
        //The above requirement avoid that problem. We migh relax it later.
        private TypeDefinition[] CheckAndFilterInvalidGhostTypes(TypeDefinition[] ghostTypes)
        {
            bool CheckIsValid(TypeDefinition t)
            {
                if(CecilExtensions.IsBufferElementData(t) &&
                   t.Fields.Any(f => f.IsPublic && !f.HasAttribute<GhostFieldAttribute>()))
                {
                    Debug.LogError($"BufferElementData {t.FullName} has some members without a GhostField attribute.\n" +
                                   "In order to be replicated, BufferElementData requires that all fields must be annotated with a GhostField attribute.");
                    return false;
                }
                return true;
            }
            return ghostTypes.Where(CheckIsValid).ToArray();
        }

        //Return true if the assembly folder is changed
        public bool FlushBatch(CompilationContext context)
        {
            var assemblyFolder = Path.Combine(kOutputFolderPath, context.assemblyNameGenerated);
            var assemblyEntry = new GeneratedFileGuidCache.AssemblyEntry {files = new Dictionary<string, Guid>()};
            //Generate all the assembly guids and file entries
            foreach (var op in context.generatedBatch.m_PendingOperations)
            {
                var newGuid = GhostCompilerServiceUtils.ComputeGuidHashFor(op.Item2);
                assemblyEntry.files.Add(Path.GetFileName(op.Item1), newGuid);
            }
            context.generatedAssembly = assemblyEntry;

            if (_alwaysRegenerateAllFiles)
            {
                FileUtil.DeleteFileOrDirectory(assemblyFolder);
            }

            if (!Directory.Exists(assemblyFolder))
            {
                Directory.CreateDirectory(assemblyFolder);
                foreach (var op in context.generatedBatch.m_PendingOperations)
                {
                    var path = op.Item1;
                    File.WriteAllText(path, op.Item2);
                }
                return true;
            }

            //Check for any changes (files added or content changed) in library/temp directory first
            //and compute the new contents guid
            if (!context.compilerCache.assemblies.TryGetValue(context.assemblyNameGenerated, out var cachedAssemblyInfo))
            {
                cachedAssemblyInfo = new GeneratedFileGuidCache.AssemblyEntry();
                cachedAssemblyInfo.files = new Dictionary<string, Guid>();
                context.compilerCache.assemblies.Add(context.assemblyNameGenerated, cachedAssemblyInfo);
            }

            bool FileGuidChanged(string filename)
            {
                return !cachedAssemblyInfo.files.TryGetValue(filename, out var guid) || guid != assemblyEntry.files[filename];
            }
            bool ShouldFlushOpPredicate(Tuple<string, string> op)
            {
                return !File.Exists(op.Item1) || FileGuidChanged(Path.GetFileName(op.Item1));
            }

            //check for any files that need to be removed
            var toRemove = cachedAssemblyInfo.files.Select(pair => pair.Key)
                .Where(f => !assemblyEntry.files.ContainsKey(f)).ToArray();

            var anyRemoved = toRemove.Length > 0;
            foreach (var f in toRemove)
            {
                var filePath = Path.Combine(context.outputFolder, f);
                FileUtil.DeleteFileOrDirectory(filePath);
                cachedAssemblyInfo.files.Remove(f);
            }

            bool anyWritten = false;
            foreach(var op in context.generatedBatch.m_PendingOperations.Where(ShouldFlushOpPredicate))
            {
                File.WriteAllText(op.Item1, op.Item2);
                anyWritten = true;
            };

            return anyWritten || anyRemoved;
        }
    }

    internal class GhostPostProcessorResolver : Mono.Cecil.IAssemblyResolver
    {
        //local cache to speed up a little the references loading
        struct CacheEntry
        {
            public DateTime lastTimeWrite;
            public Mono.Cecil.AssemblyDefinition assemblyDefinition;
        }

        private Dictionary<string, CacheEntry> _assemblyCache = new Dictionary<string, CacheEntry>();
        public HashSet<string> resolvePaths = new HashSet<string>();

        public void Dispose()
        {
            foreach (var c in _assemblyCache.Values)
            {
                c.assemblyDefinition.Dispose();
            }
            _assemblyCache.Clear();
        }

        private string GetReferenceLocation(string assemblyName)
        {
            return resolvePaths.Select(p => Path.Combine(p, assemblyName + ".dll"))
                .Where(File.Exists).FirstOrDefault();
        }

        public Mono.Cecil.AssemblyDefinition Resolve(Mono.Cecil.AssemblyNameReference name)
        {
            return Resolve(name, new Mono.Cecil.ReaderParameters(Mono.Cecil.ReadingMode.Deferred));
        }

        public Mono.Cecil.AssemblyDefinition Resolve(Mono.Cecil.AssemblyNameReference reference, Mono.Cecil.ReaderParameters parameters)
        {
            return ResolveAssemblyByLocation(GetReferenceLocation(reference.Name), parameters);
        }

        public Mono.Cecil.AssemblyDefinition Resolve(string assemblyName, Mono.Cecil.ReaderParameters parameters)
        {
            return ResolveAssemblyByLocation(GetReferenceLocation(assemblyName), parameters);
        }

        public Mono.Cecil.AssemblyDefinition ResolveAssemblyByLocation(string referenceLocation, Mono.Cecil.ReaderParameters parameters)
        {
            if (referenceLocation == null)
                return null;

            var assemblyName = Path.GetFileName(referenceLocation);
            var lastTimeWrite = File.GetLastWriteTime(referenceLocation);
            if (_assemblyCache.TryGetValue(assemblyName, out var cacheEntry))
            {
                if (lastTimeWrite == cacheEntry.lastTimeWrite)
                    return cacheEntry.assemblyDefinition;
            }

            parameters.AssemblyResolver = this;

            using (var stream = new MemoryStream(File.ReadAllBytes(referenceLocation)))
            {
                var assemblyDefinition = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream, parameters);
                cacheEntry.assemblyDefinition?.Dispose();
                cacheEntry.assemblyDefinition = assemblyDefinition;
                cacheEntry.lastTimeWrite = lastTimeWrite;
                _assemblyCache[assemblyName] = cacheEntry;
            }

            return cacheEntry.assemblyDefinition;
        }
    }
}