using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Netcode.Editor.Tracing;
using Unity.Netcode.Editor.Tracing.UI;
using Unity.Netcode.Editor.Tracing.UI.TracingToolbar;
using Unity.Netcode.Tracing;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;

namespace Tests.Editor.UI.Tracing.TracingToolbar.CustomElements
{
    [TestFixture]
    class TracingTargetFilterTests : UITestFixture
    {
        TracingToolbarView m_TracingToolbar;
        VisualElement m_TracingToolbarElement;
        TracingTargetFilter m_TracingTargetFilter;
        TracingFilterDropdown m_Dropdown;
        bool m_PreviousEnableTracing;
        readonly List<VisualElement> m_TestRows = new();
        readonly List<Action> m_Disposables = new();

        [SetUp]
        public void SetUp()
        {
            m_PreviousEnableTracing = TracingDataAccess.Config.Data.EnableTracing;
            TracingDataAccess.Config.Data.EnableTracing = false;
            CloseTracingToolWindows();

            m_TracingToolbar = new TracingToolbarView();
            m_TracingToolbarElement = m_TracingToolbar.Create();
            m_TracingTargetFilter = m_TracingToolbarElement.Q<TracingTargetFilter>();
            rootVisualElement.Add(m_TracingToolbarElement);
            simulate.FrameUpdate();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var row in m_TestRows)
                rootVisualElement.Remove(row);
            m_TestRows.Clear();

            // Clean up all disposables (event handlers, etc.) to prevent cross-test pollution
            foreach (var disposable in m_Disposables)
            {
                disposable?.Invoke();
            }
            m_Disposables.Clear();

            rootVisualElement.Remove(m_TracingToolbarElement);
            TracingDataAccess.Config.Data.Dispose();
            TracingRecordingTargets.Reset();
            TracingDisplayState.SetShowingTraces(false);
            m_Dropdown = null;
            m_TracingTargetFilter = null;
            m_TracingToolbarElement = null;
            m_TracingToolbar = null;

            // Restore the prior global state so this fixture does not affect other suites.
            TracingDataAccess.Config.Data.EnableTracing = m_PreviousEnableTracing;
        }

        static void CloseTracingToolWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>()
                         .Where(w => w.titleContent != null && w.titleContent.text == "Prediction Tracing Tool")
                         .ToArray())
            {
                window.Close();
            }
        }

        [Test]
        public void AttachToPanel_ButtonHasDropdownButtonClass()
        {
            var button = m_TracingTargetFilter.Q<Button>();
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.DropdownButton));
        }

        [Test]
        public void DefaultState_NothingIsHidden()
        {
            // Nothing is filtered by default.
            Assert.AreEqual(0, GetHiddenTypes().Count);
        }

        [Test]
        public void CreateListItems_ListsGhostInstance_OnlyWhenSelected()
        {
            var system = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(system);

            // The list follows the persisted tracing target selection; force a known one and restore it.
            var backup = CloneSelection(NetcodeTracingTargetSettings.GetSelection());
            try
            {
                NetcodeTracingTargetSettings.SaveSelection(new TracingTargetSelectionFile());
                var items = InvokeCreateListItems();
                Assert.AreEqual(1, items.Count, "An unselected GhostInstance must not appear in the filter list");
                StringAssert.Contains("GhostSendSystem", items[0].Name);
                Assert.IsTrue(items[0].IsSystem);

                // Selecting GhostInstance as a tracing target makes the views show it and lists it here.
                var selection = new TracingTargetSelectionFile();
                selection.entries.Add(new TracingTargetSelectionEntry
                {
                    assemblyQualifiedName = typeof(Unity.Netcode.GhostInstance).AssemblyQualifiedName,
                    kind = (int)TracingTargetKind.Component,
                });
                NetcodeTracingTargetSettings.SaveSelection(selection);
                items = InvokeCreateListItems();
                Assert.AreEqual(2, items.Count);
                StringAssert.Contains("GhostInstance", items[1].Name);
                Assert.IsFalse(items[1].IsUntraced, "GhostInstance is always traced by the backend, never untraced");
                Assert.IsFalse(items[1].IsRemoved, "GhostInstance is always traced by the backend, never removed");
            }
            finally
            {
                NetcodeTracingTargetSettings.SaveSelection(backup);
            }
        }

        static TracingTargetSelectionFile CloneSelection(TracingTargetSelectionFile src)
        {
            var dst = new TracingTargetSelectionFile();
            if (src?.entries == null)
                return dst;
            foreach (var e in src.entries)
            {
                dst.entries.Add(new TracingTargetSelectionEntry
                {
                    assemblyQualifiedName = e.assemblyQualifiedName,
                    kind = e.kind,
                    required = e.required
                });
            }
            return dst;
        }

        // --- Single toggle ---

        [Test]
        public void ToggleItem_AddsTypeToHiddenTypes()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));

            var toggle = BindItemRow(listView, 0).Q<Button>("toggle");
            simulate.FrameUpdate();

            simulate.Click(toggle);

            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));
        }

        // --- Targets out of sync with the current recording (added or removed since it started) ---

        [Test]
        public void UntracedItem_ShowsInfoIconAndHidesEye()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("NewSystem", isSystem: true, isUntraced: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            var info = row.Q<VisualElement>("info");
            simulate.FrameUpdate();

            Assert.IsFalse(info.ClassListContains(TracingToolbarUssClasses.Hidden));
            // Invisible, not removed from layout: the row keeps its height and the eye column its width.
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.Invisible));
            Assert.AreEqual(TracingFilterDropdown.k_TooltipUntracedTarget, info.tooltip);
            Assert.IsNull(toggle.userData, "no visibility action is bound for a target with no data");
        }

        [Test]
        public void RemovedItem_KeepsEyeAndShowsInfo()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("GoneSystem", isSystem: true, isRemoved: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            var info = row.Q<VisualElement>("info");
            simulate.FrameUpdate();

            Assert.IsFalse(info.ClassListContains(TracingToolbarUssClasses.Hidden));
            Assert.AreEqual(TracingFilterDropdown.k_TooltipRemovedTarget, info.tooltip);
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.Invisible));
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));

            // The eye still works: its recorded data can be shown and hidden.
            simulate.Click(toggle);
            Assert.IsTrue(results.hiddenSystems.Contains("GoneSystem"));
        }

        [Test]
        public void RecycledRow_TracedAfterUntraced_RestoresEye()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("NewSystem", isSystem: true, isUntraced: true));
            listView.itemsSource.Add(CreateTestTracingItem("OldSystem", isSystem: true));

            // Bind the untraced item, then rebind the same (recycled) row to a traced one.
            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            var info = row.Q<VisualElement>("info");
            listView.bindItem(row, 1);
            simulate.FrameUpdate();

            Assert.IsTrue(info.ClassListContains(TracingToolbarUssClasses.Hidden));
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.Invisible));
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));
            Assert.AreEqual(TracingFilterDropdown.k_TooltipToggleVisibility, toggle.tooltip);
        }

        [Test]
        public void CreateListItems_MarksAddedAndRemovedTargets_InGroupedOrder()
        {
            TracingDisplayState.SetShowingTraces(true);
            var keptSystem = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            var removedSystem = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostReceiveSystem>();
            var addedSystem = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostUpdateSystem>();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(keptSystem);
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(removedSystem);
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.CaptureFromConfig();
            TracingDataAccess.Config.Data.RemoveSystemTypeToTrace(removedSystem);
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(addedSystem);

            var items = InvokeCreateListItems();

            // Order: in-recording-and-selected, then removed (recorded only), then added (selected only).
            Assert.AreEqual(3, items.Count);
            StringAssert.Contains("GhostSendSystem", items[0].Name);
            Assert.IsFalse(items[0].IsUntraced);
            Assert.IsFalse(items[0].IsRemoved);
            StringAssert.Contains("GhostReceiveSystem", items[1].Name);
            Assert.IsTrue(items[1].IsRemoved, "the deselected target is marked removed");
            StringAssert.Contains("GhostUpdateSystem", items[2].Name);
            Assert.IsTrue(items[2].IsUntraced, "the target added after the recording started is marked untraced");

            Assert.IsTrue(Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.SelectionDiffersFromRecording());
        }

        [Test]
        public void ResumeMerge_ClearsUntracedFlag_ButKeepsRemovedFlag()
        {
            TracingDisplayState.SetShowingTraces(true);
            var keptSystem = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            var removedSystem = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostReceiveSystem>();
            var addedSystem = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostUpdateSystem>();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(keptSystem);
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(removedSystem);
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.CaptureFromConfig();
            TracingDataAccess.Config.Data.RemoveSystemTypeToTrace(removedSystem);
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(addedSystem);

            // Resuming merges the live selection into the recording set: the added target starts being
            // traced (flag gone), while the removed one's traces remain in the data (flag kept).
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.MergeFromConfig();

            var items = InvokeCreateListItems();
            Assert.AreEqual(3, items.Count, "both selected systems and the removed system");
            var removedNames = new List<string>();
            foreach (var item in items)
            {
                Assert.IsFalse(item.IsUntraced, $"{item.Name} should not be flagged untraced after a resume");
                if (item.IsRemoved)
                    removedNames.Add(item.Name);
            }
            Assert.AreEqual(1, removedNames.Count, "the removed target's traces are still in the recording, so it stays flagged");
            StringAssert.Contains("GhostReceiveSystem", removedNames[0]);

            Assert.IsTrue(Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.SelectionDiffersFromRecording(),
                "a removal keeps the top-level indicator on after a resume");
        }

        [Test]
        public void ResumeMerge_WithOnlyAddedTargets_ClearsTheChangedIndicator()
        {
            var system = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            var added = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostReceiveSystem>();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(system);
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.CaptureFromConfig();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(added);
            Assert.IsTrue(Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.SelectionDiffersFromRecording());

            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.MergeFromConfig();

            Assert.IsFalse(Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.SelectionDiffersFromRecording(),
                "only additions were pending, so resuming brings the selection back in sync");
        }

        [Test]
        public void RefreshTargetList_RebuildsAnOpenListInPlace()
        {
            TracingDisplayState.SetShowingTraces(true);
            var listView = InvokeCreateListView();
            Assert.AreEqual(0, listView.itemsSource.Count, "no targets configured yet");

            var system = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            var added = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostReceiveSystem>();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(system);
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.CaptureFromConfig();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(added);

            m_TracingTargetFilter.RefreshTargetList();

            Assert.AreEqual(2, listView.itemsSource.Count, "the open list picked up the selection change");
            Assert.IsTrue(((TracingItemData)listView.itemsSource[1]).IsUntraced, "untraced rows group at the bottom");
        }

        [Test]
        public void ChangedIndicator_ShownOnlyWhileShowingTracesAndSelectionDiffers()
        {
            var system = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            var added = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostReceiveSystem>();
            var indicator = m_TracingTargetFilter.Q<VisualElement>(className: TracingToolbarUssClasses.DropdownChangedIndicator);
            Assert.IsNotNull(indicator);

            // An out-of-sync selection against a captured recording...
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(system);
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.CaptureFromConfig();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(added);

            // ...means nothing while no traces are on screen.
            TracingDisplayState.SetShowingTraces(false);
            m_TracingTargetFilter.RefreshChangedIndicator();
            Assert.IsTrue(indicator.ClassListContains(TracingToolbarUssClasses.Hidden), "no displayed results to be out of sync with");

            TracingDisplayState.SetShowingTraces(true);
            m_TracingTargetFilter.RefreshChangedIndicator();
            Assert.IsFalse(indicator.ClassListContains(TracingToolbarUssClasses.Hidden), "a target was added since the displayed recording started");

            TracingDataAccess.Config.Data.RemoveSystemTypeToTrace(added);
            m_TracingTargetFilter.RefreshChangedIndicator();
            Assert.IsTrue(indicator.ClassListContains(TracingToolbarUssClasses.Hidden), "the selection is back in sync");
        }

        [Test]
        public void CreateListItems_WithoutDisplayedTraces_ShowsPlainSelection()
        {
            var system = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostSendSystem>();
            var removed = Unity.Entities.TypeManager.GetSystemTypeIndex<Unity.Netcode.GhostReceiveSystem>();
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(system);
            TracingDataAccess.Config.Data.AddSystemTypeToTrace(removed);
            Unity.Netcode.Editor.Tracing.UI.TracingRecordingTargets.CaptureFromConfig();
            TracingDataAccess.Config.Data.RemoveSystemTypeToTrace(removed);

            TracingDisplayState.SetShowingTraces(false);
            var items = InvokeCreateListItems();

            // No results on screen: no removed rows, no untraced flags — just the selection.
            Assert.AreEqual(1, items.Count);
            Assert.IsFalse(items[0].IsUntraced);
            Assert.IsFalse(items[0].IsRemoved);
        }

        static List<TracingItemData> InvokeCreateListItems() => TracingFilterDropdown.CreateListItems();

        [Test]
        public void ToggleItem_SecondClick_RemovesTypeFromHiddenTypes()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));

            var toggle = BindItemRow(listView, 0).Q<Button>("toggle");
            simulate.FrameUpdate();

            simulate.Click(toggle);
            simulate.Click(toggle);

            Assert.IsFalse(results.hiddenSystems.Contains("SystemA"));
        }

        [Test]
        public void ToggleItem_FirstClick_SetsHiddenClassOnButton()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: false));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");

            simulate.FrameUpdate();
            simulate.Click(toggle);

            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden));
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));
        }

        [Test]
        public void ToggleItem_SecondClick_RestoresVisibleClassOnButton()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: false));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            simulate.FrameUpdate();

            simulate.Click(toggle);
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden));

            simulate.Click(toggle);
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden));
        }

        [Test]
        public void MultipleItems_CanBeHiddenSimultaneously()
        {
            var listView = InvokeCreateListView();
            var results = AddTracingTargetVisibilityChanged();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("SystemB", isSystem: false));

            var toggleA = BindItemRow(listView, 0).Q<Button>("toggle");
            var toggleB = BindItemRow(listView, 1).Q<Button>("toggle");

            simulate.Click(toggleA);
            simulate.Click(toggleB);

            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));
            Assert.IsTrue(results.hiddenComponents.Contains("SystemB"));
        }


        [Test]
        public void HidingOneItem_DoesNotAffectOtherItems()
        {
            var listView = InvokeCreateListView();
            var results = AddTracingTargetVisibilityChanged();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("SystemB", isSystem: false));

            var toggle = BindItemRow(listView, 0).Q<Button>("toggle");
            simulate.FrameUpdate();

            simulate.Click(toggle);

            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));
            Assert.IsFalse(results.hiddenComponents.Contains("SystemB"));
        }


        [Test]
        public void HideOneItem_ShowAnother_LeavesBothInCorrectState()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("SystemB", isSystem: false));

            var rowA = BindItemRow(listView, 0);
            var rowB = BindItemRow(listView, 1);

            simulate.Click(rowA.Q<Button>("toggle")); // hide A
            simulate.Click(rowB.Q<Button>("toggle")); // hide B
            simulate.Click(rowB.Q<Button>("toggle")); // show B again

            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));
            Assert.IsFalse(results.hiddenComponents.Contains("SystemB"));
        }

        // --- Button and Dropdown ---

        [Test]
        public void AttachToPanel_ButtonHasDropdownContainer()
        {
            var button = m_TracingTargetFilter.Q<Button>();
            var container = button.Q<VisualElement>(className: TracingToolbarUssClasses.DropdownContainer);
            Assert.IsNotNull(container, "Button should have dropdown container child");
        }

        [Test]
        public void AttachToPanel_ButtonContainsArrowIcon()
        {
            var button = m_TracingTargetFilter.Q<Button>();
            var arrow = button.Q<VisualElement>(className: "unity-base-popup-field__arrow");
            Assert.IsNotNull(arrow, "Button should contain arrow icon");
        }

        // --- Button Text ---

        [Test]
        public void DropdownButton_ShowsConstantTracingTargetText()
        {
            // The selection counts that used to live on the button now surface in the tick inspector's
            // filter status, so the button is a fixed entry point whatever is selected.
            TracingDataAccess.Config.Data.Init();
            TracingDataAccess.Config.Data.SystemTypesToTrace.Add(1);

            var label = m_TracingTargetFilter.Q<Label>();
            Assert.That(label.text, Is.EqualTo("Tracing Target and Filters"));
        }

        // --- List View Items ---

        [Test]
        public void CreateListView_ItemRowHasToggleButton()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestSystem", isSystem: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            Assert.IsNotNull(toggle, "Row should have a toggle button");
        }

        [Test]
        public void CreateListView_ItemRowHasNameLabel()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestSystem", isSystem: true));

            var row = BindItemRow(listView, 0);
            var label = row.Q<Label>("name");
            Assert.IsNotNull(label, "Row should have a name label");
        }

        [Test]
        public void CreateListView_ItemRowHasTracingTypeIcon()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestSystem", isSystem: true));

            var row = BindItemRow(listView, 0);
            var icon = row.Q<VisualElement>("tracing-type");
            Assert.IsNotNull(icon, "Row should have a tracing type icon");
        }

        [Test]
        public void BindItem_ForSystem_SetsSystemIcon()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestSystem", isSystem: true));

            var row = BindItemRow(listView, 0);
            var icon = row.Q<VisualElement>("tracing-type");
            Assert.IsTrue(icon.ClassListContains(TracingToolbarUssClasses.IconSystem),
                "System item should have IconSystem class");
        }

        [Test]
        public void BindItem_ForComponent_SetsComponentIcon()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestComponent", isSystem: false));

            var row = BindItemRow(listView, 0);
            var icon = row.Q<VisualElement>("tracing-type");
            Assert.IsTrue(icon.ClassListContains(TracingToolbarUssClasses.IconComponent),
                "Component item should have IconComponent class");
        }

        [Test]
        public void BindItem_ExtractsFullyQualifiedNamePartAfterDot()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("Unity.Entities.System", isSystem: true));

            var row = BindItemRow(listView, 0);
            var label = row.Q<Label>("name");
            Assert.AreEqual("System", label.text, "Label should display only the part after the last dot");
        }

        [Test]
        public void BindItem_EvenRows_HaveEvenClass()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("Item0", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("Item1", isSystem: true));

            var evenRow = BindItemRow(listView, 0);
            Assert.IsTrue(evenRow.ClassListContains(TracingToolbarUssClasses.TracingFilterRowEven),
                "Even index row should have TracingFilterRowEven class");
        }

        [Test]
        public void BindItem_OddRows_DoNotHaveEvenClass()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("Item0", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("Item1", isSystem: true));

            var oddRow = BindItemRow(listView, 1);
            Assert.IsFalse(oddRow.ClassListContains(TracingToolbarUssClasses.TracingFilterRowEven),
                "Odd index row should not have TracingFilterRowEven class");
        }

        [Test]
        public void BindItem_InitiallyUnhiddenItem_HasVisibleIcon()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestSystem", isSystem: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible),
                "Initial toggle should have IconVisible class");
        }

        [Test]
        public void UnbindItem_ClearsUserData()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("TestSystem", isSystem: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            Assert.IsNotNull(toggle.userData, "Toggle should have userData (callback) set after bind");

            // Unbind the item
            listView.unbindItem(row, 0);

            Assert.IsNull(toggle.userData, "Toggle userData should be cleared after unbind");
        }

        // --- Event Handler Tests ---

        [Test]
        public void OnVisibilityTargetsChanged_IsInvokedWhenSystemToggled()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));

            var toggle = BindItemRow(listView, 0).Q<Button>("toggle");
            simulate.FrameUpdate();
            simulate.Click(toggle);

            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"), "Event should be invoked with system in hidden systems");
            Assert.AreEqual(0, results.hiddenComponents.Count, "Hidden components should be empty");
        }

        [Test]
        public void OnVisibilityTargetsChanged_IsInvokedWhenComponentToggled()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("ComponentA", isSystem: false));

            var toggle = BindItemRow(listView, 0).Q<Button>("toggle");
            simulate.FrameUpdate();
            simulate.Click(toggle);

            Assert.IsTrue(results.hiddenComponents.Contains("ComponentA"), "Event should be invoked with component in hidden components");
            Assert.AreEqual(0, results.hiddenSystems.Count, "Hidden systems should be empty");
        }

        [Test]
        public void OnVisibilityTargetsChanged_PassesSeparateSystemAndComponentTypes()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("ComponentA", isSystem: false));
            listView.itemsSource.Add(CreateTestTracingItem("ComponentB", isSystem: false));

            var systemToggle = BindItemRow(listView, 0).Q<Button>("toggle");
            var componentToggleA = BindItemRow(listView, 1).Q<Button>("toggle");
            var componentToggleB = BindItemRow(listView, 2).Q<Button>("toggle");

            simulate.FrameUpdate();
            simulate.Click(systemToggle);
            simulate.Click(componentToggleA);
            simulate.Click(componentToggleB);

            Assert.AreEqual(1, results.hiddenSystems.Count, "Should have 1 hidden system");
            Assert.AreEqual(2, results.hiddenComponents.Count, "Should have 2 hidden components");
            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));
            Assert.IsTrue(results.hiddenComponents.Contains("ComponentA"));
            Assert.IsTrue(results.hiddenComponents.Contains("ComponentB"));
        }

        [Test]
        public void HiddenTypesSurviveDetachAndReattach()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));
            listView.itemsSource.Add(CreateTestTracingItem("ComponentA", isSystem: false));

            var systemToggle = BindItemRow(listView, 0).Q<Button>("toggle");
            var componentToggle = BindItemRow(listView, 1).Q<Button>("toggle");

            simulate.FrameUpdate();
            simulate.Click(systemToggle);
            simulate.Click(componentToggle);

            Assert.AreEqual(1, GetHiddenSystemTypes().Count);
            Assert.AreEqual(1, GetHiddenComponentTypes().Count);

            var parent = m_TracingTargetFilter.parent;
            m_TracingTargetFilter.RemoveFromHierarchy();
            simulate.FrameUpdate();

            Assert.IsTrue(GetHiddenSystemTypes().Contains("SystemA"), "Hidden systems must survive detach");
            Assert.IsTrue(GetHiddenComponentTypes().Contains("ComponentA"), "Hidden components must survive detach");

            parent.Add(m_TracingTargetFilter);
            simulate.FrameUpdate();

            Assert.AreEqual(1, GetHiddenSystemTypes().Count, "Re-attach must not change the hidden systems");
            Assert.AreEqual(1, GetHiddenComponentTypes().Count, "Re-attach must not change the hidden components");
        }

        [Test]
        public void MultipleToggleCycles_ProducesCorrectFinalState()
        {
            var results = AddTracingTargetVisibilityChanged();
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));

            var toggle = BindItemRow(listView, 0).Q<Button>("toggle");
            simulate.FrameUpdate();

            // Toggle multiple times: off, on, off
            simulate.Click(toggle); // Hide
            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));

            simulate.Click(toggle); // Show
            Assert.IsFalse(results.hiddenSystems.Contains("SystemA"));

            simulate.Click(toggle); // Hide again
            Assert.IsTrue(results.hiddenSystems.Contains("SystemA"));
        }

        [Test]
        public void ToggleButton_HasCorrectInitialVisibleClass()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");

            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible),
                "Initial state should show IconVisible");
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden),
                "Initial state should not show IconHidden");
        }

        [Test]
        public void ToggleButton_IconTogglesWithEachClick()
        {
            var listView = InvokeCreateListView();
            listView.itemsSource.Clear();
            listView.itemsSource.Add(CreateTestTracingItem("SystemA", isSystem: true));

            var row = BindItemRow(listView, 0);
            var toggle = row.Q<Button>("toggle");
            simulate.FrameUpdate();

            // First click - hide
            simulate.Click(toggle);
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden));
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));

            // Second click - show
            simulate.Click(toggle);
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden));

            // Third click - hide again
            simulate.Click(toggle);
            Assert.IsTrue(toggle.ClassListContains(TracingToolbarUssClasses.IconHidden));
            Assert.IsFalse(toggle.ClassListContains(TracingToolbarUssClasses.IconVisible));
        }


        // --- Helpers ---

        // The dropdown is created through the attached filter, so the tests exercise the real event
        // chain (dropdown -> filter -> toolbar) against the filter's own persistent state.
        ListView InvokeCreateListView()
        {
            m_Dropdown ??= m_TracingTargetFilter.CreateDropdown();
            return m_Dropdown.CreateTargetListView();
        }

        VisualElement BindItemRow(ListView listView, int index)
        {
            var row = m_Dropdown.MakeItemRow();
            rootVisualElement.Add(row);
            m_TestRows.Add(row);
            simulate.FrameUpdate();
            listView.bindItem(row, index);
            return row;
        }

        static TracingItemData CreateTestTracingItem(string name, bool isSystem, bool isUntraced = false, bool isRemoved = false)
            => new() { Name = name, IsSystem = isSystem, IsUntraced = isUntraced, IsRemoved = isRemoved };

        HashSet<string> GetHiddenSystemTypes() => m_TracingTargetFilter.State.HiddenSystems;

        HashSet<string> GetHiddenComponentTypes() => m_TracingTargetFilter.State.HiddenComponents;

        List<string> GetHiddenTypes()
        {
            // Combine both system and component hidden types for backward compatibility
            var combined = new List<string>();
            combined.AddRange(GetHiddenSystemTypes());
            combined.AddRange(GetHiddenComponentTypes());
            return combined;
        }

        (HashSet<string> hiddenSystems, HashSet<string> hiddenComponents) AddTracingTargetVisibilityChanged()
        {
            var hiddenSystems = new HashSet<string>();
            var hiddenComponents = new HashSet<string>();
            void OnVisibilityChanged(HashSet<string> systemTypes, HashSet<string> componentTypes)
            {
                hiddenSystems.Clear();
                hiddenSystems.UnionWith(systemTypes);
                hiddenComponents.Clear();
                hiddenComponents.UnionWith(componentTypes);
            }

            m_TracingToolbar.OnTracingTargetVisibilityChanged += OnVisibilityChanged;
            m_Disposables.Add(() => m_TracingToolbar.OnTracingTargetVisibilityChanged -= OnVisibilityChanged);
            return (hiddenSystems, hiddenComponents);
        }
    }
}
