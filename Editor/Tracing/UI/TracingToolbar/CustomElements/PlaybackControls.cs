using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode.Tracing;
using UnityEditor;
using UnityEngine.UIElements;

namespace Unity.Netcode.Editor.Tracing.UI.TracingToolbar
{
    [UxmlElement]
    partial class PlaybackControls : VisualElement
    {
        const string k_TemplatePath = Constants.Templates + "playback-controls.uxml";

        public const string k_PreviousFrame = "previous-frame";
        public const string k_NextFrame = "next-frame";
        public const string k_NextDiff = "next-diff";
        public const string k_PreviousDiff = "previous-diff";

        public Action<TickID, FrameID> OnTickChange;
        public Action<FrameID> OnFrameChange;
        internal Action<bool> OnClickPlay;
        public Action<ReplayRenderingFrequency> OnSelectFrequency;
        public Action<float> OnChangeReplaySpeed;

        FrameID m_CurrentFrame;
        TickID m_CurrentTick;
        NativeHashMap<FrameID,FrameData> m_PerFrameData;
        readonly List<FrameID> m_FrameIDs = new();

        // Decides which stored diffs count for next/previous-diff navigation; raw HasDiff when never set.
        Func<DiffInfo, bool> m_DiffVisibilityResolver;
        Button m_PreviousFrameButton;
        Button m_PreviousDiffButton;
        PlayButton m_PlayButton;
        Button m_NextDiffButton;
        Button m_NextFrameButton;

        NativeList<TickID> GetTickIds () => GetFrameData().TickIDs;
        int GetFrameIndex () => m_FrameIDs.IndexOf(m_CurrentFrame);
        FrameData GetFrameData() => m_PerFrameData[m_CurrentFrame];
        FrameData GetFrameData(FrameID frameID) => m_PerFrameData[frameID];

        public PlaybackControls()
        {
            AddToClassList(TracingToolbarUssClasses.PlaybackControls);
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_TemplatePath);
            visualTree.CloneTree(this);

            RegisterCallback<AttachToPanelEvent>((_) =>
            {
                m_PreviousFrameButton = this.Q<Button>(k_PreviousFrame);
                m_PreviousDiffButton = this.Q<Button>(k_PreviousDiff);
                m_PlayButton = this.Q<PlayButton>();
                m_NextDiffButton = this.Q<Button>(k_NextDiff);
                m_NextFrameButton = this.Q<Button>(k_NextFrame);

                m_PreviousFrameButton.clicked += OnPreviousFrameButtonClicked;
                m_PreviousDiffButton.clicked += OnPreviousDiffButtonClicked;
                m_PlayButton.OnSelectFrequency += HandleSelectFrequency;
                m_PlayButton.OnChangeReplaySpeed += HandleChangeReplaySpeed;
                m_PlayButton.OnClickPlay += OnClickPlay;
                m_NextDiffButton.clicked += OnNextDiffClicked;
                m_NextFrameButton.clicked += OnNextFrameClicked;
            });

            RegisterCallback<DetachFromPanelEvent>((_) =>
            {
                m_PreviousFrameButton.clicked -= OnPreviousFrameButtonClicked;
                m_PreviousDiffButton.clicked -= OnPreviousDiffButtonClicked;
                m_PlayButton.OnClickPlay -= OnClickPlay;
                m_PlayButton.OnSelectFrequency -= HandleSelectFrequency;
                m_PlayButton.OnChangeReplaySpeed -= HandleChangeReplaySpeed;
                m_NextDiffButton.clicked -= OnNextDiffClicked;
                m_NextFrameButton.clicked -= OnNextFrameClicked;
            });
        }

        void HandleSelectFrequency(ReplayRenderingFrequency frequency)
        {
            OnSelectFrequency(frequency);
        }

        void HandleChangeReplaySpeed(float speed)
        {
            OnChangeReplaySpeed?.Invoke(speed);
        }

        public void SetPerFrameData(NativeHashMap<FrameID,FrameData> perFrameData, NativeList<FrameID> frameIDs)
        {
            m_PerFrameData = perFrameData;
            m_FrameIDs.Clear();
            foreach (var frameID in frameIDs)
            {
                m_FrameIDs.Add(frameID);
            }
            SetCurrentFrame(m_FrameIDs[0]);
        }

        // Sets the filter deciding which diffs the next/previous-diff buttons navigate to.
        public void SetDiffVisibilityResolver(Func<DiffInfo, bool> resolver)
        {
            m_DiffVisibilityResolver = resolver;
            if (m_PerFrameData.IsCreated && m_FrameIDs.Contains(m_CurrentFrame))
                SetCurrentTick(m_CurrentTick);
        }

        bool IsDiffVisible(in DiffInfo diffInfo) => m_DiffVisibilityResolver?.Invoke(diffInfo) ?? diffInfo.HasDiff;

        (FrameID,TickID) FindNextDiff()
        {
            var startingFrameIndex = GetFrameIndex();
            var frameIndex = startingFrameIndex;
            var ids = GetTickIds();
            var tickIndex = ids.IndexOf(m_CurrentTick);
            if (tickIndex < 0)
            {
                tickIndex = 0;
            }
            while(frameIndex < m_FrameIDs.Count)
            {
                var frameData = GetFrameData(m_FrameIDs[frameIndex]);
                if (IsDiffVisible(frameData.DiffInfo))
                {

                    while (tickIndex < frameData.TickIDs.Length)
                    {
                        var tickID = frameData.TickIDs[tickIndex];

                        var isNotCurrentTickForCurrentFrame = (!tickID.Equals(m_CurrentTick) ||
                                                               tickID.Equals(m_CurrentTick) &&
                                                               startingFrameIndex != frameIndex);


                        if (IsDiffVisible(frameData.PerTickData[tickID].DiffInfo) && isNotCurrentTickForCurrentFrame)
                        {
                            return (m_FrameIDs[frameIndex], tickID);
                        }
                        tickIndex++;
                    }
                }

                frameIndex++;
                tickIndex = 0;
            }
            return (default, default);
        }

        (FrameID,TickID) FindPreviousDiff()
        {
            var startingFrameIndex = GetFrameIndex();
            var frameIndex = startingFrameIndex;
            var tickIndex = GetTickIds().IndexOf(m_CurrentTick);

            while (frameIndex >= 0)
            {
                var frame = GetFrameData(m_FrameIDs[frameIndex]);
                if (IsDiffVisible(frame.DiffInfo))
                {
                    while (tickIndex >= 0)
                    {
                        var tickID = frame.TickIDs[tickIndex];
                        var isNotCurrentTickForCurrentFrame = (!tickID.Equals(m_CurrentTick) ||
                                                               tickID.Equals(m_CurrentTick) &&
                                                               startingFrameIndex != frameIndex);

                        if (IsDiffVisible(frame.PerTickData[tickID].DiffInfo) && isNotCurrentTickForCurrentFrame)
                        {
                            return (m_FrameIDs[frameIndex], tickID);
                        }
                        tickIndex--;
                    }
                }

                frameIndex--;
                if (frameIndex >= 0)
                {
                    tickIndex = GetFrameData(m_FrameIDs[frameIndex]).TickIDs.Length - 1;
                }

            }
            return (default, default);
        }

        public void SetCurrentFrame(FrameID frameID)
        {
            m_CurrentFrame = frameID;
            var frameIndex = GetFrameIndex();
            if (frameIndex == -1)
            {
                m_NextFrameButton.SetEnabled(false);
                m_PreviousFrameButton.SetEnabled(false);
                return;
            }

            var tickIds = GetTickIds();
            if (tickIds.Length > 0)
            {
                SetCurrentTick(tickIds.IndexOf(m_CurrentTick) == -1 ? tickIds[0] : m_CurrentTick);
            }

            var isFirstFrame = frameIndex == 0;
            var isLastFrame = frameIndex == m_FrameIDs.Count - 1;

            if (isFirstFrame && isLastFrame)
            {
                m_NextFrameButton.SetEnabled(false);
                m_PreviousFrameButton.SetEnabled(false);
                return;
            }

            if (isFirstFrame)
            {
               m_PreviousFrameButton.SetEnabled(false);
               m_NextFrameButton.SetEnabled(true);
               return;
            }

            if (isLastFrame)
            {
                m_NextFrameButton.SetEnabled(false);
                m_PreviousFrameButton.SetEnabled(true);
                return;
            }

            m_PreviousFrameButton.SetEnabled(true);
            m_NextFrameButton.SetEnabled(true);
        }

        public void SetCurrentTick(TickID tickID)
        {
            m_CurrentTick = tickID;

            if (!m_PerFrameData.IsCreated || GetFrameIndex() == -1)
            {
                m_NextDiffButton.SetEnabled(false);
                m_PreviousDiffButton.SetEnabled(false);
                return;
            }

            var (_, previousDiffTick) = FindPreviousDiff();
            var (_, nextDiffTick) = FindNextDiff();

            if (nextDiffTick.Equals(default) && previousDiffTick.Equals(default) )
            {
                m_NextDiffButton.SetEnabled(false);
                m_PreviousDiffButton.SetEnabled(false);
                return;
            }

            if (nextDiffTick.Equals(default) )
            {
                m_NextDiffButton.SetEnabled(false);
                m_PreviousDiffButton.SetEnabled(true);
                return;
            }

            if (previousDiffTick.Equals(default))
            {
                m_PreviousDiffButton.SetEnabled(false);
                m_NextDiffButton.SetEnabled(true);
                return;
            }

            m_PreviousDiffButton.SetEnabled(true);
            m_NextDiffButton.SetEnabled(true);
        }

        public void Reset()
        {
            m_PerFrameData = default;
            m_CurrentFrame = default;
            m_CurrentTick = default;
            m_FrameIDs.Clear();
        }

        public void SetPlaybackState(bool playing) => m_PlayButton?.SetPlaying(playing);

        void OnPreviousFrameButtonClicked()
        {
            var idx = GetFrameIndex();
            if (idx > 0)
            {
                SetCurrentFrame(m_FrameIDs[idx - 1]);
                OnFrameChange?.Invoke(m_CurrentFrame);
            }
        }

        void OnNextFrameClicked()
        {
            var idx = GetFrameIndex();
            var nextFrameIndex = idx + 1;
            if (idx >= 0 && idx < m_FrameIDs.Count - 1)
            {
                SetCurrentFrame(m_FrameIDs[nextFrameIndex]);
                OnFrameChange?.Invoke(m_CurrentFrame);
            }
        }

        void OnPreviousDiffButtonClicked()
        {
            var (previousFrame, previousDiff) = FindPreviousDiff();
            SetCurrentFrame(previousFrame);
            SetCurrentTick(previousDiff);
            OnTickChange?.Invoke(m_CurrentTick, m_CurrentFrame);
        }

        void OnNextDiffClicked()
        {
            var (nextFrame, nextDif) = FindNextDiff();
            SetCurrentFrame(nextFrame);
            SetCurrentTick(nextDif);
            OnTickChange?.Invoke(m_CurrentTick, m_CurrentFrame);
        }
    }
}

