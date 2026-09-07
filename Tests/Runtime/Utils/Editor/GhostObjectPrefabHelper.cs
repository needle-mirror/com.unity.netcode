using System;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Unity.Netcode.Tests
{
    internal class GhostObjectPrefabHelper
    {
        /*
         * Overloads to avoid having to change existing code as parameters were added
         */
        public static GameObject CreateGhostBehaviourPrefab(string path, string name, params Type[] componentTypes)
        {
            return CreateGhostBehaviourPrefab(path, name, true, false, componentTypes);
        }
        public static GameObject CreateGhostBehaviourPrefab(string path, string name, bool autoRegister, params Type[] componentTypes)
        {
            return CreateGhostBehaviourPrefab(path, name, autoRegister, false, componentTypes);
        }


        /// <summary>
        /// Creates GhostBehaviour prefab with proper prefab ref tracking setup.
        /// </summary>
        /// <param name="path">Ex: Assets/Tests</param>
        /// <param name="name">Ex: MyPrefab (no extension .prefab)</param>
        /// <param name="autoRegister">In order to modify the generated prefab, you need to prevent it from registering automatically, modify it, then register it yourself using <see cref="Netcode.RegisterPrefab(GameObject,World)"/></param>
        /// <param name="componentTypes">List of components to add to the created prefab. At least one component needs to be a GhostBehaviour</param>
        /// <param name="isNested">When true, the given componentTypes will be added on a child GameObject rather than the root</param>
        /// <returns></returns>
        public static GameObject CreateGhostBehaviourPrefab(string path, string name, bool autoRegister = true, bool isNested = false, params Type[] componentTypes)
        {
            bool hasGhostBehaviour = false;
            foreach (var type in componentTypes)
            {
                if (type.IsSubclassOf(typeof(GhostBehaviour)))
                {
                    hasGhostBehaviour = true;
                    break;
                }
            }
            Assert.That(hasGhostBehaviour, Is.True, $"{nameof(CreateGhostBehaviourPrefab)} failed! No ghost behaviour was found.");

            var go = new GameObject(name);
            // Disable the prefab to prevent Awake from triggering during initialization and logging null refs
            go.SetActive(false);
            // Ensure the GhostObject is added first
            var ghost = go.AddComponent<GhostObject>();

            // setup which GameObject to add the components to
            var componentHolder = go;

            if (isNested)
            {
                var child = new GameObject($"{name}_Child");
                child.transform.SetParent(go.transform);
                componentHolder = child;
            }

            // Add the components
            foreach (var type in componentTypes)
            {
                componentHolder.AddComponent(type);
            }

            ghost.SkipAutomaticPrefabRegistration = !autoRegister;
            ghost.SingleWorldHostInterpolationSmoothing = SingleWorldHostInterpolationMode.Disabled; // most tests assume the value is the predicted one. Shouldn't add smoothing that'd mess with values there
            GameObject prefab;

            // With builds, we can't create asset prefabs dynamically. So we artificially create it here.
            if (!Application.isEditor)
            {
                ghost.InitializeAsPrefab();
                prefab = go;
                if (autoRegister)
                    Netcode.RegisterPrefab(go); // trigger this manually as the IsPostProcessing skipped the OnEnable triggered right above
                Object.DontDestroyOnLoad(go); // make sure the prefab isn't destroyed when we switch/unload scenes
            }
            // In order to test the AssetPostProcessor auto registration, we want to create real assets in editor tests.
            else
            {
#if UNITY_EDITOR
                // This path will run the asset post-processer which will add a GhostPrefabReference ScriptableObject to the GhostObject
                prefab = SubSceneHelper.CreatePrefab(path, go);
#else
                throw new Exception("Shouldn't be here");
#endif
            }
            // Setting the prefab to active will run OnEnable on the GhostPrefabReference which in editor tests will automatically register the prefab
            // This will also run Awake and OnEnable on all components added to the GameObject.
            // If an exception is being thrown on this line only in builds, change the above if-check to run the build code in the editor tests.
            // Exceptions from Awake functions are handled differently in editor and in builds. You'll get better error messages in the editor.
            prefab.SetActive(true);
            foreach (var world in World.All)
            {
                // check all possible worlds for the prefab that was just created and reenable it there too, since normal prefab creation would think the prefab is inactive
                // and automatically set the associated entity disabled too
                var link = GhostEntityMapping.LookupEntityReferencePrefab(prefab.GetEntityId(), world.Unmanaged);
                if (link.WasInitialized)
                {
                    link.World.EntityManager.SetEnabled(link.Entity, true);
                    // also have to override this in tests, since prefab registration will have had the wrong value during registration
                    var pendingGameObjectSpawn = link.World.EntityManager.GetComponentData<PendingClientGameObjectSpawn>(link.Entity);
                    pendingGameObjectSpawn.ShouldBeActive = true;
                    link.World.EntityManager.SetComponentData(link.Entity, pendingGameObjectSpawn);
                }
            }
            return prefab;
        }
    }
}
