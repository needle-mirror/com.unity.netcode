#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if NETCODE_DEBUG

using System.Collections;
using NUnit.Framework;
using Unity.NetCode.Editor;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// Test helper that exposes NetcodeProfilerTab's protected members for testing
    /// </summary>
    class TestNetcodeProfilerTab : NetcodeProfilerTab
    {
        internal TestNetcodeProfilerTab(NetworkRole networkRole) : base("Test", networkRole)
        {
        }

        internal TabInfoTextManager InfoTextManager => m_InfoTextManager;

        internal void UpdateInfoTextForTest(NetcodeFrameData frameData)
        {
            UpdateInfoText(frameData);
        }
    }

    /// <summary>
    /// Test helper that allows creating NetcodeForEntitiesProfilerModuleViewController without ProfilerWindow
    /// </summary>
    class TestNetcodeProfilerModuleViewController : NetcodeForEntitiesProfilerModuleViewController
    {
        internal TestNetcodeProfilerModuleViewController(NetworkRole networkRole)
            : base(null, networkRole)
        {
        }
    }

    class NetcodeProfilerInfoTextProviderTests
    {
        bool m_PrevProfilerDriverEnabled;
        bool m_PrevProfilerDriverProfileEditor;
        bool m_PrevProfilerDeepProfiling;
        bool[] m_PrevAreaEnabled;
        bool m_ProfilerWindowWasOpen;

        [SetUp]
        public void SetupProfilerTests()
        {
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
        }
        [Test]
        public void InfoTextProvider_ClientHostMode_ShowsCorrectly()
        {
            var provider = new ClientHostModeInfoTextProvider(NetworkRole.Client);

            // Should show when: client role + host mode
            var context = new InfoTextContext
            {
                NetworkRole = NetworkRole.Client,
                FrameData = new NetcodeFrameData
                {
                    isValid = false,
                    isHostMode = true,
                    hasConnectedClients = false,
                    hasWorldMetadata = true
                }
            };

            Assert.IsTrue(provider.ShouldShow(context),
                "Should show in client module during host mode");

            // Should NOT show when: provider configured for server role
            var serverProvider = new ClientHostModeInfoTextProvider(NetworkRole.Server);
            Assert.IsFalse(serverProvider.ShouldShow(context),
                "Should not show when provider is configured for server module");

            // Should NOT show when: not host mode (hasConnectedClients is irrelevant for this provider)
            context.NetworkRole = NetworkRole.Client;
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = true,  // Doesn't matter - provider only checks isHostMode
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when not in host mode");

            // Verify UI creation doesn't throw
            var infoText = provider.CreateInfoText();
            Assert.IsNotNull(infoText);
        }

        [Test]
        public void InfoTextProvider_HostModeNoClients_ShowsCorrectly()
        {
            var provider = new HostModeNoClientsInfoTextProvider(NetworkRole.Server);

            // Should show when: server role + host mode + no clients
            var context = new InfoTextContext
            {
                NetworkRole = NetworkRole.Server,
                FrameData = new NetcodeFrameData
                {
                    isValid = false,
                    isHostMode = true,
                    hasConnectedClients = false,
                    hasWorldMetadata = true
                }
            };

            Assert.IsTrue(provider.ShouldShow(context),
                "Should show in server module during host mode with no clients");

            // Should NOT show when: clients connected
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = true,
                hasConnectedClients = true,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when clients are connected");

            // Should NOT show when: not host mode
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when not in host mode");

            // Should NOT show when: provider configured for client role
            var clientProvider = new HostModeNoClientsInfoTextProvider(NetworkRole.Client);
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = true,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(clientProvider.ShouldShow(context),
                "Should not show when provider is configured for client module");

            // Verify UI creation doesn't throw
            context.NetworkRole = NetworkRole.Server;
            var infoText = provider.CreateInfoText();
            Assert.IsNotNull(infoText);
        }

        [Test]
        public void InfoTextProvider_ClientModuleOnServer_ShowsCorrectly()
        {
            var provider = new ClientModuleOnServerInfoTextProvider();

            // Should show when: client role + no data + not host mode + no world metadata
            var context = new InfoTextContext
            {
                NetworkRole = NetworkRole.Client,
                FrameData = new NetcodeFrameData
                {
                    isValid = false,
                    isHostMode = false,
                    hasConnectedClients = false,
                    hasWorldMetadata = false
                }
            };

            Assert.IsTrue(provider.ShouldShow(context),
                "Should show in client module when profiling dedicated server (no client world metadata)");

            // Should NOT show when: has world metadata (profiling local client)
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when client world metadata exists (profiling local client)");

            // Should NOT show when: host mode (ClientHostModeInfoTextProvider handles this)
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = true,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show in host mode");

            // Should NOT show when: data is valid
            context.FrameData = new NetcodeFrameData
            {
                isValid = true,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when data is valid");

            // Should NOT show when: server role
            context.NetworkRole = NetworkRole.Server;
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show in server module");

            // Verify UI creation doesn't throw
            context.NetworkRole = NetworkRole.Client;
            var infoText = provider.CreateInfoText();
            Assert.IsNotNull(infoText);
        }

        [Test]
        public void InfoTextProvider_ServerModuleNoClients_ShowsCorrectly()
        {
            var provider = new ServerModuleNoClientsInfoTextProvider(NetworkRole.Server);

            // Should show when: server role + no data + not host mode + no clients + has world metadata
            var context = new InfoTextContext
            {
                NetworkRole = NetworkRole.Server,
                FrameData = new NetcodeFrameData
                {
                    isValid = false,
                    isHostMode = false,
                    hasConnectedClients = false,
                    hasWorldMetadata = true
                }
            };

            Assert.IsTrue(provider.ShouldShow(context),
                "Should show in server module with no clients (server world exists)");

            // Should NOT show when: no world metadata (editor is client)
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when no server world metadata exists (editor is client)");

            // Should NOT show when: host mode (HostModeNoClientsInfoTextProvider handles this)
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = true,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show in host mode");

            // Should NOT show when: clients connected
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = true,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when clients are connected");

            // Should NOT show when: data is valid
            context.FrameData = new NetcodeFrameData
            {
                isValid = true,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when data is valid");

            // Should NOT show when: provider configured for client role
            var clientProvider = new ServerModuleNoClientsInfoTextProvider(NetworkRole.Client);
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(clientProvider.ShouldShow(context),
                "Should not show when provider is configured for client module");

            // Verify UI creation doesn't throw
            context.NetworkRole = NetworkRole.Server;
            var infoText = provider.CreateInfoText();
            Assert.IsNotNull(infoText);
        }

        [Test]
        public void InfoTextProvider_ServerModuleOnClient_ShowsCorrectly()
        {
            var provider = new ServerModuleOnClientInfoTextProvider(NetworkRole.Server);

            // Should show when: server role + no data + not host mode + no world metadata
            var context = new InfoTextContext
            {
                NetworkRole = NetworkRole.Server,
                FrameData = new NetcodeFrameData
                {
                    isValid = false,
                    isHostMode = false,
                    hasConnectedClients = false,
                    hasWorldMetadata = false
                }
            };

            Assert.IsTrue(provider.ShouldShow(context),
                "Should show in server module when editor is acting as client (no server world metadata)");

            // Should NOT show when: has world metadata (server exists locally)
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = true
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when server world metadata exists (server running locally)");

            // Should NOT show when: host mode
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = true,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show in host mode");

            // Should NOT show when: data is valid
            context.FrameData = new NetcodeFrameData
            {
                isValid = true,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show when data is valid");

            // Should NOT show when: client role
            context.NetworkRole = NetworkRole.Client;
            context.FrameData = new NetcodeFrameData
            {
                isValid = false,
                isHostMode = false,
                hasConnectedClients = false,
                hasWorldMetadata = false
            };
            Assert.IsFalse(provider.ShouldShow(context),
                "Should not show in client module");

            // Verify UI creation doesn't throw
            context.NetworkRole = NetworkRole.Server;
            var infoText = provider.CreateInfoText();
            Assert.IsNotNull(infoText);
        }


        [Test]
        public void InfoTextProvider_AllScenarios_CorrectProviderShown()
        {
            // This test verifies that all possible scenario combinations show the correct info text provider
            // Scenarios are defined by: Module, isValid, isHostMode, hasConnectedClients, hasWorldMetadata

            // Providers ordered from most specific to most generic (matching registration order)
            var providers = new IInfoTextProvider[]
            {
                // Host mode providers (most specific)
                new ClientHostModeInfoTextProvider(NetworkRole.Client),
                new HostModeNoClientsInfoTextProvider(NetworkRole.Server),
                // Role-specific providers
                new ServerModuleOnClientInfoTextProvider(NetworkRole.Server),
                new ServerModuleNoClientsInfoTextProvider(NetworkRole.Server),
                new ClientModuleOnServerInfoTextProvider(),
                // Generic fallback (least specific)
                new NoDataInfoTextProvider("received"),
            };

            // Helper method to get the first matching provider
            IInfoTextProvider GetMatchingProvider(InfoTextContext context)
            {
                foreach (var provider in providers)
                {
                    if (provider.ShouldShow(context))
                        return provider;
                }
                return null;
            }

            // Test CLIENT MODULE scenarios
            // Scenario: Client module, valid data
            var context = CreateContext(NetworkRole.Client, isValid: true, isHostMode: false, hasConnectedClients: false, hasWorldMetadata: true);
            Assert.IsNull(GetMatchingProvider(context), "Client with valid data should show no info text");

            // Scenario: Client module, host mode, no data
            context = CreateContext(NetworkRole.Client, isValid: false, isHostMode: true, hasConnectedClients: false, hasWorldMetadata: true);
            Assert.IsInstanceOf<ClientHostModeInfoTextProvider>(GetMatchingProvider(context),
                "Client in host mode should show ClientHostModeInfoTextProvider");

            // Scenario: Client module, profiling dedicated server (no client world)
            context = CreateContext(NetworkRole.Client, isValid: false, isHostMode: false, hasConnectedClients: false, hasWorldMetadata: false);
            Assert.IsInstanceOf<ClientModuleOnServerInfoTextProvider>(GetMatchingProvider(context),
                "Client module profiling server should show ClientModuleOnServerInfoTextProvider");

            // Scenario: Client module, local client, no data
            context = CreateContext(NetworkRole.Client, isValid: false, isHostMode: false, hasConnectedClients: false, hasWorldMetadata: true);
            Assert.IsInstanceOf<NoDataInfoTextProvider>(GetMatchingProvider(context),
                "Client with no data should show NoDataInfoTextProvider");

            // Scenario: Client module, binary worlds with server (has metadata but not host)
            context = CreateContext(NetworkRole.Client, isValid: false, isHostMode: false, hasConnectedClients: true, hasWorldMetadata: true);
            Assert.IsInstanceOf<NoDataInfoTextProvider>(GetMatchingProvider(context),
                "Client in binary worlds should show NoDataInfoTextProvider");

            // Test SERVER MODULE scenarios
            // Scenario: Server module, valid data
            context = CreateContext(NetworkRole.Server, isValid: true, isHostMode: false, hasConnectedClients: false, hasWorldMetadata: true);
            Assert.IsNull(GetMatchingProvider(context), "Server with valid data should show no info text");

            // Scenario: Server module, host mode, no clients
            context = CreateContext(NetworkRole.Server, isValid: false, isHostMode: true, hasConnectedClients: false, hasWorldMetadata: true);
            Assert.IsInstanceOf<HostModeNoClientsInfoTextProvider>(GetMatchingProvider(context),
                "Server in host mode with no clients should show HostModeNoClientsInfoTextProvider");

            // Scenario: Server module, host mode, with clients
            context = CreateContext(NetworkRole.Server, isValid: false, isHostMode: true, hasConnectedClients: true, hasWorldMetadata: true);
            Assert.IsInstanceOf<NoDataInfoTextProvider>(GetMatchingProvider(context),
                "Server in host mode with clients should show NoDataInfoTextProvider");

            // Scenario: Server module, dedicated server, no clients
            context = CreateContext(NetworkRole.Server, isValid: false, isHostMode: false, hasConnectedClients: false, hasWorldMetadata: true);
            Assert.IsInstanceOf<ServerModuleNoClientsInfoTextProvider>(GetMatchingProvider(context),
                "Server with no clients should show ServerModuleNoClientsInfoTextProvider");

            // Scenario: Server module, dedicated server, with clients
            context = CreateContext(NetworkRole.Server, isValid: false, isHostMode: false, hasConnectedClients: true, hasWorldMetadata: true);
            Assert.IsInstanceOf<NoDataInfoTextProvider>(GetMatchingProvider(context),
                "Server with clients should show NoDataInfoTextProvider");

            // Scenario: Server module, editor is client (no server world)
            context = CreateContext(NetworkRole.Server, isValid: false, isHostMode: false, hasConnectedClients: false, hasWorldMetadata: false);
            Assert.IsInstanceOf<ServerModuleOnClientInfoTextProvider>(GetMatchingProvider(context),
                "Server module when editor is client should show ServerModuleOnClientInfoTextProvider");
        }

        [UnityTest]
        public IEnumerator InfoTextProvider_HostWorldIntegration_ShowsCorrectProvider()
        {
            // Integration test: Create real host world, collect profiler data, and verify TabInfoTextManager
            // selects the correct provider using the actual implementation logic
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(server: false, numClients: 0, numHostWorlds: 1);

                // Connect the client
                testWorld.Connect();
                testWorld.GoInGame();

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                var hostWorld = testWorld.ServerWorld;
                Assert.IsNotNull(hostWorld, "Host world should be created");
                Assert.IsTrue(hostWorld.IsHost(), "World should be in host mode");

                // Use production code path: BuildFrameData -> UpdateInfoText -> provider selection
                var viewController = new TestNetcodeProfilerModuleViewController(NetworkRole.Server);
                var frameData = viewController.BuildFrameData(ProfilerDriver.lastFrameIndex);

                // Verify BuildFrameData extracted the correct profiler metadata
                Assert.IsTrue(frameData.isHostMode, "BuildFrameData should detect host mode from profiler");
                Assert.IsFalse(frameData.hasConnectedClients, "BuildFrameData should detect no connected clients");
                Assert.IsTrue(frameData.hasWorldMetadata, "BuildFrameData should detect world metadata exists");

                // Use the actual NetcodeProfilerTab implementation (constructor registers providers automatically)
                var tab = new TestNetcodeProfilerTab(NetworkRole.Server);
                tab.UpdateInfoTextForTest(frameData);

                // Verify the implementation selected the correct provider
                Assert.IsNotNull(tab.InfoTextManager.CurrentProvider, "TabInfoTextManager should select a provider");
                Assert.IsInstanceOf<HostModeNoClientsInfoTextProvider>(tab.InfoTextManager.CurrentProvider,
                    "Host mode with no clients should show HostModeNoClientsInfoTextProvider");
            }
        }

        [UnityTest]
        [DisableSingleWorldHostTest(isPermanentReason: "The host path is covered separately by InfoTextProvider_HostWorldIntegration_ShowsCorrectProvider.")]
        public IEnumerator InfoTextProvider_ServerWorldIntegration_ShowsCorrectProvider()
        {
            // Integration test: Create real server world, collect profiler data, and verify TabInfoTextManager
            // selects the correct provider using the actual implementation logic
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateGhostCollection();
                testWorld.CreateWorlds(server: true, numClients: 1);

                // Connect the client
                testWorld.Connect();
                testWorld.GoInGame();

                testWorld.DisposeAllClientWorlds();

                yield return ProfilerFrameRecorder.RecordFrames(testWorld, 10);

                var serverWorld = testWorld.ServerWorld;
                Assert.IsNotNull(serverWorld, "Server world should be created");
                Assert.IsTrue(serverWorld.IsServer(), "World should be server");
                Assert.IsFalse(serverWorld.IsHost(), "World should not be host");

                // Use production code path: BuildFrameData -> UpdateInfoText -> provider selection
                var viewController = new TestNetcodeProfilerModuleViewController(NetworkRole.Server);
                var frameData = viewController.BuildFrameData(ProfilerDriver.lastFrameIndex);

                // Verify BuildFrameData extracted the correct profiler metadata
                Assert.IsFalse(frameData.isHostMode, "BuildFrameData should not detect host mode from profiler");
                Assert.IsFalse(frameData.hasConnectedClients, "BuildFrameData should detect no connected clients");
                Assert.IsTrue(frameData.hasWorldMetadata, "BuildFrameData should detect world metadata exists");

                // Use the actual NetcodeProfilerTab implementation (constructor registers providers automatically)
                var tab = new TestNetcodeProfilerTab(NetworkRole.Server);
                tab.UpdateInfoTextForTest(frameData);

                // Verify the implementation selected the correct provider
                Assert.IsNotNull(tab.InfoTextManager.CurrentProvider, "TabInfoTextManager should select a provider");
                Assert.IsInstanceOf<ServerModuleNoClientsInfoTextProvider>(tab.InfoTextManager.CurrentProvider,
                    "Server with no clients should show ServerModuleNoClientsInfoTextProvider");
            }
        }

        // Helper method to create InfoTextContext with all parameters
        InfoTextContext CreateContext(NetworkRole role, bool isValid, bool isHostMode, bool hasConnectedClients, bool hasWorldMetadata)
        {
            return new InfoTextContext
            {
                NetworkRole = role,
                FrameData = new NetcodeFrameData
                {
                    isValid = isValid,
                    isHostMode = isHostMode,
                    hasConnectedClients = hasConnectedClients,
                    hasWorldMetadata = hasWorldMetadata
                }
            };
        }
    }
}

#endif
