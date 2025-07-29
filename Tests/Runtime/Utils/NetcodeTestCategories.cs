namespace Unity.NetCode.Tests
{
    internal static class NetcodeTestCategories
    {
        internal const string Smoke = "Smoke"; // The number of these tests should be very small (max a dozen) to test if there's some major framework issue.
        internal const string Foundational = "Foundational"; // Running other tests if these don't pass is a waste of time
    }
}
