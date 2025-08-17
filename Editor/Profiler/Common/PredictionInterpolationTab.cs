using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class PredictionInterpolationTab : NetcodeProfilerTab
    {
        TabHeader m_TabHeader;
        MultiColumnListView m_ListView;
        List<PredictionErrorData> m_ItemList = new();

        internal PredictionInterpolationTab(NetworkRole networkRole)
            : base("Prediction and Interpolation", networkRole.ToString())
        {
            if (networkRole == NetworkRole.Server)
            {
                var infoLabel = new Label("Prediction and Interpolation stats are not available for the Server.\n" +
                    "Only Clients can use prediction to manage latency and improve responsiveness.");
                Add(infoLabel);
                return;
            }

            var networkRolePrefix = networkRole.ToString();
            m_TabHeader = new TabHeader("Prediction and Interpolation Overview",
                new List<string>
                {
                    "Time scale",
                    "Interpolation delay",
                    "Interpolation scale"
                });
            Add(m_TabHeader);
            viewDataKey = networkRolePrefix + nameof(PredictionInterpolationTab);
            m_ListView ??= CreatePredictionInterpolationListView(networkRolePrefix + nameof(PredictionInterpolationTab) + "TreeView", m_ItemList);
            Add(m_ListView);
        }

        static MultiColumnListView CreatePredictionInterpolationListView(string viewDataKey, List<PredictionErrorData> itemList)
        {
            var listView = new MultiColumnListView { viewDataKey = viewDataKey };

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

            listView.columns["errorValue"].comparison = (a, b) => itemList[a].errorValue.CompareTo(itemList[b].errorValue);
            listView.columns["name"].comparison = (a, b) => string.Compare(itemList[a].name.ToString(), itemList[b].name.ToString(), System.StringComparison.Ordinal);

            listView.columns["name"].bindCell = (element, index) =>
                ((Label)element).text = itemList[index].name.ToString();

            listView.columns["errorValue"].bindCell = (element, index) =>
                ((Label)element).text = itemList[index].errorValue.ToString();

            listView.itemsSource = itemList;
            listView.sortingMode = ColumnSortingMode.Default;
            listView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;

            listView.makeNoneElement = () =>
            {
                var noneElement = new Label("No prediction errors for this frame.");
                return noneElement;
            };

            return listView;
        }

        internal void Update(NetcodeFrameData frameData)
        {
            if (frameData.tickData.Length == 0)
                return;

            var timeScale = frameData.tickData[0].timeScale.ToString();
            var interpolationDelay = frameData.tickData[0].interpolationDelay.ToString();
            var interpolationScale = frameData.tickData[0].interpolationScale.ToString();

            m_TabHeader.SetText(0, timeScale != "0" ? timeScale : "-");
            m_TabHeader.SetText(1, interpolationDelay != "0" ? interpolationDelay : "-");
            m_TabHeader.SetText(2, interpolationScale != "0" ? interpolationScale : "-");

            m_ItemList.Clear();

            foreach (var predictionErrorData in frameData.tickData[0].predictionErrors)
            {
                if (predictionErrorData.errorValue == 0)
                    continue;
                m_ItemList.Add(predictionErrorData);
            }

            m_ItemList.Sort((a, b) => b.errorValue.CompareTo(a.errorValue));
            m_ListView.itemsSource = m_ItemList;
            m_ListView.RefreshItems();
        }


    }
}
