using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
#if ENABLE_MANAGED_UNITYTLS
using Unity.Networking.Transport.TLS;
#endif
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using UnityEngine;


namespace Unity.NetCode
{
    /// <summary>
    /// Default helper method implementation for constructing <see cref="NetworkDriverStore.NetworkDriverInstance"/>,
    /// default <see cref="NetworkSettings"/> and registering these on the <see cref="NetworkDriverStore"/>.
    /// </summary>
    public static class DefaultDriverBuilder
    {
        internal const int DefaultPayloadCapacity = 16 * 1024;
        const int MaxFrameTimeMS = 100;
        const int DefaultWindowSize = 32;

        /// <summary>
        /// Return an instance of the <see cref="IPCAndSocketDriverConstructor"/> constructor
        /// </summary>
        public static INetworkStreamDriverConstructor DefaultDriverConstructor => new IPCAndSocketDriverConstructor();

        /// <inheritdoc cref="GetNetworkClientSettings"/>
        //[Obsolete("Renamed `GetNetworkClientSettings` (RemovedAfter 2.0). (UnityUpgradable) -> GetNetworkClientSettings(*)", false)]
        public static NetworkSettings GetNetworkSettings() => GetNetworkClientSettings();

        /// <summary>
        /// Return a set of default settings for the client world. This will use the NetworkSimulator parameters set by PlayMode Tools.
        /// </summary>
        /// <returns>A new <see cref="NetworkSettings"/> instance.</returns>
        public static NetworkSettings GetNetworkClientSettings()
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: DefaultWindowSize)
                .WithFragmentationStageParameters(payloadCapacity: DefaultPayloadCapacity);

            AddNetcodePackageNetworkConfigParameters(ref settings, isServer:false);
#if UNITY_EDITOR || NETCODE_DEBUG
            if (NetworkSimulatorSettings.Enabled)
            {
                NetworkSimulatorSettings.SetSimulatorSettings(ref settings);
            }
#endif
            return settings;
        }

        /// <inheritdoc cref="GetNetworkServerSettings()"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> GetNetworkServerSettings(*)", false)]
        public static NetworkSettings GetNetworkServerSettings(int playerCount = 0)
        {
            return GetNetworkServerSettings();
        }

        /// <summary>
        /// Return a set of internal default settings. This will use the NetworkSimulator parameters set by PlayMode Tools.
        /// </summary>
        /// <returns>Parameters that describe the network configuration.</returns>
        public static NetworkSettings GetNetworkServerSettings()
        {
            var settings = new NetworkSettings();
            settings.WithReliableStageParameters(windowSize: DefaultWindowSize)
                .WithFragmentationStageParameters(payloadCapacity: DefaultPayloadCapacity);
            AddNetcodePackageNetworkConfigParameters(ref settings, isServer:true);
            return settings;
        }

              /// <summary>
        /// Helper: Adds all netcode-package specific <see cref="NetCodeConfig.Global"/> settings
        /// for the <see cref="NetworkConfigParameter"/> struct.
        /// </summary>
        /// <param name="settings">The settings to inject into.</param>
        /// <param name="isServer">Settings differ for server worlds.</param>
        public static void AddNetcodePackageNetworkConfigParameters(ref NetworkSettings settings, bool isServer)
        {
            var config = NetCodeConfig.Global;
            //force retrieve the default if not already added
            if (!settings.TryGet(out NetworkConfigParameter ncp))
                ncp = settings.GetNetworkConfigParameters();
            if (config)
            {
                ncp.connectTimeoutMS = config.ConnectTimeoutMS;
                ncp.maxConnectAttempts = config.MaxConnectAttempts;
                ncp.disconnectTimeoutMS = config.DisconnectTimeoutMS;
                ncp.heartbeatTimeoutMS = config.HeartbeatTimeoutMS;
                ncp.reconnectionTimeoutMS = config.ReconnectionTimeoutMS;
                ncp.maxMessageSize = config.MaxMessageSize;
                ncp.receiveQueueCapacity = isServer ? config.ServerReceiveQueueCapacity : config.ClientReceiveQueueCapacity;
                ncp.sendQueueCapacity = isServer ? config.ServerSendQueueCapacity : config.ClientSendQueueCapacity;
            }

            // We use this method instead of the raw struct option because - if UTP add new fields,
            // this constructor will pick it up, a raw struct won't.
            settings.WithNetworkConfigParameters(
#if UNITY_EDITOR || NETCODE_DEBUG
                maxFrameTimeMS: MaxFrameTimeMS,
#endif
                connectTimeoutMS: ncp.connectTimeoutMS,
                maxConnectAttempts: ncp.maxConnectAttempts,
                disconnectTimeoutMS: ncp.disconnectTimeoutMS,
                heartbeatTimeoutMS: ncp.heartbeatTimeoutMS,
                reconnectionTimeoutMS: ncp.reconnectionTimeoutMS,
                maxMessageSize: ncp.maxMessageSize,
                receiveQueueCapacity: ncp.receiveQueueCapacity,
                sendQueueCapacity: ncp.sendQueueCapacity
            );
        }

        /// <summary>
        /// Helper method for creating NetworkDriver suitable for client.
        /// The driver will use the the specified <paramref name="netIf"/> and is configured
        /// using the internal defaults. See: <see cref="GetNetworkClientSettings"/>.
        /// </summary>
        /// <typeparam name="T">the <see cref="INetworkInterface"/> type ot use</typeparam>
        /// <param name="netIf">the instance of a <see cref="INetworkInterface"/> to use to create the driver</param>
        /// <returns>A new <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateClientNetworkDriver<T>(T netIf) where T : unmanaged, INetworkInterface
        {
            return CreateClientNetworkDriver(netIf, GetNetworkClientSettings());
        }

        /// <summary>
        /// Helper method for creating NetworkDriver suitable for client.
        /// The driver will use the specified <see cref="INetworkInterface"/> and is configured
        /// using the provided <paramref name="settings"/>.
        /// </summary>
        /// <typeparam name="T">the <see cref="INetworkInterface"/> type ot use</typeparam>
        /// <param name="netIf">the instance of a <see cref="INetworkInterface"/> to use to create the driver</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        /// <returns>A new <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateClientNetworkDriver<T>(T netIf, NetworkSettings settings) where T : unmanaged, INetworkInterface
        {
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
#if UNITY_EDITOR || NETCODE_DEBUG
            if (NetworkSimulatorSettings.Enabled)
            {
                driverInstance.driver = NetworkDriver.Create(netIf, settings);
                CreateClientSimulatorPipelines(ref driverInstance);
            }
            else
#endif
            {
                driverInstance.driver = NetworkDriver.Create(netIf, settings);
                CreateClientPipelines(ref driverInstance);
            }
            return driverInstance;
        }

        /// <inheritdoc cref="CreateServerNetworkDriver{T}(T)"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> CreateServerNetworkDriver<T>(*)", false)]
        public static NetworkDriverStore.NetworkDriverInstance CreateServerNetworkDriver<T>(T netIf, int playerCount = 0) where T : unmanaged, INetworkInterface
        {
            return CreateServerNetworkDriver(netIf);
        }

        /// <summary>
        /// Helper method for creating server NetworkDriver given the specified <paramref name="netIf"/>.
        /// The driver is configured with the internal defaults. See: <see cref="GetNetworkServerSettings"/>.
        /// </summary>
        /// <typeparam name="T">the <see cref="INetworkInterface"/> type ot use</typeparam>
        /// <param name="netIf">the instance of a <see cref="INetworkInterface"/> to use to create the driver</param>
        /// <returns>A new <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateServerNetworkDriver<T>(T netIf) where T : unmanaged, INetworkInterface
        {
            return CreateServerNetworkDriver(netIf, GetNetworkServerSettings());
        }

        /// <summary>
        /// Helper method for creating server NetworkDriver given the specified <paramref name="netIf"/>
        /// The driver is configured using the <paramref name="settings"/>
        /// </summary>
        /// <typeparam name="T">The <see cref="INetworkInterface"/> type to use</typeparam>
        /// <param name="netIf">the instance of a <see cref="INetworkInterface"/> to use to create the driver</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        /// <returns>A new <see cref="NetworkDriverStore.NetworkDriverInstance"/></returns>
        public static NetworkDriverStore.NetworkDriverInstance CreateServerNetworkDriver<T>(T netIf, NetworkSettings settings) where T : unmanaged, INetworkInterface
        {
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance
            {
                driver = NetworkDriver.Create(netIf, settings)
            };
            CreateServerPipelines(ref driverInstance);

            return driverInstance;
        }

        /// <summary>
        /// Helper method to determine if the client world should prefer using a socket-based network interface
        /// (UDP or WebSocket) or the <see cref="IPCNetworkInterface"/>.
        /// IPC connection type is preferred only in case the <see cref="ClientServerBootstrap.RequestedPlayType"/> is set to
        /// client/server mode, a server world exist in the process and the <see cref="NetworkSimulatorSettings"/> are disable (in the editor or development build).
        /// </summary>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <returns>True when a client world should use a network driver which implements a socket based interface.</returns>
        /// <remarks>This method should not be used to configure server driver. Also, for server build, this method always return true.</remarks>
        public static bool ClientUseSocketDriver(NetDebug netDebug)
        {
#if !UNITY_CLIENT
#if UNITY_EDITOR || NETCODE_DEBUG
            //if the emulator is enabled we always force to use sockets. It also work with IPC but this is preferred choice.
            if (NetworkSimulatorSettings.Enabled)
            {
                netDebug.DebugLog("[DefaultDriverConstructor.ClientUseSocketDriver] Network simulator enabled. Forcing client to use a socket network driver, rather than an IPC.");
                return true;
            }
#endif
            //The client playmode is always set if UNITY_CLIENT define is present
            if (ClientServerBootstrap.RequestedPlayType == ClientServerBootstrap.PlayType.Client)
            {
                return true;
            }
            netDebug.DebugLog("[DefaultDriverConstructor.ClientUseSocketDriver] RequestedPlayType is ClientAndServer Or Server, so looking for a server world instance in the same process.");
            if (ClientServerBootstrap.ServerWorld != null && ClientServerBootstrap.ServerWorld.IsCreated)
            {
                netDebug.DebugLog("[DefaultDriverConstructor.ClientUseSocketDriver] Found server world instance. Thus, preferring IPC network interface.");
                return false;
            }
#endif
            return true;
        }

        /// <summary>
        /// Register a NetworkDriver instance in the <paramref name="driverStore"/> that uses either:
        /// <list type="bullet">
        /// <item>a single IPCNetworkInterface NetworkDriver if both the client and server worlds are present in the same process.</item>
        /// <item>a single UDPNetworkInterface driver if you are targeting a standalone platform.</item>
        /// <item>a single WebSocketNetworkInterface if you are targeting WebGL.</item>
        /// </list>
        /// These are configured using internal defaults. See: <see cref="GetNetworkClientSettings"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            RegisterClientDriver(world, ref driverStore, netDebug, GetNetworkClientSettings());
        }

        /// <summary>
        /// Register a NetworkDriver instance in the <paramref name="driverStore"/> that uses either:
        /// <list type="bullet">
        /// <item>a single IPCNetworkInterface NetworkDriver if both the client and server worlds are present in the same process.</item>
        /// <item>a single UDPNetworkInterface driver if you are targeting a standalone platform.</item>
        /// <item>a single WebSocketNetworkInterface if you are targeting WebGL.</item>
        /// </list>
        /// These are configured using the <paramref name="settings"/> passed in.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            if (ClientUseSocketDriver(netDebug))
            {
#if !UNITY_WEBGL || UNITY_EDITOR
                RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
#else
                RegisterClientWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
            }
            else
            {
                RegisterClientIpcDriver(world, ref driverStore, netDebug, settings);
            }
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        /// <summary>
        /// Register a <see cref="UDPNetworkInterface"/> NetworkDriver instance in <paramref name="driverStore"/>.
        /// This are configured using the <paramref name="settings"/> passed in.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        public static void RegisterClientUdpDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsClient());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterClientUdpDriver] Creating the client default UDP socket network interface driver.");
            var driverInstance = DefaultDriverBuilder.CreateClientNetworkDriver(new UDPNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.Socket, driverInstance);
        }
#endif
        /// <summary>
        /// Register a <see cref="WebSocketNetworkInterface"/> NetworkDriver instance in <paramref name="driverStore"/>.
        /// This are configured using the <paramref name="settings"/> passed in. The constructed driver
        /// does not use a reliable pipeline stage (websocket are already reliable) and the <see cref="NetworkDriverStore.NetworkDriverInstance.reliablePipeline"/>
        /// instance is a <see cref="NullPipelineStage"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        public static void RegisterClientWebSocketDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug,
            NetworkSettings settings)
        {
            Assert.IsTrue(world.IsClient());
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance();
#if UNITY_EDITOR || NETCODE_DEBUG
            if (NetworkSimulatorSettings.Enabled)
            {
                driverInstance.driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
                // Web socket does not require reliable pipeline, nor technically the fragmented stage, but they need
                // to be kept around for compatibility reasons for cross-platform connections to non-webgl players
                CreateClientSimulatorPipelines(ref driverInstance);
            }
            else
#endif
            {
                driverInstance.driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
                // Web socket does not require reliable pipeline, nor technically the fragmented stage, but they need
                // to be kept around for compatibility reasons for cross-platform connections to non-webgl players
                CreateClientPipelines(ref driverInstance);
            }
            driverStore.RegisterDriver(TransportType.Socket, driverInstance);
        }
        /// <summary>
        /// Register an <see cref="IPCNetworkInterface"/> NetworkDriver instance in <paramref name="driverStore"/>.
        /// This are configured using the <paramref name="settings"/> passed in.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        public static void RegisterClientIpcDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsClient());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterClientIpcDriver] Creating the client default IPC network interface driver.");
            var driverInstance = DefaultDriverBuilder.CreateClientNetworkDriver(new IPCNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.IPC, driverInstance);
        }

        /// <inheritdoc cref="RegisterServerDriver(World, ref NetworkDriverStore, NetDebug)"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> RegisterServerDriver(*)", false)]
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, int playerCount = 0)
        {
            RegisterServerDriver(world, ref driverStore, netDebug);
        }

        /// <summary>
        /// Register multiple NetworkDriver instances to the <paramref name="driverStore"/> that uses different <see cref="INetworkInterface"/>:
        /// <list type="bullet">
        /// <item>One driver that uses `IPCNetworkInterface` if the `ClientServerBootstrap.RequestedPlayType` is Client/Server.</item>
        /// <item>One driver that uses `UDPNetworkInterface` if the current build target is a standalone platorm (no WebGL) or dedicated server.</item>
        /// <item>One driver that uses `WebSocketNetworkInterface` if the current build target is WebGL.</item>
        /// </list>
        /// These are configured using internal defaults. See: <see cref="GetNetworkClientSettings"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            RegisterServerDriver(world, ref driverStore, netDebug, GetNetworkServerSettings());
        }

        /// <summary>
        /// Register a multiple NetworkDriver instances to hte <paramref name="driverStore"/>: <br/>
        /// <list type="bullet">
        /// <item>One driver that uses `IPCNetworkInterface` if the `ClientServerBootstrap.RequestedPlayType` is Client/Server.</item>
        /// <item>One driver that uses `UDPNetworkInterface` if the current build target is a standalone platorm (no WebGL) or dedicated server.</item>
        /// <item>One driver that uses `WebSocketNetworkInterface` if the current build target is WebGL.</item>
        /// </list>
        /// These drivers are configured using the <paramref name="settings">NetworkSettings</paramref> passed in.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        /// <remarks>Not available for WebGL builds. Always available in the Editor.</remarks>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            RegisterServerIpcDriver(world, ref driverStore, netDebug, settings);
#if !UNITY_WEBGL || UNITY_EDITOR
            RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#else
            RegisterServerWebSocketDriver(world, ref driverStore, netDebug, settings);
#endif
        }

        /// <summary>
        /// Register a <see cref="IPCNetworkInterface"/> NetworkDriver instance in <paramref name="driverStore"/>.
        /// This are configured using the <paramref name="settings"/> passed in.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        /// <remarks>Not available for WebGL builds. Always available in the Editor.</remarks>
        public static void RegisterServerIpcDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsServer());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterServerIpcDriver] Creating the server default IPC network interface driver.");
            var ipcDriver = CreateServerNetworkDriver(new IPCNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.IPC, ipcDriver);
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        /// <summary>
        /// Register a <see cref="UDPNetworkInterface"/> NetworkDriver instance in <paramref name="driverStore"/>.
        /// These are configured using the <paramref name="settings"/> passed in.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        /// <remarks>Not available for WebGL builds. Always available in the Editor.</remarks>
        public static void RegisterServerUdpDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, NetworkSettings settings)
        {
            Assert.IsTrue(world.IsServer());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterServerUdpDriver] Creating the server default socket network interface driver.");
            var socketDriver = CreateServerNetworkDriver(new UDPNetworkInterface(), settings);
            driverStore.RegisterDriver(TransportType.Socket, socketDriver);
        }
#endif

        /// <summary>
        /// Register a <see cref="WebSocketNetworkInterface"/> NetworkDriver instance in <paramref name="driverStore"/>.
        /// This are configured using the <paramref name="settings"/> passed in. The constructed driver
        /// does not use a reliable pipeline stage (websocket are already reliable) and the <see cref="NetworkDriverStore.NetworkDriverInstance.reliablePipeline"/>
        /// instance is a <see cref="NullPipelineStage"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="settings">A list of the parameters that describe the network configuration.</param>
        /// <remarks>Not available for WebGL build. Always available in the Editor.</remarks>
        public static void RegisterServerWebSocketDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug,
            NetworkSettings settings)
        {
            Assert.IsTrue(ClientServerBootstrap.RequestedPlayType != ClientServerBootstrap.PlayType.Client);
            Assert.IsTrue(world.IsServer());
            netDebug.DebugLog("[DefaultDriverConstructor.RegisterServerWebSocketDriver] Creating the server WebSocket network interface driver.");
            var driverInstance = new NetworkDriverStore.NetworkDriverInstance
            {
                driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings)
            };
            // Web socket does not require reliable pipeline, nor technically the fragmented stage, but they need
            // to be kept around for compatibility reasons for cross-platform connections to non-webgl players
            CreateServerPipelines(ref driverInstance);
            driverStore.RegisterDriver(TransportType.Socket, driverInstance);
        }

        /// <summary>
        /// Create the default network pipelines (reliable, unreliable, unreliable fragmented) for the client.
        /// </summary>
        /// <param name="driverInstance">The <see cref="NetworkDriverStore.NetworkDriverInstance"/> instance to configure</param>
        public static void CreateClientPipelines(ref NetworkDriverStore.NetworkDriverInstance driverInstance)
        {
            driverInstance.unreliablePipeline = driverInstance.driver.CreatePipeline(typeof(NullPipelineStage));
            driverInstance.reliablePipeline = driverInstance.driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            driverInstance.unreliableFragmentedPipeline = driverInstance.driver.CreatePipeline(typeof(FragmentationPipelineStage));
        }

        /// <summary>
        /// Create the default network pipelines (reliable, unreliable, unreliable fragmented) for the server.
        /// </summary>
        /// <param name="driverInstance">The <see cref="NetworkDriverStore.NetworkDriverInstance"/> instance to configure</param>
        public static void CreateServerPipelines(ref NetworkDriverStore.NetworkDriverInstance driverInstance)
        {
            driverInstance.unreliablePipeline = driverInstance.driver.CreatePipeline(typeof(NullPipelineStage));
            driverInstance.reliablePipeline = driverInstance.driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            driverInstance.unreliableFragmentedPipeline = driverInstance.driver.CreatePipeline(typeof(FragmentationPipelineStage));
        }

#if UNITY_EDITOR || NETCODE_DEBUG || UNITY_INCLUDE_TESTS
        /// <summary>
        /// Should be used only for configuring client drivers, create the network pipelines (reliable, unreliable, unreliable fragmented)
        /// with network simulator support.
        /// </summary>
        /// <param name="driverInstance"></param>
        public static void CreateClientSimulatorPipelines(ref NetworkDriverStore.NetworkDriverInstance driverInstance)
        {
            driverInstance.unreliablePipeline = driverInstance.driver.CreatePipeline(
                typeof(SimulatorPipelineStage));
            driverInstance.reliablePipeline = driverInstance.driver.CreatePipeline(
                typeof(ReliableSequencedPipelineStage),
                typeof(SimulatorPipelineStage));
            driverInstance.unreliableFragmentedPipeline = driverInstance.driver.CreatePipeline(
                typeof(FragmentationPipelineStage),
                typeof(SimulatorPipelineStage));
        }
#endif
#if ENABLE_MANAGED_UNITYTLS
        /// <summary>
        /// Register a NetworkDriver instance in and stores it in <paramref name="driverStore"/>:<br/>
        ///     - a single <see cref="IPCNetworkInterface"/> NetworkDriver if the both client and server worlds are present in the same process.<br/>
        ///     - a single <see cref="UDPNetworkInterface"/> driver in all other cases.<br/>
        /// These are configured using the default settings. See <see cref="GetNetworkClientSettings"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="caCertificate">Signed server certificate.</param>
        /// <param name="serverName">Common name in the server certificate.</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref FixedString4096Bytes caCertificate, ref FixedString512Bytes serverName)
        {
            var settings = GetNetworkClientSettings();
            settings = settings.WithSecureClientParameters(caCertificate: ref caCertificate, serverName: ref serverName);
            RegisterClientDriver(world, ref driverStore, netDebug, settings);
        }

        /// <inheritdoc cref="RegisterServerDriver(World, ref NetworkDriverStore, NetDebug, ref FixedString4096Bytes, ref FixedString4096Bytes)"/>
        //[Obsolete("Removed default parameter `GetNetworkClientSettings` (RemovedAfter 2.0). (UnityUpgradable) -> RegisterServerDriver(*)", false)]
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug,
            ref FixedString4096Bytes certificate, ref FixedString4096Bytes privateKey, int playerCount = 0)
        {
            RegisterServerDriver(world, ref driverStore, netDebug, ref certificate, ref privateKey);
        }

        /// <summary>
        /// Register a multiple NetworkDriver instances to hte <paramref name="driverStore"/>: <br/>
        /// <list type="bullet">
        /// <item>One driver that uses IPCNetworkInterface if the ClientServerBootstrap.RequestedPlayType is Client/Server.</item>
        /// <item>For all targets apart WebGL, one driver instance using a UDPNetworkInterface. For WebGL and in the Editor, one driver instance using the
        /// WebSocketNetworkInterface.</item>
        /// </list>
        /// These are configured using the default settings. See <see cref="GetNetworkServerSettings"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="certificate"></param>
        /// <param name="privateKey"></param>
        /// <remarks>Not available for WebGL builds. Always available in the Editor.</remarks>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref FixedString4096Bytes certificate, ref FixedString4096Bytes privateKey)
        {
            var settings = GetNetworkServerSettings();
            settings = settings.WithSecureServerParameters(certificate: ref certificate, privateKey: ref privateKey);
            RegisterServerDriver(world, ref driverStore, netDebug, settings);
        }
#endif
        /// <summary>
        /// Register a NetworkDriver instance in and stores it in <paramref name="driverStore"/>:<br/>
        ///     - a single <see cref="IPCNetworkInterface"/> NetworkDriver if the both client and server worlds are present in the same process.<br/>
        ///     - a single <see cref="UDPNetworkInterface"/> driver in all other cases.<br/>
        /// These are configured using the default settings. See <see cref="GetNetworkClientSettings"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="relayData">Server information to make a connection using a relay server.</param>
        public static void RegisterClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref RelayServerData relayData)
        {
            var settings = GetNetworkClientSettings();
            if (ClientUseSocketDriver(netDebug))
            {
                settings = settings.WithRelayParameters(ref relayData);
            }
            RegisterClientDriver(world, ref driverStore, netDebug, settings);
        }

        /// <inheritdoc cref="RegisterServerDriver(World, ref NetworkDriverStore, NetDebug, ref RelayServerData)"/>
        //[Obsolete("Removed playerCount (RemovedAfter 2.0). (UnityUpgradable) -> RegisterServerDriver(*)", false)]
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref RelayServerData relayData, int playerCount = 0)
        {
            RegisterServerDriver(world, ref driverStore, netDebug, ref relayData);
        }

        /// <summary>
        /// Register multiple NetworkDriver instances to the <paramref name="driverStore"/> that uses different <see cref="INetworkInterface"/>:
        /// <list type="bullet">
        /// <item>One driver that uses IPCNetworkInterface if the ClientServerBootstrap.RequestedPlayType is Client/Server.</item>
        /// <item>One driver that uses UDPNetworkInterface if the current build target is a standalone platorm (no WebGL) or dedicated server.</item>
        /// <item>One driver that uses WebSocketNetworkInterface if the current build target is WebGL.</item>
        /// </list>
        /// These are configured using internal defaults. See: <see cref="GetNetworkClientSettings"/>.
        /// </summary>
        /// <param name="world">Used for determining whether we are running in a client or server world.</param>
        /// <param name="driverStore">Store for NetworkDriver.</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        /// <param name="relayData">Server information to make a connection using a relay server.</param>
        /// <remarks>Not available for WebGL builds. Always available in the Editor.</remarks>
        public static void RegisterServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug, ref RelayServerData relayData)
        {
            var settings = GetNetworkServerSettings();
            RegisterServerIpcDriver(world, ref driverStore, netDebug, settings);
            settings = settings.WithRelayParameters(ref relayData);
#if !UNITY_WEBGL || UNITY_EDITOR
            RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
#endif
        }
    }

    /// <summary>
    /// The default NetCode driver constructor, initialise the server world to use multiple <see cref="INetworkInterface"/> and the client world using
    /// a single <see cref="INetworkInterface"/>, depending on the current <see cref="ClientServerBootstrap.RequestedPlayType"/> and current platform.
    /// In particular:
    /// - On the server: both <see cref="IPCNetworkInterface"/> and <see cref="UDPNetworkInterface"/> NetworkDriver in the editor and only
    ///   a single <see cref="UDPNetworkInterface"/> driver in the build.<br/>
    /// - On the client:<br/>
    ///     - a single <see cref="IPCNetworkInterface"/> NetworkDriver if the both client and server worlds are present in the same process.<br/>
    ///     - a single <see cref="UDPNetworkInterface"/> driver in all other cases.<br/>
    /// In the Editor and Development build, if the network simulator is enabled, force on the client to use the <see cref="UDPNetworkInterface"/> network driver.
    /// <b>To let the client use the IPC network interface when in client and server mode, you must create the server world first (in other words; call `NetworkStreamDriver.Listen` on it before attempting to connect to it).</b>
    /// </summary>
    public struct IPCAndSocketDriverConstructor : INetworkStreamDriverConstructor
    {
        /// <summary>
        /// Create and register a new <see cref="NetworkDriver"/> suitable for connecting client to server to the destination <see cref="NetworkDriverStore"/>.
        /// The network driver instance will use socket or IPC network interfaces based on the <see cref="ClientServerBootstrap.RequestedPlayType"/> and the
        /// presence of a server instance in the same process. <br/>
        /// For WebGL builds, client use by default the <see cref="WebSocketNetworkInterface"/>.
        /// </summary>
        /// <param name="world">The destination world in which the driver will be created</param>
        /// <param name="driverStore">An instance of a <see cref="NetworkDriverStore"/> where the driver will be registered</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug, DefaultDriverBuilder.GetNetworkClientSettings());
        }

        /// <summary>
        /// Create and register one or more network drivers that can be used to listen for incoming connection into the destination <see cref="NetworkDriverStore"/>.
        /// By default, a <see cref="NetworkDriver"/> that uses a socket network interface is always created. For WebGL builds in particular,
        /// the server use the <see cref="WebSocketNetworkInterface"/> for communicating with the clients. <br/>
        /// In the Editor or in a Client/Server player build, if the <see cref="ClientServerBootstrap.RequestedPlayType"/> mode is set to
        /// <see cref="ClientServerBootstrap.PlayType.ClientAndServer"/>, a second <see cref="NetworkDriver"/> that use an IPC network interface will be also created and
        /// that will be used for minimizing the latency for the in-proc client connection.
        /// </summary>
        /// <param name="world">The destination world in which the driver will be created</param>
        /// <param name="driverStore">An instance of a <see cref="NetworkDriverStore"/> where the driver will be registered</param>
        /// <param name="netDebug">The <see cref="netDebug"/> singleton, for logging errors and debug information</param>
        public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
        {
#if UNITY_EDITOR || !UNITY_WEBGL
            DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug, DefaultDriverBuilder.GetNetworkServerSettings());
#else
            throw new NotSupportedException(
                "It is not valid to use the `IPCAndSocketDriverConstructor` as default constructor for configure the Server NetworkDriverStore for WebGL build.\n" +
                "For self-hosting scenario (client/server mode) using WebGL player, in order to be able to listen for incoming connections you need to use Unity.Relay. Therefore,\n" +
                "you must create a custom INetworkStreamDriverConstructor implementation that will setup the server driver using NetworkSettings that include the necessary relay data.\n" +
                "Please refer to the Netcode For Entities documentation, in particular `Configure NetworkDriverStore to use Unity.Relay` section for more details about how to do it.");
#endif
        }
    }
}
