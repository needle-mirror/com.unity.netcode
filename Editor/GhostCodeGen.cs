using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    public class GhostCodeGen
    {
        public class Batch
        {
            internal List<Tuple<string, string>> m_PendingOperations = new List<Tuple<string, string>>();

            public void Flush()
            {
                foreach (var op in m_PendingOperations)
                {
                    File.WriteAllText(op.Item1, op.Item2);
                }
            }
        }
        private Dictionary<string, FragmentData> m_Fragments;
        private string m_FileTemplate;
        private string m_HeaderTemplate;

        public static string GetPrefabAssetPath(GameObject go)
        {
            string assetPath = "";
            var prefabStage = PrefabStageUtility.GetPrefabStage(go);
            if (prefabStage != null)
#if UNITY_2020_1_OR_NEWER
                assetPath = prefabStage.assetPath;
#else
                assetPath = prefabStage.prefabAssetPath;
#endif
            else
                assetPath = AssetDatabase.GetAssetPath(go);
            if (String.IsNullOrEmpty(assetPath))
                assetPath = "Assets";
            else
                assetPath = Path.GetDirectoryName(assetPath);
            return assetPath;
        }

        static string PreparePathForWriting(string assetPath, string root, string path)
        {
            if (root != "")
                path = Path.Combine(root, path);
            return PreparePathForWriting(assetPath, path);
        }

        static string PreparePathForWriting(string assetPath, string path)
        {
            if (path[0] == '/')
                path = Path.Combine("Assets", path.Substring(1));
            else
                path = Path.Combine(assetPath, path);
            path = Path.GetFullPath(path);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                if (UnityEditor.VersionControl.Provider.isActive)
                {
                    var relpath = path.Replace("\\", "/");
                    if (relpath.StartsWith(Application.dataPath))
                        relpath = "Assets" + relpath.Substring(Application.dataPath.Length);
                    var asset = UnityEditor.VersionControl.Provider.GetAssetByPath(relpath);
                    if (asset != null)
                        UnityEditor.VersionControl.Provider
                            .Checkout(asset, UnityEditor.VersionControl.CheckoutMode.Asset).Wait();
                }

                //else
                //    File.SetAttributes(path, File.GetAttributes(path)&~FileAttributes.ReadOnly);
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }

            return path;
        }

        class FragmentData
        {
            public string Template;
            public string Content;
        }

        public GhostCodeGen(string template)
        {
            m_Fragments = new Dictionary<string, FragmentData>();
            m_HeaderTemplate = "";

            var templateData = File.ReadAllText(template);
            int regionStart;
            while ((regionStart = templateData.IndexOf("#region")) >= 0)
            {
                while (regionStart > 0 && templateData[regionStart - 1] != '\n' &&
                       char.IsWhiteSpace(templateData[regionStart - 1]))
                {
                    --regionStart;
                }

                var pre = templateData.Substring(0, regionStart);

                var regionNameEnd = templateData.IndexOf("\n", regionStart);
                var regionNameLine = templateData.Substring(regionStart, regionNameEnd - regionStart);
                var regionNameTokens = System.Text.RegularExpressions.Regex.Split(regionNameLine.Trim(), @"\s+");
                if (regionNameTokens.Length != 2)
                    throw new InvalidOperationException($"Invalid region in GhostCodeGen template {template}");
                var regionEnd = templateData.IndexOf("#endregion", regionStart);
                if (regionEnd < 0)
                    throw new InvalidOperationException($"Invalid region in GhostCodeGen template {template}");
                while (regionEnd > 0 && templateData[regionEnd - 1] != '\n' &&
                       char.IsWhiteSpace(templateData[regionEnd - 1]))
                {
                    if (regionEnd <= regionStart)
                        throw new InvalidOperationException($"Invalid region in GhostCodeGen template {template}");
                    --regionEnd;
                }

                var regionData = templateData.Substring(regionNameEnd + 1, regionEnd - regionNameEnd - 1);
                if (regionNameTokens[1] == "__END_HEADER__")
                {
                    m_HeaderTemplate = pre;
                    pre = "";
                }
                else
                {
                    m_Fragments.Add(regionNameTokens[1], new FragmentData{Template = regionData, Content = ""});
                    pre += regionNameTokens[1];
                }

                regionEnd = templateData.IndexOf("\n", regionEnd);
                var post = "";
                if (regionEnd >= 0)
                    post = templateData.Substring(regionEnd + 1);
                templateData = pre + post;
            }

            m_FileTemplate = templateData;
        }

        private void Validate(string content, string fragment)
        {
            if (content.Contains("__"))
            {
                // Missing key, figure out which one
                int nameStart;
                int nameEnd = 0;
                while ((nameStart = content.IndexOf("__", nameEnd)) >= 0)
                {
                    nameEnd = content.IndexOf("__", nameStart+2);
                    if (nameEnd < 0)
                        throw new InvalidOperationException($"Invalid key in GhostCodeGen fragment {fragment}");
                    Debug.LogError($"GhostCodeGen did not replace {content.Substring(nameStart+2, nameEnd-nameStart-2)} in fragment {fragment}");
                    nameEnd += 2;
                }
                throw new InvalidOperationException($"GhostCodeGen failed for fragment {fragment}");
            }
        }

        string Replace(string content, Dictionary<string, string> replacements)
        {
            foreach (var keyValue in replacements)
            {
                content = content.Replace($"__{keyValue.Key}__", keyValue.Value);
            }

            return content;
        }
        public void GenerateFragment(string fragment, Dictionary<string, string> replacements, GhostCodeGen target = null, string targetFragment = null, string extraIndent = null)
        {
            if (target == null)
                target = this;
            if (targetFragment == null)
                targetFragment = fragment;
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
                throw new InvalidOperationException($"{fragment} is not a valid fragment for the given template");
            if (!target.m_Fragments.ContainsKey($"__{targetFragment}__"))
                throw new InvalidOperationException($"{targetFragment} is not a valid fragment for the given template");
            var content = Replace(m_Fragments[$"__{fragment}__"].Template, replacements);

            if (extraIndent != null)
                content = extraIndent + content.Replace("\n    ", $"\n    {extraIndent}");

            Validate(content, fragment);
            target.m_Fragments[$"__{targetFragment}__"].Content += content;
        }

        public void GenerateFile(string assetPath, string rootPath, string fileName, Dictionary<string, string> replacements, Batch batch = null)
        {
            var filePath = PreparePathForWriting(assetPath, rootPath, fileName);
            var header = Replace(m_HeaderTemplate, replacements);
            var content = Replace(m_FileTemplate, replacements);

            foreach (var keyValue in m_Fragments)
            {
                header = header.Replace(keyValue.Key, keyValue.Value.Content);
                content = content.Replace(keyValue.Key, keyValue.Value.Content);
            }
            content = header + AddNamespace(content);
            Validate(content, "Root");
            if (batch != null)
                batch.m_PendingOperations.Add(new Tuple<string, string>(filePath, content));
            else
                File.WriteAllText(filePath, content);
        }
        private const string k_BeginNamespaceTemplate = @"namespace $(GHOSTNAMESPACE)
{";
        private const string k_EndNamespaceTemplate = @"
}";
        private static string AddNamespace(string data)
        {
            var defaultNamespace = GhostAuthoringComponentEditor.DefaultNamespace;
            if (defaultNamespace == "")
                return data;
            data = data
                .Replace("\n    ", "\n        ")
                .Replace("\n[", "\n    [")
                .Replace("\n{", "\n    {")
                .Replace("\n}", "\n    }")
                .Replace("\npublic", "\n    public");

            data = k_BeginNamespaceTemplate.Replace("$(GHOSTNAMESPACE)", defaultNamespace) +
                   data + k_EndNamespaceTemplate;

            return data;
        }
    }
}