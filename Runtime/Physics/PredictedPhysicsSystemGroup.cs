#if UNITY_EDITOR && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#endif
#if ENTITIES_1_5_OR_NEWER
//Uncomment this to test the version with Reflection. It is only for testing purpose.
#define  HAS_NEW_SYSTEMATTRIBUTE_API
#endif

using Unity.Entities;
using System;
using Unity.Core;
using Unity.Collections;
using Unity.Physics;
using Unity.Physics.Extensions;
using Unity.Physics.GraphicsIntegration;
using Unity.Physics.Systems;
using Unity.Transforms;
using System.Collections.Generic;
using System.Reflection;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.NetCode
{
    /// <summary>
    /// Rate manager that control when the physics simulation will run.
    /// The use cases we have:
    /// <para>
    /// On the server
    /// <list>
    /// <li>Does require physics objects exist? No, physics should run all the time to rebuild the world (empty) if all the physics stuff are gone.</li>
    /// <li>Static physics: yes, may need to raycast</li>
    /// <li>Dynamic physics: yes, even if not replicated.</li>
    /// <li>Triggers (static or dynamic): yes</li>
    /// <li>Kinematics, non ghost with physics: yes</li>
    /// <li>Predicted ghost with physics: yes</li>
    /// <li>Interpolated ghost with physics: yes (kinematics)</li>
    /// <li>Lag Compensation On: yes, we require the collision history to be rebuilt.</li>
    /// </list>
    /// </para>
    /// <para>
    /// On the client:
    /// <list type="">
    /// <li>Does require physics objects exist? Ideally yse, in practice no: physics should run all the time to rebuild the world (empty) if all the physics stuff are gone.</li>
    /// <li>Static physics: yes, may need to raycast. Ideally, this should use client-only physics if there are no ghost. It is up to the users</li>
    /// <li>Dynamic physics: yes, even if not replicated. Not ideal keep them in world 0 in that case, but necessary. It is up to the users</li>
    /// <li>Kinematics, non ghost with physics: yes. Not ideal keep them in world 0 in that case, but necessary. It is up to the users</li>
    /// <li>Predicted ghost with physics: yes</li>
    /// <li>Interpolated ghost with physics: yes (kinematics). In this case prediction should run only once. Should be up to the user though (not an hidden, opinionated default)</li>
    /// <li>Lag Compensation On: yes</li>
    ///</list>
    /// Overall, the group should always run all the time by default. However, because this would be a breaking change, we allow to opt-in for this behaviour via
    /// <see cref="PhysicGroupRunMode"/> enum.
    /// </summary>
    class NetcodePhysicsRateManager : IRateManager
    {
        private bool m_DidUpdate;
        private EntityQuery m_LagCompensationQuery;
        private EntityQuery m_predictedPhysicsQuery;
        private EntityQuery m_relaxedPhysicsQuery;
        private EntityQuery m_PhysicsGroupConfigQuery;
        private EntityQuery m_NetworkTimeQuery;
        public NetcodePhysicsRateManager(ComponentSystemGroup group)
        {
            var queryBuilder = new EntityQueryBuilder(Allocator.Temp);
            //The default current behaviour: allow physics to run as long as entities with physics velocity exists, either kinematic or dynamic.
            //This is by far a very restrictive scenario, on client especially. For the server, this can be also
            //be not what you want. You may need to raycast against some geometry for example.
            queryBuilder.WithAll<PredictedGhost>().WithAny<PhysicsVelocity>();
            m_predictedPhysicsQuery = queryBuilder.Build(group.EntityManager);
            //this is a more relaxed condition, that allow physics to run as long there are some ghost physics entities. This is more
            //correct in my opinion, but break some "assumptions" and behavior in respect to the original default, so I left that
            //only as an options.
            //It is again not working correctly in case all physics entities get destroyed. The physics collision world is stale in that case.
            //However, if lag compensation is turned on, everything work fine.
            queryBuilder.Reset();
            queryBuilder.WithAny<PhysicsVelocity, PhysicsCollider>();
            m_relaxedPhysicsQuery = queryBuilder.Build(group.EntityManager);
            m_LagCompensationQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<LagCompensationConfig>());
            m_PhysicsGroupConfigQuery = group.World.EntityManager.CreateEntityQuery(typeof(PhysicsGroupConfig));
            m_NetworkTimeQuery = group.World.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
        }
        public bool ShouldGroupUpdate(ComponentSystemGroup group)
        {
            if (m_DidUpdate)
            {
                m_DidUpdate = false;
                return false;
            }
            m_PhysicsGroupConfigQuery.TryGetSingleton(out PhysicsGroupConfig groupConfig);
            if (groupConfig.PhysicsRunMode != PhysicGroupRunMode.AlwaysRun)
            {
                bool noEntitiesMatchingQuery;
                if (groupConfig.PhysicsRunMode == PhysicGroupRunMode.LagCompensationEnabledOrKinematicGhosts)
                    noEntitiesMatchingQuery = m_predictedPhysicsQuery.IsEmptyIgnoreFilter;
                else
                    noEntitiesMatchingQuery = m_relaxedPhysicsQuery.IsEmptyIgnoreFilter;

                //if query is emtpy and no lag compesation, there is nothing to run
                if (noEntitiesMatchingQuery)
                {
                    //On the client, if users set this to 0 is the same as disabling the hystory backup.
                    if (m_LagCompensationQuery.IsEmptyIgnoreFilter ||
                        (group.World.IsClient() &&
                         m_LagCompensationQuery.GetSingleton<LagCompensationConfig>().ClientHistorySize == 0))
                    {
                        return false;
                    }
                    //if lag compensation is enabled, run only for new full ticks,
                    var netTime = m_NetworkTimeQuery.GetSingleton<NetworkTime>();
                    if (!netTime.IsFirstTimeFullyPredictingTick)
                        return false;
                }
            }
            m_DidUpdate = true;
            return true;
        }
        public float Timestep
        {
            get
            {
                throw new System.NotImplementedException();
            }
            set
            {
                throw new System.NotImplementedException();
            }
        }
    }

    static class MovePhysicsSystemUtilities
    {
#if !HAS_NEW_SYSTEMATTRIBUTE_API
        //TODO: remove this hack when the new Entities package is public.
        private static MethodInfo s_HackGetSystemTypeMethod = null;
        private static int s_FieldOffset = -1;
        public static Type GetSystemType(World world, SystemHandle systemHandle)
        {
            if (s_HackGetSystemTypeMethod == null || s_FieldOffset == -1)
            {
                s_HackGetSystemTypeMethod = typeof(TypeManager).GetMethod("GetSystemType",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                    null, new Type[] { typeof(SystemTypeIndex) }, null);
                Assertions.Assert.IsNotNull(s_HackGetSystemTypeMethod);
                var fieldInfo = typeof(SystemState).GetField("m_SystemTypeIndex",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assertions.Assert.IsNotNull(fieldInfo);
                s_FieldOffset = UnsafeUtility.GetFieldOffset(fieldInfo);
            }

            ref var systemState = ref world.Unmanaged.ResolveSystemStateRef(systemHandle);
            SystemTypeIndex systemTypeIndex;
            unsafe { fixed (void* data = &systemState) {
                systemTypeIndex = *(SystemTypeIndex*)((byte*)data + s_FieldOffset);
            }}
            return (Type)s_HackGetSystemTypeMethod.Invoke(null, new object[] { systemTypeIndex });
        }

        public static bool MovePhysicsSystem(Type systemType, SystemHandle handle,
            ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            SystemTypeIndex systemTypeIndex = TypeManager.GetSystemTypeIndex(systemType);
            if (physicsSystemTypes.ContainsKey(systemTypeIndex))
                return false;
            var attribs = TypeManager.GetSystemAttributes(systemType, typeof(UpdateBeforeAttribute));
            foreach (var attr in attribs)
            {
                var dependencyTypeIndex = TypeManager.GetSystemTypeIndex(((UpdateBeforeAttribute)attr).SystemType);
                if (physicsSystemTypes.ContainsKey(dependencyTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            attribs = TypeManager.GetSystemAttributes(systemType, typeof(UpdateAfterAttribute));
            foreach (var attr in attribs)
            {
                var dependencyTypeIndex = TypeManager.GetSystemTypeIndex(((UpdateAfterAttribute)attr).SystemType);
                if (physicsSystemTypes.ContainsKey(dependencyTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            return false;
        }

        public static void MovePhysicsSystems(FixedStepSimulationSystemGroup srcGrp,
            PredictedFixedStepSimulationSystemGroup dstGrp, ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            bool didMove = true;
            var managedSystems = srcGrp.ManagedSystems;
            var unmanagedSystems = srcGrp.GetUnmanagedSystems();
            while (didMove)
            {
                didMove = false;
                foreach (var system in managedSystems)
                {
                    didMove |= MovePhysicsSystemUtilities.MovePhysicsSystem(system.GetType(), system.SystemHandle, ref physicsSystemTypes);
                }
                foreach (var system in unmanagedSystems)
                {
                    var systemType = MovePhysicsSystemUtilities.GetSystemType(srcGrp.World, system);
                    didMove |= MovePhysicsSystemUtilities.MovePhysicsSystem(systemType, system, ref physicsSystemTypes);
                }
            }
        }
#else
        public static void MovePhysicsSystems(FixedStepSimulationSystemGroup srcGrp,
            PredictedFixedStepSimulationSystemGroup dstGrp,
            ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            bool didMove = true;
            var systems = srcGrp.GetAllSystems();
            while (didMove)
            {
                didMove = false;
                foreach (var system in systems)
                {
                    var systemTypeIndex = srcGrp.World.Unmanaged.GetSystemTypeIndex(system);
                    didMove |= MovePhysicsSystemUtilities.MovePhysicsSystem(system, systemTypeIndex, ref physicsSystemTypes);
                }
            }
        }

        public static bool MovePhysicsSystem(SystemHandle handle, SystemTypeIndex systemTypeIndex,
            ref NativeHashMap<SystemTypeIndex, SystemHandle> physicsSystemTypes)
        {
            if (physicsSystemTypes.ContainsKey(systemTypeIndex))
                return false;
            var attribs = TypeManager.GetSystemAttributes(systemTypeIndex, TypeManager.SystemAttributeKind.UpdateBefore);
            foreach (var attr in attribs)
            {
                if (physicsSystemTypes.ContainsKey(attr.TargetSystemTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            attribs = TypeManager.GetSystemAttributes(systemTypeIndex, TypeManager.SystemAttributeKind.UpdateAfter);
            foreach (var attr in attribs)
            {
                if (physicsSystemTypes.ContainsKey(attr.TargetSystemTypeIndex))
                {
                    physicsSystemTypes[systemTypeIndex] = handle;
                    return true;
                }
            }
            return false;
        }
#endif
    }

    /// <summary>
    /// A system which setup physics for prediction. It will move the PhysicsSystemGroup
    /// to the PredictedFixedStepSimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class PredictedPhysicsConfigSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            MovePhysicsSystems();
            var physGrp = World.GetExistingSystemManaged<PhysicsSystemGroup>();
            physGrp.RateManager = new NetcodePhysicsRateManager(physGrp);
            World.GetExistingSystemManaged<InitializationSystemGroup>().RemoveSystemFromUpdateList(this);
        }

        void MovePhysicsSystems()
        {
            var srcGrp = World.GetExistingSystemManaged<FixedStepSimulationSystemGroup>();
            var dstGrp = World.GetExistingSystemManaged<PredictedFixedStepSimulationSystemGroup>();

            var physicsSystemTypes = new NativeHashMap<SystemTypeIndex, SystemHandle>(100, Allocator.Temp);
            var physicsGroupTypeIndex = TypeManager.GetSystemTypeIndex<PhysicsSystemGroup>();
            //TODO: there are a couple of problem with Incremental Physics world build and multiple worlds in general:
            //- the InjectTemporalInfo bla bla system add temporal coherence to all physics entities, regardless of the PhysicsWorld index.
            //- the InjectTemporalInfo is not moved. There is a "last resort system that does that" but seems to me just incorrect.
            //- there is not possibilty (yet) to get a PhysicsStep per "PhysicsWorld". Instead, it should be possible to specify different settings
            //  and simulation rate for the different physics world in my opinion.
            //TODO: let's resolve the problem at the source: we don't need to move physics in first place, nor the fixed step group. that means changing the
            //      whole system group and order. We can reason about this for 2.0
            physicsSystemTypes.Add(physicsGroupTypeIndex, srcGrp.World.GetExistingSystem(physicsGroupTypeIndex));
            MovePhysicsSystemUtilities.MovePhysicsSystems(srcGrp, dstGrp, ref physicsSystemTypes);
            foreach (var kv in physicsSystemTypes)
            {
                //TODO: this is silly. I think the group API should be litte more consistent
                if (!kv.Key.IsManaged)
                    srcGrp.RemoveSystemFromUpdateList(kv.Value);
                else
                    srcGrp.RemoveSystemFromUpdateList(World.GetExistingSystemManaged(kv.Key));
                dstGrp.AddSystemToUpdateList(kv.Value);
            }
        }
    }

    /// <summary>
    /// If a singleton of this type exists in the world any non-ghost with dynamic physics
    /// in the default physics world on the client will be moved to the indicated physics
    /// world index.
    /// This is required because the predicted physics loop cannot process objects which
    /// are not rolled back.
    /// </summary>
    public struct PredictedPhysicsNonGhostWorld : IComponentData
    {
        /// <summary>
        /// The physics world index to move entities to.
        /// </summary>
        public uint Value;
    }

    /// <summary>
    /// A system used to detect invalid dynamic physics objects in the predicted
    /// physics world on clients. This system also moves entities to the correct
    /// world if PredictedPhysicsNonGhostWorld exists and is not 0.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [BurstCompile]
    public partial struct PredictedPhysicsValidationSystem : ISystem
    {
        #if NETCODE_DEBUG
        private bool m_DidPrintError;
        #endif
        private EntityQuery m_Query;

        /// <inheritdoc/>
        public void OnCreate(ref SystemState state)
        {
            // This would still be needed on host worlds, to allow having physics scenes that don't interact with the authoritative world. Host worlds still need to have ragdolls for example.
            // If not debug, require the singleton for update
            #if !NETCODE_DEBUG
            state.RequireForUpdate<PredictedPhysicsNonGhostWorld>();
            #endif
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PhysicsVelocity, PhysicsWorldIndex>()
                .WithNone<GhostInstance>();
            m_Query = state.GetEntityQuery(builder);
            m_Query.SetSharedComponentFilter(new PhysicsWorldIndex(0));
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!m_Query.IsEmpty)
            {
                if (SystemAPI.TryGetSingleton<PredictedPhysicsNonGhostWorld>(out var targetWorld))
                {
                    // Go through all things and set the new target world. This is a structural change so need to be careful
                    state.EntityManager.SetSharedComponent(m_Query, new PhysicsWorldIndex(targetWorld.Value));
                }
                #if NETCODE_DEBUG
                else if (!m_DidPrintError)
                {
                    // If debug, print a warning once telling users what to do,
                    // and show them the first problem entity (for easy debugging).
                    var erredEntities = m_Query.ToEntityArray(Allocator.Temp);
                    FixedString512Bytes error = $"[{state.WorldUnmanaged.Name}] The default physics world contains {erredEntities.Length} dynamic physics objects which are not ghosts. This is not supported! In order to have client-only physics, you must setup a custom physics world:";
                    foreach (var erredEntity in erredEntities)
                    {
                        FixedString512Bytes tempFs = "\n- ";
                        tempFs.Append(erredEntity.ToFixedString());
                        tempFs.Append(' ');
                        state.EntityManager.GetName(erredEntity, out var entityName);
                        tempFs.Append(entityName);

                        var formatError = error.Append(tempFs);
                        if (formatError == FormatError.Overflow)
                            break;
                    }
                    SystemAPI.GetSingleton<NetDebug>().LogError(error);
                    m_DidPrintError = true;
                    state.RequireForUpdate<PredictedPhysicsNonGhostWorld>();
                }
                #endif
            }
        }
    }

    /// <summary>
    /// System to make sure prediction switching smoothing happens after physics motion smoothing and overwrites the results
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(SwitchPredictionSmoothingSystem))]
    [UpdateAfter(typeof(SmoothRigidBodiesGraphicalMotion))]
    public partial class SwitchPredictionSmoothingPhysicsOrderingSystem : SystemBase
    {
        internal struct Disabled : IComponentData
        {}
        protected override void OnCreate()
        {
            RequireForUpdate<Disabled>();
        }

        protected override void OnUpdate()
        {
        }
    }
}
