using System;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Serialization;

namespace Unity.NetCode.Editor.Analytics
{
    enum Operation
    {
        Connect = 0,
        ClientDisconnect = 1,
        ServerDisconnect = 2,
        ClientReconnect = 3,
        ServerReconnect = 4,
        Timeout = 5
    }

    enum TargetWorld
    {
        AllClients = 0,
        Server = 1,
        ThinClient = 2,
        Client = 3,
    }

    [Serializable]
#if UNITY_2023_2_OR_NEWER
    internal struct PlayModeConnectionChangedData : IAnalytic.IData
#else
    internal struct PlayModeConnectionChangedData
#endif
    {
        public string operation;
        public string targetWorld;
    }

#if UNITY_2023_2_OR_NEWER
    // Schema: com.unity3d.data.schemas.editor.analytics.n4eToolsPlayModeConnectionChanged_v1
    // Taxonomy: editor.analytics.n4eToolsPlayModeConnectionChanged.v1
    [AnalyticInfo(eventName: "n4eToolsPlayModeConnectionChanged", vendorKey: "unity.netcode", version:1, maxEventsPerHour: 1000)]
    internal class PlayModeConnectionChangedAnalytic : IAnalytic
#else
    internal class PlayModeConnectionChangedAnalytic
#endif
    {
        public PlayModeConnectionChangedAnalytic(Operation operation, TargetWorld targetWorld)
        {
            m_Data = new PlayModeConnectionChangedData
            {
                operation = operation.ToString(),
                targetWorld = targetWorld.ToString()
            };
        }

        public PlayModeConnectionChangedAnalytic(Operation operation, World world)
        {
            m_Data = new PlayModeConnectionChangedData()
            {
                operation = operation.ToString()

            };
            if (world.IsServer())
            {
                m_Data.targetWorld = TargetWorld.Server.ToString();
            }
            else if (world.IsThinClient())
            {
                m_Data.targetWorld = TargetWorld.ThinClient.ToString();
            }
            else if (world.IsClient())
            {
                m_Data.targetWorld = TargetWorld.Client.ToString();
            }
        }

#if UNITY_2023_2_OR_NEWER
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            error = null;
            data = m_Data;
            return data != null;
        }
#endif

        private PlayModeConnectionChangedData m_Data;
    }
}
