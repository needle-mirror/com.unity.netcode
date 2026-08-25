using System;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.NetCode.Tests
{
    internal class GhostObjectPrefabHelper
    {
        public static GameObject CreateGhostBehaviourPrefab(string path, string name, params Type[] componentTypes)
        {
            return CreateGhostBehaviourPrefab(path, name, true, componentTypes);
        }

        /// <summary>
        /// Creates GhostBehaviour prefab with proper prefab ref tracking setup.
        /// </summary>
        /// <param name="path">Ex: Assets/Tests</param>
        /// <param name="name">Ex: MyPrefab (no extension .prefab)</param>
        /// <param name="autoRegister">In order to modify the generated prefab, you need to prevent it from registering automatically, modify it, then register it yourself using <see cref="Netcode.RegisterPrefab(GameObject,World)"/></param>
        /// <param name="componentTypes">Asserts one of the provided component is a GhostBehaviour. No need to add GhostObject, that should be added automatically by GhostBehaviour RequireComponent</param>
        /// <returns></returns>
        public static GameObject CreateGhostBehaviourPrefab(string path, string name, bool autoRegister = true, params Type[] componentTypes)
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
            Assert.That(hasGhostBehaviour);
            var go = new GameObject(name);
            go.SetActive(false); // to prevent Awake from triggering initialization and logging null refs
            foreach (var type in componentTypes)
            {
                go.AddComponent(type);
            }
            var ghost = go.GetComponent<GhostObject>();

            ghost.SkipAutomaticPrefabRegistration = !autoRegister;
            ghost.SingleWorldHostInterpolationSmoothing = SingleWorldHostInterpolationMode.Disabled; // most tests assume the value is the predicted one. Shouldn't add smoothing that'd mess with values there
            GameObject prefab;
            if (!Application.isEditor)
            {
                // With builds, we can't create asset prefabs dynamically. So we artificially create it here.
                ghost.InitializeAsPrefab();
                prefab = go;
                if (autoRegister)
                    Netcode.RegisterPrefab(go); // trigger this manually as the IsPostProcessing skipped the OnEnable triggered right above
                GameObject.DontDestroyOnLoad(go); // make sure the prefab isn't destroyed when we switch/unload scenes
            }
            else
            {
#if UNITY_EDITOR
                prefab = SubSceneHelper.CreatePrefab(path, go);
#else
                throw new Exception("Shouldn't be here");
#endif
            }
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
