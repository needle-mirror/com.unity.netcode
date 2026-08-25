#if UNITY_EDITOR
using System;

namespace Unity.NetCode.Analytics
{
    [Serializable]
    struct GhostConfigurationAnalyticsData : UnityEngine.Analytics.IAnalytic.IData
    {
        public string id;
        public string ghostMode;
        public string optimizationMode;
        public int prespawnedCount;
        public bool autoCommandTarget;
        public int variance;
        public int importance;
        public int maxSendRateHz;

        public override string ToString()
        {
            return $"{nameof(id)}: {id}, " +
                   $"{nameof(ghostMode)}: {ghostMode}, " +
                   $"{nameof(optimizationMode)}: {optimizationMode}, " +
                   $"{nameof(prespawnedCount)}: {prespawnedCount}, " +
                   $"{nameof(autoCommandTarget)}: {autoCommandTarget}, " +
                   $"{nameof(importance)}: {importance}, " +
                   $"{nameof(maxSendRateHz)}: {maxSendRateHz}, " +
                   $"{nameof(variance)}: {variance}";
        }
    }
}
#endif
