using System;
using System.Collections;
using NUnit.Framework;
using Unity.Netcode;
using UnityEditor;
using UnityEngine.TestTools;

namespace Tests.Editor
{
    class NetcodeConfigTests
    {
        public bool m_OldRunInBackground;

        [SetUp]
        public void SetUp()
        {
            // Auto connect is on by default, so entering play mode connects for real. runInBackground=false would then
            // make WarnAboutApplicationRunInBackground log an error, failing the play mode transition.
            m_OldRunInBackground = PlayerSettings.runInBackground;
            PlayerSettings.runInBackground = true;
        }

        [TearDown]
        public void TearDown()
        {
            PlayerSettings.runInBackground = m_OldRunInBackground;
        }

        [UnityTest]
        public IEnumerator MakeSure_NetCodeConfig_IsNotNull()
        {
            // Make sure config isn't null while in editor and that it's accessible from editor tools. Make sure state is reset correctly.
            // This test shouldn't use NetCodeTestWorld as we're looking for default editor behaviour, not test behaviour
            Assert.IsNotNull(NetcodeConfig.Global);
            yield return new EnterPlayMode();
            Assert.IsNotNull(NetcodeConfig.Global);
            yield return new ExitPlayMode();
            Assert.IsNotNull(NetcodeConfig.Global);
        }
    }
}
