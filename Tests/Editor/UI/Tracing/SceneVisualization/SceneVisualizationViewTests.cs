using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode.Tracing;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements.TestFramework;

namespace Unity.Netcode.Editor.Tracing.UI.Tests
{
    class SceneVisualizationViewTests : UITestFixture
    {
        SceneVisualizationView m_View;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            AddTestComponent<TracingUITestComponent>();
        }

        [SetUp]
        public void SetUp()
        {
            m_View = new SceneVisualizationView();
        }

        [TearDown]
        public void TearDown()
        {
            m_View?.Dispose();
            m_View = null;
            rootVisualElement.Clear();
        }

        // --- Create() ---

        [Test]
        public void Create_ReturnsNonNullVisualElement()
        {
            Assert.That(m_View.Create(), Is.Not.Null);
        }

        [Test]
        public void Create_CanBeAddedToHierarchy()
        {
            var element = m_View.Create();
            rootVisualElement.Add(element);
            simulate.FrameUpdate();

            Assert.That(element.panel, Is.Not.Null);
        }

        // --- OnDataAvailable() ---

        [Test]
        public void OnDataAvailable_CompletesImmediately()
        {
            var data = CreateMinimalTracingData();
            try
            {
                var task = m_View.OnDataAvailable(data);
                Assert.That(task.IsCompleted, Is.True);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- OnSelectedFrameChanged() ---

        [Test]
        public void OnSelectedFrameChanged_WithNoData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_View.OnSelectedFrameChanged(default));
        }

        [Test]
        public void OnSelectedFrameChanged_WithFrameHavingNoTicks_DoesNotThrow()
        {
            var data = CreateTracingData(withTicks: false);
            try
            {
                m_View.OnDataAvailable(data);
                Assert.DoesNotThrow(() => m_View.OnSelectedFrameChanged(new FrameID { value = 1 }));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void OnSelectedFrameChanged_WithValidFrameAndTick_DoesNotThrow()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                Assert.DoesNotThrow(() => m_View.OnSelectedFrameChanged(new FrameID { value = 1 }));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- OnSelectedTickChanged() ---

        [Test]
        public void OnSelectedTickChanged_WithNoData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_View.OnSelectedTickChanged(default));
        }

        [Test]
        public void OnSelectedTickChanged_WithValidData_DoesNotThrow()
        {
            var data = CreateTracingData(withTicks: true);
            var tickId = new TickID { value = new NetworkTick(10) };
            try
            {
                m_View.OnDataAvailable(data);
                m_View.OnSelectedFrameChanged(new FrameID { value = 1 });
                Assert.DoesNotThrow(() => m_View.OnSelectedTickChanged(tickId));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- Clear() ---

        [Test]
        public void Clear_WithNoData_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_View.Clear());
        }

        [Test]
        public void Clear_AfterDataAndSelection_DoesNotThrow()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.OnSelectedFrameChanged(new FrameID { value = 1 });
                Assert.DoesNotThrow(() => m_View.Clear());
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- Dispose() ---

        [Test]
        public void Dispose_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_View.Dispose());
            m_View = null; // prevent double-dispose in TearDown
        }

        // --- IsPlaying initial state ---

        [Test]
        public void IsPlaying_InitialValue_IsFalse()
        {
            Assert.That(m_View.IsPlaying, Is.False);
        }

        // --- StartReplay() ---

        [Test]
        public void StartReplay_WithNoData_LeavesIsPlayingFalse()
        {
            m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
            Assert.That(m_View.IsPlaying, Is.False);
        }

        [Test]
        public void StartReplay_WithEmptyFrames_LeavesIsPlayingFalse()
        {
            // Minimal data has no frames, so RunPlayback returns immediately.
            var data = CreateMinimalTracingData();
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
                Assert.That(m_View.IsPlaying, Is.False);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StartReplay_WithFrameData_SetsIsPlayingTrue()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
                Assert.That(m_View.IsPlaying, Is.True);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StartReplay_SetsPlaybackMode()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerTick);
                Assert.That(m_View.PlaybackMode, Is.EqualTo(ReplayRenderingFrequency.OncePerTick));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StartReplay_WhenAlreadyPlaying_RestartsWithNewMode()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerTick);

                Assert.That(m_View.IsPlaying, Is.True);
                Assert.That(m_View.PlaybackMode, Is.EqualTo(ReplayRenderingFrequency.OncePerTick));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StartReplay_WithFrameData_FiresPlaybackStateChangedAsPlaying()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);

                bool sawPlayingTrue = false;
                m_View.PlaybackStateChanged += () => { if (m_View.IsPlaying) sawPlayingTrue = true; };

                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                Assert.That(sawPlayingTrue, Is.True);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StartReplay_WithFrameData_FiresReplayFrameChangedWithFirstFrame()
        {
            var data = CreateTracingData(withTicks: true);
            var expectedFrame = new FrameID { value = 1 };
            try
            {
                m_View.OnDataAvailable(data);

                FrameID? reportedFrame = null;
                m_View.ReplayFrameChanged += id => reportedFrame = id;

                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                Assert.That(reportedFrame, Is.EqualTo(expectedFrame));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- StopReplay() ---

        [Test]
        public void StopReplay_WhenNotPlaying_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => m_View.StopReplay());
        }

        [Test]
        public void StopReplay_WhenPlaying_SetsIsPlayingFalse()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
                m_View.StopReplay();
                Assert.That(m_View.IsPlaying, Is.False);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StopReplay_FiresPlaybackStateChanged()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                int count = 0;
                m_View.PlaybackStateChanged += () => count++;

                m_View.StopReplay();

                // Fires exactly once. StopReplay clears m_PlaybackCts before cancelling, so the cancelled
                // replay's finally block sees it is no longer the active replay and skips its own state
                // reset; only StopReplay itself raises the event.
                Assert.That(count, Is.EqualTo(1));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StopReplay_WhenPlaying_FiresReplayPausedAtSelectionWithCurrentPosition()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                FrameID pausedFrame = default;
                TickID pausedTick = default;
                bool fired = false;
                m_View.ReplayPausedAtSelection += (f, t) =>
                {
                    pausedFrame = f;
                    pausedTick = t;
                    fired = true;
                };

                m_View.StopReplay();

                // The loop sets the current frame before its first await, so the synchronous pause above sees it.
                // OncePerFrame leaves no current tick, so the frame's first tick is selected.
                Assert.That(fired, Is.True);
                Assert.That(pausedFrame, Is.EqualTo(new FrameID { value = 1 }));
                Assert.That(pausedTick, Is.EqualTo(new TickID { value = new NetworkTick(10) }));
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void StopReplay_WhenNotPlaying_DoesNotFireReplayPausedAtSelection()
        {
            bool fired = false;
            m_View.ReplayPausedAtSelection += (_, _) => fired = true;
            m_View.StopReplay();
            Assert.That(fired, Is.False);
        }

        // --- PlaybackMode ---

        [Test]
        public void PlaybackMode_CanBeChangedWhilePlaying()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                m_View.PlaybackMode = ReplayRenderingFrequency.OncePerTick;

                Assert.That(m_View.PlaybackMode, Is.EqualTo(ReplayRenderingFrequency.OncePerTick));
                Assert.That(m_View.IsPlaying, Is.True);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- PlaybackSpeed / ScaleDelayMs() (a single speed shared by all rendering frequencies) ---

        [Test]
        public void PlaybackSpeed_InitialValue_IsDefault()
        {
            Assert.That(m_View.PlaybackSpeed, Is.EqualTo(ReplaySpeed.Default));
        }

        [Test]
        public void PlaybackSpeed_CanBeChangedWhilePlaying()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                m_View.PlaybackSpeed = 2f;

                Assert.That(m_View.PlaybackSpeed, Is.EqualTo(2f));
                Assert.That(m_View.IsPlaying, Is.True);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        [Test]
        public void ScaleDelayMs_DefaultSpeed_KeepsDelay()
        {
            Assert.That(SceneVisualizationView.ScaleDelayMs(16f, ReplaySpeed.Default), Is.EqualTo(16));
        }

        [Test]
        public void ScaleDelayMs_FasterSpeed_ShortensDelay()
        {
            Assert.That(SceneVisualizationView.ScaleDelayMs(16f, 2f), Is.EqualTo(8));
        }

        [Test]
        public void ScaleDelayMs_SlowerSpeed_LengthensDelay()
        {
            Assert.That(SceneVisualizationView.ScaleDelayMs(16f, 0.5f), Is.EqualTo(32));
        }

        [Test]
        public void ScaleDelayMs_SpeedOutOfRange_IsClamped()
        {
            Assert.That(SceneVisualizationView.ScaleDelayMs(30f, 100f), Is.EqualTo((int)(30f / ReplaySpeed.Max)));
            Assert.That(SceneVisualizationView.ScaleDelayMs(30f, 0f), Is.EqualTo((int)(30f / ReplaySpeed.Min)));
        }

        [Test]
        public void ScaleDelayMs_TinyDelayAtMaxSpeed_StaysPositive()
        {
            // A fast speed must never produce a 0ms delay, which would turn the replay loop into a busy spin.
            Assert.That(SceneVisualizationView.ScaleDelayMs(1f, ReplaySpeed.Max), Is.GreaterThanOrEqualTo(1));
        }

        // --- OnSelectedFrameChanged() during playback ---

        [Test]
        public void OnSelectedFrameChanged_WhenPlaying_KeepsPlaybackRunning()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
                m_View.OnSelectedFrameChanged(new FrameID { value = 1 });
                Assert.That(m_View.IsPlaying, Is.True);
            }
            finally
            {
                m_View.StopReplay();
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- Clear() during playback ---

        [Test]
        public void Clear_WhenPlaying_StopsPlayback()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);
                m_View.Clear();
                Assert.That(m_View.IsPlaying, Is.False);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // --- StopPlaybackAsync() ---

        [Test]
        public void StopPlaybackAsync_WhenNotPlaying_CompletesImmediately()
        {
            var task = m_View.StopPlaybackAsync();
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(m_View.IsPlaying, Is.False);
        }

        [Test]
        public void StopPlaybackAsync_WhenPlaying_SetsIsPlayingFalseSynchronously()
        {
            var data = CreateTracingData(withTicks: true);
            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                // Cancellation is synchronous even though awaiting the loop's unwind is not. This is the
                // property TracingWindow relies on at ExitingEditMode: the token is flipped before the
                // trace's native data is freed on play-mode enter.
                m_View.StopPlaybackAsync();
                Assert.That(m_View.IsPlaying, Is.False);
            }
            finally
            {
                data.ClientWorldData.Dispose();
                data.ServerWorldData.Dispose();
            }
        }

        // Regression test for the ObjectDisposedException on play-mode enter while a scene replay was running:
        // the replay loop is cancelled and the trace's native data is freed (as TracingWindow does), then the
        // loop is allowed to resume. It must unwind without dereferencing the freed collections.
        [UnityTest]
        public IEnumerator StopPlaybackAsync_ThenDisposeDataWhileReplaying_LoopUnwindsWithoutThrowing()
        {
            var data = CreateTracingData(withTicks: true);

            m_View.OnDataAvailable(data);
            m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

            // Let the replay loop spin for a few editor frames so it is mid-flight (suspended on Task.Delay).
            for (int i = 0; i < 5; i++)
                yield return null;

            // Mirror TracingWindow's teardown order: cancel the replay, then free the native data. Cancellation
            // happens synchronously inside StopPlaybackAsync, before the dispose below.
            var stopTask = m_View.StopPlaybackAsync();
            data.ClientWorldData.Dispose();
            data.ServerWorldData.Dispose();

            // Pump editor frames until the loop has fully unwound (bounded so a hang fails instead of stalling).
            int guard = 0;
            while (!stopTask.IsCompleted && guard++ < 600)
                yield return null;

            Assert.That(stopTask.IsCompleted, Is.True, "Replay loop did not unwind after cancellation.");
            Assert.That(m_View.IsPlaying, Is.False);
            // Surface any non-cancellation fault (e.g. ObjectDisposedException) from the replay loop as a failure.
            if (stopTask.IsFaulted)
                throw stopTask.Exception;
        }


        // Regression test for the missing-timelines window state: a replay task that faulted (any
        // non-cancellation exception in the replay loop) used to rethrow out of the next
        // StopPlaybackAsync await, killing the async pause/play teardown that called it — views were
        // never cleared and DisposeProcessedWorldData never ran, so capture could not resume and every
        // later pause was a silent no-op. The stored fault must be logged, never rethrown.
        [Test]
        public async Task StopPlaybackAsync_AfterReplayTaskFaulted_LogsInsteadOfThrowing()
        {
            var taskField = typeof(SceneVisualizationView).GetField("m_PlaybackTask", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(taskField, Is.Not.Null, "m_PlaybackTask field not found via reflection");
            taskField.SetValue(m_View, Task.FromException(new InvalidOperationException("simulated replay fault")));

            LogAssert.Expect(LogType.Exception, new Regex("simulated replay fault"));
            await m_View.StopPlaybackAsync();
            Assert.That(m_View.IsPlaying, Is.False);
        }

        [UnityTest]
        public IEnumerator ResetStaticStateWhileReplaying_DataFreed_LoopDoesNotThrow()
        {
            var data = CreateTracingData(withTicks: true);
            TracingDataAccess.SetClientTracingDataSingleton(CreateSingleton(data.ClientWorldData));
            TracingDataAccess.SetServerTracingDataSingleton(CreateSingleton(data.ServerWorldData));

            try
            {
                m_View.OnDataAvailable(data);
                m_View.StartReplay(ReplayRenderingFrequency.OncePerFrame);

                // Let the loop spin so it is genuinely mid-flight (suspended on Task.Delay).
                for (int i = 0; i < 3; i++)
                    yield return null;

                // Mirror TracingWindow's teardown order at ExitingEditMode: cancel the replay, then let
                // ResetStaticState free the native data out from under the (now cancelled) loop.
                var pendingStop = m_View.StopPlaybackAsync();
                TracingDataAccess.ResetStaticState();

                // Pump frames so the cancelled loop resumes against the now-freed data and unwinds.
                int guard = 0;
                while (!pendingStop.IsCompleted && guard++ < 600)
                    yield return null;

                Assert.That(pendingStop.IsCompleted, Is.True, "Replay loop did not unwind after cancellation.");
                Assert.That(m_View.IsPlaying, Is.False);
                // A non-cancellation fault here is the ObjectDisposedException this fix targets.
                if (pendingStop.IsFaulted)
                    throw pendingStop.Exception;
            }
            finally
            {

                TracingDataAccess.DisposeAllWorldData();
            }
        }

        // --- ResolveReplayFrameDelayMs() (reuses the previous frame's delta over an editor-pause frame) ---

        [Test]
        public void ResolveReplayFrameDelayMs_RealFrame_UsesAndRemembersIt()
        {
            float last = 100f;
            var result = SceneVisualizationView.ResolveReplayFrameDelayMs(0.016f, ref last);
            Assert.That(result, Is.EqualTo(16f).Within(0.001f));
            Assert.That(last, Is.EqualTo(16f).Within(0.001f));
        }

        [Test]
        public void ResolveReplayFrameDelayMs_EditorPauseFrame_ReusesPreviousRealDelta()
        {
            // 45s comes from pausing play mode ~45s to inspect traces. Replay must use the previous real
            // frame's pace (16ms) and must NOT overwrite the remembered real delta.
            float last = 16f;
            var result = SceneVisualizationView.ResolveReplayFrameDelayMs(45f, ref last);
            Assert.That(result, Is.EqualTo(16f));
            Assert.That(last, Is.EqualTo(16f));
        }

        [Test]
        public void ResolveReplayFrameDelayMs_PauseOnFirstFrame_FallsBackToSeed()
        {
            // No real frame seen yet: the seeded pace is used rather than dwelling on the pause.
            float last = 100f;
            Assert.That(SceneVisualizationView.ResolveReplayFrameDelayMs(45f, ref last), Is.EqualTo(100f));
        }

        [Test]
        public void ResolveReplayFrameDelayMs_SubMillisecondFrame_StaysPositive()
        {
            float last = 100f;
            Assert.That(SceneVisualizationView.ResolveReplayFrameDelayMs(0f, ref last), Is.GreaterThanOrEqualTo(1f));
        }

        static TracingDataSingleton CreateSingleton(WorldData processedWorldData)
        {
            var singleton = default(TracingDataSingleton);
            singleton.ProcessedWorldData = processedWorldData;
            singleton.UnprocessedTraces = new UnprocessedTraces(Allocator.Persistent);
            singleton.IsCreated = true;
            return singleton;
        }

        static TracingData CreateMinimalTracingData()
        {
            return new TracingData
            {
                ClientWorldData = new WorldData(Allocator.Persistent),
                ServerWorldData = new WorldData(Allocator.Persistent)
            };
        }

        static TracingData CreateTracingData(bool withTicks)
        {
            var clientWorldData = new WorldData(Allocator.Persistent);
            var serverWorldData = new WorldData(Allocator.Persistent);

            var frameId = new FrameID { value = 1 };
            var frameData = new FrameData(0.016f);

            if (withTicks)
            {
                var tickId = new TickID { value = new NetworkTick(10) };
                frameData.TickIDs.Add(tickId);
                frameData.PerTickData.Add(tickId, new TickData(0.016f, default, TraceType.Default));
            }

            clientWorldData.FrameIDs.Add(frameId);
            clientWorldData.PerFrameData.Add(frameId, frameData);

            return new TracingData
            {
                ClientWorldData = clientWorldData,
                ServerWorldData = serverWorldData
            };
        }
    }
}
