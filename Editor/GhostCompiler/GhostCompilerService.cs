using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using CompilationPipeline = UnityEditor.Compilation.CompilationPipeline;
using CompilerMessage = UnityEditor.Compilation.CompilerMessage;
using CompilerMessageType = UnityEditor.Compilation.CompilerMessageType;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Editor.GhostCompiler
{
    [InitializeOnLoad]
    static class GhostCompilerServiceLoader
    {
        internal static GhostCompilerService service;

        //FIXME: need to localized resources
        private const string TxtDialogTitle = "Pending NetCode changes";
        private const string TxtDialogMessage = "There are un-applied code-generation changes that need to be rebuilt!\n" +
                                               "Do you still want to enter play or build the pending changes?";
        private const string TxtBuild = "Build";
        private const string TxtEnterPlayMode = "Enter PlayMode";

        static GhostCompilerServiceLoader()
        {
            if (!IsGhostCompilationEnabled())
                return;
            Load();
        }

        public static void DisableGhostCompilation()
        {
            EditorPrefs.SetBool("GhostCompilerService.Enabled", false);
            UnLoad();
        }
        public static void EnableGhostCompilation()
        {
            EditorPrefs.SetBool("GhostCompilerService.Enabled", true);
            Load();
        }

        public static bool IsGhostCompilationEnabled()
        {
            return EditorPrefs.GetBool("GhostCompilerService.Enabled", true);
        }

        private static void UnLoad()
        {
            if (service != null)
            {
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                Object.DestroyImmediate(service);
                service = null;

            }
        }

        private static void Load()
        {
            if (service != null)
            {
                Debug.LogError("An instance of GhostCompilerService already exist.");
                return;
            }

            service = GhostCompilerService.LoadOrCreateService();
            service.InitializeOnLoad();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange playModeChange)
        {
            if (!IsGhostCompilationEnabled())
                return;

            // If we're still compiling the domain will reload and cause an error, so as a safeguard forcibly exit play mode
            if (playModeChange == PlayModeStateChange.EnteredPlayMode && EditorApplication.isCompiling)
            {
                Debug.Log("Cannot enter playmode while editor is compiling");
                EditorApplication.ExitPlaymode();
                return;
            }

            if (playModeChange == PlayModeStateChange.ExitingEditMode && !EditorUtility.scriptCompilationFailed)
            {
                if (service.HasPendingChanges())
                {
                    //I need to recompile or at least advise that we have potential changes pending.
                    if (service.Settings.autoRecompile)
                    {
                        Debug.Log("Exiting playmode pending changes");
                        EditorApplication.ExitPlaymode();
                        System.Threading.Interlocked.Increment(ref service._regenerateChangeCount);
                        return;
                    }
                    if (EditorUtility.DisplayDialog(TxtDialogTitle, TxtDialogMessage, TxtBuild, TxtEnterPlayMode))
                    {
                        EditorApplication.ExitPlaymode();
                        System.Threading.Interlocked.Increment(ref service._regenerateChangeCount);
                    }
                }
            }
        }
    }


    [Flags]
    public enum ChangesFlags
    {
        None = 0x0,
        AssembliesChanged = 0x1,
        TemplatesChanged = 0x2,
    }

    [Flags]
    public enum AssemblyFilterExcludeFlag
    {
        None = 0x0,
        EditorOnly = 0x1,
        Tests = 0x2,
        Both = -1 //to trick the inspector..
    }

    public enum AssemblyStatus
    {
        None = 0x0,
        New,
        Changed,
        Removed
    }

    internal class UnityAssemblyDefinition
    {
#pragma warning disable CS0649
        public string name;
        public string[] references;
        public string[] includePlatforms;
        public string[] excludePlatforms;
        public string[] defineConstraints;
        public string[] optionalUnityReferences;
        public string[] precompiledReferences;
        public bool allowUnsafeCode;
        public bool overrideReferences;
        public bool autoReferenced;
        public bool noEngineReferences;
#pragma warning restore CS0649
        public string assetPath;
    }

    internal interface IGhostAssemblyProvider
    {
        IEnumerable<UnityEditor.Compilation.Assembly> GetAssemblies();
    }

    internal interface INetCodeTemplateProvider
    {
        IEnumerable<UnityAssemblyDefinition> GetTemplateFolders();
    }

    //TO INVESTIGATE
    //use ILPostProcessor to hook in the compilation flow.
    //Use precompiled DLL that are loaded into the the domain at bootstrap/domain reload time or world boostrap
    //time instead of compiling the code in unity for faster iteration
    //Generate a dll and merge it with the unity compiled one instead of manually inject IL code (let make the compiler do
    //the right job for us). Similar in principle to ILMerge but simplified because of the relatively simpler and constrained
    //use case

    internal class GhostCompilerService : ScriptableObject
    {
        public const string DefaultCachePath = "Library/NetCode";

        private UnityAssemblyDefinition[] _codegenTemplatesAssemblies;
        private string _cachePath;
        private string _tempPath;
        private string _generateCacheFilename;
        private string _settingFilename;

        //Those are temporary states and are save to persist domain reload
        //but they reset to default when the editor is restarted or project is reloaded
        [SerializeField]internal ChangesFlags changesFlags;
        [SerializeField]internal List<string> changedAssemblies = new List<string>();
        [SerializeField]internal bool initialized;

        //Does not need to be serialized as temporary state
        private List<string> foldersToDelete = new List<string>();
        private GhostCompilerSettings _settings;
        private GeneratedFileGuidCache _generatedFileCache;
        internal bool ignoreAssemblyCSharpNextCompilation;
        private int _templateChangeCount;
        internal int _regenerateChangeCount;
        internal bool _regenerateAll;
        internal bool _loadCustomOverrides;


        public UnityAssemblyDefinition[] CodegenTemplatesAssemblies => _codegenTemplatesAssemblies;
        public GhostCompilerSettings Settings => _settings;

        public bool HasPendingChanges()
        {
            return changesFlags != ChangesFlags.None;
        }

        public bool HasPendingFolderToDelete()
        {
            return foldersToDelete.Count > 0;
        }

        public bool IsAssemblyChanged(string assemblyName)
        {
            return changedAssemblies.Contains(assemblyName);
        }

        public bool IsCompilationDisabled => EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling;

        public GeneratedFileGuidCache GetFileGuidCache()
        {
            return _generatedFileCache;
        }

        GhostCompilerService()
        {
            _codegenTemplatesAssemblies = new UnityAssemblyDefinition[0];
        }

        public void InitializeOnLoad()
        {
            if (!initialized)
            {
                //If autocompilation is set to true, it is necessary to force the project to regenerate any changes and/or generated files the first
                //time we open the project, in order to keep up-to-date the temp folder with the current project files
                if (_settings.autoRecompile)
                {
                    GhostCompilerServiceUtils.DebugLog($"Initial load, force recompilation of everything");
                    _regenerateAll = true;
                }
                initialized = true;
            }
        }

        private void LoadCustomGhostSnapshotValueTypes()
        {
            _loadCustomOverrides = false;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.EndsWith(".NetCodeGen"));
            bool appliedOverride = false;
            //Must be editor only and end with .NetCodeGen
            foreach (var assembly in assemblies)
            {
                //Look for any t that implement the
                foreach (var t in assembly.GetTypes())
                {
                    if (typeof(IGhostDefaultOverridesModifier).IsAssignableFrom(t))
                    {
                        if (!appliedOverride)
                        {
                            var assemblyPath = Path.GetDirectoryName(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.GetName().Name));
                            var overrideImpl = Activator.CreateInstance(t) as IGhostDefaultOverridesModifier;
                            overrideImpl.Modify(GhostAuthoringModifiers.GhostDefaultOverrides);
                            overrideImpl.ModifyAlwaysIncludedAssembly(GhostAuthoringModifiers.AssembliesDefaultOverrides);
                            overrideImpl.ModifyTypeRegistry(CodeGenTypes.Registry, assemblyPath);
                            appliedOverride = true;
                        }
                        else
                        {
                            UnityEngine.Debug.LogError($"Found multiple implementations of IGhostDefaultOverridesModifier, ignoring {t}");
                        }
                    }
                }
            }
        }

        internal void DomainReload()
        {
            GhostCompilerServiceUtils.DebugLog("All assemblies has been loaded. Collecting components...");
            if (_regenerateAll)
            {
                _regenerateAll = false;
                RegenerateAll();
            }
            else if (_settings.autoRecompile)
            {
                System.Threading.Interlocked.Increment(ref _regenerateChangeCount);
            }
        }

        internal bool RegenerateAll()
        {
            //Do nothing if entering/exiting play-mode
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                Debug.Log($"RegenerateAll is a no-op if compilation is pending or entering/exiting playmode");
                return false;
            }

            //Load custom snapshosts valuetypes before compile assemblies if necessary.
            if (_loadCustomOverrides)
                LoadCustomGhostSnapshotValueTypes();
            return CompileAssemblies(GetAssemblyProvider().GetAssemblies());
        }

        public bool ManualRegeneration(List<string> excludeTypes)
        {
            //Do nothing if entering/exiting play-mode
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                Debug.Log($"RegenerateAll is a no-op if compilation is pending or entering/exiting playmode");
                return false;
            }
            return CompileAssemblies(GetAssemblyProvider().GetAssemblies(),excludeTypes);
        }

        private void OnAssemblyCompilationFinished(string assemblyName, CompilerMessage[] messages)
        {
            //Don't track those. They are going to be regenerated because their "parent" did
            if (assemblyName.Contains(".Generated"))
                return;

            if (messages != null && messages.Any(msg => msg.type == CompilerMessageType.Error))
            {
                //Don't mark the assembly has changed yet but Assembly-CSharp need special treatment
                if (Path.GetFileName(assemblyName).StartsWith("Assembly-CSharp"))
                {
                    var generatedAssemblyCSharpFiles = Path.Combine(_settings.outputFolder, "Assembly-CSharp.Generated");
                    if (!Directory.Exists(generatedAssemblyCSharpFiles))
                        return;

                    //Special case for Assembly-CSharp and Assembly-CSharp-Editor
                    //If there are errors in some code-generated files, delete the generated folder
                    if (messages.Any(msg => msg.type == CompilerMessageType.Error &&
                                            msg.file.Contains(generatedAssemblyCSharpFiles)))
                    {
                        foldersToDelete.Add(generatedAssemblyCSharpFiles);
                    }
                }

                return;
            }
            if (ignoreAssemblyCSharpNextCompilation && Path.GetFileName(assemblyName).StartsWith("Assembly-CSharp"))
            {
                GhostCompilerServiceUtils.DebugLog($"Assembly {assemblyName} changes ignored");
                return;
            }

            //Mark the assembly as changed if it is not a ."Generated" one
            GhostCompilerServiceUtils.DebugLog($"Assembly {assemblyName} added to the changelist");
            changesFlags |= ChangesFlags.AssembliesChanged;
            changedAssemblies.Add(Path.GetFileNameWithoutExtension(assemblyName));
        }

        private void OnCompilationFinished(object context)
        {
            ignoreAssemblyCSharpNextCompilation = false;
            if (foldersToDelete.Count != 0)
            {
                return;
            }

            //It is delayed because:
            // - if the compilation is succeed (so no errors are present) an actual domain reload is going to happen and
            // all the changes are going to be generated at that point because that callback is actually lost anyway
            // - I found after some tries that delaying gave me the better result, less clunky overall
            // - This is callback is triggered at the end of the compilation task and moving source code in the middle of process
            //   sometime didn't make unity reload the changed c# script. That happens only after a while. That at least
            //   in 2020.10b11
            // If the compilation did not have any errors a domain reload will trigger and we can perform codegen on domain reload instead
            if (Settings.autoRecompile)
            {
                System.Threading.Interlocked.Increment(ref _regenerateChangeCount);
            }
        }

        private void OnUpdate()
        {
            if(_codegenTemplatesAssemblies.Length == 0)
                RetrieveTemplatesFoldersAndRegisterWatcher();

            if (_loadCustomOverrides)
                LoadCustomGhostSnapshotValueTypes();

            if (foldersToDelete.Count != 0)
            {
                foreach(var f in foldersToDelete)
                {
                    GhostCompilerServiceUtils.DebugLog(
                        $"Deleting generated folder {f}. Error in code generated files");
                    if (!AssetDatabase.DeleteAsset(f))
                    {
                        GhostCompilerServiceUtils.DeleteFileOrDirectory(f);
                    }
                }
                foldersToDelete.Clear();
                AssetDatabase.Refresh();
            }
            if (System.Threading.Interlocked.Exchange(ref _templateChangeCount, 0) != 0)
            {
                //If either a recompilation is in progress or if we already scheduled a delay regeneration don't do it again
                //Also avoid scheduling something while transitioning in between modes
                if (_settings.autoRecompile && changesFlags == ChangesFlags.None && !EditorApplication.isCompiling)
                {
                    System.Threading.Interlocked.Increment(ref _regenerateChangeCount);
                }
                changesFlags |= ChangesFlags.TemplatesChanged;
            }
            if (System.Threading.Interlocked.Exchange(ref _regenerateChangeCount, 0) != 0)
            {
                RegenerateAllChanges();
            }
        }

        //Track CodeGenTemplates folders changes. In general, a template change will require a full regeneration.
        private void OnNetCodeTemplateChanged(string templateFolder)
        {
            GhostCompilerServiceUtils.DebugLog($"template {templateFolder} changed");
            System.Threading.Interlocked.Increment(ref _templateChangeCount);
        }

        internal bool RegenerateAllChanges()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                //Debug.LogWarning("Cannot run code-generation while entering playmode");
                return false;
            }
            // I need to trigger regeneration now, in presence of both errors or not.
            // In general:
            // - added assemblies are not loaded into the domain yet -> but I can detect the change
            // - remove assemblies are not remove yet -> but I can detect the change so I can remove the folders
            // - If I removed a type -> is not detected -> but cecil will so that work
            // - If I added a type -> is not detected -> but cecil will so I need to parse it to know
            //
            // All those consideration leads to the following truth table for asssemblies:
            //
            //                     Added  Removed  Changed
            //   Shoul I Gen         Y      Y        Y
            //
            // In practice I should always process all assemblies in order to remove folders
            // and update any changed script (if the user mess up the generated folder)
            // Can be optimized a little later

            // Do not generate the assembly list if we are not going to use it for anything
            if (changesFlags == ChangesFlags.None)
                return false;

            var ghostProvider = GetAssemblyProvider();
            var assemblies = ghostProvider.GetAssemblies().ToArray();

            //If any template has changed, I need a full rebuild
            if (changesFlags.HasFlag(ChangesFlags.TemplatesChanged))
            {
                GhostCompilerServiceUtils.DebugLog("Some templates changed. Force recompilation");
                return CompileAssemblies(assemblies);
            }
            //otherwise just update the changed relevant assemblies
            if (changesFlags.HasFlag(ChangesFlags.AssembliesChanged))
            {
                GhostCompilerServiceUtils.DebugLog("Some assemblies has changed. Force recompilation");
                return CompileAssemblies(assemblies);
            }
            return false;
        }

        public GhostAssemblyProvider GetAssemblyProvider()
        {
            return new GhostAssemblyProvider(_settings.excludeFlags);
        }

        private bool CompileAssemblies(IEnumerable<UnityEditor.Compilation.Assembly> assemblies, List<string> excludeTypes = null)
        {
            var compiler = new GhostCompiler(_generatedFileCache, _generateCacheFilename);
            compiler.Generate(assemblies, new GhostCompilerOptions
            {
                tempOutputFolderPath = _settings.tempOutputFolderPath,
                alwaysGenerateFiles = _settings.alwaysGenerateFiles,
                excludeTypes = excludeTypes != null ? new HashSet<string>(excludeTypes) : null
            });

            if (compiler.buildErrors != 0)
                return false;

            bool anyChanges = false;
            try
            {
                var sync = new GhostCompilerFolderSync(_generatedFileCache, new SyncOptions
                {
                    sourceFolder = _settings.tempOutputFolderPath,
                    destFolder = _settings.outputFolder
                });
                anyChanges = sync.SyncFolders();
                changedAssemblies.Clear();
                changesFlags = ChangesFlags.None;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            if (anyChanges)
            {
                // This will trigger another compilation, we ignore marking assembly csharp dirty during that
                // compilation since the changes are most likely just caused by generated code
                // This prevents the next domain reload from triggering code gen - since no assemblies changed
                ignoreAssemblyCSharpNextCompilation = true;
                AssetDatabase.Refresh();
            }

            return anyChanges;
        }

        private void RetrieveTemplatesFoldersAndRegisterWatcher()
        {
            _codegenTemplatesAssemblies = new NetCodeTemplatesProvider(_settings.excludeFlags)
                .GetTemplateFolders().ToArray();

            //Create a watcher for each templates folder
            foreach (var assemblyDefinition in _codegenTemplatesAssemblies)
            {
                var watcher = new FileSystemWatcher();
                watcher.Path = Path.GetFullPath(assemblyDefinition.assetPath);
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.LastWrite; //Watch for changes in LastWrite times
                watcher.Filter = "*.cs";
                watcher.EnableRaisingEvents = true;
                watcher.Changed += (_, args) =>
                {
                    OnNetCodeTemplateChanged(args.Name);
                };

                AppDomain.CurrentDomain.DomainUnload += (EventHandler) ((_, __) =>
                {
                    watcher.Dispose();
                });
            }
        }

        private void Awake()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.afterAssemblyReload += DomainReload;
            EditorApplication.update += OnUpdate;
            //Force loading the overrides after every domain reload
            _loadCustomOverrides = true;
        }

        private void OnDestroy()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            AssemblyReloadEvents.afterAssemblyReload -= DomainReload;
            EditorApplication.update -= OnUpdate;
        }

        private void OnBeforeAssemblyReload()
        {
            Save();
        }

        public void SaveSettings()
        {
            SaveSettings(_settings);
        }

        public void Save()
        {
            Debug.Assert(!string.IsNullOrEmpty(_cachePath));
            GhostCompilerServiceUtils.Save(this, Path.Combine(_tempPath, "GhostCompilerServiceTemp.asset"));
            SaveSettings(_settings);
        }

        public static GhostCompilerService LoadOrCreateService(string assetPath=DefaultCachePath, string tempPath="Temp")
        {
            if (string.IsNullOrEmpty(assetPath))
                assetPath = DefaultCachePath;

            var service = GhostCompilerServiceUtils.Load<GhostCompilerService>(Path.Combine(tempPath, "GhostCompilerServiceTemp.asset"));
            if (service == null)
            {
                service = CreateInstance<GhostCompilerService>();
            }
            service._cachePath = assetPath;
            service._tempPath = tempPath;
            service._settingFilename = Path.Combine(assetPath, "settings.asset");
            service._generateCacheFilename = Path.Combine(assetPath, "generatedfiles.asset");
            service.CreateOrLoadSettings();
            service.CreateOrLoadGeneratedFileCache();
            service.hideFlags = HideFlags.HideAndDontSave;
            return service;
        }

        private void SaveSettings(GhostCompilerSettings settings)
        {
            GhostCompilerServiceUtils.Save(settings, _settingFilename);
        }

        private void CreateOrLoadGeneratedFileCache()
        {
            _generatedFileCache = GhostCompilerServiceUtils.Load<GeneratedFileGuidCache>(_generateCacheFilename);
            if (_generatedFileCache == null)
            {
                _generatedFileCache = CreateInstance<GeneratedFileGuidCache>();
            }
        }


        private void CreateOrLoadSettings()
        {
            _settings = GhostCompilerServiceUtils.Load<GhostCompilerSettings>(_settingFilename);
            if (_settings == null)
            {
                _settings = CreateInstance<GhostCompilerSettings>();
                _settings.tempOutputFolderPath = "Temp/NetCodeGenerated";
                _settings.outputFolder = "Assets/NetCodeGenerated";
                _settings.autoRecompile = true;
                _settings.alwaysGenerateFiles = false;
                _settings.keepOrphans = false;
                _settings.excludeFlags = AssemblyFilterExcludeFlag.None;
                _settings.hideFlags = HideFlags.HideAndDontSave;
                SaveSettings(_settings);
            }
        }
    }
}
