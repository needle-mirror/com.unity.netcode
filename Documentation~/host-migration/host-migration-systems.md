# Host migration systems and data

Set up host migration systems in your project to enable host migration in a client hosted networking experience.

Netcode for Entities contains [APIs](host-migration-api.md) for gathering host migration data into a buffer, which can then be used with the Multiplayer Services SDK to upload to the host migration service, and for deploying this data to a new server after a migration.

Use the information on this page in conjunction with [Lobby and Relay integrations](lobby-relay-integrations.md) to set up host migration in your project.

## Start the host migration systems

To enable host migration, create an `EnableHostMigration` singleton component in the server world. A default `HostMigrationConfig` is created, which has [configuration options](host-migration-api.md#hostmigrationconfig-component-options) for host migration, including how frequently the [host migration data](host-migration-intro.md#host-migration-data) should be automatically gathered.

```
var serverWorld = ClientServerBootstrap.ServerWorld;
serverWorld.EntityManager.CreateEntity(ComponentType.ReadOnly<EnableHostMigration>());
```

## Get host migration data for uploading

Every time the host migration data is updated, the timestamp in the `HostMigrationStats` singleton component is also updated (`LastDataUpdateTime`). You can use this to automate the upload of host migration data to the lobby on a regular basis, as in the code example below.

```
var uploadData = new NativeList<byte>(Allocator.Temp);
if (SystemAPI.TryGetSingleton<HostMigrationStats>(out var stats) && stats.LastDataUpdateTime > m_LastUpdateTime)
{
    HostMigration.GetHostMigrationData(ref uploadData);
    var uploadArray = uploadData.AsArray().ToArray();
    LobbyService.Instance.UploadMigrationDataAsync(m_MigrationConfig, uploadArray, new LobbyUploadMigrationDataOptions());
}
```

## Deploy host migration data to a new server

When a host migration event occurs, the client that's becoming the new host needs to download the host migration data and deploy it to a new server world. This world then needs to take over hosting responsibilities in the lobby. A helper function for this is shown here, which you can use to create a new server world with the provided driver constructor (which contains relay information) and migrate the provided migration data into this world. Then finally start listening and connect the local client world to this new server instance.

```
var migrationData = await LobbyService.Instance.DownloadMigrationDataAsync(m_MigrationConfig, new LobbyDownloadMigrationDataOptions());

var allocation = await RelayService.Instance.CreateAllocationAsync(10);
var hostRelayData = allocation.ToRelayServerData("dtls");
var driverConstructor = new HostMigrationDriverConstructor(hostRelayData, new RelayServerData());

var arrayData = new NativeArray<byte>(migrationData.Data.Length, Allocator.Temp);
var slice = new NativeSlice<byte>(arrayData);
slice.CopyFrom(migrationData.Data);

if (!HostMigration.MigrateDataToNewServerWorld(driverConstructor, ref arrayData))
{
    Debug.LogError($"Host migration failed while migrating data to new server world");
}
```

How worlds are disposed and created can vary from project to project. In this case, the server world can be created from scratch because there is no server world already present on the client (where this will be executing). This automatically switches the client from a relay connection to a local connection to the local server world just created.

```
public static bool MigrateDataToNewServerWorld(INetworkStreamDriverConstructor driverConstructor, ref NativeArray<byte> migrationData)
{
    var oldConstructor = NetworkStreamReceiveSystem.DriverConstructor;
    NetworkStreamReceiveSystem.DriverConstructor = driverConstructor;
    var serverWorld = ClientServerBootstrap.CreateServerWorld("ServerWorld");
    NetworkStreamReceiveSystem.DriverConstructor = oldConstructor;

    if (migrationData.Length == 0)
        Debug.LogWarning($"No host migration data given during host migration, no data will be deployed.");
    else
        HostMigrationUtility.SetHostMigrationData(serverWorld, migrationData);

    using var serverDriverQuery = serverWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
    var serverDriver = serverDriverQuery.GetSingletonRW<NetworkStreamDriver>();
    if (!serverDriver.ValueRW.Listen(NetworkEndpoint.AnyIpv4))
    {
        Debug.LogError($"NetworkStreamDriver.Listen() failed");
        return false;
    }

    var ipcPort = serverDriver.ValueRW.GetLocalEndPoint(serverDriver.ValueRW.DriverStore.FirstDriver).Port;

    // The client driver needs to be recreated, and then directly connected to new server world via IPC
    return ConfigureClientAndConnect(ClientServerBootstrap.ClientWorld, driverConstructor, NetworkEndpoint.LoopbackIpv4.WithPort(ipcPort));
}

```

## Connect clients to the new host

After a host migration event every client needs to connect to the new host via the relay server (the client that just became the host will connect to the local server directly). To do this, each client needs to recreate the network driver in the client world to set up the new relay allocation from the new host. A helper function for reconfiguring the network driver in the client world is shown here.

```
var allocation = await RelayService.Instance.JoinAllocationAsync(newJoinCode);
var relayData = allocation.ToRelayServerData("dtls");
var driverConstructor = new HostMigrationDriverConstructor(new RelayServerData(), relayData);
HostMigration.ConfigureClientAndConnect(ClientServerBootstrap.ClientWorld, driverConstructor, relayData.Endpoint);
```

Clients whose role isn't changing (they are remaining as clients instead of becoming the host) can reuse their existing client worlds, but need to connect to the new server by recreating the client network driver with the new allocation information.

```
public static bool ConfigureClientAndConnect(World clientWorld, INetworkStreamDriverConstructor driverConstructor, NetworkEndpoint serverEndpoint)
{
    if (clientWorld == null || !clientWorld.IsCreated)
    {
        Debug.LogError("HostMigration.ConfigureClientAndConnect: Invalid client world provided");
        return false;
    }

    using var clientNetDebugQuery = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetDebug>());
    var clientNetDebug = clientNetDebugQuery.GetSingleton<NetDebug>();
    var clientDriverStore = new NetworkDriverStore();
    driverConstructor.CreateClientDriver(clientWorld, ref clientDriverStore, clientNetDebug);
    using var clientDriverQuery = clientWorld.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
    var clientDriver = clientDriverQuery.GetSingleton<NetworkStreamDriver>();
    clientDriver.ResetDriverStore(clientWorld.Unmanaged, ref clientDriverStore);

    var connectionEntity = clientDriver.Connect(clientWorld.EntityManager, serverEndpoint);
    if (connectionEntity == Entity.Null)
        return false;
    return true;
}
```

## Additional resources

* [Host migration API and components](host-migration-api.md)
* [Lobby and Relay integrations](lobby-relay-integration.md)
