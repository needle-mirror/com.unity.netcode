using System;
using UnityEngine;

namespace Unity.NetCode.Editor.GhostCompiler
{
    [Serializable]
    internal class GhostCompilerSettings : ScriptableObject
    {
        public string tempOutputFolderPath;
        public string outputFolder;
        public bool autoRecompile; //default true
        public bool alwaysGenerateFiles;   //default false
        public bool keepOrphans; //default false, only for tests purposes
        public AssemblyFilterExcludeFlag excludeFlags; //default None
    }
}