using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode.Tests;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.TestTools;

namespace Unity.NetCode.Physics.Tests
{
    public class LagCompensationTestPlayerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddBuffer<LagCompensationTestCommand>(entity);
            dstManager.AddComponentData(entity, new CommandDataInterpolationDelay());
            dstManager.AddComponentData(entity, new LagCompensationTestPlayer());
        }
    }

    public struct LagCompensationTestPlayer : IComponentData
    {
        [GhostDefaultField]
        public int Owner;
    }

    public struct LagCompensationTestCommand : ICommandData<LagCompensationTestCommand>
    {
        public uint Tick => tick;
        public uint tick;
        public float3 origin;
        public float3 direction;
        public uint lastFire;

        public void Serialize(ref DataStreamWriter writer)
        {
            writer.WriteFloat(origin.x);
            writer.WriteFloat(origin.y);
            writer.WriteFloat(origin.z);
            writer.WriteFloat(direction.x);
            writer.WriteFloat(direction.y);
            writer.WriteFloat(direction.z);
            writer.WriteUInt(lastFire);
        }
        public void Serialize(ref DataStreamWriter writer, LagCompensationTestCommand baseline, NetworkCompressionModel model)
        {
            Serialize(ref writer);
        }
        public void Deserialize(uint t, ref DataStreamReader reader)
        {
            tick = t;
            origin.x = reader.ReadFloat();
            origin.y = reader.ReadFloat();
            origin.z = reader.ReadFloat();
            direction.x = reader.ReadFloat();
            direction.y = reader.ReadFloat();
            direction.z = reader.ReadFloat();
            lastFire = reader.ReadUInt();
        }
        public void Deserialize(uint t, ref DataStreamReader reader, LagCompensationTestCommand baseline, NetworkCompressionModel model)
        {
            Deserialize(t, ref reader);
        }
    }
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class TestAutoInGameSystem : JobComponentSystem
    {
        EndSimulationEntityCommandBufferSystem m_EndSimulationCommandBufferSystem;
        bool m_IsServer;
        protected override void OnCreate()
        {
            m_EndSimulationCommandBufferSystem = World.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
            m_IsServer = World.GetExistingSystem<ServerSimulationSystemGroup>()!=null;
        }
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var commandBuffer = m_EndSimulationCommandBufferSystem.CreateCommandBuffer().ToConcurrent();

            bool isServer = m_IsServer;
            var serverPrefabs = GetSingleton<GhostPrefabCollectionComponent>().serverPrefabs;
            var bufferFromEntity = GetBufferFromEntity<GhostPrefabBuffer>(true);
            var handle = Entities.WithReadOnly(bufferFromEntity).WithNone<NetworkStreamInGame>().WithoutBurst().ForEach((int entityInQueryIndex, Entity ent, in NetworkIdComponent id) =>
            {
                commandBuffer.AddComponent(entityInQueryIndex, ent, new NetworkStreamInGame());
                if (isServer)
                {
                    // Spawn the player so it gets replicated to the client
                    var ghostId = LagCompensationTestGhostSerializerCollection.FindGhostType<LagCompensationTestPlayerSnapshotData>();
                    var playerPrefab = bufferFromEntity[serverPrefabs][ghostId].Value;
                    // Spawn the cube when a player connects for simplicity
                    ghostId = LagCompensationTestGhostSerializerCollection.FindGhostType<LagCompensationTestCubeSnapshotData>();
                    var cubePrefab = bufferFromEntity[serverPrefabs][ghostId].Value;
                    commandBuffer.Instantiate(entityInQueryIndex, cubePrefab);
                    var player = commandBuffer.Instantiate(entityInQueryIndex, playerPrefab);
                    commandBuffer.SetComponent(entityInQueryIndex, player, new LagCompensationTestPlayer{Owner = id.Value});
                    commandBuffer.SetComponent(entityInQueryIndex, ent, new CommandTargetComponent{targetEntity = player});
                }
            }).Schedule(inputDeps);
            m_EndSimulationCommandBufferSystem.AddJobHandleForProducer(handle);
            return handle;
        }
    }
    [DisableAutoCreation]
    public class LagCompensationTestCommandCommandSendSystem : CommandSendSystem<LagCompensationTestCommand>
    {}
    [DisableAutoCreation]
    public class LagCompensationTestCommandCommandReceiveSystem : CommandReceiveSystem<LagCompensationTestCommand>
    {}

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    public class LagCompensationTestCubeMoveSystem : JobComponentSystem
    {
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            return Entities.WithNone<LagCompensationTestPlayer>().WithAll<GhostComponent>().ForEach((ref Translation pos) => {
                pos.Value.x += 0.1f;
                if (pos.Value.x > 100)
                    pos.Value.x -= 200;
            }).Schedule(inputDeps);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostPredictionSystemGroup))]
    public class LagCompensationTestHitScanSystem : JobComponentSystem
    {
        private PhysicsWorldHistory m_physicsHistory;
        private GhostPredictionSystemGroup m_predictionGroup;
        private bool m_IsServer;
        public static int HitStatus = 0;
        public static bool EnableLagCompensation = true;
        protected override void OnCreate()
        {
            m_physicsHistory = World.GetOrCreateSystem<PhysicsWorldHistory>();
            m_predictionGroup = World.GetOrCreateSystem<GhostPredictionSystemGroup>();
            m_IsServer = World.GetExistingSystem<ServerSimulationSystemGroup>() != null;
        }
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var collisionHistory = m_physicsHistory.CollisionHistory;
            uint predictingTick = m_predictionGroup.PredictingTick;
            // Do not perform hit-scan when rolling back, only when simulating the latest tick
            if (!m_predictionGroup.IsFinalPredictionTick)
                return inputDeps;
            var isServer = m_IsServer;
            // Not using burst since there is a static used to update the UI
            var handle = Entities
                .WithoutBurst()
                .ForEach((DynamicBuffer<LagCompensationTestCommand> commands, in CommandDataInterpolationDelay delay) =>
            {
                // If there is no data for the tick or a fire was not requested - do not process anything
                if (!commands.GetDataAtTick(predictingTick, out var cmd))
                    return;
                if (cmd.lastFire != predictingTick)
                    return;
                var interpolDelay = EnableLagCompensation ? delay.Delay : 0;

                // Get the collision world to use given the tick currently being predicted and the interpolation delay for the connection
                collisionHistory.GetCollisionWorldFromTick(predictingTick, interpolDelay, out var collWorld);
                var rayInput = new Unity.Physics.RaycastInput();
                rayInput.Start = cmd.origin;
                rayInput.End = cmd.origin + cmd.direction * 100;
                rayInput.Filter = Unity.Physics.CollisionFilter.Default;
                bool hit = collWorld.CastRay(rayInput);
                if (hit)
                {
                    HitStatus |= isServer?1:2;
                }
            }).Schedule(JobHandle.CombineDependencies(inputDeps, m_physicsHistory.LastPhysicsJobHandle));

            m_physicsHistory.LastPhysicsJobHandle = handle;
            return handle;
        }
    }
    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(LagCompensationTestCommandCommandSendSystem))]
    [DisableAutoCreation]
    public class LagCompensationTestCommandSystem : JobComponentSystem
    {
        public static float3 Target;
        ClientSimulationSystemGroup m_systemGroup;
        protected override void OnCreate()
        {
            RequireSingletonForUpdate<CommandTargetComponent>();
            m_systemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
        }
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps.Complete();
            var target = GetSingleton<CommandTargetComponent>();
            if (target.targetEntity == Entity.Null)
            {
                Entities.WithoutBurst().WithAll<PredictedGhostComponent>().ForEach((Entity entity, in LagCompensationTestPlayer player) => {
                    target.targetEntity = entity;
                    SetSingleton(target);
                }).Run();
            }
            if (target.targetEntity == Entity.Null || m_systemGroup.ServerTick == 0 || !EntityManager.HasComponent<LagCompensationTestCommand>(target.targetEntity))
                return default;

            var buffer = EntityManager.GetBuffer<LagCompensationTestCommand>(target.targetEntity);
            var cmd = default(LagCompensationTestCommand);
            cmd.tick = m_systemGroup.ServerTick;
            if (math.any(Target != default))
            {
                Entities.WithoutBurst().WithNone<PredictedGhostComponent>().WithAll<GhostComponent>().ForEach((in Translation pos) => {
                    var offset = new float3(0,0,-10);
                    cmd.origin = pos.Value + offset;
                    cmd.direction = Target - offset;
                    cmd.lastFire = cmd.tick;
                }).Run();
                Target = default;
            }
            // Not firing and data for the tick already exists, skip it to make sure a fiew command is not overwritten
            else if (buffer.GetDataAtTick(cmd.tick, out var dupCmd) && dupCmd.Tick == cmd.tick)
                return default;
            buffer.AddCommandData(cmd);
            return default;
        }
    }

    public class LagCompensationTests
    {
        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitWithLagCompensation()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // Test lag compensation with 100ms ping
                testWorld.DriverSimulatedDelay = 50;
                testWorld.NetCodeAssemblies.Add("Unity.NetCode.Physics,");
                testWorld.NetCodeAssemblies.Add("Unity.Physics,");

                testWorld.Bootstrap(true,
                    typeof(TestAutoInGameSystem),
                    typeof(LagCompensationTestCubeMoveSystem),
                    typeof(LagCompensationTestCommandCommandSendSystem),
                    typeof(LagCompensationTestCommandCommandReceiveSystem),
                    typeof(LagCompensationTestCommandSystem),
                    typeof(LagCompensationTestHitScanSystem),
                    typeof(LagCompensationTestCubeGhostSpawnSystem),
                    typeof(LagCompensationTestCubeGhostUpdateSystem),
                    typeof(LagCompensationTestPlayerGhostSpawnSystem),
                    typeof(LagCompensationTestPlayerGhostUpdateSystem),
                    typeof(LagCompensationTestGhostSendSystem),
                    typeof(LagCompensationTestGhostReceiveSystem));

                var playerGameObject = new GameObject();
                playerGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LagCompensationTestPlayerConverter();
                playerGameObject.name = "LagCompensationTestPlayer";
                var playerGhost = playerGameObject.AddComponent<GhostAuthoringComponent>();
                playerGhost.DefaultClientInstantiationType = GhostAuthoringComponent.ClientInstantionType.OwnerPredicted;
                playerGhost.PredictingPlayerNetworkId = "Unity.NetCode.Physics.Tests.LagCompensationTestPlayer.Owner";
                var cubeGameObject = new GameObject();
                cubeGameObject.name = "LagCompensationTestCube";
                var collider = cubeGameObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(1,1,1);

                Assert.IsTrue(testWorld.CreateGhostCollection(
                    "/../Packages/com.unity.netcode/Tests/Editor/Physics/Generated/",
                    "LagCompensationTest",
                    playerGameObject, cubeGameObject));

                testWorld.CreateWorlds(true, 1);

                var ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestHitScanSystem.EnableLagCompensation = true;
                LagCompensationTestCommandSystem.Target = default;
                // Give the netcode some time to spawn entities and settle on a good time synchronization
                for (int i = 0; i < 128; ++i)
                    testWorld.Tick(16f/1000f);
                LagCompensationTestCommandSystem.Target = new float3(-0.45f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(16f/1000f);
                Assert.AreEqual(3, LagCompensationTestHitScanSystem.HitStatus);

                // Test miss
                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestCommandSystem.Target = new float3(-0.55f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(16f/1000f);
                Assert.AreEqual(0, LagCompensationTestHitScanSystem.HitStatus);

                // Make sure there is no hit without lag compensation
                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestHitScanSystem.EnableLagCompensation = false;
                LagCompensationTestCommandSystem.Target = new float3(-0.45f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(16f/1000f);
                Assert.AreEqual(2, LagCompensationTestHitScanSystem.HitStatus);

                // Test miss
                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestCommandSystem.Target = new float3(-0.55f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(16f/1000f);
                Assert.AreEqual(0, LagCompensationTestHitScanSystem.HitStatus);
            }
        }
    }
}