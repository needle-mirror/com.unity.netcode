using System.Collections.Generic;
#if NETCODE_TRACING_TOOL
using Unity.Netcode.Editor.Tracing.UI;
using UnityEditor;
#endif
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor
{
    class PredictionInterpolationTab : NetcodeProfilerTab
    {
        const string k_PredictionErrorsHeaderUssClass = "prediction-errors-header";
#if NETCODE_TRACING_TOOL
        const string k_OpenPredictionTracingToolText = "Open Prediction Tracing Tool";
        const string k_OpenPredictionTracingToolTooltip =
            "Open the Prediction Tracing Tool to record and inspect per-tick prediction data.";
        const string k_OpenTracingToolButtonUssClass = "open-tracing-tool-button";
        const string k_OpenTracingToolButtonStandaloneUssClass = k_OpenTracingToolButtonUssClass + "--standalone";
        const string k_OpenTracingToolButtonIconUssClass = k_OpenTracingToolButtonUssClass + "__icon";
#endif

        TabHeaderVertical m_InterpolationDataTabHeaderVertical;
        TabHeaderVertical m_TickDataTabHeaderVertical;
        MultiColumnListView m_ListView;
        List<PredictionErrorData> m_ItemList = new();
        ToolbarSearchField m_SearchField;
        Label m_PredictionErrorsTitle;

        internal PredictionInterpolationTab(NetworkRole networkRole)
            : base("Prediction and Interpolation", networkRole)
        {
            // For server, the ServerOnlyInfoTextProvider registered in RegisterInfoTextProviders() will handle the message
            if (m_NetworkRole == NetworkRole.Server)
            {
#if NETCODE_TRACING_TOOL
                // Not registered as a data element so it stays visible alongside the server-only info text
                var serverOpenTracingToolButton = CreateOpenTracingToolButton();
                serverOpenTracingToolButton.AddToClassList(k_OpenTracingToolButtonStandaloneUssClass);
                Add(serverOpenTracingToolButton);
#endif
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

            Add(CreatePredictionErrorsHeader());

            m_SearchField = new ToolbarSearchField();
            var innerInputField = m_SearchField.Q(className: "unity-text-element--inner-input-field-component");
            if (innerInputField != null) innerInputField.name = "search-field";
            m_SearchField.RegisterValueChangedCallback(FilterListView);
            Add(m_SearchField);

            m_ListView ??= CreatePredictionInterpolationListView(networkRolePrefix + nameof(PredictionInterpolationTab) + "TreeView");
            Add(m_ListView);

            // Register elements that should be hidden when info text is displayed
            RegisterDataElements(m_SearchField, m_ListView, m_InterpolationDataTabHeaderVertical, m_TickDataTabHeaderVertical, m_PredictionErrorsTitle);
        }

        VisualElement CreatePredictionErrorsHeader()
        {
            var header = new VisualElement { name = "prediction-errors-header" };
            header.AddToClassList(k_PredictionErrorsHeaderUssClass);

            m_PredictionErrorsTitle = new Label("Prediction Errors");
            m_PredictionErrorsTitle.AddToClassList("tab-header__main-name");
            header.Add(m_PredictionErrorsTitle);

#if NETCODE_TRACING_TOOL
            header.Add(CreateOpenTracingToolButton());
#endif
            return header;
        }

#if NETCODE_TRACING_TOOL
        static Button CreateOpenTracingToolButton()
        {
            var openTracingToolButton = new Button(TracingWindow.ShowWindow)
            {
                name = "open-prediction-tracing-tool-button",
                tooltip = k_OpenPredictionTracingToolTooltip
            };
            openTracingToolButton.AddToClassList(k_OpenTracingToolButtonUssClass);

            var icon = new Image { image = EditorGUIUtility.IconContent(Constants.PredictionTracingIconPath).image, pickingMode = PickingMode.Ignore };
            icon.AddToClassList(k_OpenTracingToolButtonIconUssClass);
            openTracingToolButton.Add(icon);

            var buttonLabel = new Label(k_OpenPredictionTracingToolText) { pickingMode = PickingMode.Ignore };
            openTracingToolButton.Add(buttonLabel);

            return openTracingToolButton;
        }
#endif

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

        protected override void RegisterInfoTextProviders()
        {
            // Register server-only provider first (always shows for server)
            m_InfoTextManager.RegisterProvider(new ServerOnlyInfoTextProvider(m_NetworkRole));

            // Call base to register default providers
            base.RegisterInfoTextProviders();
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

        internal void Update(NetcodeFrameData frameData)
        {
            // Update info text (automatically manages data element visibility)
            UpdateInfoText(frameData);

            if (m_NetworkRole == NetworkRole.Server)
                return;

            // No data received for this frame
            if (!frameData.isValid) return;

            if (frameData.tickData.Length == 0)
                return;

            var timeScale = frameData.tickData[0].timeScale.ToString();
            var interpolationDelay = frameData.tickData[0].interpolationDelay.ToString();
            var interpolationScale = frameData.tickData[0].interpolationScale.ToString();
            var clientPredictionTick = frameData.tickData[0].predictionTick.ToString();
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
