using System.Collections.Generic;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// Create a class implementing this interface in one of the NetCodeGen assemblies to override the default set of ghosts.
    /// There can only be exactly one instance of this, having more will give you errors.
    /// </summary>
    public interface IGhostDefaultOverridesModifier
    {
        void Modify(Dictionary<string, GhostComponentModifier> overrides);
        void ModifyAlwaysIncludedAssembly(HashSet<string> alwaysIncludedAssemblies);
        void ModifyTypeRegistry(TypeRegistry typeRegistry, string netCodeGenAssemblyPath);
    }
}