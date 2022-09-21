using System;
using System.IO;
using NUnit.Framework;

namespace Unity.NetCode.GeneratorTests
{
    class BaseTest
    {
        protected string originalDirectory;

        [SetUp]
        public void SetupCommon()
        {
            originalDirectory = Environment.CurrentDirectory;
            //This will point to the com.unity.netcode directory
            var currentDir = originalDirectory;
            while (currentDir?.Length > 0 && !currentDir.EndsWith("com.unity.netcode"))
                currentDir = Path.GetDirectoryName(currentDir);

            if(!currentDir.EndsWith("com.unity.netcode"))
                Assert.Fail("Cannot find com.unity.netcode folder");

            //Execute in Runtime/SourceGenerators/Source~/Temp
            Environment.CurrentDirectory = Path.Combine(currentDir, "Runtime", "SourceGenerators", "Source~");
            Generators.Profiler.Initialize();
        }
        [TearDown]
        public void TearDownCommon()
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }
}
