using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Jobs;
using Unity.NetCode;
using UnityEngine.TestTools;

namespace Tests.Editor
{
    internal class StaticNetcodeAPITests
    {
        public struct TestJob_Managed : IJob
        {
            public static bool didExecute;
            public void Execute()
            {
                var a = Netcode.Instance;
                didExecute = true;
            }
        }

        public struct TestJob_Unmanaged : IJob
        {
            public static bool didExecute;
            public void Execute()
            {
                var a = Netcode.Unmanaged;
                didExecute = true;
            }
        }

        [Test(Description = "Make sure doing unsafe operations guides users and prevents them from doing race conditions")]
        public void TestJobsSafety_FromStaticAPI_Managed()
        {
            LogAssert.Expect(new Regex(".*Static access while in a job is unsafe and unsupported by Unity's job system. Please save the instance you're trying to access as a field inside the job to take full advantage of the jobs safety system.*"));
            new TestJob_Managed().Schedule().Complete();
            Assert.IsFalse(TestJob_Managed.didExecute);
        }

        [Test(Description = "Make sure doing unsafe operations guides users and prevents them from doing race conditions")]
        public void TestJobsSafety_FromStaticAPI_Unmanaged()
        {
            LogAssert.Expect(new Regex(".*Static access while in a job is unsafe and unsupported by Unity's job system. Please save the instance you're trying to access as a field inside the job to take full advantage of the jobs safety system.*"));
            new TestJob_Unmanaged().Schedule().Complete();
            Assert.IsFalse(TestJob_Unmanaged.didExecute);
        }

        [Test]
        public void TestOrder_Initialization()
        {
            Netcode.DisposeAfterEnterEditMode();

            Assert.IsTrue(Netcode.Instance != null);
            Assert.IsTrue(Netcode.Unmanaged.Initialized);

            // swap the order of lazy initialization
            Netcode.DisposeAfterEnterEditMode();
            Assert.IsTrue(Netcode.Unmanaged.Initialized);
            Assert.IsTrue(Netcode.Instance != null);
        }
    }
}
