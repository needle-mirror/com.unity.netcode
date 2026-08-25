using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Unity.NetCode.Tests
{
    class DisableSingleWorldHostTestAttribute : CategoryAttribute
    {
        /// <summary>
        /// For annotation purposes only, this isn't used anywhere. Documents why the test is permanently disabled for single world host.
        /// </summary>
        /// <param name="isPermanentReason"></param>
        public DisableSingleWorldHostTestAttribute(string isPermanentReason) { }
        public DisableSingleWorldHostTestAttribute() { }
    }
}
