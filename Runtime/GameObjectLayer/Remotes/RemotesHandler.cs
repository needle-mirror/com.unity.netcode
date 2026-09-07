using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation,
     WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [CreateBefore(typeof(Internal_RemoteAutoInvokeSystemGroup))]
    [UpdateInGroup(typeof(Internal_RemoteAutoInvokeSystemGroup), OrderFirst = true)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    partial class RemotesAutoInvokeSystem : SystemBase
    {
        private EntityQuery m_Query;
        internal Dictionary<RemoteInvokeID, RemoteHandler.AutoHandleDelegate> m_InvokeFunctions;

        protected override void OnCreate()
        {
            m_Query = GetEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                .WithAll<RemoteAutoInvoke>());
            m_InvokeFunctions = new Dictionary<RemoteInvokeID, RemoteHandler.AutoHandleDelegate>();
            RequireForUpdate(m_Query);
        }

        protected override void OnUpdate()
        {
            foreach (var (autoInvoke, ent) in SystemAPI.Query<RefRO<RemoteAutoInvoke>>().WithEntityAccess())
            {
                if ( m_InvokeFunctions.TryGetValue(autoInvoke.ValueRO.invokeType, out var invokeFunction) )
                {
                    invokeFunction(EntityManager, ent);
                }
                else
                {
                    Debug.LogWarning($"Attempted to auto invoke Remote method with invokeType: {autoInvoke.ValueRO.invokeType}. To debug set unity.netcode.sourcegenerator.write_files_to_disk to 1 in Default.globalconfig and look in your projects Temp directory for 'RemoteSerializer.cs' files containing this number to match the name of the Remote.");
                }
            }
            EntityManager.DestroyEntity(m_Query);
        }
    }

    /// <summary>
    /// Main point of access to Remote APIs.
    /// </summary>
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#else
    internal
#endif // NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    class RemoteHandler
    {
        // When you call invoke with no arguments we send to all
        private List<NetcodeWorld> GetAllClientAndServerWorlds()
        {
            List<NetcodeWorld> worlds = new List<NetcodeWorld>(ClientServerBootstrap.ServerWorlds);

            foreach (var w in ClientServerBootstrap.ClientWorlds)
            {
                if (!worlds.Contains(w))
                {
                    worlds.Add(w);
                }
            }

            return worlds;
        }

        private List<Connection> GetWorldTargetConnections( NetcodeWorld world, Directionality direction)
        {
            List<Connection> connections = new List<Connection>();

            if (world.IsServer() && (direction == Directionality.ServerToClient || direction == Directionality.Undefined))
            {
                connections.AddRange(world.AllConnections);
                if (world.IsHost())
                {
                    connections.Add(world.LocalConnection);
                }
            }

            if (world.IsClient() && (direction == Directionality.ClientToServer || direction == Directionality.Undefined))
            {
                connections.Add(world.LocalConnection);
            }

            return connections;
        }

        private void Send<T>(T remote, Connection connection) where T : unmanaged, IRemote
        {
            var req = connection.World.EntityManager.CreateEntity(typeof(SendRpcCommandRequest), typeof(T));
            connection.World.EntityManager.SetComponentData(req, new SendRpcCommandRequest { TargetConnection = connection.ConnectionEntity });
            connection.World.EntityManager.SetComponentData(req, remote);
        }

        /// <summary>
        /// Call to send a remote of type T to the specified world, this will find the connection on that world
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Invoke.</typeparam>
        /// <param name="remote">The remote to send</param>
        /// <param name="target">The world to send the remote on all of that worlds connections</param>
        /// <param name="direction">The direction the remote is intended to be sent</param>
        public void Invoke<T>(T remote, NetcodeWorld target, Directionality direction) where T : unmanaged, IRemote
        {
            Invoke<T>(remote, GetWorldTargetConnections(target, direction));
        }

        /// <summary>
        /// Call to send a remote of type T to the specified worlds, this will find the connection on the worlds
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Invoke.</typeparam>
        /// <param name="remote">The remote to send</param>
        /// <param name="target">The worlds to send the remote on all of that worlds connections</param>
        /// /// <param name="direction">The direction the remote is intended to be sent</param>
        public void Invoke<T>(T remote, List<NetcodeWorld> targets, Directionality direction) where T : unmanaged, IRemote
        {
            foreach (var t in targets)
            {
                Invoke<T>(remote, t, direction);
            }
        }

        /// <summary>
        /// Call to send a remote of type T to the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Invoke.</typeparam>
        /// <param name="remote">The remote to send</param>
        /// <param name="target">The connection to send the remote on</param>
        public void Invoke<T>(T remote, Connection target) where T : unmanaged, IRemote
        {
            Send<T>(remote, target);
        }

        /// <summary>
        /// Call to send a remote of type T to the list of specified connections
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Invoke.</typeparam>
        /// <param name="remote">The remote to send</param>
        /// <param name="targets">The list of connections to send to</param>
        public void Invoke<T>(T remote, List<Connection> targets) where T : unmanaged, IRemote
        {
            foreach (var t in targets)
            {
                Invoke(remote, t);
            }
        }

        /// <summary>
        /// Call to get all the remotes of type T received in all connections on the client and server worlds since the last call of Query. This will clear the remotes list before adding to it.
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param>A list of remotes of type T to set to all the received remotes.</returns>
        public void Query<T>( List<T> remotes ) where T : unmanaged, IRemote
        {
            Query<T>(GetAllClientAndServerWorlds(), remotes);
        }

        /// <summary>
        /// Call to get all the remotes of type T received in all connections on the client and server worlds since the last call of Query.
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <returns>A list of all the received remotes.</returns>
        public List<T> Query<T>() where T : unmanaged, IRemote
        {
            List<T> remotes = new List<T>();
            Query<T>(remotes);
            return remotes;
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="worlds">The list of worlds to query for received remotes</param>
        /// <param name="remotes">A list of all the received remotes.</param>
        public void Query<T>(List<NetcodeWorld> worlds, List<T> remotes) where T : unmanaged, IRemote
        {
            foreach (var w in worlds)
            {
                Query<T>(w, remotes);
            }
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="worlds">The connection to query for received remotes</param>
        /// <returns>A list of all the received remotes.</returns>
        public List<T> Query<T>(List<NetcodeWorld> worlds) where T : unmanaged, IRemote
        {
            List<T> remotes = new List<T>();
            Query<T>(worlds, remotes);
            return remotes;
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="target">The connection to query for received remotes</param>
        /// <param name="remotes">A list of all the received remotes.</param>
        public void Query<T>(NetcodeWorld world, List<T> remotes) where T : unmanaged, IRemote
        {
            Query<T>(GetWorldTargetConnections(world, Directionality.Undefined), remotes);
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="world">The world to query for received remotes</param>
        /// <returns>A list of all the received remotes.</returns>
        public List<T> Query<T>(NetcodeWorld world) where T : unmanaged, IRemote
        {
            return Query<T>(GetWorldTargetConnections(world, Directionality.Undefined));
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connections
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="targets">The list of connections to query for received remotes</param>
        /// <param name="remotes">A list of all the received remotes.</param>
        public void Query<T>(List<Connection> targets, List<T> remotes) where T : unmanaged, IRemote
        {
            foreach (var t in targets)
            {
                Query<T>(t, remotes);
            }
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connections
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="targets">The list of connections to query for received remotes</param>
        /// <returns>A list of all the received remotes.</returns>
        public List<T> Query<T>(List<Connection> targets) where T : unmanaged, IRemote
        {
            List<T> remotes = new List<T>();
            Query<T>(targets, remotes);
            return remotes;
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="target">The connection to query for received remotes</param>
        /// <param name="remotes">A list of all the received remotes.</param>
        public void Query<T>(Connection target, List<T> remotes) where T : unmanaged, IRemote
        {
            if (target.World != null)
            {
                // We should revisit this query and rebuilding it all th time. Currently caching this will require some way to cache per world and per type being looked up
                // that could get messy so there might be a better way to go about this but it needs some thought.
                var query = target.World.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<T, ReceiveRpcCommandRequest>());
                var entites = query.ToEntityArray(Allocator.Temp);
                var receivedRemotes = query.ToComponentDataArray<T>(Allocator.Temp);

                foreach (var e in entites)
                {
                    var rpcCommand = target.World.EntityManager.GetComponentData<ReceiveRpcCommandRequest>(e);
                    if (rpcCommand.SourceConnection == target.ConnectionEntity)
                    {
                        remotes.Add(target.World.EntityManager.GetComponentData<T>(e));
                        target.World.EntityManager.DestroyEntity(e);
                    }
                }
            }
        }

        /// <summary>
        /// Call to get all the remotes of type T received on the specified connection
        /// </summary>
        /// <typeparam name="T">The type of the Remote to Query.</typeparam>
        /// <param name="target">The connection to query for received remotes</param>
        /// <returns>A list of all the received remotes.</returns>
        public List<T> Query<T>(Connection target) where T : unmanaged, IRemote
        {
            List<T> remotes = new List<T>();
            Query<T>(target, remotes);
            return remotes;
        }

        /// <summary>
        /// Used for calling a Remotes handle method.
        /// </summary>
        public delegate void AutoHandleDelegate(EntityManager em, Entity e);

        /// <summary>
        /// Reister a remotes handle method with its id so it can be automatically actioned when received
        /// </summary>
        /// <param name="id">The id of the remote</param>
        /// <param name="invokeDelegate">the method to invoke when a remote with this id is received</param>
        /// <param name="world">the world to be registered into</param>
        public void RegisterAutoInvokeType(RemoteInvokeID id, AutoHandleDelegate invokeDelegate, NetcodeWorld world)
        {
            world.GetExistingSystemManaged<RemotesAutoInvokeSystem>().m_InvokeFunctions.TryAdd(id, invokeDelegate);
        }

        /// <summary>
        /// Utility function to resolve a Gameobject from a world, entity id and spawnTick.
        /// </summary>
        /// <param name="world">The id of the remote</param>
        /// <param name="id">the id of the entity to find the atached gameobject for</param>
        /// <param name="spawnTick">the spawnTick of the entity to find the atached gameobject for</param>
        /// /// <returns>The gameobject if found, null otherwise</returns>
        public GameObject ResolveGameObject( NetcodeWorld world, int id, uint spawnTick )
        {
            // TODO: We need to cache this query and possibly others, since managing this cache isn't trivial (need to account for the same world being taken down and back up again)
            //       we might want some central place we do this since getting the SpawnedGhostEntityMap will be useful in a bunch of places and we can manage this internally in one place for the netcode package
            using var query = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp)
                    .WithAll<SpawnedGhostEntityMap>());
            if (query.CalculateEntityCount() > 0)
            {
                var ghostMap = world.EntityManager.GetComponentData<SpawnedGhostEntityMap>(query.GetSingletonEntity());
                var netTick = new NetworkTick { SerializedData = spawnTick };
                if (ghostMap.Value.TryGetValue(new SpawnedGhost { ghostId = id, spawnTick = netTick }, out var ghostEnt) && world.EntityManager.Exists(ghostEnt))
                {
                    return (GameObject)Resources.EntityIdToObject(world.EntityManager.GetComponentData<GhostGameObjectLink>(ghostEnt).AssociatedGameObject);
                }
            }

            return null;
        }
    }
}
