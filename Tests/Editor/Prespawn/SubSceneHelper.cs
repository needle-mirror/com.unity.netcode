using System.IO;
using System.Linq;
using Unity.Entities;
using Unity.NetCode.Tests;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Hash128 = UnityEngine.Hash128;

namespace Unity.NetCode.PrespawnTests
{
    public class SubSceneHelper
    {
        static public GameObject CreatePrefabVariant(GameObject prefab, string variantName = null)
        {
            //Use default name with space
            if (string.IsNullOrEmpty(variantName))
                variantName = $"{prefab.name} Variant.prefab";
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var variantAssetPath = $"{Path.GetDirectoryName(AssetDatabase.GetAssetPath(prefab))}/{variantName}";
            var prefabVariant = PrefabUtility.SaveAsPrefabAsset(instance, variantAssetPath);
            return prefabVariant;
        }

        //Create a row for each prefab in the list along the X-axis by spacing each ghost 2 mt apart.
        //Each row is offset along the Z-Axis by 2.0 mt
        static public Scene CreateSubSceneWithPrefabs(string scenePath, string subSceneName,
            GameObject[] prefabs, int countPerPrefabs, float startZOffset=0.0f)
        {
            var subScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(subScene);
            subScene.isSubScene = true;
            subScene.name = subSceneName;

            float zoffset = startZOffset;
            foreach (var prefab in prefabs)
            {
                for (int i = 0; i < countPerPrefabs; ++i)
                {
                    var obj = (GameObject) PrefabUtility.InstantiatePrefab(prefab, subScene);
                    obj.transform.SetPositionAndRotation(new Vector3(i*2.0f, 0.0f, zoffset), Random.rotation);
                }
                zoffset += 2.0f;
            }
            EditorSceneManager.SaveScene(subScene, $"{scenePath}/{subSceneName}.unity");
            EditorSceneManager.CloseScene(subScene, false);
            return subScene;
        }

        //Create a xz grid of object with 1 mt spacing starting from offset startOffset.
        static public Scene CreateSubScene(string scenePath, string subSceneName, int numRows, int numCols, GameObject prefab,
            Vector3 startOffsets)
        {
            //Create the sub and parent scenes
            var subScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(subScene);
            subScene.isSubScene = true;
            subScene.name = subSceneName;
            if (prefab != null)
            {
                //Create a bunch of gameobject in the subscene
                float xOffset = startOffsets.x;
                float zOffset = startOffsets.z;
                for (int i = 0; i < numRows; ++i)
                {
                    for (int j = 0; j < numCols; ++j)
                    {
                        var obj = (GameObject) PrefabUtility.InstantiatePrefab(prefab, subScene);
                        obj.transform.SetPositionAndRotation(new Vector3(j + xOffset, startOffsets.y, i + zOffset),
                            Quaternion.identity);
                    }
                }
            }
            EditorSceneManager.SaveScene(subScene, $"{scenePath}/{subSceneName}.unity");
            EditorSceneManager.CloseScene(subScene, false);
            return subScene;
        }

        static public SubScene AddSubSceneToParentScene(Scene parentScene, Scene subScene)
        {
            SceneManager.SetActiveScene(parentScene);
            var subSceneGo = new GameObject("SubScene");
            subSceneGo.SetActive(false);
            var subSceneComponent = subSceneGo.AddComponent<SubScene>();
            var subSceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(subScene.path);
            subSceneComponent.SceneAsset = subSceneAsset;
            subSceneComponent.AutoLoadScene = false;
            subSceneGo.SetActive(true);
            EditorSceneManager.MarkSceneDirty(parentScene);
            EditorSceneManager.SaveScene(parentScene, parentScene.path);
            return subSceneComponent;
        }

        static public Scene CreateEmptyScene(string scenePath, string name)
        {
            //Create the parent scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = name;
            EditorSceneManager.SaveScene(scene, $"{scenePath}/{name}.unity");
            if (!Directory.Exists(Path.Combine(scenePath, name)))
                Directory.CreateDirectory(Path.Combine(scenePath, name));
            return scene;
        }

        static public GameObject CreateSimplePrefab(string path, string name, params System.Type[] componentTypes)
        {
            //Create a prefab
            GameObject go = new GameObject(name, componentTypes);
            return CreatePrefab(path, go);
        }

        static public GameObject CreatePrefab(string path, GameObject go)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{path}/{go.name}.prefab");
            Object.DestroyImmediate(go);

            return prefab;
        }

        //Load into the terget world a list of subscene.
        //if the subScenes list is empty, a list of all the SubScene gameobjects in the active scene is retrieved
        //and loaded in the target world instead.
        static public void LoadSubScene(World world, params SubScene[] subScenes)
        {
            if(subScenes.Length == 0)
                subScenes = Object.FindObjectsOfType<SubScene>();
            var sceneSystem = world.GetExistingSystem<SceneSystem>();
            var sceneEntities = new Entity[subScenes.Length];
            //Retry to load the scene a couple of time before give up
            var retry = 3;
            LogAssert.ignoreFailingMessages = true;
            while(--retry > 0)
            {
                System.Threading.Thread.Sleep(100);
                for(int i=0;i<subScenes.Length;++i)
                {
                    sceneEntities[i] = sceneSystem.GetSceneEntity(subScenes[i].SceneGUID);
                    if(sceneEntities[i] == null || !sceneSystem.IsSceneLoaded(sceneEntities[i]))
                    {
                        sceneEntities[i] = sceneSystem.LoadSceneAsync(subScenes[i].SceneGUID, new SceneSystem.LoadParameters
                        {
                            Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn
                        });
                    }
                }

                bool loaded = false;
                for(int i=0;i<32 && !loaded;++i)
                {
                    world.Update();
                    loaded = (sceneEntities.All(s => sceneSystem.IsSceneLoaded(s)));
                }
                if(loaded)
                    break;
            }
            LogAssert.ignoreFailingMessages = false;
            if(retry == 0)
                throw new System.Exception("Cannot load some subscenes");
        }

        static public void ResolveScenes(NetCodeTestWorld testWorld, float frameTime, int maxTicks, params SubScene[] subScenes)
        {
            bool AreEntitiesCreated(Hash128 sceneGuid)
            {
                bool allLoaded = !testWorld.ServerWorld.EntityManager
                    .CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>()).IsEmpty;
                for (int c = 0; c < testWorld.ClientWorlds.Length; ++c)
                {
                    allLoaded &= !testWorld.ClientWorlds[c].EntityManager
                        .CreateEntityQuery(ComponentType.ReadOnly<SubSceneWithPrespawnGhosts>()).IsEmpty;
                }

                return allLoaded;
            }

            void LoadScene(Hash128 subSceneGUID)
            {
                var retry = 3;
                while(--retry > 0)
                {
                    var sceneSystem = testWorld.ServerWorld.GetExistingSystem<SceneSystem>();
                    sceneSystem.LoadSceneAsync(subSceneGUID, new SceneSystem.LoadParameters
                    {
                        Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.DisableAutoLoad,
                    });
                    for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                    {
                        testWorld.ClientWorlds[i].GetExistingSystem<SceneSystem>().LoadSceneAsync(subSceneGUID,
                            new SceneSystem.LoadParameters
                            {
                                Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.DisableAutoLoad,
                            });
                    }
                    for (int i = 0; i < maxTicks; ++i)
                    {
                        testWorld.Tick(frameTime);
                        if (AreEntitiesCreated(subSceneGUID))
                            return;
                    }
                    System.Threading.Thread.Sleep(100);
                }
                if(retry == 0)
                    throw new System.Exception("Cannot load some subscenes");
            }

            foreach (var sub in subScenes)
            {
                LoadScene(sub.SceneGUID);
            }
        }

        static public Entity LoadSubSceneAsync(World world, in NetCodeTestWorld testWorld, Hash128 subSceneGUID, float frameTime, int maxTicks)
        {
            var sceneSystem = world.GetExistingSystem<SceneSystem>();
            var retry = 3;
            while(--retry > 0)
            {
                System.Threading.Thread.Sleep(100);
                var subSceneEntity = sceneSystem.LoadSceneAsync(subSceneGUID, new SceneSystem.LoadParameters
                {
                    Flags = SceneLoadFlags.BlockOnImport,
                });
                for (int i = 0; i < maxTicks; ++i)
                {
                    testWorld.Tick(frameTime);
                    if(sceneSystem.IsSceneLoaded(subSceneEntity))
                        return subSceneEntity;
                }
            }
            if(retry == 0)
                throw new System.Exception("Cannot load some subscenes");
            return Entity.Null;
        }

        //Load the specified subscene in the both clients and server world.
        //if the subScenes list is empty, a list of all the SubScene gameobjects in the active scene is retrieved
        //and loaded in the worlds.
        static public void LoadSubSceneInWorlds(in NetCodeTestWorld testWorld, params SubScene[] subScenes)
        {
            SubSceneHelper.LoadSubScene(testWorld.ServerWorld, subScenes);
            for (int i = 0; i < testWorld.ClientWorlds.Length; ++i)
                SubSceneHelper.LoadSubScene(testWorld.ClientWorlds[i], subScenes);
        }
    }
}
