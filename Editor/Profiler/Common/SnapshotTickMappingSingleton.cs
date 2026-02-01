using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Unity.NetCode.Editor
{
    /// <summary>
    /// This singleton keeps track of the mapping between frame indices, server ticks and client ticks.
    /// It contains a mapping struct that serializes this information to persist it throughout domain reloads.
    /// The mapping is rebuild when playmode is paused or exited and when a new profiler capture is loaded.
    /// TODO: This could also be a regular singleton that rebuilds the map on every domain reload.
    /// TODO: If this ScriptableSingleton is extended in the future and/or the serialization logic gets more complex we should change it to an in-memory singleton.
    /// </summary>
    internal sealed class SnapshotTickMappingSingleton : ScriptableSingleton<SnapshotTickMappingSingleton>, ISerializationCallbackReceiver
    {
        [SerializeField]
        internal FrameToSnapshotTickMapping frameToSnapshotTickMapping;
        [SerializeField]
        bool initialized;
        [SerializeField]
        int firstMappedFrameIndex;
        [SerializeField]
        int lastMappedFrameIndex;

        internal void Initialize()
        {
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            ProfilerDriver.profileCleared -= OnProfilerCaptureCleared;
            ProfilerDriver.profileCleared += OnProfilerCaptureCleared;
            ProfilerDriver.profileLoaded -= OnProfilerCaptureLoaded;
            ProfilerDriver.profileLoaded += OnProfilerCaptureLoaded;

            if (initialized)
                return;

            frameToSnapshotTickMapping = new FrameToSnapshotTickMapping();
            frameToSnapshotTickMapping.Initialize();

            initialized = true;
        }

        void OnPlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            // Make sure to update the mapping when the play mode is exited
            if (playModeStateChange == PlayModeStateChange.EnteredEditMode)
                MapFramesToSnapshotTicks();
        }

        void OnPauseStateChanged(PauseState pauseState)
        {
            // Make sure to update the mapping when the editor is paused (i.e. when a frame in the profiler is selected)
            if (pauseState == PauseState.Paused)
                MapFramesToSnapshotTicks();
        }

        void OnProfilerCaptureCleared()
        {
            // Clears the map. Called when the profiler capture is cleared in the editor.
            frameToSnapshotTickMapping.Clear();
        }

        void OnProfilerCaptureLoaded()
        {
            MapFramesToSnapshotTicks();
        }

        /// <summary>
        /// Iterates through all captured profiler frames to build the mapping between frame indices and network ticks.
        /// Checks if an update is needed before rebuilding the mapping.
        /// </summary>
        void MapFramesToSnapshotTicks()
        {
            var firstFrameIndex = ProfilerDriver.firstFrameIndex;
            var lastFrameIndex = ProfilerDriver.lastFrameIndex;
            if (!frameToSnapshotTickMapping.NeedsUpdate(lastFrameIndex))
                return;

            firstMappedFrameIndex = -1;
            lastMappedFrameIndex = 0;

            // Iterate over all captured profiler frames
            for (var i = firstFrameIndex; i <= lastFrameIndex; i++)
            {
                using (var frameDataView = ProfilerDriver.GetRawFrameDataView(i, 0))
                {
                    if (frameDataView == null || frameDataView.valid == false)
                    {
                        continue;
                    }

                    lastMappedFrameIndex = i;
                    if (firstMappedFrameIndex == -1)
                        firstMappedFrameIndex = i;

                    var clientArray = frameDataView.GetFrameMetaData<NetworkTick>(ProfilerMetricsConstants.ClientGuid, ProfilerMetricsConstants.SnapshotTickTag);
                    var serverArray = frameDataView.GetFrameMetaData<NetworkTick>(ProfilerMetricsConstants.ServerGuid, ProfilerMetricsConstants.SnapshotTickTag);

                    var frameTickInfo = new FrameTickInfo();

                    if (clientArray.Length != 0)
                    {
                        var snapshotTickClient = clientArray[0];
                        if (snapshotTickClient.IsValid)
                        {
                            frameTickInfo.ClientTick = snapshotTickClient;
                            frameToSnapshotTickMapping.clientTickToFrame.TryAdd(snapshotTickClient, i);
                        }
                    }

                    if (serverArray.Length != 0)
                    {
                        var snapshotTickServer = serverArray[0];
                        if (snapshotTickServer.IsValid)
                        {
                            frameTickInfo.ServerTick = snapshotTickServer;
                            frameToSnapshotTickMapping.serverTickToFrame.TryAdd(snapshotTickServer, i);
                        }
                    }

                    frameToSnapshotTickMapping.frameToTickInfo.Add(i, frameTickInfo);
                }
            }
        }

        /// <summary>
        /// Given a server tick frame index, returns the corresponding client tick frame index.
        /// </summary>
        /// <param name="serverFrameIndex">The frame index of a server tick.</param>
        /// <returns>The frame index of a client tick that corresponds to the given server tick frame index.</returns>
        internal int GetClientTickFrameIndexFromServerTickFrameIndex(int serverFrameIndex)
        {
            MapFramesToSnapshotTicks();

            if (frameToSnapshotTickMapping.frameToTickInfo.TryGetValue(serverFrameIndex, out var frameTickInfo))
            {
                if (frameToSnapshotTickMapping.clientTickToFrame.TryGetValue(frameTickInfo.ServerTick, out var frameIndex))
                {
                    return frameIndex;
                }
            }
            return -1;
        }

        /// <summary>
        /// Given a client tick frame index, returns the corresponding server tick frame index.
        /// </summary>
        /// <param name="clientFrameIndex">The frame index of a client tick.</param>
        /// <returns>The frame index of a server tick that corresponds to the given client tick frame index.</returns>
        internal int GetServerTickFrameIndexFromClientTickFrameIndex(int clientFrameIndex)
        {
            MapFramesToSnapshotTicks();

            if (frameToSnapshotTickMapping.frameToTickInfo.TryGetValue(clientFrameIndex, out var frameTickInfo))
            {
                if (frameToSnapshotTickMapping.serverTickToFrame.TryGetValue(frameTickInfo.ClientTick, out var frameIndex))
                {
                    return frameIndex;
                }
            }
            return -1;
        }

        /// <summary>
        /// Returns the frame index for the adjacent tick in the specified direction for the given network role.
        /// </summary>
        /// <param name="networkRole">The network role to get the adjacent tick for (client or server).</param>
        /// <param name="currentFrameIndex">The frame index to find a neighbouring tick for.</param>
        /// <param name="direction">The direction in which to find the next tick. -1 corresponds to previous ticks, 1 corresponds to next ticks.</param>
        /// <returns>The frame index for an adjacent tick based on network role, current frame index and search direction.</returns>
        internal int GetFrameIndexForAdjacentTick(NetworkRole networkRole, int currentFrameIndex, int direction)
        {
            MapFramesToSnapshotTicks();

            // Limit the search range to the max number of captured frames.
            var maxSearchDistance = ProfilerDriver.lastFrameIndex - ProfilerDriver.firstFrameIndex;

            // Special handling for first and last frames
            if (currentFrameIndex == ProfilerDriver.firstFrameIndex && direction > 0)
            {
                // Find first tick after the first frame
                currentFrameIndex = firstMappedFrameIndex;
            }

            if (currentFrameIndex == ProfilerDriver.lastFrameIndex && direction < 0)
            {
                // Process the last frame in the mapped list
                currentFrameIndex = lastMappedFrameIndex;
            }

            if (!frameToSnapshotTickMapping.frameToTickInfo.TryGetValue(currentFrameIndex, out var frameTickInfo))
                return -1;

            var currentTick = networkRole == NetworkRole.Server ? frameTickInfo.ServerTick : frameTickInfo.ClientTick;
            if (currentTick == NetworkTick.Invalid)
            {
                // In this case there is no tick mapped for this frame because no snapshot was sent/received.
                // We need to find the next tick by searching the next/previous valid frame.
                for (var frameIndexOffset = 1; frameIndexOffset <= maxSearchDistance; frameIndexOffset++)
                {
                    var adjacentFrameIndex = currentFrameIndex + direction * frameIndexOffset;
                    if (frameToSnapshotTickMapping.frameToTickInfo.TryGetValue(adjacentFrameIndex, out var tickInfo))
                    {
                        var tick = networkRole == NetworkRole.Server ? tickInfo.ServerTick : tickInfo.ClientTick;
                        if (tick != NetworkTick.Invalid)
                            return adjacentFrameIndex;
                    }
                }
            }

            var tickToFrameDictionary = networkRole == NetworkRole.Server ? frameToSnapshotTickMapping.serverTickToFrame : frameToSnapshotTickMapping.clientTickToFrame;
            for (var tickOffset = 1; tickOffset <= maxSearchDistance; tickOffset++)
            {
                if (currentTick == NetworkTick.Invalid)
                    break;

                if (direction == -1)
                    currentTick.Decrement();
                else
                    currentTick.Increment();

                if (currentTick == NetworkTick.Invalid)
                    break;
                if (tickToFrameDictionary.TryGetValue(currentTick, out var frameIndex))
                    return frameIndex;
            }

            return -1;
        }

        internal bool FrameBelongsToTick(int frameIndex, NetworkRole networkRole, NetworkTick tick)
        {
            var dictionary = networkRole == NetworkRole.Client ? frameToSnapshotTickMapping.clientTickToFrame : frameToSnapshotTickMapping.serverTickToFrame;
            if (dictionary.TryGetValue(tick, out var frameForTick))
            {
                return frameForTick == frameIndex;
            }

            return false;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            frameToSnapshotTickMapping.OnBeforeSerialize();
        }
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            frameToSnapshotTickMapping.OnAfterDeserialize();
        }
    }

    /// <summary>
    /// Struct to hold the client and server tick information for a given frame.
    /// </summary>
    [Serializable]
    struct FrameTickInfo
    {
        internal NetworkTick ClientTick;
        internal NetworkTick ServerTick;
    }

    /// <summary>
    /// Struct to hold the mapping between frame indices and snapshot ticks for both client and server.
    /// </summary>
    [Serializable]
    struct FrameToSnapshotTickMapping
    {
        /// <summary>
        /// Struct to hold a mapping between a frame index and its corresponding tick information.
        /// </summary>
        [Serializable]
        internal struct FrameTickPair
        {
            internal int FrameIndex;
            internal FrameTickInfo TickInfo;
        }

        /// <summary>
        /// Struct to hold a mapping between a tick and its corresponding frame index.
        /// </summary>
        [Serializable]
        internal struct TickToFramePair
        {
            internal NetworkTick Tick;
            internal int FrameIndex;
        }

        [SerializeField]
        internal List<FrameTickPair> frameToTickInfoList;
        [SerializeField]
        internal List<TickToFramePair> clientTickToFrameList;
        [SerializeField]
        internal List<TickToFramePair> serverTickToFrameList;

        internal Dictionary<int, FrameTickInfo> frameToTickInfo;
        internal Dictionary<NetworkTick, int> clientTickToFrame;
        internal Dictionary<NetworkTick, int> serverTickToFrame;

        int m_LastMappedFrameIndex;

        internal void Initialize()
        {
            frameToTickInfo = new Dictionary<int, FrameTickInfo>();
            clientTickToFrame = new Dictionary<NetworkTick, int>();
            serverTickToFrame = new Dictionary<NetworkTick, int>();
            m_LastMappedFrameIndex = -1;

            frameToTickInfoList = new List<FrameTickPair>();
            clientTickToFrameList = new List<TickToFramePair>();
            serverTickToFrameList = new List<TickToFramePair>();
        }

        internal void Clear()
        {
            frameToTickInfo.Clear();
            clientTickToFrame.Clear();
            serverTickToFrame.Clear();

            frameToTickInfoList.Clear();
            clientTickToFrameList.Clear();
            serverTickToFrameList.Clear();
        }

        /// <summary>
        /// Determines whether the mapping needs to be updated based on the last mapped frame index.
        /// </summary>
        /// <param name="lastFrameIndex">>The last frame index from the profiler.</param>
        /// <returns>True if the mapping needs to be updated; otherwise, false.</returns>
        internal bool NeedsUpdate(int lastFrameIndex)
        {
            if (m_LastMappedFrameIndex != lastFrameIndex)
            {
                Clear();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Prepares the mapping data for serialization by converting dictionaries to lists.
        /// </summary>
        internal void OnBeforeSerialize()
        {
            frameToTickInfoList.Clear();
            foreach (var kvp in frameToTickInfo)
            {
                frameToTickInfoList.Add(new FrameTickPair { FrameIndex = kvp.Key, TickInfo = kvp.Value });
            }

            clientTickToFrameList.Clear();
            foreach (var kvp in clientTickToFrame)
            {
                clientTickToFrameList.Add(new TickToFramePair { Tick = kvp.Key, FrameIndex = kvp.Value });
            }

            serverTickToFrameList.Clear();
            foreach (var kvp in serverTickToFrame)
            {
                serverTickToFrameList.Add(new TickToFramePair { Tick = kvp.Key, FrameIndex = kvp.Value });
            }
        }

        /// <summary>
        /// Reconstructs the mapping data from serialized lists back into dictionaries.
        /// </summary>
        internal void OnAfterDeserialize()
        {
            frameToTickInfo = new Dictionary<int, FrameTickInfo>();
            foreach (var pair in frameToTickInfoList)
            {
                frameToTickInfo.Add(pair.FrameIndex, pair.TickInfo);
            }

            clientTickToFrame = new Dictionary<NetworkTick, int>();
            foreach (var pair in clientTickToFrameList)
            {
                clientTickToFrame.Add(pair.Tick, pair.FrameIndex);
            }

            serverTickToFrame = new Dictionary<NetworkTick, int>();
            foreach (var pair in serverTickToFrameList)
            {
                serverTickToFrame.Add(pair.Tick, pair.FrameIndex);
            }
        }
    }
}
