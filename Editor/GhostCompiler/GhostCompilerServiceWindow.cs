using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Unity.NetCode.Editor.GhostCompiler;
using Object = UnityEngine.Object;

namespace Unity.NetCode.Editor
{
    class GhostCompilerServiceWindow : EditorWindow
    {
        Vector2 _scrollPosition = Vector2.zero;
        private GUIContent _folderIcon;
        private GUIContent _ghostIcon;
        private GUIContent _rpcIcon;
        private GUIContent _scriptIcon;
        private GUIContent _assemblyNeedRecompileIcon;
        private GUIContent _assemblyOkIcon;
        private GUIStyle _boxStyle;
        private GUIStyle _foldoutStyle;
        private GUIContent _lockedIcon;
        private GUIContent _unlockedIcon;
        private GUIContent _greenIcon;
        private GUIContent _redIcon;
        private KeyValuePair<string, List<Type>>[] _serializedCommandsAndComponents;
        private Dictionary<string, bool> _foldedAssembly = new Dictionary<string, bool>();
        [SerializeField] private List<string> _exludeAssemblyAndTypes = new List<string>();

        [MenuItem("Multiplayer/Code Generation Window")]
        public static void ShowWindow()
        {
            var window = GetWindow(typeof(GhostCompilerServiceWindow));
            window.Show();
        }

        void OnEnable()
        {
            _folderIcon = EditorGUIUtility.IconContent("Folder Icon");
            _scriptIcon = EditorGUIUtility.IconContent("cs Script Icon");
            _ghostIcon = EditorGUIUtility.IconContent("Packages/com.unity.netcode/EditorIcons/component.png");
            _rpcIcon = EditorGUIUtility.IconContent("Packages/com.unity.netcode/EditorIcons/rpc.png");
            _assemblyNeedRecompileIcon = EditorGUIUtility.IconContent("Warning");
            _assemblyOkIcon = EditorGUIUtility.IconContent("TestPassed");
            _lockedIcon = EditorGUIUtility.IconContent("IN LockButton on");
            _unlockedIcon = EditorGUIUtility.IconContent("IN LockButton");
            _greenIcon = EditorGUIUtility.IconContent("lightMeter/greenLight");
            _redIcon = EditorGUIUtility.IconContent("lightMeter//redLight");

            var boxScaledBg = new[] {EditorGUIUtility.Load("box@2x") as Texture2D};
            var boxTxtColor = new Color(0.7f, 0.7f, 0.7f, 1.0f);
            _boxStyle = new GUIStyle();
            _boxStyle.name = "box";
            _boxStyle.imagePosition = ImagePosition.ImageLeft;
            _boxStyle.stretchHeight = false;
            _boxStyle.fixedHeight = 24;
            _boxStyle.alignment = TextAnchor.MiddleCenter;
            _boxStyle.normal.textColor = boxTxtColor;
            _boxStyle.normal.scaledBackgrounds = boxScaledBg;
            _boxStyle.active.textColor = boxTxtColor;
            _boxStyle.hover.textColor = boxTxtColor;
        }

        private void OnGUI()
        {
            EnableDisableOnGUI();

            if (!GhostCompilerServiceLoader.IsGhostCompilationEnabled())
                return;

            Scan(GhostCompilerServiceLoader.service);
            ButtonsBarOnGUI(GhostCompilerServiceLoader.service);
            EditorGUILayout.Separator();
            OptionsOnGUI(GhostCompilerServiceLoader.service);
            EditorGUILayout.Separator();
            TemplateFoldersOnGUI(GhostCompilerServiceLoader.service);
            EditorGUILayout.Separator();
            ComponentsDataOnGUI(GhostCompilerServiceLoader.service);
        }

        private void EnableDisableOnGUI()
        {
            using (new GUILayout.HorizontalScope())
            {
                var icon = GhostCompilerServiceLoader.IsGhostCompilationEnabled() ? _greenIcon.image : _redIcon.image;

                if (GUILayout.Button(new GUIContent(icon), GUILayout.ExpandWidth(false),
                    GUILayout.MaxHeight(38), GUILayout.ExpandHeight(false)))
                {
                    if (GhostCompilerServiceLoader.IsGhostCompilationEnabled())
                    {
                        GhostCompilerServiceLoader.DisableGhostCompilation();
                    }
                    else
                    {
                        GhostCompilerServiceLoader.EnableGhostCompilation();
                    }
                }

                if (GhostCompilerServiceLoader.IsGhostCompilationEnabled())
                {
                    EditorGUILayout.HelpBox("Ghost Compilation is enabled", MessageType.Info, true);
                }
                else
                {
                    EditorGUILayout.HelpBox("Ghost Compilation is disabled!", MessageType.Warning, true);
                }
            }
        }


        private void OptionsOnGUI(GhostCompilerService service)
        {
            var optionLocked = EditorPrefs.GetBool("GhostComp/OptionLock", false);
            using (new GUILayout.HorizontalScope())
            {
                var lockIcon = optionLocked ? _lockedIcon : _unlockedIcon;
                if (GUILayout.Button(lockIcon, _boxStyle, GUILayout.ExpandWidth(false)))
                {
                    optionLocked = !optionLocked;
                }

                EditorPrefs.SetBool("GhostComp/OptionLock", optionLocked);
                GUILayout.Box("Options", _boxStyle, GUILayout.ExpandWidth(true));
            }

            using (new EditorGUI.DisabledGroupScope(service.IsCompilationDisabled || optionLocked))
            {
                var settings = service.Settings;
                EditorGUI.BeginChangeCheck();
                settings.tempOutputFolderPath =
                    EditorGUILayout.TextField("Temp Build Folder", settings.tempOutputFolderPath);
                settings.outputFolder = EditorGUILayout.TextField("Output Folder", settings.outputFolder);
                settings.autoRecompile = EditorGUILayout.ToggleLeft("Auto Compile", settings.autoRecompile);
                settings.alwaysGenerateFiles =
                    EditorGUILayout.ToggleLeft("Always Generate Files", settings.alwaysGenerateFiles);
                settings.excludeFlags = (AssemblyFilterExcludeFlag) EditorGUILayout.EnumFlagsField("Exclude Assembly",
                    settings.excludeFlags, GUILayout.ExpandWidth(false), GUILayout.MinWidth(300));
                if (EditorGUI.EndChangeCheck())
                {
                    service.SaveSettings();
                }
            }
        }

        private void ButtonsBarOnGUI(GhostCompilerService service)
        {
            GUILayout.Box("Actions", _boxStyle, GUILayout.ExpandWidth(true));
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledGroupScope(service.IsCompilationDisabled))
                {
                    if (GUILayout.Button("Build", GUILayout.MinHeight(50)))
                    {
                        service.ManualRegeneration(_exludeAssemblyAndTypes);
                    }

                    if (GUILayout.Button("Rescan", GUILayout.MinHeight(50)))
                    {
                        _serializedCommandsAndComponents = null;
                        Scan(service);
                    }

                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void ComponentsDataOnGUI(GhostCompilerService service)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Box(new GUIContent("Serialized Components"), _boxStyle, GUILayout.ExpandWidth(true));

                if (service.HasPendingChanges())
                {
                    EditorGUILayout.HelpBox("Something changed. You need to recompile", MessageType.Warning);
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    _foldoutStyle = new GUIStyle(EditorStyles.foldout);
                    _foldoutStyle.fixedHeight = 18;
                    _foldoutStyle.stretchWidth = true;
                    _foldoutStyle.fixedWidth = EditorGUIUtility.currentViewWidth;
                    _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.ExpandHeight(true),
                        GUILayout.MaxHeight(500));

                    foreach(var kv in _serializedCommandsAndComponents)
                    {
                        var icon = service.IsAssemblyChanged(kv.Key)
                            ? _assemblyNeedRecompileIcon
                            : _assemblyOkIcon;
                        bool showContent = _foldedAssembly[kv.Key];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(icon, GUILayout.ExpandWidth(false), GUILayout.MaxWidth(20));
                            var oldExclude = _exludeAssemblyAndTypes.Contains(kv.Key);
                            //Need to use ! because I should display the inverse of the selection (the included type)
                            var newExclude = !EditorGUILayout.Toggle(!oldExclude, GUILayout.ExpandWidth((false)), GUILayout.MaxWidth(20));
                            showContent = EditorGUILayout.Foldout(showContent, kv.Key, _foldoutStyle);

                            _foldedAssembly[kv.Key] = showContent;
                            if (oldExclude && !newExclude)
                            {
                                _exludeAssemblyAndTypes.Remove(kv.Key);
                                foreach (var t in kv.Value)
                                {
                                    _exludeAssemblyAndTypes.Remove(t.FullName);
                                }
                            }
                            else if(!oldExclude && newExclude)
                            {
                                if (!_exludeAssemblyAndTypes.Contains(kv.Key))
                                {
                                    _exludeAssemblyAndTypes.Add(kv.Key);
                                }
                                foreach (var t in kv.Value)
                                {
                                    if (!_exludeAssemblyAndTypes.Contains(t.FullName))
                                    {
                                        _exludeAssemblyAndTypes.Add(t.FullName);
                                    }
                                }
                            }
                        }

                        if (showContent)
                        {
                            EditorGUI.indentLevel += 1;
                            bool allTypeExcluded = true;
                            foreach (var t in kv.Value)
                            {
                                var rect = EditorGUILayout.GetControlRect(true, 20f,
                                    GUILayout.ExpandWidth(true));
                                rect = EditorGUI.IndentedRect(rect);
                                var toggleRect = rect;
                                toggleRect.width = 30.0f;
                                var oldExcludeType = _exludeAssemblyAndTypes.Contains(t.FullName);
                                //Need to use ! because I should display the inverse of the selection (the included type)
                                var newExcludeType = !EditorGUI.Toggle(toggleRect, !oldExcludeType);
                                rect.x += 20.0f;
                                rect.width -= 20.0f;
                                var scriptIcon = (typeof(IRpcCommand).IsAssignableFrom(t) || typeof(ICommandData).IsAssignableFrom(t)) ? _rpcIcon : _ghostIcon;
                                EditorGUI.LabelField(rect, new GUIContent(t.Name, scriptIcon.image));

                                allTypeExcluded &= newExcludeType;
                                if (oldExcludeType && !newExcludeType)
                                {
                                    _exludeAssemblyAndTypes.Remove(t.FullName);
                                    _exludeAssemblyAndTypes.Remove(kv.Key);
                                }
                                else if (!oldExcludeType && newExcludeType)
                                {
                                    if (!_exludeAssemblyAndTypes.Contains(t.FullName))
                                    {
                                        _exludeAssemblyAndTypes.Add(t.FullName);
                                    }
                                }
                            }

                            if (allTypeExcluded)
                            {
                                if (!_exludeAssemblyAndTypes.Contains(kv.Key))
                                {
                                    _exludeAssemblyAndTypes.Add(kv.Key);
                                }
                            }
                            else
                            {
                                _exludeAssemblyAndTypes.Remove(kv.Key);
                            }
                            EditorGUI.indentLevel -= 1;
                        }
                    }

                    EditorGUILayout.EndScrollView();
                }
            }
        }

        private void TemplateFoldersOnGUI(GhostCompilerService service)
        {
            var filedStyle = new GUIStyle("TextField");
            filedStyle.fixedHeight = 20;
            filedStyle.imagePosition = ImagePosition.ImageLeft;

            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Box("Template Folders", _boxStyle, GUILayout.ExpandWidth(true));
                EditorGUI.indentLevel += 1;
                foreach (var assemblyDef in service.CodegenTemplatesAssemblies)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var folderObject = AssetDatabase.LoadAssetAtPath<Object>(assemblyDef.assetPath);
                        if (GUILayout.Button(new GUIContent(assemblyDef.name, _folderIcon.image), filedStyle))
                        {
                            EditorGUIUtility.PingObject(folderObject);
                        }
                    }
                }

                EditorGUI.indentLevel -= 1;
            }
        }

        private void Scan(GhostCompilerService service)
        {
            if (_serializedCommandsAndComponents == null)
            {
                var provider = service.GetAssemblyProvider();

                var ghostFilter = new GhostComponentFilter();
                var commandFilter = new CommandComponentFilter();
                var ghosts = ghostFilter.Filter(provider.GetAssemblies()).ToDictionary(
                    kv => kv.Item1, kv => new List<Type>(kv.Item2));
                foreach (var kv in commandFilter.Filter(provider.GetAssemblies()))
                {
                    if (!ghosts.TryGetValue(kv.Item1, out var types))
                    {
                        types = new List<Type>();
                        ghosts.Add(kv.Item1, types);
                    }
                    types.AddRange(kv.Item2);
                }


                var temp = new List<KeyValuePair<string, List<Type>>>(ghosts.Count);
                foreach (var kv in ghosts)
                {
                    temp.Add(kv);
                }

                _serializedCommandsAndComponents = temp.OrderBy(kv => kv.Key).ToArray();


                UpdateFoldedDict();
                UpdateToggles();
            }
        }

        private void UpdateToggles()
        {
            var allTypes = new HashSet<string>();
            {
                foreach (var kv in _serializedCommandsAndComponents)
                {
                    allTypes.Add(kv.Key);
                    foreach (var t in kv.Value)
                    {
                        allTypes.Add(t.FullName);
                    }
                }
            }
            var excludeList = new List<string>();
            foreach (var v in _exludeAssemblyAndTypes)
            {
                if (allTypes.Contains(v))
                {
                    excludeList.Add(v);
                }
            }

            _exludeAssemblyAndTypes = excludeList;
        }

        private void UpdateFoldedDict()
        {
            var toRemove = _foldedAssembly.Keys.Where(k =>
                { return !_serializedCommandsAndComponents.Any(kv => kv.Key == k); });

            foreach (var k in toRemove)
            {
                _foldedAssembly.Remove(k);
            }
            foreach (var kv in _serializedCommandsAndComponents)
            {
                _foldedAssembly[kv.Key] = false;
            }
        }
    }
}
