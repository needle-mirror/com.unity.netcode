# Logging

Netcode for Entities has a built-in logging component that enables you to modify how much log information is printed. You can control general logging messages and ghost snapshot/packet logging separately.

## Generic logging message and levels

Log messages are printed to whichever standard log destination Unity is using (either the console log or `Editor.log` when using the Editor). You can change the logging level by setting `NetDebug.LogLevelType`. The available logging levels are:

* Debug
* Notify
* Warning
* Error
* Exception

The default logging level is _Notify_, which includes informational messages and higher importance messages such as warnings and errors. If you want more details about connection flow and received ghosts, you can select the _Debug_ logging level, which provides more informative messages that are useful when debugging issues.

## Ghost snapshot logging (packet dumps)

You can also enable detailed log messages about ghost snapshots, describing how they're being written to the packets sent over the network.
Ghost snapshot logging is quite verbose and expensive, and should therefore be used sparingly (for example, when debugging issues related to ghost replication).

Ghost snapshot logging can be enabled by adding an `EnablePacketLogging` component to any connection entities you want to debug. One file will be created for each connection.

For example, to add it to *every* connection established, you would write this in a system:

```c#
[BurstCompile]
public void OnUpdate(ref SystemState state)
{
    state.EntityManager.AddComponent<EnablePacketLogging>(SystemAPI.QueryBuilder().WithAll<NetworkId>().WithNone<EnablePacketLogging>().Build());
}
```

Packet log dumps go into the same directory as the normal log file on desktop platforms (Windows, Mac, and Linux).
On mobile (Android or iOS) platforms, logs go into the persistent file location where the app has write access.

- On Android, log files are output to `/Android/data/BUNDLE_IDENTIFIER/files` and a file manager that can see hidden files is needed to retrieve them.
- On iOS, log files are output to the app container at `/var/mobile/Containers/Data/Application/GUID/Documents`, which can be retrieved via the Xcode **Devices and Simulators** window (select the app from the **Installed Apps** list, click the three dots below, and select **Download Container...**).

>[!NOTE]
>These log files aren't deleted automatically and therefore need to be cleaned up manually. They can grow very large.

### Packet logging debug defines

By default, packet logging works in the Editor and in development builds. The additional logging code can affect performance, even when logging is turned off, and is disabled by default in release builds.

It can be forced off by adding `NETCODE_NDEBUG` define to the project settings, in the _Scripting Define Symbols_ field, in the Editor. To force it off in a player build, `NETCODE_NDEBUG` needs to be added with the _Additional Scripting Defines_ in the _DOTS_ project settings.

>[!NOTE]
> Nuances: We also use this define to guard additional debug functionality. For example: `PrefabDebugName`, `WarnAboutStaleRpcSystem` etc.
> Netcode's external browser tool - Net Debugger (Unity NetDbg) - can be enabled via `NETCODE_DEBUG`, but cannot be disabled via `NETCODE_NDEBUG`.
> Setting `LogLevel` for `NetDebug` (via `NetCodeDebugConfig`) doesn't affect netcode's `UnityEngine.Debug` logging at all, either.

### Netcode debug, info, warning, and error logs

The Netcode package endeavours to use `NetDebug` for all package logging, but there are exceptions (where historic limitations of `NetDebug` have led to the use of `UnityEngine.Debug` instead).
Both loggers are available in the Editor, in development builds, and in production builds and both loggers ignore the `NETCODE_NDEBUG` define (with a few exceptions).
The `NetDebug` wrapper uses _com.unity.logging_ if available (it's an **optional** package), and doesn't output stack-traces. _See the `USING_UNITY_LOGGING` define, which is the fallback for built-in UnityEngine logging._

### Custom packet dump messages

You can write custom information to the packet dump using the `EnablePacketLogging.LogToPacket` method. However, be aware of the following:

- This custom code must be inside an `#if NETCODE_DEBUG` define.
- You must access the `EnablePacketLogging` struct as writable, to guarantee job safety and ensure you don't write to the logs while they're being written by Netcode for Entities itself. Logging uses a lock, but Netcode for Entities writes multiple lines via multiple calls.

## Simple ways of enabling packet logging and changing log levels

You can easily customize the logging level and enable packet dumping by either:

- Using the [**PlayMode Tools** window](playmode-tool.md) after entering Play Mode in the Editor.
- Adding the `NetCodeDebugConfigAuthoring` component to a game object in a subscene.

To debug specific connections, user-code needs to be written, adding the `EnablePacketLogging` component to said connection entities.

## Input (i.e. Command) Packet Dumps
Packet dumps include information about the current input commands being sent.
An example output on a client world dump:
```text
[CSS][ShipCommandData:15257441568649283849] Sent for inputTargetTick: 262 | Entity(1205:3) on GhostInst[type:0|id:191,st:56] | isAutoTarget:True
    | stableHash: 64 bits [8 bytes]
    | commandSize: 16 bits [2 bytes]
    | autoCommandTargetGhost: 64 bits [8 bytes]
    | numCommandsToSend(4): 5 bits
    [b]=[355|→-1 ↑-1] (tick: 32 bits [4 bytes]) (data: 8 bits)
    [1]=[354|→-1 ↑-1] (cb: 0) (tΔ: 2 bits)
    [2]=[353|→-1 ↑-1] (cb: 0) (tΔ: 2 bits)
    [3]=[352|→1 ↑-1] (cb: 1) (tΔ: 2 bits) (data: 6 bits)
    | payloadTicks: 38 bits [5 bytes]
    | payload: 10 bits [2 bytes]
    | changeBits: 3 bits
    | flush: 5 bits
    ---
    208 bits [26 bytes]
```
* `[CSS]` is the CommandSendSystem.
* `[ShipCommandData:15257441568649283849]` is the type name and hash of the input type.
* `Sent for inputTargetTick: 262 | Entity(1205:3) on GhostInst[type:0|id:191,st:56] | isAutoTarget:True` denotes details of the entity that this input was raised on.
* `stableHash` is the bits required to send the hash of the input type.
* `commandSize` is the size of the command payload.
* `autoCommandTargetGhost` is the size of sending the ghost.
* `numCommandsToSend(4)` is the size of the field used to send the count of commands included in this payload. In this case, 4 commands.
* `[b]` here refers to the **baseline** input. I.e. The most recent (a.k.a. newest) input raised on the client.
* `[1]` (and so on) denotes the index of the previous input sent in this packet. `[1]` denotes the previous input before the baseline, `[2]` denotes the input raised before that, and so on.
* `[355|→-1 ↑-1]` refers to `InputBufferData<YourInputTypeHere>`, with a tick value of `355`, and a user defined `ToFixedString` override returning `→-1 ↑-1`. In this case: NetCube's `CubeInput`.
* `[Invalid|...]` denotes that we don't have an input entry for the previous tick, which is only expected on game start. Arguably we could simply not send these `Invalid` inputs, but the `numCommandsToSend` has an expected value, so culling them may be misleading.
* You may see a `InputBufferData<>` prefix in your own logging, which denotes that the `ToFixedString` was invoked on an `InputBufferData<T>` rather than the underlying type.
* You may see `?ICD?` instead of input data, which denotes that your input struct has not overridden the optional `ToFixedString` method. Ensure your overload is burst compatible!
* `(tick: 32 bits [4 bytes])` refers to the cost of serializing the baseline tick. We send it uncompressed.
* `(data: 8 bits)` is the compressed size of the input struct itself. The first input is delta-compressed against `default(T)`, the latter input values are delta-compressed against their previous value.
* `(cb: 1)` denotes the "changeBit", which - when 1 - denotes that this input differs from the one above it. We send a single bit for the changeBit for each input. I.e. It's similar to the `composite=true` flag for components with GhostFields. We send `0` when the tick is `Invalid`, as we don't expect to read the input in this case anyway.
* `(tΔ: 2 bits)` denotes how many bits are needed to serialize the tick associated with this input (relative to the previous input). The common case is `-1`, `-2`, or `-3`, which all use 2 bits. If `-4` or worse, we use Huffman (using `assumedDeltaTick.Subtract(4)` as our baseline).
* `payloadTicks` denotes how many bits were used to send all input struct tick values.
* `payload` denotes how many bits were used to send all your compressed input structs.
* `changeBits` denotes how many bits were used to send the changeBits. Essentially: `numCommandsToSend - 1`.
* `flush` denotes the wasted bits required to align to the byte boundary size when `DataStreamWriter.Flush` gets called.
* `---  208 bits [26 bytes]` denotes the final size (excluding UTP + UDP headers).

Example output on the server world dump:
```text
[CRS][3480158943696179440] Received command packet from Entity(623:9) on GhostInst[type:??|id:191,st:56] targeting tick 355:
    | arrivalTick:353
    | margin:2
    [b]=[355|→-1 ↑-1]
    [1]=[354|→-1 ↑-1] (cb:0)
    [2]=[353|→-1 ↑-1] (cb:0)
    [3]=[352|→1 ↑-1] (cb:1) Late!
    ---
    26 bytes
```
* _See above for additional info. You can see how this Server dump mirrors the Client's dump, but with different meta-data._
* `[CRS]` is the CommandReceiveSystem.
* `Entity(623:9)` denotes the server's corresponding entity for a given ghost instance (which is why it's different).
* `GhostInst[type:??|id:191,st:56]` denotes the assumed GhostInst struct values. Type will always be unknown here, but it should otherwise match the clients one (only applicable if you're using `AutoCommandTarget`).
* `arrivalTick:353` denotes that this input arrived on `ServerTick` 353.
* `margin:2` denotes that this input arrived 2 ticks early (thanks to `TargetCommandSlack`).
* `Late!` on `[3]` denotes that - of the previous 3 inputs included in this packet - the oldest one (i.e. the 3rd previous) arrived too late to be processed. This is expected for historic inputs, but not for the latest 2, typically (it's configuration dependent).

> [!NOTE]
> Most of this input data is redundant, but we send it to ensure that packet loss and jitter are correctly recovered from, as losing inputs from the client causes significant mispredictions (and thus; significant corrections).
