#if UNITY_EDITOR
using Unity.Netcode.Hybrid;

namespace Unity.Netcode.Tests
{
    /// <summary>
    /// Helper functions that helps building playmode tests.
    /// </summary>
    internal static class PlaymodeUtils
    {
        /// <summary>
        /// Helper function to set the current build-target to client-only.
        /// Can be executed before a build by passing "-executeMethod Unity.Netcode.Tests.PlaymodeUtils.SetClientBuild" when launching the editor through command line.
        /// </summary>
        public static void SetClientBuild()
        {
            NetcodeClientSettings.instance.ClientTarget = NetcodeClientTarget.Client;
            NetcodeClientSettings.instance.Save();
        }
    }
}
#endif
