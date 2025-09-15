using System;
using System.Text;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    ///     Config file, allowing the package user to tweak netcode variables without having to write code.
    ///     Create as many instances as you like.
    /// </summary>
    [CreateAssetMenu(menuName = "Multiplayer/NetCodeConfig Asset", fileName = "NetCodeConfig", order = 1)]
    public class NetCodeConfig : ScriptableObject, IComparable<NetCodeConfig>
    {
        /// <summary>
        ///     The Default NetcodeConfig asset, selected in ProjectSettings via the NetCode tab,
        ///     and fetched at runtime via the PreloadedAssets. Set via <see cref="RuntimeInitializeOnLoadMethodAttribute"/>.
        /// </summary>
        public static NetCodeConfig Global { get; internal set; }

        /// <summary> <see cref="ClientServerBootstrap"/> to either be <see cref="EnableAutomaticBootstrap"/> or <see cref="DisableAutomaticBootstrap"/>.</summary>
        public enum AutomaticBootstrapSetting
        {
            /// <summary>ENABLES the default <see cref="Unity.Entities.ICustomBootstrap"/> Entities bootstrap.</summary>
            EnableAutomaticBootstrap = 1,
            /// <summary>DISABLES the default <see cref="Unity.Entities.ICustomBootstrap"/> Entities bootstrap.</summary>
            /// <remarks>Only the Local world will be created, as if you called <see cref="ClientServerBootstrap.CreateLocalWorld"/>.</remarks>
            DisableAutomaticBootstrap = 0,
        }

        /// <summary>
        /// Which client-hosted mode to use.
        /// </summary>
#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        public enum HostWorldMode
#else
        internal enum HostWorldMode
#endif
        {
            /// <summary>
            /// A local client and server world are used to host a server on a client. A local IPC connection is used for communication between the two.
            /// </summary>
            BinaryWorlds = 0, // TODO change this so SingleWorld is default in N4E 2.0.
            // TODO start host methods in Unified Netcode should have single world as the default

            /// <summary>
            /// This world acts as both client and server. There's no client to server connection, only a listening driver. A fake connection entity is generated for convenience.
            /// </summary>
            SingleWorld = 1,
        }

        /// <summary>
        /// Netcode helper: Allows you to add multiple configs to the PreloadedAssets list. There can only be one global one.
        /// </summary>
        public bool IsGlobalConfig;

        /// <summary>
        ///     Denotes if the ClientServerBootstrap (or any derived version of it) should be triggered on game boot. Project-wide
        ///     setting, overridable via the OverrideAutomaticNetCodeBootstrap MonoBehaviour.
        /// </summary>
        [Header("NetCode")]
        [Tooltip("Denotes if the ClientServerBootstrap (or any derived version of it) should be triggered on game boot. Project-wide setting (when this config is applied in the Netcode tab), overridable via the OverrideAutomaticNetCodeBootstrap MonoBehaviour.")] [SerializeField]
        public AutomaticBootstrapSetting EnableClientServerBootstrap = AutomaticBootstrapSetting.EnableAutomaticBootstrap;

#if NETCODE_EXPERIMENTAL_SINGLE_WORLD_HOST
        /// <summary>
        /// Denotes which client-hosted server world mode to use. Single world mode will create a world that acts as both client and server. Binary world mode will create a client and a server world, connected together through intra-process communication (IPC).
        /// </summary>
        /// <remarks>
        /// Once this is set, the expectation is that users will create their whole project with this assumption. This shouldn't be something you change lightly once in a while to test things. This should be commited to your project's source control.
        /// </remarks>
        [Tooltip("Denotes which client-hosted server world mode to use. Single world mode will create a world that acts as both client and server. Binary world mode will create a client and a server world, connected together through intra-process communication (IPC).")]
        [SerializeField]
        public HostWorldMode HostWorldModeSelection;
#else
        internal HostWorldMode HostWorldModeSelection;
#endif

        // TODO - Add a helper link to open the NetDbg when viewing the NetConfig asset.
        /// <inheritdoc cref="Unity.NetCode.ClientServerTickRate" path="/summary"/>
        public ClientServerTickRate ClientServerTickRate;
        /// <inheritdoc cref="Unity.NetCode.ClientTickRate"/>
        public ClientTickRate ClientTickRate;
        // TODO - World creation options.
        // TODO - Thin Client options.
        /// <inheritdoc cref="Unity.NetCode.GhostSendSystemData"/>
        public GhostSendSystemData GhostSendSystemData;
        // TODO - Importance.
        // TODO - Relevancy.

        // Transport:
        /// <inheritdoc cref="NetworkConfigParameter.connectTimeoutMS"/>
        [Tooltip("Time between connection attempts, in milliseconds.")]
        [Min(1)]
        public int ConnectTimeoutMS;

        /// <inheritdoc cref="NetworkConfigParameter.maxConnectAttempts"/>
        [Tooltip("Maximum number of connection attempts to try. If no answer is received from the server after this number of attempts, a <b>Disconnect</b> event is generated for the connection.")]
        [Min(1)]
        public int MaxConnectAttempts;

        /// <inheritdoc cref="NetworkConfigParameter.disconnectTimeoutMS"/>
        [Tooltip("Inactivity timeout for a connection, in milliseconds. If nothing is received on a connection for this amount of time, it is disconnected (a <b>Disconnect</b> event will be generated).\n\nTo prevent this from happening when the game session is simply quiet, set <b>heartbeatTimeoutMS</b> to a positive non-zero value.")]
        [Min(1)]
        public int DisconnectTimeoutMS;

        /// <inheritdoc cref="NetworkConfigParameter.heartbeatTimeoutMS"/>
        [Tooltip("Time after which if nothing from a peer is received, a heartbeat message will be sent to keep the connection alive. Prevents the <b>disconnectTimeoutMS</b> mechanism from kicking when nothing happens on a connection. A value of 0 will disable heartbeats.")]
        [Min(1)]
        public int HeartbeatTimeoutMS;

        /// <inheritdoc cref="NetworkConfigParameter.reconnectionTimeoutMS"/>
        [Tooltip("Time after which to attempt to re-establish a connection if nothing is received from the peer. This is used to re-establish connections for example when a peer's IP address changes (e. g. mobile roaming scenarios).\n\nTo be effective, should be less than <b>disconnectTimeoutMS</b> but greater than <b>heartbeatTimeoutMS</b>.\n\nA value of 0 will disable this functionality.")]
        [Min(1)]
        public int ReconnectionTimeoutMS;

        /// <summary>
        ///     Capacity of the send queue (per pipeline-stage) on the client.
        ///     This should be the maximum number of packets expected to be sent by the client in a single update (i.e. each render frame).
        ///     Broad recommendation: 8 If not memory constrained, else use minimum, as it can affect Reliable and Fragmentation pipeline throughput.
        /// </summary>
        /// <seealso cref="NetworkConfigParameter.sendQueueCapacity"/>
        [Tooltip(@"Capacity of the send queue (per pipeline-stage) on the client.
This should be the maximum number of packets expected to be sent by the client, per pipeline-stage, in a single update (i.e. each render frame).

Recommended value: 8 if not memory constrained, else minimum, as it can affect Reliable and Fragmentation pipeline throughput.
Default value: 512 i.e. <b>NetworkParameterConstants.SendQueueCapacity</b>")]
        [Min(4)]
        public int ClientSendQueueCapacity;

        /// <summary>
        ///     Capacity of the receive queue (per pipeline-stage) on the client.
        ///     This should be the maximum number of in-flight packets expected to be received by the client - from the
        ///     server - during a worst-case frame (like if the client executable stalls).
        ///     Broad recommendation: 64.
        /// </summary>
        /// <seealso cref="NetworkConfigParameter.receiveQueueCapacity"/>
        [Tooltip(@"Capacity of the receive queue (per pipeline-stage) on the client.
This should be the maximum number of in-flight packets expected to be received by the client - from the
server - during a worst-case frame (like if the client executable stalls).

Broad recommendation: 64.
Default value: 512 i.e. <b>NetworkParameterConstants.ReceiveQueueCapacity</b>")]
        [Min(8)]
        public int ClientReceiveQueueCapacity;

        /// <summary>
        ///     Capacity of the send queue (per pipeline-stage) on the server.
        ///     This should be a multiple (likely 1) of the maximum number of packets expected to be sent by the server, across all
        ///     connections, on a per pipeline-stage basis, in a single update (i.e. each render frame).
        ///     Broad recommendations: For 2 players, ~64. For 100 players, ~100. For 1k players, ~1k.
        /// </summary>
        /// <example><c>1 packet per pipeline-stage, per connection, for a game supporting, at most, 512 players per server.</c></example>
        /// <seealso cref="NetworkConfigParameter.sendQueueCapacity"/>
        [Tooltip(@"Capacity of the send queue (per pipeline-stage) on the server.
This should be a multiple of the maximum number of packets expected to be sent by the server, across all connections, on a per pipeline-stage basis, in a single update (i.e. each render frame).

For 2 players, ~128. For 100 players, ~512. For 1k players, ~1k.
<i>If memory constrained, use minimum, but note it can affect Reliable and Fragmentation pipeline throughput.
Default value: 512 i.e. <b>NetworkParameterConstants.SendQueueCapacity</b>")]
        [Min(16)]
        public int ServerSendQueueCapacity;

        /// <summary>
        ///     Capacity of the receive queue (per pipeline-stage) on the server.
        ///     This should be the maximum number of in-flight packets - expected to be sent across by the maximum supported
        ///     number of connected clients - to the server - arriving within a worst-case server game loop update.
        ///     Broad recommendations: For 2 players, ~64. For 100 players, ~512. For 1k players, ~1.2k.
        /// </summary>
        /// <seealso cref="NetworkConfigParameter.receiveQueueCapacity"/>
        [Tooltip(@"Capacity of the receive queue (per pipeline-stage) on the server.
This should be the maximum number of in-flight packets - expected to be sent across by the maximum supported
number of connected clients - to the server - arriving within a worst-case server game loop update.

Broad recommendations: For 2 players, ~64. For 100 players, ~512. For 1k players, ~1.2k.
Default value: 512 i.e. <b>NetworkParameterConstants.ReceiveQueueCapacity</b>")]
        [Min(64)]
        public int ServerReceiveQueueCapacity;

        /// <inheritdoc cref="NetworkConfigParameter.maxMessageSize"/>
        [Tooltip("Maximum size of a packet that can be sent by the transport.\n\nNote that this size includes any headers that could be added by the transport (e. g. headers for DTLS or pipelines), which means the actual maximum message size that can be sent by a user is slightly less than this value.\n\nTo find out what the size of these headers is, use MaxHeaderSize(NetworkPipeline).\n\nIt is possible to send messages larger than that by sending them through a pipeline with a FragmentationPipelineStage. These headers do not include those added by the OS network stack (like UDP or IP).")]
        [Range(64, NetworkParameterConstants.AbsoluteMaxMessageSize)]
        public int MaxMessageSize;

        internal NetCodeConfig()
        {
            // Note that these will be clobbered by any ScriptableObject in-place deserialization.
            Reset();
        }

        /// <summary>Setup default values.</summary>
        public void Reset()
        {
            ClientServerTickRate = default;
            ClientServerTickRate.ResolveDefaults();
            ClientServerTickRate.NetworkTickRate = 0; // Special case: For the config, let this be "dynamic" i.e. zero.

            ClientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            GhostSendSystemData = default;
            GhostSendSystemData.Initialize();

            ResetIfDefault(ref ConnectTimeoutMS, NetworkParameterConstants.ConnectTimeoutMS);
            ResetIfDefault(ref MaxConnectAttempts, NetworkParameterConstants.MaxConnectAttempts);
            ResetIfDefault(ref DisconnectTimeoutMS, NetworkParameterConstants.DisconnectTimeoutMS);
            ResetIfDefault(ref HeartbeatTimeoutMS, NetworkParameterConstants.HeartbeatTimeoutMS);
            ResetIfDefault(ref ReconnectionTimeoutMS, NetworkParameterConstants.ReconnectionTimeoutMS);
            ResetIfDefault(ref ClientReceiveQueueCapacity, 64);
            ResetIfDefault(ref ClientSendQueueCapacity, 64);
            ResetIfDefault(ref ServerReceiveQueueCapacity, NetworkParameterConstants.ReceiveQueueCapacity);
            ResetIfDefault(ref ServerSendQueueCapacity, NetworkParameterConstants.SendQueueCapacity);
            ResetIfDefault(ref MaxMessageSize, NetworkParameterConstants.MaxMessageSize);

            static void ResetIfDefault<T>(ref T value, T defaultValue)
                where T : IEquatable<T>
            {
                if (value.Equals(default))
                    value = defaultValue;
            }
        }

        /// <summary>
        ///     Fetch the existing NetCodeConfig (from Resources), or, if not found, create one.
        /// </summary>
        /// <remarks><see cref="RuntimeInitializeLoadType.AfterAssembliesLoaded"/> guarantees that this is called BEFORE Entities initialization.</remarks>
        /// <returns></returns>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        internal static void RuntimeTryFindSettings()
        {
            if (Application.isEditor)
            {
                void OnQuit()
                {
                    Application.quitting -= OnQuit;
                    Global = default; // resetting for convenience, to make sure we don't carry over settings with no domain reloads. Normally this should get reset next time we enter playmode, but that doesn't happen for editor tests when running them after having tested a project which changes settings at runtime
                }

                Application.quitting += OnQuit;
            }

            var configs = Resources.FindObjectsOfTypeAll<NetCodeConfig>();
            Array.Sort(configs);
            if (configs.Length > 0)
            {
                NetCodeConfig erringConfig = default;
                var errSb = new StringBuilder($"[NetCodeConfig] Discovered {configs.Length} loaded NetcodeConfig files. Using '{configs[0].name}', but the following errors occured:");
                bool isUsingGlobalConfig = false;
                for (var i = 0; i < configs.Length; i++)
                {
                    var config = configs[i];
                    errSb.Append($"\n[{i}] '{config.name}' (global: {config.IsGlobalConfig})");
                    if (i != 0 && config.IsGlobalConfig && isUsingGlobalConfig)
                    {
                        erringConfig = config;
                        errSb.Append($"\t <-- Expected this NOT to have IsGlobalConfig set!");
                    }
                    isUsingGlobalConfig |= config.IsGlobalConfig;
                }

                if (erringConfig)
                {
                    errSb.Append("\nImplies an error during ProjectSettings selection! Please open the ProjectSettings and re-apply the NetCodeConfig!");
                    Debug.LogError(errSb, erringConfig); // Support the ping, allowing quick-jump to error.
                }
            }
            // It is valid to NOT have a Global config, but to have multiple NetCodeConfigs in your build.
            Global = configs.Length > 0 ? configs[0] : null;
        }

        /// <summary>
        ///     Makes Find deterministic.
        /// </summary>
        /// <param name="other">Instance of <see cref="NetCodeConfig"/></param>
        /// <returns>Whether the config and names match.</returns>
        public int CompareTo(NetCodeConfig other)
        {
            if (IsGlobalConfig != other.IsGlobalConfig)
                return -IsGlobalConfig.CompareTo(other.IsGlobalConfig);
            return string.Compare(name, other.name, StringComparison.Ordinal);
        }
    }
}
