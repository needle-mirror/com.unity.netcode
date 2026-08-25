using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Unity.NetCode.Tests
{
    internal class OwnershipTest
    {
        [Test(Description = "test there's an error if you try to access the owner without the authoring setting set")]
        public async Task TestAccessingOwner_WithNoAuthoringSetup()
        {
            await using var testWorld = new NetCodeTestWorld();
            await testWorld.SetupGameObjectTest();

            var prefabNoOwner = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("wrong ownerships");
            var authoringNoOwner = prefabNoOwner.GetComponent<GhostObject>();
            authoringNoOwner.HasOwner = false;
            Netcode.RegisterPrefab(prefabNoOwner.gameObject);
            var server = GameObject.Instantiate(prefabNoOwner);
            await testWorld.TickAsync();

            // Test
            LogAssert.Expect(LogType.Error, $"Trying to get the owner of a ghost that wasn't setup with ownership. Please update your {nameof(GhostObject)} component to reflect this.");
            var _ = server.Ghost.OwnerNetworkId;

            LogAssert.Expect(LogType.Error, $"Trying to set the owner of a ghost that wasn't setup with ownership. Please update your {nameof(GhostObject)} component to reflect this.");
            server.Ghost.OwnerNetworkId = new NetworkId { Value = 123 };

            var prefabWithOwner = GhostObjectUtils.CreatePredictionCallbackHelperPrefab("correct ownerships", autoRegister: false);
            var authoringWithOwner = prefabWithOwner.GetComponent<GhostObject>();
            authoringWithOwner.HasOwner = true;
            Netcode.RegisterPrefab(prefabWithOwner.gameObject);
            var serverWithOwner = GameObject.Instantiate(prefabWithOwner);
            await testWorld.TickAsync();
            serverWithOwner.Ghost.OwnerNetworkId = new NetworkId { Value = 123 };
            Assert.AreEqual(123, serverWithOwner.Ghost.OwnerNetworkId.Value);
        }
    }
}
