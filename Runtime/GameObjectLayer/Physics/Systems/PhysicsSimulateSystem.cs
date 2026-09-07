using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Netcode.NetcodeTime;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;
using Unity.Transforms;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Assertions;

namespace Unity.Netcode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [CreateAfter(typeof(NetDebugSystem))]
    internal partial class GameObjectPhysicsSimulateSystem : SystemBase
    {
        ProfilerMarker m_PredictMarker = new ProfilerMarker($"{nameof(GameObjectPhysicsSimulateSystem)}.Predict");

        internal delegate PhysicsScene GetPhysicsSceneToSimulateDelegate(World forWorld);
        [AutoStaticsCleanup]
        internal static GetPhysicsSceneToSimulateDelegate GetPhysicsSceneCallback;

        SimulationMode m_OldMode = SimulationMode.FixedUpdate;
        EntityQuery m_RigidbodyQuery;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_RigidbodyQuery = World.EntityManager.CreateEntityQuery(typeof(GhostRigidbodyData), typeof(GhostRigidbodyGameObjectTracker));

            m_OldMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script; // This means if users want an offline scene, they'll need to call Simulate on it manually. Unity doesn't allow Physics.Simulate + normal FixedUpdate simulation.
            RequireForUpdate<GhostRigidbodyData>();
#if UNITY_EDITOR
            var netDebug = SystemAPI.GetSingleton<NetDebug>();
            // Those logs should only appear if log level is set to max noise level (debug). These are useful warnings/guidance when debugging issues with netcode.
            if (GetSolverType() != 0 && netDebug.LogLevel <= NetDebug.LogLevelType.Debug)
                netDebug.LogWarning("[Netcode Debug] [Predicted GameObject Physics] The solver type Temporal Gauss Seidel usually doesn't produce deterministic enough results for prediction. Please update your solver type in your project settings.");
            if (!GetIsDeterminism() && netDebug.LogLevel <= NetDebug.LogLevelType.Debug)
                netDebug.LogWarning("[Netcode Debug] [Predicted GameObject Physics] Enhanced Determinism is set to false in your physics settings. It's recommended to set it to true in a predicted netcode context.");
#endif
        }

        protected override void OnUpdate()
        {
            using var _ = m_PredictMarker.Auto();
            var time = SystemAPI.GetSingleton<NetworkTime>();
            if (time.IsPartialTick) return;

            var deltaTime = SystemAPI.Time.DeltaTime;

            // shouldAutoWake is set to false client side before the syncing system from entity to GameObject executes, but we need this server side too because of the SyncTransform below which wakes things up
            foreach (var (data, goTracker, ghostTransformData) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker, LocalTransform>())
            {
                Rigidbody rigidbody = goTracker.Rigidbody.Value;
                // rigidbody.shouldAutoWake = false; // TODO-next@physicsSleepWake this should be a global state?
                // TODO-next@physicsSleepWake ^^^
            }

            // This needs to happen before we reenable AutoWake on each rigidbody
            // GO to RB
            {
                // TODO-next@physicsPerf there's some jobs overhead here... would be great if we had some APIs engine side to force schedule those jobs, there's idle time right now

                using var rbTrackers = m_RigidbodyQuery.ToComponentDataArray<GhostRigidbodyGameObjectTracker>(Allocator.Temp);
                for (int i = 0; i < rbTrackers.Length; i++)
                {
                    Rigidbody rb = rbTrackers[i].Rigidbody.Value;
                    rb.gameObject.transform.GetPositionAndRotation(out var pos, out var rot);
                    rb.Move(pos, rot);
                }

                // TODO-next@physicsPerf, looks like doing this is different from SyncTransform. That's why tests keep passing when they shouldn't. Using this for now and will use Physics.SyncTransform once it's fixed. Make sure to come back to tests with multiple worlds, they will need to be adapted to sync individual worlds
                // Physics.SyncTransforms(); // assumes auto sync transform is set to false (set in prefixedupdate)

                //TODO-next@physicsPerf since we control inputs, we could detect if there's zero inputs and zero user side logic that could influence the sim and then just skip transform syncing in those ticks?
            }

            // this needs to be true for the remainder of the frame so that if users move transforms in the scene view server side, objects are woken appropriately
            foreach (var (data, goTracker, ghostTransformData) in SystemAPI.Query<GhostRigidbodyData, GhostRigidbodyGameObjectTracker, LocalTransform>())
            {
                Rigidbody rigidbody = goTracker.Rigidbody.Value;
                // rigidbody.shouldAutoWake = true; // TODO-next@physicsSleepWake
            }

            PhysicsScene currentScene;
            if (GetPhysicsSceneCallback == null)
            {
                currentScene = Physics.defaultPhysicsScene;
            }
            else
            {
                // useful for tests when running with binary world (so one scene per world). Internal right now, but could be useful for binary world setup for users in the future as well
                currentScene = GetPhysicsSceneCallback(World);
            }
            currentScene.RunSimulationStages(deltaTime, SimulationStage.PrepareSimulation, SimulationOption.None); // TODO-release@physics this could be skipped if users can tell us "I'm not touching anything this tick"
            currentScene.RunSimulationStages(deltaTime, SimulationStage.RunSimulation, SimulationOption.None);
            currentScene.RunSimulationStages(deltaTime, SimulationStage.PublishSimulationResults, SimulationOption.None); // TODO-release@physics can skip this? or at least the publishing of contact events? If we make this optional, this could help skip some of the sim cost.
        }

        protected override void OnDestroy()
        {
            int netcodeWorldCount = 0;
            foreach (var netWorld in Netcode.GetNetcodeWorlds())
            {
                netcodeWorldCount++;
            }

            if (netcodeWorldCount == 1)
            {
                // Only set this when this is the last world remaining
                Physics.simulationMode = m_OldMode;
            }
        }

#if UNITY_EDITOR
        const string k_SolverTypePropertyKey = "m_SolverType";
        const string k_DeterminismPropertyKey = "m_EnableEnhancedDeterminism";

        static SerializedObject GetPhysicsSettings()
        {
            var found = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
            if (found == null || found.Length == 0)
                return null;

            return new SerializedObject(found[0]);
        }
        /// <summary>
        /// Since there's no API to get this, reading it ourselves to validate at the recommendation of @Alexandru
        /// </summary>
        /// <returns>0 for Projected Gauss Seidel and 1 for Temporal Gauss Seidel</returns>
        internal static int GetSolverType()
        {
            var serializedObject = GetPhysicsSettings();
            if (serializedObject == null)
            {
                Debug.LogWarning("couldn't find physics settings, defaulting to 0 for solver type check.");
                return 0;
            }
            var solverType = serializedObject.FindProperty(k_SolverTypePropertyKey);
            return solverType.intValue;
        }

        /// <summary>
        /// Gets the setting for physics determinism. Note that this only gets the setting, not whether any active Physics World (scene) had it enabled when they themselves were created. A discrepancy can happen if you set that setting after scene creation.
        /// </summary>
        /// <returns></returns>
        internal static bool GetIsDeterminism()
        {
            var serializedObject = GetPhysicsSettings();
            if (serializedObject == null)
            {
                Debug.LogWarning("couldn't find physics settings, defaulting to false for determinism check.");
                return false;
            }

            var determinismEnabled = serializedObject.FindProperty(k_DeterminismPropertyKey);
            return determinismEnabled.boolValue;
        }

        /// <summary>
        /// Saves on disk the determinism setting in physics. This needs to happen before the target Physics World is created (so before scene creation)
        /// </summary>
        /// <param name="value"></param>
        /// <exception cref="FileNotFoundException"></exception>
        internal static void SetDeterminism(bool value)
        {
            var serializedObject = GetPhysicsSettings();
            if (serializedObject == null)
            {
                throw new FileNotFoundException("Couldn't find physics settings to set determinism checkbox");
            }
            var determinismEnabled = serializedObject.FindProperty(k_DeterminismPropertyKey);
            determinismEnabled.boolValue = value;
            serializedObject.ApplyModifiedProperties();
        }
#endif
    }
}
