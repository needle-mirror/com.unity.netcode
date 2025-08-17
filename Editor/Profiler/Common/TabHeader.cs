using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class TabHeader : VisualElement
    {
        List<Label> m_ColumnValues = new();

        internal TabHeader(string title, List<string> columnNames)
        {
            AddToClassList("tab-header");
            var mainNameLabel = new Label(title);
            mainNameLabel.AddToClassList("tab-header__main-name");
            Add(mainNameLabel);

            for (var i = 0; i < columnNames.Count; i++)
            {
                var columnName = columnNames[i];
                var columnContainer = CreateColumn(columnName);

                if (i == columnNames.Count - 1)
                    columnContainer.AddToClassList("tab-header__sub-element--last");
            }
        }

        VisualElement CreateColumn(string columnName)
        {
            var columnContainer = new VisualElement();
            columnContainer.AddToClassList("tab-header__sub-element");
            var columnNameLabel = new Label(columnName);
            columnNameLabel.AddToClassList("tab-header__sub-element-column-name");
            var columnValueLabel = new Label();
            columnNameLabel.AddToClassList("tab-header__sub-element-column-value");
            m_ColumnValues.Add(columnValueLabel);
            columnContainer.Add(columnNameLabel);
            columnContainer.Add(columnValueLabel);
            Add(columnContainer);
            return columnContainer;
        }

        internal void AddColumn(string columnName)
        {
            ElementAt(childCount-1).RemoveFromClassList("tab-header__sub-element--last");
            var columnContainer = CreateColumn(columnName);
            columnContainer.AddToClassList("tab-header__sub-element--last");
        }

        internal void SetText(int index, string text)
        {
            m_ColumnValues[index].text = text;
        }
    }
}
