#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if UNITY_6000_0_OR_NEWER && NETCODE_DEBUG

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.NetCode.Editor;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    class NetcodeProfilerUtilityTests
    {
        [Test, Description("Verifies that we can call the internal Editor utility method to show ghost components in the inspector.")]
        public void Profiler_Utility_ExecuteEntitiesEditorInternals()
        {
            var type = typeof(GhostGenTestUtils.GhostGenTestType_IComponentData);
            NetcodeEditorUtility.ShowGhostComponentInspectorContent(type);
        }
    }

    class NetcodeProfilerTests
    {
        string m_ProfilerDataFilePath;
        const string k_GhostName = "ProfilerTestGhost";

        bool m_PrevProfilerEnabled;
        bool m_PrevProfilerDriverEnabled;
        bool m_PrevProfilerDriverProfileEditor;
        bool m_ProfilerWindowWasOpen;

        [SetUp]
        public void SetupProfilerTests()
        {
            m_ProfilerDataFilePath = null;

            m_PrevProfilerEnabled = Profiler.enabled;
            m_PrevProfilerDriverEnabled = ProfilerDriver.enabled;
            m_PrevProfilerDriverProfileEditor = ProfilerDriver.profileEditor;
            m_ProfilerWindowWasOpen = EditorWindow.HasOpenInstances<ProfilerWindow>();
        }

        [UnityTest, Description("Collects some stats while profiling, saves the session and loads it back to verify the stats are still correct")]
        public IEnumerator Profiler_SaveAndLoadStats()
        {
            m_ProfilerDataFilePath = Path.Combine(Application.temporaryCachePath, "Profiler_DumpAndLoadStats_Savefile.data");

            using (var testWorld = new NetCodeTestWorld())
            {
                const int frameCount = 100;
                yield return CreateTestWorldAndRunProfiler(testWorld, frameCount);

                // Save the profiler run and load it back again
                ProfilerDriver.SaveProfile(m_ProfilerDataFilePath);
                ProfilerDriver.ClearAllFrames();

                var loaded = ProfilerDriver.LoadProfile(m_ProfilerDataFilePath, false);
                Assert.IsTrue(loaded);
                Assert.AreNotEqual(-1, ProfilerDriver.lastFrameIndex);

                // Read the frame metadata
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    Assert.NotNull(frameDataView);
                    Assert.True(frameDataView.valid);

                    GetAndCheckStats(frameDataView, ProfilerMetricsConstants.ServerGuid);
                    GetAndCheckStats(frameDataView, ProfilerMetricsConstants.ClientGuid);
                }
            }
        }

        static IEnumerator CreateTestWorldAndRunProfiler(NetCodeTestWorld testWorld, int frameCount)
        {
            testWorld.Bootstrap(true);
            testWorld.CreateGhostCollection();
            testWorld.CreateWorlds(true, 1);
            // We don't create a GhostMetricsSingleton here because it would be deleted by the ProfilerMetricsCollector
            // who is creating its own instance.
            var serverPrefab = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld, k_GhostName);
            DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[0], k_GhostName);

            testWorld.Connect();
            testWorld.GoInGame();
            for (var i = 0; i < 32; i++)
            {
                testWorld.Tick();
            }

            var serverEntity = testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct { field000 = 123 }); // need to set non default value to get per component stats
            testWorld.Tick();
            testWorld.Tick(); // entity is sent, then client world receives it. Both jobs happen one after the other in the same Tick() call. server write stat should contain new entry now, client write stats should also be written to
            testWorld.Tick(); // both client and server write stats are copied to the respective read stats buffer

            // Client also now has the ghost spawned
            testWorld.Tick();

            // update server side component to trigger component stats
            testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new GhostGenTestTypes.GhostGenBigStruct { field000 = 124 });
            testWorld.Tick(); // data is sent
            testWorld.Tick(); // server read buffer is updated

            // Enable Profiler
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.profileEditor = true;
            ProfilerDriver.enabled = true;
            Profiler.enabled = true;

            // Run the stats collection for several frames
            for (var i = 0; i < frameCount; i++)
            {
                testWorld.Tick();
                yield return null;
            }
        }

        static void GetAndCheckStats(RawFrameDataView frameDataView, Guid guid)
        {
            // Get the serialized ghost stats
            var serializedGhostStatsSnapshot = frameDataView.GetFrameMetaData<byte>(guid, ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag);
            Assert.IsNotEmpty(serializedGhostStatsSnapshot);

            // Deserialize the ghost stats
            var ghostStatsSnapshot = UnsafeGhostStatsSnapshot.FromBlittableData(Allocator.Temp, serializedGhostStatsSnapshot);
            Assert.NotNull(ghostStatsSnapshot);
            var perGhostTypeStats = ghostStatsSnapshot.PerGhostTypeStatsListRO;
            Assert.IsTrue(perGhostTypeStats.IsCreated);
            Assert.IsFalse(perGhostTypeStats.IsEmpty);
            var firstTypeComponentStats = perGhostTypeStats[0].PerComponentStatsList;
            Assert.IsTrue(firstTypeComponentStats.IsCreated);
            Assert.IsFalse(firstTypeComponentStats.IsEmpty);

            // Check all other metrics
            var frameMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(frameDataView, guid);
            Assert.IsTrue(frameMetaData.CommandStats.IsCreated);
            Assert.IsTrue(frameMetaData.CommandStats.Length == 3);
            Assert.IsTrue(frameMetaData.ComponentIndices.IsCreated);
            Assert.IsFalse(frameMetaData.ComponentIndices.Length == 0);
            Assert.NotNull(frameMetaData.NetworkMetrics);
            Assert.IsTrue(frameMetaData.PredictionErrorMetrics.IsCreated);
            Assert.IsTrue(frameMetaData.PrefabSerializers.IsCreated);
            Assert.NotNull(frameMetaData.ProfilerMetrics);
            Assert.IsTrue(frameMetaData.SerializerStates.IsCreated);
            Assert.IsTrue(frameMetaData.UncompressedSizesPerType.IsCreated);
            Assert.IsTrue(frameMetaData.GhostNames.IsCreated);
            Assert.IsFalse(frameMetaData.GhostNames.Length == 0);
            Assert.AreEqual(frameMetaData.GhostNames[0].Name.ToString(), k_GhostName);
            Assert.IsTrue(frameMetaData.PredictionErrors.IsCreated);
            Assert.IsTrue(frameMetaData.ServerTick.IsValid);
            Assert.IsTrue(frameMetaData.InterpolationTick.IsValid);
        }

        [UnityTest, Description("Verifies that the mapping is correctly persisted across serialization.")]
        public IEnumerator Profiler_FrameToTickMapping_IsPersistedAcrossSerialization()
        {
            var singleton = SnapshotTickMappingSingleton.instance;
            singleton.Initialize();

            const int frameCount = 100;
            using (var testWorld = new NetCodeTestWorld())
            {
                yield return CreateTestWorldAndRunProfiler(testWorld, frameCount);
            }

            var clientTickFrames = new List<int>();
            var serverTickFrames = new List<int>();
            for (var i = 0; i < frameCount; i++)
            {
                clientTickFrames.Add(singleton.GetClientTickFrameIndexFromServerTickFrameIndex(i));
                serverTickFrames.Add(singleton.GetServerTickFrameIndexFromClientTickFrameIndex(i));
            }

            CollectionAssert.AreEqual(clientTickFrames, serverTickFrames);

            // Serialize
            ((ISerializationCallbackReceiver)singleton).OnBeforeSerialize();

            // Clear and deserialize
            singleton.frameToSnapshotTickMapping.clientTickToFrame.Clear();
            singleton.frameToSnapshotTickMapping.serverTickToFrame.Clear();
            singleton.frameToSnapshotTickMapping.frameToTickInfo.Clear();
            ((ISerializationCallbackReceiver)singleton).OnAfterDeserialize();

            // Assert mapping is restored
            Assert.IsTrue(singleton.frameToSnapshotTickMapping.clientTickToFrame.Count != 0);
            Assert.IsTrue(singleton.frameToSnapshotTickMapping.serverTickToFrame.Count != 0);
            for (var i = 0; i < frameCount; i++)
            {
                Assert.AreEqual(clientTickFrames[i], singleton.GetClientTickFrameIndexFromServerTickFrameIndex(i));
                Assert.AreEqual(serverTickFrames[i], singleton.GetServerTickFrameIndexFromClientTickFrameIndex(i));
            }
        }

        [UnityTest, Description("Verifies that the correct frames are selected when selecting adjacent or corresponding tick frames.")]
        public IEnumerator Profiler_FrameToTickMapping_AdjacentFramesAreSelected()
        {
            var singleton = SnapshotTickMappingSingleton.instance;
            singleton.Initialize();

            const int frameCount = 100;
            using (var testWorld = new NetCodeTestWorld())
            {
                yield return CreateTestWorldAndRunProfiler(testWorld, frameCount);
            }

            var profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();

            // Go to first frame and try going to a previous tick (should not change frame)
            profilerWindow.selectedFrameIndex = ProfilerDriver.firstFrameIndex;
            ProfilerUtils.SelectAdjacentTick(-1, NetworkRole.Server);
            Assert.AreEqual(ProfilerDriver.firstFrameIndex, profilerWindow.selectedFrameIndex);
            ProfilerUtils.SelectAdjacentTick(-1, NetworkRole.Client);
            Assert.AreEqual(ProfilerDriver.firstFrameIndex, profilerWindow.selectedFrameIndex);

            // Go to last frame and try going to next tick (should not change frame)
            profilerWindow.selectedFrameIndex = ProfilerDriver.lastFrameIndex;
            ProfilerUtils.SelectAdjacentTick(1, NetworkRole.Server);
            Assert.AreEqual(ProfilerDriver.lastFrameIndex, profilerWindow.selectedFrameIndex);
            ProfilerUtils.SelectAdjacentTick(1, NetworkRole.Client);
            Assert.AreEqual(ProfilerDriver.lastFrameIndex, profilerWindow.selectedFrameIndex);

            // Step from first to last frame and back
            profilerWindow.selectedFrameIndex = ProfilerDriver.firstFrameIndex;
            TestSelectingMultipleAdjacentTicks(frameCount, 1, NetworkRole.Server);
            Assert.AreEqual(ProfilerDriver.lastFrameIndex, profilerWindow.selectedFrameIndex);
            TestSelectingMultipleAdjacentTicks(frameCount, -1, NetworkRole.Server);
            // firstFrameIndex is not a tick frame in the test case, so we should end up on the next one
            Assert.AreEqual(ProfilerDriver.firstFrameIndex+1, profilerWindow.selectedFrameIndex);
            TestSelectingMultipleAdjacentTicks(frameCount, 1, NetworkRole.Client);
            Assert.AreEqual(ProfilerDriver.lastFrameIndex, profilerWindow.selectedFrameIndex);
            TestSelectingMultipleAdjacentTicks(frameCount, -1, NetworkRole.Client);
            Assert.AreEqual(ProfilerDriver.firstFrameIndex+1, profilerWindow.selectedFrameIndex);
        }

        static void TestSelectingMultipleAdjacentTicks(int frameCount, int direction, NetworkRole networkRole)
        {
            for (var i = 0; i < frameCount; i++)
            {
                ProfilerUtils.SelectAdjacentTick(direction, networkRole);
            }
        }

        [TearDown]
        public void CleanupProfilerTests()
        {
            // Restore profiler state
            Profiler.enabled = m_PrevProfilerEnabled;
            ProfilerDriver.enabled = m_PrevProfilerDriverEnabled;
            ProfilerDriver.profileEditor = m_PrevProfilerDriverProfileEditor;
            ProfilerDriver.ClearAllFrames();

            if (!m_ProfilerWindowWasOpen)
                EditorWindow.GetWindow<ProfilerWindow>().Close();

            // Clean save file
            if (!string.IsNullOrEmpty(m_ProfilerDataFilePath) && File.Exists(m_ProfilerDataFilePath))
            {
                File.Delete(m_ProfilerDataFilePath);
            }
        }
    }
}
#endif
