/* TODO: This test is failing on main https://unity-ci.cds.internal.unity3d.com/job/51974397?utm_source=slack. Tracked in https://jira.unity3d.com/browse/MTT-13324
#if UNITY_2023_2_OR_NEWER
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.NetCode.Analytics;
using Unity.NetCode.Editor;
using UnityEditor;
using UnityEngine.Analytics;
using UnityEngine.PlayerLoop;

namespace Unity.NetCode.Tests
{
    internal class MultiplayerPlayModeWindowTests
    {
        private class AnalyticsMock : IAnalyticsSender
        {
            private readonly List<string> _eventsSent = new ();

            public void ClearEvents()
            {
                _eventsSent.Clear();
            }

            public int EventCountByType<T>()
            {
                return _eventsSent.FindAll(e => e == typeof(T).Name).Count;
            }

            public void SendAnalytic(IAnalytic analytic)
            {
                _eventsSent.Add(analytic.GetType().Name);
            }
        }

        private bool _oldValueWarnBatchedTicks;
        private MultiplayerPlayModeWindow _window;
        private readonly AnalyticsMock _analyticsMock = new ();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _oldValueWarnBatchedTicks = MultiplayerPlayModePreferences.WarnBatchedTicks;
            _window = EditorWindow.GetWindow<MultiplayerPlayModeWindow>();
            NetCodeAnalytics.s_AnalyticsSender = _analyticsMock;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            MultiplayerPlayModePreferences.WarnBatchedTicks = _oldValueWarnBatchedTicks;
            NetCodeAnalytics.s_AnalyticsSender = null;
            _window.Close();
        }

        [SetUp]
        public void SetUp()
        {
            _analyticsMock.ClearEvents();
        }

        [Test]
        public void AnalyticsEventSentOnFirstUpdate()
        {
            // Act: Trigger the first update
            var oldValue = MultiplayerPlayModePreferences.WarnBatchedTicks;
            MultiplayerPlayModePreferences.WarnBatchedTicks = !oldValue;
            _window.PlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            Assert.AreEqual(1, _analyticsMock.EventCountByType<MultiplayerPlayModePreferencesUpdatedAnalytic>(), "One event should have been sent on first update");
        }

        [Test]
        public void AnalyticsEventSentOnlyOnPrefsChange()
        {
            // Act 1: enter playmode, no event should be sent
            _window.PlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            Assert.AreEqual(0, _analyticsMock.EventCountByType<MultiplayerPlayModePreferencesUpdatedAnalytic>(), "No event should have been sent");

            // Act 2: Update preferences and exit playmode, a preferences updated event should be sent
            var oldValue = MultiplayerPlayModePreferences.WarnBatchedTicks;
            MultiplayerPlayModePreferences.WarnBatchedTicks = !oldValue;
            _window.PlayModeStateChanged(PlayModeStateChange.ExitingPlayMode);
            Assert.AreEqual(1, _analyticsMock.EventCountByType<MultiplayerPlayModePreferencesUpdatedAnalytic>(), "One event should have been sent");

            // Act 3: Enter playmode again, no event should be sent
            _window.PlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            Assert.AreEqual(1, _analyticsMock.EventCountByType<MultiplayerPlayModePreferencesUpdatedAnalytic>(), "No event should have been sent");
        }
    }
}
#endif
*/
