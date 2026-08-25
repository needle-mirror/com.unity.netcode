using System;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.NetCode.Editor.Tracing.UI
{

#if NETCODE_TRACING_TOOL
    [Overlay(typeof(SceneView), k_OverlayId, "Netcode Tracing")]
#endif
    internal class SceneVisualizationOverlay : Overlay
    {
        internal const string k_OverlayId = "netcode-tracing-scene-vis";
        const string k_StyleSheetPath = Constants.Stylesheets + "scene-visualization-overlay.uss";
        const string k_UssClassName = "scene-visualization-overlay";
        const string k_ColorRowUssClassName = "scene-visualization-overlay__color-row";
        const string k_ColorFieldUssClassName = "scene-visualization-overlay__color-field";

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement();
            root.AddToClassList(k_UssClassName);
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(k_StyleSheetPath);
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            var enableToggle = new Toggle("Show visualization") { value = SceneVisualizationView.IsEnabled };
            enableToggle.RegisterValueChangedCallback(evt =>
            {
                SceneVisualizationView.IsEnabled = evt.newValue;
                SceneView.RepaintAll();
            });
            root.Add(enableToggle);

            root.Add(CreateColorRow("Predicted",
                () => SceneVisualizationView.PredictedColor, c => SceneVisualizationView.PredictedColor = c,
                () => SceneVisualizationView.ShowPredicted, b => SceneVisualizationView.ShowPredicted = b));

            root.Add(CreateColorRow("Authority",
                () => SceneVisualizationView.AuthorityColor, c => SceneVisualizationView.AuthorityColor = c,
                () => SceneVisualizationView.ShowAuthority, b => SceneVisualizationView.ShowAuthority = b));

            root.Add(CreateColorRow("Difference",
                () => SceneVisualizationView.DiffLineColor, c => SceneVisualizationView.DiffLineColor = c,
                () => SceneVisualizationView.ShowDifference, b => SceneVisualizationView.ShowDifference = b));

            return root;
        }

        // A category row: an enable checkbox inline with the color field that picks that category's marker color.
        static VisualElement CreateColorRow(string label, Func<Color> getColor, Action<Color> setColor,
            Func<bool> getEnabled, Action<bool> setEnabled)
        {
            var row = new VisualElement();
            row.AddToClassList(k_ColorRowUssClassName);

            var enabledToggle = new Toggle { value = getEnabled() };
            enabledToggle.RegisterValueChangedCallback(evt =>
            {
                setEnabled(evt.newValue);
                SceneView.RepaintAll();
            });
            row.Add(enabledToggle);

            var colorField = new ColorField(label) { value = getColor() };
            colorField.AddToClassList(k_ColorFieldUssClassName);
            colorField.RegisterValueChangedCallback(evt =>
            {
                setColor(evt.newValue);
                SceneView.RepaintAll();
            });
            row.Add(colorField);

            return row;
        }
    }
}
