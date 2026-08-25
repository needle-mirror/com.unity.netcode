using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode.Tracing;

namespace Unity.NetCode.Editor.Tracing.UI
{
    /// <summary>
    /// Snapshot of the tracing targets the current recording was started with. Selection changes apply to
    /// the config immediately, but already-recorded traces keep the old target set.
    /// </summary>
    static class TracingRecordingTargets
    {
        static readonly HashSet<string> s_Systems = new();
        static readonly HashSet<string> s_Components = new();
        
        public static bool IsCaptured { get; private set; }

        // Raised when the snapshot is captured or reset, so indicators can re-evaluate.
        public static event Action Changed;
        
        public static IReadOnlyCollection<string> RecordedSystems => s_Systems;
        
        public static IReadOnlyCollection<string> RecordedComponents => s_Components;

        // Snapshots the current config as the target set of the recording that is starting.
        public static void CaptureFromConfig()
        {
            s_Systems.Clear();
            s_Components.Clear();
            var config = TracingDataAccess.Config.Data;
            if (!config.Initialized)
            {
                IsCaptured = false;
                Changed?.Invoke();
                return;
            }
            
            foreach (var sysIndex in config.SystemTypesToTrace)
                s_Systems.Add(TracingTargetNames.System(sysIndex));
            foreach (var ct in config.RequiredTypesToTrace)
                s_Components.Add(TracingTargetNames.Component(ct.TypeIndex));
            foreach (var ct in config.OptionalTypesToTrace)
                s_Components.Add(TracingTargetNames.Component(ct.TypeIndex));
            IsCaptured = true;
            Changed?.Invoke();
        }

        /// <summary>
        /// Adds the current config to the recorded target set without dropping anything.
        /// </summary>
        public static void MergeFromConfig()
        {
            var config = TracingDataAccess.Config.Data;
            if (!config.Initialized)
                return;

            foreach (var sysIndex in config.SystemTypesToTrace)
                s_Systems.Add(TracingTargetNames.System(sysIndex));
            foreach (var ct in config.RequiredTypesToTrace)
                s_Components.Add(TracingTargetNames.Component(ct.TypeIndex));
            foreach (var ct in config.OptionalTypesToTrace)
                s_Components.Add(TracingTargetNames.Component(ct.TypeIndex));
            IsCaptured = true;
            Changed?.Invoke();
        }

        public static void Reset()
        {
            s_Systems.Clear();
            s_Components.Clear();
            IsCaptured = false;
            Changed?.Invoke();
        }
        
        public static bool IsUntracedTarget(string name, bool isSystem)
            => IsCaptured && !(isSystem ? s_Systems : s_Components).Contains(name);
        
        public static bool SelectionDiffersFromRecording()
        {
            if (!IsCaptured)
                return false;
            var config = TracingDataAccess.Config.Data;
            if (!config.Initialized)
                return s_Systems.Count > 0 || s_Components.Count > 0;

            var systemCount = 0;
            foreach (var sysIndex in config.SystemTypesToTrace)
            {
                systemCount++;
                if (!s_Systems.Contains(TracingTargetNames.System(sysIndex)))
                    return true;
            }

            var componentCount = 0;
            foreach (var ct in config.RequiredTypesToTrace)
            {
                componentCount++;
                if (!s_Components.Contains(TracingTargetNames.Component(ct.TypeIndex)))
                    return true;
            }

            foreach (var ct in config.OptionalTypesToTrace)
            {
                componentCount++;
                if (!s_Components.Contains(TracingTargetNames.Component(ct.TypeIndex)))
                    return true;
            }

            // No additions found; any size mismatch left is a removal.
            return systemCount != s_Systems.Count || componentCount != s_Components.Count;
        }
    }
}
