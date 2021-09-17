using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Debug = UnityEngine.Debug;

namespace Unity.NetCode
{
    public struct MigrationTicket : IComponentData
    {
        public int Value;
    }

    [DisableAutoCreation]
    [UpdateInWorld(TargetWorld.Default)]
    public partial class DriverMigrationSystem : SystemBase
    {
        public struct DriverState
        {
            public NetworkDriver Driver;
            public NetworkPipeline UnreliablePipeline;
            public NetworkPipeline ReliablePipeline;
            public NetworkPipeline UnreliableFragmentedPipeline;

            public bool Listening;
            public int NextId;
            public NativeArray<int> FreeList;
        }

        public struct WorldState
        {
            public DriverState DriverState;
            public World BackupWorld;
        }

        private Dictionary<int, WorldState> driverMap;
        private int m_TicketCounter;

        protected override void OnCreate()
        {
            driverMap = new Dictionary<int, WorldState>();
            m_TicketCounter = 0;
        }

        /// <summary>
        /// Stores NetworkDriver and Connection data for migration of a specific world.
        /// </summary>
        /// <param name="sourceWorld">The world we want to store.</param>
        /// <remarks>Only entities with the type `NetworkStreamConnection` are migrated over to the new World.</remarks>
        /// <returns>A ticket that can be used to retrieve the stored NetworkDriver data.</returns>
        public int StoreWorld(World sourceWorld)
        {
            var ticket = ++m_TicketCounter;

            if (driverMap.ContainsKey(ticket))
                throw new ApplicationException("Unhandled error state, the ticket already exists in driver map.");

            driverMap.Add(ticket, default);

            var system = sourceWorld.GetExistingSystem<NetworkStreamReceiveSystem>();
            system.StoreMigrationState(ticket);

            var filter = sourceWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
            var backupWorld = new World(sourceWorld.Name);

            backupWorld.EntityManager.MoveEntitiesFrom(sourceWorld.EntityManager, filter);

            var worldState = driverMap[ticket];
            worldState.BackupWorld = backupWorld;

            driverMap[ticket] = worldState;
            return ticket;
        }

        /// <summary>
        /// Loads a stored NetworkDriver and Connection data into a new or existing World.
        /// </summary>
        /// <param name="ticket">A ticket to a stored World</param>
        /// <param name="newWorld">An optional world we would want to Load into.</param>
        /// <returns>A prepared world that is ready to have its systems added.</returns>
        /// <remarks>This function needs to be called before any systems are initialized on the world we want to migrate to.</remarks>
        /// <exception cref="ArgumentException">Is thrown incase a invalid world is supplied. Only Netcode worlds work.</exception>
        public World LoadWorld(int ticket, World newWorld = null)
        {
            if (driverMap.TryGetValue(ticket, out var driver))
            {
                if (!driver.BackupWorld.IsCreated)
                    throw new ApplicationException("The driver contains no valid BackupWorld to migrate from.");

                if (newWorld == null)
                    newWorld = driver.BackupWorld;
                else
                {
                    Debug.Assert(null == newWorld.GetExistingSystem<NetworkStreamReceiveSystem>());

                    var filter = driver.BackupWorld.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
                    newWorld.EntityManager.MoveEntitiesFrom(driver.BackupWorld.EntityManager, filter);
                    driver.BackupWorld.Dispose();
                }

                var e = newWorld.EntityManager.CreateEntity();
                newWorld.EntityManager.AddComponentData(e, new MigrationTicket {Value = ticket});

                return newWorld;
            }
            throw new ArgumentException("You can only migrate a world created by netcode. Make sure you are creating your worlds correctly.");
        }

        internal DriverState Load(int ticket)
        {
            if (driverMap.TryGetValue(ticket, out var driver))
            {
                driverMap.Remove(ticket);
                return driver.DriverState;
            }
            throw new ArgumentException("You can only migrate a world created by netcode. Make sure you are creating your worlds correctly.");
        }

        internal void Store(DriverState state, int ticket)
        {
            Debug.Assert(driverMap.ContainsKey(ticket));
            var worldState = driverMap[ticket];

            worldState.DriverState = state;

            driverMap[ticket] = worldState;
        }


        protected override void OnDestroy()
        {
            foreach (var keyValue in driverMap)
            {
                var state = keyValue.Value;
                state.DriverState.Driver.Dispose();
                if (state.BackupWorld.IsCreated)
                    state.BackupWorld.Dispose();
                if (state.DriverState.FreeList.IsCreated)
                    state.DriverState.FreeList.Dispose();
            }
        }

        protected override void OnUpdate()
        {
        }
    }
}
