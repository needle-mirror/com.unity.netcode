using System;
using System.Collections.Generic;
using System.IO;
using Unity.NetCode.Editor.GhostCompiler;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    public class GhostCodeGen
    {
        public override string ToString()
        {
            var replacements = "";
            foreach (var fragment in m_Fragments)
            {
                replacements += $"Key: {fragment.Key}, Template: {fragment.Value.Template}, Content: {fragment.Value.Content}";
            }

            return replacements;
        }

        public Dictionary<string, string> Replacements;
        public Dictionary<string, FragmentData> Fragments => m_Fragments;

        public class Batch
        {
            internal List<Tuple<string, string>> m_PendingOperations = new List<Tuple<string, string>>();

            public bool Flush(bool testOnly = false)
            {
                bool didWriteAnyFile = false;
                foreach (var op in m_PendingOperations)
                {
                    var path = op.Item1;
                    bool writeFile = true;
                    var outFileInfo = new FileInfo(path);
                    if (outFileInfo.Exists)
                    {
                        var prevContent = File.ReadAllText(path);
                        if (prevContent == op.Item2)
                        {
                            writeFile = false;
                        }
                        else if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
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
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!String.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                    }

                    if (writeFile && !testOnly)
                        File.WriteAllText(path, op.Item2);
                    didWriteAnyFile |= writeFile;
                }
                return didWriteAnyFile;
            }
        }

        private Dictionary<string, FragmentData> m_Fragments;
        private string m_FileTemplate;
        private string m_HeaderTemplate;

        static string ConcatPath(string assetPath, string root, string path)
        {
            if (root != "")
                path = Path.Combine(root, path);

            if (path[0] == '/')
                path = Path.Combine("Assets", path.Substring(1));
            else
                path = Path.Combine(assetPath, path);
            path = Path.GetFullPath(path);

            return path;
        }

        public class FragmentData
        {
            public string Template;
            public string Content;
        }

        public void AddTemplateOverrides(string template)
        {
            var templateData = File.ReadAllText(template);
            int regionStart;
            while ((regionStart = templateData.IndexOf("#region")) >= 0)
            {
                while (regionStart > 0 && templateData[regionStart - 1] != '\n' &&
                       char.IsWhiteSpace(templateData[regionStart - 1]))
                {
                    --regionStart;
                }

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
                if (m_Fragments.TryGetValue(regionNameTokens[1], out var fragmentData))
                    fragmentData.Template = regionData;
                else
                    Debug.LogError($"Did not find {regionNameTokens[1]} region to override");

                templateData = templateData.Substring(regionEnd + 1);
            }
        }

        public GhostCodeGen(string template)
        {
            Replacements = new Dictionary<string, string>();

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
                    if (m_Fragments.ContainsKey(regionNameTokens[1]))
                    {
                        Debug.Log($"The template {template} already contains the key [{regionNameTokens[1]}]");
                    }
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
        private GhostCodeGen()
        {}
        public GhostCodeGen Clone()
        {
            var codeGen = new GhostCodeGen();
            codeGen.m_FileTemplate = m_FileTemplate;
            codeGen.m_HeaderTemplate = m_HeaderTemplate;
            codeGen.Replacements = new Dictionary<string, string>();
            codeGen.m_Fragments = new Dictionary<string, FragmentData>();
            foreach (var value in m_Fragments)
            {
                codeGen.m_Fragments.Add(value.Key, new FragmentData{Template = value.Value.Template, Content = ""});
            }
            return codeGen;
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

        public void Append(GhostCodeGen target)
        {
            if (target == null)
                target = this;

            foreach (var fragment in m_Fragments)
            {
                if (!target.m_Fragments.ContainsKey($"{fragment.Key}"))
                {
                    Debug.LogWarning($"Target CodeGen is missing fragment: {fragment.Key}");
                    continue;
                }
                target.m_Fragments[$"{fragment.Key}"].Content += m_Fragments[$"{fragment.Key}"].Content;
            }
        }

        public void AppendFragment(string fragment,
            GhostCodeGen target, string targetFragment = null, string extraIndent = null)
        {
            if (target == null)
                target = this;
            if (targetFragment == null)
                targetFragment = fragment;
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
                throw new InvalidOperationException($"{fragment} is not a valid fragment for the given template");
            if (!target.m_Fragments.ContainsKey($"__{targetFragment}__"))
                throw new InvalidOperationException($"{targetFragment} is not a valid fragment for the given template");

            target.m_Fragments[$"__{targetFragment}__"].Content += m_Fragments[$"__{fragment}__"].Content;
        }

        public string GetFragmentTemplate(string fragment)
        {
            if (!m_Fragments.ContainsKey($"__{fragment}__"))
                throw new InvalidOperationException($"{fragment} is not a valid fragment for the given template");
            return m_Fragments[$"__{fragment}__"].Template;
        }

        public bool HasFragment(string fragment)
        {
            return m_Fragments.ContainsKey($"__{fragment}__");
        }
        public bool GenerateFragment(string fragment, Dictionary<string, string> replacements, GhostCodeGen target = null, string targetFragment = null, string extraIndent = null, bool allowMissingFragment = false)
        {
            if (target == null)
                target = this;
            if (targetFragment == null)
                targetFragment = fragment;
            if (!m_Fragments.ContainsKey($"__{fragment}__") && !allowMissingFragment)
                throw new InvalidOperationException($"{fragment} is not a valid fragment for the given template");
            if (!m_Fragments.ContainsKey($"__{fragment}__") && allowMissingFragment)
                return false;
            if (!target.m_Fragments.ContainsKey($"__{targetFragment}__"))
                throw new InvalidOperationException($"{targetFragment} is not a valid fragment for the given template");
            var content = Replace(m_Fragments[$"__{fragment}__"].Template, replacements);

            if (extraIndent != null)
                content = extraIndent + content.Replace("\n    ", $"\n    {extraIndent}");

            Validate(content, fragment);
            target.m_Fragments[$"__{targetFragment}__"].Content += content;
            return true;
        }

        public void GenerateFile(string assetPath, string rootPath, string fileName, Dictionary<string, string> replacements, Batch batch)
        {
            var filePath = ConcatPath(assetPath, rootPath, fileName);
            var header = Replace(m_HeaderTemplate, replacements);
            var content = Replace(m_FileTemplate, replacements);

            foreach (var keyValue in m_Fragments)
            {
                header = header.Replace(keyValue.Key, keyValue.Value.Content);
                content = content.Replace(keyValue.Key, keyValue.Value.Content);
            }
            content = header + content;
            Validate(content, "Root");
            batch.m_PendingOperations.Add(new Tuple<string, string>(filePath, content));
        }
        private const string k_BeginNamespaceTemplate = @"namespace $(GHOSTNAMESPACE)
{";
        private const string k_EndNamespaceTemplate = @"
}";
    }
}