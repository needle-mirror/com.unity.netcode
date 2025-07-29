# Limitations and known issues

Understand the limitations and known issues with host migration to implement it most effectively in your project.

## Limitations

* Host migration data is limited to 10 MiB per snapshot. This is not configurable.
* Host migration data is always uploaded and downloaded to and from US Central region.
* Host election is randomized. There's no ranking of candidate players.
* Migrating ghosts with child entities is not supported.
* WebGL platform is not supported at the moment

## Known issues

### Considerations for owned ghosts

Ghost IDs and connection Network IDs will not be identical on the new host compared to the old host. When the new host respawns ghosts they're being allocated fresh IDs from the new host ghost ID pool. The same goes for the Network IDs on connection: they will be assigned from 1 and onwards on the new host, and the ordering will likely be different than on the old host.

Because of this network ID mismatch the ghost owner needs to be updated every time a new connection arrives. Right after the migration all ghost owners are set to -1, then when a connection arrives which is a returning client the ghost owner value will be updated to reflect their current network ID value.

### Allocation ID not found errors

Sometimes during a host migration the new host will fail to establish a connection to the relay server. This will fail the host migration and the clients will be kept waiting for the new host to report the new relay join code to the lobby for them to connect to. If the host join code is reported before the relay connection is fully established, the clients may fail with "allocation ID not found" errors. If this happens often it may help to switch to the UDP connection type when setting up the relay data before connecting/listening.

### Entity scene loading behaviour

Entity scenes loaded on the host will be stored and then reloaded on the new host. This is a simple mechanism at the moment, if anything is destroyed in those entity scenes it will appear again on the new host as this isn’t tracked (also prespawned ghosts in those scenes).

### Crash in ServerHostMigrationSystem after migration

There could be random crashes in this system on newly elected hosts as they try to deploy the host migration data. Another host will be selected and should resume normally. Potentially burst related.

### Invalid ghosts on clients after migration

Sometimes after a migration there could be log entries like `Entity Unity.Entities.Entity is not a valid ghost (i.e. it is not a real 'replicated ghost', nor is it a 'predicted spawn' ghost). This can happen if you instantiate a ghost entity on the client manually (without marking it as a predicted spawn).` likely related to pre-spawned ghosts. Likely ghosts left over after a host migration on the client in an invalid state.

### Prespawned ghost instability

Sometimes after a migration there can be ghost synchronization errors affecting prespawned ghosts only, something like `Received a ghost (ID -2147483647 Entity(40:25)) with an invalid ghost type 5 (expected 6)`. Usually the prespawned ghost will then stop synchronization on the client where this error appeared, but other ghosts are unaffected.

## Additional resources

* [Host migration requirements](host-migration-requirements.md)
