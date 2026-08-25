using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;

namespace Unity.NetCode.Editor.Tracing.UI.Tests
{
    class SceneVisualizationOverlayTests : UITestFixture
    {

        Color m_OriginalClientColor;
        Color m_OriginalServerColor;
        Color m_OriginalDiffLineColor;
        bool m_OriginalIsEnabled;
        bool m_OriginalShowPredicted;
        bool m_OriginalShowAuthority;
        bool m_OriginalShowDifference;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            AddTestComponent<TracingUITestComponent>();
            m_OriginalClientColor = SceneVisualizationView.PredictedColor;
            m_OriginalServerColor = SceneVisualizationView.AuthorityColor;
            m_OriginalDiffLineColor = SceneVisualizationView.DiffLineColor;
            m_OriginalIsEnabled = SceneVisualizationView.IsEnabled;
            m_OriginalShowPredicted = SceneVisualizationView.ShowPredicted;
            m_OriginalShowAuthority = SceneVisualizationView.ShowAuthority;
            m_OriginalShowDifference = SceneVisualizationView.ShowDifference;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // Restore the production defaults the tests mutated so the editor tool is left untouched.
            SceneVisualizationView.PredictedColor = m_OriginalClientColor;
            SceneVisualizationView.AuthorityColor = m_OriginalServerColor;
            SceneVisualizationView.DiffLineColor = m_OriginalDiffLineColor;
            SceneVisualizationView.IsEnabled = m_OriginalIsEnabled;
            SceneVisualizationView.ShowPredicted = m_OriginalShowPredicted;
            SceneVisualizationView.ShowAuthority = m_OriginalShowAuthority;
            SceneVisualizationView.ShowDifference = m_OriginalShowDifference;
        }

        [SetUp]
        public void SetUp()
        {
            // Ensure each test starts from the captured defaults regardless of execution order.
            SceneVisualizationView.IsEnabled = m_OriginalIsEnabled;
            SceneVisualizationView.PredictedColor = m_OriginalClientColor;
            SceneVisualizationView.AuthorityColor = m_OriginalServerColor;
            SceneVisualizationView.DiffLineColor = m_OriginalDiffLineColor;
            SceneVisualizationView.ShowPredicted = m_OriginalShowPredicted;
            SceneVisualizationView.ShowAuthority = m_OriginalShowAuthority;
            SceneVisualizationView.ShowDifference = m_OriginalShowDifference;
        }

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
        }

        VisualElement CreateAndAddOverlayContent()
        {
            var content = new SceneVisualizationOverlay().CreatePanelContent();
            rootVisualElement.Add(content);
            simulate.FrameUpdate();
            return content;
        }

        // --- CreatePanelContent() structure ---

        [Test]
        public void CreatePanelContent_ReturnsNonNullVisualElement()
        {
            Assert.That(new SceneVisualizationOverlay().CreatePanelContent(), Is.Not.Null);
        }

        [Test]
        public void CreatePanelContent_ContainsEnabledToggle()
        {
            var content = CreateAndAddOverlayContent();
            Assert.That(content.Q<Toggle>(), Is.Not.Null);
        }

        [Test]
        public void CreatePanelContent_ContainsThreeColorFields()
        {
            var content = CreateAndAddOverlayContent();
            Assert.That(content.Query<ColorField>().ToList().Count, Is.EqualTo(3));
        }

        // --- Default values reflect current static state ---

        [Test]
        public void EnabledToggle_DefaultValue_ReflectsIsEnabled()
        {
            var content = CreateAndAddOverlayContent();
            Assert.That(content.Q<Toggle>().value, Is.EqualTo(SceneVisualizationView.IsEnabled));
        }

        [Test]
        public void ClientColorField_DefaultValue_ReflectsClientColor()
        {
            var content = CreateAndAddOverlayContent();
            var colorField = content.Query<ColorField>().ToList()[0];
            Assert.That(colorField.value, Is.EqualTo(SceneVisualizationView.PredictedColor));
        }

        [Test]
        public void ServerColorField_DefaultValue_ReflectsServerColor()
        {
            var content = CreateAndAddOverlayContent();
            var colorField = content.Query<ColorField>().ToList()[1];
            Assert.That(colorField.value, Is.EqualTo(SceneVisualizationView.AuthorityColor));
        }

        [Test]
        public void DiffLineColorField_DefaultValue_ReflectsDiffLineColor()
        {
            var content = CreateAndAddOverlayContent();
            var colorField = content.Query<ColorField>().ToList()[2];
            Assert.That(colorField.value, Is.EqualTo(SceneVisualizationView.DiffLineColor));
        }

        // --- Value changes propagate to static state ---

        [Test]
        public void EnabledToggle_WhenDisabled_UpdatesIsEnabled()
        {
            var content = CreateAndAddOverlayContent();
            content.Q<Toggle>().value = false;
            Assert.That(SceneVisualizationView.IsEnabled, Is.False);
        }

        [Test]
        public void ClientColorField_WhenChanged_UpdatesClientColor()
        {
            var content = CreateAndAddOverlayContent();
            content.Query<ColorField>().ToList()[0].value = Color.blue;
            Assert.That(SceneVisualizationView.PredictedColor, Is.EqualTo(Color.blue));
        }

        [Test]
        public void ServerColorField_WhenChanged_UpdatesServerColor()
        {
            var content = CreateAndAddOverlayContent();
            content.Query<ColorField>().ToList()[1].value = Color.cyan;
            Assert.That(SceneVisualizationView.AuthorityColor, Is.EqualTo(Color.cyan));
        }

        [Test]
        public void DiffLineColorField_WhenChanged_UpdatesDiffLineColor()
        {
            var content = CreateAndAddOverlayContent();
            content.Query<ColorField>().ToList()[2].value = Color.green;
            Assert.That(SceneVisualizationView.DiffLineColor, Is.EqualTo(Color.green));
        }

        // --- Per-category toggles gate Scene View visibility ---
        // Toggle order: [0] master "Show visualization", [1] Predicted, [2] Authority, [3] Difference.

        [Test]
        public void PredictedToggle_WhenDisabled_UpdatesShowPredicted()
        {
            var content = CreateAndAddOverlayContent();
            content.Query<Toggle>().ToList()[1].value = false;
            Assert.That(SceneVisualizationView.ShowPredicted, Is.False);
        }

        [Test]
        public void AuthorityToggle_WhenDisabled_UpdatesShowAuthority()
        {
            var content = CreateAndAddOverlayContent();
            content.Query<Toggle>().ToList()[2].value = false;
            Assert.That(SceneVisualizationView.ShowAuthority, Is.False);
        }

        [Test]
        public void DifferenceToggle_WhenDisabled_UpdatesShowDifference()
        {
            var content = CreateAndAddOverlayContent();
            content.Query<Toggle>().ToList()[3].value = false;
            Assert.That(SceneVisualizationView.ShowDifference, Is.False);
        }
    }
}
