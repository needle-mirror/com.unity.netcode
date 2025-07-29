# Lobby and Relay integration

Integrate with Unity Lobby and Unity Relay to enable host migration in Netcode for Entities.

The information on this page requires the use of the Multiplayer Services SDK (com.unity.services.multiplayer) package, as outlined in the [host migration requirements](host-migration-requirements.md).

## Configure Lobby settings on the Unity Cloud dashboard

Lobby settings for host migration must be balanced as to:
- reduce the delay before a host is declared missing (_Disconnect Host Migration Time_)
- allow enough time for the host migration to take place before remaining players are themselves ejected from the lobby for being disconnected (_Disconnect Removal Time_)

To configure Lobby for a project and environment:
* Visit [cloud.unity.com](https://cloud.unity.com/)
* Click on _Products_ in the sidebar
* Click on _Lobby_
* Click _Config _
* Ensure the correct Project is selected in the dropdown
* Select the desired Environment to view the configuration (eg. _production_)
* Click _Edit config_ to modify values

The following values are recommended as a starting point:
- Active Lifespan: 120 seconds
- Disconnect Removal Time: 60 seconds
- Disconnect Host Migration Time: 5 seconds

Refer to the [config documentation](https://docs.unity.com/ugs/manual/lobby/manual/config-options) for more details.
## Retrieve a player ID

The player ID can be retrieved from the `AuthenticationService` instance:

```
var currentPlayerId = AuthenticationService.Instance.PlayerId;
```

This ID is assigned to the player and remains constant after a host migration.

## Create a Relay allocation

The host creates the Relay allocation and specifies the maximum number of connections allowed (excluding the host).

```
const maxPlayers = 4;
const maxConnections = maxPlayers - 1;
var allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
var relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
```

The relay [join code](https://docs.unity.com/ugs/en-us/manual/relay/manual/join-codes) is used by connecting players.

## Create a lobby

The initial host should create a lobby with the relay join code as a `Data` property:

```
CreateLobbyOptions options = new CreateLobbyOptions();
options.Data = new Dictionary<string, DataObject>()
{
    {“relayHost”, new DataObject(DataObject.VisibilityOptions.Member,
        AuthenticationService.Instance.PlayerId)},
    {“relayJoinCode, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode)}
};
options.Player = new Player(id: AuthenticationService.Instance.PlayerId, allocationId: allocationId);
var lobby = await LobbyService.Instance.CreateLobbyAsync("name", maxPlayers, options);
```

`AllocationId` refers to the Relay allocation and is required when Relay is used.

## Join a lobby

Lobby supports multiple methods of joining:

* by join code
* by quick join
* by ID

When joining by ID, the ID is typically discovered by a [query](https://docs.unity.com/ugs/en-us/manual/lobby/manual/query-for-lobbies), which can filter and sort using any of the indexed properties (such as `Name`).

The following code example shows how to obtain a join code (to display it in-game, for example).

```
var code = lobby.LobbyCode;

and join using a typed-in code:

var lobby = await LobbyService.Instance.JoinLobbyByCodeAsync("CODE");
```

## Join a Relay allocation

When joining a lobby, the player should read the relay join code:

```
var relayJoinCodelobby.Data[“relayJoinCode”].Value;
```

Then join the relay allocation:

```
var allocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
```

After a host migration, non-host players should not immediately use the (now stale) relay join code and instead wait for a change to the `relayJoinCode` property. This can be done by simply ignoring update events until the `relayHost` property matches the lobby host.

## Configuring network drivers

Use the utility extension to convert allocations to the `RelayServerData` struct, specifying the desired connection type (dtls, udp, wss).

```
var connectionType = “dtls”;
var relayServerData = allocation.ToRelayServerData(connectionType);
```

This struct is used in the client/server driver construction and best handled using a custom `INetworkStreamDriverConstructor` implementation:

```
public class MyDriverConstructor : INetworkStreamDriverConstructor
{
    RelayServerData m_RelayClientData;
    RelayServerData m_RelayServerData;


    public MyDriverConstructor(RelayServerData serverData, RelayServerData clientData)
    {
      m_RelayServerData = serverData;
      m_RelayClientData = clientData;
    }


    public void CreateClientDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkClientSettings();
        if (ClientServerBootstrap.ServerWorld == null || !ClientServerBootstrap.ServerWorld.IsCreated)
            DefaultDriverBuilder.RegisterClientDriver(world, ref driverStore, netDebug, ref m_RelayClientData);
        else
            DefaultDriverBuilder.RegisterClientIpcDriver(world, ref driverStore, netDebug, settings);
    }


    public void CreateServerDriver(World world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        DefaultDriverBuilder.RegisterServerDriver(world, ref driverStore, netDebug, ref m_RelayServerData);
    }
}

var clientData = new RelayServerData();
var serverData = new RelayServerData();
NetworkStreamReceiveSystem.DriverConstructor = new MyDriverConstructor(serverData, clientData);
```

For the host, assign `allocation.ToRelayServerData` to `serverData`. For clients, assign it to `clientData`. This must be done before world creation.

## Heartbeat a lobby

It's the responsibility of the host to send heartbeat pings at least once every 30 seconds to keep the lobby active. Upon migration, the new host should take over this responsibility.

```
await LobbyService.Instance.SendHeartbeatPingAsync("lobbyId");
```

Inactive lobbies disappear from queries until reactivated. This prevents new members from discovering and joining an inactive lobby. After an hour of inactivity, lobbies are deleted permanently.

## Subscribe to events

This creates a real-time notification channel with lobby which informs the client of any lobby change such as a host change. It also establishes the live WebSocket connection whose termination serves as a disconnection signal:

```
var callbacks = new LobbyEventCallbacks();
callbacks.LobbyChanged += OnLobbyChanged
await LobbyService.Instance.SubscribeToLobbyEventsAsync("lobbyId", callbacks);
```

## Obtain host migration data

Uploading migration data is done in two steps. The first step is retrieving the info structure from lobby:

```
MigrationDataInfo info = await LobbyService.Instance.GetMigrationDataInfoAsync("lobbyId");
var expires = info.Expires; // Refresh before this deadline
```

This data is only valid for a few minutes and must be refreshed periodically. The expiration date is available from the `Expires` property.

### Upload host migration data

To upload migration data, pass the info structure and the byte array to the `UploadMigrationDataAsync` function:

```
byte[] data = new byte[1_024];
await LobbyService.Instance.UploadMigrationDataAsync(info, data);
```

This executea an HTTP PUT request using `UnityWebRequest`.

## Detect a host migration

The host migration is communicated via the general purpose `LobbyChanged` callback. The `ILobbyChanges` parameter indicates whether the changes contain a `HostId` change:

```
void OnLobbyChanged(ILobbyChanges changes)
{
  changes.ApplyToLobby(lobby);
  if (changes.HostId.Changed) {
    var newHostId = changes.HostId.Value;
  }
}
```

## Download migration data

To download migration data as the new host, obtain the host migration data info using the steps above, then pass the info structure to the `DownloadMigrationDataAsync` function:

```
byte[] data = await LobbyService.Instance.DownloadMigrationDataAsync(info);
```

This executes an HTTP GET request using `UnityWebRequest`.

## Force a host migration

The host of a session can voluntarily elect a new host without leaving the session:

```
await LobbyService.Instance.UpdateLobbyAsync(
  "lobbyId", new UpdateLobbyOptions() { HostId = "newHostId" });
```

The previous host remains in the session and is demoted to a regular player.

## Additional Relay considerations

Host migration doesn't change anything about the Relay integration. However, there are a few aspects to take into account upon a migration.

After a host migration, the entire Relay allocation and join sequence must be repeated. Every previously used Relay allocation ID and Relay join code is invalidated. In addition, there's no guarantee that the new Relay allocation will land on the same server (or even the same region).

### Quality of Service (QoS)

When the Relay region parameter for `CreateAllocationAsync` is null (the default), [QoS](https://docs.unity.com/ugs/en-us/manual/relay/manual/qos) measurements are performed to pick the closest region for the allocation. This adds some startup latency (up to 500ms). After a host migration, the new host should repeat the process.

Alternatively, the region can be saved (in memory or on the session), which will reduce latency upon host migration on the assumption that the previous region is still a reasonable choice for remaining players.

### Re-allocation and wait condition

When a host disconnects, its relay allocation and relay join code (distinct from the lobby join code) is terminated and cannot be reused. The new host must create a new relay allocation and obtain a new join code. The new join code should overwrite the previous one on the lobby.

This creates a race condition. Other players can be notified of the host change before this new relay join code is ready. In addition, the previous code is still present in the lobby, but should be ignored. To solve this race condition, the relay join code is placed alongside a property that identifies the player ID that created the allocation. Until this ID aligns with the lobby host ID, players should ignore the relay join code and not connect.

### Keeping `allocationId` field up to date

Each lobby member has an optional field to store its relay `allocationId`. It is important to set this field upon starting or joining a session as well as after a host migration, when every `allocationId` will change.

## Additional resources

* [Unity Lobby documentation](https://docs.unity.com/ugs/en-us/manual/lobby/manual/unity-lobby-service)
* [Unity Relay documentation](https://docs.unity.com/ugs/en-us/manual/relay/manual/introduction)
* [Host migration requirements](host-migration-requirements.md)
* [Host migration API and components](host-migration-api.md)
* [Host migration systems and data](host-migration-systems.md)
