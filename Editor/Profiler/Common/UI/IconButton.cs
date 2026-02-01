using System;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor
{
    class IconButton : Button
    {
        const string k_UssClassName = MetricsHeader.ussClassName + "__icon-button";

        /// <summary>
        /// A custom button that displays an icon.
        /// Used in the metrics header for tick navigation.
        /// </summary>
        /// <param name="clickEvent">The Action to trigger on button click.</param>
        /// <param name="iconClass">The USS class to apply to the icon element.</param>
        internal IconButton(Action clickEvent, string iconClass, string name)
            : base(clickEvent)
        {
            RemoveFromClassList(ussClassName); // Remove the base button uss class.
            AddToClassList(k_UssClassName);
            this.name = name;
            var iconElement = new VisualElement();
            iconElement.AddToClassList(iconClass);
            Add(iconElement);
        }
    }
}
