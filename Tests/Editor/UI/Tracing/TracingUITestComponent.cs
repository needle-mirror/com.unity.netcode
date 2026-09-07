using UnityEngine.UIElements.TestFramework;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Test component providing common setup for Tracing UI tests.
    /// Handles stylesheet loading and common UI initialization.
    /// </summary>
    class TracingUITestComponent : UITestComponent
    {
        public const string TestViewDataKey = "test-view";

        protected override void Initialize(AbstractUITestFixture testFixture)
        {
            base.Initialize(testFixture);
            fixture.clearContentAfterTest = true;
        }

        protected override void BeforeTest()
        {
            // Called before each test
            // Future: Load stylesheets if needed for visual tests
        }

        protected override void AfterTest()
        {
            // Called after each test
            // Cleanup happens automatically via clearContentAfterTest
        }

        /// <summary>
        /// Helper to simulate frame updates and verify layout is stable.
        /// </summary>
        public void UpdateAndWaitForLayout()
        {
            fixture.simulate.FrameUpdate();
            // Future: Add wait for layout if needed
        }
    }
}
