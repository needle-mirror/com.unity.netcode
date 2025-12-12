using System;
using Unity.Entities;
using UnityEngine;
using Unity.Collections;

// IMPORTANT DESIGN note
// A lot of this file will be refactored with future connection management work.

// Most of the code here helps with keeping up to date Netcode.Connection APIs and offer GameObject users way to interact with connection information without having to write
// ECS queries for it.
namespace Unity.NetCode
{
    /// <summary>
    /// OnConnect delegate. See <see cref="Client.OnConnect"/>
    /// </summary>
    // TODO-doc review me once we're established on Connection API
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    delegate void OnConnectDelegate(Connection connection, NetCodeConnectionEvent connectionEvent);
    /// <summary>
    /// OnDisconnect delegate. See <see cref="Client.OnDisconnect"/>
    /// </summary>
    // TODO-doc review me once we're established on Connection API
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    delegate void OnDisconnectDelegate(Connection connection, NetCodeConnectionEvent connectionEvent);

    // TODO-release rework me
    // TODO-release some ideas from reviews: https://github.cds.internal.unity3d.com/unity/dots/pull/9738#discussion_r482963
    // This connection wrapper could be simply abstracting queries and caching as little state as possible, to make sure it's always up to date.
    // Similar to what Client does
    // See Client's OnConnect for some other TODOs and ideas
    /// <summary>
    /// Abstraction to interact with an underlying connection.
    /// If offline, some of its fields will be invalid.
    /// </summary>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct Connection
    {
        internal World m_World;
        /// <summary>
        /// The entity associated with this connection. This is the entity that has the <see cref="NetworkStreamConnection"/> component.
        /// </summary>
        public Entity ConnectionEntity { get; internal set; }

        /// <summary>
        /// The NetworkId associated with this connection. See <see cref="Unity.NetCode.NetworkId"/>
        /// </summary>
        public NetworkId NetworkId;

        /// <summary>
        /// Current estimated Round Trip Time for this connection.
        /// </summary>
        public float RTT
        {
            get
            {
                if (m_World != null && m_World.IsCreated && m_World.EntityManager.HasComponent<NetworkSnapshotAck>(ConnectionEntity))
                {
                    var ackData = m_World.EntityManager.GetComponentData<NetworkSnapshotAck>(ConnectionEntity);
                    return ackData.EstimatedRTT;
                }
                return 0;
            }
        }

        /// <summary>
        /// Whether is this connection is associated with a server or client world.
        /// </summary>
        // todo-release this API is weird, should probably be something else
        // TODO-doc
        public bool IsServerRole => m_World.IsServer();

        // TODO-release move this to core assembly
        // TODO-release should be flag https://github.cds.internal.unity3d.com/unity/dots/pull/9738#discussion_r486215
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

        internal Connection(World world, Entity connectionEntity, NetworkId networkId)
        {
            m_World = world;
            ConnectionEntity = connectionEntity;
            NetworkId = networkId;
        }

        /// <summary>
        /// Dictates which replication behaviour to use for this connection. <see cref="NetworkStreamInGame"/>
        /// By default, replication is disabled for new connections. You need to enable it both client side on your connection and server side for the new connection from that client.
        /// TODO-release review doc once we have new behaviour for this, we should have this enabled by default
        /// </summary>
        /// <param name="enable"></param>
        // See todo-release above for ReplicationBehaviour enum
        // public void EnableStateReplication(ReplicationBehaviour behaviour)
        public void EnableGhostReplication(bool enable)
        {
            if (enable)
            {
                m_World.EntityManager.AddComponentData(ConnectionEntity, default(NetworkStreamInGame));
                // Netcode.Instance.m_PrefabsRegistry.RetriggerPrefabEventsHack(m_World); // needed to retrigger the prefab events when we go back in game
            }
            else
            {
                m_World.EntityManager.RemoveComponent<NetworkStreamInGame>(ConnectionEntity);
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
        /// Send a simple message over that connection
        /// See <see cref="Unity.NetCode.RpcCommandHandler"/> for more details on how to receive this message
        /// TODO-release review doc, this is most likely to be refactored completely. Review this
        /// </summary>
        /// <param name="message"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns>false if message failed to send</returns>
        public bool SendMessage<T>(T message) where T : unmanaged, IRpcCommand
        {
            if (GetConnectionState() != ConnectionState.State.Connected)
            {
                return false;
            }
            var req = m_World.EntityManager.CreateEntity(ComponentType.ReadWrite<SendRpcCommandRequest>(), ComponentType.ReadWrite<T>());
            m_World.EntityManager.SetComponentData(req, new SendRpcCommandRequest{TargetConnection = ConnectionEntity});
            m_World.EntityManager.SetComponentData(req, message);
            return true;
        }

        /// <summary>
        /// Whether is this Connection struct is initialized and valid or not. See <see cref="GetConnectionState"/> for getting the state of a valid Connection
        /// </summary>
        /// TODO-release doc review me once we're established on Connection API
        /// <returns></returns>
        public bool IsValid()
        {
            return m_World != null && m_World.IsCreated && ConnectionEntity != Entity.Null;
        }

        /// <summary>
        /// Returns the <see cref="ConnectionState.State"/> of the current connection
        /// </summary>
        /// <remarks>
        /// DOTS remark: GhostAdapter will add a <see cref="ConnectionState"/> component on your connection entity by default
        /// </remarks>
        /// <returns>Unknown if the current connection is invalid</returns>
        /// TODO-release doc review me once we're established on Connection API
        public ConnectionState.State GetConnectionState()
        {
            if (!IsValid()) return ConnectionState.State.Unknown;

            if (m_World.IsHost() && this.NetworkId.Value == Netcode.Client.Connection.NetworkId.Value && Netcode.Server.Listening()) return ConnectionState.State.Connected;
            if (m_World.EntityManager.HasComponent<NetworkStreamConnection>(ConnectionEntity)) // OnDisconnect, this component is removed
            {
                return m_World.EntityManager.GetComponentData<NetworkStreamConnection>(ConnectionEntity).CurrentState;
            }
            if (m_World.EntityManager.HasComponent<ConnectionState>(ConnectionEntity))
            {
                return m_World.EntityManager.GetComponentData<ConnectionState>(ConnectionEntity).CurrentState;
            }

            return ConnectionState.State.Disconnected; // not returning unknown, as users shouldn't care about whether the driver is initialized or not. If there's no connection, we're disconnected
        }

        /// <summary>
        /// Async method to disconnect this connection. See <see cref="NetworkStreamRequestDisconnect"/>
        /// </summary>
        /// TODO-next disconnect reason parameter
        public void RequestDisconnect()
        {
            if (!IsValid()) return;
            m_World.EntityManager.AddComponent<NetworkStreamRequestDisconnect>(ConnectionEntity);
        }
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(NetworkGroupCommandBufferSystem))] // to make sure we have the most up to date events for the tick (which are applied in that system)
    partial class ConnectionManagementUpdateConnections : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<NetworkStreamDriver>();
        }

        protected override void OnUpdate()
        {
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
                var con = new Connection(World, connectionEntity: connectionEvent.ConnectionEntity, connectionEvent.Id);

                switch (connectionEvent.State)
                {
                    case ConnectionState.State.Connected:
                    {
                        OnConnectDelegate toInvoke = null;

                        // TODO-release: If there are multiple worlds we need the world to know which Client/Server instance matches (add the world->Client mapping inside the world)
                        //       In case of thin clients there will be no callbacks registered?

                        if (this.World.IsServer())
                        {
                            Netcode.Server.Connections.Add(con);
                            toInvoke += Netcode.Server.OnConnect;
                            if (this.World.IsHost() && con.NetworkId == hostNetworkId) // hostNetworkId to make sure we don't touch thin clients
                            {
                                Netcode.Client.Connection = con;
                                toInvoke += Netcode.Client.OnConnect;
                            }
                        }
                        else
                        {
                            if (this.World != ClientServerBootstrap.ClientWorld) return; // TODO-release once we have multi world support in Netcode API, this is to handle thin clients (and secondary clients in tests), so that it doesn't override Netcode.Client. Should handle this for real though
                            Netcode.Client.Connection = con;
                            toInvoke = Netcode.Client.OnConnect;
                        }

                        try
                        {
                            toInvoke?.Invoke(new Connection { m_World = World, ConnectionEntity = connectionEvent.ConnectionEntity, NetworkId = connectionEvent.Id }, connectionEvent);
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }

                        break;
                    }
                    case ConnectionState.State.Disconnected:
                    {
                        OnDisconnectDelegate toInvoke = null;

                        // Invoke callbacks before removing connection from list
                        if (World.IsServer())
                        {
                            toInvoke = Netcode.Server.OnDisconnect;

                            // TODO-release: This could instead be another system which keep the Server.Connections list in sync with actual connections
                            Netcode.Server.Connections.Remove(con);
                            if (World.IsHost() && con.NetworkId == hostNetworkId) // hostNetworkId to make sure we don't touch thin clients
                            {
                                // TODO-release: Connection needs to be invalid here or maybe with a state variable set (could also contain disconnect reason)
                                Netcode.Client.Connection = con;
                                toInvoke += Netcode.Client.OnDisconnect;
                            }
                        }
                        else
                        {
                            // TODO-release: Connection needs to be invalid here or maybe with a state variable set (could also contain disconnect reason)
                            if (this.World != ClientServerBootstrap.ClientWorld) return; // TODO-release this is to handle thin clients (and secondary clients in tests), so that it doesn't override Netcode.Client. Should handle this for real though
                            Netcode.Client.Connection = con;
                            toInvoke += Netcode.Client.OnDisconnect;
                        }

                        try
                        {
                            toInvoke?.Invoke(new Connection { m_World = World, ConnectionEntity = connectionEvent.ConnectionEntity, NetworkId = connectionEvent.Id }, connectionEvent);
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                        }

                        break;
                    }
                }
            }
        }
    }
}
