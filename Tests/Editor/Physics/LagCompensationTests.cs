#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode.Tests;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.TestTools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Physics;
using Unity.Physics.Extensions;
using BoxCollider = Unity.Physics.BoxCollider;
using Collider = Unity.Physics.Collider;
using RaycastHit = Unity.Physics.RaycastHit;
using SphereCollider = Unity.Physics.SphereCollider;

namespace Unity.NetCode.Physics.Tests
{
    internal class LagCompensationTestPlayerConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddBuffer<LagCompensationTestCommand>(entity);
            baker.AddComponent(entity, new CommandDataInterpolationDelay());
            baker.AddComponent(entity, new LagCompensationTestPlayer());
            baker.AddComponent(entity, new GhostOwner());
        }
    }

    internal struct LagCompensationTestPlayer : IComponentData
    {
    }

    [NetCodeDisableCommandCodeGen]
    internal struct LagCompensationTestCommand : ICommandData, ICommandDataSerializer<LagCompensationTestCommand>
    {
        public NetworkTick Tick {get; set;}
        public float3 origin;
        public float3 direction;
        public NetworkTick lastFire;

        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in LagCompensationTestCommand data)
        {
            writer.WriteFloat(data.origin.x);
            writer.WriteFloat(data.origin.y);
            writer.WriteFloat(data.origin.z);
            writer.WriteFloat(data.direction.x);
            writer.WriteFloat(data.direction.y);
            writer.WriteFloat(data.direction.z);
            writer.WriteUInt(data.lastFire.SerializedData);
        }
        public void Serialize(ref DataStreamWriter writer, in RpcSerializerState state, in LagCompensationTestCommand data, in LagCompensationTestCommand baseline, StreamCompressionModel model)
        {
            Serialize(ref writer, state, data);
        }
        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref LagCompensationTestCommand data)
        {
            data.origin.x = reader.ReadFloat();
            data.origin.y = reader.ReadFloat();
            data.origin.z = reader.ReadFloat();
            data.direction.x = reader.ReadFloat();
            data.direction.y = reader.ReadFloat();
            data.direction.z = reader.ReadFloat();
            data.lastFire = new NetworkTick{SerializedData = reader.ReadUInt()};
        }
        public void Deserialize(ref DataStreamReader reader, in RpcDeserializerState state, ref LagCompensationTestCommand data, in LagCompensationTestCommand baseline, StreamCompressionModel model)
        {
            Deserialize(ref reader, state, ref data);
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ServerSimulation)]
    internal partial class TestAutoInGameSystem : SystemBase
    {
        BeginSimulationEntityCommandBufferSystem m_BeginSimulationCommandBufferSystem;
        EntityQuery m_PlayerPrefabQuery;
        EntityQuery m_ColliderPrefabQuery;
        protected override void OnCreate()
        {
            m_BeginSimulationCommandBufferSystem = World.GetOrCreateSystemManaged<BeginSimulationEntityCommandBufferSystem>();
            m_PlayerPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<LagCompensationTestPlayer>());
            m_ColliderPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<GhostInstance>(), ComponentType.Exclude<LagCompensationTestPlayer>());
        }
        protected override void OnUpdate()
        {
            var commandBuffer = m_BeginSimulationCommandBufferSystem.CreateCommandBuffer().AsParallelWriter();

            bool isServer = World.IsServer();
            var playerPrefab = m_PlayerPrefabQuery.ToEntityArray(Allocator.Temp)[0];
            var colliderPrefabs = m_ColliderPrefabQuery.ToEntityArray(Allocator.TempJob);
            Entities.WithNone<NetworkStreamInGame>().WithoutBurst().WithReadOnly(colliderPrefabs).ForEach((int entityInQueryIndex, Entity ent, in NetworkId id) =>
            {
                commandBuffer.AddComponent(entityInQueryIndex, ent, new NetworkStreamInGame());
                if (isServer)
                {
                    // Spawn the player so it gets replicated to the client
                    // Spawn the cube and sphere when a player connects, for simplicity
                    foreach (var colliderPrefab in colliderPrefabs)
                        commandBuffer.Instantiate(entityInQueryIndex, colliderPrefab);
                    var player = commandBuffer.Instantiate(entityInQueryIndex, playerPrefab);
                    commandBuffer.SetComponent(entityInQueryIndex, player, new GhostOwner{NetworkId = id.Value});
                    commandBuffer.SetComponent(entityInQueryIndex, ent, new CommandTarget{targetEntity = player});
                }
            }).Run();
            colliderPrefabs.Dispose();
            m_BeginSimulationCommandBufferSystem.AddJobHandleForProducer(Dependency);
        }
    }
    [DisableAutoCreation]
    [UpdateInGroup(typeof(CommandSendSystemGroup))]
    [BurstCompile]
    internal partial struct LagCompensationTestCommandCommandSendSystem : ISystem
    {
        CommandSendSystem<LagCompensationTestCommand, LagCompensationTestCommand> m_CommandSend;
        [BurstCompile]
        struct SendJob : IJobChunk
        {
            public CommandSendSystem<LagCompensationTestCommand, LagCompensationTestCommand>.SendJobData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_CommandSend.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!m_CommandSend.ShouldRunCommandJob(ref state))
                return;
            var sendJob = new SendJob{data = m_CommandSend.InitJobData(ref state)};
            state.Dependency = sendJob.Schedule(m_CommandSend.Query, state.Dependency);
        }
    }
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(CommandReceiveSystemGroup))]
    [BurstCompile]
    internal partial struct LagCompensationTestCommandCommandReceiveSystem : ISystem
    {
        CommandReceiveSystem<LagCompensationTestCommand, LagCompensationTestCommand> m_CommandRecv;
        [BurstCompile]
        struct ReceiveJob : IJobChunk
        {
            public CommandReceiveSystem<LagCompensationTestCommand, LagCompensationTestCommand>.ReceiveJobData data;
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);
                data.Execute(chunk, unfilteredChunkIndex);
            }
        }
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            m_CommandRecv.OnCreate(ref state);
        }
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var recvJob = new ReceiveJob{data = m_CommandRecv.InitJobData(ref state)};
            state.Dependency = recvJob.Schedule(m_CommandRecv.Query, state.Dependency);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    internal unsafe partial class LagCompensationTestCubeMoveSystem : SystemBase
    {
        internal const float DebugDrawLineDuration = 30f;
        protected  override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            foreach(var (transRef, physicsCollider) in SystemAPI.Query<RefRW<LocalTransform>, PhysicsCollider>().WithNone<LagCompensationTestPlayer>())
            {
                var prevPos = transRef.ValueRW.Position;
                var newPos = prevPos;
                newPos.x = LagCompensationTestCommandSystem.GetDeterministicXPosition(networkTime.ServerTick);
                transRef.ValueRW.Position = newPos;

                var stepColor = Color.green;
                if (networkTime.InputTargetTick.TickIndexForValidTick % 2 == 0) stepColor.a = 0.4f;

                Debug.DrawLine(newPos, prevPos, stepColor, DebugDrawLineDuration);
                Debug.DrawLine(newPos, newPos + new float3(0, 0.5f, 0), stepColor, DebugDrawLineDuration);
                if (LagCompensationTestCommandSystem.ClientShotAction != LagCompensationTestCommandSystem.ShotType.DontShoot)
                {
                    if (physicsCollider.Value.Value.Type == ColliderType.Box)
                        DrawCube(newPos, ((BoxCollider*) physicsCollider.ColliderPtr), Color.green);
                    else if (physicsCollider.Value.Value.Type == ColliderType.Sphere)
                        DrawSphere(newPos, ((SphereCollider*) physicsCollider.ColliderPtr), Color.magenta);
                }
            }
        }

        internal static void DrawSphere(float3 pos, SphereCollider* sphereColliderPtr, Color color)
        {
            var geo = sphereColliderPtr->Geometry;
            pos -= geo.Center;
            var halfSize = geo.Radius;
            var x = new float3(halfSize, halfSize, 0);
            Debug.DrawLine(pos + x, pos - x, color, DebugDrawLineDuration);
            var y = new float3(0, halfSize, halfSize);
            Debug.DrawLine(pos + y, pos - y, color, DebugDrawLineDuration);
            var z = new float3(halfSize, 0, halfSize);
            Debug.DrawLine(pos + z, pos - z, color, DebugDrawLineDuration);
        }

        internal static void DrawCube(float3 pos, BoxCollider* boxColliderPtr, Color color)
        {
            var geo = boxColliderPtr->Geometry;
            pos -= geo.Center;
            var halfSize = geo.Size * .5f;
            var x = new float3(halfSize.x, 0, 0);
            Debug.DrawLine(pos + x, pos - x, color, DebugDrawLineDuration);
            var y = new float3(0, halfSize.y, 0);
            Debug.DrawLine(pos + y, pos - y, color, DebugDrawLineDuration);
            var z = new float3(0, 0, halfSize.z);
            Debug.DrawLine(pos + z, pos - z, color, DebugDrawLineDuration);
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation|WorldSystemFilterFlags.ServerSimulation)]
    internal unsafe partial class LagCompensationTestHitScanSystem : SystemBase
    {
        public static RaycastHit? ServerRayCastHit;
        public static RaycastHit? ClientRayCastHit;
        public static bool ServerVictimEntityStillExists;
        public static bool ClientVictimEntityStillExists;
        public static bool EnableLagCompensation = true;
        public static bool NoHitsRegistered => ServerRayCastHit == null && ClientRayCastHit == null;
        public static bool OnlyClientHitRegistered => ServerRayCastHit == null && ClientRayCastHit != null;
        public static bool BothHitsRegistered => ServerRayCastHit != null && ClientRayCastHit != null;
        public static byte ForcedInputLatencyTicks;

        protected override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var collisionHistory = SystemAPI.GetSingleton<PhysicsWorldHistorySingleton>();
            var physicsWorld = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld;
            var isServer = World.IsServer();

            Entities
                .WithoutBurst()
                .WithReadOnly(physicsWorld)
                .WithAll<LagCompensationTestPlayer>()
                .ForEach((ref LocalTransform characterTrans, in DynamicBuffer<LagCompensationTestCommand> commands, in CommandDataInterpolationDelay delay) =>
                {
                    Assert.AreEqual(1, networkTime.SimulationStepBatchSize, "Must not be batching ticks!");
                    Assert.IsFalse(networkTime.IsCatchUpTick, "Must not be catching up!");

                    // Movement:
                    var prevPos = characterTrans.Position;
                    characterTrans.Position = LagCompensationTestCommandSystem.GetPlayersDeterministicPositionForTick(networkTime.ServerTick);

                    if (networkTime.IsFirstTimeFullyPredictingTick)
                    {
                        // Draw:
                        var stepColor = networkTime.InputTargetTick.TickIndexForValidTick % 2 == 0
                            ? (isServer ? Color.grey : Color.white)
                            : (isServer ? Color.black : Color.grey);
                        var offset = new float3(0, 0, isServer ? 0.05f : 0);
                        Debug.DrawLine(characterTrans.Position + offset, prevPos + offset, stepColor, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                        Debug.DrawLine(characterTrans.Position + offset, characterTrans.Position + offset + new float3(0, 0.5f, 0), stepColor, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                    }

                    // Do not perform hit-scan when rolling back, only when simulating the latest tick
                    if (!networkTime.IsFirstTimeFullyPredictingTick)
                        return;

                    // If there is no data for the tick or a fire was not requested - do not process anything
                    if (!commands.GetDataAtTick(networkTime.ServerTick, out var cmd))
                        return;
                    if (cmd.lastFire != networkTime.ServerTick)
                        return;

                    // When we fetch the CollisionWorld for ServerTick T, we need to account for the fact that the user
                    // raised this input sometime on the previous tick (render-frame, technically).
                    const int additionalRenderDelay = 1;
                    var interpolDelay = EnableLagCompensation && isServer
                        ? delay.Delay // Don't account for `additionalRenderDelay` here,
                                      // because we're using an auto-aim "bot",
                                      // which doesn't have any additional input delay on the server.
                        : additionalRenderDelay;

                    var forcedInputLatencyEnabled = ForcedInputLatencyTicks > 0;
                    var (expected, margin) = (isServer, forcedInputLatencyEnabled) switch
                    {
                        // The client has a default value of 0, even with ForcedInputLatency enabled,
                        // as we're not polling input gather ticks here, we're polling predicted ticks.
                        (false, _) => (0, 0),
                        // Server, with lag compensation enabled, we expect a huge difference between
                        // ForcedInputLatency ON vs OFF:
                        (true, true) => (14 - (int)ForcedInputLatencyTicks, 2),
                        (true, false) => (14, 2),
                    };
                    Assert.That(delay.Delay, Is.EqualTo(expected).Within(margin), $"CommandDataInterpolationDelay.Delay value for: EnableLagCompensation:{EnableLagCompensation}, isServer:{isServer}, ForcedInputLatencyTicks:{ForcedInputLatencyTicks} ({forcedInputLatencyEnabled})!");

                    // Get the collision world to use given the tick currently being predicted and the interpolation delay for the connection
                    collisionHistory.GetCollisionWorldFromTick(networkTime.ServerTick, interpolDelay, ref physicsWorld, out var collWorld, out var expectedTick, out var returnedTick);
                    var rayInput = new Unity.Physics.RaycastInput();
                    rayInput.Start = characterTrans.Position; // NOTE: We're NOT using the ray origin here!
                    var positionDesyncMeters = math.distance(rayInput.Start, cmd.origin);
                    rayInput.End = rayInput.Start + cmd.direction;
                    rayInput.Filter = Unity.Physics.CollisionFilter.Default;

                    bool hit = collWorld.CastRay(rayInput, out var raycastHit);
                    var color = isServer ? Color.blue : Color.red;
                    Debug.DrawLine(characterTrans.Position, rayInput.End, color, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);

                    // Draw a faint line showing where the client originally shot from, to show the divergence when using Forced Input Latency.
                    {
                        var black = Color.black;
                        black.a = 0.2f;
                        Debug.DrawLine(cmd.origin, cmd.origin + cmd.direction, black, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                    }

                    var victimIsAlive = EntityManager.Exists(raycastHit.Entity);
                    FixedString512Bytes networkTickInfo = $"\n{networkTime.ToFixedString()}";
                    string collisionInfo = hit ? $" - {collWorld.Bodies[raycastHit.RigidBodyIndex].Collider.Value.Type}!\n\traycastHit[Entity: {raycastHit.Entity} (alive: {victimIsAlive}), Position: {raycastHit.Position}, SurfaceNormal: {raycastHit.SurfaceNormal}, Fraction: {raycastHit.Fraction}, ColliderKey: {raycastHit.ColliderKey.ToString()}, RigidBodyIndex: {raycastHit.RigidBodyIndex}, Material.Friction: {raycastHit.Material.Friction}]" : "";
                    collisionInfo = $"[TickIndex:{NetCodeTestWorld.TickIndex}][ServerTick:{networkTime.ServerTick.ToFixedString()}] LagCompensationTest result on <color=green>{(isServer ? "SERVER" : "CLIENT")}</color> is {(hit ? $"<color=green>HIT</color> (index: {raycastHit.RigidBodyIndex})" : "<color=red>MISS</color>")} on ServerTick {cmd.Tick.ToFixedString()} with interpolDelay: {interpolDelay} ticks (historyBufferEntry[expectedTick:{expectedTick}, returnedTick:{returnedTick.ToFixedString()}]), and origin desync of: {positionDesyncMeters}m!\n\tRay(start: {rayInput.Start} vs cmd.origin: {cmd.origin}, end: {rayInput.End}, dir: {(rayInput.End - rayInput.Start)}, range: {math.length(cmd.direction):0.00}m)! {networkTickInfo} {collisionInfo}\n";
                    if (hit)
                    {
                        if (isServer)
                            ServerRayCastHit = raycastHit;
                        else ClientRayCastHit = raycastHit;

                        // NOTE: The Entity SHOULD BE RETURNED even if said Entity was 'since deleted',
                        // because this is a historic query on a historic CollisionWorld.
                        if (isServer)
                            ServerVictimEntityStillExists = victimIsAlive;
                        else ClientVictimEntityStillExists = victimIsAlive;

                        var victimCollider = collWorld.Bodies[raycastHit.RigidBodyIndex].Collider;
                        Assert.IsTrue(victimCollider.IsCreated, "Expecting physics collider in historic collision world to be valid, due to deep copy clone operation!");
                    }

                    // Draw all colliders:
                    for (var i = 0; i < collWorld.Bodies.Length; i++)
                    {
                        var rigidBody = collWorld.Bodies[i];
                        var victimPos = rigidBody.WorldFromBody.pos;
                        var victimCollider = rigidBody.Collider;
                        if (!victimCollider.IsCreated)
                        {
                            collisionInfo += $"\n\tcollWorld.Bodies[{i}] Pos:{victimPos} null";
                            continue;
                        }
                        var drawOffset = new float3(0, 0, 0.001f); // See the other line!
                        if (victimCollider.Value.Type == ColliderType.Box)
                        {
                            var boxCollider = ((BoxCollider*) victimCollider.GetUnsafePtr());
                            LagCompensationTestCubeMoveSystem.DrawCube(victimPos + drawOffset, boxCollider, color);
                            collisionInfo += $"\n\tcollWorld.Bodies[{i}] BoxCollider Pos:{victimPos} Geometry.Size:{boxCollider->Geometry.Size}";
                        }
                        else if (victimCollider.Value.Type == ColliderType.Sphere)
                        {
                            var sphereCollider = ((SphereCollider*) victimCollider.GetUnsafePtr());
                            LagCompensationTestCubeMoveSystem.DrawSphere(victimPos + drawOffset, sphereCollider, color);
                            collisionInfo += $"\n\tcollWorld.Bodies[{i}] SphereCollider Pos:{victimPos} Geometry.Radius:{sphereCollider->Geometry.Radius}";
                        }
                        else Assert.Fail("Sanity check");
                    }

                    collisionInfo += $"\n\n{collisionHistory.GetHistoryBufferData(ref physicsWorld)}";
                    Debug.Log(collisionInfo);
                }).Run();
        }
    }
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [AlwaysSynchronizeSystem]
    [DisableAutoCreation]
    internal partial class LagCompensationTestCommandSystem : SystemBase
    {
        internal enum ShotType
        {
            DontShoot = default,
            ShootToHit,
            ShootToMiss,
        }
        public static ShotType ClientShotAction;
        public static Entity ClientAimAtTarget;

        protected override void OnCreate()
        {
            RequireForUpdate<CommandTarget>();
        }
        protected override void OnUpdate()
        {
            var target = SystemAPI.GetSingleton<CommandTarget>();
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (target.targetEntity == Entity.Null)
            {
                foreach (var (_, entity) in SystemAPI.Query<RefRO<PredictedGhost>>().WithEntityAccess().WithAll<LagCompensationTestPlayer>())
                {
                    target.targetEntity = entity;
                    SystemAPI.SetSingleton(target);
                }
            }
            if (target.targetEntity == Entity.Null || !networkTime.ServerTick.IsValid || !EntityManager.HasComponent<LagCompensationTestCommand>(target.targetEntity))
                return;

            var buffer = EntityManager.GetBuffer<LagCompensationTestCommand>(target.targetEntity);
            var cmd = default(LagCompensationTestCommand);
            cmd.Tick = networkTime.InputTargetTick;
            if (ClientShotAction != ShotType.DontShoot)
            {
                foreach (var localTransform in SystemAPI.Query<LocalTransform>().WithAll<LagCompensationTestPlayer>())
                {
                    // We CANNOT use the players CURRENT Entity LocalTransform.Position here,
                    // because it's out of date, because it has not been updated yet, as the GhostInputSystemGroup
                    // runs before both the GhostUpdateSystem and the Prediction Loop.
                    cmd.origin = GetPlayersDeterministicPositionForTick(networkTime.ServerTick);

                    var victimTransform = EntityManager.GetComponentData<LocalTransform>(ClientAimAtTarget);
                    var aimPoint = victimTransform.Position;
                    var isTryingToMiss = ClientShotAction == ShotType.ShootToMiss;
                    if (isTryingToMiss) aimPoint.y += 2.5f; // Force shot miss by aiming ABOVE the target.
                    cmd.direction = (aimPoint - cmd.origin) * 1.1f; // add 10% to the distance.
                    cmd.lastFire = cmd.Tick;

                    Debug.DrawLine(cmd.origin, aimPoint, Color.yellow, LagCompensationTestCubeMoveSystem.DebugDrawLineDuration);
                    Debug.Log($"<color=yellow>[TickIndex:{NetCodeTestWorld.TickIndex}][ServerTick:{networkTime.ServerTick.ToFixedString()}] Client aiming at {ClientAimAtTarget.ToFixedString()} and pressing shoot once: From {cmd.origin} (vs deterministic: {GetPlayersDeterministicPositionForTick(networkTime.InputTargetTick)}), to: {victimTransform.Position}, thus direction {cmd.direction}, with goal '{ClientShotAction}'!</color>");
                    ClientShotAction = default;
                }
            }
            // Not firing and data for the tick already exists, skip it to make sure a few command is not overwritten
            else if (buffer.GetDataAtTick(cmd.Tick, out var dupCmd) && dupCmd.Tick == cmd.Tick)
                return;
            buffer.AddCommandData(cmd);
        }

        internal static float3 GetPlayersDeterministicPositionForTick(NetworkTick targetTick)
        {
            return new float3(GetDeterministicXPosition(targetTick), 0, -10);
        }

        internal static float GetDeterministicXPosition(NetworkTick targetTick)
        {
            return (targetTick.TickIndexForValidTick * LagCompensationTests.MovementSpeedPerTick);
        }
    }

    internal class LagCompensationTests
    {
        const int k_TicksToRegisterHit = 12;

        // Unique values for ease of debugging.
        internal static float BoxColliderGeometryOriginalSize = 0.222f;
        private static float BoxColliderGeometryResizeSize = 0.333f;
        private static float SphereColliderRadiusSize = 0.4444f;
        internal static float MovementSpeedPerTick = 0.5f; // It's larger than the diameter of each collider,
                                                           // which means we're validating perfect hits.

        [Test]
        public void LagCompensationDoesNotUpdateIfLagCompensationConfigIsNotPresent()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.DriverSimulatedDelay = 50; // Each way! I.e. Testing lag compensation with a MINIMUM of 100ms ping.
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
                testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");
                testWorld.Bootstrap(true);

                testWorld.CreateWorlds(true, 1, false);
                Assert.IsFalse(testWorld.TryGetSingletonEntity<LagCompensationConfig>(testWorld.ServerWorld) != Entity.Null);
                Assert.IsFalse(testWorld.TryGetSingletonEntity<LagCompensationConfig>(testWorld.ClientWorlds[0]) != Entity.Null);
                testWorld.Connect(maxSteps: 16);

                var serverPhy = testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ServerWorld);
                Assert.AreEqual(NetworkTick.Invalid, serverPhy.LatestStoredTick);
                var clientPhy = testWorld.GetSingleton<PhysicsWorldHistorySingleton>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(NetworkTick.Invalid, clientPhy.LatestStoredTick);
            }
        }

        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitAndMissWithLagCompensation()
        {
            LagCompensationTestHitScanSystem.ForcedInputLatencyTicks = 0;
            HitAndMissWithLagCompensationTest();
        }

        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitAndMissWithLagCompensation_AndForcedInputLatency_Of4()
        {
            LagCompensationTestHitScanSystem.ForcedInputLatencyTicks = 4;
            HitAndMissWithLagCompensationTest();
        }

        public void HitAndMissWithLagCompensationTest()
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                InitTest(testWorld, false, IncrementalBroadphase.FullBVHRebuild, out var clientEm, out _, new LagCompensationConfig
                {
                    ServerHistorySize = PhysicsWorldHistory.RawHistoryBufferMaxCapacity / 2,
                    ClientHistorySize = 2,
                    DeepCopyDynamicColliders = true,
                    DeepCopyStaticColliders = true,
                });
                var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
                clientTickRate.ForcedInputLatencyTicks = LagCompensationTestHitScanSystem.ForcedInputLatencyTicks;
                testWorld.ClientWorlds[0].EntityManager.CreateSingleton(clientTickRate);

                // Give the netcode some time to spawn entities and settle on a good time synchronization
                for (int i = 0; i < 70; ++i)
                    testWorld.Tick();

                GetCubeAndSphere(clientEm, out var clientVictimCubeEntity, out _, out _, out _);
                LagCompensationTestCommandSystem.ClientAimAtTarget = clientVictimCubeEntity;
                LagCompensationTestHitScanSystem.EnableLagCompensation = true;
                int ticksToRegisterHit = k_TicksToRegisterHit + LagCompensationTestHitScanSystem.ForcedInputLatencyTicks;

                // Test hit:
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToHit;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.BothHitsRegistered);

                // Test miss
                ResetHits();
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToMiss;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.NoHitsRegistered);

                // Make sure there is no hit without lag compensation
                ResetHits();
                LagCompensationTestHitScanSystem.EnableLagCompensation = false;
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToHit;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.OnlyClientHitRegistered);

                // Test miss
                ResetHits();
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToMiss;
                for (int i = 0; i < ticksToRegisterHit; ++i)
                    testWorld.Tick();
                Assert.IsTrue(LagCompensationTestHitScanSystem.NoHitsRegistered);
            }
        }

        internal enum ColliderChangeType
        {
            NoColliderChange,
            ResizeCollider,
            ChangeColliderToSphere,
            ColliderMakeUnique,
        }
        internal enum DestroyType
        {
            DestroyVictimEntity,
            KeepVictimEntityAlive,
        }
        internal enum DeepCopyStrategy
        {
            DeepCopyOnlyDynamic,
            DeepCopyOnlyStatic,
            OnlyManualWhitelist,
            DeepCopyBoth,
            DeepCopyNeither,
        }
        internal enum ColliderStaticType
        {
            StaticVictimEntity,
            DynamicVictimEntity,
        }
        internal enum ColliderChangeTiming
        {
            ColliderChangeBeforeShot,
            ColliderChangeAfterShot,
        }
        internal enum IncrementalBroadphase
        {
            FullBVHRebuild,
            IncrementalBVH,
        }

        /// <summary>
        /// Customer issue where Lag Compensation was throwing BlobAsset exceptions when triggering on since-destroyed Entities.
        /// https://docs.google.com/document/d/18RZrbZfAwD37J2goBPODvqTcH9jkwCyeQN5wlmMqGVk/edit
        /// DOTS-10392
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitWithLagCompensationWithColliderChangeBeforeShot([Values]IncrementalBroadphase incrementalBroadphase, [Values]ColliderStaticType victimColliderType, [Values]DestroyType destroyType, [Values]DeepCopyStrategy deepCopyStrategy, [Values] ColliderChangeType colliderChangeType)
        {
            RunHitWithLagCompensationWithColliderChangeTest(incrementalBroadphase, ColliderChangeTiming.ColliderChangeBeforeShot, victimColliderType, destroyType, deepCopyStrategy, colliderChangeType);
        }

        /// <summary>
        /// Customer issue where Lag Compensation was throwing BlobAsset exceptions when triggering on since-destroyed Entities.
        /// https://docs.google.com/document/d/18RZrbZfAwD37J2goBPODvqTcH9jkwCyeQN5wlmMqGVk/edit
        /// DOTS-10392
        /// </summary>
        [Test]
        [UnityPlatform(RuntimePlatform.OSXEditor, RuntimePlatform.WindowsEditor)]
        public void HitWithLagCompensationWithColliderChangeAfterShot([Values]IncrementalBroadphase incrementalBroadphase, [Values] ColliderStaticType victimColliderType, [Values] DestroyType destroyType, [Values] DeepCopyStrategy deepCopyStrategy, [Values] ColliderChangeType colliderChangeType)
        {
            RunHitWithLagCompensationWithColliderChangeTest(incrementalBroadphase, ColliderChangeTiming.ColliderChangeAfterShot, victimColliderType, destroyType, deepCopyStrategy, colliderChangeType);
        }

        private static void RunHitWithLagCompensationWithColliderChangeTest(IncrementalBroadphase incrementalBroadphase, ColliderChangeTiming colliderChangeTiming, ColliderStaticType victimColliderType, DestroyType destroyType, DeepCopyStrategy deepCopyStrategy, ColliderChangeType colliderChangeType)
        {
            // TODO - Do a statistics based test (e.g. shooting 1k times).
            // TODO - What happens if interpolation delay changes DURING the simulation?
            // TODO - Use a variable time-step with degradation.
            using (var testWorld = new NetCodeTestWorld())
            {
                var config = new LagCompensationConfig
                {
                    ServerHistorySize = PhysicsWorldHistory.RawHistoryBufferMaxCapacity,
                    ClientHistorySize = 2,
                    DeepCopyDynamicColliders = deepCopyStrategy is DeepCopyStrategy.DeepCopyOnlyDynamic or DeepCopyStrategy.DeepCopyBoth,
                    DeepCopyStaticColliders = deepCopyStrategy is DeepCopyStrategy.DeepCopyOnlyStatic or DeepCopyStrategy.DeepCopyBoth,
                };
                InitTest(testWorld, victimColliderType == ColliderStaticType.StaticVictimEntity, incrementalBroadphase, out var clientEm, out var serverEm, config);

                // Give the netcode some time to spawn entities and settle on a good time synchronization
                for (int i = 0; i < 20; ++i)
                    testWorld.Tick();

                // Fetch the LagCompensationTestCube and LagCompensationTestSphere entities:
                GetCubeAndSphere(serverEm, out var serverVictimCubeEntity, out var serverVictimCollider, out var serverSphereEntity, out var serverSphereCollider);
                GetCubeAndSphere(clientEm, out var clientVictimCubeEntity, out var clientVictimCollider, out var clientSphereEntity, out var clientSphereCollider);

                // Now we have the bodies spawned, add them to the copy whitelist:
                {
                    var serverBodies = testWorld.GetSingletonRW<PhysicsWorldSingleton>(testWorld.ServerWorld).ValueRW.Bodies;
                    var clientBodies = testWorld.GetSingletonRW<PhysicsWorldSingleton>(testWorld.ClientWorlds[0]).ValueRW.Bodies;
                    ref var serverWhitelist = ref testWorld.GetSingletonRW<PhysicsWorldHistorySingleton>(testWorld.ServerWorld).ValueRW.DeepCopyRigidBodyCollidersWhitelist;
                    ref var clientWhitelist = ref testWorld.GetSingletonRW<PhysicsWorldHistorySingleton>(testWorld.ClientWorlds[0]).ValueRW.DeepCopyRigidBodyCollidersWhitelist;
                    AddBodiesToWhitelist("Server", serverBodies, ref serverWhitelist, serverVictimCubeEntity, serverSphereEntity, deepCopyStrategy);
                    AddBodiesToWhitelist("Client", clientBodies, ref clientWhitelist, clientVictimCubeEntity, clientSphereEntity, deepCopyStrategy);
                    static void AddBodiesToWhitelist(string context, NativeArray<RigidBody> bodies, ref NativeList<int> whitelist, Entity victimCubeEntity, Entity sphereEntity, DeepCopyStrategy deepCopyStrategy)
                    {
                        // Can be more than 3 as null entries can exist in the bodies list!
                        Assert.That(bodies.Length, Is.GreaterThanOrEqualTo(3), $"Sanity - PhysicsWorld Bodies count on {context}!");
                        if (deepCopyStrategy is not DeepCopyStrategy.OnlyManualWhitelist) return;
                        for (var bodyIdx = 0; bodyIdx < bodies.Length; bodyIdx++)
                        {
                            if (bodies[bodyIdx].Entity == victimCubeEntity || bodies[bodyIdx].Entity == sphereEntity)
                                whitelist.Add(bodyIdx);
                        }
                        Assert.That(bodies.Length, Is.GreaterThanOrEqualTo(2), $"Sanity! {context} must have bodies!");
                    }
                }

                // Ensure this collider change is "replicated" on both the client and server,
                // REGARDLESS of respective timelines.
                if (colliderChangeTiming == ColliderChangeTiming.ColliderChangeBeforeShot)
                {
                    PredictColliderChanges(colliderChangeType, serverEm, serverVictimCubeEntity, ref serverVictimCollider, serverSphereCollider);
                    PredictColliderChanges(colliderChangeType, clientEm, clientVictimCubeEntity, ref clientVictimCollider, clientSphereCollider);
                }

                // Give the netcode some MORE time to settle on a good time synchronization.
                // This should also apply the appropriate deep copy strategy after the above collider update.
                for (int i = 0; i < 50; ++i)
                    testWorld.Tick();

                // Client fires shot.
                LagCompensationTestHitScanSystem.ForcedInputLatencyTicks = 0;
                LagCompensationTestCommandSystem.ClientAimAtTarget = clientVictimCubeEntity;
                LagCompensationTestHitScanSystem.EnableLagCompensation = true;
                Assert.IsTrue(LagCompensationTestHitScanSystem.NoHitsRegistered, "Sanity check: Neither client nor server should have hit anything yet.");
                LagCompensationTestCommandSystem.ClientShotAction = LagCompensationTestCommandSystem.ShotType.ShootToHit;
                // Note: The simulated delay will mean the clients shot input arrives on the server in a future frame.

                testWorld.Tick();
                testWorld.Tick(); // Tick where the shot confirms on client.
                Assert.IsTrue(LagCompensationTestHitScanSystem.OnlyClientHitRegistered, "Sanity check: Expected the client shot to have landed by now.");

                testWorld.Tick();
                if (colliderChangeTiming == ColliderChangeTiming.ColliderChangeAfterShot)
                {
                    // Why only the client?
                    // 1. The client is obviously ahead of the server.
                    // 2. We're emulating here that the collider change is predicted.
                    // 3. Thus, if the client predicts the collider change on tick T5 (two frames before the shot),
                    // the server will also predict it on T5 (two frames before the shot).

                    // NOTE: WE DON'T CARE ABOUT INPUT INDETERMINISM LEADING TO COLLIDER SIZE TO BE INCORRECTLY PREDICTED,
                    // BECAUSE OF COURSE THAT WILL FAIL!
                    PredictColliderChanges(colliderChangeType, clientEm, clientVictimCubeEntity, ref clientVictimCollider, clientSphereCollider);
                }

                // Delete the LagCompensationTestCube entity on the server,
                // The simulated delay will mean the clients shot input arrives on the server in a future frame.
                if (destroyType == DestroyType.DestroyVictimEntity)
                {
                    //Debug.Log($"Destroying victim entity: {serverVictimCubeEntity} to trigger Physics BlobAsset bug...");
                    serverEm.DestroyEntity(serverVictimCubeEntity);

                    // HACK: Destroying is a multi-step process, as ICleanupComponentData's exist.
                    // See GhostDespawnParallelJob, and the fact that entity deletion is deferred until all clients have
                    // acked a snapshot containing the deletion.

                    // Unfortunately for us, this takes many more ticks, which means we have to begin this destroy
                    // operation earlier (so that it arrives on the client almost exactly one tick after the client shot),
                    // and the clients ack needs to arrive in the input packet BEFORE the input packet needs ot be processed.
                    // These two facts makes this test very hard to reason about, and very fragile, so we brute force
                    // the deletion here instead, by removing the GhostCleanup component manually.

                    // Note: It therefore still is possible for the client to send a hit for a since-deleted ghost entity,
                    // but it's rare in practice due to this deferred deletion. But rare = common at N4E scales.

                    serverEm.RemoveComponent<GhostCleanup>(serverVictimCubeEntity);
                }

                // Server performs hit detection on historic CollisionWorld,
                // thus must return the PREVIOUS (i.e. unmodified) collider (when deep copy is enabled).
                for (int i = 0; i < k_TicksToRegisterHit; ++i)
                {
                    testWorld.Tick();
                }

                Assert.IsTrue(LagCompensationTestHitScanSystem.BothHitsRegistered, "Sanity: Expected the hit to have registered now on BOTH the client and server!");
                switch (destroyType)
                {
                    case DestroyType.DestroyVictimEntity:
                        Assert.IsTrue(LagCompensationTestHitScanSystem.ClientVictimEntityStillExists, "Sanity: Expected only the client to hit an ALIVE entity, and server a dead one!");
                        Assert.IsFalse(LagCompensationTestHitScanSystem.ServerVictimEntityStillExists, "Sanity: Expected only the client to hit an ALIVE entity, and server a dead one!");
                        break;
                    case DestroyType.KeepVictimEntityAlive:
                        Assert.IsTrue(LagCompensationTestHitScanSystem.ClientVictimEntityStillExists, "Sanity: Expected both entities to be alive!");
                        Assert.IsTrue(LagCompensationTestHitScanSystem.ServerVictimEntityStillExists, "Sanity: Expected both entities to be alive!");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(destroyType), destroyType, null);
                }

                if (colliderChangeTiming == ColliderChangeTiming.ColliderChangeAfterShot)
                {
                    // Finally, after we know the server hit occurred,
                    // the server simulates the tick that resizes the collider.
                    if(serverEm.Exists(serverVictimCubeEntity))
                        PredictColliderChanges(colliderChangeType, serverEm, serverVictimCubeEntity, ref serverVictimCollider, serverSphereCollider);
                }

                // Even when we Destroy the Entity, it should still be returned by the collision hit,
                // thus we check both of these are identical.
                Assert.AreEqual(clientVictimCubeEntity, LagCompensationTestHitScanSystem.ClientRayCastHit.Value.Entity, "Expecting to hit the client victim entity!");
                var serverRayCastHit = LagCompensationTestHitScanSystem.ServerRayCastHit.Value;
                Assert.AreEqual(serverVictimCubeEntity, serverRayCastHit.Entity, "Expecting to hit the server victim entity!");

                // Also ensure the hit INFO is mostly deterministic:
                var hitDistance = math.length(serverRayCastHit.Position - LagCompensationTestHitScanSystem.ClientRayCastHit.Value.Position);
                var hitRayFraction = math.length(serverRayCastHit.Fraction - LagCompensationTestHitScanSystem.ClientRayCastHit.Value.Fraction);
                var hitNormalDot = math.dot(serverRayCastHit.SurfaceNormal, LagCompensationTestHitScanSystem.ClientRayCastHit.Value.SurfaceNormal);
                Debug.Log($"ServerRayCastHit vs ClientRayCastHit: hitDistance: {hitDistance}, hitRayFraction: {hitRayFraction}, hitNormalDot: {hitNormalDot}!");

                // We now compare the hits on the server vs the client, ensuring hits are "mostly deterministic".
                // HOWEVER: We should only do so if there is an expectation that it'll actually be correct.
                // The non-deep-copy scenario is essentially "undefined behaviour", in terms of expectations.
                var isCopyingTheRightTypeOfCollider = victimColliderType == ColliderStaticType.StaticVictimEntity
                    ? config.DeepCopyStaticColliders
                    : config.DeepCopyDynamicColliders;
                var isDeepCopyingCorrectly = deepCopyStrategy is DeepCopyStrategy.DeepCopyBoth or DeepCopyStrategy.OnlyManualWhitelist
                                             || isCopyingTheRightTypeOfCollider;
                if (isDeepCopyingCorrectly)
                {
                    AssertInRange(hitDistance, 0f, allowedTolerance: 0.1f, "RayCastHit.Position between the hit on the client, and the hit on the server!");
                    AssertInRange(hitRayFraction, 0f, allowedTolerance: 0.02f, "RayCastHit.Fraction (i.e. ray.distance / ray.length) between the hit on the client, and the hit on the server!");
                    AssertInRange(hitNormalDot, 1f, allowedTolerance: 0.02f, "RayCastHit.SurfaceNormal between the hit on the client, and the hit on the server!");
                }

                static void AssertInRange(float testedValue, float expectedValue, float allowedTolerance, string reasoning)
                {
                    var rawDelta = testedValue - expectedValue;
                    var isInBounds = math.abs(rawDelta) <= allowedTolerance;
                    if (!isInBounds)
                    {
                        reasoning = $"Expected {testedValue} to BE WITHIN {expectedValue}±{allowedTolerance}, but it wasn't! Value was {testedValue} (a delta of ?{rawDelta})! " + reasoning;
                        Assert.Fail(reasoning);
                    }
                }
            }
        }

        internal static unsafe void PredictColliderChanges(ColliderChangeType colliderChangeType, EntityManager em, Entity victimCubeEntity, ref PhysicsCollider victimCollider, PhysicsCollider sphereCollider)
        {
            // Reading: https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/master/PhysicsSamples/Assets/9.%20Modify
            // MUST be predicted.
            // I.e. Client and server change the victim box collider in some fun ways, on the same "serverTick".

            em.CompleteAllTrackedJobs(); // Fixes safety issues.
            switch (colliderChangeType)
            {
                case ColliderChangeType.NoColliderChange:
                    break;
                case ColliderChangeType.ResizeCollider:
                    // Resizes ALL boxColliders which share this BlobAsset Geometry!
                    var boxCollider = ((BoxCollider*) victimCollider.ColliderPtr);
                    var boxGeometry = boxCollider->Geometry;
                    boxGeometry.Size = BoxColliderGeometryResizeSize;
                    boxCollider->Geometry = boxGeometry;
                    break;
                case ColliderChangeType.ChangeColliderToSphere:
                    // Change the collider type of ONLY this collider:
                    victimCollider.Value = sphereCollider.Value;
                    em.SetComponentData(victimCubeEntity, victimCollider);
                    break;
                case ColliderChangeType.ColliderMakeUnique:
                    victimCollider.MakeUnique(victimCubeEntity, em);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(colliderChangeType), colliderChangeType, null);
            }

            victimCollider = em.GetComponentData<PhysicsCollider>(victimCubeEntity);
            em.CompleteAllTrackedJobs(); // Fixes safety issues.
        }

        private static void GetCubeAndSphere(EntityManager em, out Entity victimCubeEntity, out Unity.Physics.PhysicsCollider victimCollider, out Entity sphereEntity, out Unity.Physics.PhysicsCollider sphereCollider)
        {
            using var colliderQuery = em.CreateEntityQuery(ComponentType.ReadWrite<Unity.Physics.PhysicsCollider>());
            var colliderEntities = colliderQuery.ToEntityArray(Allocator.Temp);
            var colliderColliders = colliderQuery.ToComponentDataArray<Unity.Physics.PhysicsCollider>(Allocator.Temp);
            victimCubeEntity = colliderEntities[0];
            sphereEntity = colliderEntities[1];
            victimCollider = colliderColliders[0];
            sphereCollider = colliderColliders[1];

            Assert.IsTrue(victimCollider.IsValid);
            Assert.IsTrue(sphereCollider.IsValid);
            // Defensive! Swap them if we selected wrong due to query ToComponentDataArray order non-determinism!
            if (victimCollider.Value.Value.Type != ColliderType.Box)
            {
                (victimCollider, sphereCollider) = (sphereCollider, victimCollider);
                (victimCubeEntity, sphereEntity) = (sphereEntity, victimCubeEntity);
            }
            Assert.AreEqual(ColliderType.Box, victimCollider.Value.Value.Type);
            Assert.AreEqual(ColliderType.Sphere, sphereCollider.Value.Value.Type);
        }

        private static void InitTest(NetCodeTestWorld testWorld, bool useStaticColliders, IncrementalBroadphase broadphaseMode, out EntityManager clientEm, out EntityManager serverEm, LagCompensationConfig config)
        {
            testWorld.DriverSimulatedDelay = 50; // Each way! I.e. Testing lag compensation with a MINIMUM of 100ms ping.
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.NetCode.Physics,");
            testWorld.TestSpecificAdditionalAssemblies.Add("Unity.Physics,");

            testWorld.Bootstrap(true,
                typeof(TestAutoInGameSystem),
                typeof(LagCompensationTestCubeMoveSystem),
                typeof(LagCompensationTestCommandCommandSendSystem),
                typeof(LagCompensationTestCommandCommandReceiveSystem),
                typeof(LagCompensationTestCommandSystem),
                typeof(LagCompensationTestHitScanSystem));

            var cubeGameObject = new GameObject("LagCompensationTestCube");
            cubeGameObject.AddComponent<UnityEngine.BoxCollider>().size = new Vector3(BoxColliderGeometryOriginalSize, BoxColliderGeometryOriginalSize, BoxColliderGeometryOriginalSize);
            var sphereGameObject = new GameObject("LagCompensationTestSphere");
            sphereGameObject.transform.position = new Vector3(0, -5, 0); // Y pos moves it out of the way!
            sphereGameObject.AddComponent<UnityEngine.SphereCollider>().radius = SphereColliderRadiusSize;
            var playerGameObject = new GameObject("LagCompensationTestPlayer");
            playerGameObject.transform.position = new Vector3(0, 0, 0);
            playerGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new LagCompensationTestPlayerConverter();
            var ghostAuth = playerGameObject.AddComponent<GhostAuthoringComponent>();
            ghostAuth.DefaultGhostMode = GhostMode.OwnerPredicted;

            if (!useStaticColliders) cubeGameObject.AddComponent<Rigidbody>().useGravity = false;
            if (!useStaticColliders) sphereGameObject.AddComponent<Rigidbody>().useGravity = false;
            if (!useStaticColliders) playerGameObject.AddComponent<Rigidbody>().useGravity = false;

            Assert.IsTrue(testWorld.CreateGhostCollection(playerGameObject, cubeGameObject, sphereGameObject));

            testWorld.CreateWorlds(true, 1);

            serverEm = testWorld.ServerWorld.EntityManager;
            clientEm = testWorld.ClientWorlds[0].EntityManager;
            serverEm.CreateSingleton(config);
            clientEm.CreateSingleton(config);
            var step = PhysicsStep.Default;
            step.IncrementalStaticBroadphase = broadphaseMode == IncrementalBroadphase.IncrementalBVH;
            step.IncrementalDynamicBroadphase = broadphaseMode == IncrementalBroadphase.IncrementalBVH;
            step.MultiThreaded = 0;
            step.SimulationType = SimulationType.UnityPhysics;
            step.SolverIterationCount = 1;
            serverEm.CreateSingleton(step);
            clientEm.CreateSingleton(step);
            testWorld.Connect(maxSteps: 32);

            ResetHits();
        }

        private static void ResetHits()
        {
            LagCompensationTestHitScanSystem.ClientRayCastHit = default;
            LagCompensationTestHitScanSystem.ServerRayCastHit = default;
            LagCompensationTestHitScanSystem.ClientVictimEntityStillExists = default;
            LagCompensationTestHitScanSystem.ServerVictimEntityStillExists = default;
            LagCompensationTestCommandSystem.ClientShotAction = default;
        }
    }
}
