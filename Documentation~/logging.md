# Logging

Netcode for Entities comes with a builtin logging component so you can manipulate how much log information is printed. Currently it allows you to control either general logging messages or ghost snapshot / packet logging separately.

## Generic Logging message and levels

Log messages will be printed to the usual log destination Unity is using (i.e. console log and Editor.log when using the Editor). <br/>
You can change the log level by setting `NetDebug.LogLevelType`. The different log levels are:

* Debug
* Notify
* Warning
* Error
* Exception

The default log level is _Notify_, which has informational messages and higher importance (Notify/Warning/etc). In case you want more details about connection flow, received ghosts etc, you can select the _Debug_ log level. 
This will emit more informative messages, which will be most useful when debugging issues.

## Packet dumps i.e. ghost snapshot logging

You can also enable detailed log messages - outputted to a file called a 'dump' - about ghost snapshots, describing how they're being written to the packets sent over the network. 
The 'packet dump' is quite verbose and expensive, and thus should be used sparingly (i.e. only when debugging issues related to ghost replication). 

The snapshot logging can be enabled by adding a `EnablePacketLogging` component to any/all connection entities you want to debug. One file will be created for each connection.

For example, to add it to *every* connection established, you would write this in a system:

```c#
protected override void OnUpdate()
{
    var cmdBuffer = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>().CreateCommandBuffer();
    Entities.WithNone<EnablePacketLogging>().ForEach((Entity entity, in NetworkStreamConnection conn) =>
    {
        cmdBuffer.AddComponent<EnablePacketLogging>(entity);
    }).Schedule();
}
```
Packet log dumps will go into the same directory as the normal log file on desktop platforms (Win/Mac/Lin). 
On mobile (Android/iOS) platforms, it will go into the persistent file location where the app will have write access. 
- On Android, the files are output to _/Android/data/BUNDLE_IDENTIFIER/files_ and a file manager which can see these hidden files is needed to retrieve them. 
- On iOS, the files are output to the app container at _/var/mobile/Containers/Data/Application/GUID/Documents_, which can be retrieved via the Xcode _Devices and Simulators_ window (select the app from the _Installed Apps_ list, click the three dots below and select _Download Container..._). <br/>
>[!NOTE]
> These packet dump files will not be deleted automatically, and will therefore need to be cleaned up manually. They can grow very large!

### Packet log debug defines - `NETCODE_DEBUG` and `NETCODE_NDEBUG`
By default, packet dump logging works in the editor, and in development builds, and uses the `NETCODE_DEBUG` define internally.

>[!NOTE]
> Nuances: We also use this define to guard additional debug functionality. For example: `PrefabDebugName`, `WarnAboutStaleRpcSystem` etc.
> Netcode's external browser tool - Net Debugger (Unity NetDbg) - can be enabled via `NETCODE_DEBUG`, but cannot be disabled via `NETCODE_NDEBUG`.
> Setting `LogLevel` for `NetDebug` (via `NetCodeDebugConfig`) doesn't affect netcode's `UnityEngine.Debug` logging at all, either. 

The added packet dump logging code can affect performance, even when logging is turned off, and it is therefore disabled by default in release builds.
It can be forced off by adding `NETCODE_NDEBUG` define to the project settings, in the _Scripting Define Symbols_ field, for the editor and development builds.
Similarly, adding `NETCODE_DEBUG` to your ProjectSettings will enable packet dump logging in Production builds.

### Netcode Debug, Info, Warning & Error Logs
The Netcode package endeavours to use `NetDebug` for all package logging, but there are exceptions (where historic limitations of `NetDebug` have led to the use of `UnityEngine.Debug` instead).
Both loggers are available in-editor, in development builds, and in production builds.
Both loggers ignore the `NETCODE_NDEBUG` define (with a few exceptions).
The `NetDebug` wrapper uses _com.unity.logging_ if available (it's an **optional** package), and doesn't output stack-traces. _See the `USING_UNITY_LOGGING` define, which is the fallback for built-in UnityEngine logging._

### Custom packet dump messages
You can write custom information to the `packet dump` via the `EnablePacketLogging.LogToPacket` method. However, be aware of the following:
- This custom code must be inside a `#if NETCODE_DEBUG` define.
- You must access the `EnablePacketLogging` struct as writable, to guarantee job safety (i.e. to ensure you don't write to the logs while they're being written by netcode itself. Logging uses a lock, but netcode writes multiple lines via multiple calls).

## Simple ways of enabling packet logging, and changing log levels
You can easily customise the logging level and enable packet dump by either:
- Using the _Playmode Tools Window_ after entering playmode in the editor.
- By adding the `NetCodeDebugConfigAuthoring` component to a game object in a subscene. 

To debug specific connections, user-code needs to be written, adding the `EnablePacketLogging` component to said connection entities.
