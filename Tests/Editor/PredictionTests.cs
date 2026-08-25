#pragma warning disable CS0618 // Disable Entities.ForEach obsolete warnings
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Core;
using Unity.Entities;
using UnityEngine;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.TestTools;
using Random = UnityEngine.Random;

namespace Unity.NetCode.Tests
{
    //FIXME this will break serialization. It is non handled and must be documented
    [GhostEnabledBit]
    struct BufferWithReplicatedEnableBits: IBufferElementData, IEnableableComponent
    {
        public byte value;
    }

    //Added to the ISystem state entity, track the number of time a system update has been called
    struct SystemExecutionCounter : IComponentData
    {
        public int value;
    }

    class PredictionTestConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            //Transform is replicated, Owner is replicated (components with sizes)
            baker.AddComponent(entity, new GhostOwner());
            //Buffer with enable bits, replicated
            //TODO: missing: Buffer with enable bits, no replicated fields. This break serialization
            //baker.AddBuffer<BufferWithReplicatedEnableBits>().ResizeUninitialized(3);
            baker.AddBuffer<EnableableBuffer>(entity).ResizeUninitialized(3);
            //Empty enable flags
            baker.AddComponent<EnableableFlagComponent>(entity);
            //Non empty enable flags
            baker.AddComponent(entity, new ReplicatedEnableableComponentWithNonReplicatedField{value = 9999});
        }
    }

    struct CountSimulationFromSpawnTick : IComponentData
    {
        public int Value;
    }

    class GhostWithRollbackConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new CountSimulationFromSpawnTick{Value = 0});
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class PredictionTestPredictionSystem : SystemBase
    {
        partial struct IncrementPostionJob : IJobEntity
        {
            public float DeltaTime;
            public void Execute(ref LocalTransform trans)
            {
                trans.Position.x += DeltaTime * 60.0f;
            }
        }

        public static bool s_IsEnabled;
        protected override void OnUpdate()
        {
            if (!s_IsEnabled)
                return;
            new IncrementPostionJob{DeltaTime = SystemAPI.Time.DeltaTime}.ScheduleParallel();
        }
    }

    /// <summary>Client-only misprediction: pushes the predicted transform right on every predicted tick, client-side only.
    /// The server leaves the ghost at the origin, so every snapshot is the authoritative (0,0,0) correction and the client
    /// must be pulled back to it. A ghost that latches onto a stale prediction-history backup keeps drifting right instead.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial struct MispredictClientTransformEachTickSystem : ISystem
    {
        public const float MovePerTick = 10f;
        public static bool s_Enabled;
        public void OnUpdate(ref SystemState state)
        {
            if (!s_Enabled) return;
            if (state.World.IsServer()) return; // The host world is authoritative; only the pure client should mispredict.
            foreach (var trans in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<Simulate, GhostInstance>())
                trans.ValueRW.Position.x += MovePerTick;
        }
    }

    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    [UpdateBefore(typeof(GhostReceiveSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class InvalidateAllGhostDataBeforeUpdate : SystemBase
    {
        protected override void OnCreate()
        {
            EntityManager.AddComponent<SystemExecutionCounter>(SystemHandle);
        }

        protected override void OnUpdate()
        {
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var tick = networkTime.ServerTick;
            if(!tick.IsValid)
                return;
            //Do not invalidate full ticks. The backup is not restored in that case
            if(!networkTime.IsPartialTick)
                return;

            foreach (var (trans, buffer, comp, ent) in SystemAPI.Query<RefRW<LocalTransform>,DynamicBuffer<EnableableBuffer>,RefRW<ReplicatedEnableableComponentWithNonReplicatedField>>().WithEntityAccess().WithAll<Simulate, GhostInstance>())
            {
                for (int el = 0; el < buffer.Length; ++el)
                    buffer.ElementAt(el) = new EnableableBuffer { value = 100*(int)tick.SerializedData };

                // for (int el = 0; el < nonReplicatedBuffer.Length; ++el)
                //     nonReplicatedBuffer[el] = new BufferWithReplicatedEnableBits { value = (byte)tick.SerializedData };

                trans.ValueRW.Position = new float3(-10 * tick.SerializedData, -10 * tick.SerializedData, -10 * tick.SerializedData);
                trans.ValueRW.Scale = -10f*tick.SerializedData;
                comp.ValueRW.value = -10*(int)tick.SerializedData;
                EntityManager.SetComponentEnabled<ReplicatedEnableableComponentWithNonReplicatedField>(ent, false);
                EntityManager.SetComponentEnabled<EnableableFlagComponent>(ent, false);
            }
            var counter = SystemAPI.GetComponentRW<SystemExecutionCounter>(SystemHandle);
            ++counter.ValueRW.value;
        }
    }
    [DisableAutoCreation]
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CheckRestoreFromBackupIsCorrect : SystemBase
    {
        protected override void OnCreate()
        {
            EntityManager.AddComponent<SystemExecutionCounter>(SystemHandle);
        }

        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            if(!tick.IsValid)
                return;


            foreach (var (trans, buffer, comp, ent) in SystemAPI.Query<LocalTransform,DynamicBuffer<EnableableBuffer>,ReplicatedEnableableComponentWithNonReplicatedField>().WithEntityAccess().WithAll<Simulate, GhostInstance>())
            {
                Assert.IsTrue(trans.Position.x > 0f);
                Assert.IsTrue(trans.Position.y > 0f);
                Assert.IsTrue(trans.Position.z > 0f);
                Assert.IsTrue(math.abs(1f - trans.Scale) < 1e-4f);

                //enable bits must be replicated
                Assert.IsTrue(EntityManager.IsComponentEnabled<ReplicatedEnableableComponentWithNonReplicatedField>(ent));
                Assert.IsTrue(EntityManager.IsComponentEnabled<EnableableFlagComponent>(ent));
                //This component is not replicated. As such its values is never restored.
                Assert.AreEqual(-10*(int)tick.SerializedData, comp.value);
                for (int el = 0; el < buffer.Length; ++el)
                        Assert.AreEqual(1000 * (el+1), buffer[el].value);
            }
            var counter = SystemAPI.GetComponentRW<SystemExecutionCounter>(SystemHandle);
            ++counter.ValueRW.value;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    partial struct CheckElapsedTime : ISystem
    {
        private double SinceFirstUpdate;
        private double LastElapsedTime;
        public void OnUpdate(ref SystemState state)
        {
            var timestep = state.World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
            var time = SystemAPI.Time;
            if (SinceFirstUpdate == 0.0)
            {
                SinceFirstUpdate = time.ElapsedTime;
            }
            Assert.GreaterOrEqual(time.ElapsedTime, LastElapsedTime);
            //the elapsed time must be always an integral multiple of the time step
            Assert.LessOrEqual(math.fmod(time.ElapsedTime, timestep), 1e-6);
            //the relative elapsed time since last update should also be equal to the timestep. If the timestep is changed
            //before the last update, this may be not true
            var totalElapsedSinceFirstUpdate = math.fmod(time.ElapsedTime - SinceFirstUpdate,  timestep);
            var elapsedTimeSinceLastUpdate = math.fmod(time.ElapsedTime - LastElapsedTime,  timestep);
            Assert.LessOrEqual(elapsedTimeSinceLastUpdate, 1e-6);
            Assert.LessOrEqual(totalElapsedSinceFirstUpdate, 1e-6);
            LastElapsedTime = time.ElapsedTime;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    internal partial class CheckSkipFrameSystem : SystemBase
    {
        public struct Count : IComponentData
        {
            public NetworkTick LastProcessedServerTick;
            public int lastFrame;
            public int SkippedFrames;
        }

        protected override void OnCreate()
        {
            EntityManager.CreateSingleton(new Count());
        }

        protected override void OnUpdate()
        {
            ref var c = ref SystemAPI.GetSingletonRW<Count>().ValueRW;
            var tick = SystemAPI.GetSingleton<NetworkTime>().ServerTick;
            if (UnityEngine.Time.frameCount > c.lastFrame
                && c.lastFrame != 0
                && (UnityEngine.Time.frameCount - c.lastFrame) > 1)
            {
                ++c.SkippedFrames;
                UnityEngine.Debug.Log($"[{UnityEngine.Time.frameCount}] CheckSkipFrameSystem missed a Unity frame. Current frame {UnityEngine.Time.frameCount} last processed frame {c.lastFrame} - tick {tick}");
            }
            c.lastFrame = UnityEngine.Time.frameCount;
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial struct CountNumberOfRollbacksSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var time = SystemAPI.GetSingleton<NetworkTime>();
            foreach (var (data, instance) in SystemAPI.Query<RefRW<CountSimulationFromSpawnTick>, RefRO<GhostInstance>>().WithAll<Simulate>())
            {
                var spawnTick = instance.ValueRO.spawnTick;
                //don't check prediction spawned ghosts not initialized yet
                if(!spawnTick.IsValid)
                    return;
                if (!time.IsPartialTick && time.ServerTick.TicksSince(spawnTick) == 1)
                {
                    data.ValueRW.Value++;
                }
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    partial struct CheckGhostsAlwaysResumedFromLastPredictionBackupTick : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            //don't need to map
            foreach (var rollback in SystemAPI.Query<RefRW<CountSimulationFromSpawnTick>>().WithAll<GhostInstance>().WithAll<Simulate>())
            {
                ++rollback.ValueRW.Value;
            }
        }
    }

    internal class StructuralChangesConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            var b1 = baker.AddBuffer<EnableableBuffer_0>(entity);
            for (int i = 0; i < 3; ++i)
                b1.Add(new EnableableBuffer_0{value = 10+i});
            var b2 = baker.AddBuffer<EnableableBuffer_1>(entity);
            for (int i = 0; i < 3; ++i)
                b2.Add(new EnableableBuffer_1{value = 20+i});
            var b3 = baker.AddBuffer<EnableableBuffer_2>(entity);
            for (int i = 0; i < 3; ++i)
                b3.Add(new EnableableBuffer_2{value = 30+i});
            baker.AddComponent<EnableableComponent_0>(entity, new EnableableComponent_0{value = 1000});
            baker.AddComponent<EnableableComponent_1>(entity, new EnableableComponent_1{value = 2000});
            baker.AddComponent<EnableableComponent_3>(entity, new EnableableComponent_3{value = 3000});
            baker.AddComponent<Data>(entity, new Data{Value = 100});
            baker.AddComponent<CountSimulationFromSpawnTick>(entity);
        }
    }

    internal class AlwaysRollbackAllConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new GhostOwner());
            baker.AddComponent(entity, new AlwaysRollbackAllData());
        }
    }

    internal struct AlwaysRollbackAllData : IComponentData
    {
        [GhostField] public int Value;
    }

    // --- Scaffolding for the two AlwaysRollbackAllPredictedGhosts history-restore regression tests. ---

    /// <summary>Baked tag marking the "subject" ghosts the corrupt/check systems below operate on (not the driver).</summary>
    internal struct GlobalRollbackSubject : IComponentData {}

    /// <summary>Client-only tag, added mid-test to force a structural change (chunk move) on a subject.</summary>
    internal struct GlobalRollbackMovedTag : IComponentData {}

    /// <summary>Subject ghost: a replicated value plus the subject tag. Predicted and throttled (static, or dynamic at a low send rate) so it must roll back via prediction history.</summary>
    internal class GlobalRollbackSubjectConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new Data { Value = 0 });
            baker.AddComponent<GlobalRollbackSubject>(entity);
        }
    }

    /// <summary>Driver ghost: a replicated value the server bumps every tick, keeping globalRollbackTick advancing so subjects roll back every full tick.</summary>
    internal class GlobalRollbackDriverConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new Data { Value = 0 });
        }
    }

    /// <summary>Subject that also carries an enable-bit-only replicated component (no ghost fields), for the enable-bit layout-check regression.</summary>
    internal class EnableBitLayoutSubjectConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new Data { Value = 0 });
            baker.AddComponent<GlobalRollbackSubject>(entity);
            baker.AddComponent<EnableableFlagComponent>(entity);
        }
    }

    /// <summary>Child of the layout-check subject: a replicated child component whose presence is toggled to exercise the child layout check.</summary>
    internal class ChildLayoutComponentConverter : TestNetCodeAuthoring.IConverter
    {
        public void Bake(GameObject gameObject, IBaker baker)
        {
            var entity = baker.GetEntity(TransformUsageFlags.Dynamic);
            baker.AddComponent(entity, new ChildData { Value = 0 });
        }
    }

    /// <summary>Corrupts every subject's replicated value right before GhostUpdateSystem on full ticks, so only the global-rollback history restore can fix it.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostUpdateSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CorruptGlobalRollbackSubjects : SystemBase
    {
        public const int Sentinel = -987654321;
        public static bool s_Enabled;
        protected override void OnUpdate()
        {
            if (!s_Enabled) return;
            if (World.IsServer()) return; // Skip the host world's client systems: it holds authoritative state, so there is no misprediction to corrupt.
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!networkTime.ServerTick.IsValid) return;
            foreach (var data in SystemAPI.Query<RefRW<Data>>().WithAll<GhostInstance, GlobalRollbackSubject>())
                data.ValueRW.Value = Sentinel;
        }
    }

    /// <summary>After GhostUpdateSystem, counts subjects still holding the corruption sentinel — i.e. ones the rollback failed to restore.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostUpdateSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial class CheckGlobalRollbackSubjectsRestored : SystemBase
    {
        public static bool s_Enabled;
        public static int s_NotRestored;
        public static int s_Checked;
        protected override void OnUpdate()
        {
            if (!s_Enabled) return;
            if (World.IsServer()) return; // Only the pure client mispredicts; skip the host world so the static counters reflect one client.
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!networkTime.ServerTick.IsValid) return;
            foreach (var data in SystemAPI.Query<RefRO<Data>>().WithAll<GhostInstance, GlobalRollbackSubject>())
            {
                ++s_Checked;
                if (data.ValueRO.Value == CorruptGlobalRollbackSubjects.Sentinel)
                    ++s_NotRestored;
            }
        }
    }

    /// <summary>Applies a one-time client-only divergence to every subject inside the prediction loop, gated on
    /// IsFirstTimeFullyPredictingTick so rollback re-simulations never re-apply it - this is what bakes a misprediction
    /// into the prediction history.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    internal partial struct MispredictStaticSubjectsOnce : ISystem
    {
        public const int Sentinel = -987654321;
        public static bool s_Armed;
        public void OnUpdate(ref SystemState state)
        {
            if (!s_Armed) return;
            if (state.World.IsServer()) return; // Host world is authoritative; the misprediction must only land on the pure client.
            if (!SystemAPI.GetSingleton<NetworkTime>().IsFirstTimeFullyPredictingTick) return;
            foreach (var data in SystemAPI.Query<RefRW<Data>>().WithAll<Simulate, GlobalRollbackSubject>())
                data.ValueRW.Value = Sentinel;
            s_Armed = false;
        }
    }

    internal partial class PredictionTests
    {
        static void BumpGlobalRollbackDriver(NetCodeTestWorld testWorld, Entity driverServer)
        {
            var em = testWorld.ServerWorld.EntityManager;
            var data = em.GetComponentData<Data>(driverServer);
            data.Value += 1;
            em.SetComponentData(driverServer, data);
        }

        [Test]
        [Description("Regression for the global-rollback history lookup: a predicted ghost that changes chunk (structural change) between globalRollbackTick and the latest backup must still roll back, since its history lives in the previous chunk's ring rather than its current one.")]
        public void AlwaysRollbackAll_RestoresGhostThatChangedChunk([Values] GhostOptimizationMode subjectOptimizationMode)
        {
            CorruptGlobalRollbackSubjects.s_Enabled = false;
            CheckGlobalRollbackSubjectsRestored.s_Enabled = false;
            CheckGlobalRollbackSubjectsRestored.s_NotRestored = 0;
            CheckGlobalRollbackSubjectsRestored.s_Checked = 0;

            using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 30; // ~2 ticks each way, so globalRollbackTick sits several ticks behind the structural change.
            testWorld.Bootstrap(true, typeof(CorruptGlobalRollbackSubjects), typeof(CheckGlobalRollbackSubjectsRestored));

            var subjectGO = new GameObject("Subject");
            subjectGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackSubjectConverter();
            var subjectConfig = subjectGO.AddComponent<GhostAuthoringComponent>();
            subjectConfig.DefaultGhostMode = GhostMode.Predicted;
            subjectConfig.OptimizationMode = subjectOptimizationMode;
            // Dynamic keeps sending; throttle it so most ticks lack a fresh snapshot at globalRollbackTick and fall onto the history ring (static already goes silent once settled).
            if (subjectOptimizationMode == GhostOptimizationMode.Dynamic)
                subjectConfig.MaxSendRate = 10;

            var driverGO = new GameObject("Driver");
            driverGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackDriverConverter();
            var driverConfig = driverGO.AddComponent<GhostAuthoringComponent>();
            driverConfig.DefaultGhostMode = GhostMode.Predicted;
            driverConfig.OptimizationMode = GhostOptimizationMode.Dynamic;

            Assert.IsTrue(testWorld.CreateGhostCollection(subjectGO, driverGO));
            testWorld.CreateWorlds(true, 1);
            testWorld.SetAlwaysRollbackAllPredictedGhosts(true);

            const int subjectCount = 8;
            for (int i = 0; i < subjectCount; ++i)
            {
                var e = testWorld.SpawnOnServer(subjectGO);
                testWorld.ServerWorld.EntityManager.SetComponentData(e, new Data { Value = 1000 + i });
            }
            var driverServer = testWorld.SpawnOnServer(driverGO);

            testWorld.Connect(maxSteps: 32); // DriverSimulatedDelay lengthens the handshake past the default budget.
            testWorld.GoInGame();

            // Converge so the subjects settle off globalRollbackTick (static stops sending; dynamic drops to its low send rate); bump the driver each tick.
            for (int i = 0; i < 48; ++i)
            {
                BumpGlobalRollbackDriver(testWorld, driverServer);
                testWorld.Tick();
            }

            var subjectQuery = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GlobalRollbackSubject>());
            var subjects = subjectQuery.ToEntityArray(Allocator.Temp);
            Assert.AreEqual(subjectCount, subjects.Length);

            // Structural change on half the subjects: they move to a new chunk, while the other half keep the old
            // chunk (and its ring) alive. Their history at globalRollbackTick now lives in that old chunk's ring.
            for (int i = 0; i < subjects.Length; i += 2)
                testWorld.ClientWorlds[0].EntityManager.AddComponent<GlobalRollbackMovedTag>(subjects[i]);

            CorruptGlobalRollbackSubjects.s_Enabled = true;
            CheckGlobalRollbackSubjectsRestored.s_Enabled = true;

            for (int i = 0; i < 16; ++i)
            {
                BumpGlobalRollbackDriver(testWorld, driverServer);
                testWorld.Tick();
            }

            Assert.Greater(CheckGlobalRollbackSubjectsRestored.s_Checked, 0, "Check system never ran - the test isn't exercising the rollback path.");
            Assert.AreEqual(0, CheckGlobalRollbackSubjectsRestored.s_NotRestored,
                "A subject that changed chunk was left holding the corruption sentinel: the global rollback failed to find its history in the previous chunk's ring.");
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description("Regression for UUM-131597: a predicted ghost that changes chunk on a partial tick (with the default RollbackPredictionOnStructuralChanges) must still restore from the prediction-history backup in the chunk it occupied at backup time, not fail the lookup and roll all the way back to an old snapshot.")]
        public void HistoryBackup_RestoresGhostThatChangedChunkOnPartialTick()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var subjectGO = new GameObject("Subject");
            subjectGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackSubjectConverter();
            var subjectConfig = subjectGO.AddComponent<GhostAuthoringComponent>();
            subjectConfig.DefaultGhostMode = GhostMode.Predicted;
            // Static so the subject goes silent: with no fresh snapshot to roll back to, the partial-tick continuation
            // must go through the prediction-history backup (the path this bug lives in).
            subjectConfig.OptimizationMode = GhostOptimizationMode.Static;

            Assert.IsTrue(testWorld.CreateGhostCollection(subjectGO));
            testWorld.CreateWorlds(true, 1);

            const int subjectCount = 8;
            for (int i = 0; i < subjectCount; ++i)
                testWorld.SpawnOnServer(subjectGO);

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 8; ++i)
                testWorld.Tick();

            // Land exactly on a full tick so the following ticks are partial.
            var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
            testWorld.TickClientWorld((1 - time.ServerTickFraction) / 60f);
            Assert.IsFalse(testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).IsPartialTick);

            var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
            Assert.IsTrue(lastBackupTick.IsValid);

            var clientEm = testWorld.ClientWorlds[0].EntityManager;
            var subjects = clientEm.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GlobalRollbackSubject>())
                .ToEntityArray(Allocator.Temp);
            Assert.AreEqual(subjectCount, subjects.Length);

            // First partial only arms lastPredictedTickWasPartial (its previous tick was full, so no restore yet).
            const float partialDT = 1f / (60f * 4f); // three partials fit within one full tick.
            testWorld.TickClientWorld(partialDT);

            // Move half the subjects to a new, un-backed chunk; the rest stay put as a control.
            for (int i = 0; i < subjects.Length; i += 2)
                clientEm.AddComponent<GlobalRollbackMovedTag>(subjects[i]);

            testWorld.TickClientWorld(partialDT); // The restore fires here.
            Assert.AreEqual(lastBackupTick, testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value);

            // Every subject - moved or not - must restore from the backup tick. Before the fix, moved subjects failed the
            // lookup on their new chunk and rolled back to a much older snapshot.
            for (int i = 0; i < subjects.Length; i++)
            {
                var startTick = clientEm.GetComponentData<PredictedGhost>(subjects[i]).PredictionStartTick;
                Assert.AreEqual(lastBackupTick, startTick,
                    $"Subject {i} (moved:{i % 2 == 0}) re-predicted from {startTick} instead of the backup tick {lastBackupTick}.");
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description("Re-adding a replicated component (a layout-changing structural move) must not restore the zeroed backup slot from the entity's old chunk; the ghost rolls back to the snapshot instead, keeping the component's authoritative value.")]
        public void HistoryBackup_LayoutChangingMove_RollsBackToSnapshotNotStaleBackup()
        {
            const int authoritativeValue = 12345;
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var subjectGO = new GameObject("Subject");
            subjectGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackSubjectConverter();
            var subjectConfig = subjectGO.AddComponent<GhostAuthoringComponent>();
            subjectConfig.DefaultGhostMode = GhostMode.Predicted;
            subjectConfig.OptimizationMode = GhostOptimizationMode.Static; // goes silent, forcing the partial-tick history path
            // RollbackPredictionOnStructuralChanges left default (true).

            Assert.IsTrue(testWorld.CreateGhostCollection(subjectGO));
            testWorld.CreateWorlds(true, 1);

            var server = testWorld.SpawnOnServer(subjectGO);
            testWorld.ServerWorld.EntityManager.SetComponentData(server, new Data { Value = authoritativeValue });

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 8; ++i)
                testWorld.Tick();

            var clientEm = testWorld.ClientWorlds[0].EntityManager;
            var subject = clientEm.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GlobalRollbackSubject>())
                .GetSingletonEntity();
            Assert.AreEqual(authoritativeValue, clientEm.GetComponentData<Data>(subject).Value); // converged

            void LandOnFullTick() => testWorld.TickClientWorld((1 - testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction) / 60f);
            LandOnFullTick();
            const float partialDT = 1f / (60f * 4f);

            // Remove the replicated component, then run a full tick so its backup slot is captured absent (zeroed).
            clientEm.RemoveComponent<Data>(subject);
            testWorld.TickClientWorld(1f / 60f);
            testWorld.TickClientWorld(partialDT); // arm lastPredictedTickWasPartial

            // Re-add it (its last backup slot is now zeroed), then a partial tick triggers the moved-chunk restore path.
            clientEm.AddComponent<Data>(subject);
            testWorld.TickClientWorld(partialDT);

            // The stale (zeroed) backup must be rejected in favor of a snapshot rollback; without the fix this reads 0.
            Assert.AreEqual(authoritativeValue, clientEm.GetComponentData<Data>(subject).Value);
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description("Re-adding an enable-bit-only replicated component (no ghost fields) is a layout-changing structural move: its old-chunk backup slot was captured absent (change-version 0), so the moved-chunk restore must be rejected in favor of a snapshot rollback, keeping the authoritative enabled state instead of the stale backup bit.")]
        public void HistoryBackup_LayoutChangingMove_EnableBitOnlyComponent_RollsBackToSnapshotNotStaleBackup()
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var subjectGO = new GameObject("Subject");
            subjectGO.AddComponent<TestNetCodeAuthoring>().Converter = new EnableBitLayoutSubjectConverter();
            var subjectConfig = subjectGO.AddComponent<GhostAuthoringComponent>();
            subjectConfig.DefaultGhostMode = GhostMode.Predicted;
            subjectConfig.OptimizationMode = GhostOptimizationMode.Static; // goes silent, forcing the partial-tick history path

            Assert.IsTrue(testWorld.CreateGhostCollection(subjectGO));
            testWorld.CreateWorlds(true, 1);

            var server = testWorld.SpawnOnServer(subjectGO);
            testWorld.ServerWorld.EntityManager.SetComponentEnabled<EnableableFlagComponent>(server, true); // authoritative = enabled

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 8; ++i)
                testWorld.Tick();

            var clientEm = testWorld.ClientWorlds[0].EntityManager;
            var subject = clientEm.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GlobalRollbackSubject>())
                .GetSingletonEntity();
            Assert.IsTrue(clientEm.IsComponentEnabled<EnableableFlagComponent>(subject)); // converged to the authoritative enabled state

            void LandOnFullTick() => testWorld.TickClientWorld((1 - testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction) / 60f);
            LandOnFullTick();
            const float partialDT = 1f / (60f * 4f);

            // Remove the enable-bit component, then a full tick so its backup slot is captured absent (change-version 0).
            clientEm.RemoveComponent<EnableableFlagComponent>(subject);
            testWorld.TickClientWorld(1f / 60f);
            testWorld.TickClientWorld(partialDT); // arm lastPredictedTickWasPartial

            // Re-add it disabled (opposite of the authoritative enabled state); a partial tick triggers the moved-chunk restore path.
            clientEm.AddComponent<EnableableFlagComponent>(subject);
            clientEm.SetComponentEnabled<EnableableFlagComponent>(subject, false);
            testWorld.TickClientWorld(partialDT);

            // The stale (zeroed) backup bit must be rejected in favor of a snapshot rollback; without the fix this stays disabled.
            Assert.IsTrue(clientEm.IsComponentEnabled<EnableableFlagComponent>(subject));
        }

        [Test]
        [DisableSingleWorldHostTest]
        [Description("Re-adding a replicated CHILD component is a layout-changing structural move: the child's slot in the root's old-chunk backup was captured absent (change-version 0), so when the root moves chunks the moved-chunk restore must be rejected in favor of a snapshot rollback, keeping the child's authoritative value instead of the stale/zeroed backup.")]
        public void HistoryBackup_LayoutChangingMove_ChildComponent_RollsBackToSnapshotNotStaleBackup()
        {
            const int authoritativeValue = 12345;
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var subjectGO = new GameObject("Subject");
            subjectGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackSubjectConverter();
            var subjectConfig = subjectGO.AddComponent<GhostAuthoringComponent>();
            subjectConfig.DefaultGhostMode = GhostMode.Predicted;
            subjectConfig.OptimizationMode = GhostOptimizationMode.Static; // inert with child components (CanBeStaticOptimized needs none); the client-only ticks below are what force the history path

            var childGO = new GameObject("Child");
            childGO.transform.parent = subjectGO.transform;
            childGO.AddComponent<TestNetCodeAuthoring>().Converter = new ChildLayoutComponentConverter();

            Assert.IsTrue(testWorld.CreateGhostCollection(subjectGO));
            testWorld.CreateWorlds(true, 1);

            var server = testWorld.SpawnOnServer(subjectGO);
            var serverEm = testWorld.ServerWorld.EntityManager;
            var serverChild = serverEm.GetBuffer<LinkedEntityGroup>(server)[1].Value;
            serverEm.SetComponentData(serverChild, new ChildData { Value = authoritativeValue });

            testWorld.Connect();
            testWorld.GoInGame();
            for (int i = 0; i < 8; ++i)
                testWorld.Tick();

            var clientEm = testWorld.ClientWorlds[0].EntityManager;
            var subject = clientEm.CreateEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<GlobalRollbackSubject>())
                .GetSingletonEntity();
            var clientChild = clientEm.GetBuffer<LinkedEntityGroup>(subject)[1].Value;
            Assert.AreEqual(authoritativeValue, clientEm.GetComponentData<ChildData>(clientChild).Value); // converged

            void LandOnFullTick() => testWorld.TickClientWorld((1 - testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction) / 60f);
            LandOnFullTick();
            const float partialDT = 1f / (60f * 4f);

            // Remove the replicated child component, then a full tick so the child's backup slot is captured absent (zeroed).
            clientEm.RemoveComponent<ChildData>(clientChild);
            testWorld.TickClientWorld(1f / 60f);
            testWorld.TickClientWorld(partialDT); // arm lastPredictedTickWasPartial

            // Re-add the child component (its backup slot is now zeroed) and move the root to a new chunk (so the restore
            // takes the moved-chunk fallback). The partial tick then triggers the moved-chunk restore path.
            clientEm.AddComponent<ChildData>(clientChild);
            clientEm.AddComponent<GlobalRollbackMovedTag>(subject);
            testWorld.TickClientWorld(partialDT);

            // The stale (zeroed) child backup must be rejected in favor of a snapshot rollback; without the fix this reads 0.
            Assert.AreEqual(authoritativeValue, clientEm.GetComponentData<ChildData>(clientChild).Value);
        }

        [Test]
        [Description("A static-optimized predicted ghost mispredicted client-side never receives a corrective snapshot (server state is unchanged, so static optimization sends nothing). Only AlwaysRollbackAllPredictedGhosts can fix it: with the flag OFF the misprediction sticks forever, with it ON the global rollback re-simulates it back to the authoritative state. See the 'TODO: BUG' note in GhostUpdateSystem.GetPredictionStartTick.")]
        public void StaticPredictedGhost_ClientMisprediction_CorrectedOnlyWithAlwaysRollbackAll([Values] bool alwaysRollbackAllGhosts)
        {
            MispredictStaticSubjectsOnce.s_Armed = false;

            using var testWorld = new NetCodeTestWorld();
            testWorld.DriverSimulatedDelay = 30; // a few ticks of latency, so globalRollbackTick sits behind the mispredicted tick (as it would in the real "client shoots a door" case).
            testWorld.Bootstrap(true, typeof(MispredictStaticSubjectsOnce));

            var subjectGO = new GameObject("Subject");
            subjectGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackSubjectConverter();
            var subjectConfig = subjectGO.AddComponent<GhostAuthoringComponent>();
            subjectConfig.DefaultGhostMode = GhostMode.Predicted;
            subjectConfig.OptimizationMode = GhostOptimizationMode.Static; // goes silent once settled, so no corrective snapshot can ever arrive.

            var driverGO = new GameObject("Driver");
            driverGO.AddComponent<TestNetCodeAuthoring>().Converter = new GlobalRollbackDriverConverter();
            var driverConfig = driverGO.AddComponent<GhostAuthoringComponent>();
            driverConfig.DefaultGhostMode = GhostMode.Predicted;
            driverConfig.OptimizationMode = GhostOptimizationMode.Dynamic; // keeps receiving snapshots, like the player who fired the shot, so globalRollbackTick keeps advancing.

            Assert.IsTrue(testWorld.CreateGhostCollection(subjectGO, driverGO));
            testWorld.CreateWorlds(true, 1);
            testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);

            const int subjectCount = 8;
            const int authoritativeBase = 1000;
            for (int i = 0; i < subjectCount; ++i)
            {
                var e = testWorld.SpawnOnServer(subjectGO);
                testWorld.ServerWorld.EntityManager.SetComponentData(e, new Data { Value = authoritativeBase + i });
            }
            var driverServer = testWorld.SpawnOnServer(driverGO);

            testWorld.Connect(maxSteps: 32); // generous handshake budget to cover the simulated latency.
            testWorld.GoInGame();

            const float subTickDt = 1f / 60f / 4f;
            var clientEm = testWorld.ClientWorlds[0].EntityManager;

            // Converge and let the subjects go static (server stops sending them); keep the driver moving.
            for (int i = 0; i < 192; ++i)
            {
                BumpGlobalRollbackDriver(testWorld, driverServer);
                testWorld.Tick(subTickDt);
            }

            using var subjectQuery = clientEm.CreateEntityQuery(ComponentType.ReadOnly<Data>(), ComponentType.ReadOnly<GlobalRollbackSubject>());
            Assert.AreEqual(subjectCount, subjectQuery.CalculateEntityCount());

            // Sanity: the client agrees with the server before the misprediction.
            foreach (var d in subjectQuery.ToComponentDataArray<Data>(Allocator.Temp))
                Assert.AreNotEqual(MispredictStaticSubjectsOnce.Sentinel, d.Value, "Subjects should hold their authoritative value before being mispredicted.");

            // Bake a one-time client-only misprediction into the prediction history, then let the sim run on.
            MispredictStaticSubjectsOnce.s_Armed = true;
            for (int i = 0; i < 48; ++i)
            {
                BumpGlobalRollbackDriver(testWorld, driverServer);
                testWorld.Tick(subTickDt);
            }

            var finalValues = subjectQuery.ToComponentDataArray<Data>(Allocator.Temp);
            int stillMispredicted = 0;
            foreach (var d in finalValues)
                if (d.Value == MispredictStaticSubjectsOnce.Sentinel)
                    ++stillMispredicted;

            if (alwaysRollbackAllGhosts)
                Assert.AreEqual(0, stillMispredicted,
                    "With AlwaysRollbackAllPredictedGhosts ON, the static predicted ghost should be rolled back and re-simulated to its authoritative state even with no new snapshot.");
            else
                Assert.AreEqual(subjectCount, stillMispredicted,
                    "With the flag OFF, a static predicted ghost with no corrective snapshot stays mispredicted forever - the bug this flag fixes.");
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        [Description("With the flag ON, GhostSnapshotLastBackupTick.Capacity should be at least minRingCapacity (4 per the current ring formula in GhostPredictionHistorySystem). With it OFF, capacity stays at 1 (memory parity with the legacy single-tick design).")]
        public void RingCapacity_FollowsFlag([Values] bool alwaysRollbackAllPredictedGhosts)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new AlwaysRollbackAllConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            if (alwaysRollbackAllPredictedGhosts)
            {
                var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
                clientTickRate.AlwaysRollbackAllPredictedGhosts = true;
                var configEntity = testWorld.TryGetSingletonEntity<ClientTickRate>(testWorld.ClientWorlds[0]);
                testWorld.ClientWorlds[0].EntityManager.SetComponentData(configEntity, clientTickRate);
            }

            testWorld.SpawnOnServer(ghostGameObject);
            testWorld.Connect();
            testWorld.GoInGame();

            for (int i = 0; i < 8; ++i)
                testWorld.Tick();

            var range = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]);

            if (alwaysRollbackAllPredictedGhosts)
            {
                // Contractual minimum from the formula is 4 (minRingCapacity in
                // GhostPredictionHistorySystem). The actual value is typically higher because
                // commandInterpolationDelay * 2 + worstSendInterval > 4 in practice.
                Assert.GreaterOrEqual(range.Capacity, 4,
                    "Ring capacity should be at least minRingCapacity (4) when flag is on.");
            }
            else
            {
                Assert.AreEqual(1, range.Capacity,
                    "Ring capacity should be 1 when flag is off (memory parity with single-tick design).");
            }
        }

        /// <summary>
        /// Allocates a dummy backup slot stamped with the given tick. The ring only reads each slot's
        /// tickValue, so a header-sized blob is enough to exercise its slot-selection / lookup logic.
        /// </summary>
        static unsafe System.IntPtr AllocRingSlot(NetworkTick tick)
        {
            var slot = (PredictionBackupState*)UnsafeUtility.Malloc(PredictionBackupState.GetHeaderSize(), 16, Allocator.Persistent);
            slot->tickValue = tick.SerializedData;
            return (System.IntPtr)slot;
        }

        [Test]
        [Description("AllocNew sizes the ring to the requested capacity and starts with no valid slots.")]
        public unsafe void PredictionBackupRing_AllocNew_IsEmptyWithRequestedCapacity()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(4).Ref;
            Assert.AreEqual(4, ring.Capacity);
            Assert.AreEqual(0, ring.CountValid());
            ring.FreeAll();
        }

        [Test]
        [Description("TryGetSlotForTick returns the slot stamped with the exact tick, and misses for any other.")]
        public unsafe void PredictionBackupRing_TryGetSlotForTick_MatchesExactTick()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(4).Ref;
            var slot10 = AllocRingSlot(new NetworkTick(10));
            ring.GetSlots()[2] = slot10;
            Assert.IsTrue(ring.TryGetSlotForTick(new NetworkTick(10), out var found));
            Assert.AreEqual(slot10, found);
            Assert.IsFalse(ring.TryGetSlotForTick(new NetworkTick(11), out _));
            ring.FreeAll();
        }

        [Test]
        [Description("SelectSlotForWrite dedups onto the slot already holding the wanted tick.")]
        public unsafe void PredictionBackupRing_SelectSlotForWrite_Dedups()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(4).Ref;
            ring.GetSlots()[1] = AllocRingSlot(new NetworkTick(10));
            var selection = ring.SelectSlotForWrite(new NetworkTick(10));
            Assert.IsTrue(selection.IsRePredict);
            Assert.AreEqual(1, selection.Index);
            ring.FreeAll();
        }

        [Test]
        [Description("SelectSlotForWrite fills an empty slot before evicting any valid one.")]
        public unsafe void PredictionBackupRing_SelectSlotForWrite_PrefersEmptyOverEviction()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(3).Ref;
            ring.GetSlots()[0] = AllocRingSlot(new NetworkTick(10));
            var selection = ring.SelectSlotForWrite(new NetworkTick(11));
            Assert.IsFalse(selection.IsRePredict);
            Assert.AreNotEqual(0, selection.Index);
            ring.FreeAll();
        }

        [Test]
        [Description("SelectSlotForWrite evicts the oldest valid slot when the ring is full.")]
        public unsafe void PredictionBackupRing_SelectSlotForWrite_EvictsOldestWhenFull()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(2).Ref;
            ring.GetSlots()[0] = AllocRingSlot(new NetworkTick(11));
            ring.GetSlots()[1] = AllocRingSlot(new NetworkTick(10)); // oldest
            var selection = ring.SelectSlotForWrite(new NetworkTick(12));
            Assert.IsFalse(selection.IsRePredict);
            Assert.AreEqual(1, selection.Index);
            ring.FreeAll();
        }

        [Test]
        [Description("Resize grows the ring, migrating populated slots and dropping logically-cleared ones.")]
        public unsafe void PredictionBackupRing_Resize_GrowsMigratingPopulatedSlots()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(2).Ref;
            var slot10 = AllocRingSlot(new NetworkTick(10));
            ring.GetSlots()[0] = slot10;
            ring.GetSlots()[1] = AllocRingSlot(NetworkTick.Invalid); // logically cleared - dropped, not migrated

            ref var grown = ref ring.Resize(4).Ref;
            Assert.AreEqual(4, grown.Capacity);
            Assert.AreEqual(1, grown.CountValid());
            Assert.IsTrue(grown.TryGetSlotForTick(new NetworkTick(10), out var found));
            Assert.AreEqual(slot10, found, "The populated slot must be migrated by reference, not reallocated.");
            grown.FreeAll();
        }

        [Test]
        [Description("LogicalClear invalidates every slot's tick but keeps the allocations for reuse.")]
        public unsafe void PredictionBackupRing_LogicalClear_InvalidatesButKeepsSlots()
        {
            ref var ring = ref PredictionBackupRing.AllocNew(2).Ref;
            ring.GetSlots()[0] = AllocRingSlot(new NetworkTick(10));
            ring.GetSlots()[1] = AllocRingSlot(new NetworkTick(11));
            ring.LogicalClear();
            Assert.AreEqual(0, ring.CountValid());
            Assert.AreNotEqual(System.IntPtr.Zero, ring.GetSlots()[0], "Allocations must remain so the next write reuses them.");
            ring.FreeAll();
        }

        [Test]
        [Description("Regression: Commit points predictionState at the serverTick slot, not the ring's max-tick slot, so a stale future slot (left by an AlwaysRollbackAllPredictedGhosts toggle plus a backwards ServerTick correction) can't restore future state.")]
        public unsafe void PredictionBackupStore_Commit_PointsPredictionStateAtServerTickSlotNotMaxTick()
        {
            using var world = new World("PredictionBackupStoreTest");
            var chunk = world.EntityManager.GetChunk(world.EntityManager.CreateEntity());

            using var store = new PredictionBackupStore();
            store.Allocate(8);

            // Stage a fresh capacity-8 ring whose backed-up tick is 99 (mimics a ring grown while AlwaysRollback was on).
            var slot99 = AllocRingSlot(new NetworkTick(99));
            store.BeginFrame(1);
            var writer = store.AsWriter();
            writer.MarkChunkUsed(chunk);
            writer.StageFreshRing(chunk, slot99, capacity: 8);
            store.Commit(8, new NetworkTick(99));

            // Inject a stale FUTURE slot (tick 107) that a never-cleared ring would still hold after a backwards
            // jump, so the ring's max valid tick (107) differs from the just-backed-up tick (99).
            var singleton = new GhostPredictionHistoryState();
            store.PopulateSingleton(ref singleton);
            Assert.IsTrue(singleton.PredictionRings.TryGetValue(chunk.SequenceNumber, out var ringPtr));
            var slot107 = AllocRingSlot(new NetworkTick(107));
            ringPtr.Ref.SetSlot(1, slot107);

            // Re-commit for the backed-up tick 99. predictionState must track the tick-99 slot, not the future 107.
            store.BeginFrame(1);
            store.AsWriter().MarkChunkUsed(chunk);
            store.Commit(8, new NetworkTick(99));

            store.PopulateSingleton(ref singleton);
            Assert.IsTrue(singleton.PredictionState.TryGetValue(chunk.SequenceNumber, out var newest));
            Assert.AreEqual(slot99, (System.IntPtr)newest.Value, "predictionState must point at the serverTick (99) slot, not the stale future (107) slot.");
        }

        [Test]
        [Category(NetcodeTestCategories.Foundational)]
        [Category(NetcodeTestCategories.Smoke)]
        public void PredictionTickEvolveCorrectly([Values] bool alwaysRollbackAllGhosts, [Values((uint)0x229321, (uint)100, (uint)0x7FFF011F, (uint)0x7FFFFF00, (uint)0x7FFFFFF0, (uint)0x7FFFF1F0)] uint serverTickData)
        {
            var serverTick = new NetworkTick(serverTickData);
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(PredictionTestPredictionSystem));
            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);
            testWorld.SetServerTick(serverTick);
            testWorld.Connect();
            testWorld.GoInGame();
            var serverEnt = testWorld.SpawnOnServer(0);
            Assert.AreNotEqual(Entity.Null, serverEnt);
            for(int i=0;i<64;++i)
                testWorld.Tick();
        }

        [Test]
        public void PartialPredictionTicksAreRolledBack([Values] bool alwaysRollbackAllGhosts)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(PredictionTestPredictionSystem));
                PredictionTestPredictionSystem.s_IsEnabled = true;

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);

                var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                Assert.AreNotEqual(Entity.Null, serverEnt);
                var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<EnableableBuffer>(serverEnt);
                for (int i = 0; i < buffer.Length; ++i)
                    buffer[i] = new EnableableBuffer { value = 1000 * (i + 1) };
                // var nonReplicatedBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<BufferWithReplicatedEnableBits>(serverEnt);
                // for (int i = 0; i < nonReplicatedBuffer.Length; ++i)
                //     nonReplicatedBuffer[i] = new BufferWithReplicatedEnableBits { value = (byte)(10 * (i + 1)) };

                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                // Let the game run for a bit so the ghosts are spawned on the client
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                var clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
                Assert.AreNotEqual(Entity.Null, clientEnt);

                var prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position;
                var prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position;

                for (int i = 0; i < 64; ++i)
                {
                    testWorld.Tick(1.0f / 60.0f / 4f);

                    var curServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt);
                    var curClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt);
                    testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs();
                    // Server does not do fractional ticks so it will not advance the position every frame
                    Assert.GreaterOrEqual(curServer.Position.x, prevServer.x);
                    testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                    // Client does fractional ticks and position should be always increasing
                    Assert.Greater(curClient.Position.x, prevClient.x);
                    prevServer = curServer.Position;
                    prevClient = curClient.Position;
                }
                // Stop updating, let it run for a while and check that they ended on the same value
                PredictionTestPredictionSystem.s_IsEnabled = false;
                for (int i = 0; i < 16; ++i)
                    testWorld.Tick();

                prevServer = testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position;
                prevClient = testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position;
                Assert.IsTrue(math.distance(prevServer, prevClient) < 0.01);
            }
        }

        [Test]
        [DisableSingleWorldHostTest] // A host is server-authoritative and runs no client prediction, so it cannot mispredict; this scenario only exists in a distinct client world.
        [Description("UUM-147343: at a very high client frame rate with ~zero command age (host/local client), a client-only misprediction pushed one tick ahead of the server bakes a divergence into the prediction-history backup. Once the client stops mispredicting, the authoritative snapshot (applied on partial ticks) must pull the ghost back to the origin - the stale backup must NOT be restored on every partial and latch the misprediction. AlwaysRollbackAllPredictedGhosts avoids this by clearing the rings on rollback.")]
        public void ClientMisprediction_DoesNotLatchOnStaleBackup([Values] bool alwaysRollbackAllGhosts)
        {
            PredictionTestPredictionSystem.s_IsEnabled = false; // Server keeps the ghost at the origin; only the client mispredicts.
            MispredictClientTransformEachTickSystem.s_Enabled = false;
            using var testWorld = new NetCodeTestWorld();
            testWorld.UseFakeSocketConnection = 0; // IPC => zero command age (host/local client).
            testWorld.Bootstrap(true, typeof(MispredictClientTransformEachTickSystem));

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            var tickRateEnt = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            testWorld.ServerWorld.EntityManager.SetComponentData(tickRateEnt, new ClientServerTickRate { SimulationTickRate = 30 });
            testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);

            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            const float fullDt = 1f / 30f;
            const float partialDt = fullDt / 8f; // ~240fps client.

            Entity clientEnt = Entity.Null;
            void Sync() { testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs(); testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs(); }
            float ClientServerOffset()
            {
                Sync();
                return math.abs(testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position.x
                    - testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position.x);
            }
            NetworkTick BackupTick() { Sync(); return testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value; }

            testWorld.Connect(1f / 240f, 64);
            testWorld.GoInGame();
            for (int i = 0; i < 16; ++i)
                testWorld.Tick(fullDt);

            clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
            Assert.AreNotEqual(Entity.Null, clientEnt);
            Assert.That(ClientServerOffset(), Is.EqualTo(0f), "Client and server should be identical before the misprediction.");

            // Land on a full tick, then advance the applied snapshot to the tick just below the one we bake next.
            var frac = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction;
            if (frac < 1f)
                testWorld.TickClientWorld((1f - frac) * fullDt);
            testWorld.TickServerWorld(fullDt);
            testWorld.TickClientWorld(partialDt);

            // Client predicts (and mispredicts) the next tick ahead of its snapshot, baking a stale backup.
            MispredictClientTransformEachTickSystem.s_Enabled = true;
            var startBackup = BackupTick();
            for (int i = 0; i < 12 && BackupTick() == startBackup; ++i)
                testWorld.TickClientWorld(partialDt);
            Assert.AreNotEqual(startBackup, BackupTick(), "Client should have crossed a full tick and recorded a (mispredicted) backup.");

            // Server sends the authoritative snapshot for that tick; client stops mispredicting. Every partial tick from here
            // rolls back to that (origin) snapshot, so the offset must collapse to zero and stay there - a ghost latching onto
            // the stale mispredicted backup would instead hold near the injected drift on every partial after the first.
            testWorld.TickServerWorld(fullDt);
            MispredictClientTransformEachTickSystem.s_Enabled = false;
            for (int i = 0; i < 6; ++i)
            {
                testWorld.TickClientWorld(partialDt);
                Assert.That(ClientServerOffset(), Is.EqualTo(0f).Within(0.001f),
                    $"Predicted ghost latched onto a stale prediction-history backup instead of being pulled back to the origin on partial tick {i} (alwaysRollbackAllGhosts={alwaysRollbackAllGhosts}).");
            }
        }

        internal enum PredictionRenderRegime
        {
            /// <summary>Client renders many frames per server tick.</summary>
            HighRefreshManyPartialTicks,
            /// <summary>Client dt == sim dt, landing on full-tick boundaries (no partial ticks).</summary>
            SameRateNoPartialTicks,
            /// <summary>Client dt == sim dt but phase-shifted, so every tick is partial.</summary>
            SameRateAlwaysPartialTicks,
            /// <summary>Client dt > sim dt (25fps vs 30hz), so the sim catches up multiple ticks per frame.</summary>
            BelowRefreshRate,
        }

        [Test]
        [DisableSingleWorldHostTest] // A host is server-authoritative and runs no client prediction, so it cannot mispredict; this scenario only exists in a distinct client world.
        [Description("UUM-147343 (regression coverage): a client-only misprediction (the server holds the ghost at the origin) must stay pulled toward the authoritative snapshot rather than drifting away, across every render regime crossed with AlwaysRollbackAllPredictedGhosts on/off and the ghost's send rate throttled or not. The dedicated stale-backup latch is covered by ClientMisprediction_DoesNotLatchOnStaleBackup.")]
        public void ClientMisprediction_ConvergesToSnapshot_AcrossRenderRegimes([Values] PredictionRenderRegime regime, [Values] bool alwaysRollbackAllGhosts, [Values] bool lowSendRate)
        {
            PredictionTestPredictionSystem.s_IsEnabled = false; // Server keeps the ghost at the origin; only the client mispredicts.
            MispredictClientTransformEachTickSystem.s_Enabled = false;
            using var testWorld = new NetCodeTestWorld();
            testWorld.UseFakeSocketConnection = 0; // IPC => zero command age (host/local client).
            testWorld.Bootstrap(true, typeof(MispredictClientTransformEachTickSystem));

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            if (lowSendRate)
                ghostConfig.MaxSendRate = 10;

            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);

            var tickRateEnt = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
            testWorld.ServerWorld.EntityManager.SetComponentData(tickRateEnt, new ClientServerTickRate { SimulationTickRate = 30 });
            testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);

            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            const float fullDt = 1f / 30f;
            const float highRefreshDt = fullDt / 8f; // ~240fps client.

            Entity clientEnt = Entity.Null;
            void Sync() { testWorld.ServerWorld.EntityManager.CompleteAllTrackedJobs(); testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs(); }
            float ClientServerOffset()
            {
                Sync();
                return math.abs(testWorld.ClientWorlds[0].EntityManager.GetComponentData<LocalTransform>(clientEnt).Position.x
                    - testWorld.ServerWorld.EntityManager.GetComponentData<LocalTransform>(serverEnt).Position.x);
            }

            // Advance client and server by one server tick's worth of time in the pattern the regime dictates. Both worlds tick
            // at the render rate (as an in-proc host does); the server only advances a full tick once enough dt has accumulated.
            void DriveOneServerTick()
            {
                switch (regime)
                {
                    case PredictionRenderRegime.HighRefreshManyPartialTicks:
                        for (int j = 0; j < 8; ++j)
                            testWorld.Tick(highRefreshDt);
                        break;
                    case PredictionRenderRegime.SameRateNoPartialTicks:
                    case PredictionRenderRegime.SameRateAlwaysPartialTicks:
                        testWorld.Tick(fullDt);
                        break;
                    case PredictionRenderRegime.BelowRefreshRate:
                        testWorld.Tick(1f / 25f);
                        break;
                }
            }

            testWorld.Connect(1f / 240f, 64);
            testWorld.GoInGame();
            for (int i = 0; i < 4; ++i)
                testWorld.Tick(fullDt);

            clientEnt = testWorld.TryGetSingletonEntity<GhostOwner>(testWorld.ClientWorlds[0]);
            Assert.AreNotEqual(Entity.Null, clientEnt);
            Assert.That(ClientServerOffset(), Is.EqualTo(0f), "Client and server should be identical before the misprediction.");

            // The same-rate regimes need the client aligned to a full tick; always-partial then shifts half a tick off it.
            if (regime == PredictionRenderRegime.SameRateNoPartialTicks || regime == PredictionRenderRegime.SameRateAlwaysPartialTicks)
            {
                var frac = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTickFraction;
                if (frac < 1f)
                    testWorld.TickClientWorld((1f - frac) * fullDt);
                if (regime == PredictionRenderRegime.SameRateAlwaysPartialTicks)
                    testWorld.TickClientWorld(fullDt / 2f);
            }

            MispredictClientTransformEachTickSystem.s_Enabled = true;
            float maxOffset = 0f;
            for (int cycle = 0; cycle < 8; ++cycle)
            {
                DriveOneServerTick();
                maxOffset = math.max(maxOffset, ClientServerOffset());
            }

            // Each combo settles at a known, deterministic divergence from the authoritative snapshot: further when the ghost's
            // send rate is throttled (staler snapshots) or the client renders below the sim rate (catch-up ticks). Assert that
            // exact peak; a latch or unbounded drift would blow past it.
            var expectedPeakTicks = regime switch
            {
                PredictionRenderRegime.HighRefreshManyPartialTicks or PredictionRenderRegime.SameRateNoPartialTicks => lowSendRate ? 3f : 1f,
                PredictionRenderRegime.SameRateAlwaysPartialTicks or PredictionRenderRegime.BelowRefreshRate => lowSendRate ? 4f : 2f,
                _ => throw new System.ArgumentOutOfRangeException(nameof(regime), regime, null),
            };
            Assert.That(maxOffset, Is.EqualTo(expectedPeakTicks * MispredictClientTransformEachTickSystem.MovePerTick).Within(1f),
                $"Predicted ghost diverged from the authoritative snapshot by an unexpected amount (regime={regime}, alwaysRollbackAllGhosts={alwaysRollbackAllGhosts}, lowSendRate={lowSendRate}).");
        }

#if !NETCODE_SNAPSHOT_HISTORY_SIZE_6
        // At history size 6 the server throttles snapshot send cadence (withholding sends until in-flight snapshots are acked), which breaks this test's prediction-ahead timing assumptions.
        [Test]
        [Description("Expect that MaxPredictAheadTimeMS a) caps the number of prediction ticks performed & b) adds some forced input latency.")]
        public void MaxPredictAheadTimeMS_Works([Values] bool alwaysRollbackAllGhosts)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true);

            var ghostGameObject = new GameObject();
            ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;

            const int one60HzTickMs = 17;
            testWorld.DriverSimulatedDelay = 150 - one60HzTickMs; // EACH WAY! Thus, adjusted to be a EstimatedRTT of ~300ms.
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 3);
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            var clientTickRate = NetworkTimeSystem.DefaultClientTickRate;
            clientTickRate.MaxPredictAheadTimeMS = 200;
            clientTickRate.ForcedInputLatencyTicks = 0;
            var singletonEntity = testWorld.TryGetSingletonEntity<ClientTickRate>(testWorld.ClientWorlds[0]);
            testWorld.ClientWorlds[0].EntityManager.SetComponentData(singletonEntity, clientTickRate);

            clientTickRate.MaxPredictAheadTimeMS = 200;
            clientTickRate.ForcedInputLatencyTicks = 6; // 100ms.
            singletonEntity = testWorld.TryGetSingletonEntity<ClientTickRate>(testWorld.ClientWorlds[1]);
            testWorld.ClientWorlds[1].EntityManager.SetComponentData(singletonEntity, clientTickRate);

            clientTickRate.MaxPredictAheadTimeMS = 500;
            clientTickRate.ForcedInputLatencyTicks = 3; // 50ms.
            singletonEntity = testWorld.TryGetSingletonEntity<ClientTickRate>(testWorld.ClientWorlds[2]);
            testWorld.ClientWorlds[2].EntityManager.SetComponentData(singletonEntity, clientTickRate);

            testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);

            testWorld.Connect(maxSteps:64);
            testWorld.GoInGame();

            for (int i = 0; i < 512; ++i)
                testWorld.Tick();

            var client0NetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[0]);
            var client0SnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[0]);
            var client1NetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[1]);
            var client1SnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[1]);
            var client2NetTime = testWorld.GetSingleton<NetworkTime>(testWorld.ClientWorlds[2]);
            var client2SnapshotAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ClientWorlds[2]);
            Debug.Log($"Client0: {client0NetTime} {(int)client0SnapshotAck.EstimatedRTT}ms \nClient1:{client1NetTime} {(int)client1SnapshotAck.EstimatedRTT}ms\nClient2:{client2NetTime} {(int)client2SnapshotAck.EstimatedRTT}ms");
            Assert.That(client0SnapshotAck.EstimatedRTT, Is.EqualTo(300).Within(20));
            Assert.That(client1SnapshotAck.EstimatedRTT, Is.EqualTo(300).Within(20));
            Assert.That(client2SnapshotAck.EstimatedRTT, Is.EqualTo(300).Within(20));
            Assert.That(client0NetTime.EffectiveInputLatencyTicks, Is.EqualTo(9).Within(2)); // ( ~18 ticks (~300ms) + 2 ticks for TargetCommandSlack - clamped at 12 ticks (200ms) = ~8 ticks.
            Assert.That(client1NetTime.EffectiveInputLatencyTicks, Is.EqualTo(9).Within(2)); // Same as above, as ForcedInputLatency is essentially ignored here, as its lower than 8 ticks, which is being forced by MaxPredictAheadMS.
            Assert.That(client2NetTime.EffectiveInputLatencyTicks, Is.EqualTo(3).Within(2)); // Expect it to be the same as its config value.
            Assert.That(client0NetTime.InputTargetTick.TicksSince(client0NetTime.ServerTick), Is.EqualTo(client0NetTime.EffectiveInputLatencyTicks));
            Assert.That(client1NetTime.InputTargetTick.TicksSince(client1NetTime.ServerTick), Is.EqualTo(client1NetTime.EffectiveInputLatencyTicks));
            Assert.That(client2NetTime.InputTargetTick.TicksSince(client2NetTime.ServerTick), Is.EqualTo(client2NetTime.EffectiveInputLatencyTicks));
            Assert.That(client0NetTime.PredictedTickIndex, Is.GreaterThan(12)); // ~200ms + 1 for partial tick.
            Assert.That(client1NetTime.PredictedTickIndex, Is.GreaterThan(12)); // ~200ms + 1 for partial tick.
            Assert.That(client2NetTime.PredictedTickIndex, Is.GreaterThan((6*3)-3)); // ~300ms - 3 ticks for ForcedInputLatency.
        }
#endif

        [DisableSingleWorldHostTest]
        public void HistoryBufferIsRollbackCorrectly([Values] bool alwaysRollbackAllGhosts, [Values(1, 20, 30, 40)] int ghostCount)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(PredictionTestPredictionSystem),
                    typeof(InvalidateAllGhostDataBeforeUpdate),
                    typeof(CheckRestoreFromBackupIsCorrect));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new PredictionTestConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);

                for (int i = 0; i < ghostCount; ++i)
                {
                    var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
                    var buffer = testWorld.ServerWorld.EntityManager.GetBuffer<EnableableBuffer>(serverEnt);
                    for (int el = 0; el < buffer.Length; ++el)
                        buffer[el] = new EnableableBuffer { value = 1000 * (el+ 1) };
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEnt, LocalTransform.FromPosition(new float3(0f, 10f, 100f)));
                    // var nonReplicatedBuffer = testWorld.ServerWorld.EntityManager.GetBuffer<BufferWithReplicatedEnableBits>(serverEnt);
                    // for (int el = 0; el < nonReplicatedBuffer.Length; ++el)
                    //     nonReplicatedBuffer[el] = new BufferWithReplicatedEnableBits { value = (byte)(10 * (el + 1)) };
                }
                // Connect and make sure the connection could be established
                testWorld.Connect();

                // Go in-game
                testWorld.GoInGame();

                PredictionTestPredictionSystem.s_IsEnabled = true;
                for (int i = 0; i < 64; ++i)
                {
                    testWorld.Tick(1.0f / 60.0f / 4f);
                }
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                PredictionTestPredictionSystem.s_IsEnabled = false;
                var counter1 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SystemExecutionCounter>(
                        testWorld.ClientWorlds[0].GetExistingSystem<InvalidateAllGhostDataBeforeUpdate>());
                var counter2 = testWorld.ClientWorlds[0].EntityManager.GetComponentData<SystemExecutionCounter>(
                    testWorld.ClientWorlds[0].GetExistingSystem<InvalidateAllGhostDataBeforeUpdate>());
                Assert.Greater(counter1.value, 0);
                Assert.Greater(counter2.value, 0);
                Assert.AreEqual(counter1.value, counter2.value);
            }
        }

        [Category(NetcodeTestCategories.Foundational)]
        [TestCase(90)]
        [TestCase(82)]
        [TestCase(45)]
        public void NetcodeClientPredictionRateManager_WillWarnWhenMismatchSimulationTickRate(int fixedStepRate)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true);
                testWorld.CreateWorlds(true, 1);
                testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;
                testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;

                // Connect and make sure the connection could be established
                testWorld.Connect();
                //Expect 2, one for server, one for the client
                LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                  "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                  "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");

                LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                  "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                  "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");

                //Check that the simulation tick rate are the same
                var clientRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(60, clientRate.SimulationTickRate);
                Assert.AreEqual(1, clientRate.PredictedFixedStepSimulationTickRatio);
                var serverTimeStep = testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                var clientTimestep = testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                Assert.That(serverTimeStep, Is.EqualTo(clientRate.SimulationFixedTimeStep));
                Assert.That(clientTimestep, Is.EqualTo(clientRate.SimulationFixedTimeStep));

                //Also check that if the value is overriden, it is still correctly set to the right value
                for (int i = 0; i < 8; ++i)
                {
                    testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;
                    testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().InternalRateManager.Timestep = 1f/fixedStepRate;
                    testWorld.Tick();
                    serverTimeStep = testWorld.ServerWorld.GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                    clientTimestep = testWorld.ClientWorlds[0].GetOrCreateSystemManaged<PredictedFixedStepSimulationSystemGroup>().Timestep;
                    LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                      "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                      "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                    LogAssert.Expect(LogType.Warning, $"The PredictedFixedStepSimulationSystemGroup.TimeStep is {1f/fixedStepRate}ms ({fixedStepRate}FPS) but should be equals to ClientServerTickRate.PredictedFixedStepSimulationTimeStep: {1f/60f}ms ({60f}FPS).\n" +
                                                      "The current timestep will be changed to match the ClientServerTickRate settings. You should never set the rate of this system directly with neither the PredictedFixedStepSimulationSystemGroup.TimeStep nor the RateManager.TimeStep method.\n " +
                                                      "Instead, you must always configure the desired rate by changing the ClientServerTickRate.PredictedFixedStepSimulationTickRatio property.");
                    Assert.That(clientTimestep, Is.EqualTo(clientRate.SimulationFixedTimeStep));
                    Assert.That(serverTimeStep, Is.EqualTo(clientRate.SimulationFixedTimeStep));
                }
            }
        }

        [Category(NetcodeTestCategories.Foundational)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
#if ENABLE_CORECLR
        [Explicit("CoreCLR: predicted fixed-step ElapsedTime accumulates float rounding beyond the 1e-6 tolerance for non-integer tick ratios, see https://jira.unity3d.com/browse/UUM-150429")]
#endif
        public void PredictedFixedStepSimulation_ElapsedTimeReportedCorrectly(int ratio)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true, typeof(CheckElapsedTime));
                testWorld.CreateWorlds(true, 1);

                //tick the world before connecting or finalizing the setup to mimic the fact the values has been changed by users
                //after the world creation later on.
                for(int i=0;i<10;++i)
                    testWorld.Tick();
                var tickRate = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(tickRate, new ClientServerTickRate
                {
                    PredictedFixedStepSimulationTickRatio = ratio
                });
                testWorld.Connect();
                //Check that the simulation tick rate are the same
                var clientRate = testWorld.GetSingleton<ClientServerTickRate>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(60, clientRate.SimulationTickRate);
                Assert.AreEqual(ratio, clientRate.PredictedFixedStepSimulationTickRatio);
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick();
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(1f / 30f);
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(1f / 45f);
                }
                for (int i = 0; i < 16; ++i)
                {
                    testWorld.Tick(1f / 117f);
                }
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        public void HistoryBufferIsPreservedOnStructuralChanges([Values] bool alwaysRollbackAllGhosts, [Values]GhostOptimizationMode ghostOptimizationMode, [Values]bool rollbackHistoryOnStructuralChange, [Values(1, 100)]int ghostCount)
        {
            void CheckPredictionPartialStepAndStartTick(NativeArray<Entity> entities, NetCodeTestWorld testWorld, NetworkTick currentPartialTick,
                NetworkTick lastBackupTick, bool expectIsPartialTick)
            {
                var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                // `rollbackHistoryOnStructuralChange` specifies that we should rollback to the snapshot if we cannot find the entry in the history buffer.
                Assert.That(clientTime.PredictedTickIndex, rollbackHistoryOnStructuralChange ? Is.InRange(1, 20) : Is.EqualTo(1));
                Assert.That(clientTime.IsPartialTick, Is.EqualTo(expectIsPartialTick));

                for (int i = 0; i < entities.Length; i++)
                {
                    var predictionCount = testWorld.ClientWorlds[0].EntityManager.GetComponentData<CountSimulationFromSpawnTick>(entities[i]).Value;
                    var predictedGhost = testWorld.ClientWorlds[0].EntityManager.GetComponentData<PredictedGhost>(entities[i]);
                    if (predictedGhost.AppliedTick == predictedGhost.PredictionStartTick)
                    {
                        Assert.AreEqual(currentPartialTick.TicksSince(predictedGhost.AppliedTick), predictionCount);
                    }
                    else
                    {
                        // Static ghosts attempt to rollback to the snapshot (when rollbackHistoryOnStructuralChange is true),
                        // but WHEN that snapshot is too old, the rollback is CLAMPED to the interpolationTick (5 ticks ago).
                        var rollbackAndResimulateTicks = lastBackupTick.TicksSince(predictedGhost.PredictionStartTick);
                        if (ghostOptimizationMode == GhostOptimizationMode.Static)
                            Assert.That(rollbackAndResimulateTicks, Is.Zero.Or.EqualTo(rollbackHistoryOnStructuralChange ? 5 : 1));
                        else Assert.That(rollbackAndResimulateTicks, Is.Zero);
                    }

                    //reset here the start tick, so next partial we will track and reset counters
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new CountSimulationFromSpawnTick());
                }
            }

            void CheckValues(NativeArray<Entity> entities, NetCodeTestWorld testWorld, NativeArray<int> expectedDataValue)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Assert.AreEqual(1000, testWorld.ClientWorlds[0].EntityManager.GetComponentData<EnableableComponent_0>(entities[i]).value);
                    Assert.AreEqual(2000, testWorld.ClientWorlds[0].EntityManager.GetComponentData<EnableableComponent_1>(entities[i]).value);
                    Assert.AreEqual(3000, testWorld.ClientWorlds[0].EntityManager.GetComponentData<EnableableComponent_3>(entities[i]).value);
                    if (testWorld.ClientWorlds[0].EntityManager.HasComponent<Data>(entities[i]))
                        Assert.AreEqual(expectedDataValue[i], testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(entities[i]).Value);
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_0>(entities[i]);
                        Assert.AreEqual(3, b.Length);
                        Assert.AreEqual(10, b[0].value);
                        Assert.AreEqual(11, b[1].value);
                        Assert.AreEqual(12, b[2].value);
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_1>(entities[i]);
                        Assert.AreEqual(3, b.Length);
                        Assert.AreEqual(20, b[0].value);
                        Assert.AreEqual(21, b[1].value);
                        Assert.AreEqual(22, b[2].value);
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_2>(entities[i]);
                        Assert.AreEqual(3, b.Length);
                        Assert.AreEqual(30, b[0].value);
                        Assert.AreEqual(31, b[1].value);
                        Assert.AreEqual(32, b[2].value);
                    }
                }
            }

            void InvalidateValues(NativeArray<Entity> entities, NetCodeTestWorld testWorld)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new EnableableComponent_0(){value = 0});
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new EnableableComponent_1(){value = 0});
                    testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new EnableableComponent_3(){value = 0});
                    if (testWorld.ClientWorlds[0].EntityManager.HasComponent<Data>(entities[i]))
                    {
                       testWorld.ClientWorlds[0].EntityManager.SetComponentData(entities[i], new Data(){Value = 0});
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_0>(entities[i]);
                        b.ElementAt(0).value = 0;
                        b.ElementAt(1).value = 0;
                        b.ElementAt(2).value = 0;
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_1>(entities[i]);
                        b.ElementAt(0).value = 0;
                        b.ElementAt(1).value = 0;
                        b.ElementAt(2).value = 0;
                    }
                    {
                        var b = testWorld.ClientWorlds[0].EntityManager.GetBuffer<EnableableBuffer_2>(entities[i]);
                        b.ElementAt(0).value = 0;
                        b.ElementAt(1).value = 0;
                        b.ElementAt(2).value = 0;
                    }
                }
            }

            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.Bootstrap(true,
                    typeof(PredictionTestPredictionSystem),
                    typeof(CheckGhostsAlwaysResumedFromLastPredictionBackupTick));

                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();
                var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                ghostConfig.DefaultGhostMode = GhostMode.Predicted;
                ghostConfig.OptimizationMode = ghostOptimizationMode; // Static-optimization prevents this ghost from rolling
                                                                      // back to the snapshot, so we RELY on the predicted history backup.
                ghostConfig.MaxSendRate = 10; // We ALSO don't want dynamic ghosts to constantly rollback to the snapshot,
                                             // as it makes it hard to verify prediction history rollback, so we prevent that via this.
                ghostConfig.RollbackPredictionOnStructuralChanges = rollbackHistoryOnStructuralChange;
                var ghostChild = new GameObject();
                ghostChild.transform.parent = ghostGameObject.transform;
                ghostChild.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();

                Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));

                testWorld.CreateWorlds(true, 1);
                testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);
                testWorld.Connect();
                testWorld.GoInGame();
                //sync clocks
                for(int i=0;i<16;++i)
                    testWorld.Tick();
                //spawn
                for (int i = 0; i < ghostCount; ++i)
                {
                    var serverEntity = testWorld.SpawnOnServer(ghostGameObject);
                    // Give each ghost a UNIQUE data value, to ensure we're not reading back a different ghost's values.
                    testWorld.ServerWorld.EntityManager.SetComponentData(serverEntity, new Data {Value = 100 + i});
                }

                testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<CheckGhostsAlwaysResumedFromLastPredictionBackupTick>().Enabled = false;
                //sync everything
                for(int i=0;i<64;++i)
                    testWorld.Tick();

                //Run one last time with a delta time such that we end up exactly on a full tick
                var time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                testWorld.TickClientWorld((1 - time.ServerTickFraction)/60f);

                time = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                Assert.IsFalse(time.IsPartialTick, $"time.IsPartialTick, server tick fraction is {time.ServerTickFraction}");

                var ghosts = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(typeof(GhostInstance));
                var entities = ghosts.ToEntityArray(Allocator.Temp);
                var dataValues = new NativeArray<int>(entities.Length, Allocator.Temp);
                for (int i = 0; i < dataValues.Length; ++i)
                    dataValues[i] = testWorld.ClientWorlds[0].EntityManager.GetComponentData<Data>(entities[i]).Value;
                Assert.AreEqual(ghostCount, entities.Length);
                CheckValues(entities, testWorld, dataValues);

                //run partial ticks and verify max 1 prediction step is done
                testWorld.ClientWorlds[0].Unmanaged.GetExistingSystemState<CheckGhostsAlwaysResumedFromLastPredictionBackupTick>().Enabled = true;
                var lastBackupTick = testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value;
                Assert.IsTrue(lastBackupTick.IsValid);
                //there is no partial tick restore in this tick because the last tick was a full tick. The continuation goes without actually
                //restoring from the backup.
                //TODO: would be nice to distinguish
                const float partialTickDT = (1f/(60f*4)); // We want to do exactly 3 partial ticks (the 4th step will trigger a full tick).
                testWorld.TickClientWorld(partialTickDT); // 1st PartialTick.
                var currentPartialTick = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick;
                CheckPredictionPartialStepAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick, expectIsPartialTick:true);
                //Now I can invalidate and check restore work properly
                InvalidateValues(entities, testWorld);
                testWorld.TickClientWorld(partialTickDT); // 2nd PartialTick.
                //run partial ticks and verify max 1 prediction step is done`
                Assert.AreEqual(testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value, lastBackupTick);
                CheckPredictionPartialStepAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick, expectIsPartialTick:true);
                CheckValues(entities, testWorld, dataValues);
                //change half of the entities. Backup should be still the same and so all values should be restored as they were at backup time
                for (int i = 0; i < entities.Length; i+=2)
                    testWorld.ClientWorlds[0].EntityManager.RemoveComponent<Data>(entities[i]);
                InvalidateValues(entities, testWorld);
                //What happen in this tick ? The client will receive a new snapshot from the server and rollback-prediction will occur,
                //causing a new backup being made for the same tick on the client. What the Data backup contains? because the component has been
                //removed, the value should be 0 on certain entities
                testWorld.TickServerWorld();
                testWorld.TickClientWorld(partialTickDT); // 3rd PartialTick.
                //run partial ticks and verify max 1 prediction step is done
                Assert.AreEqual(testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value, lastBackupTick);
                CheckPredictionPartialStepAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick, expectIsPartialTick:true);
                CheckValues(entities, testWorld, dataValues);
                //Add 1/4 of the entities back to the previous chunk. For these entities, we STILL EXPECT to find the backup.
                // Note: If we DID NOT restoreFromBackup below, the GhostField values within Data would now be stale...
                // But thanks to the partial tick, we restore REAL values.
                for (int i = 0; i < entities.Length; i += 4)
                {
                    testWorld.ClientWorlds[0].EntityManager.AddComponent<Data>(entities[i]);
                }
                InvalidateValues(entities, testWorld);
                testWorld.TickClientWorld(partialTickDT);  // Next FullTick!
                Assert.AreEqual(currentPartialTick.TickIndexForValidTick, testWorld.GetNetworkTime(testWorld.ClientWorlds[0]).ServerTick.TickIndexForValidTick);
                //A new backup has been made
                Assert.AreNotEqual(lastBackupTick, testWorld.GetSingleton<GhostSnapshotLastBackupTick>(testWorld.ClientWorlds[0]).Value);
                CheckPredictionPartialStepAndStartTick(entities, testWorld, currentPartialTick, lastBackupTick, expectIsPartialTick:false);
                CheckValues(entities, testWorld, dataValues);
            }
        }

        internal struct TestCommand : IInputComponentData
        {
            public int Value;
        }

        [Test(Description = "Tests that we have 0 margin for commands when using IPC")]
        public void MarginIsZeroWithIPC([Values] bool alwaysRollbackAllGhosts)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true);
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.Predicted;
                authoring.HasOwner = true;
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);
                testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);
                var entity = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, new GhostOwner()
                {
                    NetworkId = 1
                });
                testWorld.Connect();
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.GoInGame();

                for (int i = 0; i < 256; ++i)
                {
                    testWorld.Tick();

                    // Check that the margin is zero
                    var serverTime = testWorld.GetNetworkTime(testWorld.ServerWorld);
                    var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    var serverAck = testWorld.GetSingleton<NetworkSnapshotAck>(testWorld.ServerWorld);
                    Debug.Log($"[{i}] ST:{serverTime.ServerTick}, LastReceivedSnapshotByRemote:{serverAck.LastReceivedSnapshotByRemote}, LastReceivedSnapshotByLocal:{serverAck.LastReceivedSnapshotByLocal}, MostRecentFullCommandTick:{serverAck.MostRecentFullCommandTick}!");
                    if (serverAck.LastReceivedSnapshotByRemote.IsValid)
                        Assert.IsTrue(!serverTime.ServerTick.IsNewerThan(serverAck.LastReceivedSnapshotByLocal));
                    if (serverAck.MostRecentFullCommandTick.IsValid)
                        Assert.AreEqual(serverTime.ServerTick, serverAck.MostRecentFullCommandTick);
                }
            }
        }

        [Test(Description = "Tests that the client stay ahead of the server and never skip a prediction tick, even in presence of partial ticks and lower send rate.")]
        public void ClientNeverSkipAPredictionTick([Values] bool alwaysRollbackAllGhosts)
        {
            using (var testWorld = new NetCodeTestWorld())
            {
                //enable using IPC connection
                testWorld.UseFakeSocketConnection = 0;
                testWorld.Bootstrap(true, typeof(CheckSkipFrameSystem));
                var ghostGameObject = new GameObject();
                ghostGameObject.AddComponent<TestNetCodeAuthoring>().Converter = new StructuralChangesConverter();
                var authoring = ghostGameObject.AddComponent<GhostAuthoringComponent>();
                authoring.DefaultGhostMode = GhostMode.Predicted;
                authoring.HasOwner = true;
                testWorld.CreateGhostCollection(ghostGameObject);
                testWorld.CreateWorlds(true, 1);
                testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);
                var entity = testWorld.SpawnOnServer(ghostGameObject);
                testWorld.ServerWorld.EntityManager.SetComponentData(entity, new GhostOwner()
                {
                    NetworkId = 1
                });
                var ent = testWorld.TryGetSingletonEntity<ClientServerTickRate>(testWorld.ServerWorld);
                testWorld.ServerWorld.EntityManager.SetComponentData(ent, new ClientServerTickRate
                {
                    SimulationTickRate = 30,
                });
                testWorld.Connect(0.271f/60f, 64);
                testWorld.ClientWorlds[0].EntityManager.CompleteAllTrackedJobs();
                testWorld.GoInGame();
                var rnd = new Unity.Mathematics.Random(0x4000);
                for (int i = 0; i < 256; ++i)
                {
                    var dt = rnd.NextFloat(0.1f, 0.4f) / 60f;
                    testWorld.Tick(dt);
                    var serverTime = testWorld.GetNetworkTime(testWorld.ServerWorld);
                    var clientTime = testWorld.GetNetworkTime(testWorld.ClientWorlds[0]);
                    //check that when the server tick change, the client tick is already ahead so that the server always
                    //receive the right full tick.
                    if (clientTime.ServerTick.IsValid)
                    {
                        Assert.IsTrue(clientTime.ServerTick.IsNewerThan(serverTime.ServerTick), $"Expected client tick {clientTime.ServerTick}.{clientTime.ServerTickFraction} to be always ahead of the server to ensure full command tick update arrive in time, but server tick was already {serverTime.ServerTick}");
                    }
                }
                var count = testWorld.GetSingleton<CheckSkipFrameSystem.Count>(testWorld.ClientWorlds[0]);
                Assert.AreEqual(0, count.SkippedFrames, "Expect client does not skip any partial prediction or ticks.");
            }
        }

        [Test]
        [DisableSingleWorldHostTest]
        public void NetworkTimeSingleton_CorrectValuesInsidePredictionLoop([Values] bool alwaysRollbackAllGhosts)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, typeof(AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem), typeof(AssertNetworkTimeSingletonValuesCorrectAfterPredictionLoopSystem));
            testWorld.DriverSimulatedDelay = 40;
            testWorld.DriverSimulatedJitter = 20;
            testWorld.DriverSimulatedDrop = 20; // Interval, so 5%, or every 20th packet.
            var ghostGameObject = new GameObject();
            var ghostConfig = ghostGameObject.AddComponent<GhostAuthoringComponent>();
            ghostConfig.DefaultGhostMode = GhostMode.Predicted;
            Assert.IsTrue(testWorld.CreateGhostCollection(ghostGameObject));
            testWorld.CreateWorlds(true, 1);
            testWorld.SetAlwaysRollbackAllPredictedGhosts(alwaysRollbackAllGhosts);
            const float FrameTime = 1.0f / 60.0f;
            testWorld.Connect(FrameTime, 128);
            testWorld.GoInGame();
            // Spawn a new entity on the server. Server will start send snapshots now.
            var serverEnt = testWorld.SpawnOnServer(ghostGameObject);
            Assert.AreNotEqual(Entity.Null, serverEnt);

            // Tick for a while, using an extremely wobbly client step.
            AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem.Reset();
            var rand = Unity.Mathematics.Random.CreateFromIndex(10350135);
            for (int i = 0; i < 100; i++)
            {
                testWorld.Tick(rand.NextFloat(FrameTime * 0.5f, FrameTime * 1.5f));
            }

            AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem.Validate();
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
        internal partial struct AssertNetworkTimeSingletonValuesCorrectInsidePredictionLoopSystem : ISystem
        {
            public static bool HasHadPartialTickOnClient;
            public static bool HasHadFullTickOnClient;
            public static bool HasHadFinalPredictionTickOnClient;
            public static bool HasHadFinalPredictionTickOnServer;

            private NetworkTick m_LatestFullServerTick;
            private NetworkTick m_LastServerTickOnServer;
            private int m_LastFinalTickIndex;
            private double m_LastElapsedNetworkTimeOnServer;

            public static void Reset()
            {
                HasHadPartialTickOnClient = false;
                HasHadFullTickOnClient = false;
                HasHadFinalPredictionTickOnClient = false;
                HasHadFinalPredictionTickOnServer = false;
            }

            public static void Validate()
            {
                Assert.IsTrue(HasHadPartialTickOnClient);
                Assert.IsTrue(HasHadFullTickOnClient);
                Assert.IsTrue(HasHadFinalPredictionTickOnClient);
                Assert.IsTrue(HasHadFinalPredictionTickOnServer);
            }

            public void OnUpdate(ref SystemState state)
            {
                Assert.IsFalse(state.WorldUnmanaged.IsThinClient());
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                SystemAPI.GetSingleton<NetDebug>().Log($"[{state.WorldUnmanaged.Name}] [TestTick:{NetCodeTestWorld.TickIndex}] ServerTick:{networkTime.ServerTick.ToFixedString()} (fraction:{(int) (100 * networkTime.ServerTickFraction)}%), SimulationStepBatchSize:{networkTime.SimulationStepBatchSize}, ElapsedNT:{networkTime.ElapsedNetworkTime}\n<color=green>{networkTime.Flags}</color>");
                Assert.IsTrue(networkTime.IsInPredictionLoop);

                if (!networkTime.ServerTick.IsValid) return;

                SystemAPI.TryGetSingleton<ClientServerTickRate>(out var clientServerTickRate);
                clientServerTickRate.ResolveDefaults();

                if (state.WorldUnmanaged.IsClient())
                    AssertForClient(networkTime, SystemAPI.Time);
                else
                    AssertForServer(networkTime, clientServerTickRate, SystemAPI.Time);
                AssertForBoth(networkTime);
            }

            private void AssertForClient(NetworkTime networkTime, TimeData timeData)
            {
                Assert.IsFalse(networkTime.IsCatchUpTick, "IsCatchUpTick");
                Assert.NotZero(networkTime.ElapsedNetworkTime, "ElapsedNetworkTime");
                Assert.NotZero(timeData.DeltaTime, "DeltaTime");
                Assert.NotZero(timeData.ElapsedTime, "ElapsedTime");
                Assert.That(networkTime.PredictedTickIndex, Is.AtLeast(1), "PredictedTickIndex");
                Assert.That(networkTime.NumPredictedTicksExpected, Is.AtLeast(1), "NumPredictedTicksExpected");

                Assert.IsTrue(networkTime.ServerTick.IsNewerThan(networkTime.InterpolationTick), "ST.IsNewerThan(IT)");
                if (networkTime.IsPartialTick)
                {
                    HasHadPartialTickOnClient = true;
                    Assert.IsFalse(networkTime.IsFirstTimeFullyPredictingTick, "IsFirstTimeFullyPredictingTick when IsPartialTick");
                    Assert.NotZero(networkTime.ServerTickFraction, "ServerTickFraction when IsPartialTick");
                }
                else
                {
                    HasHadFullTickOnClient = true;
                    Assert.That(networkTime.ServerTickFraction, Is.EqualTo(1), "ServerTickFraction");

                    if (networkTime.IsFirstTimeFullyPredictingTick)
                    {
                        if (m_LatestFullServerTick.IsValid) Assert.That(networkTime.ServerTick.TicksSince(m_LatestFullServerTick), Is.GreaterThanOrEqualTo(0), "networkTime.ServerTick.TicksSince(m_LatestFullServerTick)");
                        m_LatestFullServerTick = networkTime.ServerTick;
                    }
                }

                if (networkTime.IsFinalPredictionTick) HasHadFinalPredictionTickOnClient = true;
                if (networkTime.IsFinalFullPredictionTick) Assert.IsFalse(networkTime.IsPartialTick, "IsPartialTick");

                Assert.NotZero(networkTime.ServerTickFraction, "ServerTickFraction");
            }

            private void AssertForServer(NetworkTime networkTime, ClientServerTickRate clientServerTickRate, TimeData timeData)
            {
                Assert.IsFalse(networkTime.IsPartialTick, "IsPartialTick");
                Assert.IsTrue(networkTime.IsFirstTimeFullyPredictingTick, "IsFirstTimeFullyPredictingTick");
                Assert.AreEqual(networkTime.IsFinalFullPredictionTick, networkTime.IsFinalPredictionTick, "IsFinalPredictionTick");
                Assert.That(networkTime.SimulationStepBatchSize, Is.AtLeast(1), "SimulationStepBatchSize");
                Assert.That(networkTime.ServerTickFraction, Is.EqualTo(1), "ServerTickFraction");
                Assert.That(networkTime.PredictedTickIndex, Is.EqualTo(1), "PredictedTickIndex");
                Assert.That(networkTime.NumPredictedTicksExpected, Is.EqualTo(1), "PredictedTickIndex");

                Assert.IsFalse(networkTime.IsCatchUpTick, "Server is not being death-spiral stressed in this test.");
                if (networkTime.IsFinalPredictionTick) HasHadFinalPredictionTickOnServer = true;

                if (m_LastServerTickOnServer.IsValid)
                    Assert.That(networkTime.ServerTick.TicksSince(m_LastServerTickOnServer), Is.EqualTo(networkTime.SimulationStepBatchSize), "ServerTick.TicksSince(m_LastServerTickOnServer)");
                m_LastServerTickOnServer = networkTime.ServerTick;

                if (m_LastElapsedNetworkTimeOnServer != default)
                {
                    var deltaTime = networkTime.ElapsedNetworkTime - m_LastElapsedNetworkTimeOnServer;
                    Assert.That(deltaTime, Is.EqualTo(clientServerTickRate.SimulationFixedTimeStep * networkTime.SimulationStepBatchSize), "dt == SimulationStepBatchSize");
                    Assert.That(deltaTime, Is.EqualTo(timeData.DeltaTime), "dt == timeData.DeltaTime");
                    Assert.That(networkTime.ElapsedNetworkTime, Is.EqualTo(timeData.ElapsedTime), "ElapsedNetworkTime == timeData.ElapsedTime");
                }

                m_LastElapsedNetworkTimeOnServer = networkTime.ElapsedNetworkTime;
            }

            private void AssertForBoth(NetworkTime networkTime)
            {
                Assert.IsTrue(networkTime.InputTargetTick.IsValid, "InputTargetTick.IsValid");
                Assert.IsTrue(networkTime.ServerTick.IsValid, "ServerTick.IsValid");

                // NetCodeTestWorld.TickIndex is a test-compatible proxy for the outer loop tick.
                // I.e. Time.frameCount.
                // So we can use this to validate that we're not ticking
                // the outer loop when networkTime.IsFinalPredictionTick is false.
                if (networkTime.IsFinalPredictionTick)
                {
                    m_LastFinalTickIndex = NetCodeTestWorld.TickIndex;
                }
                else if (m_LastFinalTickIndex != default)
                {
                    Assert.That(NetCodeTestWorld.TickIndex - m_LastFinalTickIndex, Is.EqualTo(1), "TickIndex - m_LastFinalTickIndex");
                }

                Assert.Zero(networkTime.EffectiveInputLatencyTicks, "EffectiveInputLatencyTicks");
            }
        }

        [DisableAutoCreation]
        [UpdateInGroup(typeof(SimulationSystemGroup))]
        [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
        internal partial struct AssertNetworkTimeSingletonValuesCorrectAfterPredictionLoopSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                Assert.IsFalse(state.WorldUnmanaged.IsThinClient());
                var networkTime = SystemAPI.GetSingleton<NetworkTime>();
                Assert.IsFalse(networkTime.IsInPredictionLoop, "A system created without an UpdateInGroup should NOT automatically be added to the prediction loop.");

                if (!networkTime.ServerTick.IsValid) return;

                Assert.IsTrue(networkTime.InputTargetTick.IsValid);
                Assert.IsTrue(networkTime.ServerTick.IsValid);

                SystemAPI.TryGetSingleton<ClientServerTickRate>(out var clientServerTickRate);
                clientServerTickRate.ResolveDefaults();

                Assert.NotZero(state.WorldUnmanaged.Time.DeltaTime);
                Assert.That(state.WorldUnmanaged.Time.ElapsedTime, Is.GreaterThanOrEqualTo(0));

                Assert.AreEqual(networkTime.Flags, default(NetworkTimeFlags));
                Assert.IsFalse(networkTime.IsCatchUpTick);
                Assert.IsFalse(networkTime.IsFinalPredictionTick);
                Assert.IsFalse(networkTime.IsFirstTimeFullyPredictingTick);
                Assert.IsFalse(networkTime.IsFirstPredictionTick);
                Assert.IsFalse(networkTime.IsFinalFullPredictionTick);
                Assert.That(networkTime.ElapsedNetworkTime, Is.GreaterThanOrEqualTo(0));
                Assert.That(networkTime.SimulationStepBatchSize, Is.GreaterThanOrEqualTo(1));
                Assert.That(networkTime.PredictedTickIndex, Is.EqualTo(networkTime.NumPredictedTicksExpected));
                Assert.That(networkTime.PredictedTickIndex, Is.InRange(0, 11));
                Assert.Zero(networkTime.EffectiveInputLatencyTicks);
            }
        }
    }
}
