# Gather metrics

Gather metrics to monitor performance and identify potential issues in your Netcode for Entities project.

There are two ways to gather metrics in Netcode for Entities:

* Use the [Network Profiler tool](network-profiler.md) by selecting **Window** > **Multiplayer** > **Network Debugger** in the Editor. This provides you with a simple web interface to view metrics.
* Create a [`GhostMetricsMonitorComponent`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostMetricsMonitor.html) singleton and populate it with the data points you want to monitor. This allows you to access the metrics programmatically and use them in your project logic or for custom monitoring solutions.

The following example creates a singleton containing all available data metrics.

Adding the respective `IComponentData` to the singleton enables the collection of that metrics type.

[!code-cs[blobs](../../Tests/Editor/DocCodeSamples/metrics.cs#MetricsSystem)]

Use `SystemAPI.GetSingleton` to access data metrics for a specific metrics type. For example, to access the `NetworkMetrics`:

[!code-cs[blobs](../../Tests/Editor/DocCodeSamples/metrics.cs#GetNetworkMetrics)]

## Available metrics

| Component | Description |
| -------------- | ----------- |
| [`NetworkMetrics`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.NetworkMetrics.html) | Network and time related metrics. |
| [`SnapshotMetrics`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.SnapshotMetrics.html) | Snapshot related metrics. |
| [`GhostMetrics`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostMetrics.html) | Ghost related metrics indexed using `GhostNames`. |
| [`GhostSerializationMetrics`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostSerializationMetrics.html) | Ghost serialization metrics indexed using `GhostNames`. |
| [`PredictionErrorMetrics`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictionErrorMetrics.html) | Prediction errors indexed using `PredictionErrorNames`. |
| [`GhostNames`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.GhostNames.html) | A list of all available ghosts for this simulation. |
| [`PredictionErrorNames`](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.PredictionErrorNames.html) | A list of all available prediction errors for this simulation. |

