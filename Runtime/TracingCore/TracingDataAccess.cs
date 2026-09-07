using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace Unity.Netcode.Tracing
{
    internal enum TracingProcessingStep
    {
        RawTraces,
        Diff,
    }

    // Tracing backend entry point.
    internal struct TracingDataAccess
    {
        // Public shared static for the config that enables tracing and what to trace.
        public static readonly SharedStatic<UnmanagedConfig> Config = SharedStatic<UnmanagedConfig>.GetOrCreate<TracingDataAccess>();
        public static Action OnTypeTracesUpdated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        public static void ResetStaticState()
        {
            DisposeAllWorldData();
            Config.Data.Dispose();
            IsProcessed = false;
            m_ForceProcessingOverMultipleFrames = false;
        }

        internal struct WorldsSaveSize
        {
            public double ClientTracesSizeMB;
            public bool ServerShouldReset;
            public bool ClientShouldReset;
            public double ServerTracesSizeMB;
            public double MaxTracesSizeMB;
        }

        // Private shared static to keep track of the total size used by both the client and server traces and reset timing.
        internal static readonly SharedStatic<WorldsSaveSize> s_WorldsSaveSize = SharedStatic<WorldsSaveSize>.GetOrCreate<WorldsSaveSize>();
        private static bool m_IsCreated;
        private static TracingDataSingleton s_Client;
        private static TracingDataSingleton s_Server;
        private const double BytesToMbConversionFactor = 1f / (1024 * 1024);
        /// <summary>
        /// True if the TracingDataSingletons have been processed and the ProcessedWorldData is up to date with the unprocessed traces, false otherwise.
        /// </summary>
        internal static bool IsProcessed;

        /// <summary>
        /// Only used for testing
        /// </summary>
        internal static bool m_ForceProcessingOverMultipleFrames;

        /// <summary>
        /// TracingDataAccess is responsible for disposing of client and server singleton/traces since it's used to access those outside of playmode.
        /// </summary>
        private static void Init()
        {
            if(m_IsCreated)
                return;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
#endif
            m_IsCreated = true;
            IsProcessed = false;
            s_Client = default;
            s_Server = default;
        }

        internal static void ResetStateSaveSizes()
        {
            s_WorldsSaveSize.Data.ClientTracesSizeMB = 0;
            s_WorldsSaveSize.Data.ServerTracesSizeMB = 0;
            s_WorldsSaveSize.Data.ClientShouldReset = false;
            s_WorldsSaveSize.Data.ServerShouldReset = false;

            s_WorldsSaveSize.Data.MaxTracesSizeMB = UnmanagedConfig.k_TracingMemoryLimitMB;
        }

        /// <summary>
        /// Adds the size of the new server trace to total size used by tracing, and checks if we need to reset the tracing window to not go over memory limits.
        /// </summary>
        /// <param name="sizeBytes">Size in bytes of the StateSave that was written to the server tracingDataSingleton unprocess trace array.</param>
        /// <returns></returns>
        internal static bool AddServerTraceSize(int sizeBytes)
        {
            s_WorldsSaveSize.Data.ServerTracesSizeMB += sizeBytes * BytesToMbConversionFactor;
            if ( s_WorldsSaveSize.Data.ServerShouldReset)
            {
                s_WorldsSaveSize.Data.ServerShouldReset = false;
                s_WorldsSaveSize.Data.ServerTracesSizeMB = 0;
                return true;
            }
            if ( s_WorldsSaveSize.Data.ClientTracesSizeMB +  s_WorldsSaveSize.Data.ServerTracesSizeMB >  s_WorldsSaveSize.Data.MaxTracesSizeMB)
            {
                s_WorldsSaveSize.Data.ServerTracesSizeMB = 0;
                s_WorldsSaveSize.Data.ClientShouldReset = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Adds the size of the new client trace to total size used by tracing, and checks if we need to reset the tracing window to not go over memory limits.
        /// </summary>
        /// <param name="sizeBytes">Size in bytes of the StateSave that was written to the client tracingDataSingleton unprocess trace array.</param>
        /// <returns></returns>
        internal static bool AddClientTraceSize(int sizeBytes)
        {
            s_WorldsSaveSize.Data.ClientTracesSizeMB += sizeBytes * BytesToMbConversionFactor;
            if ( s_WorldsSaveSize.Data.ClientShouldReset)
            {
                s_WorldsSaveSize.Data.ClientShouldReset = false;
                s_WorldsSaveSize.Data.ClientTracesSizeMB = 0;
                return true;
            }
            if ( s_WorldsSaveSize.Data.ClientTracesSizeMB +  s_WorldsSaveSize.Data.ServerTracesSizeMB >  s_WorldsSaveSize.Data.MaxTracesSizeMB)
            {
                s_WorldsSaveSize.Data.ClientTracesSizeMB = 0;
                s_WorldsSaveSize.Data.ServerShouldReset = true;
                return true;
            }
            return false;
        }

        // IEnumerator cannot take struct ref and must yield return the step so we need a class to keep the ref.
        internal class ProcessedWorldsData
        {
            public WorldData ClientWorldData;
            public WorldData ServerWorldData;
        }

        /// <summary>
        /// Get the most up to date TracingDataSingletons for client and server, process their raw trace data and then process the client server diff.
        /// </summary>
        /// <param name="ct">Cancellation token to interrupt the processing.</param>
        /// <param name="progress">Optional callback reporting the current processing step and its progress in [0,1].</param>
        /// <returns>Returns true if the diff was processed successfully.</returns>
        static async Awaitable<ProcessedWorldsData> ProcessDiff(CancellationToken ct, Action<TracingProcessingStep, float> progress = null)
        {
            Config.Data.m_IsReadingRawTraces = true;
            // First get updated world singletons if available.
            var usePlaymodeSingletons = GetRefSingletonsFromWorlds(out var playModeClientSingleton, out var playModeServerSingleton, true);

            // If we are during playmode with tracing running, we use the updated singletons.
            if (usePlaymodeSingletons)
                RefreshPlayModeSingletons(playModeClientSingleton, playModeServerSingleton);

            // Outside playmode we can use the singleton references that wre set by EnableTracingSystem's OnDestroy.
            if (!s_Client.IsCreated || !s_Server.IsCreated)
                return null;

            var frameBudgetMs = 1000f / UnmanagedConfig.k_TargetFPSDuringProcessing;
            var stop = false;

            var processedWorldData = new ProcessedWorldsData();
            var totalTraceCount = Mathf.Max(1, s_Client.UnprocessedTraces.Count + s_Server.UnprocessedTraces.Count);
            var clientProgressEnd = (float)s_Client.UnprocessedTraces.Count / totalTraceCount;

            using var clientProcessingSteps = s_Client.ProcessRawTraceData(processedWorldData, ct).GetEnumerator();
            if (!await ProcessTickEnumerable(clientProcessingSteps, frameBudgetMs, usePlaymodeSingletons, ct, progress, TracingProcessingStep.RawTraces, 0f, clientProgressEnd))
                stop = true;
            stop |= ct.IsCancellationRequested;

            using var serverProcessingSteps = s_Server.ProcessRawTraceData(processedWorldData, ct).GetEnumerator();
            if (!stop && !await ProcessTickEnumerable(serverProcessingSteps, frameBudgetMs, usePlaymodeSingletons, ct, progress, TracingProcessingStep.RawTraces, clientProgressEnd, 1f))
                stop = true;
            stop |= ct.IsCancellationRequested;

            if (!processedWorldData.ClientWorldData.IsCreated || !processedWorldData.ServerWorldData.IsCreated)
            {
                if(!ct.IsCancellationRequested)
                    Debug.LogError("An unexpected error occured during the raw traces processing. Did the tracing singletons get disposed during processing.");
                stop = true;
            }

            using var diffProcessingSteps = WorldData.ProcessDiff(processedWorldData, ct).GetEnumerator();
            if (!stop && !await ProcessTickEnumerable(diffProcessingSteps, frameBudgetMs, usePlaymodeSingletons, ct, progress, TracingProcessingStep.Diff, 0f, 1f))
                stop = true;
            stop |= ct.IsCancellationRequested;
            if (stop && !ct.IsCancellationRequested)
                Debug.LogError("An unexpected error occured during the processed traces diffing. Did the tracing singletons get disposed during diffing.");

            if (!stop)
            {
                s_Client.ProcessedWorldData = processedWorldData.ClientWorldData;
                s_Server.ProcessedWorldData = processedWorldData.ServerWorldData;
            }

            if (!stop && usePlaymodeSingletons)
            {
                if (!GetRefSingletonsFromWorlds(out playModeClientSingleton, out playModeServerSingleton))
                    stop = true;
                else
                {
                    playModeClientSingleton.ValueRW = s_Client;
                    playModeServerSingleton.ValueRW = s_Server;
                }
            }

            if(stop)
            {
                processedWorldData.ClientWorldData.Dispose();
                processedWorldData.ServerWorldData.Dispose();
                Config.Data.m_IsReadingRawTraces = false;
                return null;
            }

            IsProcessed = true;
            return processedWorldData;
        }

        static void RefreshPlayModeSingletons(RefRW<TracingDataSingleton> playModeClientSingleton, RefRW<TracingDataSingleton> playModeServerSingleton)
        {
            s_Client = playModeClientSingleton.ValueRO;
            s_Server = playModeServerSingleton.ValueRO;
        }

        /// <summary>
        /// Runs the tick processing enumerable while keeping the editor responsive (yields to the next frame
        /// when the frame budget is exceeded).
        /// </summary>
        static async Awaitable<bool> ProcessTickEnumerable(IEnumerator<float> steps, float frameBudgetMs, bool refreshPlayModeSingletons, CancellationToken ct, Action<TracingProcessingStep, float> progress, TracingProcessingStep step, float progressStart, float progressEnd)
        {
            var frameTimer = System.Diagnostics.Stopwatch.StartNew();
            while (steps.MoveNext())
            {
                if (ct.IsCancellationRequested)
                    return false;

                // m_ForceProcessingOverMultipleFrames is only used for testing the processing over multiple frames
                if (!m_ForceProcessingOverMultipleFrames && frameTimer.Elapsed.TotalMilliseconds < frameBudgetMs)
                    continue;

                // Only report progress when yielding: the UI can't repaint mid-loop, so per-step
                // updates would just waste cycles dirtying the progress bar.
                progress?.Invoke(step, Mathf.Lerp(progressStart, progressEnd, steps.Current));

                await Task.Delay(1, ct);
                if (ct.IsCancellationRequested)
                    return false;

                if (refreshPlayModeSingletons)
                {
                    if (!GetRefSingletonsFromWorlds(out var playModeClientSingleton, out var playModeServerSingleton))
                        return false;

                    RefreshPlayModeSingletons(playModeClientSingleton, playModeServerSingleton);
                }

                frameTimer.Restart();
            }

            progress?.Invoke(step, progressEnd);
            return true;
        }

        static bool GetRefSingletonsFromWorlds(out RefRW<TracingDataSingleton> playModeClientSingleton, out RefRW<TracingDataSingleton> playModeServerSingleton, bool refreshGhostNames = false)
        {
            playModeClientSingleton = default;
            playModeServerSingleton = default;
            foreach (var world in World.All)
            {
                if (world.IsServer())
                {
                    if (playModeServerSingleton.IsValid)
                    {
                        Debug.LogWarning("Tracing only supports one server world.");
                        return false;
                    }

                    if(world.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton)).TryGetSingletonRW(out playModeServerSingleton) && refreshGhostNames)
                    {
                        using var ghostsToCollect = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance>().WithAll<TracingNameCollected>()).ToEntityArray(Allocator.Temp);
                        TracingCollectGhostNamesSystem.UpdateGhostNames(world.EntityManager, playModeServerSingleton, ghostsToCollect);
                    }
                }
                else if (world.IsClient() && !world.IsThinClient())
                {
                    if (playModeClientSingleton.IsValid)
                    {
                        Debug.LogWarning("Tracing only supports one client world.");
                        return false;
                    }

                    if(world.EntityManager.CreateEntityQuery(typeof(TracingDataSingleton)).TryGetSingletonRW(out playModeClientSingleton) && refreshGhostNames)
                    {
                        using var ghostsToCollect = world.EntityManager.CreateEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<GhostInstance>().WithAll<TracingNameCollected>()).ToEntityArray(Allocator.Temp);
                        TracingCollectGhostNamesSystem.UpdateGhostNames(world.EntityManager, playModeClientSingleton, ghostsToCollect);
                    }
                }
            }
            return playModeClientSingleton is { IsValid: true, ValueRO: { IsCreated: true } }
                   && playModeServerSingleton is { IsValid: true, ValueRO: { IsCreated: true } };
        }

        /// <summary>
        /// Getter for the processed tracing data for both client and server.
        /// Will trigger processing of the raw trace data if it hasn't been processed yet, or if the singletons have been updated with new traces since last processing.
        /// After calling this method you must call <see cref="DisposeProcessedWorldData"/> to allow tracing to be resumed.
        /// <param name="ct">Cancellation token to interrupt the processing.</param>
        /// <param name="progress">Optional callback reporting the current processing step and its progress in [0,1].</param>
        /// <returns>Returns <see cref="ProcessedWorldsData"/> if successful, returns null otherwise.</returns>
        /// </summary>
        internal static async Awaitable<ProcessedWorldsData> GetProcessedWorldsData(CancellationToken ct = default, Action<TracingProcessingStep, float> progress = null)
        {
            await Awaitable.MainThreadAsync();
            if(!m_IsCreated)
                Init();
            if (!IsProcessed)
            {
               return await ProcessDiff(ct, progress);
            }
            progress?.Invoke(TracingProcessingStep.Diff, 1f);
            return new ProcessedWorldsData
            {
                ClientWorldData = s_Client.ProcessedWorldData,
                ServerWorldData = s_Server.ProcessedWorldData
            };
        }

        /// <summary>
        /// Dispose the processed worldsData and allow tracing to be resumed.
        /// </summary>
        internal static void DisposeProcessedWorldData()
        {
            if(!m_IsCreated)
                Init();
            var usePlaymodeSingletons = GetRefSingletonsFromWorlds(out var playModeClientSingleton, out var playModeServerSingleton);
            if (playModeClientSingleton is { IsValid: true, ValueRO: { IsCreated: true, ProcessedWorldData: { IsCreated: true } } } )
                playModeClientSingleton.ValueRW.DisposeProcessedWorldData();
            if (playModeServerSingleton is { IsValid: true, ValueRO: { IsCreated: true, ProcessedWorldData: { IsCreated: true } } } )
                playModeServerSingleton.ValueRW.DisposeProcessedWorldData();
            if(!usePlaymodeSingletons)
            {
                if(s_Client.ProcessedWorldData.IsCreated)
                    s_Client.ProcessedWorldData.Dispose();
                if(s_Server.ProcessedWorldData.IsCreated)
                    s_Server.ProcessedWorldData.Dispose();
            }
            IsProcessed = false;
            Config.Data.m_IsReadingRawTraces = false;
        }


        /// <summary>
        /// Dispose all tracing data and allow tracing to be restarted.
        /// </summary>
        public static void DisposeAllWorldData()
        {
            // First get updated world singletons if available.
            var usePlaymodeSingletons = GetRefSingletonsFromWorlds(out var playModeClientSingleton, out var playModeServerSingleton);
            if (playModeClientSingleton is { IsValid: true, ValueRO: { IsCreated: true } })
                playModeClientSingleton.ValueRW.Dispose();
            if (playModeServerSingleton is { IsValid: true, ValueRO: { IsCreated: true } })
                playModeServerSingleton.ValueRW.Dispose();
            if(!usePlaymodeSingletons)
            {
                if(s_Client.IsCreated)
                    s_Client.Dispose();
                if(s_Server.IsCreated)
                    s_Server.Dispose();
            }

            IsProcessed = false;
            s_Client = default;
            s_Server = default;
            ResetStateSaveSizes();
            Config.Data.m_IsReadingRawTraces = false;
        }

        internal static void Dispose()
        {
            if (!m_IsCreated)
                return;
            AllTypeDiffers.Instance.Dispose();
            DisposeAllWorldData();
            Config.Data.Dispose();
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
#endif
            m_IsCreated = false;
        }


        /// <summary>
        /// Setter for the client tracing data singletons.
        /// Should be called by the system that handles the client TracingDataSingleton
        /// before exiting playmode so we have an updated reference to the unprocessed traces outside playmode/ecs worlds.
        /// </summary>
        /// <param name="value">The client tracing data singleton</param>
        internal static void SetClientTracingDataSingleton(TracingDataSingleton value)
        {
            if(!m_IsCreated)
                Init();
            if (s_Client.IsCreated && s_Client.m_WorldID.value != value.m_WorldID.value)
            {
                s_Client.Dispose();
                ResetStateSaveSizes();
            }
            s_Client = value;
            IsProcessed = false;
        }


        /// <summary>
        /// Setter for the server tracing data singletons.
        /// Should be called by the system that handles the server TracingDataSingleton
        /// before exiting playmode so we have an updated reference to the unprocessed traces outside playmode/ecs worlds.
        /// </summary>//
        /// <param name="value">The server tracing data singleton</param>
        internal static void SetServerTracingDataSingleton(TracingDataSingleton value)
        {
            if(!m_IsCreated)
                Init();
            if (s_Server.IsCreated && s_Server.m_WorldID.value != value.m_WorldID.value)
            {
                s_Server.Dispose();
                ResetStateSaveSizes();
            }
            s_Server = value;
            IsProcessed = false;
        }
    }
}
