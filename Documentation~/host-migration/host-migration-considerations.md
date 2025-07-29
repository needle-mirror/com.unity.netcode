# Project considerations for supporting host migration

When server data is migrated to a new server the normal flow for setting up ghosts and players / connections is different and might require certain consideration for making it work. Anything not migrated over will start from defaults after unity scenes have been loaded in the server world and the normal scene will be in whatever configuration it was on the client before he took over hosting duties. Reconnecting clients connecting might not need to initialize like new clients as their data might have been migrated, and so on.

## `NetworkStreamInGame` considerations

[`NetworkStreamInGame`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkStreamInGame.html) requires special consideration on the client. After a migration, the server creates existing and new connections in-game after all subscenes have been loaded. No ghosts are respawned before at least one connection is in-game (this is usually the local client that immediately connects after the local server starts). On the client side, however, it can be difficult to tell when it's safe to start streaming ghost snapshots and `NetworkStreamInGame` needs to be added manually. To treat re-connections differently, you can check if the connection on the client has the `NetworkStreamIsReconnected` component added. If there's no difference in the connection flow depending on re-connections or new connections then nothing needs to be done. For example, if the server is sending a `LoadLevel` style RPC to the client after which it will place itself in game.

## Migrated data can be invalid on the new server

If there are entity variables in ghost components or any data that won't have a valid reference when it's been moved between hosts, then these instances need to be fixed after a migration. This can be done by querying against particular components on ghost entities with the `IsMigrated` tag added to identify respawned ghosts. Some other data needs to added instead or in addition to this to help correct these variables to reflect the new host state. For example, the entity could be looked up via some other component data (ID or name but not ghost ID, see note below) used to identify it.

## Waiting until migration is completed

Certain systems could start running as soon as a new server world is created and start initializing variables which will then be overwritten or become invalid after the host migration data has been deployed in the world. For example, if a system is ensuring certain conditions are met (querying for certain entities), these conditions are met in the host migration data but might be enforced before the host migration data is ready. Thus things might be duplicated or otherwise invalid.

To ensure things settle properly after a host migration, these systems should check if there is a singleton `HostMigrationInProgress` component in the world. If this is present the system could early out until it has disappeared. This component is created immediately after world creation so this should ensure things are stable after the migration is deployed.

## Dealing with the client's player entity

When clients reconnect to a new host there might be special considerations for dealing with the player entity owned in the previous host session. Since the player is included in the host migration data, there's no need to spawn it again. As would normally happen when the client connects, possibly after some initial init/handshake with the server. There are multiple ways to deal with that, for example if the server saves information about the client on the  connection entity (like a tag component with `PlayerSpawned`) then this information will be migrated as well and seeing that tag could skip initialization of the player for that client. The `GhostOwner` component will then also be updated and inputs should work correctly without any intervention.

