using Unity.Entities;
using System.Collections.Generic;
using System;
using Unity.Physics.Systems;
using Unity.Mathematics;
using Unity.Core;
using Unity.Collections;
using Unity.Jobs;
using Unity.Physics;
using Unity.Physics.Extensions;
using Unity.Physics.GraphicsIntegration;
using Unity.Transforms;

namespace Unity.NetCode
{
    /// <summary>
    /// A component used as a singleton to control the use of physics in ghost prediction.
    /// When the singleton exists all physics systems, and systems with ordering contraints
    /// against physics systems ([UpdateBefore], [UpdateAfter]) are moved into the ghost
    /// prediction system group and run as part of prediction.
    /// The singleton must be added on both the client and server with compatible values.
    /// When using physics simulation in prediction all dynamic physics objects must be ghosts.
    /// When netcode is present it will add a PhysicsMassOverride to all ghosts with dynamic physics
    /// on the client, this means it is not possible to use PhysicsMassOverride for game specific
    /// purposes on ghosts.
    /// </summary>
    public struct PredictedPhysicsConfig : IComponentData
    {
        /// <summary>
        /// The number of physics step to perform for each prediction simulation step.
        /// This for example allows you to run 60Hz simulation with 120Hz physics if set to 2.
        /// </summary>
        public int PhysicsTicksPerSimTick;
        /// <summary>
        /// If set to true the predicted physics will not update when there are no connections.
        /// This can be used to reduce the simulation cost for idle servers.
        /// </summary>
        public bool DisableWhenNoConnections;
    }

    [UpdateInGroup(typeof(GhostPredictionSystemGroup))]
    public class PredictedPhysicsSystemGroup : ComponentSystemGroup
    {
        BeginFixedStepSimulationEntityCommandBufferSystem m_BeginFixedStepSimulationEntityCommandBufferSystem;
        EndFixedStepSimulationEntityCommandBufferSystem m_EndFixedStepSimulationEntityCommandBufferSystem;

        bool m_SystemsMoved;

        ClientSimulationSystemGroup m_ClientSimulationSystemGroup;
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        protected override void OnCreate()
        {
            base.OnCreate();
            m_BeginFixedStepSimulationEntityCommandBufferSystem = World.GetExistingSystem<BeginFixedStepSimulationEntityCommandBufferSystem>();
            m_EndFixedStepSimulationEntityCommandBufferSystem = World.GetExistingSystem<EndFixedStepSimulationEntityCommandBufferSystem>();

            RequireSingletonForUpdate<PredictedPhysicsConfig>();

            m_ClientSimulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
        }
        void MovePhysicsSystems()
        {
            var grp = World.GetExistingSystem<FixedStepSimulationSystemGroup>();

            var physicsSystemTypes = new Dictionary<Type, bool>();
            physicsSystemTypes.Add(typeof(BuildPhysicsWorld), true);
            physicsSystemTypes.Add(typeof(StepPhysicsWorld), true);
            physicsSystemTypes.Add(typeof(ExportPhysicsWorld), true);
            physicsSystemTypes.Add(typeof(EndFramePhysicsSystem), true);

            bool didMove = true;
            var fixedUpdateSystems = grp.Systems;
            while (didMove)
            {
                didMove = false;
                foreach (var system in fixedUpdateSystems)
                {
                    var systemType = system.GetType();
                    if (physicsSystemTypes.ContainsKey(systemType))
                        continue;
                    var attribs = TypeManager.GetSystemAttributes(systemType, typeof(UpdateBeforeAttribute));
                    foreach (var attr in attribs)
                    {
                        var dep = attr as UpdateBeforeAttribute;
                        if (physicsSystemTypes.ContainsKey(dep.SystemType))
                        {
                            didMove = true;
                            physicsSystemTypes[systemType] = true;
                        }
                    }
                    attribs = TypeManager.GetSystemAttributes(systemType, typeof(UpdateAfterAttribute));
                    foreach (var attr in attribs)
                    {
                        var dep = attr as UpdateAfterAttribute;
                        if (physicsSystemTypes.ContainsKey(dep.SystemType))
                        {
                            didMove = true;
                            physicsSystemTypes[systemType] = true;
                        }
                    }
                }
            }
            foreach (var system in fixedUpdateSystems)
            {
                if (physicsSystemTypes.ContainsKey(system.GetType()))
                {
                    grp.RemoveSystemFromUpdateList(system);
                    AddSystemToUpdateList(system);
                }
            }
        }
        protected override void OnUpdate()
        {
            var physicsConfig = GetSingleton<PredictedPhysicsConfig>();
            if (physicsConfig.PhysicsTicksPerSimTick == 0)
                physicsConfig.PhysicsTicksPerSimTick = 1;

            if (!m_SystemsMoved)
            {
                MovePhysicsSystems();

                m_SystemsMoved = true;

                if (physicsConfig.DisableWhenNoConnections)
                {
                    var query = GetEntityQuery(typeof(NetworkStreamConnection));
                    RequireForUpdate(query);
                }
            }

            var physTicks = physicsConfig.PhysicsTicksPerSimTick;
            var elapsedTime = Time.ElapsedTime;
            var fixedDeltaTime = Time.DeltaTime;
            if (m_ClientSimulationSystemGroup != null)
                fixedDeltaTime = m_ClientSimulationSystemGroup.ServerTickDeltaTime;
            fixedDeltaTime /= (float)physicsConfig.PhysicsTicksPerSimTick;

            // Do not handle partial ticks, but if there should be multiple physics ticks per sim tick we ran run the physics ticks which are complete
            if (m_ClientSimulationSystemGroup != null && m_ClientSimulationSystemGroup.ServerTickFraction < 1 && m_GhostPredictionSystemGroup.IsFinalPredictionTick)
            {
                var physTicksFlt = m_ClientSimulationSystemGroup.ServerTickFraction * physTicks;
                physTicks = (int)physTicksFlt;
                elapsedTime -= (physTicksFlt - physTicks) * fixedDeltaTime;
                if (physTicks <= 0)
                    return;
            }

            for (int i = 0; i < physTicks; ++i)
            {
                World.PushTime(new TimeData(
                    elapsedTime: elapsedTime - (physTicks - 1 - i)*fixedDeltaTime,
                    deltaTime: fixedDeltaTime));
                m_BeginFixedStepSimulationEntityCommandBufferSystem.Update();
                base.OnUpdate();
                m_EndFixedStepSimulationEntityCommandBufferSystem.Update();
                World.PopTime();
            }
        }
    }

    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(PredictedPhysicsSystemGroup), OrderFirst = true)]
    /// <summary>
    /// A system which marks predicted ghosts as kinematic or dynamic based on if they
    /// should be predicted for the current tick.
    /// </summary>
    public partial class PrepareGhostPhysicsPrediction : SystemBase
    {
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        protected override void OnCreate()
        {
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
        }
        protected override void OnUpdate()
        {
            var tick = m_GhostPredictionSystemGroup.PredictingTick;
            Entities.ForEach((ref Unity.Physics.PhysicsMassOverride massOverride, in PredictedGhostComponent prediction) => {
                massOverride.IsKinematic = (byte)(GhostPredictionSystemGroup.ShouldPredict(tick, prediction) ? 0 : 1);
            }).ScheduleParallel();
        }
    }

    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostPredictionSystemGroup))]
    /// <summary>
    /// A system which restores the state of IsKinematic for predicted ghosts after the prediction loop has run.
    /// </summary>
    public partial class FinishGhostPhysicsPrediction : SystemBase
    {
        protected override void OnUpdate()
        {
            Entities.ForEach((ref Unity.Physics.PhysicsMassOverride massOverride, in PredictedGhostComponent prediction) => {
                massOverride.IsKinematic = 1;
            }).ScheduleParallel();
        }
    }

    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(BuildPhysicsWorld))]
    [UpdateBefore(typeof(StepPhysicsWorld))]
    ///<summary>
    /// A system which reset the physics MotionVelocity before the physics step for all the kinematic predicted ghosts
    ///</summary>
    public partial class ResetPhysicsMotionVelocity : SystemBase
    {
        private BuildPhysicsWorld buildPhysicsWorld;

        protected override void OnCreate()
        {
            buildPhysicsWorld = World.GetExistingSystem<BuildPhysicsWorld>();
        }

        protected override void OnStartRunning()
        {
            this.RegisterPhysicsRuntimeSystemReadWrite();
        }
        protected override void OnUpdate()
        {
            var physicsWorld = buildPhysicsWorld.PhysicsWorld;
            Entities
                .WithAll<GhostComponent>()
                .ForEach((Entity entity, in Unity.Physics.PhysicsMassOverride massOverride) => {
                if (massOverride.IsKinematic == 1)
                {
                    var index = physicsWorld.GetRigidBodyIndex(entity);
                    physicsWorld.SetLinearVelocity(index, float3.zero);
                    physicsWorld.SetAngularVelocity(index, float3.zero);
                }
            }).Schedule();
        }
    }
    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(StepPhysicsWorld))]
    [UpdateBefore(typeof(ExportPhysicsWorld))]
    [UpdateBefore(typeof(Unity.Physics.GraphicsIntegration.BufferInterpolatedRigidBodiesMotion))]
    /// <summary>
    /// A system which restore the physics MotionVelocity before the physics export world
    /// </summary>
    public partial class RestorePhysicsVelocity : SystemBase
    {
        private BuildPhysicsWorld buildPhysicsWorld;
        private StepPhysicsWorld stepPhysicsWorld;

        protected override void OnCreate()
        {
            buildPhysicsWorld = World.GetExistingSystem<BuildPhysicsWorld>();
            stepPhysicsWorld = World.GetExistingSystem<StepPhysicsWorld>();
        }

        protected override void OnStartRunning()
        {
            this.RegisterPhysicsRuntimeSystemReadWrite();
        }

        protected override void OnUpdate()
        {
            var physicsWorld = buildPhysicsWorld.PhysicsWorld;
            Dependency = Entities
                .WithAll<GhostComponent>()
                .ForEach((Entity entity, in Unity.Physics.PhysicsMassOverride massOverride, in PhysicsVelocity physicsVelocity) => {
                if (massOverride.IsKinematic == 1)
                {
                    var index = physicsWorld.GetRigidBodyIndex(entity);
                    physicsWorld.SetLinearVelocity(index, physicsVelocity.Linear);
                    physicsWorld.SetAngularVelocity(index, physicsVelocity.Angular);
                }
            }).Schedule(JobHandle.CombineDependencies(Dependency, stepPhysicsWorld.FinalSimulationJobHandle));
        }
    }

    [UpdateInGroup(typeof(GhostSpawnSystemGroup), OrderLast=true)]
    /// <summary>
    /// A system which adds the required PhysicsMassOverride component to all ghosts which
    /// have dynamic phyics on the client. If the ghost is predicted it will switch between
    /// dynamic and kinematic based on if it should predict. If the ghost is interpolated
    /// it will always be treated as kinematic.
    /// </summary>
    public partial class GhostPhysicsPreProcess : SystemBase
    {
        private EntityQuery m_PhysicsGhosts;
        protected override void OnCreate()
        {
            m_PhysicsGhosts = GetEntityQuery(ComponentType.ReadOnly<GhostComponent>(),
                ComponentType.ReadOnly<Unity.Physics.PhysicsMass>(),
                ComponentType.ReadOnly<Unity.Physics.PhysicsVelocity>(),
                ComponentType.Exclude<Unity.Physics.PhysicsMassOverride>());
            RequireForUpdate(m_PhysicsGhosts);
        }
        protected override void OnUpdate()
        {
            var ents = m_PhysicsGhosts.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ents.Length; ++i)
                EntityManager.AddComponentData(ents[i], new Unity.Physics.PhysicsMassOverride{IsKinematic = 1});
        }
    }

    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(SwitchPredictionSmoothingSystem))]
    [UpdateAfter(typeof(SmoothRigidBodiesGraphicalMotion))]
    /// <summary>
    /// System to make sure prediction switching smoothing happens after physics motion smoothing and overwrites the results
    /// </summary>
    public partial class SwitchPredictionSmoothingPhysicsOrderingSystem : SystemBase
    {
        public struct Disabled : IComponentData
        {}
        protected override void OnCreate()
        {
            RequireSingletonForUpdate<Disabled>();
        }

        protected override void OnUpdate()
        {
        }
    }
}
