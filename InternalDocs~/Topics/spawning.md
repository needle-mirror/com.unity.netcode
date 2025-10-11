# Ghost Spawn and Despawn

Replicated ghosts are spawned/despawned automatically by Netcode for Entities. Intances of `ghost prefabs` created on
the server are automatically replicated to the clients.

## Identify replicated ghosts.

Client and Server lives in differet worlds (same process) or even different machine/processes. To replicate the entities
it is first necessary to identify them univocally.

Netcode for Entities associate a GUID to each individal replicated ghost, and both server and client
maintain a mappings GUID -> Entity. Such mapping is used to replicate and identify references to replicated entities
between server and client.

Each ghost has unique identifier is 8 byte large and is composed by two pieces:

```
Ghost Guid:
{
    Id : can be reused, 4 bytes
    Tick : 4 bytes, never reused, alwasy increment. Can wrap at the uint boundary
}
```

The ghost guid itself can also be seen more generically as ID + Version

```
Ghost Guid:
{
    Id : 4 bytes, can be reused
    Version : 4 bytes, never reused, alwasy increment. Can wrap at the uint boundary
}
```

> Remark: the size of the ghost id is not optimal. For the case we are targeting:
> - mid-large world.
> - rarely millions of entities.
> - short to mid running sessions.
>
> we could have used less bits. The compression of the GhostId it is a secondary concern (architecture wyse) but
> it is indeed impacting the number of ghosts we can pack into each packet.


## Spawning and Relevancy

Creation and destruction of ghosts (spawn, despawn) and `relevancy changes` (relevant, irrelevant) are handled
the same way by the client. They are seen as the same thing.

| Server State          | New state  | Action  |
|-----------------------|------------|---------|
| Irrelevant            | Relevant   | Spawn   |
| Relevant              | Irrelevant | Despwn  |


On the server side, `relevancy changes` are distinct from creation and destruction of entities. However,
the relevancy mechanism is used to handle them and make many code path common.

| Server State | New state  | Action                                                  |
|--------------|------------|---------------------------------------------------------|
| Irrelevant   | Relevant   | Start sync ghost                                        |
| Relevant     | Irrelevant | Stop sync ghost, mark as despawned                      |
| Spawned      | Irrelevant | Assign id and cleanup. Wait for relevancy to handle it. |
| Despawn      | Irrelevant | Send depawn event if was sent to the client             |

## Spawning Client Side

Replicated ghosts are spawned/despawned automatically by Netcode for Entities and handled by the `GhostSpawnSystem`.

```
-`GhostSpawnSystemGroup`
---- `GhostSpawnSystem`

-`NetworkReceiveSystemGroup`
...
```

> Rationale: Why the `GhostSpawnSystem` execute before the next update of the `NetworkReceiveSystemGroup`?  To ensure that:
> - All ghosts are spawned and despawned before we read any new server data.
> - Consistent point in the frame were all ghosts are created.

The `GhostSpawnSystem` exposes a `GhostSpawnBuffer` that is used to queue the new ghost to spawn.
The spawning process itself is divided in three stages:

### Stage1: adding new ghost to spawn buffer

The `GhostReceiceSystem.ReadJob` decode the data received from the server. If the data received is for new ghost,
a new entry is appended to the `GhostSpawnBuffer`

### Stage2: Classification stage

The `GhostSpawnBuffer` is then inspected later to further `classify` and change the `spawning` mode based on:
- The Netcode default criteria: see `GhostSpawnClassificationSystem`.
- Any user specific criteria (custom made).

The `Default` critera are based on on the setting present in the `GhostAuthoringComponent`:

| mode          | spawn type           |
|---------------|----------------------|
| predicted     | predicted            |
| interpolated  | interpolated         |
| all           | Default ghost mode   |

After the first `criteria` pass, the ghost is classified usually has `Predicted` or `Interpolated`.
Further passes can be used to classify `Predicted Spawned Ghosts` (see guide fo that).

After the criteria and spawning type has been setup, the ghost is ready to be spawned.

### Stage3: spawning the ghosts

> Design decition: new ghosts are not spawned immediately in the same frame the data has has been received by the client`.
> All the spawning logic alwasy happen at the beginning of the SimulationSytemGroup update, inside the `GhostSpawnSystem`,
> that is the system responsible to consume the Interpolated and Predicted queue.

Ghosts are spawned on the client **at the same tick the server did**. Based on the spawn type the client target either
the interpolated or the predicted timeline.

| spawn type   | timeline                      | condition                                   |
|--------------|-------------------------------|---------------------------------------------|
| predicted    | NetworkTime.ServerTick        | NetworkTime.ServerTick >= spawn tick        |
| interpolated | NetworkTime.InterpolationTick | NetworkTime.InterpolatedTick >= spawn tick  |

In case the ghost can't be spawn immediately, the client **create a temporary "ghost placeholder"** entity, that is used to
hold any new incoming data from the server, but that it is not an instance of the ghost prefab. The creation of the real ghost instance is
then postponed until spawn condition is true.

The entity placeholder has at least the following components:

```csharp
GhostInstance
GhostPlaceholder
SnapshotBuffer
DynamicSnapshotBuffer
```

> Rationale 1: The existance of the placeholder is necessary because the client need to map an entity to the SpawnedGhost
> mapping. That mapping is used to discern if a ghost exist or not for the given id.
> Rationale 2: the placeholder also hold any new data received by the server until the client is ready to spawn the ghost.
> Because the client ack the server about the packet received and therefore data may be sent delta compressed, it is necessary to
> keep the all the received snapshots.

## Spawing Server Side

On the server we have two cases to distinguish:
- new ghost instance (i.e EntityManager.Instantiate)
- relevancy changes (or relevancy spawns): a ghost become relevant for a given connection.

### handling new ghost instances:

The same frame a new ghost is created, the GhostSendSystem detect the new chunk that has ghost instances.
The detection is done inside the `GhostSendSystem.SpawingGhostJob`.

The two most important pieces:
- A `GhostCleanup` is added to the entities to track their despawn via command buffer (next SimulationSystemGroup update).
- The `GhostInstanceId` is initialized: the `GhostId` and `Tick` are assigned.

> Design decision: the new ghost is not immediately sent. Only the ghosts that has the `GhostCleanup` are considered.

Once the ghost has been detected, it is up to the `Relevancy` to determine if the ghost can be sent or not. By default,
ghosts are marked as `Irrelevant` for all connection in this phase.

### Relevancy changes as spawn/despawn events

| Old State  | New State  | Client    |
|------------|------------|-----------|
| Irrelevant | Relevant   | New Spawn |
| Relevant   | Irrelevant | Despawn   |


## Spawning Timing

The replication of new spawned ghosts requires a certain amount of ticks. While this is technically a low level details,
architecturally it is important here to understand the timing from a Server and Client perspective.

The following snippet assume:
- Client/Server same process and IPC connection (0 latency)
- The replicateion best case scenario: new ghosts chunks can be sent immediately
- For extra simplification: each frame run both client and server and is 1:1 with ticks.

### Timing for new instances

```
Tick N
Server, Tick N:
  New Ghost Spawned on the server
  GhostSendSystem:
    - Detect the new ghost spawned
    - Add (command buffer) cleanup component to track the ghost existance
    - Newly spawned ghosts that doesn not have the `GhostCleanup` are never procesed nor sent to clients

Client, Tick X: Nothing received yet

Server, Tick N+1:
  GhostSendSystem:
    - Send the new spawned ghosts to the client

Client, Tick X+1:
  GhostReceiveSystem:
     Receive the new ghosts
     Queue into the GhostSpawnBuffer

  GhostDefaultClassificationSystem:
     Classify the spawn as either interpolated or predicted

Client: Tick X+2:
  GhostSpawnSystem:
    Predicted:
      - If ServerTick > spawnTick (for predicted) -> spawn immeditately (common case)
    Interpolated:
      - Delay spawn until InterpolationTick > spawnTick (common case). Usually InterpolationTick behind (defalt 2)

Client: Tick X+3:
  GhostSpawnSystem:
    - Spawn any pending interpolated ghosts (none)

Client: Tick X+4:
  GhostSpawnSystem:
    - Spawn any pending interpolated ghosts (the new ones)
```

- PredictedGhosts:
  - Server perspecive: at leat 2 ticks since the spawned on the Server
  - Client perpective: At least 1 tick since received by the Client

- Interpolated:
    - Server perspecive: at leat 4 ticks since the spawned on the Server.
    - Client perpective: At least 2 tick since received by the Client


### Timing for relevancy changes

```
Tick N
Server, Tick N:
  Ghost marked as releveant
  GhostSendSystem:
    - If the priority is high enough, send the new ghost to the client.

Client, Tick X:
  GhostReceiveSystem:
     Receive the new ghosts
     Queue into the GhostSpawnBuffer

  GhostDefaultClassificationSystem:
     Classify the spawn as either interpolated or predicted

Client: Tick X+1:
  GhostSpawnSystem:
    Predicted:
      - If ServerTick > spawnTick (for predicted) -> spawn immeditately (common case)
    Interpolated:
      - Delay spawn until InterpolationTick > spawnTick (common case). Usually InterpolationTick behind (defalt 2)

Client: Tick X+2:
  GhostSpawnSystem:
    - Spawn any pending interpolated ghosts (none)

Client: Tick X+3:
  GhostSpawnSystem:
    - Spawn any pending interpolated ghosts (the new ones)
```

- PredictedGhosts:
    - Server perspecive: at leat 1 ticks since the spawned on the Server.
    - Client perpective: at least 1 tick since received by the Client

- Interpolated:
    - Server perspecive: at leat 3 ticks since the spawned on the Server.
    - Client perpective: At least 2 tick since received by the Client


## Despawn

Client can't normally despawn replicated entities. There are some "exception" to that (i.e pre-spawned ghosts) but that
the rule of thumb to be respected. Only the Server has the authority to do so.

On the client, the destruction of the replicated entities is handled by the `GhostDespawnSystem`. In analogy with spawning,
ghosts are despawned that the same tick the server did.

The `GhostDespawnSystem` exposes two queues for that purpose:

| queue             | timeline                      | condition                                   |
|-------------------|-------------------------------|---------------------------------------------|
| PredictedQueue    | NetworkTime.ServerTick        | NetworkTime.ServerTick >= spawn tick        |
| InterpolatedQueue | NetworkTime.InterpolationTick | NetworkTime.InterpolatedTick >= spawn tick  |

When the condition for each queue is true, the client destroy the entity by using an EntityCommandBuffer. The real
entity destruction is therefore further delayed by 1 frame.

The despawning flow looks like this:

```
GhostReceivedSystem
 -> read despawn list
 -> enqueue ghost to despawn in either the interpolated or predicted queue
 -> remove the ghosts from the mappings (the ghost itself is seen as detroyed, even though the entity is still alive)

GhostDespawnSystem (later in the frame)
 -> Check the spawning queues, evaluate the conditions and in case destroy the entity via begin frame command buffer
...
...

Netxt Frame:
BeginFrameCommanBufferSystem -> Destroy all the scheduled entities.
```

## Despawning Server Side

The despawn is quite complex on the server side and requires some understanding.

> Reminder: The server detect a ghost is destroyed because of the`cleanup` component added when he detect the new spawn.
> As such, if by the time a ghost has been detected and cleanup added, it is destroyed, no despawn events are going to be sent
> to the clients.

From a very high level point of view, the server just send to the client a list of ghosts to depawn as part of the snapshot.

The list is sent reliably, using a custom reliability layer implemented by the GhostSendSystem; the server can send up to
100 ghosts to despawn events per snapshot, in a `round robin` fashion.

The reliability is implemented using a classic ARQ mechanism (automatic repeat) that exploit the fact the server and the client
are exchanging messages regularly at high frequency. Instead of waiting for an ack that depends on the RTT, the server prefer
to resend quite often the that partial list blocks every X ticks. The current repeat frequency is constant: every 5 ticks.

The despawn events/ghost to send for a given frame is populated from three different sources:

- Despawned ghosts
- Despawned pre-spawned ghosts
- Relevancy changes

The despawned ghosts (and despawns) are sent always first (higher priority). The relevancy changes (added to a "despawn" queue),
are handled after all the normal despawns has been sent or resent.

If a ghost has changed relevancy (become irrelevant) and it is then destroyed but now acked yet, it is in prioritize accordingly
to the rule above and resent as normal despawn (higher priority).

Both relevancy despawn and normal despawn are resent for a given connection until the client acks a packet that contains the
despawn information.

For normal despawn in particular, the server will not remove the `cleanup` until all connected client has acked that ghost despawn
(the check is performed by the `GhostDespawnParallelJob`). **That means these entities can linger longer than normal and
based on the network conditions and other external factor.**


### Despawn and relevancy changes
The server threat despawn the same as relevancy changes and relevancy changes as despawn. It send both to the client as
replicated entities to be destroyed.

When relevancy is enabled, and there is a change from `relevant` to `irrelevant`, the server updatte the internal state
of the ghost accordingly and queue a `despawn` event for that the current tick to be sent to the client.

Because the despawns are handled before the chunk relevancy changes detection, the ghost despawn events are sent always
the next frame. As such, there is at lest 1 frame delay from a relevancy despawn before it is reported to the client.
