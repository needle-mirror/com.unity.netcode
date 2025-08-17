using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class BarChartCategory
    {
        MultiColumnListView m_LegendListView;
        internal Action<NetcodeFrameData> Update;

        internal VisualElement legendListViewContainer { get; private set; }
        internal List<LegendEntryData> listViewData { get; } = new();

        internal Button ViewDetailsButton
        {
            get
            {
                return m_ViewDetailsButton ??= new Button {text = "View details"};
            }
        }

        VisualElement MainBarElement { get; }
        Dictionary<string,string> LegendEntryNames { get; }
        List<int> LegendEntryWidths { get; }
        Button m_ViewDetailsButton;

        internal BarChartCategory(string name, string ussClassName, Dictionary<string,string> legendEntryNames, List<int> legendEntryWidths, bool showButton = false)
        {
            MainBarElement = new VisualElement();
            MainBarElement.AddToClassList($"{BarChart.ussClassBarGraph}__element");
            MainBarElement.AddToClassList($"{BarChart.ussClassBarGraph}__element--{ussClassName}");

            LegendEntryNames = legendEntryNames;
            LegendEntryWidths = legendEntryWidths;
            CreateLegend(name, ussClassName, showButton);
        }

        void CreateLegend(string name, string chartUSS, bool showButton = false)
        {
            legendListViewContainer = new VisualElement();
            legendListViewContainer.AddToClassList("legend");

            var legendNameContainer = new VisualElement();
            legendNameContainer.AddToClassList("legend__name-container");
            legendListViewContainer.Add(legendNameContainer);

            var visualIndicator = new VisualElement();
            visualIndicator.AddToClassList("legend__color-indicator");
            visualIndicator.AddToClassList($"legend__color-indicator--{chartUSS}");
            legendNameContainer.Add(visualIndicator);

            var legendName = new Label(name);
            legendNameContainer.Add(legendName);

            m_LegendListView = new MultiColumnListView();
            m_LegendListView.AddToClassList("legend__listview");
            legendListViewContainer.Add(m_LegendListView);

            m_LegendListView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;

            var index = 0;
            foreach (var legendEntryName in LegendEntryNames)
            {
                var column = new Column
                {
                    name = legendEntryName.Key,
                    title = legendEntryName.Value,
                    width = LegendEntryWidths[index]
                };

                m_LegendListView.columns.Add(column);
                column.makeCell = UIFactory.CreateTreeViewLabel;
                var stringIndex = index;
                column.bindCell = (element, i) =>
                {
                    var line = listViewData[i];
                    if (stringIndex >= line.values.Count)
                    {
                        Debug.LogError($"Legend entry {legendEntryName.Key} does not have enough strings");
                        return;
                    }
                    ((Label)element).text = line.values[stringIndex];
                };

                index++;
            }

            if (showButton)
            {
                var linkColumn = new Column
                {
                    name = "link",
                    title = "",
                    width = 109
                };
                m_LegendListView.columns.Add(linkColumn);
                linkColumn.makeCell = () => ViewDetailsButton;
            }

            m_LegendListView.itemsSource = listViewData;
            m_LegendListView.Rebuild();
        }

        internal void Refresh()
        {
            m_LegendListView.Rebuild();
        }
    }
}
