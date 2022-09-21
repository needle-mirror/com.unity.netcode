# Logging

NetCode comes with a builtin logging component so you can manipulate how much log information is printed. Currently it allows you to control either general NetCode log messages or ghost snapshot / packet logging separately.

## General NetCode logs

Normal log messages will be printed to the usual log destination Unity is using (i.e. console log and Editor.log when using the Editor. You can change the log level by setting `NetCodeDebugSystem.LogLevel`. The different log levels are:

* Debug
* Notify
* Warning
* Error
* Exception

The default log level is _Notify_ which has informational messages and higher importance (Notify/Warning/etc). You can set the log level to _Debug_ to get a lot of debug messages related to connection flow, ghost information and so on. This will be a lot of messages which will be most useful when debugging issues.

## Packet and ghost snapshot logging

You can also enable detailed log messages about ghost snapshots and how they're being written to the packets sent over the network. This will be very verbose information so should be used sparingly when debugging issues related to ghost replication. It can be enabled by adding a `EnablePacketLogging` component to the connection entity you want to debug.

For example, to add it to every connection established you would write this in a system:

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

Each connection will get its own log file, with the world name and connection ID in the name, placed in the default log directory _Logs_, in the current working directory.

## Default ways of enabling logging

Logging can also be easily manipulated in the _Playmode Tools Window_ after entering playmode in the editor and by adding the `NetCodeDebugConfigAuthoring` component to a game object in a SubScene. The default methods are mostly for convenience and besides allowing changes to the log level only have a toggle to dump packet logs for all connections or none. To debug specific connections code needs to be written for it depending on the use case.

## Packet log debug defines

By default the packet logging works in the editor and in development builds. The added logging code can affect performance even when logging is turned off and it's therefore disabled by default in release builds. It can be forced off by adding `NETCODE_NDEBUG` define to the project settings, in the _Scripting Define Symbols_ field, in the editor. To force it off in a player build it needs to be added with the _Player Scripting Defines_ component in the `BuildConfiguration` settings. It can be forced on via the `NETCODE_DEBUG` define.

## Note

It can happen that the `NetDebugConfigSystem` or the system which sets the log level yet had the chance to run before the first log message appears. In which case no logging will be written regardless of the configured log level.

See the [System Update Order](https://docs.unity3d.com/Packages/com.unity.entities@0.1/manual/system_update_order.html) ECS documentation page for more information about system ordering.

Packet log dumps will go into the same directory as the normal log file on desktop platforms (Win/Mac/Lin). On mobile (Android/iOS) platforms it will go into the persistent file location where the app will have write access. On Android it will be in _/Android/data/BUNDLE_IDENTIFIER/files_ and a file manager which can see these hidden files is needed to retrieve them. On iOS this will be in the app container at _/var/mobile/Containers/Data/Application/GUID/Documents_, which can be retrieved via the Xcode _Devices and Simulators_ window (select the app from the _Installed Apps_ list, click the three dots below and select _Download Container..._). These files will not be deleted automatically and will need to be cleaned up manually, they can grow very large so it's good to be aware of this.