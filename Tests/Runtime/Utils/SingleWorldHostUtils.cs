using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using NUnit.Framework;

namespace Unity.NetCode.Tests
{
    [ExcludeFromCodeCoverage]
    internal static class SingleWorldHostUtils
    {
        /// <summary>
        /// Gets whether the current test supports running with a single world host.
        /// Uses the <see cref="DisableSingleWorldHostTestAttribute"/> to determine if the test or its class has opted out of single world host support.
        /// </summary>
        /// <returns>True if the current test supports single world host, false otherwise.</returns>
        public static bool CurrentTestSupportsSingleWorldHost()
        {
            try
            {
                var test = TestContext.CurrentContext.Test;
                var methodInfo = GetTestMethodInfo(test.ClassName, test.MethodName);

                if (methodInfo == null)
                    return true;

                // Check if the custom attribute exists on the method OR the class
                bool hasDisableAttribute = HasDisableAttribute(methodInfo) || HasDisableAttribute(methodInfo.DeclaringType);

                return !hasDisableAttribute;
            }
            catch
            {
                return true; // Default to true if anything fails
            }
        }

        private static System.Reflection.MethodInfo GetTestMethodInfo(string className, string methodName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(methodName))
                return null;

            var testType = ResolveType(className);
            if (testType == null)
                return null;

            return testType.GetMethod(methodName);
        }

        private static Type ResolveType(string className)
        {
            // Get the type by name
            Type testType = Type.GetType(className);
            if (testType != null)
                return testType;

            // Try to find it in the current assembly if Type.GetType fails
            testType = System.Reflection.Assembly.GetExecutingAssembly().GetType(className);
            if (testType != null)
                return testType;

            // Try all loaded assemblies as last resort.
            // TypeCache doesn't support finding types by full qualified name, so we fall back to
            // AppDomain enumeration. Suppress UAC0005 since there is no direct Unity API replacement
            // for this lookup pattern.
#pragma warning disable UAC0005
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
#pragma warning restore UAC0005
            {
                foreach (var type in GetTypesFromAssembly(assembly))
                {
                    if (type.FullName == className)
                        return type;
                }
            }
            return null;
        }

        private static IEnumerable<Type> GetTypesFromAssembly(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch
            {
                // Some assemblies might not be accessible or might throw exceptions
                return new Type[0];
            }
        }

        private static bool HasDisableAttribute(System.Reflection.MethodInfo methodInfo)
        {
            return methodInfo?.GetCustomAttributes(typeof(DisableSingleWorldHostTestAttribute), true).Length > 0;
        }

        private static bool HasDisableAttribute(Type classType)
        {
            return classType?.GetCustomAttributes(typeof(DisableSingleWorldHostTestAttribute), true).Length > 0;
        }
    }
}
