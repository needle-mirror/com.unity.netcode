using System;
using System.Collections.Generic;
using System.Text;
using Unity.Entities;
using Unity.NetCode.Analytics;
using Unity.Networking.Transport;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine.Analytics;

namespace Unity.NetCode.Editor
{
    [InitializeOnLoad]
    internal static class PlayStateNotifier
    {
        static PlayStateNotifier()
        {
            EditorApplication.playModeStateChanged += ModeChanged;
        }

        static void ModeChanged(PlayModeStateChange playModeState)
        {
            if (playModeState != PlayModeStateChange.ExitingPlayMode || !EditorAnalytics.enabled)
            {
                return;
            }

            GhostComponentAnalytics.SendGhostComponentScale(ComputeScaleData());
            GhostComponentAnalytics.SendGhostComponentConfiguration(ComputeConfigurationData());
        }

        static GhostScaleAnalyticsData ComputeScaleData()
        {
            var data = new GhostScaleAnalyticsData
            {
                Settings = new PlaymodeSettings
                {
                    ThinClientCount = MultiplayerPlayModePreferences.RequestedNumThinClients,
                    SimulatorEnabled = MultiplayerPlayModePreferences.SimulatorEnabled,
                    Delay = MultiplayerPlayModePreferences.PacketDelayMs,
                    DropPercentage = MultiplayerPlayModePreferences.PacketDropPercentage,
                    Jitter = MultiplayerPlayModePreferences.PacketJitterMs,
                    PlayModeType = MultiplayerPlayModePreferences.RequestedPlayType.ToString(),
                    SimulatorPreset = MultiplayerPlayModePreferences.CurrentNetworkSimulatorPreset
                },
                GhostTypes = Array.Empty<GhostTypeData>(),
            };

            uint serializedSent = 0;
            uint amount = 0;

            var numMainClientWorlds = 0;
            var numServerWorlds = 0;
            foreach (var world in World.All)
            {
                var sent = NetCodeAnalyticsState.GetUpdateLength(world);
                if (sent > 0)
                {
                    amount++;
                    serializedSent += sent;
                }

                if (world.IsServer())
                {
                    if (numServerWorlds == 0)
                    {
                        CollectServerData();
                    }
                    numServerWorlds++;
                }
                else if (IsMainClient(world))
                {
                    if (numMainClientWorlds == 0)
                    {
                        CollectMainClientData();
                    }
                    numMainClientWorlds++;
                }

                continue;

                void CollectServerData()
                {
                    data.ServerSpawnedGhostCount = CountSpawnedGhosts(world.EntityManager);

                    data.GhostTypes = CollectGhostTypes(world.EntityManager);
                    data.GhostTypeCount = data.GhostTypes.Length;

                    data.SnapshotTargetSize = TryGetSingleton<NetworkStreamSnapshotTargetSize>(world, out var snapshotTargetSize)
                        ? snapshotTargetSize.Value
                        : NetworkParameterConstants.MaxMessageSize;
                }

                void CollectMainClientData()
                {
                    if (TryGetSingleton(world, out ClientTickRate clientTickRate))
                    {
                        data.ClientTickRate = new WrappedClientTickRate(clientTickRate);
                    }
                    data.MainClientData.NumOfSpawnedGhost = CountSpawnedGhosts(world.EntityManager);
                    if (TryGetSingleton(world, out ClientServerTickRate clientServerTickRate))
                    {
                        data.ClientServerTickRate = new WrappedClientServerTickRate(clientServerTickRate);
                    }

                    var spawnedGhostCount = CountSpawnedGhosts(world.EntityManager);
                    TryGetSingleton<GhostRelevancy>(world, out var ghostRelevancy);
                    using var predictedGhostQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PredictedGhost>());
                    var predictionCount = predictedGhostQuery.CalculateEntityCountWithoutFiltering();
                    TryGetSingleton<PredictionSwitchingAnalyticsData>(world, out var predictionSwitchingAnalyticsData);
                    data.MainClientData = new MainClientData
                    {
                        RelevancyMode = (int)ghostRelevancy.GhostRelevancyMode,
                        NumOfSpawnedGhost = spawnedGhostCount,
                        NumOfPredictedGhosts = predictionCount,
                        NumSwitchToInterpolated = (int)predictionSwitchingAnalyticsData.NumTimesSwitchedToInterpolated,
                        NumSwitchToPredicted = (int)predictionSwitchingAnalyticsData.NumTimesSwitchedToPredicted,
                    };
                }
            }

            if (amount == 0)
            {
                data.AverageGhostInSnapshot = 0;
            }
            else
            {
                data.AverageGhostInSnapshot = (int)(serializedSent / amount);
            }
            data.NumMainClientWorlds = numMainClientWorlds;
            data.NumServerWorlds = numServerWorlds;
            data.NetCodeConfigCount = AssetDatabase.FindAssets($"t:{nameof(NetCodeConfig)}").Length;

            return data;
        }

        static bool TryGetSingleton<T>(World world, out T val) where T : unmanaged, IComponentData
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            return query.TryGetSingleton(out val);
        }

        static bool IsMainClient(World world)
        {
            return world.IsClient() && !world.IsThinClient();
        }

        static GhostConfigurationAnalyticsData[] ComputeConfigurationData()
        {
            var data = NetCodeAnalytics.RetrieveGhostComponents();
            NetCodeAnalytics.ClearGhostComponents();
            return data;
        }

        static int CountSpawnedGhosts(EntityManager entityManager)
        {
            using var q = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>());
            return q.CalculateEntityCountWithoutFiltering();
        }

        static GhostTypeData[] CollectGhostTypes(EntityManager entityManager)
        {
            using var ghostCollectionQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            if (ghostCollectionQuery.CalculateEntityCount() != 1)
            {
                return Array.Empty<GhostTypeData>();
            }

            var ghostCollection = ghostCollectionQuery.GetSingletonEntity();
            var ghostCollectionPrefab = entityManager.GetBuffer<GhostCollectionPrefab>(ghostCollection);
            var ghostCollectionPrefabSerializers =
                entityManager.GetBuffer<GhostCollectionPrefabSerializer>(ghostCollection);

            var ghostTypes = new List<GhostTypeData>();
            for (var index = 0; index < ghostCollectionPrefabSerializers.Length; index++)
            {
                var prefabSerializer = ghostCollectionPrefabSerializers[index];
                var ghostPrefab = ghostCollectionPrefab[index].GhostPrefab;
                var archetype = entityManager.GetChunk(ghostPrefab).Archetype;
                var ghostId = entityManager.GetComponentData<GhostType>(ghostPrefab);
                ghostTypes.Add(new GhostTypeData()
                {
                    GhostId = $"{ghostId.guid0:x}{ghostId.guid1:x}{ghostId.guid2:x}{ghostId.guid3:x}",
                    ChildrenCount = prefabSerializer.NumChildComponents,
                    ComponentCount = archetype.TypesCount,
                    ComponentsWithSerializedDataCount = prefabSerializer.NumComponents
                });
            }
            return ghostTypes.ToArray();
        }
    }

    [Serializable]
    struct GhostTypeData : IAnalytic.IData
    {
        public string GhostId;
        public int ChildrenCount;
        public int ComponentCount;
        public int ComponentsWithSerializedDataCount;

        public override string ToString()
        {
            return $"{nameof(GhostId)}: {GhostId}, " +
                   $"{nameof(ChildrenCount)}: {ChildrenCount}, " +
                   $"{nameof(ComponentCount)}: {ComponentCount}, " +
                   $"{nameof(ComponentsWithSerializedDataCount)}: {ComponentsWithSerializedDataCount}";
        }
    }

    /// <summary>
    /// This struct is used to wrap the <see cref="ClientServerTickRate"/> struct to be serializable.
    /// We ensure here that the data matches the expected format for the analytics.
    /// If you change this struct, you must also update the analytics event in schemata.
    /// </summary>
    [Serializable]
    internal struct WrappedClientServerTickRate
    {
        public int MaxSimulationStepBatchSize;
        public int MaxSimulationStepsPerFrame;
        public int NetworkTickRate;
        public int SimulationTickRate;
        public int TargetFrameRateMode;

        public WrappedClientServerTickRate(ClientServerTickRate clientServerTickRate)
        {
            this.MaxSimulationStepBatchSize = clientServerTickRate.MaxSimulationStepBatchSize;
            this.MaxSimulationStepsPerFrame = clientServerTickRate.MaxSimulationStepsPerFrame;
            this.NetworkTickRate = clientServerTickRate.NetworkTickRate;
            this.SimulationTickRate = clientServerTickRate.SimulationTickRate;
            this.TargetFrameRateMode = (int)clientServerTickRate.TargetFrameRateMode;
        }
    }

    /// <summary>
    /// This struct is used to wrap the <see cref="ClientTickRate"/> struct to be serializable.
    /// We ensure here that the data matches the expected format for the analytics.
    /// If you change this struct, you must also update the analytics event in schemata.
    /// </summary>
    [Serializable]
    internal struct WrappedClientTickRate
    {
        public int CommandAgeCorrectionFraction;
        public int InterpolationDelayCorrectionFraction;
        public int InterpolationDelayJitterScale;
        public int InterpolationDelayMaxDeltaTicksFraction;
        public int InterpolationTimeMS;
        public int InterpolationTimeNetTicks;
        public int InterpolationTimeScaleMax;
        public int InterpolationTimeScaleMin;
        public int MaxExtrapolationTimeSimTicks;
        public int MaxPredictAheadTimeMS;
        public int MaxPredictionStepBatchSizeFirstTimeTick;
        public int MaxPredictionStepBatchSizeRepeatedTick;
        public int PredictionTimeScaleMax;
        public int PredictionTimeScaleMin;
        public int TargetCommandSlack;

        public WrappedClientTickRate(ClientTickRate clientTickRate)
        {
            this.CommandAgeCorrectionFraction = (int)clientTickRate.CommandAgeCorrectionFraction;
            this.InterpolationDelayCorrectionFraction = (int)clientTickRate.InterpolationDelayCorrectionFraction;
            this.InterpolationDelayJitterScale = (int)clientTickRate.InterpolationDelayJitterScale;
            this.InterpolationDelayMaxDeltaTicksFraction = (int)clientTickRate.InterpolationDelayMaxDeltaTicksFraction;
            this.InterpolationTimeMS = (int)clientTickRate.InterpolationTimeMS;
            this.InterpolationTimeNetTicks = (int)clientTickRate.InterpolationTimeNetTicks;
            this.InterpolationTimeScaleMax = (int)clientTickRate.InterpolationTimeScaleMax;
            this.InterpolationTimeScaleMin = (int)clientTickRate.InterpolationTimeScaleMin;
            this.MaxExtrapolationTimeSimTicks = (int)clientTickRate.MaxExtrapolationTimeSimTicks;
            this.MaxPredictAheadTimeMS = (int)clientTickRate.MaxPredictAheadTimeMS;
            this.MaxPredictionStepBatchSizeFirstTimeTick = clientTickRate.MaxPredictionStepBatchSizeFirstTimeTick;
            this.MaxPredictionStepBatchSizeRepeatedTick = clientTickRate.MaxPredictionStepBatchSizeRepeatedTick;
            this.PredictionTimeScaleMax = (int)clientTickRate.PredictionTimeScaleMax;
            this.PredictionTimeScaleMin = (int)clientTickRate.PredictionTimeScaleMin;
            this.TargetCommandSlack = (int)clientTickRate.TargetCommandSlack;
        }
    }

    [Serializable]
    struct GhostScaleAnalyticsData : IAnalytic.IData
    {
        public PlaymodeSettings Settings;
        public int ServerSpawnedGhostCount;
        public int GhostTypeCount;
        public int AverageGhostInSnapshot;
        public GhostTypeData[] GhostTypes;
        public WrappedClientServerTickRate ClientServerTickRate;
        public WrappedClientTickRate ClientTickRate;
        public MainClientData MainClientData;
        public int SnapshotTargetSize;
        public int NumMainClientWorlds;
        public int NumServerWorlds;
        // Number of NetCodeConfig assets in the project. Used to track how many projects still rely on the
        // (deprecated, RemovedAfter 6.8) ability to add multiple configs. >1 implies deprecated multi-config usage.
        // TODO (6.8): Once multiple configs are removed this should always report 1.
        public int NetCodeConfigCount;

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append($"{nameof(Settings)}: {Settings}, " +
                           $"{nameof(ServerSpawnedGhostCount)}: {ServerSpawnedGhostCount}, " +
                           $"{nameof(GhostTypeCount)}: {GhostTypeCount}, " +
                           $"{nameof(AverageGhostInSnapshot)}: {AverageGhostInSnapshot}, " +
                           $"{nameof(ClientServerTickRate)}: {ClientServerTickRate}, " +
                           $"{nameof(ClientTickRate)}:{ClientTickRate}, " +
                           $"{nameof(MainClientData)}:{MainClientData}, " +
                           $"{nameof(SnapshotTargetSize)}:{SnapshotTargetSize}, " +
                           $"{nameof(NumMainClientWorlds)}:{NumMainClientWorlds}, " +
                           $"{nameof(NumServerWorlds)}:{NumServerWorlds}, " +
                           $"{nameof(NetCodeConfigCount)}:{NetCodeConfigCount}, " +
                           $" {nameof(GhostTypes)}:\n");

            foreach (var ghostTypeData in GhostTypes)
            {
                builder.AppendLine($"[{ghostTypeData}],");
            }

            return builder.ToString();
        }
    }

    [Serializable]
    struct MainClientData
    {
        public int RelevancyMode;
        public int NumOfPredictedGhosts;
        public int NumSwitchToPredicted;
        public int NumSwitchToInterpolated;
        public int NumOfSpawnedGhost;

        public override string ToString()
        {
            return $"{nameof(RelevancyMode)}: {RelevancyMode}, " +
                   $"{nameof(NumOfPredictedGhosts)}: {NumOfPredictedGhosts}, " +
                   $"{nameof(NumSwitchToPredicted)}: {NumSwitchToPredicted}, " +
                   $"{nameof(NumSwitchToInterpolated)}: {NumSwitchToInterpolated}, " +
                   $"{nameof(NumOfSpawnedGhost)}: {NumOfSpawnedGhost}";
        }
    }

    [Serializable]
    struct PlaymodeSettings
    {
        public int ThinClientCount;
        public bool SimulatorEnabled;
        public int Delay;
        public int DropPercentage;
        public int Jitter;
        public string PlayModeType;
        public string SimulatorPreset;

        public override string ToString()
        {
            return $"{nameof(ThinClientCount)}: {ThinClientCount}, " +
                   $"{nameof(SimulatorEnabled)}: {SimulatorEnabled}, " +
                   $"{nameof(Delay)}: {Delay}, " +
                   $"{nameof(DropPercentage)}: {DropPercentage}, " +
                   $"{nameof(Jitter)}: {Jitter}, " +
                   $"{nameof(PlayModeType)}: {PlayModeType}, " +
                   $"{nameof(SimulatorPreset)}: {SimulatorPreset}, ";
        }
    }

    static class GhostComponentAnalytics
    {
        public const int k_MaxEventsPerHour = 1000;
        public const int k_MaxItems = 1000;
        public const string k_VendorKey = "unity.netcode";
        public const string k_Scale = "NetcodeGhostComponentScale";
        public const int k_ScaleVersion = 3;
        public const string k_Configuration = "NetcodeGhostComponentConfiguration";
        public const int k_ConfigurationVersion = 2;

        /// <summary>
        /// This will add or update the buffer containing the configuration data from a <see cref="GhostAuthoringComponent"/>.
        /// </summary>
        /// <param name="ghostComponent">Retrieve data from this component.</param>
        /// <param name="numVariants">Count of the number of variants on this ghost.</param>
        public static void BufferConfigurationData(BaseGhostSettings ghostComponent, int numVariants)
        {
            var analyticsData = new GhostConfigurationAnalyticsData
            {
                id = ghostComponent.prefabId,
                autoCommandTarget = ghostComponent.SupportAutoCommandTarget,
                optimizationMode = ghostComponent.OptimizationMode.ToString(),
                ghostMode = ghostComponent.DefaultGhostMode.ToString(),
                importance = ghostComponent.Importance,
                maxSendRateHz = ghostComponent.MaxSendRate,
                variance = numVariants,
            };
            NetCodeAnalytics.StoreGhostComponent(analyticsData);
        }

        /// <summary>
        /// Generic basic class that allow to dispatch any <see cref="IAnalytic.IData"/> data. Used internally by
        /// GhostComponentAnalytics
        /// </summary>
        /// <typeparam name="T"></typeparam>
        class NetCodeAnalytic<T> : IAnalytic where T: IAnalytic.IData
        {
            private T m_Data;

            public NetCodeAnalytic(T data)
            {
                m_Data = data;
            }
            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = m_Data;
                error = null;
                return data != null;
            }
        }
        [AnalyticInfo(eventName:k_Scale, vendorKey:k_VendorKey, version: k_ScaleVersion, maxEventsPerHour: k_MaxEventsPerHour, maxNumberOfElements: k_MaxItems)]
        class GhostScaleAnalytics : NetCodeAnalytic<GhostScaleAnalyticsData>
        {
            public GhostScaleAnalytics(GhostScaleAnalyticsData data) : base(data) {}
        }

        [AnalyticInfo(eventName:k_Configuration, vendorKey:k_VendorKey, maxEventsPerHour: k_MaxEventsPerHour, maxNumberOfElements: k_MaxItems)]
        class GhostConfigurationAnalytics : NetCodeAnalytic<GhostConfigurationAnalyticsData>
        {
            public GhostConfigurationAnalytics(GhostConfigurationAnalyticsData data) : base(data) {}
        }

        public static void SendGhostComponentScale(GhostScaleAnalyticsData data)
        {
            EditorAnalytics.SendAnalytic(new GhostScaleAnalytics(data));
        }

        public static void SendGhostComponentConfiguration(GhostConfigurationAnalyticsData[] data)
        {
            foreach (var analyticsData in data)
            {
                EditorAnalytics.SendAnalytic(new GhostConfigurationAnalytics(analyticsData));
            }
        }
    }
}
