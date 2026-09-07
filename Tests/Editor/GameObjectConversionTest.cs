using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode.Tests
{
    [GhostComponentVariation(typeof(ComponentWeWillOverride), "Client Only")]
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    internal struct ComponentWeWillOverrideVariant
    {
    }
    [DisableAutoCreation]
    partial class ComponentWeWillOverrideDefaultVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            defaultVariants.Add(typeof(ComponentWeWillOverride), Rule.ForAll(typeof(ComponentWeWillOverrideVariant)));
        }
    }

    internal class ComponentWeWillOverrideConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ComponentWeWillOverride());
        }
    }
    internal class ServerComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ServerComponentData {Value = 1});
        }
    }
    internal class ClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ClientComponentData {Value = 1});
        }
    }
    internal class InterpolatedClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new InterpolatedClientComponentData {Value = 1});
        }
    }
    internal class PredictedClientComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new PredictedClientComponentData {Value = 1});
        }
    }
    internal class AllPredictedComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new AllPredictedComponentData {Value = 1});
        }
    }
    internal class AllComponentDataConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new AllComponentData {Value = 1});
        }
    }

    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    internal struct ComponentWeWillOverride : IComponentData
    {
        public int value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Server)]
    internal struct ServerComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.Client)]
    internal struct ClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.InterpolatedClient)]
    internal struct InterpolatedClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
    internal struct PredictedClientComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.AllPredicted)]
    internal struct AllPredictedComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }
    [GhostComponent(PrefabType = GhostPrefabType.All)]
    internal struct AllComponentData : IComponentData
    {
        [GhostField]
        public int Value;
    }

    [Category(NetcodeTestCategories.Foundational)]
    internal class GameObjectConversionTest
    {
        void CheckComponent(World w, ComponentType testType, int expectedCount)
        {
            using var query = w.EntityManager.CreateEntityQuery(testType);
            using (var ghosts = query.ToEntityArray(Allocator.Temp))
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
                testWorld.Bootstrap(true, typeof(ComponentWeWillOverrideDefaultVariantSystem));

                var gameObject0 = new GameObject();
                // SupportedGhostModes=All DefaultGhostMode=Interpolated
                var ghostComponent = gameObject0.AddComponent<GhostAuthoringComponent>();
                ghostComponent.SupportedGhostModes = GhostModeMask.All;
                gameObject0.AddComponent<TestNetCodeAuthoring>().Converter = new ComponentWeWillOverrideConverter();
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
                ghostComponent.SupportedGhostModes = GhostModeMask.Predicted;
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
                ghostComponent.SupportedGhostModes = GhostModeMask.Interpolated;
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

                var isHost = NetCodeTestWorld.OverrideUseSingleWorldHost;
                // Component which was configured as server but override changes it to client only
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ComponentWeWillOverride>(), isHost ? 1 : 0);

                // Binary server never has client type ghost components; the host keeps them on all 3 prefabs since it's a client as well.
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ClientComponentData>(), isHost ? 3 : 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<InterpolatedClientComponentData>(), isHost ? 2 : 0);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<PredictedClientComponentData>(), isHost ? 2 : 0);

                // Server always has all+server type ghosts components
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<ServerComponentData>(), 3);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<AllComponentData>(), 3);
                CheckComponent(testWorld.ServerWorld, ComponentType.ReadOnly<AllPredictedComponentData>(), 3);

                testWorld.Connect();
                testWorld.GoInGame();
                for (int i = 0; i < 64; ++i)
                    testWorld.Tick();

                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ComponentWeWillOverride>(), 1);

                // On client, ghost never has server type components
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ServerComponentData>(), 0);

                // On client, ghost with Predicted SupportedGhostModes get the predicted components, DefaultGhostMode is Interpolated on the All type ghost
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<PredictedClientComponentData>(), 1);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<AllPredictedComponentData>(), 1);

                // On client, ghosts with All and Interpolated SupportedGhostModes get interpolated components
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<InterpolatedClientComponentData>(), 2);

                // All ghosts get the other type components
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<ClientComponentData>(), 3);
                CheckComponent(testWorld.ClientWorlds[0], ComponentType.ReadOnly<AllComponentData>(), 3);
            }
        }
    }
}
