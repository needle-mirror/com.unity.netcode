using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Unity.NetCode.Editor.GhostCompiler
{
    internal struct SyncOptions
    {
        public string sourceFolder;
        public string destFolder;
        public bool forceReplaceFolders; //default is false, only changes are synced
        public bool keepOrphans;  //default is false, orphans are removed
    }

    // Helper class to copy/merge the compiler output (usually in Assets/NetCodeGenerated) with the temporary
    // folders created during the code-generation phase
    // The contents of the directories are mirrored, so any changed/added files are going to be replaced.
    // Try to minimise the amount of copy / changes to avoid big recompilation time
    internal struct GhostCompilerFolderSync
    {
        private GeneratedFileGuidCache compilerCache;
        private int _numChanges;
        public SyncOptions options;

        public GhostCompilerFolderSync(GeneratedFileGuidCache cache, SyncOptions syncOptions)
        {
            options = syncOptions;
            compilerCache = cache;
            _numChanges = 0;
        }

        //Will generate a pair of relative source directory -> dest directory paths
        public static string MapGeneratedAssemblyToOutputFolder(string destFolder, string generatedAssemblyName)
        {
            // Precondition: generatedAssemblyName must be without any directory name!
            if(string.IsNullOrEmpty(destFolder))
                throw new ArgumentException("The destFolder must be not empty");
            // Precondition: generatedAssemblyName must be without any directory name!
            if(!string.IsNullOrEmpty(Path.GetDirectoryName(generatedAssemblyName)))
                throw new ArgumentException("generatedAssemblyName must be without any directory name");

            if (generatedAssemblyName == "Assembly-CSharp-Editor.Generated")
                return Path.Combine(destFolder, "Assembly-CSharp.Generated", "Editor");
            if (generatedAssemblyName == "Assembly-CSharp.Generated")
                return Path.Combine(destFolder, generatedAssemblyName);
            return Path.Combine(destFolder, generatedAssemblyName);
        }

        public static string InverseMapOutputFolderToGeneratedAssembly(string subFolder, string folderRootPath, string sourceFolderParent)
        {
            // Precondition: destFolder must be a relative path!
            Debug.Assert(!Path.IsPathRooted(subFolder));
            if (!subFolder.StartsWith(folderRootPath))
                throw new ArgumentException($"{subFolder} must start with {folderRootPath}");

            //if match, just return the source root
            if (subFolder.Length == folderRootPath.Length)
                return sourceFolderParent;
            if (subFolder.EndsWith("Assembly-CSharp.Generated/Editor"))
                return Path.Combine(sourceFolderParent, "Assembly-CSharp-Editor.Generated");
            if (subFolder.EndsWith("Assembly-CSharp.Generated"))
                return Path.Combine(sourceFolderParent, "Assembly-CSharp.Generated");
            return Path.Combine(sourceFolderParent, subFolder.Substring(folderRootPath.Length+1));
        }

        //Folders to update contains a relative path to the temporary output directory
        public bool SyncFolders()
        {
            GhostCompilerServiceUtils.DebugLog($"---- Syncing folder {options.destFolder} ----");
            var rootOutputFolderInfo = new DirectoryInfo(options.destFolder);
            if (!rootOutputFolderInfo.Exists)
                rootOutputFolderInfo.Create();

            _numChanges = 0;
            //If we force to replace everything just remove everything
            if (options.forceReplaceFolders)
            {
                FileUtil.DeleteFileOrDirectory(options.destFolder);
                ++_numChanges;
            }

            //Remove everything from the temp and dest folder that is not present in the generated file cache
            foreach (var sourceSubFolder in new DirectoryInfo(options.sourceFolder).GetDirectories())
            {
                if (!compilerCache.assemblies.ContainsKey(sourceSubFolder.Name))
                {
                    FileUtil.DeleteFileOrDirectory(sourceSubFolder.FullName);
                    FileUtil.DeleteFileOrDirectory(MapGeneratedAssemblyToOutputFolder(options.destFolder,sourceSubFolder.Name));
                    ++_numChanges;
                }
            }

            if (!options.forceReplaceFolders && !options.keepOrphans)
            {
                GhostCompilerServiceUtils.DebugLog($"-- Check for orphans in {options.destFolder}");
                RemoveOrphanInFolder(options.destFolder, options.sourceFolder);
            }

            ////Copy to the destination folder any missing and successfully built assembly
            foreach(var sourceFolderInfo in new DirectoryInfo(options.sourceFolder).EnumerateDirectories())
            {
                if (compilerCache.assemblies[sourceFolderInfo.Name].compilationErrors != 0)
                {
                    GhostCompilerServiceUtils.DebugLog($"Skipped {sourceFolderInfo.Name} because build failed");
                    continue;
                }
                var destFolder =  MapGeneratedAssemblyToOutputFolder(options.destFolder, sourceFolderInfo.Name);
                if (!Directory.Exists(destFolder))
                    Directory.CreateDirectory(destFolder);

                CopyChangedGeneratedFiles(sourceFolderInfo, destFolder);
            }
            return _numChanges > 0;
        }

        //Recursively cleanup the folders from any orphans files and directories
        private void RemoveOrphanInFolder(string destFolder, string sourceFolder)
        {
            //First validate what I'm expecting
            Debug.Assert(!Path.IsPathRooted(destFolder));

            //GhostCompilerServiceUtils.DebugLog($"orphan: check directory {destFolder}");

            //Remove any files added to the generated output folders that aren't present in the generated files set
            foreach (var fileInfo in new DirectoryInfo(destFolder).GetFiles())
            {
                //GhostCompilerServiceUtils.DebugLog($"orphan: check file {fileInfo.Name}");
                string sourceFile = fileInfo.Extension == ".meta"
                    ? Path.Combine(sourceFolder, Path.GetFileNameWithoutExtension(fileInfo.Name))
                    :Path.Combine(sourceFolder, fileInfo.Name);

                if (!File.Exists(sourceFile) && !Directory.Exists(sourceFile))
                {
                    if (!sourceFile.EndsWith("/Assembly-CSharp.Generated/Editor") || !compilerCache.assemblies.ContainsKey("Assembly-CSharp-Editor.Generated"))
                    {
                        GhostCompilerServiceUtils.DebugLog($"Deleting orphan file {fileInfo.FullName}");
                        fileInfo.Delete();
                        ++_numChanges;
                    }
                }
            }

            // Delete all the generated directories in the output root folder that aren't present in the generated assemblies
            foreach (var subFolder in Directory.GetDirectories(destFolder))
            {
                //need to remap to handle the special "Assembly-CSharp/Editor case"
                var mappedSubFolder = InverseMapOutputFolderToGeneratedAssembly(subFolder,
                    options.destFolder, options.sourceFolder);

                if (!Directory.Exists(mappedSubFolder))
                {
                    GhostCompilerServiceUtils.DebugLog($"Deleting orphan folder {subFolder}");

                    GhostCompilerServiceUtils.DeleteFileOrDirectory(subFolder);
                    ++_numChanges;
                    continue;
                }
                RemoveOrphanInFolder(subFolder, mappedSubFolder);
            }
        }

        private void CopyChangedGeneratedFiles(DirectoryInfo sourceFolderInfo, string destFolder)
        {
            GhostCompilerServiceUtils.DebugLog($"-- Sync {destFolder}");

            bool ShouldCopyAssemblyDefinition(string originalAssemblyName)
            {
                return !originalAssemblyName.StartsWith("Assembly-CSharp");
            }

            var assemblyName = sourceFolderInfo.Name;
            var guidDictionary = compilerCache.assemblies[sourceFolderInfo.Name].files;

            //TODO: make again an async version of this to make copy operation a little faster
            int filesCopied = 0;
            foreach (var sourceInfo in sourceFolderInfo.EnumerateFiles())
            {
                var destInfo = new FileInfo(Path.Combine(destFolder, sourceInfo.Name));
                if (sourceInfo.Name.EndsWith(".asmdef") && !ShouldCopyAssemblyDefinition(assemblyName))
                    continue;

                if (!destInfo.Exists)
                {
                    GhostCompilerServiceUtils.DebugLog($"Copy {sourceInfo.Name}");
                    sourceInfo.CopyTo(destInfo.FullName);
                    ++filesCopied;
                    continue;
                }
                if (sourceInfo.Length != destInfo.Length)
                {
                    GhostCompilerServiceUtils.DebugLog($"Replacing {destInfo.Name}");
                    GhostCompilerServiceUtils.CheckoutFile(destInfo);
                    sourceInfo.CopyTo(destInfo.FullName, true);
                    ++filesCopied;
                    continue;
                }
                var guid = GhostCompilerServiceUtils.ComputeFileGuid(destInfo.FullName);

                if (!guidDictionary.ContainsKey(sourceInfo.Name))
                {
                    UnityEngine.Debug.LogError($"Could not find {sourceInfo.Name}");
                    continue;
                }
                if (guid != guidDictionary[sourceInfo.Name])
                {
                    GhostCompilerServiceUtils.DebugLog($"Replacing (guid changed) {destInfo.Name}");
                    GhostCompilerServiceUtils.CheckoutFile(destInfo);
                    sourceInfo.CopyTo(destInfo.FullName, true);
                    ++filesCopied;
                }
            }
            _numChanges += filesCopied;
        }
    }
}