using System.Collections;
using NUnit.Framework;
using Unity.NetCode;
using UnityEngine.TestTools;

namespace Tests.Editor
{
    class NetCodeConfigTests
    {
        [UnityTest]
#if ENABLE_CORECLR
        [Explicit("CoreCLR: entering play mode fails domain reload because BindingRegistryLiveProperties..cctor throws MethodAccessException constructing the internal UnityEditor.InspectorUtility.LivePropertyChangedCallback delegate, see https://jira.unity3d.com/browse/UUM-150429")]
#endif
        public IEnumerator MakeSure_NetCodeConfig_IsNotNull()
        {
            // Make sure config isn't null while in editor and that it's accessible from editor tools. Make sure state is reset correctly.
            // This test shouldn't use NetCodeTestWorld as we're looking for default editor behaviour, not test behaviour
            Assert.IsNotNull(NetCodeConfig.Global);
            yield return new EnterPlayMode();
            Assert.IsNotNull(NetCodeConfig.Global);
            yield return new ExitPlayMode();
            Assert.IsNotNull(NetCodeConfig.Global);
        }
    }
}
