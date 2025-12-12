using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.Jobs;
using static Unity.NetCode.NetCodeConfig.HostWorldMode;

namespace Unity.NetCode
{
    /// <summary>
    /// Main point of access for client side netcode configurations and operations.
    /// Provides shortcuts to its current <see cref="P:Unity.NetCode.Client.Connection" /> if online, its prefab registry, etc.
    /// A Client abstracts ClientServerBootstrap.ClientWorld and abstracts common operations on it.
    /// </summary>
    // The goal is to store as little state as possible here. That state should be stored ECS side in most cases.
    // One instance of each should be stored in the static Netcode class.
    // TODO-next Client and Server classes will potentially disappear with future refactors. They are here for ease of backporting reasons.
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    class Client
    {
        internal void Init()
        {
            if (ClientServerBootstrap.ClientWorld == null || !ClientServerBootstrap.ClientWorld.IsCreated)
                return;
            LazyCacheQueries(ClientServerBootstrap.ClientWorld);
        }

        private void LazyCacheQueries(World world)
        {
            if (m_NetworkStreamDriverQuery == default || m_NetworkTimeQuery == default)
            {
                m_NetworkTimeQuery = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<NetworkTime>());
                m_NetworkStreamDriverQuery = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAllRW<NetworkStreamDriver>());
            }
        }

        /// <summary>
        /// The connection to the server. The network ID and other values default to 0 when there is no connection.
        /// </summary>
        public Connection Connection { get; internal set; }

        // TODO-release rework with connection management
        // Some ideas https://github.cds.internal.unity3d.com/unity/dots/pull/9738#discussion_r482299
        // Should handle other players connecting
        // Could support burst handlers
        /// <summary>
        /// Event called when the local client is now connected to a server.
        /// </summary>
        public OnConnectDelegate OnConnect;

        /// <summary>
        /// Event called when the local client is now disconnected from a server.
        /// </summary>
        public OnDisconnectDelegate OnDisconnect;

        EntityQuery m_NetworkTimeQuery;
        EntityQuery m_NetworkStreamDriverQuery;
        internal NetworkTime NetworkTime => m_NetworkTimeQuery.GetSingleton<NetworkTime>();
        internal ref NetworkStreamDriver Driver => ref m_NetworkStreamDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW;

        internal Client()
        {
            Init();
        }

        internal bool HasServerConnection()
        {
            return Connection.GetConnectionState() >= ConnectionState.State.Connecting;
        }
    }

    /// <summary>
    /// Main point of access for server side netcode configurations and operations similar to <see cref="T:Unity.NetCode.Client" />.
    /// Provides shortcuts to its list of connections from clients, its prefab registry, etc.
    /// A server provides access to its associated ECS world and abstracts common operations to it.
    /// </summary>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    class Server // TODO-release needed? we sort of need to have per client/server things. like PrefabRegistry. Could we have a NetcodeWorld : World instead? Thing is, they need to live outside of worlds, so we can register prefabs when offline too.
    {
        internal void Init()
        {

            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return;

            LazyCacheQueries(ClientServerBootstrap.ServerWorld);
        }

        /// <summary>
        /// Check if the server world is set up and valid and has a driver instance listening.
        /// </summary>
        /// <returns>True if server is listening.</returns>
        public bool Listening()
        {
            var world = ClientServerBootstrap.ServerWorld;
            if (world == null || !world.IsCreated) return false;
            //failsafe if the Init method has been called yet. We can call also Init here lazily,
            //but then we are really losing control when things occurs.
            LazyCacheQueries(world);

            return Driver.DriverStore.HasListeningInterfaces;
        }

        private void LazyCacheQueries(World world)
        {
            if (m_NetworkStreamDriverQuery == default || m_NetworkTimeQuery == default)
            {

                m_NetworkTimeQuery = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<NetworkTime>());
                m_NetworkStreamDriverQuery = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAllRW<NetworkStreamDriver>());
            }
        }

        // TODO-release Instead of tracking connections this could just wrap to a query on the server world which checks for connections
        /// <summary>
        /// All connections from clients on this server. This is empty if no client is connected.
        /// </summary>
        public List<Connection> Connections { get; internal set; } = new();

        /// <summary>
        /// Event called when a client is now connected to this server.
        /// </summary>
        public OnConnectDelegate OnConnect;

        /// <summary>
        /// Event called when a client is now disconnected from this server.
        /// </summary>
        public OnDisconnectDelegate OnDisconnect;

        EntityQuery m_NetworkTimeQuery;
        EntityQuery m_NetworkStreamDriverQuery;
        internal NetworkTime NetworkTime => m_NetworkTimeQuery.GetSingleton<NetworkTime>();
        internal ref NetworkStreamDriver Driver => ref m_NetworkStreamDriverQuery.GetSingletonRW<NetworkStreamDriver>().ValueRW;

        internal Server()
        {
            Init();
        }
    }
}
