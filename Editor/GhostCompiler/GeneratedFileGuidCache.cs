using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.NetCode.Editor.GhostCompiler
{

    // GeneratedFileGuidCache to store the generated MD5's files content hashes. The MD5 hashes are then used to compare the files
    // that we need to copy/synchronize after the code generation
    // The cache is serialized on disk, by default in Library/NetCode/FileCache.assets and in a readable text format
    internal class GeneratedFileGuidCache : ScriptableObject, ISerializationCallbackReceiver
    {
        [Serializable]
        [StructLayout(LayoutKind.Sequential)]
        private struct FileData
        {
            public string name;
            public byte[] guidData;
        }

        [Serializable]
        [StructLayout(LayoutKind.Sequential)]
        private struct AssemblyData
        {
            public string assemblyName;
            public FileData[] fileData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AssemblyEntry
        {
            public int compilationErrors;
            public Dictionary<string, Guid> files;
        }

        [SerializeField] private AssemblyData[] serializedData;
        public Dictionary<string, AssemblyEntry> assemblies = new Dictionary<string, AssemblyEntry>();

        public void OnBeforeSerialize()
        {
            serializedData = new AssemblyData[assemblies.Count];
            int count = 0;
            foreach (var kv in assemblies)
            {
                serializedData[count].assemblyName = kv.Key;
                serializedData[count].fileData = new FileData[kv.Value.files.Count];
                int fileIndex = 0;
                foreach(var file in kv.Value.files)
                {
                    serializedData[count].fileData[fileIndex] = new FileData
                    {
                        name = file.Key,
                        guidData = file.Value.ToByteArray()
                    };;
                    ++fileIndex;
                }
                ++count;
            }
        }

        public void OnAfterDeserialize()
        {
            assemblies.Clear();
            foreach (var assemblyData in serializedData)
            {
                AssemblyEntry entry = new AssemblyEntry
                {
                    files = new Dictionary<string, Guid>(assemblyData.fileData.Length)
                };
                foreach (var file in assemblyData.fileData)
                {
                    entry.files[file.name] = new Guid(file.guidData);
                }
                assemblies.Add(assemblyData.assemblyName, entry);
            }
        }
    }
}