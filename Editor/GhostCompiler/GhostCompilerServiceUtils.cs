using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace Unity.NetCode.Editor.GhostCompiler
{
    internal static class GhostCompilerServiceUtils
    {
        public static void Save(ScriptableObject obj, string assetPath, bool useTextFormat = true)
        {
            Debug.Assert(obj != null);
            Debug.Assert(obj is GhostCompilerService || obj is GeneratedFileGuidCache || obj is GhostCompilerSettings);
            if (Directory.Exists(assetPath))
            {
                throw new ArgumentException($"cannot save a scriptable object to {assetPath}: is a directory!");
            }
            var directory = Path.GetDirectoryName(assetPath);
            Debug.Assert(!string.IsNullOrEmpty(directory), $"invalid path {assetPath}. You must specify a folder where to save the asset");
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            InternalEditorUtility.SaveToSerializedFileAndForget(new UnityEngine.Object[]{obj}, assetPath, useTextFormat);
        }

        public static T Load<T>(string assetPath) where T: ScriptableObject
        {
            var objects = InternalEditorUtility.LoadSerializedFileAndForget(assetPath);
            if (objects != null && objects.Length > 0)
            {
                Debug.Assert(objects.Length == 1);
                Debug.Assert(objects[0] is T, $"object: {objects[0].GetType().Name} - {assetPath}");
                return objects[0] as T;
            }
            return null;
        }

        public static Guid ComputeGuidHashFor(Stream stream)
        {
            var hash = MD5.Create().ComputeHash(stream);
            return new Guid(hash);
        }

        public static Guid ComputeFileGuid(string filename)
        {
            using (var stream = new FileStream(filename, FileMode.Open))
            {
                return ComputeGuidHashFor(stream);
            }
        }

        public static Guid ComputeGuidHashFor(string input)
        {
            var hash = MD5.Create().ComputeHash(System.Text.Encoding.Default.GetBytes(input));
            return new Guid(hash);
        }

        [System.Diagnostics.Conditional("UNITY_GHOST_COMPILER_VERBOSE")]
        public static void DebugLog(string message)
        {
            Debug.LogFormat("[GhostCompiler] {0}", message);
        }

        public static string GetRelativeFilenamePath(string filename)
        {
            var relpath = filename.Replace("\\", "/");
            if (relpath.StartsWith(UnityEngine.Application.dataPath))
                return "Assets" + relpath.Substring(UnityEngine.Application.dataPath.Length);
            return filename;
        }

        public static void CheckoutFile(FileInfo fileInfo)
        {
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                if (UnityEditor.VersionControl.Provider.isActive)
                {
                    var relpath = GetRelativeFilenamePath(fileInfo.FullName);
                    var asset = UnityEditor.VersionControl.Provider.GetAssetByPath(relpath);
                    if (asset != null)
                        UnityEditor.VersionControl.Provider.Checkout(asset, UnityEditor.VersionControl.CheckoutMode.Asset).Wait();
                }
            }
        }

        public static void DeleteFileOrDirectory(string filename)
        {
            FileUtil.DeleteFileOrDirectory(filename);
            FileUtil.DeleteFileOrDirectory(filename + ".meta");
        }
    }
}