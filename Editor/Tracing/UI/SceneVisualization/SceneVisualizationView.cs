using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode.Tracing;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI
{
    /// <summary>
    /// Renders 3D axis markers in the Scene View for each traced ghost/entity at the selected tick.
    /// Client-world ghosts use the predicted color, server-world ghosts use the authority color.
    /// When a ghost matches within the diff threshold, only the authority ghost is drawn. When it
    /// diverges, both the predicted and authority markers are drawn and a line in the diff color
    /// connects the two so the prediction error is visible at a glance.
    /// </summary>
    internal class SceneVisualizationView : DataObserverView
    {
        internal static Color PredictedColor = new Color(1f, 165f / 255f, 0f);
        internal static Color AuthorityColor = new Color(178f/255f, 178f/255f, 178f/255f);
        internal static Color DiffLineColor = new Color(1f, 36f / 255f, 0f);
        internal static bool IsEnabled = true;

        // Per-category visibility, toggled from the Scene View overlay. Gated under IsEnabled.
        internal static bool ShowPredicted = true;
        internal static bool ShowAuthority = true;
        internal static bool ShowDifference = true;

        const float k_AxisLengthFactor = 0.5f;
        const int k_PlaybackStepMs = 100;
        // A recorded frame delta this large can only come from the editor being suspended (pause, breakpoint,
        // domain reload)
        const float k_MaxRealFrameDeltaMs = 1000f;

        protected override string UssClassName => string.Empty;

        FrameID m_SelectedFrameID;
        TickID m_SelectedTickID;
        bool m_HasSelection;

        CancellationTokenSource m_PlaybackCts;
        Task m_PlaybackTask;
        FrameID? m_SeekFrame;

        FrameID m_ReplayCurrentFrameID;
        TickID m_ReplayCurrentTickID;
        bool m_HasReplayPosition;

        public bool IsPlaying { get; private set; }
        public ReplayRenderingFrequency PlaybackMode { get; set; }
        public float PlaybackSpeed { get; set; } = ReplaySpeed.Default;
        public event Action PlaybackStateChanged;
        public event Action<FrameID> ReplayFrameChanged;
        public event Action<FrameID, TickID> ReplayPausedAtSelection;

        readonly Dictionary<int, GhostMarkerData> m_ClientTransforms = new();
        readonly Dictionary<int, GhostMarkerData> m_ServerTransforms = new();

        readonly Dictionary<(FrameID frameID, TickID tickID), ManagedTickData> m_ClientTickCache = new();
        readonly Dictionary<TickID, ManagedTickData> m_ServerTickCache = new();

        struct GhostMarkerData
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public string Name;
            public bool HasDiff;
        }

        public SceneVisualizationView()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        public override VisualElement Create() => m_Root;

        public override Task OnDataAvailable(TracingData data)
        {
            // Stop any in-flight replay before swapping data, otherwise RunPlayback keeps iterating with the
            // old frameIDs against the new m_Data and can spin without yielding, freezing the Editor.
            CancelPlayback();
            m_Data = data;
            InvalidateTickCache();
            return Task.CompletedTask;
        }

        protected internal override Task OnSelectedFrameChanged(FrameID frameID)
        {
            m_SelectedFrameID = frameID;

            if (IsPlaying)
            {
                m_SeekFrame = frameID;
                return Task.CompletedTask;
            }

            if (!m_Data.ClientWorldData.IsCreated
                || !m_Data.ClientWorldData.PerFrameData.TryGetValue(frameID, out var frameData))
            {
                // Selected a frame with no data: drop the previous frame's markers instead of leaving them stale.
                ClearMarkers();
                return Task.CompletedTask;
            }

            var tickIDs = frameData.TickIDs;
            if (tickIDs.Length == 0)
            {
                ClearMarkers();
                return Task.CompletedTask;
            }

            m_SelectedTickID = tickIDs[0];
            m_HasSelection = true;
            RefreshTransforms();
            SceneView.RepaintAll();
            return Task.CompletedTask;
        }

        protected internal override Task OnSelectedTickChanged(TickID tickID)
        {
            if (tickID.Equals(default))
            {
                return Task.CompletedTask;
            }
            m_SelectedTickID = tickID;
            m_HasSelection = true;
            RefreshTransforms();
            SceneView.RepaintAll();
            return Task.CompletedTask;
        }

        void ClearMarkers()
        {
            m_HasSelection = false;
            m_ClientTransforms.Clear();
            m_ServerTransforms.Clear();
            SceneView.RepaintAll();
        }

        public override void Clear()
        {
            CancelPlayback();
            m_HasSelection = false;
            m_ClientTransforms.Clear();
            m_ServerTransforms.Clear();
            InvalidateTickCache();
            m_Data = default;
            SceneView.RepaintAll();
        }

        public override void Dispose()
        {
            CancelPlayback();
            SceneView.duringSceneGui -= OnSceneGUI;
            m_ClientTransforms.Clear();
            m_ServerTransforms.Clear();
            InvalidateTickCache();
        }

        // Cancels any running replay and resets playback state.
        void CancelPlayback()
        {
            var cts = m_PlaybackCts;
            m_PlaybackCts = null;
            cts?.Cancel();
            IsPlaying = false;
            PlaybackStateChanged?.Invoke();
        }

        // User-facing stop/pause: cancel the replay and select the frame/tick the replay was showing, so the
        // whole tool (timeline, tick selector, tick inspector) and the Scene View markers all land on the
        // paused position.
        public void StopReplay()
        {
            CancelPlayback();

            if (m_HasReplayPosition)
            {
                m_HasReplayPosition = false;
                m_SelectedFrameID = m_ReplayCurrentFrameID;
                m_SelectedTickID = ResolvePausedTickID(m_ReplayCurrentFrameID, m_ReplayCurrentTickID);
                m_HasSelection = true;
                ReplayPausedAtSelection?.Invoke(m_SelectedFrameID, m_SelectedTickID);
            }

            RefreshTransforms();
            SceneView.RepaintAll();
        }

        // The replay position carries a tick for tick/system playback, but in OncePerFrame mode no single tick
        // is current, so fall back to the frame's first tick to give the rest of the tool a valid selection.
        TickID ResolvePausedTickID(FrameID frameID, TickID tickID)
        {
            if (!tickID.Equals(default))
                return tickID;

            if (m_Data.ClientWorldData.IsCreated
                && m_Data.ClientWorldData.PerFrameData.TryGetValue(frameID, out var frame)
                && frame.TickIDs.Length > 0)
                return frame.TickIDs[0];

            return tickID;
        }

        public void StartReplay(ReplayRenderingFrequency mode)
        {
            PlaybackMode = mode;
            CancelPlayback();
            if (!m_Data.ClientWorldData.IsCreated)
                return;

            m_HasReplayPosition = false;

            var cts = new CancellationTokenSource();
            m_PlaybackCts = cts;
            IsPlaying = true;
            PlaybackStateChanged?.Invoke();
            m_PlaybackTask = RunPlaybackLoop(cts);
        }

        async Task RunPlaybackLoop(CancellationTokenSource cts)
        {
            try
            {
                await RunPlayback(cts.Token);
            }
            catch (OperationCanceledException) { }
            // A replay failure must surface now. Left in the task, it would rethrow out of the next
            // StopPlaybackAsync await and abort that caller's teardown (pause/playmode handlers).
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                cts.Dispose();
                if (ReferenceEquals(m_PlaybackCts, cts))
                {
                    m_PlaybackCts = null;
                    IsPlaying = false;
                    PlaybackStateChanged?.Invoke();
                }
            }
        }

        // Cancels any running replay and awaits its full unwind before returning.
        public async Task StopPlaybackAsync()
        {
            CancelPlayback();
            var task = m_PlaybackTask;
            m_PlaybackTask = null;
            if (task != null)
            {
                try { await task; }
                catch (OperationCanceledException) { }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // Resolves the replay delay (ms) for one frame. A frame longer than k_MaxRealFrameDeltaMs is the editor
        // being suspended (a pause to inspect traces, a breakpoint) so reuse the last delta MS
        internal static float ResolveReplayFrameDelayMs(float frameDeltaSeconds, ref float lastRealFrameDeltaMs)
        {
            var deltaMs = frameDeltaSeconds * 1000f;
            if (deltaMs > k_MaxRealFrameDeltaMs)
                return lastRealFrameDeltaMs;

            lastRealFrameDeltaMs = Mathf.Max(1f, deltaMs);
            return lastRealFrameDeltaMs;
        }

        // Applies the replay speed multiplier to a frame delay. Clamps the speed to its valid range and keeps
        // the result >= 1ms so a fast speed can never turn the replay loop into a busy spin.
        internal static int ScaleDelayMs(float frameDeltaMs, float speed)
        {
            var clampedSpeed = Mathf.Clamp(speed, ReplaySpeed.Min, ReplaySpeed.Max);
            return Mathf.Max(1, (int)(frameDeltaMs / clampedSpeed));
        }

        async Task RunPlayback(CancellationToken ct)
        {
            // Snapshot m_Data so the loop keeps using the data it started on.
            var data = m_Data;
            var frameIDs = data.ClientWorldData.FrameIDs;
            int frameCount = frameIDs.Length;
            if (frameCount == 0) return;

            int startIndex = 0;
            if (m_HasSelection)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    if (frameIDs[i].Equals(m_SelectedFrameID))
                    {
                        startIndex = i;
                        break;
                    }
                }
            }

            int f = startIndex;
            float lastRealFrameDeltaMs = k_PlaybackStepMs;
            while (!ct.IsCancellationRequested)
            {
                if (m_SeekFrame.HasValue)
                {
                    var seekTarget = m_SeekFrame.Value;
                    m_SeekFrame = null;
                    for (int i = 0; i < frameCount; i++)
                    {
                        if (frameIDs[i].Equals(seekTarget))
                        {
                            f = i;
                            break;
                        }
                    }
                }

                var frameID = frameIDs[f];
                // Track the position so a pause can select it. Reset the tick; the tick loops below set it,
                // and OncePerFrame leaves it default (resolved to the frame's first tick on pause).
                m_ReplayCurrentFrameID = frameID;
                m_ReplayCurrentTickID = default;
                m_HasReplayPosition = true;
                var currentMode = PlaybackMode;
                var hasFrameData = data.ClientWorldData.PerFrameData.TryGetValue(frameID, out var frame);
                var frameDeltaMs = hasFrameData
                    ? ResolveReplayFrameDelayMs(frame.m_CurrentDeltaTimeSeconds, ref lastRealFrameDeltaMs)
                    : k_PlaybackStepMs;
                int frameDelayMs = ScaleDelayMs(frameDeltaMs, PlaybackSpeed);

                ReplayFrameChanged?.Invoke(frameID);

                if (currentMode == ReplayRenderingFrequency.OncePerFrame)
                {
                    RefreshTransformsForFrame(frameID);
                    SceneView.RepaintAll();
                    await Task.Delay(frameDelayMs, ct);
                    f = (f + 1) % frameCount;
                    continue;
                }

                if (!hasFrameData)
                {
                    await Task.Delay(frameDelayMs, ct);
                    f = (f + 1) % frameCount;
                    continue;
                }

                var tickIDs = frame.TickIDs;
                int tickCount = tickIDs.Length;
                if (tickCount == 0)
                {
                    await Task.Delay(frameDelayMs, ct);
                    f = (f + 1) % frameCount;
                    continue;
                }

                if (currentMode == ReplayRenderingFrequency.OncePerTick)
                {
                    int tickDelayMs = Mathf.Max(1, frameDelayMs / tickCount);
                    for (int t = 0; t < tickCount && !ct.IsCancellationRequested && PlaybackMode == currentMode; t++)
                    {
                        var tickID = tickIDs[t];
                        m_ReplayCurrentTickID = tickID;
                        m_ClientTransforms.Clear();
                        m_ServerTransforms.Clear();
                        m_HasSelection = true;

                        if (frame.PerTickData.ContainsKey(tickID))
                        {
                            var clientManaged = GetClientTickDataCached(data.ClientWorldData, frameID, tickID);
                            ExtractTransforms(clientManaged, m_ClientTransforms);
                        }

                        if (data.ServerWorldData.IsCreated && data.ServerWorldData.PerTickData.ContainsKey(tickID))
                        {
                            var serverManaged = GetServerTickDataCached(data.ServerWorldData, tickID);
                            ExtractTransforms(serverManaged, m_ServerTransforms);
                        }

                        SceneView.RepaintAll();
                        await Task.Delay(tickDelayMs, ct);
                    }
                }
                else // OncePerSystem
                {
                    int totalSystems = CountTotalSystemsInFrame(frame);
                    if (totalSystems == 0)
                    {
                        await Task.Delay(frameDelayMs, ct);
                        f = (f + 1) % frameCount;
                        continue;
                    }
                    int systemDelayMs = Mathf.Max(1, frameDelayMs / totalSystems);

                    for (int t = 0; t < tickCount && !ct.IsCancellationRequested && PlaybackMode == currentMode; t++)
                    {
                        var tickID = tickIDs[t];
                        if (!frame.PerTickData.ContainsKey(tickID))
                            continue;

                        m_ReplayCurrentTickID = tickID;

                        var clientManaged = GetClientTickDataCached(data.ClientWorldData, frameID, tickID);

                        ManagedTickData? serverManaged = null;
                        if (data.ServerWorldData.IsCreated && data.ServerWorldData.PerTickData.ContainsKey(tickID))
                            serverManaged = GetServerTickDataCached(data.ServerWorldData, tickID);

                        m_ClientTransforms.Clear();
                        m_ServerTransforms.Clear();
                        m_HasSelection = true;
                        if (serverManaged.HasValue)
                            ExtractTransforms(serverManaged.Value, m_ServerTransforms);

                        var systems = clientManaged.SystemsByExecutionOrder;
                        for (int s = 0; s < systems.Count && !ct.IsCancellationRequested && PlaybackMode == currentMode; s++)
                        {
                            ExtractSystemTransforms(systems[s], m_ClientTransforms);

                            SceneView.RepaintAll();
                            await Task.Delay(systemDelayMs, ct);
                        }
                    }
                }

                f = (f + 1) % frameCount;
            }
        }

        static int CountTotalSystemsInFrame(FrameData frame)
        {
            int total = 0;
            var tickIDs = frame.TickIDs;
            for (int i = 0; i < tickIDs.Length; i++)
            {
                var tickID = tickIDs[i];
                if (frame.PerTickData.TryGetValue(tickID, out var tickData))
                    total += tickData.PerSystemData.Count;
            }
            return total;
        }

        void RefreshTransformsForFrame(FrameID frameID)
        {
            m_ClientTransforms.Clear();
            m_ServerTransforms.Clear();
            m_HasSelection = false;

            if (!m_Data.ClientWorldData.IsCreated || !m_Data.ClientWorldData.PerFrameData.TryGetValue(frameID, out var frame))
                return;

            var tickIDs = frame.TickIDs;
            for (int t = 0; t < tickIDs.Length; t++)
            {
                var tickID = tickIDs[t];
                if (frame.PerTickData.ContainsKey(tickID))
                {
                    var managed = GetClientTickDataCached(m_Data.ClientWorldData, frameID, tickID);
                    ExtractTransforms(managed, m_ClientTransforms);
                }

                if (m_Data.ServerWorldData.IsCreated && m_Data.ServerWorldData.PerTickData.ContainsKey(tickID))
                {
                    var managed = GetServerTickDataCached(m_Data.ServerWorldData, tickID);
                    ExtractTransforms(managed, m_ServerTransforms);
                }
            }

            m_HasSelection = true;
        }

        void RefreshTransforms()
        {
            m_ClientTransforms.Clear();
            m_ServerTransforms.Clear();

            if (m_Data.ClientWorldData.IsCreated
                && m_Data.ClientWorldData.PerFrameData.TryGetValue(m_SelectedFrameID, out var frame))
            {
                if (frame.PerTickData.ContainsKey(m_SelectedTickID))
                {
                    var managed = GetClientTickDataCached(
                        m_Data.ClientWorldData, m_SelectedFrameID, m_SelectedTickID);
                    ExtractTransforms(managed, m_ClientTransforms);
                }
            }

            if (m_Data.ServerWorldData.IsCreated
                && m_Data.ServerWorldData.PerTickData.ContainsKey(m_SelectedTickID))
            {
                var managed = GetServerTickDataCached(m_Data.ServerWorldData, m_SelectedTickID);
                ExtractTransforms(managed, m_ServerTransforms);
            }
        }

        // Materialize-once accessors: GetClientTickData/GetServerTickData are expensive (see m_ClientTickCache),
        // so memoize their result per frame/tick. The cached value is a self-contained managed copy, so it stays
        // valid even after the native trace data is freed.
        ManagedTickData GetClientTickDataCached(WorldData clientWorldData, FrameID frameID, TickID tickID)
        {
            var key = (frameID, tickID);
            if (!m_ClientTickCache.TryGetValue(key, out var managed))
            {
                managed = clientWorldData.GetClientTickData(frameID, tickID);
                m_ClientTickCache[key] = managed;
            }
            return managed;
        }

        ManagedTickData GetServerTickDataCached(WorldData serverWorldData, TickID tickID)
        {
            if (!m_ServerTickCache.TryGetValue(tickID, out var managed))
            {
                managed = serverWorldData.GetServerTickData(tickID);
                m_ServerTickCache[tickID] = managed;
            }
            return managed;
        }

        void InvalidateTickCache()
        {
            m_ClientTickCache.Clear();
            m_ServerTickCache.Clear();
        }

        static void ExtractTransforms(ManagedTickData tickData, Dictionary<int, GhostMarkerData> target)
        {
            foreach (var system in tickData.SystemsByExecutionOrder)
                ExtractSystemTransforms(system, target);
        }

        static void ExtractSystemTransforms(ManagedSystemData system, Dictionary<int, GhostMarkerData> target)
        {
            foreach (var (ghostId, ghostData) in system.GhostComponents)
            {
                if (!ghostData.Components.TryGetValue(typeof(LocalTransform), out var tracedComp))
                    continue;

                var ltValue = tracedComp.AfterValue ?? tracedComp.BeforeValue ?? tracedComp.NetcodeValue;
                if (ltValue is not LocalTransform lt)
                    continue;

                // OR HasDiff across systems so any diffing system marks the ghost as diffing.
                var hasDiff = ghostData.HasDiff || (target.TryGetValue(ghostId, out var prev) && prev.HasDiff);
                target[ghostId] = new GhostMarkerData
                {
                    Position = lt.Position,
                    Rotation = lt.Rotation,
                    Name = ghostData.GhostName,
                    HasDiff = hasDiff,
                };
            }
        }

        void OnSceneGUI(SceneView _)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            if (!m_HasSelection || !IsEnabled)
                return;

            m_AuthorityLabelStyle ??= new GUIStyle(EditorStyles.label);
            m_AuthorityLabelStyle.normal.textColor = AuthorityColor;
            m_PredictedLabelStyle ??= new GUIStyle(EditorStyles.label);
            m_PredictedLabelStyle.normal.textColor = PredictedColor;

            if (ShowAuthority)
            {
                foreach (var (_, data) in m_ServerTransforms)
                {
                    DrawAxisMarker(data, AuthorityColor, m_AuthorityLabelStyle);
                }
            }

            foreach (var (ghostId, data) in m_ClientTransforms)
            {
                var hasAuthority = m_ServerTransforms.TryGetValue(ghostId, out var authorityData);
                if (ShowPredicted && (data.HasDiff || !hasAuthority))
                    DrawAxisMarker(data, PredictedColor, m_PredictedLabelStyle);

                if (ShowDifference && data.HasDiff && hasAuthority)
                {
                    Handles.color = DiffLineColor;
                    Handles.DrawLine(data.Position, authorityData.Position, 2f);
                }
            }
        }

        // One cached style per marker color, refreshed once per repaint in OnSceneGUI.
        GUIStyle m_AuthorityLabelStyle;
        GUIStyle m_PredictedLabelStyle;

        void DrawAxisMarker(GhostMarkerData data, Color color, GUIStyle labelStyle)
        {
            var pos = data.Position;
            var rot = data.Rotation;
            float size = HandleUtility.GetHandleSize(pos) * k_AxisLengthFactor;

            var xEnd = pos + rot * Vector3.right * size;
            var yEnd = pos + rot * Vector3.up * size;
            var zEnd = pos + rot * Vector3.forward * size;

            Handles.color = color;
            Handles.DrawLine(pos, xEnd, 2f);
            Handles.DrawLine(pos, yEnd, 2f);
            Handles.DrawLine(pos, zEnd, 2f);
            Handles.SphereHandleCap(0, pos, Quaternion.identity, size * 0.1f, EventType.Repaint);

            Handles.Label(xEnd, "X", labelStyle);
            Handles.Label(yEnd, "Y", labelStyle);
            Handles.Label(zEnd, "Z", labelStyle);
            Handles.Label(pos + rot * Vector3.up * size * 1.4f, data.Name, labelStyle);
        }
    }
}
