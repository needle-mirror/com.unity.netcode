using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Tests
{
    [GhostComponentVariation(typeof(HybridComponentWeWillOverride))]
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct HybridComponentWeWillOverrideVariant
    {
    }

    public class HybridComponentWeWillOverrideConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentObject(entity, gameObject.GetComponent<HybridComponentWeWillOverride>());
        }
    }
    public class ServerComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new ServerComponentData {Value = 1});
        }
    }
    public class ClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new ClientComponentData {Value = 1});
        }
    }
    public class InterpolatedClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new InterpolatedClientComponentData {Value = 1});
        }
    }
    public class PredictedClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new PredictedClientComponentData {Value = 1});
        }
    }
    public class AllPredictedComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new AllPredictedComponentData {Value = 1});
        }
    }
    public class AllComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentData(entity, new AllComponentData {Value = 1});
        }
    }
    public class ServerHybridComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentObject(entity, gameObject.GetComponent<ServerHybridComponent>());
        }
    }
    public class ClientHybridComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddComponentObject(entity, gameObject.GetComponent<ClientHybridComponent>());
        }
    }

    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public class HybridComponentWeWillOverride : MonoBehaviour
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public struct ServerComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public struct ClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    public class ClientHybridComponent : MonoBehaviour
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    public class ServerHybridComponent : MonoBehaviour
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    public struct InterpolatedClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
    public struct PredictedClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    public struct AllPredictedComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.All)]
    public struct AllComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }

    public class GameObjectConversionTest
    {
        void CheckComponent(World w, ComponentType testType, int expectedCount)
        {
            var query = w.EntityManager.CreateEntityQuery(testType);
            using (var ghosts = query.ToEntityArray(Allocator.TempJob))
            {
                var compCount = ghosts.Length;
                Assert.AreEqual(expectedCount, compCount);
            }
        }

        [Test]
        public void ComponentsStrippedAccordingToGhostConfig()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);

                var gameObject0 = new GameObject();
                // SupportedGhostModes=All DefaultGhostMode=Interpolated
                var ghostComponent = gameObject0.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostAuthoringComponent.GhostModeMask.All;
                gameObject0.AddComponent<HybridComponentWeWillOverride>();
                gameObject0.AddComponent<ClientHybridComponent>();
                gameObject0.AddComponent<ServerHybridComponent>();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new HybridComponentWeWillOverrideConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ServerHybridComponentConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ClientHybridComponentConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ServerComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ClientComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new InterpolatedClientComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedClientComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new AllPredictedComponentDataConverter();
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new AllComponentDataConverter();
                gameObject0.name = "TestConversionGOAll";

                var gameObject1 = new GameObject();
                // SupportedGhostModes=Predicted DefaultGhostMode=Interpolated
                ghostComponent = gameObject1.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostAuthoringComponent.GhostModeMask.Predicted;
                gameObject1.AddComponent<ClientHybridComponent>();
                gameObject1.AddComponent<ServerHybridComponent>();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ServerHybridComponentConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ClientHybridComponentConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ServerComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new ClientComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new InterpolatedClientComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedClientComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new AllPredictedComponentDataConverter();
                gameObject1.AddComponent<TestNetCodeAuthoring>().Converter = new AllComponentDataConverter();
                gameObject1.name = "TestConversionGOPredicted";

                var gameObject2 = new GameObject();
                // SupportedGhostModes=Interpolated DefaultGhostMode=Interpolated
                ghostComponent = gameObject2.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostAuthoringComponent.GhostModeMask.Interpolated;
                gameObject2.AddComponent<ClientHybridComponent>();
                gameObject2.AddComponent<ServerHybridComponent>();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ServerHybridComponentConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ClientHybridComponentConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ServerComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new ClientComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new InterpolatedClientComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new PredictedClientComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new AllPredictedComponentDataConverter();
                gameObject2.AddComponent<TestNetCodeAuthoring>().Converter = new AllComponentDataConverter();
                gameObject2.name = "TestConversionGOInterpolated";

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    gameObject0, gameObject1, gameObject2));

                testWorld.CreateWorlds(true, 1);

                testWorld.SpawnOnServer(gameObject0);
                testWorld.SpawnOnServer(gameObject1);
                testWorld.SpawnOnServer(gameObject2);

                // HybridComponent which was configured as server but override changes it to client only
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<HybridComponentWeWillOverride>(), 0);

                // Server never has client type ghost components
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ClientComponentData>(), 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ClientHybridComponent>(), 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InterpolatedClientComponentData>(), 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<PredictedClientComponentData>(), 0);

                // Server always has all+server type ghosts components
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ServerComponentData>(), 3);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ServerHybridComponent>(), 3);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<AllComponentData>(), 3);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<AllPredictedComponentData>(), 3);

                float frameTime = 1.0f / 60.0f;
                Assert.IsTrue(testWorld.Connect(frameTime, 4));
                testWorld.GoInGame();
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick(frameTime);

                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<HybridComponentWeWillOverride>(), 1);

                // On client, ghost never has server type components
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ServerComponentData>(), 0);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ServerHybridComponent>(), 0);

                // On client, ghost with Predicted SupportedGhostModes get the predicted components, DefaultGhostMode is Interpolated on the All type ghost
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<PredictedClientComponentData>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<AllPredictedComponentData>(), 1);

                // On client, ghosts with All and Interpolated SupportedGhostModes get interpolated components
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InterpolatedClientComponentData>(), 2);

                // All ghosts get the other type components
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ClientComponentData>(), 3);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ClientHybridComponent>(), 3);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<AllComponentData>(), 3);
            }
        }
    }
}
