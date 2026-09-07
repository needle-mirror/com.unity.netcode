using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode.Tracing
{
    /// <summary>
    /// The shared static that stores the config to enable tracing and filter what components and systems to trace.
    /// </summary>
    internal struct UnmanagedConfig : IDisposable
    {
        internal const int k_TracingMemoryLimitMB = 2048;
        internal const int k_TargetFPSDuringProcessing = 30;

        public NativeHashSet<ComponentType> RequiredTypesToTrace;
        public NativeHashSet<ComponentType> OptionalTypesToTrace;
        public NativeHashSet<SystemTypeIndex> SystemTypesToTrace;

        private bool m_EnableTracing;
        internal bool Initialized {get; private set;}
        internal bool m_IsReadingRawTraces;
        internal bool m_ClientHasConnection;
        internal bool m_ServerHasConnection;
        public bool OnlyTraceAfter;

        public bool EnableTracing
        {
            get => m_EnableTracing;
            set
            {
                if (value)
                    TracingDataAccess.IsProcessed = false;
                m_EnableTracing = value;
            }
        }

        public bool IsTracingEnabledAndReady () => Initialized && m_EnableTracing && !m_IsReadingRawTraces && m_ClientHasConnection && m_ServerHasConnection;

        /// <summary>
        /// Returns true if tracing is initialized and at least one component or system type is selected to be traced.
        /// </summary>
        public bool TracingTypesSelected() => Initialized && SystemTypesToTrace.Count > 0;

        static Action OnTypeTracesUpdated
        {
            get => TracingDataAccess.OnTypeTracesUpdated;
            set => TracingDataAccess.OnTypeTracesUpdated = value;
        }

        public void Init()
        {
            EnableTracing = false;

            InitializeComponentTypes();
        }

        void InitializeComponentTypes()
        {
            RequiredTypesToTrace = new(0, Allocator.Persistent);
            OptionalTypesToTrace = new(0, Allocator.Persistent);
            SystemTypesToTrace = new(0, Allocator.Persistent);
            TracingDataAccess.s_WorldsSaveSize.Data.MaxTracesSizeMB = k_TracingMemoryLimitMB;
            Initialized = true;
        }

        public void AddRequiredTypeToTrace(ComponentType type)
        {
            if(!Initialized)
                InitializeComponentTypes();
            // GhostInstance is always traced implicitly, so registering it as required is a no-op.
            if(RequiredTypesToTrace.Contains(type) || type.TypeIndex == TypeManager.GetTypeIndex<GhostInstance>())
            {
                return;
            }
            if (OptionalTypesToTrace.Contains(type))
            {
                Debug.LogWarning($"Type {type} already registered as optional type to trace, it can't also be registered as required.");
                return;
            }
            RequiredTypesToTrace.Add(type);
            AllTypeDiffers.Instance.TryAddTypeDiffer(type);
            OnTypeTracesUpdated?.Invoke();
        }

        public void RemoveRequiredTypeToTrace(ComponentType type)
        {
            if (!Initialized)
            {
                InitializeComponentTypes();
                return;
            }

            RequiredTypesToTrace.Remove(type);
            if (AllTypeDiffers.Instance.AllDiffers.TryGetValue(type, out var differ))
            {
                differ.Dispose();
                AllTypeDiffers.Instance.AllDiffers.Remove(type);
            }
            OnTypeTracesUpdated?.Invoke();
        }

        public void AddOptionalTypeToTrace(ComponentType type)
        {
            if(!Initialized)
                InitializeComponentTypes();
            if(OptionalTypesToTrace.Contains(type))
            {
                return;
            }

            if (type.TypeIndex == TypeManager.GetTypeIndex<GhostInstance>())
            {
                Debug.LogWarning("GhostInstance component is always traced, it can't be registered as optional.");
                return;
            }
            if (RequiredTypesToTrace.Contains(type))
            {
                Debug.LogWarning($"Type {type} already registered as required type to trace, it can't also be registered as optional.");
                return;
            }
            OptionalTypesToTrace.Add(type);
            AllTypeDiffers.Instance.TryAddTypeDiffer(type);
            OnTypeTracesUpdated?.Invoke();
        }

        public void RemoveOptionalTypeToTrace(ComponentType type)
        {
            if(!Initialized)
                InitializeComponentTypes();
            OptionalTypesToTrace.Remove(type);
            if (AllTypeDiffers.Instance.AllDiffers.TryGetValue(type, out var differ))
            {
                differ.Dispose();
                AllTypeDiffers.Instance.AllDiffers.Remove(type);
            }
            OnTypeTracesUpdated?.Invoke();
        }

        /// <summary>
        /// Add systems for which a trace will be saved.
        /// If no systems are registered all systems will be traced.
        /// </summary>
        /// <param name="type"></param>
        public void AddSystemTypeToTrace(SystemTypeIndex type)
        {
            if(!Initialized)
                InitializeComponentTypes();
            if (SystemTypesToTrace.Contains(type))
            {
                Debug.LogWarning($"System type {type} already registered as system to trace.");
                return;
            }
            SystemTypesToTrace.Add(type);
            OnTypeTracesUpdated?.Invoke();
        }

        public void RemoveSystemTypeToTrace(SystemTypeIndex type)
        {
            if(!Initialized)
                InitializeComponentTypes();
            SystemTypesToTrace.Remove(type);
            OnTypeTracesUpdated?.Invoke();
        }


        public void ResetTracingTargets()
        {
            DisposeAllTracingTargets();
            InitializeComponentTypes();
        }


        void DisposeAllTracingTargets()
        {
            if(RequiredTypesToTrace.IsCreated)
                RequiredTypesToTrace.Dispose();
            if(OptionalTypesToTrace.IsCreated)
                OptionalTypesToTrace.Dispose();
            if(SystemTypesToTrace.IsCreated)
                SystemTypesToTrace.Dispose();
            AllTypeDiffers.Instance.Dispose();
        }

        public void Dispose()
        {
            if(!Initialized)
                return;
            DisposeAllTracingTargets();

            Initialized = false;
            EnableTracing = false;
            OnlyTraceAfter = false;
            m_ClientHasConnection = false;
            m_ServerHasConnection = false;
            m_IsReadingRawTraces = false;
            OnTypeTracesUpdated?.Invoke();
        }
    }
}
