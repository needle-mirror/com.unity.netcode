using System;
using Unity.Assertions;
using Unity.Entities;
using UnityEngine;
using Unity.Collections;

namespace Unity.NetCode
{
    // Most of the code here helps with keeping up to date Netcode.Connection APIs and offer GameObject users way to interact with connection
    // information without having to write ECS queries for it.
    /// <summary>
    /// Connection event delegate. <see cref="Netcode.OnConnectionEvent"/> and <see cref="NetcodeWorld.OnConnectionEvent"/>.
    /// </summary>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    delegate void OnConnectionEventDelegate(Connection connection, NetCodeConnectionEvent connectionEvent);


    /// <summary>
    /// Abstraction to interact with an underlying connection.
    /// If offline, some of its fields will be invalid.
    /// </summary>
    // Design note: This connection wrapper abstracts queries and caches as little state as possible, to make sure it's always up to date.
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct Connection : IEquatable<Connection>
    {
        /// <summary>
        /// The world this connection belongs to. A server world will have multiple connections, one for each connected client. All those connections would have
        /// their world set to the server world, this is the world the connection lives in.
        /// </summary>
        public NetcodeWorld World;
        /// <summary>
        /// The entity associated with this connection. This is the entity that has the <see cref="NetworkId"/> component.
        /// </summary>
        public Entity ConnectionEntity { get; internal set; }

        /// <summary>
        /// The NetworkId associated with this connection. See <see cref="Unity.NetCode.NetworkId"/>
        /// </summary>
        public NetworkId NetworkId
        {
            get
            {
                var em = World.EntityManager;
                if (!em.HasComponent<NetworkId>(ConnectionEntity))
                    return NetworkId.Invalid;
                return em.GetComponentData<NetworkId>(ConnectionEntity);
            }
        }

        /// <summary>
        /// Current estimated Round Trip Time for this connection.
        /// </summary>
        public float RTT
        {
            get
            {
                if (World != null && World.IsCreated && World.EntityManager.HasComponent<NetworkSnapshotAck>(ConnectionEntity))
                {
                    var ackData = World.EntityManager.GetComponentData<NetworkSnapshotAck>(ConnectionEntity);
                    return ackData.EstimatedRTT;
                }
                return 0;
            }
        }

        // TODO-next@connection move this to core assembly
        // TODO-next@connection should be flag. See discussion https://github.cds.internal.unity3d.com/unity/dots/pull/9738#discussion_r486215
        // public enum ReplicationBehaviour
        // {
        //     /// <summary>
        //     /// Only RPCs will be synced. This is the default for new connections. <see cref="NetworkStreamInGame"/>
        //     /// </summary>
        //     RpcsOnly,
        //     /// <summary>
        //     /// All states like commands and ghosts will be synced in addition to RPCs
        //     /// </summary>
        //     StateReplication
        // }

        internal Connection(NetcodeWorld world, Entity connectionEntity)
        {
            World = world;
            ConnectionEntity = connectionEntity;
        }

        /// <summary>
        /// Dictates which replication behaviour to use for this connection. <see cref="NetworkStreamInGame"/>
        /// By default, replication is disabled for new connections. You need to enable it both client side on your connection and server side for the new connection from that client.
        /// TODO-next@breakingChange review doc once we have new behaviour for this, we should have this enabled by default
        /// </summary>
        /// <param name="enable"></param>
        // See TODO-next@connection above for ReplicationBehaviour enum
        // public void EnableStateReplication(ReplicationBehaviour behaviour)
        public void EnableGhostReplication(bool enable)
        {
            Assert.IsTrue(IsValid(), "Connection not initialized yet");
            if (enable)
            {
                World.EntityManager.AddComponentData(ConnectionEntity, default(NetworkStreamInGame));
                // Netcode.Instance.m_PrefabsRegistry.RetriggerPrefabEventsHack(m_World); // needed to retrigger the prefab events when we go back in game
            }
            else
            {
                World.EntityManager.RemoveComponent<NetworkStreamInGame>(ConnectionEntity);
            }
            // switch (behaviour)
            // {
            //     case ReplicationBehaviour.RpcsOnly:
            //         m_World.EntityManager.RemoveComponent<NetworkStreamInGame>(m_ConnectionEntity);
            //         break;
            //     case ReplicationBehaviour.StateReplication:
            //         m_World.EntityManager.AddComponentData(m_ConnectionEntity, default(NetworkStreamInGame));
            //         break;
            //     default:
            //         throw new NotImplementedException($"Unknown behaviour {behaviour}");
            // }
        }

        /// <summary>
        /// Whether this Connection struct is initialized and valid. See <see cref="GetConnectionState"/> for getting the state of a valid Connection
        /// </summary>
        /// <returns></returns>
        public bool IsValid()
        {
            return World != null && World.IsCreated && ConnectionEntity != Entity.Null && World.EntityManager.Exists(ConnectionEntity);
        }

        /// <summary>
        /// Returns the <see cref="ConnectionState.State"/> of the current connection
        /// </summary>
        /// <returns><see cref="ConnectionState.State.Unknown"/> if the current connection is invalid</returns>
        public ConnectionState.State GetConnectionState()
        {
            if (!IsValid()) return ConnectionState.State.Unknown;

            if (World.IsHost() && this.NetworkId.Value == World.LocalConnection.NetworkId.Value && World.Listening()) return ConnectionState.State.Connected;
            if (World.EntityManager.HasComponent<NetworkStreamConnection>(ConnectionEntity)) // OnDisconnect, this component is removed
            {
                return World.EntityManager.GetComponentData<NetworkStreamConnection>(ConnectionEntity).CurrentState;
            }
            if (World.EntityManager.HasComponent<ConnectionState>(ConnectionEntity))
            {
                return World.EntityManager.GetComponentData<ConnectionState>(ConnectionEntity).CurrentState;
            }

            return ConnectionState.State.Disconnected; // not returning unknown, as users shouldn't care about whether the driver is initialized or not. If there's no connection, we're disconnected
        }

        /// <summary>
        /// Async method to disconnect this connection. See <see cref="NetworkStreamRequestDisconnect"/>
        /// </summary>
        public void RequestDisconnect(NetworkStreamDisconnectReason reason = default)
        {
            if (!IsValid()) return;
            World.EntityManager.AddComponentData(ConnectionEntity, new NetworkStreamRequestDisconnect(){Reason = reason});
        }

        internal NetCodeConnectionEvent GenerateConnectedEvent()
        {
            return new NetCodeConnectionEvent()
            {
                ConnectionEntity = this.ConnectionEntity,
                ConnectionId =
                    this.World.EntityManager.GetComponentData<NetworkStreamConnection>(ConnectionEntity).Value,
                Id = this.NetworkId,
                State = this.GetConnectionState()
            };
        }

        public bool Equals(Connection other)
        {
            return NetworkId.Equals(other.NetworkId) && ConnectionEntity.Equals(other.ConnectionEntity);
        }

        public static bool operator ==(Connection left, Connection right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Connection left, Connection right)
        {
            return !left.Equals(right);
        }

        public override bool Equals(object obj)
        {
            return obj is Connection other && Equals(other);
        }

        public override int GetHashCode()
        {
            return this.NetworkId.Value;
        }

        public override string ToString()
        {
            return $"Connection {NetworkId.ToFixedString()}:{GetConnectionState()} in World:{World}";
        }
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(NetworkGroupCommandBufferSystem))] // to make sure we have the most up to date events for the tick (which are applied in that system)
    [CreateAfter(typeof(NetworkStreamReceiveSystem))]
    partial class ConnectionManagementUpdateConnections : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamDriver>();
            if (World.IsHost())
            {
                var netWorld = (NetcodeWorld)World;
                netWorld.LocalConnection = new Connection(netWorld, SystemAPI.GetSingletonEntity<LocalConnection>());
            }
        }

        protected override void OnUpdate()
        {
            var netWorld = (NetcodeWorld)World;
            var connectionEventsForTick = SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick;

            var hostNetworkId = new NetworkId();
            if (World.IsHost())
            {
                if (GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<NetworkId, LocalConnection>())
                    .TryGetSingleton<NetworkId>(out var networkId))
                {
                    hostNetworkId = networkId;
                }
            }

            foreach (var connectionEvent in connectionEventsForTick)
            {
                var con = new Connection(netWorld, connectionEntity: connectionEvent.ConnectionEntity);

                switch (connectionEvent.State)
                {
                    case ConnectionState.State.Connected:
                    {
                        if (this.World.IsServer())
                        {
                            netWorld.AllConnections.Add(con);
                            if (this.World.IsHost() && con.NetworkId == hostNetworkId) // hostNetworkId to make sure we don't touch thin clients
                            {
                                netWorld.LocalConnection = con;
                            }
                        }
                        else
                        {
                            netWorld.LocalConnection = con;
                        }

                        break;
                    }
                    case ConnectionState.State.Disconnected:
                    {
                        // TODO-next@connection store Disconnect reason somewhere for later access?
                        // Invoke callbacks before removing connection from list
                        if (World.IsServer())
                        {
                            netWorld.AllConnections.Remove(con);
                            if (World.IsHost() && con.NetworkId == hostNetworkId)
                            {
                                netWorld.LocalConnection = con;
                            }
                        }
                        else
                        {
                            netWorld.LocalConnection = con;
                        }

                        break;
                    }
                }

                var onAnyConnectionEvent = netWorld.GetCallbackToInvokeOnConnectionEvent();
                if (onAnyConnectionEvent == null || onAnyConnectionEvent.GetInvocationList().Length == 0)
                {
                    netWorld.NetDebug.DebugLog($"[{connectionEvent}]: No connection callback set in {nameof(NetcodeWorld)}.{nameof(NetcodeWorld.OnConnectionEvent)} or {nameof(Netcode)}.{nameof(Netcode.OnConnectionEvent)}."); // There's going to be other DebugLogs in bursted systems logging the actual events. This just augments those logs.
                }
                else
                {
                    foreach (var toInvoke in onAnyConnectionEvent.GetInvocationList())
                    {
                        try
                        {
                            toInvoke.Method.Invoke(toInvoke.Target, new object[] { new Connection(netWorld, connectionEvent.ConnectionEntity), connectionEvent });
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                }
            }
        }

        protected override void OnDestroy()
        {
            var netWorld = (NetcodeWorld)World;
            if (!netWorld.IsServer()) Assert.IsTrue(netWorld.AllConnections.Count == 0, "sanity check failed");

            // calling this manually since that won't be called if we dispose the world
            foreach (var connection in netWorld.AllConnections)
            {
                if (!connection.IsValid()) continue;

                var connectionEvent = new NetCodeConnectionEvent()
                {
                    ConnectionEntity = connection.ConnectionEntity,
                    Id = connection.NetworkId,
                    ConnectionId = this.EntityManager.GetComponentData<NetworkStreamConnection>(connection.ConnectionEntity).Value,
                    DisconnectReason = NetworkStreamDisconnectReason.ConnectionClose,
                    State = ConnectionState.State.Disconnected
                };
                var onAnyConnectionEvent = netWorld.GetCallbackToInvokeOnConnectionEvent();
                if (onAnyConnectionEvent == null || onAnyConnectionEvent.GetInvocationList().Length == 0)
                {
                    netWorld.NetDebug.DebugLog($"[{connectionEvent}]: No connection callback set in {nameof(NetcodeWorld)}.{nameof(NetcodeWorld.OnConnectionEvent)} or {nameof(Netcode)}.{nameof(Netcode.OnConnectionEvent)}."); // There's going to be other DebugLogs in bursted systems logging the actual events. This just augments those logs.
                }
                else
                {
                    try
                    {
                        netWorld.GetCallbackToInvokeOnConnectionEvent().Invoke(connection, connectionEvent);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }

        }
    }
}
