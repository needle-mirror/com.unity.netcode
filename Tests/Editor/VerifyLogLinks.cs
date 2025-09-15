using System.Collections.Generic;
using NUnit.Framework;
using Unity.NetCode.Editor;

namespace Unity.NetCode.Tests
{
    internal class VerifyLogLinks
    {
        // Simple test to verify calling the log link to 'OpenPlayodeTools' works as expected.
        [Test]
        public void VerifyOpenPlayModeTools()
        {
            bool openBeforeTest = false;
            if (UnityEditor.EditorWindow.HasOpenInstances<MultiplayerPlayModeWindow>())
            {
                openBeforeTest = true;
                UnityEditor.EditorWindow.GetWindow<MultiplayerPlayModeWindow>().Close();
            }

            // Call the hyperlink method
            var args = new Dictionary<string,string>{{"href",NetCodeHyperLinkArguments.s_OpenPlayModeTools.ToString()}};
            MultiplayerPlayModeWindow.HandleHyperLinkArgs( args );

            // we probably need to wait a sec
            Assert.True(UnityEditor.EditorWindow.HasOpenInstances<MultiplayerPlayModeWindow>());

            if (!openBeforeTest)
            {
                UnityEditor.EditorWindow.GetWindow<MultiplayerPlayModeWindow>().Close();
            }
        }
    }
}
