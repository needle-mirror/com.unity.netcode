using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.NetCode.Editor.Tracing.UI.TracingToolbar;
using Unity.NetCode.Tracing;
using UnityEngine.UIElements;
using UnityEngine.UIElements.TestFramework;

namespace Unity.NetCode.Editor.Tracing.UI.Tests
{
    [TestFixture]
    class PlaybackControlsTests : UITestFixture
    {
        TracingToolbarView m_TracingToolbar;
        VisualElement m_TracingToolbarElement;
        PlaybackControls m_PlaybackControls;

        NativeHashMap<FrameID, FrameData> m_TestFrameData;
        NativeList<FrameID> m_TestFrameIDs;
        readonly List<Action> m_Disposables = new();

        [SetUp]
        public void SetUp()
        {
            panelSize = new UnityEngine.Vector2(900, 500);
            m_Disposables.Clear();
            m_TracingToolbar = new TracingToolbarView();
            m_TracingToolbarElement = m_TracingToolbar.Create();

            m_PlaybackControls = m_TracingToolbarElement.Q<PlaybackControls>();
            m_TestFrameData = new NativeHashMap<FrameID, FrameData>(5, Allocator.Temp);
            m_TestFrameIDs = new NativeList<FrameID>(5, Allocator.Temp);
            rootVisualElement.Add(m_TracingToolbarElement);
            simulate.FrameUpdate();
            CreateTestFrameData();
            m_PlaybackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);
            m_PlaybackControls.SetEnabled(true);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var dispose in m_Disposables)
                dispose();
            m_Disposables.Clear();

            m_PlaybackControls?.RemoveFromHierarchy();
            if (m_TestFrameData.IsCreated)
                m_TestFrameData.Dispose();
            if (m_TestFrameIDs.IsCreated)
                m_TestFrameIDs.Dispose();

            rootVisualElement.Remove(m_TracingToolbarElement);
            m_PlaybackControls = null;
            m_TracingToolbarElement = null;
            m_TracingToolbar = null;
        }

        void CreateTestFrameData()
        {
            // Create 5 frames with ticks
            for (int f = 0; f < 5; f++)
            {
                var frameID = new FrameID { value = f };
                m_TestFrameIDs.Add(frameID);

                var frameData = new FrameData(0.016f);
                if (f % 2 == 0) // Frames 0, 2, 4 have diffs
                    frameData.DiffInfo.AddDiff(DiffInfo.DiffReasons.ComponentData);

                // Add ticks to frame
                for (int t = 0; t < 3; t++)
                {
                    var tickID = new TickID { value = new NetworkTick((uint)(f * 10 + t)) };
                    frameData.TickIDs.Add(tickID);

                    var tickData = new TickData(0.016f, default, TraceType.Default);
                    if (t == 1) // Middle tick has diff
                        tickData.DiffInfo.AddDiff(DiffInfo.DiffReasons.ComponentData);
                    frameData.PerTickData.Add(tickID, tickData);
                }

                m_TestFrameData.Add(frameID, frameData);
            }
        }

       [Test]
        public void Reset_ClearsAllData()
        {
            m_PlaybackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);
            m_PlaybackControls.Reset();

            // After reset, the state should be cleared
            Assert.AreEqual(default(FrameID), GetCurrentFrameFieldValue());
        }

        [Test]
        public void SetCurrentFrame_EmptyOrUnknownFrame_DoesNotThrow()
        {
            var emptyFrame = new FrameID { value = 99 };
            m_TestFrameIDs.Add(emptyFrame);
            m_TestFrameData.Add(emptyFrame, new FrameData(0.016f));
            m_PlaybackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);

            Assert.DoesNotThrow(() => m_PlaybackControls.SetCurrentFrame(emptyFrame));
            Assert.DoesNotThrow(() => m_PlaybackControls.SetCurrentFrame(default));
            Assert.DoesNotThrow(() => m_PlaybackControls.SetCurrentTick(default));
        }

        [Test]
        public void ClickNextFrame_WhenAtLastFrame_ThenButtonShouldBeDisabled()
        {
            FrameID callbackResult = default;
            void Capture(object _, TracingSelectionChange change)
            {
                callbackResult = change.frameID;
            }

            m_TracingToolbar.OnTracingSelectionChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnTracingSelectionChanged -= Capture);

            m_PlaybackControls.SetCurrentFrame(m_TestFrameIDs[^1]);
            Assert.AreEqual(false, GetNextFrameButton().enabledSelf);

            simulate.Click(GetPreviousFrameButton());
            Assert.AreEqual(true, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(m_TestFrameIDs[^2], callbackResult);

            simulate.Click(GetNextFrameButton());
            Assert.AreEqual(false, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(m_TestFrameIDs[^1], callbackResult);
        }

        [Test]
        public void ClickPreviousFrame_WhenAtFirstFrame_ThenButtonShouldBeDisabled()
        {
            FrameID callbackResult = default;
            void Capture(object _, TracingSelectionChange change)
            {
                callbackResult = change.frameID;
            }

            m_TracingToolbar.OnTracingSelectionChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnTracingSelectionChanged -= Capture);

            m_PlaybackControls.SetCurrentFrame(m_TestFrameIDs[0]);
            Assert.AreEqual(false, GetPreviousFrameButton().enabledSelf);

            simulate.Click(GetNextFrameButton());
            Assert.AreEqual(true, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(m_TestFrameIDs[1], callbackResult);

            simulate.Click(GetPreviousFrameButton());
            Assert.AreEqual(false, GetPreviousFrameButton().enabledSelf);
            Assert.AreEqual(m_TestFrameIDs[0], callbackResult);
        }

        [Test]
        public void ClickPreviousDiff_WhenAtFirstDiff_ThenButtonShouldBeDisabled()
        {
            m_PlaybackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);

            TickID firstDiff = default;
            TickID nextTick = default;
            FrameID firstFrame = default;
            FrameID nextFramee = default;
            TickID callbackTickId = default;
            FrameID callbackFrameId = default;


            foreach (var kvp in m_TestFrameData)
            {
                var frameData = kvp.Value;
                if (frameData.DiffInfo.HasDiff)
                {
                    foreach (var id in frameData.TickIDs)
                    {
                        if (frameData.PerTickData[id].DiffInfo.HasDiff)
                        {
                            if (firstDiff.Equals(default))
                            {
                                firstDiff = id;
                                firstFrame = kvp.Key;

                            }
                            else if (nextTick.Equals(default))
                            {
                                nextTick = id;
                                nextFramee = kvp.Key;
                                break;
                            }
                        }
                    }

                    if (!nextTick.Equals(default) && !nextFramee.Equals(default))
                    {
                        break;
                    }
                }
            }

            void Capture(object _, TracingSelectionChange change)
            {
                callbackFrameId = change.frameID;
                callbackTickId = change.tickID;
            }

            m_TracingToolbar.OnTracingSelectionChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnTracingSelectionChanged -= Capture);

            // by default after initial processing frame[0] is selected and tick is stays at default and previous button is selected
            Assert.AreEqual(false, GetPreviousFrameButton().enabledSelf);
            Assert.AreEqual(firstFrame, callbackFrameId);
            Assert.AreEqual(false, GetPreviousFrameButton().enabledSelf);

            // clicking next diff will select the first diff and corresponding frame
            // previous tick will remain disabled since no previous tick exists
            simulate.Click(GetNextDiffButton());
            Assert.AreEqual(false, GetPreviousFrameButton().enabledSelf);
            Assert.AreEqual(firstFrame, callbackFrameId);
            Assert.AreEqual(firstDiff, callbackTickId);

            // clicking next diff will select the next diff and corresponding frame
            // previous tick will be enabled since a previous tick exists
            simulate.Click(GetNextDiffButton());
            Assert.AreEqual(true, GetPreviousFrameButton().enabledSelf);
            Assert.AreEqual(nextFramee, callbackFrameId);
            Assert.AreEqual(nextTick, callbackTickId);

            // clicking prev diff will select the previous diff and corresponding frame
            // previous tick should now be disabled since this is the first diffs
            simulate.Click(GetPreviousDiffButton());
            Assert.AreEqual(false, GetPreviousFrameButton().enabledSelf);
            Assert.AreEqual(firstFrame, callbackFrameId);
            Assert.AreEqual(firstDiff, callbackTickId);
        }

        [Test]
        public void ClickNextDiff_WhenAtLastDiff_ThenButtonShouldBeDisabled()
        {
            m_PlaybackControls.SetPerFrameData(m_TestFrameData, m_TestFrameIDs);

            TickID lastDiff = default;
            TickID previousDiff = default;
            FrameID lastFrame = default;
            FrameID previousFrame = default;
            TickID callbackTickId = default;
            FrameID callbackFrameId = default;

            for (var frameIndex = m_TestFrameIDs.Count - 1; frameIndex >= 0; frameIndex--)
            {
                var frameId = m_TestFrameIDs[frameIndex];
                var frameData = m_TestFrameData[frameId];
                if (frameData.DiffInfo.HasDiff)
                {
                    for (var tickIndex = frameData.PerTickData.Count - 1; tickIndex >= 0; tickIndex--)
                    {
                        var tickId = frameData.TickIDs[tickIndex];
                        if (frameData.PerTickData[tickId].DiffInfo.HasDiff)
                        {
                            if (lastDiff.Equals(default))
                            {
                                lastDiff = tickId;
                                lastFrame = frameId;

                            }
                            else if (previousDiff.Equals(default))
                            {
                                previousDiff = tickId;
                                previousFrame = frameId;
                                break;
                            }
                        }
                    }
                    if (!previousDiff.Equals(default) && !previousFrame.Equals(default))
                    {
                        break;
                    }
                }
            }

            void Capture(object _, TracingSelectionChange change)
            {
                callbackFrameId = change.frameID;
                callbackTickId = change.tickID;
            };

            m_TracingToolbar.OnTracingSelectionChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnTracingSelectionChanged -= Capture);

            // start with last frame and last tick selected (not last diff)
            m_PlaybackControls.SetCurrentFrame(m_TestFrameIDs[^1]);
            m_PlaybackControls.SetCurrentTick(m_TestFrameData[m_TestFrameIDs[^1]].TickIDs[^1]);
            Assert.AreEqual(false, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(false, GetNextDiffButton().enabledSelf);

            // clicking previous diff will select the first diff and corresponding frame
            // previous tick will remain disabled since no previous tick exists
            simulate.Click(GetPreviousDiffButton());
            Assert.AreEqual(lastFrame, callbackFrameId);
            Assert.AreEqual(lastDiff, callbackTickId);
            var isNextFrameEnabled = !m_TestFrameData[m_TestFrameIDs[^1]].TickIDs.Contains(lastDiff);
            Assert.AreEqual(isNextFrameEnabled, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(false, GetNextDiffButton().enabledSelf);

            // clicking previous diff will select the previous diff and corresponding frame
            // previous tick will be enabled since a previous tick exists
            simulate.Click(GetPreviousDiffButton());
            Assert.AreEqual(previousFrame, callbackFrameId);
            Assert.AreEqual(previousDiff, callbackTickId);
            isNextFrameEnabled = !m_TestFrameData[m_TestFrameIDs[^1]].TickIDs.Contains(previousDiff);
            Assert.AreEqual(isNextFrameEnabled, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(true, GetNextDiffButton().enabledSelf);

            // clicking prev diff will select the previous diff and corresponding frame
            // previous tick should now be disabled since this is the first diffs
            simulate.Click(GetNextDiffButton());
            Assert.AreEqual(lastFrame, callbackFrameId);
            Assert.AreEqual(lastDiff, callbackTickId);
            isNextFrameEnabled = !m_TestFrameData[m_TestFrameIDs[^1]].TickIDs.Contains(lastDiff);
            Assert.AreEqual(isNextFrameEnabled, GetNextFrameButton().enabledSelf);
            Assert.AreEqual(false, GetNextDiffButton().enabledSelf);
        }

        [Test]
        public void FrequencySelection_ThreeOptionsAvailable()
        {
            Assert.AreEqual(3, Enum.GetValues(typeof(ReplayRenderingFrequency)).Length);
        }

        [Test]
        public void SelectFrequency_OncePerFrame_InvokesOnSelectFrequencyCallback()
        {
            ReplayRenderingFrequency? capturedFrequency = null;

            void Capture(object _, ReplayRenderingFrequency freq)
            {
                capturedFrequency = freq;
            }

            m_TracingToolbar.OnReplayRenderingFrequencyChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnReplayRenderingFrequencyChanged -= Capture);

            ClickFrequencyDropdownItem(PlayButton.k_MTextOncePerFrame);

            Assert.IsTrue(capturedFrequency.HasValue);
            Assert.AreEqual(ReplayRenderingFrequency.OncePerFrame, capturedFrequency.Value);
            var button = GetPlayButtonElement();
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.IconPlay));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconPlayPerTick));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconPlayPerSystem));
        }

        [Test]
        public void SelectFrequency_OncePerTick_InvokesOnSelectFrequencyCallback()
        {
            ReplayRenderingFrequency? capturedFrequency = null;

            void Capture(object _, ReplayRenderingFrequency freq)
            {
                capturedFrequency = freq;
            }

            m_TracingToolbar.OnReplayRenderingFrequencyChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnReplayRenderingFrequencyChanged -= Capture);
            ClickFrequencyDropdownItem(PlayButton.k_MTextOncePerTick);

            Assert.IsTrue(capturedFrequency.HasValue);
            Assert.AreEqual(ReplayRenderingFrequency.OncePerTick, capturedFrequency.Value);
            var button = GetPlayButtonElement();
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.IconPlayPerTick));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconPlay));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconPlayPerSystem));
        }

        [Test]
        public void SelectFrequency_OncePerSystem_InvokesOnSelectFrequencyCallback()
        {
            ReplayRenderingFrequency? capturedFrequency = null;

            void Capture(object _, ReplayRenderingFrequency freq)
            {
                capturedFrequency = freq;
            }

            m_TracingToolbar.OnReplayRenderingFrequencyChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnReplayRenderingFrequencyChanged -= Capture);

            ClickFrequencyDropdownItem(PlayButton.k_MTextOncePerSystem);

            Assert.IsTrue(capturedFrequency.HasValue);
            Assert.AreEqual(ReplayRenderingFrequency.OncePerSystem, capturedFrequency.Value);
            var button = GetPlayButtonElement();
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.IconPlayPerSystem));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconPlay));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconPlayPerTick));
        }

        [Test]
        public void ChangeReplaySpeed_InvokesOnReplaySpeedChangedCallback()
        {
            float? capturedSpeed = null;

            void Capture(object _, float speed)
            {
                capturedSpeed = speed;
            }

            m_TracingToolbar.OnReplaySpeedChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnReplaySpeedChanged -= Capture);

            SetReplaySpeed(2f);

            Assert.IsTrue(capturedSpeed.HasValue);
            Assert.AreEqual(2f, capturedSpeed.Value);
        }

        [Test]
        public void ChangeReplaySpeed_ToSameValue_DoesNotInvokeCallback()
        {
            int callCount = 0;

            void Capture(object _, float __) => callCount++;

            m_TracingToolbar.OnReplaySpeedChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnReplaySpeedChanged -= Capture);

            SetReplaySpeed(ReplaySpeed.Default);

            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SelectFrequency_DoesNotChangeReplaySpeed()
        {
            SetReplaySpeed(2.5f);

            float? capturedSpeed = null;
            void Capture(object _, float speed) => capturedSpeed = speed;
            m_TracingToolbar.OnReplaySpeedChanged += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnReplaySpeedChanged -= Capture);

            ClickFrequencyDropdownItem(PlayButton.k_MTextOncePerTick);
            ClickFrequencyDropdownItem(PlayButton.k_MTextOncePerSystem);

            Assert.IsFalse(capturedSpeed.HasValue);
            Assert.AreEqual(2.5f, GetPlayButton().ReplaySpeed);
        }

        [TestCase(1f, 1f)]
        [TestCase(1.7f, 1.5f)]
        [TestCase(1.8f, 2f)]
        [TestCase(5f, 3f)]
        [TestCase(-1f, 0.5f)]
        public void SnapReplaySpeed_ClampsAndSnapsToStep(float input, float expected)
        {
            Assert.AreEqual(expected, PlayButton.SnapReplaySpeed(input));
        }

        [Test]
        public void RecordButton_InitialState_HasRecordOffClass()
        {
            var button = GetRecordButton();
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconRecord));
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.IconRecordOff));
        }


        [Test]
        public void ToggleRecordButton_InvokesOnToggleTracingAndAppliesCorrespondingClass()
        {
            bool? captured = null;

            void Capture(bool enabled)
            {
                captured = enabled;
            }

            m_TracingToolbar.OnTracingEnabledToggled += Capture;
            m_Disposables.Add(() => m_TracingToolbar.OnTracingEnabledToggled -= Capture);

            var button = GetRecordButton();

            simulate.Click(button);

            Assert.IsTrue(captured.Value);
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.IconRecord));
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconRecordOff));

            simulate.Click(button);

            Assert.IsFalse(captured.Value);
            Assert.IsFalse(button.ClassListContains(TracingToolbarUssClasses.IconRecord));
            Assert.IsTrue(button.ClassListContains(TracingToolbarUssClasses.IconRecordOff));
        }

        [Test]
        public void DiffNavigation_RespectsDiffVisibilityFilters()
        {
            Assert.AreEqual(true, GetNextDiffButton().enabledSelf, "precondition: diffs ahead with no filter");

            // Filter out the only recorded reason: navigation must stop seeing any diff.
            var filters = new TracingViewFilters
            {
                EnabledDiffReasons = DiffInfo.AllDiffReasons & ~DiffInfo.DiffReasons.ComponentData,
            };
            m_PlaybackControls.SetDiffVisibilityResolver(diffInfo => filters.HasVisibleDiff(diffInfo));

            Assert.AreEqual(false, GetNextDiffButton().enabledSelf, "next-diff should not navigate to filtered-out diffs");
            Assert.AreEqual(false, GetPreviousDiffButton().enabledSelf);

            // Clearing the resolver falls back to the raw HasDiff navigation.
            m_PlaybackControls.SetDiffVisibilityResolver(null);
            Assert.AreEqual(true, GetNextDiffButton().enabledSelf);
        }

        // Helper methods to access private fields through reflection
        private FrameID GetCurrentFrameFieldValue()
        {
            var field = typeof(PlaybackControls).GetField("m_CurrentFrame",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (FrameID)field!.GetValue(m_PlaybackControls);
        }

        private Button GetPreviousFrameButton()
        {
            return m_PlaybackControls.Q<Button>(PlaybackControls.k_PreviousFrame);
        }

        private Button GetNextFrameButton()
        {
            return m_PlaybackControls.Q<Button>(PlaybackControls.k_NextFrame);
        }

        VisualElement GetNextDiffButton()
        {
            return m_PlaybackControls.Q<Button>(PlaybackControls.k_NextDiff);
        }

        VisualElement GetPreviousDiffButton()
        {
            return m_PlaybackControls.Q<Button>(PlaybackControls.k_PreviousDiff);
        }

        VisualElement GetPlayButtonElement()
        {
            var playButton = m_PlaybackControls.Q<PlayButton>();
            return playButton.Q<Button>(PlayButton.k_PlayButton);
        }

        Button GetRecordButton() => m_TracingToolbarElement.Q<Button>(TracingToolbarView.k_EnableTracingButton);

        PlayButton GetPlayButton() => m_PlaybackControls.Q<PlayButton>();

        void SetReplaySpeed(float speed)
        {
            var method = typeof(PlayButton).GetMethod("SetReplaySpeed",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(GetPlayButton(), new object[] { speed });
        }

        void ClickFrequencyDropdownItem(string menuItemText)
        {
            var playButton = m_PlaybackControls.Q<PlayButton>();
            var frequency = menuItemText switch
            {
                PlayButton.k_MTextOncePerFrame => ReplayRenderingFrequency.OncePerFrame,
                PlayButton.k_MTextOncePerTick  => ReplayRenderingFrequency.OncePerTick,
                PlayButton.k_MTextOncePerSystem => ReplayRenderingFrequency.OncePerSystem,
                _ => throw new ArgumentException($"Unknown menu item: {menuItemText}")
            };

            var method = typeof(PlayButton).GetMethod("SetSelectedFrequency",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(playButton, new object[] { frequency });
        }
    }
}

