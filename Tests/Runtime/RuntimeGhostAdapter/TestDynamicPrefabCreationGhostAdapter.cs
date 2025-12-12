#if UNITY_EDITOR

using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    internal class TestDynamicPrefabCreationGhostAdapter
    {
        // this test can only run in editor side playmode, because of the dynamic prefab asset creation
        [Test(Description = "Sanity check for other tests")]
        public async Task TestDynamicPrefabCreationWithSubsceneHelper()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            await testWorld.ConnectAsync(enableGhostReplication: true);

            var prefab = SubSceneHelper.CreateGhostBehaviourPrefab(NetCodeTestWorld.k_GeneratedFolderBasePath, "Empty", typeof(EmptyBehaviour));
            var clone = GameObject.Instantiate(prefab);
            var ghost = clone.GetComponent<GhostAdapter>();
            Assert.That(clone.scene, Is.EqualTo(testWorld.m_EmptyScene));
            Assert.That(ghost.World, Is.EqualTo(testWorld.ServerWorld));
        }

        [Test(Description = "Sanity check for other tests")]
        public void TestDynamicPrefabCreationWithSubsceneHelper_Step2()
        {
            // check there's not remaining GOs from previous tests
            Assert.That(GameObject.FindObjectsByType<GhostAdapter>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
        }
    }
}
#endif
