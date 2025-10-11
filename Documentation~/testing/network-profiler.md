# Network Profiler tool

>[!NOTE]
> The Netcode for Entities Profiler is an experimental feature that's disabled by default.
> Refer to the [Requirements](#requirements) section for instructions on enabling it.

Use the Netcode for Entities Profiler to view detailed information about the network performance and behavior of your project.

The Profiler captures and visualizes networking data to help identify sources of high bandwidth consumption, analyze the cost of synchronizing [ghosts](../ghosts.md), and inspect data for client-side [prediction](../prediction.md) and [interpolation](../interpolation.md).

There are two modules for the [Unity Profiler](https://docs.unity3d.com/Manual/Profiler.html), one for the client world and one for the server world, which you can use to analyze performance from different perspectives.

## Requirements

To enable the Netcode for Entities Profiler:

1. Go to **Project Settings** > **Player** > **Other Settings**.
2. Find the **Scripting Define Symbols** field.
3. Add `NETCODE_PROFILER_ENABLED` to the list.

To use the client and server Profiler modules, your project must meet the following requirements:

* Unity version: Unity 6.0 or newer
* Netcode for Entities: `com.unity.netcode` package version 1.8.0 or newer

## Usage

Once enabled, you can access the Profiler modules from the Unity Profiler window (**Window** > **Analysis** > **Profiler**). The **Client World** and **Server World** modules are enabled by default and can also be toggled from the module dropdown menu. Press the **Record** button to begin capturing data during a play session.

The Netcode Profiler data is organized into three tabs:

* [**Frame Overview** tab](#frame-overview-tab)
* [**Snapshot Overview** tab](#snapshot-overview-tab)
* [**Prediction and Interpolation** tab](#prediction-and-interpolation-tab)

### Frame Overview tab

The **Frame Overview** tab provides a high-level summary of all network activity for the selected frame. Use this tab to get an initial overview of [snapshot](../ghost-snapshots.md#snapshots) and [command](../command-stream.md) data, and to identify frames with high bandwidth usage

Key metrics include total bandwidth usage, packet counts, and number of instances.

>[!NOTE]
> Not every frame will contain snapshot data depending on the network tick rate, the application’s frame rate, and the individual send rates of your ghosts.
> If the network update rate is lower than the frame rate then it's expected behavior to see empty frames in the Netcode for Entities Profiler.

### Snapshot Overview tab

The **Snapshot Overview** tab provides a detailed breakdown of the data contained within each network snapshot. It features a tree view that lists all synchronized ghost types and their components.

For each item, the following metrics are available:

* **Size:** The total data size for this item in the snapshot.
* **% of snapshot:** Indicates the relative data size of a selected item compared to the total size of the entire snapshot for that frame.
* **Instance Count:** The number of instances of this ghost type currently being synchronized.
* **Compression Efficiency:** A measure of how effectively the data was compressed, where higher is generally better. You can increase your efficiency using different compression options. Refer to the [Data Compression](../optimization/compression.md) page for more information.
* **Avg size / instance:** The average size of a single instance for a given ghost type or ghost component type.

You can use this tab to identify the ghost types and components that contribute most to network bandwidth usage.

#### Overhead

The **Overhead** item in the tree view represents the metadata that Netcode for Entities requires to manage and synchronize the snapshot data. This data isn't part of any user-defined component but is used internally to identify and synchronize ghosts.

Overhead is displayed at two levels: a global overhead for the entire tick, and a per-ghost type overhead. While this display can be toggled in the Profiler settings, the overhead is a fundamental and unavoidable part of the snapshot's data size. You can reduce this overhead by having fewer, bigger ghosts.

### Prediction and Interpolation tab

The **Prediction and Interpolation** tab's data is only available for the **Client World** module. It's a specialized view for debugging entities that use client-side prediction and interpolation. You can use it to diagnose issues related to character smoothness and input responsiveness by visualizing prediction errors.

## Best practices

* General workflow: Use the **Frame Overview** tab and the graph counters to find frames with high bandwidth usage. After identifying a frame of interest, switch to the **Snapshot Overview** tab to investigate the specific ghosts and components that contributed to the data usage in that frame.
* For optimization: In the **Snapshot Overview**, check the **Size** column and the **% of snapshot column** to identify the ghost types that contribute most to your bandwidth. Focus [optimization](../optimizations.md) efforts on these high-cost items.
* Testing network environments: For the most accurate data, use the [PlayMode Tool](../playmode-tool.md) window to run your application under representative network conditions, such as a client-hosted server with multiple clients and simulated latency.

## Known issues

### Exception with JetBrains Rider

An exception may be thrown when using the Profiler with JetBrains Rider.

**Solution:** This can be resolved by disabling the integration in Rider's settings. Instructions are provided in the official JetBrains documentation: [Disabling Unity Profiler Assistance in Rider](https://www.jetbrains.com/help/rider/Unity_Profiler_Assistance.html?utm_source=product#before-you-start).

## Additional resources

* [Unity Profiler documentation](https://docs.unity3d.com/Manual/Profiler.html)
