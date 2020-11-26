using NUnit.Framework;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode.Tests;
using Unity.Jobs;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.TestTools;
using Unity.Burst;

namespace Unity.NetCode.Physics.Tests
{
    public class LagCompensationTestPlayerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Convert(GameObject gameObject, Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            dstManager.AddBuffer<LagCompensationTestCommand>(entity);
            dstManager.AddComponentData(entity, new CommandDataInterpolationDelay());
            dstManager.AddComponentData(entity, new LagCompensationTestPlayer());
            dstManager.AddComponentData(entity, new GhostOwnerComponent());
        }
    }

    public struct LagCompensationTestPlayer : IComponentData
    {
    }

    [NetCodeDisableCommandCodeGen]
    public struct LagCompensationTestCommand : ICommandData, ICommandDataSerializer<LagCompensationTestCommand>
    {
        public uint Tick {get; set;}
        public float3 origin;
        public float3 direction;
        public uint lastFire;

        public void Serialize(ref DataStreamWriter writer, in LagCompensationTestCommand data)
        {
            writer.WriteFloat(data.origin.x);
            writer.WriteFloat(data.origin.y);
            writer.WriteFloat(data.origin.z);
            writer.WriteFloat(data.direction.x);
            writer.WriteFloat(data.direction.y);
            writer.WriteFloat(data.direction.z);
            writer.WriteUInt(data.lastFire);
        }
        public void Serialize(ref DataStreamWriter writer, in LagCompensationTestCommand data, in LagCompensationTestCommand baseline, NetworkCompressionModel model)
        {
            Serialize(ref writer, data);
        }
        public void Deserialize(ref DataStreamReader reader, ref LagCompensationTestCommand data)
        {
            data.origin.x = reader.ReadFloat();
            data.origin.y = reader.ReadFloat();
            data.origin.z = reader.ReadFloat();
            data.direction.x = reader.ReadFloat();
            data.direction.y = reader.ReadFloat();
            data.direction.z = reader.ReadFloat();
            data.lastFire = reader.ReadUInt();
        }
        public void Deserialize(ref DataStreamReader reader, ref LagCompensationTestCommand data, in LagCompensationTestCommand baseline, NetworkCompressionModel model)
        {
            Deserialize(ref reader, ref data);
        }
    }
    [DisableAutoCreation]
    [AlwaysUpdateSystem]
    public class TestAutoInGameSystem : SystemBase
    {
        BeginSimulationEntityCommandBufferSystem m_BeginSimulationCommandBufferSystem;
        bool m_IsServer;
        EntityQuery m_PlayerPrefabQuery;
        EntityQuery m_CubePrefabQuery;
        protected override void OnCreate()
        {
            m_BeginSimulationCommandBufferSystem = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            m_IsServer = World.GetExistingSystem<ServerSimulationSystemGroup>()!=null;
            m_PlayerPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<GhostComponent>(), ComponentType.ReadOnly<LagCompensationTestPlayer>());
            m_CubePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<GhostComponent>(), ComponentType.Exclude<LagCompensationTestPlayer>());
        }
        protected override void OnUpdate()
        {
            var commandBuffer = m_BeginSimulationCommandBufferSystem.CreateCommandBuffer().AsParallelWriter();

            bool isServer = m_IsServer;
            var playerPrefab = m_PlayerPrefabQuery.GetSingletonEntity();
            var cubePrefab = m_CubePrefabQuery.GetSingletonEntity();
            Entities.WithNone<NetworkStreamInGame>().WithoutBurst().ForEach((int entityInQueryIndex, Entity ent, in NetworkIdComponent id) =>
            {
                commandBuffer.AddComponent(entityInQueryIndex, ent, new NetworkStreamInGame());
                if (isServer)
                {
                    // Spawn the player so it gets replicated to the client
                    // Spawn the cube when a player connects for simplicity
                    commandBuffer.Instantiate(entityInQueryIndex, cubePrefab);
                    var player = commandBuffer.Instantiate(entityInQueryIndex, playerPrefab);
                    commandBuffer.SetComponent(entityInQueryIndex, player, new GhostOwnerComponent{NetworkId = id.Value});
                    commandBuffer.SetComponent(entityInQueryIndex, ent, new CommandTargetComponent{targetEntity = player});
                }
            }).Schedule();
            m_BeginSimulationCommandBufferSystem.AddJobHandleForProducer(Dependency);
        }
    }
    [DisableAutoCreation]
    public class LagCompensationTestCommandCommandSendSystem : CommandSendSystem<LagCompensationTestCommand, LagCompensationTestCommand>
    {
        [BurstCompile]
        struct SendJob : IJobEntityBatch
        {
            public SendJobData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var sendJob = new SendJob{data = InitJobData()};
            ScheduleJobData(sendJob);
        }
    }
    [DisableAutoCreation]
    public class LagCompensationTestCommandCommandReceiveSystem : CommandReceiveSystem<LagCompensationTestCommand, LagCompensationTestCommand>
    {
        [BurstCompile]
        struct ReceiveJob : IJobEntityBatch
        {
            public ReceiveJobData data;
            public void Execute(ArchetypeChunk chunk, int orderIndex)
            {
                data.Execute(chunk, orderIndex);
            }
        }
        protected override void OnUpdate()
        {
            var recvJob = new ReceiveJob{data = InitJobData()};
            ScheduleJobData(recvJob);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(ServerSimulationSystemGroup))]
    public class LagCompensationTestCubeMoveSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities.WithNone<LagCompensationTestPlayer>().WithAll<GhostComponent>().ForEach((ref Translation pos) => {
                pos.Value.x += 0.1f;
                if (pos.Value.x > 100)
                    pos.Value.x -= 200;
            }).ScheduleParallel();
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostPredictionSystemGroup))]
    public class LagCompensationTestHitScanSystem : SystemBase
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
        protected override void OnUpdate()
        {
            var collisionHistory = m_physicsHistory.CollisionHistory;
            if (!m_physicsHistory.IsInitialized)
                return;
            uint predictingTick = m_predictionGroup.PredictingTick;
            // Do not perform hit-scan when rolling back, only when simulating the latest tick
            if (!m_predictionGroup.IsFinalPredictionTick)
                return;
            var isServer = m_IsServer;
            // Not using burst since there is a static used to update the UI
            Dependency = Entities
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
            }).Schedule(JobHandle.CombineDependencies(Dependency, m_physicsHistory.LastPhysicsJobHandle));

            m_physicsHistory.LastPhysicsJobHandle = Dependency;
        }
    }
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup), OrderFirst = true)]
    [AlwaysSynchronizeSystem]
    [DisableAutoCreation]
    public class LagCompensationTestCommandSystem : SystemBase
    {
        public static float3 Target;
        ClientSimulationSystemGroup m_systemGroup;
        protected override void OnCreate()
        {
            RequireSingletonForUpdate<CommandTargetComponent>();
            m_systemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
        }
        protected override void OnUpdate()
        {
            var target = GetSingleton<CommandTargetComponent>();
            if (target.targetEntity == Entity.Null)
            {
                Entities.WithoutBurst().WithAll<PredictedGhostComponent>().ForEach((Entity entity, in LagCompensationTestPlayer player) => {
                    target.targetEntity = entity;
                    SetSingleton(target);
                }).Run();
            }
            if (target.targetEntity == Entity.Null || m_systemGroup.ServerTick == 0 || !EntityManager.HasComponent<LagCompensationTestCommand>(target.targetEntity))
                return;

            var buffer = EntityManager.GetBuffer<LagCompensationTestCommand>(target.targetEntity);
            var cmd = default(LagCompensationTestCommand);
            cmd.Tick = m_systemGroup.ServerTick;
            if (math.any(Target != default))
            {
                Entities.WithoutBurst().WithNone<PredictedGhostComponent>().WithAll<GhostComponent>().ForEach((in Translation pos) => {
                    var offset = new float3(0,0,-10);
                    cmd.origin = pos.Value + offset;
                    cmd.direction = Target - offset;
                    cmd.lastFire = cmd.Tick;
                }).Run();
                // If too close to an edge, wait a bit
                if (cmd.origin.x < -90 || cmd.origin.x > 90)
                {
                    buffer.AddCommandData(new LagCompensationTestCommand{Tick = cmd.Tick});
                    return;
                }
                Target = default;

            }
            // Not firing and data for the tick already exists, skip it to make sure a fiew command is not overwritten
            else if (buffer.GetDataAtTick(cmd.Tick, out var dupCmd) && dupCmd.Tick == cmd.Tick)
                return;
            buffer.AddCommandData(cmd);
        }
    }

    public class LagCompensationTests
    {
        [Test]
        public void LagCompensationDoesNotUpdateIfDisableLagCompensationIsPresent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                // Test lag compensation with 100ms ping
                testWorld.DriverSimulatedDelay = 50;
                testWorld.NetCodeAssemblies.Add("Unity.NetCode.Physics,");
                testWorld.NetCodeAssemblies.Add("Unity.Physics,");
                testWorld.Bootstrap(true);

                testWorld.CreateWorlds(true, 1);
                testWorld.ServerWorld.EntityManager.CreateEntity(typeof(DisableLagCompensation));
                testWorld.ClientWorlds[0].EntityManager.CreateEntity(typeof(DisableLagCompensation));

                var ep = NetworkEndPoint.LoopbackIpv4;
                ep.Port = 7979;
                testWorld.ServerWorld.GetExistingSystem<NetworkStreamReceiveSystem>().Listen(ep);
                testWorld.ClientWorlds[0].GetExistingSystem<NetworkStreamReceiveSystem>().Connect(ep);

                for (int i = 0; i < 16; ++i)
                    testWorld.Tick(16f/1000f);

                var serverPhy = testWorld.ServerWorld.GetExistingSystem<PhysicsWorldHistory>();
                Assert.IsFalse(serverPhy.IsInitialized);
                Assert.AreEqual(0, serverPhy.LastStoreTick);
                var clientPhy = testWorld.ServerWorld.GetExistingSystem<PhysicsWorldHistory>();
                Assert.IsFalse(clientPhy.IsInitialized);
                Assert.AreEqual(0, clientPhy.LastStoreTick);
            }
        }

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
                    typeof(LagCompensationTestHitScanSystem));

                var playerGameObject = new GameObject();
                playerGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LagCompensationTestPlayerConverter();
                playerGameObject.name = "LagCompensationTestPlayer";
                var ghostAuth = playerGameObject.AddComponent<GhostAuthoringComponent>();
                ghostAuth.DefaultGhostMode = GhostAuthoringComponent.GhostMode.OwnerPredicted;
                var cubeGameObject = new GameObject();
                cubeGameObject.name = "LagCompensationTestCube";
                var collider = cubeGameObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(1,1,1);

                Assert.IsTrue(testWorld.CreateGhostCollection(
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
                    testWorld.Tick(1f/60f);
                LagCompensationTestCommandSystem.Target = new float3(-0.45f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1f/60f);
                Assert.AreEqual(3, LagCompensationTestHitScanSystem.HitStatus);

                // Test miss
                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestCommandSystem.Target = new float3(-0.55f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1f/60f);
                Assert.AreEqual(0, LagCompensationTestHitScanSystem.HitStatus);

                // Make sure there is no hit without lag compensation
                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestHitScanSystem.EnableLagCompensation = false;
                LagCompensationTestCommandSystem.Target = new float3(-0.45f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1f/60f);
                Assert.AreEqual(2, LagCompensationTestHitScanSystem.HitStatus);

                // Test miss
                LagCompensationTestHitScanSystem.HitStatus = 0;
                LagCompensationTestCommandSystem.Target = new float3(-0.55f,0,-0.5f);
                for (int i = 0; i < 32; ++i)
                    testWorld.Tick(1f/60f);
                Assert.AreEqual(0, LagCompensationTestHitScanSystem.HitStatus);
            }
        }
    }
}