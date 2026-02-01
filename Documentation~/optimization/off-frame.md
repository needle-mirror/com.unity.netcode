# Execute expensive operations during off frames

Execute expensive operations during off frames to spread their impact and improve performance.

On client-hosted servers, your game can be set at a tick rate of 30Hz and a frame rate of 60Hz (if your [ClientServerTickRate.TargetFrameRateMode](https://docs.unity3d.com/Packages/com.unity.netcode@latest?subfolder=/api/Unity.NetCode.ClientServerTickRate.FrameRateMode.html) is set to `BusyWait`), so that the host executes two frames per tick. This makes your game less busy for one frame out of two, and you can use the less busy frame (referred to as the off frame) to execute extra operations. To find out whether a tick will execute during the frame, you can access the server world's rate manager.

> [!NOTE]
> A server world isn't idle during off frames and can time-slice its data sending to multiple connections if there's enough connections and enough off frames. For example, a server with ten connections can send data to five connections in one frame and the other five the next frame if its tick rate is low enough.

[!code-cs[blobs](../../Tests/Editor/DocCodeSamples/optimizations.cs#OffFrames)]
