using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;
using Unity.Transforms;
using System.Reflection;
using UnityEditor.Compilation;

namespace Unity.NetCode.Editor.GhostCompiler
{
    internal struct GhostAssemblyProvider : IGhostAssemblyProvider
    {
        private AssemblyFilterExcludeFlag flags;

        public GhostAssemblyProvider(AssemblyFilterExcludeFlag excludeFlags)
        {
            flags = excludeFlags;
        }

        public IEnumerable<UnityEditor.Compilation.Assembly> GetAssemblies()
        {
            return CompilationPipeline.
                GetAssemblies(AssembliesType.Editor).Where(WillProcess);
        }
        public IEnumerable<UnityEditor.Compilation.Assembly> FilterAssemblies(string[] assembliesNames)
        {
            return CompilationPipeline
                .GetAssemblies(AssembliesType.Editor)
                .Where(a => assembliesNames.Contains(a.name))
                .Where(WillProcess);
        }
        public bool WillProcess(UnityEditor.Compilation.Assembly editorAssembly)
        {
            if (editorAssembly.name.Contains(".Generated"))
                return false;
            if (GhostAuthoringComponentEditor.AssembliesDefaultOverrides.Contains(editorAssembly.name))
                return true;
            if (!editorAssembly.assemblyReferences.Any(a => a.name == "Unity.Entities"))
                return false;
            if (!editorAssembly.assemblyReferences.Any(a => a.name == "Unity.NetCode"))
                return false;
            if (flags.HasFlag(AssemblyFilterExcludeFlag.EditorOnly) && editorAssembly.flags.HasFlag(AssemblyFlags.EditorAssembly))
                return false;
            if (flags.HasFlag(AssemblyFilterExcludeFlag.Tests) && editorAssembly.compiledAssemblyReferences.Any(a => a.EndsWith("nunit.framework.dll")))
                return false;
            return true;
        }
    }

    internal struct GhostComponentFilter
    {
        public HashSet<string> excludeList;
        public IEnumerable<Mono.Cecil.TypeDefinition> Filter(Mono.Cecil.AssemblyDefinition assembly)
        {
            bool IsEnginedSupportedType(Mono.Cecil.TypeDefinition type)
            {
                if (GhostAuthoringComponentEditor.GhostDefaultOverrides.TryGetValue(type.FullName.Replace("/", "+"), out var newComponent))
                    return true;
                return false;
            }

            var excludeTypes = excludeList;
            bool IsGhostPredicate(Mono.Cecil.TypeDefinition t)
            {
                if (!t.IsValueType || t.IsPrimitive || !t.IsIComponentData() || t.IsIRpcCommand())
                    return false;

                if (excludeTypes != null && excludeTypes.Contains(t.FullName))
                    return false;

                // Backward compatibility for Transform and Rotation and other default types here.
                // Customize the below method in case you need to add more
                if (IsEnginedSupportedType(t))
                    return true;

                // Otherwise, for backward compatibility I need to scan for presence of any GhostField.
                return t.HasFields && t.Fields.Any(f => f.HasAttribute<GhostFieldAttribute>());
            }

            var result = new List<Mono.Cecil.TypeDefinition>();
            if (assembly?.Modules != null)
            {
                foreach (var m in assembly.Modules)
                {
                    if (m != null)
                    {
                        result.AddRange(m.GetTypes().Where(IsGhostPredicate));
                    }
                }
            }

            return result;
        }

        //This filter version is used by the GhostCompilerWindow to show the types that are going to be converted
        public List<Tuple<string, Type[]>> Filter(IEnumerable<UnityEditor.Compilation.Assembly> assemblies)
        {
            bool IsEnginedSupportedType(Type type)
            {
                if (GhostAuthoringComponentEditor.GhostDefaultOverrides.TryGetValue(type.FullName, out var newComponent))
                    return true;
                return false;
            }

            bool IsGhostPredicate(Type t)
            {
                if (!t.IsValueType || t.IsPrimitive || !typeof(IComponentData).IsAssignableFrom(t))
                    return false;

                // Backward compatibility for Transform and Rotation and other default types here.
                // Customize the below method in case you need to add more
                if (IsEnginedSupportedType(t))
                    return true;

                // Otherwise, for backward compatibility I need to scan for presence of any GhostField.
                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return fields.Any(f => f.GetCustomAttribute<GhostFieldAttribute>() != null);
            }

            var assemblyWithGhosts = new List<Tuple<string, Type[]>>();
            foreach (var editorAssembly in assemblies)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    a => a.GetName().Name == editorAssembly.name);

                if (assembly == null)
                {
                    UnityEngine.Debug.LogError("Cannot find " + editorAssembly.name);
                    continue;
                }

                var types = assembly.GetTypes().Where(IsGhostPredicate).ToArray();
                if(types.Length > 0)
                {
                    assemblyWithGhosts.Add(new Tuple<string, Type[]>(assembly.GetName().Name, types));
                }
            }

            return assemblyWithGhosts;
        }
    }
    internal struct CommandComponentFilter
    {
        public HashSet<string> excludeList;
        public IEnumerable<Mono.Cecil.TypeDefinition> Filter(Mono.Cecil.AssemblyDefinition assembly)
        {
            var excludeTypes = excludeList;
            bool IsCommandPredicate(Mono.Cecil.TypeDefinition t)
            {
                if (!t.IsValueType || t.IsPrimitive || (!t.IsIRpcCommand() && !t.IsICommandData()))
                    return false;

                if (excludeTypes != null && excludeTypes.Contains(t.FullName))
                    return false;
                return !t.HasAttribute<NetCodeDisableCommandCodeGenAttribute>();
            }

            var result = new List<Mono.Cecil.TypeDefinition>();
            if (assembly?.Modules != null)
            {
                foreach (var m in assembly.Modules)
                {
                    if (m != null)
                    {
                        result.AddRange(m.GetTypes().Where(IsCommandPredicate));
                    }
                }
            }

            return result;
        }

        public List<Tuple<string, Type[]>> Filter(IEnumerable<UnityEditor.Compilation.Assembly> assemblies)
        {
            bool IsCommandPredicate(Type t)
            {
                if (!t.IsValueType || t.IsPrimitive || (!typeof(IRpcCommand).IsAssignableFrom(t) && !typeof(ICommandData).IsAssignableFrom(t)))
                    return false;
                return t.GetCustomAttribute<NetCodeDisableCommandCodeGenAttribute>() == null;
            }

            var assemblyWithRpcs = new List<Tuple<string, Type[]>>();
            foreach (var editorAssembly in assemblies)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    a => a.GetName().Name == editorAssembly.name);

                if (assembly == null)
                {
                    UnityEngine.Debug.LogError("Cannot find " + editorAssembly.name);
                    continue;
                }

                var types = assembly.GetTypes().Where(IsCommandPredicate).ToArray();
                if(types.Length > 0)
                {
                    assemblyWithRpcs.Add(new Tuple<string, Type[]>(assembly.GetName().Name, types));
                }
            }

            return assemblyWithRpcs;
        }
    }
    internal class NetCodeTemplatesProvider : INetCodeTemplateProvider
    {
        private AssemblyFilterExcludeFlag flags;

        public NetCodeTemplatesProvider(AssemblyFilterExcludeFlag excludeFlags)
        {
            flags = excludeFlags;
        }

        public IEnumerable<UnityAssemblyDefinition> GetTemplateFolders()
        {
            var assets = UnityEditor.AssetDatabase.FindAssets("t: asmdef",
                new[] { "Assets", "Packages/com.unity.netcode"});

            //Look for custom asmdef for codegen templates that we would like to track
            return assets.Select(UnityEditor.AssetDatabase.GUIDToAssetPath).Select(path =>
            {
                var assemblyDefinition = UnityEngine.JsonUtility.FromJson<UnityAssemblyDefinition>(System.IO.File.ReadAllText(path));
                if (assemblyDefinition == null)
                    throw new Exception($"File '{path}' does not contain valid asmdef data.");
                assemblyDefinition.assetPath = System.IO.Path.GetDirectoryName(path);
                return assemblyDefinition;
            }).Where(WillProcess);
        }

        public bool WillProcess(UnityAssemblyDefinition asmdef)
        {
            //Do not consider tests templates folders
            if (flags.HasFlag(AssemblyFilterExcludeFlag.Tests))
            {
                if (asmdef.optionalUnityReferences != null &&
                    asmdef.optionalUnityReferences.Any(r => r == "TestAssemblies"))
                    return false;

                if (asmdef.references != null && asmdef.references.Any(r => r == "nunit.framework.dll"))
                    return false;
            }

            //Consider Editor-only or Editor available assembly
            if (asmdef.includePlatforms != null && !asmdef.includePlatforms.Contains("Editor"))
                return false;

            if (asmdef.excludePlatforms != null && asmdef.excludePlatforms.Contains("Editor"))
                return false;

            return asmdef.name.Contains("CodeGenTemplates") &&
                   asmdef.defineConstraints != null &&
                   asmdef.defineConstraints.Contains("NETCODE_CODEGEN_TEMPLATES");
        }
    }
}