#if UNITY_EDITOR
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEditor;
using UnityEngine.Analytics;

namespace Unity.NetCode.Analytics
{

    internal interface IAnalyticsSender
    {
        void SendAnalytic(IAnalytic analytic);
    }

    internal static class NetCodeAnalytics
    {
        const string NetCodeGhostConfiguration = "netcode_ghost_configuration";
        const char Separator = ';';

        /// <summary>
        /// Used for mocking.
        /// Default is null which uses EditorAnalytics.
        /// </summary>
        internal static IAnalyticsSender s_AnalyticsSender { get; set; }

        public static void StoreGhostComponent(GhostConfigurationAnalyticsData component)
        {
            var current = SessionState.GetString(NetCodeGhostConfiguration, string.Empty);
            if (current == string.Empty)
            {
                SessionState.SetString(NetCodeGhostConfiguration, JsonUtility.ToJson(component));
                return;
            }

            if (TryGetExisting(component, current, out var existing))
            {
                current = current.Replace(existing, JsonUtility.ToJson(component), StringComparison.Ordinal);
            }
            else
            {
                current += Separator + JsonUtility.ToJson(component);
            }
            SessionState.SetString(NetCodeGhostConfiguration, current);
        }

        static bool TryGetExisting(
            GhostConfigurationAnalyticsData newData,
            string current,
            out string existing)
        {
            var jsonId = @$"{{""id"":""{newData.id}"",";
            if (!current.Contains(jsonId, StringComparison.Ordinal))
            {
                existing = "";
                return false;
            }

            int from = current.IndexOf(jsonId, StringComparison.Ordinal);
            var length = current.IndexOf(Separator, from);
            if (length == -1)
            {
                length = current.Length - from;
            }
            existing = current.Substring(from, length);
            return true;
        }

        public static GhostConfigurationAnalyticsData[] RetrieveGhostComponents()
        {
            var x = SessionState.GetString(NetCodeGhostConfiguration, string.Empty);
            if (string.Empty == x)
            {
                return Array.Empty<GhostConfigurationAnalyticsData>();
            }
            var configurations = x.Split(Separator);
            var res = new GhostConfigurationAnalyticsData[configurations.Length];
            for (var i = 0; i < configurations.Length; i++)
            {
                res[i] = JsonUtility.FromJson<GhostConfigurationAnalyticsData>(configurations[i]);
            }

            return res;
        }

        public static void ClearGhostComponents()
        {
            SessionState.EraseString(NetCodeGhostConfiguration);
        }

        internal static void SendAnalytic(IAnalytic data)
        {
            try
            {
                if(s_AnalyticsSender == null)
                {
                    EditorAnalytics.SendAnalytic(data);
                }
                else
                {
                    s_AnalyticsSender.SendAnalytic(data);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }
    }
}
#endif
