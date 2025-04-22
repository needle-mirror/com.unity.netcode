using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Have netcode automatically manage thin clients for you by assigning <see cref="NumThinClientsRequested"/>.
    /// </summary>
    public class AutomaticThinClientWorldsUtility
    {
        /// <summary>Set the desired number of thin client worlds.</summary>
        /// <remarks>
        /// If null (the default), it'll use <see cref="MultiplayerPlayModePreferences.RequestedNumThinClients"/> in the editor, else 0.
        /// Worlds are only created in builds if you hook up <see cref="UpdateAutomaticThinClientWorlds"/>.
        /// </remarks>
        public static int? NumThinClientsRequested;

        /// <summary>
        /// The frequency with which we should create the thin client worlds (in hertz i.e. worlds per second).
        /// 0 denotes 'create all immediately'.
        /// If null (the default), it'll use <see cref="MultiplayerPlayModePreferences.ThinClientCreationFrequency"/> in the editor, else 0.
        /// </summary>
        public static float? CreationFrequency;

        /// <summary>
        /// The world to use for data injection (like to know which sub-scene(s) to load).
        /// If null, we'll try to use any existing client or server worlds, found via <see cref="ClientServerBootstrap.ClientWorld"/> etc.
        /// </summary>
        public static World ReferenceWorld;

        /// <summary>
        ///     If your automatic thin clients need custom initialization during bootstrap (e.g. due to custom scene management settings),
        ///     modify this delegate. Uses <see cref="DefaultBootstrapThinClientWorldInitialization"/> by default.
        ///     Set to null to disable the bootstrap initialization feature.
        /// </summary>
        public static ThinClientWorldInitializationDelegate BootstrapInitialization = DefaultBootstrapThinClientWorldInitialization;

        /// <summary>
        ///     If your automatic thin clients need custom initialization at runtime (e.g. due to custom scene management settings),
        ///     modify this delegate. Uses <see cref="DefaultRuntimeThinClientWorldInitialization"/> by default.
        ///     Set to null to disable the runtime initialization feature.
        /// </summary>
        public static ThinClientWorldInitializationDelegate RuntimeInitialization = DefaultRuntimeThinClientWorldInitialization;

        /// <summary>Denotes if automatic bootstrap thin client creation is enabled.</summary>
        public static bool IsBootstrapInitializationEnabled => BootstrapInitialization != null;

        /// <summary>Denotes if automatic RUNTIME thin client creation is enabled.</summary>
        public static bool IsRuntimeInitializationEnabled => RuntimeInitialization != null;

        /// <summary>
        /// A list of all thin client worlds created by (and managed by) the netcode package itself.
        /// If you add a thin client to this list, netcode will take ownership of it.
        /// This list prevents the netcode package from deleting your thin client worlds.
        /// </summary>
        public static List<World> AutomaticallyManagedWorlds { get; } = new();

        private static double s_LastSpawnRealtime;

        /// <summary>Delegate for <see cref="DefaultBootstrapThinClientWorldInitialization"/> and
        /// <see cref="DefaultRuntimeThinClientWorldInitialization"/>.</summary>
        /// <param name="referenceWorld">The world to reference when creating this one (for the purposes of scene loading etc.).</param>
        /// <returns>The newly created world, otherwise null.</returns>
        public delegate World ThinClientWorldInitializationDelegate(World referenceWorld);

        /// <summary>
        /// Resets the utility to starting values via <see cref="RuntimeInitializeOnLoadMethodAttribute"/>
        /// and <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            NumThinClientsRequested = default;
            CreationFrequency = default;
            s_LastSpawnRealtime = default;
            ReferenceWorld = default;
            BootstrapInitialization = DefaultBootstrapThinClientWorldInitialization;
            RuntimeInitialization = DefaultRuntimeThinClientWorldInitialization;
            CleanupWorlds();
        }

        /// <summary>Utility to remove all stale worlds from the list.</summary>
        /// <returns>Num removed.</returns>
        public static int CleanupWorlds() => AutomaticallyManagedWorlds.RemoveAll(x => x == null || !x.IsCreated);

        /// <summary>
        /// By default, thin clients created during the bootstrap will automatically be injected with the loaded scenes sub-scenes.
        /// Thus, we do not need to do anything custom.
        /// </summary>
        /// <param name="referenceWorld">The world to reference when creating this one (for the purposes of scene loading etc.).</param>
        /// <returns>The newly created world, otherwise null.</returns>
        public static World DefaultBootstrapThinClientWorldInitialization(World referenceWorld)
        {
            return ClientServerBootstrap.CreateThinClientWorld();
        }

        /// <inheritdoc cref="RuntimeInitialization"/>
        /// <param name="referenceWorld">The world to reference when creating this one (for the purposes of scene loading etc.).</param>
        /// <returns>The newly created world, otherwise null.</returns>
        public static World DefaultRuntimeThinClientWorldInitialization(World referenceWorld)
        {
            if (referenceWorld?.IsCreated != true)
            {
                UnityEngine.Debug.LogError($"Cannot properly initialize ThinClientWorld as referenceWorld:{referenceWorld} is null, so no idea which scenes to load.");
                return null;
            }

            var newThinClientWorld = ClientServerBootstrap.CreateThinClientWorld();
            using var serverWorldScenesQuery = referenceWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RequestSceneLoaded>(), ComponentType.ReadOnly<SceneReference>());
            var serverWorldScenes = serverWorldScenesQuery.ToComponentDataArray<SceneReference>(Allocator.Temp);
            for (int i = 0; i < serverWorldScenes.Length; i++)
            {
                var desiredGoSceneReferenceGuid = serverWorldScenes[i];
                SceneSystem.LoadSceneAsync(newThinClientWorld.Unmanaged,
                    desiredGoSceneReferenceGuid.SceneGUID,
                    new SceneSystem.LoadParameters
                    {
                        Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn,
                        AutoLoad = true,
                    });
            }
            return newThinClientWorld;
        }

        /// <summary>
        /// Use this method when inside the <see cref="ClientServerBootstrap.Initialize"/> flow.
        /// </summary>
        /// <remarks>
        /// This has to exist because Entities/Netcode uses a fast-path, where it loads the entity scene data (for all
        /// loaded scenes) once, and then auto-injects said data into all appropriate bootstrapping worlds.
        /// </remarks>
        public static void BootstrapThinClientWorlds()
        {
            if (!IsBootstrapInitializationEnabled) return;
            var requestedNumThinClients = NumThinClientsRequested ?? 0;
#if UNITY_EDITOR
            if(NumThinClientsRequested == null) requestedNumThinClients = MultiplayerPlayModePreferences.RequestedNumThinClients;
#endif
            for (var i = 0; i < requestedNumThinClients; i++)
            {
                var newThinClientWorld = BootstrapInitialization(ReferenceWorld);
                if (newThinClientWorld != null && newThinClientWorld.IsCreated)
                    AutomaticallyManagedWorlds.Add(newThinClientWorld);
            }

        }

        /// <summary>
        /// If you use this feature, call this method in a <see cref="MonoBehaviour"/> Update method.
        /// It'll apply the current configured values.
        /// </summary>
        /// <returns>True if any worlds were created or destroyed.</returns>
        public static bool UpdateAutomaticThinClientWorlds()
        {
            var requestedNumThinClients = NumThinClientsRequested ?? 0;
            var instantiationFrequency = CreationFrequency ?? 0f;
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isPaused)
                return false;
            // Creating & destroying thin clients can be expensive, so prevent changes while editing the value.
            if (UnityEditor.EditorGUIUtility.editingTextField)
                return false;
            if(NumThinClientsRequested == null) requestedNumThinClients = MultiplayerPlayModePreferences.RequestedNumThinClients;
            if(CreationFrequency == null) instantiationFrequency = MultiplayerPlayModePreferences.ThinClientCreationFrequency;
#endif
            int maxAllowedToSpawn;
            if (instantiationFrequency == 0)
            {
                maxAllowedToSpawn = int.MaxValue;
            }
            else
            {
                maxAllowedToSpawn = 1;
                var elapsedSecondsSinceLastSpawn = Time.realtimeSinceStartupAsDouble - s_LastSpawnRealtime;
                if (elapsedSecondsSinceLastSpawn < 1d / instantiationFrequency)
                    maxAllowedToSpawn = 0;
            }
            UpdateAutomaticThinClientWorldsImmediate(ReferenceWorld, requestedNumThinClients, maxAllowedToSpawn, out var didCreateOrDestroy);
            return didCreateOrDestroy;
        }

        /// <summary>
        /// Creates and/or Disposes thin client worlds until the final count is equal to <see cref="targetThinClientCount"/>.
        /// </summary>
        /// <param name="referenceWorld">The desired world to use as a reference. If null, we'll try to use any existing client or server worlds.</param>
        /// <param name="targetThinClientCount">The desired final count of thin clients.</param>
        /// <param name="maxAllowedSpawn">Rate limiting feature. Worlds are disposed immediately, but only instantiated at this frequency.</param>
        /// <param name="didCreateOrDestroy">True if worlds were created or destroyed.</param>
        /// <returns>The list of successfully created worlds, otherwise default.</returns>
        public static NativeList<WorldUnmanaged> UpdateAutomaticThinClientWorldsImmediate(World referenceWorld, int targetThinClientCount, int maxAllowedSpawn, out bool didCreateOrDestroy)
        {
            referenceWorld ??= ClientServerBootstrap.ServerWorld ?? ClientServerBootstrap.ClientWorld;
            didCreateOrDestroy = false;

            // Dispose if too many:
            didCreateOrDestroy |= CleanupWorlds() > 0;
            var autoWorlds = AutomaticallyManagedWorlds;
            while(autoWorlds.Count > targetThinClientCount)
            {
                var index = autoWorlds.Count - 1;
                var world = autoWorlds[index];
                autoWorlds.RemoveAt(index);
                if (world.IsCreated)
                    world.Dispose();
                didCreateOrDestroy = true;
            }

            // Create new:
            var maxAllowedToSpawn = math.clamp(targetThinClientCount - autoWorlds.Count, 0, maxAllowedSpawn);
            NativeList<WorldUnmanaged> newWorlds = default;
            var runtimeCreationIsEnabled = RuntimeInitialization != null;
            if (runtimeCreationIsEnabled && referenceWorld != null && referenceWorld.IsCreated)
            {
                newWorlds = new NativeList<WorldUnmanaged>(maxAllowedToSpawn, Allocator.Temp);
                for(var newIdx = 0; newIdx < maxAllowedToSpawn; newIdx++)
                {
                    didCreateOrDestroy = true;
                    var newThinClientWorld = RuntimeInitialization(referenceWorld);
                    if (newThinClientWorld != null && newThinClientWorld.IsCreated)
                    {
                        autoWorlds.Add(newThinClientWorld);
                        newWorlds.Add(newThinClientWorld.Unmanaged);
                    }
                    s_LastSpawnRealtime = Time.realtimeSinceStartupAsDouble;
                }
            }
            return newWorlds;
        }
    }
}
