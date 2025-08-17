using System;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class PercentBar : VisualElement
    {
        readonly Label m_Label = new();
        readonly VisualElement m_Bar = new();

        internal PercentBar()
        {
            AddToClassList("percentage-bar");
            m_Bar.AddToClassList("percentage-bar__indicator");
            m_Label.AddToClassList("percentage-bar__label");
            Add(m_Bar);
            Add(m_Label);
        }

        internal void SetValue(float valueInPercent)
        {
            m_Bar.style.width = new StyleLength(new Length(valueInPercent, LengthUnit.Percent));
            m_Label.text = valueInPercent.ToString() + "%";
        }
    }
}
