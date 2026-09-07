#if UNITY_EDITOR && !NETCODE_NDEBUG

using System;
using System.Collections;
using NUnit.Framework;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace Unity.Netcode.Tests
{
    // Drives a NetCodeTestWorld until ProfilerDriver has integrated `frameCount` new frames,
    // not until N yields have happened — under editor load (e.g. CI) one yield does not always
    // produce one profiler frame, so a fixed-iteration loop sometimes leaves the buffer empty.
    internal static class ProfilerFrameRecorder
    {
        static readonly ProfilerArea[] s_AllAreas = (ProfilerArea[])Enum.GetValues(typeof(ProfilerArea));

        public static IEnumerator RecordFrames(NetCodeTestWorld testWorld, int frameCount, bool clearFrames = true)
        {
            var framesRecorded = 0;
            Action<int, int> onFrameRecorded = (_, __) => framesRecorded++;
            ProfilerDriver.NewProfilerFrameRecorded += onFrameRecorded;
            try
            {
                if (clearFrames)
                {
                    // Disable then yield so any in-flight frame integrations land before we clear.
                    // ProfilerHistory::AddFrame and CleanupFrameHistory share a lock; without this
                    // a stray integration can land mid-clear and skew the next-enable behaviour.
                    ProfilerDriver.enabled = false;
                    yield return null;
                    ProfilerDriver.ClearAllFrames();
                    // Discard counts from frames that landed during the drain — we only want to
                    // count frames produced after we re-enable below.
                    framesRecorded = 0;
                }
                ProfilerDriver.profileEditor = true;
                ProfilerDriver.enabled = true;

                const int maxTicks = 1000;
                var ticksDone = 0;
                var profilerDisabledMidLoop = false;
                for (; ticksDone < maxTicks && framesRecorded < frameCount; ticksDone++)
                {
                    // Bail if Unity auto-disabled the profiler (e.g. ring-buffer overflow:
                    // "Stopping profiler. Profiler is not able to send data..."). Without this
                    // check the loop would spin to maxTicks and the eventual failure would land
                    // on testWorld.Dispose() with a misleading "wasn't cleaned up" message.
                    if (!ProfilerDriver.enabled)
                    {
                        profilerDisabledMidLoop = true;
                        break;
                    }
                    testWorld.Tick();
                    yield return null;
                }
                Assert.IsFalse(profilerDisabledMidLoop,
                    $"Profiler was disabled externally during the recording loop (after {framesRecorded}/{frameCount} frames " +
                    $"and {ticksDone} editor ticks). This usually means a buffer overflow — check the editor log for " +
                    "'Stopping profiler. Profiler is not able to send data...'.");
                Assert.GreaterOrEqual(framesRecorded, frameCount,
                    $"Profiler only recorded {framesRecorded}/{frameCount} frames after {ticksDone} editor ticks. " +
                    "This usually means the editor stalled (CI load) or the buffer was reset mid-test (e.g. domain reload).");
            }
            finally
            {
                ProfilerDriver.NewProfilerFrameRecorded -= onFrameRecorded;
            }
        }

        // CPU carries Profiler.EmitFrameMetaData payloads our tests assert on. Disabling the
        // other areas cuts profile buffer pressure enough that we don't trigger the
        // "Profiler is not able to send data" auto-disable under CI load.
        public static bool[] DisableNonEssentialAreas()
        {
            var previous = new bool[s_AllAreas.Length];
            for (var i = 0; i < s_AllAreas.Length; i++)
            {
                previous[i] = ProfilerDriver.IsAreaEnabled(s_AllAreas[i]);
                if (s_AllAreas[i] != ProfilerArea.CPU)
                    ProfilerDriver.SetAreaEnabled(s_AllAreas[i], false);
            }
            return previous;
        }

        public static void RestoreAreas(bool[] previous)
        {
            if (previous == null)
                return;
            for (var i = 0; i < s_AllAreas.Length && i < previous.Length; i++)
                ProfilerDriver.SetAreaEnabled(s_AllAreas[i], previous[i]);
        }
    }
}

#endif
