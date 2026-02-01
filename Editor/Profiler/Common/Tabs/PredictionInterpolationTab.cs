using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class PredictionInterpolationTab : NetcodeProfilerTab
    {
        TabHeaderVertical m_InterpolationDataTabHeaderVertical;
        TabHeaderVertical m_TickDataTabHeaderVertical;
        MultiColumnListView m_ListView;
        List<PredictionErrorData> m_ItemList = new();
        ToolbarSearchField m_SearchField;

        internal PredictionInterpolationTab(NetworkRole networkRole)
            : base("Prediction and Interpolation", networkRole)
        {
            if (m_NetworkRole == NetworkRole.Server)
            {
                var infoLabel = new Label("Prediction and Interpolation stats are not available for the Server.\n" +
                    "Only Clients can use prediction to manage latency and improve responsiveness.");
                Add(infoLabel);
                return;
            }

            var tabHeaderContainer = new VisualElement();
            tabHeaderContainer.style.flexDirection = FlexDirection.Row;
            m_InterpolationDataTabHeaderVertical = new TabHeaderVertical("Prediction and Interpolation Overview",
                new List<string>
                {
                    "Time scale",
                    "Interpolation delay",
                    "Interpolation scale"
                });
            tabHeaderContainer.Add(m_InterpolationDataTabHeaderVertical);

            m_TickDataTabHeaderVertical = new TabHeaderVertical(" ",
                new List<string>
                {
                    "Client Prediction Tick",
                    "Interpolation Tick"
                });
            tabHeaderContainer.Add(m_TickDataTabHeaderVertical);

            Add(tabHeaderContainer);

            var networkRolePrefix = networkRole.ToString();
            viewDataKey = networkRolePrefix + nameof(PredictionInterpolationTab);

            m_SearchField = new ToolbarSearchField();
            var innerInputField = m_SearchField.Q(className: "unity-text-element--inner-input-field-component");
            if (innerInputField != null) innerInputField.name = "search-field";
            m_SearchField.RegisterValueChangedCallback(FilterListView);
            Add(m_SearchField);

            m_ListView ??= CreatePredictionInterpolationListView(networkRolePrefix + nameof(PredictionInterpolationTab) + "TreeView");
            Add(m_ListView);
        }

        void FilterListView(ChangeEvent<string> evt)
        {
            var filteredList = FilterPredictionErrorDataList(evt.newValue);
            m_ListView.itemsSource = filteredList;
            m_ListView.RefreshItems();
        }

        MultiColumnListView CreatePredictionInterpolationListView(string listViewDataKey)
        {
            var listView = new MultiColumnListView { viewDataKey = listViewDataKey };

            var columnKeysToColumnTitle = new Dictionary<string, string>
            {
                { "name", "Name" },
                { "errorValue", "Error value" }
            };

            foreach (var columns in columnKeysToColumnTitle)
            {
                listView.columns.Add(new Column { name = columns.Key, title = columns.Value, width = 100 });
            }

            listView.columns["name"].makeCell = UIFactory.CreateTreeViewLabel;
            listView.columns["name"].width = 430;
            listView.columns["errorValue"].makeCell = UIFactory.CreateTreeViewLabel;

            listView.columns["name"].bindCell = (element, index) => BindNameCell(element, index, listView);
            listView.columns["errorValue"].bindCell = (element, index) => BindErrorValueCell(element, index, listView);

            listView.columns["name"].comparison = (a, b) => CompareNameCells(a, b, listView);
            listView.columns["errorValue"].comparison = (a, b) => CompareErrorValueCells(a, b, listView);

            listView.itemsSource = m_ItemList;
            listView.sortingMode = ColumnSortingMode.Default;
            listView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;

            listView.makeNoneElement = () =>
            {
                var noneElement = new Label("No prediction errors for this frame.");
                return noneElement;
            };

            return listView;
        }

        static int CompareErrorValueCells(int a, int b, MultiColumnListView listView)
        {
            var dataA = (PredictionErrorData)listView.itemsSource[a];
            var dataB = (PredictionErrorData)listView.itemsSource[b];
            return dataA.errorValue.CompareTo(dataB.errorValue);
        }

        static int CompareNameCells(int a, int b, MultiColumnListView listView)
        {
            var dataA = (PredictionErrorData)listView.itemsSource[a];
            var dataB = (PredictionErrorData)listView.itemsSource[b];
            return string.Compare(dataA.name.ToString(), dataB.name.ToString(), System.StringComparison.InvariantCultureIgnoreCase);
        }

        static void BindErrorValueCell(VisualElement element, int index, MultiColumnListView listView)
        {
            ((Label)element).text = ((PredictionErrorData)listView.itemsSource[index]).errorValue.ToString();
        }

        static void BindNameCell(VisualElement element, int index, MultiColumnListView listView)
        {
            ((Label)element).text = ((PredictionErrorData)listView.itemsSource[index]).name.ToString();
        }

        internal void ClearTab()
        {
            ShowNoDataInfoLabel(true);
        }

        internal void Update(NetcodeFrameData frameData)
        {
            if (m_NetworkRole == NetworkRole.Server)
                return;

            // No data received for this frame
            var noData = !frameData.isValid;

            ShowNoDataInfoLabel(noData);
            if (noData) return;

            if (frameData.tickData.Length == 0)
                return;

            var timeScale = frameData.tickData[0].timeScale.ToString();
            var interpolationDelay = frameData.tickData[0].interpolationDelay.ToString();
            var interpolationScale = frameData.tickData[0].interpolationScale.ToString();
            var clientPredictionTick = frameData.tickData[0].tick.ToString();
            var interpolationTick = frameData.tickData[0].interpolationTick.ToString();

            m_InterpolationDataTabHeaderVertical.SetText(0, timeScale != "0" ? timeScale : "-");
            m_InterpolationDataTabHeaderVertical.SetText(1, interpolationDelay != "0" ? interpolationDelay : "-");
            m_InterpolationDataTabHeaderVertical.SetText(2, interpolationScale != "0" ? interpolationScale : "-");

            m_TickDataTabHeaderVertical.SetText(0, clientPredictionTick);
            m_TickDataTabHeaderVertical.SetText(1, interpolationTick);

            m_ItemList.Clear();

            foreach (var predictionErrorData in frameData.tickData[0].predictionErrors)
            {
                if (predictionErrorData.errorValue == 0)
                    continue;
                m_ItemList.Add(predictionErrorData);
            }

            var searchField = this.Q<ToolbarSearchField>();
            var filteredList = FilterPredictionErrorDataList(searchField.value);

            filteredList.Sort((a, b) => b.errorValue.CompareTo(a.errorValue));
            m_ListView.itemsSource = filteredList;
            m_ListView.RefreshItems();
        }

        void ShowNoDataInfoLabel(bool noData)
        {
            if (m_SearchField != null) m_SearchField.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            if (m_ListView != null) m_ListView.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            if (m_InterpolationDataTabHeaderVertical != null) m_InterpolationDataTabHeaderVertical.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            if (m_TickDataTabHeaderVertical != null) m_TickDataTabHeaderVertical.style.display = noData ? DisplayStyle.None : DisplayStyle.Flex;
            if (m_NoDataInfoLabels != null) m_NoDataInfoLabels.style.display = noData ? DisplayStyle.Flex : DisplayStyle.None;
        }

        List<PredictionErrorData> FilterPredictionErrorDataList(string filterText)
        {
            if (string.IsNullOrEmpty(filterText))
                return m_ItemList;

            var filteredList = new List<PredictionErrorData>();
            foreach (var item in m_ItemList)
            {
                if (item.name.ToString().IndexOf(filterText, System.StringComparison.InvariantCultureIgnoreCase) >= 0)
                {
                    filteredList.Add(item);
                }
            }
            return filteredList;
        }
    }
}
