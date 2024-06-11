using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Unity.NetCode.GeneratorTests
{
    class BaseTest
    {
        string? m_OriginalDirectory;
        protected Regex? ErrorLogExclusion;

        [SetUp]
        public void SetupCommon()
        {
            Generators.Debug.LastErrorLog = "";
            m_OriginalDirectory = Environment.CurrentDirectory;
            //This will point to the com.unity.netcode directory
            string? currentDir = m_OriginalDirectory;
            while (currentDir?.Length > 0 && !currentDir.EndsWith("com.unity.netcode", StringComparison.Ordinal))
                currentDir = Path.GetDirectoryName(currentDir);

            if (currentDir == null || !currentDir.EndsWith("com.unity.netcode", StringComparison.Ordinal))
            {
                Assert.Fail("Cannot find com.unity.netcode folder");
                return;
            }

            //Execute in Runtime/SourceGenerators/Source~/Temp
            Environment.CurrentDirectory = Path.Combine(currentDir, "Runtime", "SourceGenerators", "Source~");
            Generators.Profiler.Initialize();
        }

        private bool ErrorLogMatchesExclusion()
        {
            return ErrorLogExclusion != null && ErrorLogExclusion.Matches(Generators.Debug.LastErrorLog).Count > 0;
        }

        [TearDown]
        public void TearDownCommon()
        {
            Environment.CurrentDirectory = m_OriginalDirectory ?? string.Empty;
            if (Generators.Debug.LastErrorLog.Length > 0 && !ErrorLogMatchesExclusion())
            {
                // can't use diagnostics here since there's some parts where it logs directly, bypassing it.
                Assert.Fail("Unexpected error log: "+Generators.Debug.LastErrorLog);
            }

            ErrorLogExclusion = null;
        }
    }
}
