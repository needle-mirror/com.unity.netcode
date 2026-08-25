using System;
using System.Collections.Generic;
using Unity.NetCode.Editor.Tracing.UI;
using UnityEngine;

namespace Unity.NetCode.Editor.Tracing
{
    internal delegate bool TryResolveTracingTarget(string key, out Type type, out TracingTargetKind kind);

    /// <summary>
    /// The tracing target selection edited by <see cref="SelectTracingTargetWindow"/>: which systems and
    /// components are selected, whether each component is a required or optional.
    /// </summary>
    internal class TracingTargetSelection
    {
        readonly struct KeyState
        {
            public readonly TracingTargetKind Kind;
            public readonly bool ComponentTraceRequired;

            public KeyState(TracingTargetKind kind, bool componentTraceRequired)
            {
                Kind = kind;
                ComponentTraceRequired = kind == TracingTargetKind.Component && componentTraceRequired;
            }
        }

        readonly HashSet<string> m_SelectedKeys = new(StringComparer.Ordinal);
        readonly List<string> m_SelectedOrder = new();
        readonly Dictionary<string, KeyState> m_StateByKey = new(StringComparer.Ordinal);

        /// <summary>Selected keys in selection order.</summary>
        public IReadOnlyList<string> SelectedOrder => m_SelectedOrder;

        public int Count => m_SelectedOrder.Count;

        public bool IsSelected(Type type) => m_SelectedKeys.Contains(TracingTargetTypes.TypeKey(type));

        public void CountByKind(out int systems, out int components)
        {
            systems = 0;
            components = 0;
            foreach (var key in m_SelectedOrder)
            {
                if (!m_StateByKey.TryGetValue(key, out var state))
                {
                    continue;
                }

                if (state.Kind == TracingTargetKind.System)
                {
                    systems++;
                }
                else if (state.Kind == TracingTargetKind.Component)
                {
                    components++;
                }
            }
        }

        /// <summary>Selection state change without persisting; callers batch changes and <see cref="Save"/> once.</summary>
        public void Update(Type type, TracingTargetKind kind, bool selected)
        {
            var key = TracingTargetTypes.TypeKey(type);
            if (selected)
            {
                if (m_SelectedKeys.Add(key))
                {
                    m_SelectedOrder.Add(key);
                }

                var required = false;
                if (kind == TracingTargetKind.Component
                    && m_StateByKey.TryGetValue(key, out var prior)
                    && prior.Kind == TracingTargetKind.Component)
                {
                    required = prior.ComponentTraceRequired;
                }

                m_StateByKey[key] = new KeyState(kind, required);
            }
            else
            {
                if (m_SelectedKeys.Remove(key))
                {
                    m_SelectedOrder.Remove(key);
                }

                m_StateByKey.Remove(key);
            }
        }

        public void SetSelected(Type type, TracingTargetKind kind, bool selected)
        {
            Update(type, kind, selected);
            Save();
        }

        public bool GetComponentRequired(Type componentType)
        {
            var key = TracingTargetTypes.TypeKey(componentType);
            return m_StateByKey.TryGetValue(key, out var state)
                && state.Kind == TracingTargetKind.Component
                && state.ComponentTraceRequired;
        }

        public void SetComponentRequired(Type componentType, bool required)
        {
            var key = TracingTargetTypes.TypeKey(componentType);
            if (m_StateByKey.TryGetValue(key, out var existing) && existing.Kind == TracingTargetKind.System)
            {
                return;
            }

            m_StateByKey[key] = new KeyState(TracingTargetKind.Component, required);

            if (IsSelected(componentType))
            {
                Save();
            }
        }

        /// <summary>
        /// Reloads the selection from settings. <paramref name="tryResolveTarget"/> resolves a persisted key
        /// to a live type and its kind; unresolvable entries are dropped.
        /// </summary>
        public void Load(TryResolveTracingTarget tryResolveTarget)
        {
            m_SelectedKeys.Clear();
            m_SelectedOrder.Clear();
            m_StateByKey.Clear();

            try
            {
                var data = NetCodeTracingTargetSettings.GetSelection();
                if (data?.entries == null)
                {
                    return;
                }

                foreach (var e in data.entries)
                {
                    if (string.IsNullOrEmpty(e.assemblyQualifiedName))
                    {
                        continue;
                    }

                    var key = e.assemblyQualifiedName;
                    if (!tryResolveTarget(key, out _, out var kind))
                    {
                        continue;
                    }

                    if (!m_SelectedKeys.Add(key))
                    {
                        continue;
                    }

                    m_SelectedOrder.Add(key);
                    m_StateByKey[key] = new KeyState(kind,
                        kind == TracingTargetKind.Component && e.required);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Select Tracing Target] Could not load tracing target selection: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                var list = new List<TracingTargetSelectionEntry>(m_SelectedOrder.Count);
                foreach (var key in m_SelectedOrder)
                {
                    m_StateByKey.TryGetValue(key, out var state);
                    list.Add(new TracingTargetSelectionEntry
                    {
                        assemblyQualifiedName = key,
                        kind = (int)state.Kind,
                        required = state.Kind == TracingTargetKind.Component && state.ComponentTraceRequired
                    });
                }

                NetCodeTracingTargetSettings.SaveSelection(new TracingTargetSelectionFile { entries = list });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Select Tracing Target] Could not save tracing target selection: {ex.Message}");
            }
        }
    }
}
