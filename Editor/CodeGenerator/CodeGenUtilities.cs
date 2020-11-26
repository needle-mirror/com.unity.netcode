using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Unity.NetCode.Editor
{
    public class CodeGenNamespaceUtils
    {
        //Namespace generation can be a little tricky.
        //Given the current generated NS: AssemblyName.Generated
        //I assumed the following rules:
        // 1) if the type has no namespace => nothing to consider, is global NS
        // 2) if the type ns namespace has as common ancestor AssemblyName => use type NS
        // 3) if the type ns namespace doesn't have a common prefix with AssemblyName => use type NS
        // 4) if the type ns namespace and AssemblyName has some common prefix => prepend global::
        public static string GetValidNamespaceForType(string generatedNs, string ns)
        {
            //if it is 0 is still part of the ancestor
            if (generatedNs.IndexOf(ns, StringComparison.Ordinal) <= 0)
                return ns;

            //need to use global to avoid confusion
            return "global::" + ns;
        }

        public static string GenerateNamespaceFromAssemblyName(string assemblyGeneratedName)
        {
            return Regex.Replace(assemblyGeneratedName, @"[^\w\.]", "_", RegexOptions.Singleline);
        }
    }
}
