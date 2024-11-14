using NUnit.Framework;
using Unity.NetCode.Analytics;
using Unity.NetCode.Editor;

namespace Unity.NetCode.Tests
{
    namespace AnalyticsTests
    {
        class SessionState
        {
            [TearDown]
            public void SetUp()
            {
                NetCodeAnalytics.ClearGhostComponents();
            }

            [Test]
            public void StoredValueCanBeRetrieved()
            {
                var x = new GhostConfigurationAnalyticsData { id = "42", };

                NetCodeAnalytics.StoreGhostComponent(x);

                Assert.That(x.id, Is.EqualTo(NetCodeAnalytics.RetrieveGhostComponents()[0].id));
            }

            [Test]
            public void ChangedValueCanBeRetrieved()
            {
                var x = new GhostConfigurationAnalyticsData();
                NetCodeAnalytics.StoreGhostComponent(x);
                x.importance = 123;
                NetCodeAnalytics.StoreGhostComponent(x);
                var res = NetCodeAnalytics.RetrieveGhostComponents();
                Assert.That(x.importance, Is.EqualTo(res[0].importance));
            }

            [Test]
            public void ChangedSecondValue()
            {
                var x = new GhostConfigurationAnalyticsData { id = "43" };
                NetCodeAnalytics.StoreGhostComponent(x);
                var y = new GhostConfigurationAnalyticsData { id = "42" };
                NetCodeAnalytics.StoreGhostComponent(y);
                y.importance = 100;
                NetCodeAnalytics.StoreGhostComponent(y);
                var res = NetCodeAnalytics.RetrieveGhostComponents();
                Assert.That(y.importance, Is.EqualTo(res[1].importance));
            }

            [Test]
            public void ChangedBothValues()
            {
                var x = new GhostConfigurationAnalyticsData { id = "43" };
                NetCodeAnalytics.StoreGhostComponent(x);
                var y = new GhostConfigurationAnalyticsData { id = "42" };
                NetCodeAnalytics.StoreGhostComponent(y);
                y.importance = 100;
                NetCodeAnalytics.StoreGhostComponent(y);
                x.importance = 42;
                NetCodeAnalytics.StoreGhostComponent(x);
                var res = NetCodeAnalytics.RetrieveGhostComponents();
                Assert.That(x.importance, Is.EqualTo(res[0].importance));
                Assert.That(y.importance, Is.EqualTo(res[1].importance));
            }

            [Test]
            public void MultipleValueCanBeRetrieved()
            {
                var x = new GhostConfigurationAnalyticsData { id = "42", };
                NetCodeAnalytics.StoreGhostComponent(x);
                var y = new GhostConfigurationAnalyticsData { id = "43", };
                NetCodeAnalytics.StoreGhostComponent(y);
                var res = NetCodeAnalytics.RetrieveGhostComponents();
                Assert.That(res.Length, Is.EqualTo(2));
                Assert.That(x.id, Is.EqualTo(res[0].id));
                Assert.That(y.id, Is.EqualTo(res[1].id));
            }
        }

        class FieldVerification
        {
            /// <summary>
            /// This test will fail because you have changed the layout of the analytics data in the schema.
            /// https://schemata.prd.cds.internal.unity3d.com/onboarding
            /// </summary>
            [Test]
            public void VerifyGhostConfigurationAnalyticsData()
            {
                var ghostConfigurationAnalyticsDataFields = typeof(GhostConfigurationAnalyticsData).GetFields();
                Assert.That(ghostConfigurationAnalyticsDataFields.Length, Is.EqualTo(8));
                Assert.That(ghostConfigurationAnalyticsDataFields[0].Name, Is.EqualTo("id"));
                Assert.That(ghostConfigurationAnalyticsDataFields[0].FieldType, Is.EqualTo(typeof(string)));
                Assert.That(ghostConfigurationAnalyticsDataFields[1].Name, Is.EqualTo("ghostMode"));
                Assert.That(ghostConfigurationAnalyticsDataFields[1].FieldType, Is.EqualTo(typeof(string)));
                Assert.That(ghostConfigurationAnalyticsDataFields[2].Name, Is.EqualTo("optimizationMode"));
                Assert.That(ghostConfigurationAnalyticsDataFields[2].FieldType, Is.EqualTo(typeof(string)));
                Assert.That(ghostConfigurationAnalyticsDataFields[3].Name, Is.EqualTo("prespawnedCount"));
                Assert.That(ghostConfigurationAnalyticsDataFields[3].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[4].Name, Is.EqualTo("autoCommandTarget"));
                Assert.That(ghostConfigurationAnalyticsDataFields[4].FieldType, Is.EqualTo(typeof(bool)));
                Assert.That(ghostConfigurationAnalyticsDataFields[5].Name, Is.EqualTo("variance"));
                Assert.That(ghostConfigurationAnalyticsDataFields[5].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[6].Name, Is.EqualTo("importance"));
                Assert.That(ghostConfigurationAnalyticsDataFields[6].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[7].Name, Is.EqualTo("maxSendRateHz"));
                Assert.That(ghostConfigurationAnalyticsDataFields[7].FieldType, Is.EqualTo(typeof(int)));
            }

            /// <summary>
            /// This test will fail because you have changed the layout of the analytics data in the schema.
            /// https://schemata.prd.cds.internal.unity3d.com/onboarding
            /// </summary>
            [Test]
            public void VerifyGhostScaleAnalyticsData()
            {
                var ghostConfigurationAnalyticsDataFields = typeof(GhostScaleAnalyticsData).GetFields();
                Assert.That(ghostConfigurationAnalyticsDataFields.Length, Is.EqualTo(11));
                Assert.That(ghostConfigurationAnalyticsDataFields[0].Name, Is.EqualTo("Settings"));
                Assert.IsTrue(ghostConfigurationAnalyticsDataFields[0].FieldType.IsValueType);
                Assert.That(ghostConfigurationAnalyticsDataFields[1].Name, Is.EqualTo("ServerSpawnedGhostCount"));
                Assert.That(ghostConfigurationAnalyticsDataFields[1].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[2].Name, Is.EqualTo("GhostTypeCount"));
                Assert.That(ghostConfigurationAnalyticsDataFields[2].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[3].Name, Is.EqualTo("AverageGhostInSnapshot"));
                Assert.That(ghostConfigurationAnalyticsDataFields[3].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[4].Name, Is.EqualTo("GhostTypes"));
                Assert.IsTrue(ghostConfigurationAnalyticsDataFields[4].FieldType.IsArray);
                Assert.That(ghostConfigurationAnalyticsDataFields[5].Name, Is.EqualTo("ClientServerTickRate"));
                Assert.IsTrue(ghostConfigurationAnalyticsDataFields[5].FieldType.IsValueType);
                Assert.That(ghostConfigurationAnalyticsDataFields[6].Name, Is.EqualTo("ClientTickRate"));
                Assert.IsTrue(ghostConfigurationAnalyticsDataFields[6].FieldType.IsValueType);
                Assert.That(ghostConfigurationAnalyticsDataFields[7].Name, Is.EqualTo("MainClientData"));
                Assert.IsTrue(ghostConfigurationAnalyticsDataFields[7].FieldType.IsValueType);
                Assert.That(ghostConfigurationAnalyticsDataFields[8].Name, Is.EqualTo("SnapshotTargetSize"));
                Assert.That(ghostConfigurationAnalyticsDataFields[8].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[9].Name, Is.EqualTo("NumMainClientWorlds"));
                Assert.That(ghostConfigurationAnalyticsDataFields[9].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostConfigurationAnalyticsDataFields[10].Name, Is.EqualTo("NumServerWorlds"));
                Assert.That(ghostConfigurationAnalyticsDataFields[10].FieldType, Is.EqualTo(typeof(int)));

                var playmodeSettingsFields = ghostConfigurationAnalyticsDataFields[0].FieldType.GetFields();
                Assert.That(playmodeSettingsFields.Length, Is.EqualTo(7));
                Assert.That(playmodeSettingsFields[0].Name, Is.EqualTo("ThinClientCount"));
                Assert.That(playmodeSettingsFields[0].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(playmodeSettingsFields[1].Name, Is.EqualTo("SimulatorEnabled"));
                Assert.That(playmodeSettingsFields[1].FieldType, Is.EqualTo(typeof(bool)));
                Assert.That(playmodeSettingsFields[2].Name, Is.EqualTo("Delay"));
                Assert.That(playmodeSettingsFields[2].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(playmodeSettingsFields[3].Name, Is.EqualTo("DropPercentage"));
                Assert.That(playmodeSettingsFields[3].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(playmodeSettingsFields[4].Name, Is.EqualTo("Jitter"));
                Assert.That(playmodeSettingsFields[4].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(playmodeSettingsFields[5].Name, Is.EqualTo("PlayModeType"));
                Assert.That(playmodeSettingsFields[5].FieldType, Is.EqualTo(typeof(string)));
                Assert.That(playmodeSettingsFields[6].Name, Is.EqualTo("SimulatorPreset"));
                Assert.That(playmodeSettingsFields[6].FieldType, Is.EqualTo(typeof(string)));

                var ghostTypeDataFields = ghostConfigurationAnalyticsDataFields[4].FieldType.GetElementType().GetFields();
                Assert.That(ghostTypeDataFields.Length, Is.EqualTo(4));
                Assert.That(ghostTypeDataFields[0].Name, Is.EqualTo("GhostId"));
                Assert.That(ghostTypeDataFields[0].FieldType, Is.EqualTo(typeof(string)));
                Assert.That(ghostTypeDataFields[1].Name, Is.EqualTo("ChildrenCount"));
                Assert.That(ghostTypeDataFields[1].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostTypeDataFields[2].Name, Is.EqualTo("ComponentCount"));
                Assert.That(ghostTypeDataFields[2].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(ghostTypeDataFields[3].Name, Is.EqualTo("ComponentsWithSerializedDataCount"));
                Assert.That(ghostTypeDataFields[3].FieldType, Is.EqualTo(typeof(int)));

                var clientServerTickRateFields = ghostConfigurationAnalyticsDataFields[5].FieldType.GetFields();
                Assert.That(clientServerTickRateFields.Length, Is.EqualTo(5));
                Assert.That(clientServerTickRateFields[0].Name, Is.EqualTo("MaxSimulationStepBatchSize"));
                Assert.That(clientServerTickRateFields[0].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientServerTickRateFields[1].Name, Is.EqualTo("MaxSimulationStepsPerFrame"));
                Assert.That(clientServerTickRateFields[1].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientServerTickRateFields[2].Name, Is.EqualTo("NetworkTickRate"));
                Assert.That(clientServerTickRateFields[2].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientServerTickRateFields[3].Name, Is.EqualTo("SimulationTickRate"));
                Assert.That(clientServerTickRateFields[3].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientServerTickRateFields[4].Name, Is.EqualTo("TargetFrameRateMode"));
                Assert.That(clientServerTickRateFields[4].FieldType, Is.EqualTo(typeof(int)));

                var clientTickRateFields = ghostConfigurationAnalyticsDataFields[6].FieldType.GetFields();
                Assert.That(clientTickRateFields.Length, Is.EqualTo(15));
                Assert.That(clientTickRateFields[0].Name, Is.EqualTo("CommandAgeCorrectionFraction"));
                Assert.That(clientTickRateFields[0].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[1].Name, Is.EqualTo("InterpolationDelayCorrectionFraction"));
                Assert.That(clientTickRateFields[1].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[2].Name, Is.EqualTo("InterpolationDelayJitterScale"));
                Assert.That(clientTickRateFields[2].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[3].Name, Is.EqualTo("InterpolationDelayMaxDeltaTicksFraction"));
                Assert.That(clientTickRateFields[3].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[4].Name, Is.EqualTo("InterpolationTimeMS"));
                Assert.That(clientTickRateFields[4].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[5].Name, Is.EqualTo("InterpolationTimeNetTicks"));
                Assert.That(clientTickRateFields[5].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[6].Name, Is.EqualTo("InterpolationTimeScaleMax"));
                Assert.That(clientTickRateFields[6].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[7].Name, Is.EqualTo("InterpolationTimeScaleMin"));
                Assert.That(clientTickRateFields[7].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[8].Name, Is.EqualTo("MaxExtrapolationTimeSimTicks"));
                Assert.That(clientTickRateFields[8].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[9].Name, Is.EqualTo("MaxPredictAheadTimeMS"));
                Assert.That(clientTickRateFields[9].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[10].Name, Is.EqualTo("MaxPredictionStepBatchSizeFirstTimeTick"));
                Assert.That(clientTickRateFields[10].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[11].Name, Is.EqualTo("MaxPredictionStepBatchSizeRepeatedTick"));
                Assert.That(clientTickRateFields[11].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[12].Name, Is.EqualTo("PredictionTimeScaleMax"));
                Assert.That(clientTickRateFields[12].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[13].Name, Is.EqualTo("PredictionTimeScaleMin"));
                Assert.That(clientTickRateFields[13].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(clientTickRateFields[14].Name, Is.EqualTo("TargetCommandSlack"));
                Assert.That(clientTickRateFields[14].FieldType, Is.EqualTo(typeof(int)));

                var mainClientDataFields = ghostConfigurationAnalyticsDataFields[7].FieldType.GetFields();
                Assert.That(mainClientDataFields.Length, Is.EqualTo(5));
                Assert.That(mainClientDataFields[0].Name, Is.EqualTo("RelevancyMode"));
                Assert.That(mainClientDataFields[0].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(mainClientDataFields[1].Name, Is.EqualTo("NumOfPredictedGhosts"));
                Assert.That(mainClientDataFields[1].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(mainClientDataFields[2].Name, Is.EqualTo("NumSwitchToPredicted"));
                Assert.That(mainClientDataFields[2].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(mainClientDataFields[3].Name, Is.EqualTo("NumSwitchToInterpolated"));
                Assert.That(mainClientDataFields[3].FieldType, Is.EqualTo(typeof(int)));
                Assert.That(mainClientDataFields[4].Name, Is.EqualTo("NumOfSpawnedGhost"));
                Assert.That(mainClientDataFields[4].FieldType, Is.EqualTo(typeof(int)));
            }
        }
    }
}
