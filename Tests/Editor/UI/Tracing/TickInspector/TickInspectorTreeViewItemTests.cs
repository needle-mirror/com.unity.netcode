using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.NetCode.Tracing;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;
using Unity.NetCode.Editor.Tracing.UI.TickInspector;

namespace Unity.NetCode.Editor.Tracing.UI.Tests
{
    /// <summary>
    /// Tests for TickInspectorTreeViewItem custom VisualElement.
    /// Demonstrates testing pattern for custom UI components.
    /// </summary>
    class TickInspectorTreeViewItemTests : UITestFixture
    {
        TracingUITestComponent m_TestComponent;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            m_TestComponent = AddTestComponent<TracingUITestComponent>();
        }

        [Test]
        public void Constructor_CreatesItemWithLabel()
        {
            // Act
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            // Assert
            var label = item.Q<Label>(className: TickInspectorUssClasses.TreeViewItemLabel);
            Assert.That(label, Is.Not.Null);
        }

        [Test]
        public void SetText_UpdatesLabelText()
        {
            // Arrange
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();
            const string testText = "Test Tick Item";

            // Act
            item.SetText(testText);
            simulate.FrameUpdate();

            // Assert
            var label = item.Q<Label>(className: TickInspectorUssClasses.TreeViewItemLabel);
            Assert.That(label.text, Is.EqualTo(testText));
        }

        // Visible diff tags on the row, in render order.
        static List<Label> VisibleTags(TickInspectorTreeViewItem item)
        {
            var tags = new List<Label>();
            foreach (var tag in item.Query<Label>(className: TickInspectorUssClasses.TreeViewItemDiffTag).ToList())
            {
                if (tag.resolvedStyle.display != DisplayStyle.None)
                    tags.Add(tag);
            }

            return tags;
        }

        [Test]
        public void SetNode_RendersOneTagPerReason_SelectedRedAndDeselectedGrey()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            // Two reasons on the row, only one of them a selected diff tag.
            item.SetNode(new TickInspectorNode
            {
                NodeType = TickInspectorNodeType.System,
                DisplayName = "MovementSystem",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.SystemOrder | DiffInfo.DiffReasons.ComponentData,
                SelectedDiffReasons = DiffInfo.DiffReasons.SystemOrder,
            });
            simulate.FrameUpdate();

            var tags = VisibleTags(item);
            Assert.That(tags.Count, Is.EqualTo(2), "each reason on the row gets its own tag");

            // Render order follows the declared reason order, so ComponentData comes before SystemOrder.
            Assert.That(tags[0].text, Is.EqualTo("Component data"));
            Assert.That(tags[0].ClassListContains(TickInspectorUssClasses.TreeViewItemDiffTagDeselected), Is.True,
                "a deselected reason still shows, marked grey");
            Assert.That(tags[0].ClassListContains(TickInspectorUssClasses.TreeViewItemDiffTagSelected), Is.False);

            Assert.That(tags[1].text, Is.EqualTo("System order"));
            Assert.That(tags[1].ClassListContains(TickInspectorUssClasses.TreeViewItemDiffTagSelected), Is.True,
                "a selected reason is marked red");
            Assert.That(tags[1].ClassListContains(TickInspectorUssClasses.TreeViewItemDiffTagDeselected), Is.False);
        }

        [Test]
        public void SetNode_TagTooltip_IsTheSharedPerReasonExplanation()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode
            {
                NodeType = TickInspectorNodeType.System,
                DisplayName = "RotationSystem",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.MissingSystem,
                SelectedDiffReasons = DiffInfo.AllDiffReasons,
            });
            simulate.FrameUpdate();

            // Same catalog entry the Filtering options dropdown shows, so a reason reads the same in both.
            var expected = Array.Find(DiffReasonCatalog.All, e => e.Reason == DiffInfo.DiffReasons.MissingSystem).Tooltip;
            Assert.That(expected, Is.Not.Empty);
            Assert.That(VisibleTags(item)[0].tooltip, Is.EqualTo(expected));
        }

        [Test]
        public void DiffReasonCatalog_CoversEveryReason_WithAShortTooltip()
        {
            foreach (DiffInfo.DiffReasons reason in Enum.GetValues(typeof(DiffInfo.DiffReasons)))
            {
                if (reason == DiffInfo.DiffReasons.Undefined)
                    continue;
                var entry = Array.Find(DiffReasonCatalog.All, e => e.Reason == reason);
                Assert.That(entry.Label, Is.Not.Null.And.Not.Empty, $"{reason} has no catalog entry or label");
                Assert.That(entry.Tooltip, Is.Not.Null.And.Not.Empty, $"{reason} has no tooltip");
                Assert.That(entry.Tooltip.Length, Is.LessThanOrEqualTo(60), $"{reason} tooltip is not short");
            }
        }

        [Test]
        public void SetNode_OnRecycledRow_DoesNotLeakPreviousRowsTags()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode
            {
                NodeType = TickInspectorNodeType.System,
                DisplayName = "MovementSystem",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.SystemOrder | DiffInfo.DiffReasons.ComponentData,
                SelectedDiffReasons = DiffInfo.AllDiffReasons,
            });
            simulate.FrameUpdate();
            Assert.That(VisibleTags(item).Count, Is.EqualTo(2));

            // TreeView recycles rows; the second bind must not keep the first row's extra tag around.
            item.SetNode(new TickInspectorNode
            {
                NodeType = TickInspectorNodeType.System,
                DisplayName = "RotationSystem",
                HasDiff = true,
                DiffReasonFlags = DiffInfo.DiffReasons.MissingSystem,
                SelectedDiffReasons = DiffInfo.AllDiffReasons,
            });
            simulate.FrameUpdate();

            var tags = VisibleTags(item);
            Assert.That(tags.Count, Is.EqualTo(1));
            Assert.That(tags[0].text, Is.EqualTo("Missing system"));
        }

        [Test]
        public void SetNode_AppliesNodeTypeVariantClass()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.Ghost, DisplayName = "Player" });
            simulate.FrameUpdate();

            var root = item.Q(className: TickInspectorUssClasses.TreeViewItem);
            Assert.That(root.ClassListContains(TickInspectorUssClasses.TreeViewItemGhost), Is.True);
            Assert.That(root.ClassListContains(TickInspectorUssClasses.TreeViewItemSystem), Is.False);

            // Re-binding the recycled row to another node type swaps the variant class (no leftover icon).
            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.Component, DisplayName = "Speed", ComponentType = typeof(int) });
            simulate.FrameUpdate();
            Assert.That(root.ClassListContains(TickInspectorUssClasses.TreeViewItemComponent), Is.True);
            Assert.That(root.ClassListContains(TickInspectorUssClasses.TreeViewItemGhost), Is.False);
        }

        [Test]
        public void SetNode_DisablesOpenScript_WhenSystemTypeNull()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.System, DisplayName = "Sys", OwningSystemType = null });
            simulate.FrameUpdate();

            var scriptButton = item.Q<Button>(className: TracingWindowUssClasses.ScriptIcon);
            Assert.That(scriptButton.enabledSelf, Is.False);
        }

        [Test]
        public void SetNode_ComponentWindowButton_EnabledInEditMode()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.Component, DisplayName = "Speed", ComponentType = typeof(int) });
            simulate.FrameUpdate();

            // The component inspector does not require Play mode, unlike the system inspector.
            var windowButton = item.Q<Button>(className: TracingWindowUssClasses.OpenWindowIcon);
            Assert.That(windowButton.enabledSelf, Is.True);
        }

        [Test]
        public void SetNode_SystemWindowButton_DisabledInEditMode()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.System, DisplayName = "Sys", OwningSystemType = typeof(TickInspectorTreeViewItemTests) });
            simulate.FrameUpdate();

            // The System Inspector needs a live world, so its button is gated to Play mode.
            var windowButton = item.Q<Button>(className: TracingWindowUssClasses.OpenWindowIcon);
            Assert.That(windowButton.enabledSelf, Is.False);
        }

        [Test]
        public void SetNode_MispredictionTable_ShowsTable_HidesRowChrome()
        {
            var item = new TickInspectorTreeViewItem();
            rootVisualElement.Add(item);
            simulate.FrameUpdate();

            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.MispredictionTable, Detail = new MispredictionDetail() });
            simulate.FrameUpdate();

            var label = item.Q<Label>(className: TickInspectorUssClasses.TreeViewItemLabel);
            var table = item.Q<MispredictionDetailTable>();
            Assert.That(table, Is.Not.Null);
            Assert.That(table.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(label.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));

            // Re-binding the recycled row to a normal node restores the label and hides the table.
            item.SetNode(new TickInspectorNode { NodeType = TickInspectorNodeType.Component, DisplayName = "Speed", ComponentType = typeof(int) });
            simulate.FrameUpdate();
            Assert.That(label.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(table.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [TearDown]
        public void TearDown()
        {
            rootVisualElement.Clear();
        }
    }
}