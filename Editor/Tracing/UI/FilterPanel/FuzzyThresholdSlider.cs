using System;
using UnityEngine;

namespace Unity.NetCode.Editor.Tracing.UI.FilterPanel
{
    // The fuzzy-threshold slider's value mapping: the leftmost position is 0 (fuzzy filter off), the rest of
    // the track is a log scale from k_Min to k_Max. The field beside the slider is authoritative and accepts
    // any value >= 0, including outside the slider range.
    internal static class FuzzyThresholdSlider
    {
        internal const float k_Min = 0.01f;
        internal const float k_Max = 10f;
        static readonly float k_LogMin = Mathf.Log10(k_Min);
        static readonly float k_LogMax = Mathf.Log10(k_Max);

        public static float PositionToThreshold(float position)
        {
            if (position <= 0f)
                return 0f;
            var threshold = Mathf.Pow(10f, Mathf.Lerp(k_LogMin, k_LogMax, Mathf.Clamp01(position)));
            return (float)Math.Round(threshold, 4);
        }

        public static float ThresholdToPosition(float threshold)
        {
            if (threshold <= 0f)
                return 0f;
            return Mathf.Clamp01(Mathf.InverseLerp(k_LogMin, k_LogMax, Mathf.Log10(threshold)));
        }
    }
}
