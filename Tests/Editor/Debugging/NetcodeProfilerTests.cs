#if UNITY_EDITOR && !NETCODE_NDEBUG

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

        // Profiler.enabled and ProfilerDriver.enabled map to the same native field
        // (ProfilerSession::m_RecordEnabled), so we only save/restore one.
        bool m_PrevProfilerDriverEnabled;
        bool m_PrevProfilerDriverProfileEditor;
        bool m_PrevProfilerDeepProfiling;
        bool[] m_PrevAreaEnabled;
        bool m_ProfilerWindowWasOpen;

        [SetUp]
        public void SetupProfilerTests()
        {
            m_ProfilerDataFilePath = null;

            m_PrevProfilerDriverEnabled = ProfilerDriver.enabled;
            m_PrevProfilerDriverProfileEditor = ProfilerDriver.profileEditor;
            m_PrevProfilerDeepProfiling = ProfilerDriver.deepProfiling;
            m_ProfilerWindowWasOpen = EditorWindow.HasOpenInstances<ProfilerWindow>();

            // Disable up front so frames captured between tests don't leak in.
            ProfilerDriver.enabled = false;
            m_PrevAreaEnabled = ProfilerFrameRecorder.DisableNonEssentialAreas();
        }

        [UnityTest, Description("Collects some stats while profiling, saves the session and loads it back to verify the stats are still correct")]
        public IEnumerator Profiler_SaveAndLoadStats()
        {
            m_ProfilerDataFilePath = Path.Combine(Application.temporaryCachePath, "Profiler_DumpAndLoadStats_Savefile.data");

            using (var testWorld = new NetCodeTestWorld())
            {
                yield return CreateTestWorldAndRunProfiler(testWorld, frameCount: 20);

                // Snapshot the live buffer first — its last frame is the one our metadata
                // assertions below look at. Disable+yield after, so ClearAllFrames doesn't
                // race the AddFrame lock in ProfilerHistory.
                ProfilerDriver.SaveProfile(m_ProfilerDataFilePath);
                ProfilerDriver.enabled = false;
                yield return null;
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
            testWorld.TickMultiple(32);

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

            yield return ProfilerFrameRecorder.RecordFrames(testWorld, frameCount);
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

        [UnityTearDown]
        public IEnumerator CleanupProfilerTests()
        {
            // Disable then yield once so in-flight frame integrations land before we clear.
            // ProfilerHistory::AddFrame and CleanupFrameHistory share a lock, so without
            // the yield ClearAllFrames can race with a frame that's still being integrated.
            ProfilerDriver.enabled = false;
            yield return null;
            ProfilerDriver.ClearAllFrames();

            ProfilerFrameRecorder.RestoreAreas(m_PrevAreaEnabled);
            ProfilerDriver.enabled = m_PrevProfilerDriverEnabled;
            ProfilerDriver.profileEditor = m_PrevProfilerDriverProfileEditor;
            ProfilerDriver.deepProfiling = m_PrevProfilerDeepProfiling;

            if (!m_ProfilerWindowWasOpen && EditorWindow.HasOpenInstances<ProfilerWindow>())
                EditorWindow.GetWindow<ProfilerWindow>().Close();

            if (!string.IsNullOrEmpty(m_ProfilerDataFilePath) && File.Exists(m_ProfilerDataFilePath))
                File.Delete(m_ProfilerDataFilePath);
        }
    }

    class NetcodeProfilerSingleWorldHostTests
    {
        string m_ProfilerDataFilePath;
        const string k_GhostName = "HostModeTestGhost";

        bool m_PrevProfilerDriverEnabled;
        bool m_PrevProfilerDriverProfileEditor;
        bool m_PrevProfilerDeepProfiling;
        bool[] m_PrevAreaEnabled;
        bool m_ProfilerWindowWasOpen;

        [SetUp]
        public void SetupProfilerTests()
        {
            m_ProfilerDataFilePath = null;

            m_PrevProfilerDriverEnabled = ProfilerDriver.enabled;
            m_PrevProfilerDriverProfileEditor = ProfilerDriver.profileEditor;
            m_PrevProfilerDeepProfiling = ProfilerDriver.deepProfiling;
            m_ProfilerWindowWasOpen = EditorWindow.HasOpenInstances<ProfilerWindow>();

            ProfilerDriver.enabled = false;
            m_PrevAreaEnabled = ProfilerFrameRecorder.DisableNonEssentialAreas();
        }

        [UnityTearDown]
        public IEnumerator CleanupProfilerTests()
        {
            ProfilerDriver.enabled = false;
            yield return null;
            ProfilerDriver.ClearAllFrames();

            ProfilerFrameRecorder.RestoreAreas(m_PrevAreaEnabled);
            ProfilerDriver.enabled = m_PrevProfilerDriverEnabled;
            ProfilerDriver.profileEditor = m_PrevProfilerDriverProfileEditor;
            ProfilerDriver.deepProfiling = m_PrevProfilerDeepProfiling;

            if (!m_ProfilerWindowWasOpen && EditorWindow.HasOpenInstances<ProfilerWindow>())
                EditorWindow.GetWindow<ProfilerWindow>().Close();

            if (!string.IsNullOrEmpty(m_ProfilerDataFilePath) && File.Exists(m_ProfilerDataFilePath))
                File.Delete(m_ProfilerDataFilePath);
        }

        [UnityTest, Description("Verifies that Single World Host metadata (isHostMode, hasConnectedClients) is emitted correctly")]
        public IEnumerator Profiler_SingleWorldHost_MetadataIsEmittedCorrectly()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                // Create Single World Host: server=false, numClients=0, numHostWorlds=1
                testWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1);

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                // Read frame metadata from server GUID (host mode emits under server GUID)
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    Assert.True(frameDataView.valid);

                    var frameMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ServerGuid);

                    // Verify host mode flag
                    Assert.AreEqual(1, frameMetaData.ProfilerMetrics.IsHostMode, "IsHostMode should be 1");

                    // Verify no clients connected initially
                    Assert.AreEqual(0, frameMetaData.ProfilerMetrics.HasConnectedClients, "HasConnectedClients should be 0");
                }
            }
        }

        [Test, Description("Verifies that world name fallback logic works correctly for host worlds")]
        public void Profiler_WorldNameFallback_ReturnsCorrectNameForHost()
        {
            // Test fallback for host world
            var hostWorldName = ProfilerUtils.GetWorldName(NetworkRole.Server, isHost: true);
            Assert.AreEqual("Host World", hostWorldName);

            // Test fallback for server world
            var serverWorldName = ProfilerUtils.GetWorldName(NetworkRole.Server, isHost: false);
            Assert.IsTrue(serverWorldName.Contains("Server"));
            Assert.IsFalse(serverWorldName.Contains("Host"));

            // Test fallback for client world
            var clientWorldName = ProfilerUtils.GetWorldName(NetworkRole.Client, isHost: false);
            Assert.IsTrue(clientWorldName.Contains("Client"));
        }

        [UnityTest, Description("Verifies that hasConnectedClients flag transitions when clients connect")]
        public IEnumerator Profiler_SingleWorldHost_ConnectedClientsFlagUpdates()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                // Create host world + 1 thin client
                testWorld.CreateWorlds(server: false, numClients: 1, numHostWorlds: 1);

                // Connect the client
                testWorld.Connect();
                testWorld.GoInGame();

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                // Read frame metadata - should show client connected
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    Assert.True(frameDataView.valid);

                    var frameMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ServerGuid);

                    Assert.AreEqual(1, frameMetaData.ProfilerMetrics.IsHostMode);
                    Assert.AreEqual(1, frameMetaData.ProfilerMetrics.HasConnectedClients,
                        "HasConnectedClients should be 1 after client connects");
                }
            }
        }

        [UnityTest, Description("Verifies that ghost stats collection works correctly in Single World Host mode")]
        public IEnumerator Profiler_SingleWorldHost_GhostStatsAreCollected()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(server: false, numClients: 1, numHostWorlds: 1);

                var hostPrefab = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld, k_GhostName);
                DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[0], k_GhostName);

                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickMultiple(32);

                // Spawn ghost in host world
                var hostEntity = testWorld.ServerWorld.EntityManager.Instantiate(hostPrefab);
                testWorld.ServerWorld.EntityManager.SetComponentData(
                    hostEntity, new GhostGenTestTypes.GhostGenBigStruct { field000 = 123 });

                testWorld.TickMultiple(3);

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                // Verify ghost stats are collected
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    Assert.True(frameDataView.valid);

                    var serializedGhostStats = frameDataView.GetFrameMetaData<byte>(
                        ProfilerMetricsConstants.ServerGuid,
                        ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag);
                    Assert.IsNotEmpty(serializedGhostStats, "Ghost stats should be collected");

                    var ghostStatsSnapshot = UnsafeGhostStatsSnapshot.FromBlittableData(
                        Allocator.Temp, serializedGhostStats);
                    Assert.NotNull(ghostStatsSnapshot);
                    Assert.IsFalse(ghostStatsSnapshot.PerGhostTypeStatsListRO.IsEmpty);

                    // Verify ghost name
                    var ghostNames = frameDataView.GetFrameMetaData<GhostNames>(
                        ProfilerMetricsConstants.ServerGuid,
                        ProfilerMetricsConstants.GhostNamesTag);
                    Assert.IsFalse(ghostNames.Length == 0);
                    Assert.AreEqual(k_GhostName, ghostNames[0].Name.ToString());
                }
            }
        }


        [UnityTest, Description("Verifies that Single World Host metadata persists across save/load cycle")]
        public IEnumerator Profiler_SingleWorldHost_MetadataPersistedAcrossSerialization()
        {
            m_ProfilerDataFilePath = Path.Combine(Application.temporaryCachePath,
                "Profiler_SingleWorldHost_Serialization.data");

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(server: false, numClients: 1, numHostWorlds: 1);

                testWorld.Connect();
                testWorld.GoInGame();

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                // Capture metadata before save
                byte expectedIsHostMode;
                byte expectedHasConnectedClients;

                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    var frameMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ServerGuid);
                    expectedIsHostMode = frameMetaData.ProfilerMetrics.IsHostMode;
                    expectedHasConnectedClients = frameMetaData.ProfilerMetrics.HasConnectedClients;
                }

                // Save profiler data
                ProfilerDriver.SaveProfile(m_ProfilerDataFilePath);
                ProfilerDriver.ClearAllFrames();

                // Load profiler data
                var loaded = ProfilerDriver.LoadProfile(m_ProfilerDataFilePath, false);
                Assert.IsTrue(loaded);

                // Verify metadata after load
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    Assert.True(frameDataView.valid);

                    var frameMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ServerGuid);

                    Assert.AreEqual(expectedIsHostMode, frameMetaData.ProfilerMetrics.IsHostMode,
                        "IsHostMode should persist across serialization");
                    Assert.AreEqual(expectedHasConnectedClients, frameMetaData.ProfilerMetrics.HasConnectedClients,
                        "HasConnectedClients should persist across serialization");
                }
            }
        }

        [UnityTest, Description("Verifies that in Single World Host mode, profiler data is emitted under Server GUID only")]
        public IEnumerator Profiler_SingleWorldHost_DataEmittedUnderServerGuid()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                // Create host world with a remote thin client to generate network traffic
                testWorld.CreateWorlds(server: false, numClients: 1, numHostWorlds: 1);

                // Create ghost entities
                var hostPrefab = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld, k_GhostName);
                DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[0], k_GhostName);

                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickMultiple(32);

                // Spawn ghost in host world
                var hostEntity = testWorld.ServerWorld.EntityManager.Instantiate(hostPrefab);
                testWorld.ServerWorld.EntityManager.SetComponentData(
                    hostEntity, new GhostGenTestTypes.GhostGenBigStruct { field000 = 123 });

                testWorld.TickMultiple(3);

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                // Verify host world data is emitted under SERVER GUID
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    Assert.True(frameDataView.valid);

                    // Host world should emit to Server GUID
                    var serverGhostStats = frameDataView.GetFrameMetaData<byte>(
                        ProfilerMetricsConstants.ServerGuid,
                        ProfilerMetricsConstants.SerializedGhostStatsSnapshotTag);
                    Assert.IsNotEmpty(serverGhostStats,
                        "Server GUID should have ghost stats from host world");

                    // Verify the host mode flag is set for server GUID
                    var serverMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ServerGuid);
                    Assert.AreEqual(1, serverMetaData.ProfilerMetrics.IsHostMode,
                        "Server GUID should have isHostMode=1");

                    // Thin client (if it has data) would be under Client GUID
                    // But we're specifically testing that the HOST world data goes to Server GUID
                }
            }
        }

        [UnityTest, Description("Verifies that isHostMode is correctly set to 0 in Binary World mode (baseline test)")]
        [DisableSingleWorldHostTest(isPermanentReason: "The host-mode equivalent is covered by Profiler_SingleWorldHost_DataEmittedUnderServerGuid.")]
        public IEnumerator Profiler_BinaryWorldMode_MetadataIsCorrect()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                // Create Binary World mode: separate server and client
                testWorld.CreateWorlds(server: true, numClients: 1);

                testWorld.Connect();
                testWorld.GoInGame();

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                // Check SERVER world metadata
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    var serverMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ServerGuid);

                    Assert.AreEqual(0, serverMetaData.ProfilerMetrics.IsHostMode,
                        "IsHostMode should be 0 in Binary World mode (server)");
                    Assert.AreEqual(1, serverMetaData.ProfilerMetrics.HasConnectedClients,
                        "HasConnectedClients should be 1 in Binary World mode");
                }

                // Check CLIENT world metadata
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(ProfilerDriver.lastFrameIndex, 0))
                {
                    var clientMetaData = NetcodeForEntitiesProfilerModuleViewController.GetProfilerFrameMetaData(
                        frameDataView, ProfilerMetricsConstants.ClientGuid);

                    Assert.AreEqual(0, clientMetaData.ProfilerMetrics.IsHostMode,
                        "IsHostMode should be 0 in Binary World mode (client)");
                }
            }
        }
    }

    /// <summary>
    /// Tests for the tick mapping logic using real profiler captures.
    /// </summary>
    class SnapshotTickMappingTests
    {
        const string k_GhostName = "TickMappingTestGhost";

        // Profiler.enabled and ProfilerDriver.enabled map to the same native field
        // (ProfilerSession::m_RecordEnabled), so we only save/restore one.
        bool m_PrevProfilerDriverEnabled;
        bool m_PrevProfilerDriverProfileEditor;
        bool m_PrevProfilerDeepProfiling;
        bool[] m_PrevAreaEnabled;
        bool m_ProfilerWindowWasOpen;

        SnapshotTickMappingSingleton m_Singleton;

        [SetUp]
        public void Setup()
        {
            m_PrevProfilerDriverEnabled = ProfilerDriver.enabled;
            m_PrevProfilerDriverProfileEditor = ProfilerDriver.profileEditor;
            m_PrevProfilerDeepProfiling = ProfilerDriver.deepProfiling;
            m_ProfilerWindowWasOpen = EditorWindow.HasOpenInstances<ProfilerWindow>();

            // Disable up front so frames captured between tests don't leak in.
            ProfilerDriver.enabled = false;
            m_PrevAreaEnabled = ProfilerFrameRecorder.DisableNonEssentialAreas();

            m_Singleton = SnapshotTickMappingSingleton.instance;
            m_Singleton.Initialize();
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            // Disable then yield once so in-flight frame integrations land before we clear.
            // ProfilerHistory::AddFrame and CleanupFrameHistory share a lock, so without
            // the yield ClearAllFrames can race with a frame that's still being integrated.
            ProfilerDriver.enabled = false;
            yield return null;
            ProfilerDriver.ClearAllFrames();

            ProfilerFrameRecorder.RestoreAreas(m_PrevAreaEnabled);
            ProfilerDriver.enabled = m_PrevProfilerDriverEnabled;
            ProfilerDriver.profileEditor = m_PrevProfilerDriverProfileEditor;
            ProfilerDriver.deepProfiling = m_PrevProfilerDeepProfiling;

            if (!m_ProfilerWindowWasOpen && EditorWindow.HasOpenInstances<ProfilerWindow>())
                EditorWindow.GetWindow<ProfilerWindow>().Close();
        }

        static IEnumerator CreateTestWorldAndRunProfiler(int frameCount, bool clearFrames = true)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(true, 1);
                var serverPrefab = DebuggingTestUtils.CreateEntityPrefab(testWorld.ServerWorld, k_GhostName);
                DebuggingTestUtils.CreateEntityPrefab(testWorld.ClientWorlds[0], k_GhostName);

                testWorld.Connect();
                testWorld.GoInGame();
                testWorld.TickMultiple(32);

                testWorld.ServerWorld.EntityManager.Instantiate(serverPrefab);

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, frameCount, clearFrames);
            }
        }

        [UnityTest, Description("Verifies that adjacent tick selection works in both directions.")]
        public IEnumerator Profiler_AdjacentTick_SelectsCorrectFrame()
        {
            yield return CreateTestWorldAndRunProfiler(10);

            // Step forward from the first frame to find the first tick
            var firstTickFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, ProfilerDriver.firstFrameIndex, 1);
            Assert.AreNotEqual(-1, firstTickFrame, "Should find a valid server tick frame");

            // Step forward to the next tick
            var nextFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, firstTickFrame, 1);
            Assert.Greater(nextFrame, firstTickFrame, "Next tick should be at a later frame");

            // Step backward should return to the first tick frame
            var prevFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, nextFrame, -1);
            Assert.AreEqual(firstTickFrame, prevFrame, "Previous tick should return to the starting frame");

            // Same for client role
            firstTickFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Client, ProfilerDriver.firstFrameIndex, 1);
            Assert.AreNotEqual(-1, firstTickFrame, "Should find a valid client tick frame");

            nextFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Client, firstTickFrame, 1);
            Assert.Greater(nextFrame, firstTickFrame);

            prevFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Client, nextFrame, -1);
            Assert.AreEqual(firstTickFrame, prevFrame);
        }

        [UnityTest, Description("Verifies that selecting the corresponding tick returns the correct frame.")]
        public IEnumerator Profiler_CorrespondingTick_SelectsCorrectFrame()
        {
            yield return CreateTestWorldAndRunProfiler(10);

            // In the test world both worlds tick together, so the corresponding frame should be the same frame
            var firstTickFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, ProfilerDriver.firstFrameIndex, 1);

            var clientFrame = m_Singleton.GetClientTickFrameIndexFromServerTickFrameIndex(firstTickFrame);
            Assert.AreEqual(firstTickFrame, clientFrame, "Client frame should match server frame when both worlds tick together");

            var serverFrame = m_Singleton.GetServerTickFrameIndexFromClientTickFrameIndex(firstTickFrame);
            Assert.AreEqual(firstTickFrame, serverFrame, "Server frame should match client frame when both worlds tick together");
        }

        [UnityTest, Description("Verifies that duplicate ticks across captures don't cause navigation to jump between captures.")]
        public IEnumerator Profiler_DuplicateTicks_NavigationStaysLocal()
        {
            // Run two captures without clearing to produce duplicate ticks
            yield return CreateTestWorldAndRunProfiler(20, clearFrames: true);
            var capture1Last = ProfilerDriver.lastFrameIndex;

            yield return CreateTestWorldAndRunProfiler(20, clearFrames: false);
            var capture2Last = ProfilerDriver.lastFrameIndex;
            Assert.Greater(capture2Last, capture1Last, "Second capture should have later frames");

            // Find a tick frame in capture 2 (must be after capture 1's last frame)
            var capture2Frame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, capture1Last, 1);
            Assert.Greater(capture2Frame, capture1Last, "Should find a tick in capture 2");

            // Navigate forward/backward within capture 2
            var nextFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, capture2Frame, 1);
            Assert.Greater(nextFrame, capture1Last, "Forward navigation should stay in capture 2");

            var prevFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, nextFrame, -1);
            Assert.Greater(prevFrame, capture1Last, "Backward navigation should stay in capture 2");

            // Cross-role from capture 2 should stay in capture 2
            var correspondingFrame = m_Singleton.GetClientTickFrameIndexFromServerTickFrameIndex(capture2Frame);
            Assert.Greater(correspondingFrame, capture1Last, "Cross-role should return frame in capture 2");
        }

        [UnityTest, Description("Verifies that FrameBelongsToTick returns true when the frame's tick matches.")]
        public IEnumerator Profiler_FrameBelongsToTick_MatchesCorrectly()
        {
            yield return CreateTestWorldAndRunProfiler(10);

            // Find a frame with a valid tick
            var tickFrame = m_Singleton.GetFrameIndexForAdjacentTick(NetworkRole.Server, ProfilerDriver.firstFrameIndex, 1);
            Assert.AreNotEqual(-1, tickFrame);

            // Get the actual tick at that frame
            Assert.IsTrue(m_Singleton.frameToSnapshotTickMapping.frameToTickInfo.TryGetValue(tickFrame, out var tickInfo));
            Assert.IsTrue(tickInfo.ServerTick.IsValid);

            // Matching tick returns true
            Assert.IsTrue(m_Singleton.FrameBelongsToTick(tickFrame, NetworkRole.Server, tickInfo.ServerTick));

            // Wrong tick returns false
            var wrongTick = tickInfo.ServerTick;
            wrongTick.Increment();
            Assert.IsFalse(m_Singleton.FrameBelongsToTick(tickFrame, NetworkRole.Server, wrongTick));

            // Missing frame returns false
            Assert.IsFalse(m_Singleton.FrameBelongsToTick(-999, NetworkRole.Server, tickInfo.ServerTick));
        }

        [UnityTest, Description("Verifies that the mapping is correctly persisted across serialization.")]
        public IEnumerator Profiler_FrameToTickMapping_IsPersistedAcrossSerialization()
        {
            yield return CreateTestWorldAndRunProfiler(10);

            // Iterate the actually-recorded range rather than a fixed count — the helper guarantees
            // at least N frames landed, but the exact range depends on profiler-frame boundaries.
            var firstFrame = ProfilerDriver.firstFrameIndex;
            var lastFrame = ProfilerDriver.lastFrameIndex;
            var clientTickFrames = new List<int>();
            var serverTickFrames = new List<int>();
            for (var i = firstFrame; i <= lastFrame; i++)
            {
                clientTickFrames.Add(m_Singleton.GetClientTickFrameIndexFromServerTickFrameIndex(i));
                serverTickFrames.Add(m_Singleton.GetServerTickFrameIndexFromClientTickFrameIndex(i));
            }

            CollectionAssert.AreEqual(clientTickFrames, serverTickFrames);

            // Serialize
            ((ISerializationCallbackReceiver)m_Singleton).OnBeforeSerialize();

            // Clear and deserialize
            m_Singleton.frameToSnapshotTickMapping.frameToTickInfo.Clear();
            ((ISerializationCallbackReceiver)m_Singleton).OnAfterDeserialize();

            // Assert mapping is restored
            Assert.IsTrue(m_Singleton.frameToSnapshotTickMapping.frameToTickInfo.Count != 0);
            for (var i = firstFrame; i <= lastFrame; i++)
            {
                var idx = i - firstFrame;
                Assert.AreEqual(clientTickFrames[idx], m_Singleton.GetClientTickFrameIndexFromServerTickFrameIndex(i));
                Assert.AreEqual(serverTickFrames[idx], m_Singleton.GetServerTickFrameIndexFromClientTickFrameIndex(i));
            }
        }

        [Test, Description("Verifies that NeedsUpdate returns false after MarkUpdated, and true after Clear.")]
        public void Profiler_NeedsUpdate_MarkUpdatedCycle()
        {
            var mapping = new FrameToSnapshotTickMapping();
            mapping.Initialize();

            // After init, m_LastMappedFrameIndex is -1, so any non-(-1) value triggers update
            Assert.IsTrue(mapping.NeedsUpdate(100));

            // MarkUpdated should prevent re-triggering
            mapping.MarkUpdated(100);
            Assert.IsFalse(mapping.NeedsUpdate(100));

            // Different lastFrameIndex should trigger update
            Assert.IsTrue(mapping.NeedsUpdate(200));

            // Clear resets, so same value should trigger update again
            mapping.Initialize();
            mapping.MarkUpdated(50);
            mapping.Clear();
            Assert.IsTrue(mapping.NeedsUpdate(50));
        }
    }
}
#endif
