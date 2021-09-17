using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Jobs;
using System.Runtime.InteropServices;

namespace Unity.NetCode
{
    [UpdateInWorld(TargetWorld.Client)]
    [UpdateInGroup(typeof(GhostPredictionSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(GhostPredictionHistorySystem))]
    public unsafe partial class GhostPredictionSmoothingSystem : SystemBase
    {
        GhostPredictionSystemGroup m_GhostPredictionSystemGroup;
        GhostPredictionHistorySystem m_GhostPredictionHistorySystem;

        EntityQuery m_PredictionQuery;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SmoothingActionDelegate(void* currentData, void* previousData, void* userData);

        struct SmoothingActionState
        {
            public int compIndex;
            public int compSize;
            public int serializerIndex;
            public int entityIndex;
            public int compBackupOffset;
            public int userTypeId;
            public int userTypeSize;
            public PortableFunctionPointer<SmoothingActionDelegate> action;
        }

        NativeList<ComponentType> m_UserSpecifiedComponentData;
        NativeHashMap<ComponentType, SmoothingActionState> m_SmoothingActions;

        struct SmoothingAction : IComponentData {}
        Entity m_HasSmoothingAction;

        public bool RegisterSmoothingAction<T>(PortableFunctionPointer<SmoothingActionDelegate> action) where T : struct, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            if (type.IsBuffer)
            {
                UnityEngine.Debug.LogError("Smoothing actions are not supported for buffers");
                return false;
            }
            if (m_SmoothingActions.ContainsKey(type))
            {
                UnityEngine.Debug.LogError($"There is already a action registered for the type {type.ToString()}");
                return false;
            }

            var actionData = new SmoothingActionState
            {
                action = action,
                compIndex = -1,
                compSize = -1,
                serializerIndex = -1,
                entityIndex = -1,
                compBackupOffset = -1,
                userTypeId = -1,
                userTypeSize = -1
            };

            m_SmoothingActions.Add(type, actionData);
            if (m_HasSmoothingAction == Entity.Null)
            {
                m_HasSmoothingAction = EntityManager.CreateEntity(ComponentType.ReadOnly<SmoothingAction>());
            }
            return true;
        }

        public bool RegisterSmoothingAction<T, U>(PortableFunctionPointer<SmoothingActionDelegate> action)
            where T : struct, IComponentData
            where U : struct, IComponentData
        {
            if (!RegisterSmoothingAction<T>(action))
                return false;

            var type = ComponentType.ReadWrite<T>();
            var userType = ComponentType.ReadWrite<U>();
            var userTypeId = -1;
            for (int i = 0; i < m_UserSpecifiedComponentData.Length; ++i)
            {
                if (userType == m_UserSpecifiedComponentData[i])
                {
                    userTypeId = i;
                    break;
                }
            }
            if (userTypeId == -1)
            {
                if (m_UserSpecifiedComponentData.Length == 8)
                {
                    UnityEngine.Debug.LogError("There can only be 8 components registered as user data.");

                    m_SmoothingActions.Remove(type);
                    if (m_SmoothingActions.IsEmpty)
                        EntityManager.DestroyEntity(m_HasSmoothingAction);

                    return false;
                }
                m_UserSpecifiedComponentData.Add(userType);
                userTypeId = m_UserSpecifiedComponentData.Length - 1;
            }
            var actionState = m_SmoothingActions[type];
            actionState.userTypeId = userTypeId;
            actionState.userTypeSize = UnsafeUtility.SizeOf<U>();

            m_SmoothingActions[type] = actionState;
            return true;
        }

        protected override void OnCreate()
        {
            m_GhostPredictionSystemGroup = World.GetExistingSystem<GhostPredictionSystemGroup>();
            m_GhostPredictionHistorySystem = World.GetExistingSystem<GhostPredictionHistorySystem>();

            m_PredictionQuery = GetEntityQuery(ComponentType.ReadOnly<PredictedGhostComponent>(), ComponentType.ReadOnly<GhostComponent>());

            m_UserSpecifiedComponentData = new NativeList<ComponentType>(8, Allocator.Persistent);
            m_SmoothingActions = new NativeHashMap<ComponentType, SmoothingActionState>(32, Allocator.Persistent);

            RequireSingletonForUpdate<GhostCollection>();
            RequireSingletonForUpdate<SmoothingAction>();
        }
        protected override void OnDestroy()
        {
            m_UserSpecifiedComponentData.Dispose();
            m_SmoothingActions.Dispose();

            if (m_HasSmoothingAction != Entity.Null)
                EntityManager.DestroyEntity(m_HasSmoothingAction);
        }
        protected override void OnUpdate()
        {
            if (m_GhostPredictionSystemGroup.PredictingTick != m_GhostPredictionHistorySystem.LastBackupTick)
                return;

            var smoothingJob = new PredictionSmoothingJob
            {
                predictionState = m_GhostPredictionHistorySystem.PredictionState,
                ghostType = GetComponentTypeHandle<GhostComponent>(true),
                predictedGhostType = GetComponentTypeHandle<PredictedGhostComponent>(true),
                entityType = GetEntityTypeHandle(),

                GhostCollectionSingleton = GetSingletonEntity<GhostCollection>(),
                GhostComponentCollectionFromEntity = GetBufferFromEntity<GhostComponentSerializer.State>(true),
                GhostTypeCollectionFromEntity = GetBufferFromEntity<GhostCollectionPrefabSerializer>(true),
                GhostComponentIndexFromEntity = GetBufferFromEntity<GhostCollectionComponentIndex>(true),

                childEntityLookup = GetStorageInfoFromEntity(),
                linkedEntityGroupType = GetBufferTypeHandle<LinkedEntityGroup>(),
                tick = m_GhostPredictionSystemGroup.PredictingTick,

                smoothingActions = m_SmoothingActions
            };

            Dependency = JobHandle.CombineDependencies(Dependency,
                m_GhostPredictionHistorySystem.PredictionStateWriteJobHandle);

            var ghostComponentCollection = EntityManager.GetBuffer<GhostCollectionComponentType>(smoothingJob.GhostCollectionSingleton);
            DynamicTypeList.PopulateList(this, ghostComponentCollection, false, ref smoothingJob.DynamicTypeList);
            DynamicTypeList.PopulateListFromArray(this, m_UserSpecifiedComponentData, true, ref smoothingJob.UserList);

            Dependency = smoothingJob.ScheduleParallelByRef(m_PredictionQuery, Dependency);

            m_GhostPredictionHistorySystem.AddPredictionStateReader(Dependency);
        }

        [BurstCompile]
        struct PredictionSmoothingJob : IJobChunk
        {
            public DynamicTypeList DynamicTypeList;
            public DynamicTypeList UserList;
            [ReadOnly] public NativeHashMap<ArchetypeChunk, System.IntPtr> predictionState;

            [ReadOnly] public ComponentTypeHandle<GhostComponent> ghostType;
            [ReadOnly] public ComponentTypeHandle<PredictedGhostComponent> predictedGhostType;
            [ReadOnly] public EntityTypeHandle entityType;

            public Entity GhostCollectionSingleton;
            [ReadOnly] public BufferFromEntity<GhostComponentSerializer.State> GhostComponentCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionPrefabSerializer> GhostTypeCollectionFromEntity;
            [ReadOnly] public BufferFromEntity<GhostCollectionComponentIndex> GhostComponentIndexFromEntity;

            [ReadOnly] public StorageInfoFromEntity childEntityLookup;
            [ReadOnly] public BufferTypeHandle<LinkedEntityGroup> linkedEntityGroupType;

            [ReadOnly] public NativeHashMap<ComponentType, SmoothingActionState> smoothingActions;
            public uint tick;

            const GhostComponentSerializer.SendMask requiredSendMask = GhostComponentSerializer.SendMask.Predicted;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                if (!predictionState.TryGetValue(chunk, out var state) ||
                    (*(PredictionBackupState*)state).entityCapacity != chunk.Capacity)
                    return;

                DynamicComponentTypeHandle* ghostChunkComponentTypesPtr = DynamicTypeList.GetData();
                int ghostChunkComponentTypesLength = DynamicTypeList.Length;
                DynamicComponentTypeHandle* userTypes = UserList.GetData();
                int userTypesLength = UserList.Length;

                var GhostTypeCollection = GhostTypeCollectionFromEntity[GhostCollectionSingleton];
                var GhostComponentIndex = GhostComponentIndexFromEntity[GhostCollectionSingleton];
                var GhostComponentCollection = GhostComponentCollectionFromEntity[GhostCollectionSingleton];

                var ghostComponents = chunk.GetNativeArray(ghostType);

                int ghostTypeId = ghostComponents.GetFirstGhostTypeId();
                if (ghostTypeId < 0)
                    return;
                if (ghostTypeId >= GhostTypeCollection.Length)
                    return; // serialization data has not been loaded yet. This can only happen for prespawn objects

                var typeData = GhostTypeCollection[ghostTypeId];

                var headerSize = PredictionBackupState.GetHeaderSize();
                var entitySize = PredictionBackupState.GetEntitiesSize(chunk.Capacity, out var singleEntitySize);

                Entity* backupEntities = PredictionBackupState.GetEntities(state);
                var entities = chunk.GetNativeArray(entityType);

                var predictedGhostComponents = chunk.GetNativeArray(predictedGhostType);

                int numBaseComponents = typeData.NumComponents - typeData.NumChildComponents;
                int baseOffset = typeData.FirstComponent;

                var actions = new NativeList<SmoothingActionState>(Allocator.Temp);
                var childActions = new NativeList<SmoothingActionState>(Allocator.Temp);

                int backupOffset = headerSize + entitySize;

                // todo: this loop could be cached on chunk.capacity, because now we are re-calculating it everytime.
                for (int comp = 0; comp < typeData.NumComponents; ++comp)
                {
                    int index = baseOffset + comp;
                    int compIdx = GhostComponentIndex[index].ComponentIndex;
                    int serializerIdx = GhostComponentIndex[index].SerializerIndex;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    if (compIdx >= ghostChunkComponentTypesLength)
                        throw new System.InvalidOperationException("Component index out of range");
#endif
                    if ((GhostComponentIndex[index].SendMask&requiredSendMask) == 0)
                        continue;

                    if (GhostComponentCollection[serializerIdx].ComponentType.IsBuffer)
                    {
                        backupOffset += PredictionBackupState.GetDataSize(GhostSystemConstants.DynamicBufferComponentSnapshotSize, chunk.Capacity);
                        continue;
                    }
                    var compSize = GhostComponentCollection[serializerIdx].ComponentSize;

                    if (smoothingActions.TryGetValue(GhostComponentCollection[serializerIdx].ComponentType,
                        out var action))
                    {
                        action.compIndex = compIdx;
                        action.compSize = compSize;
                        action.serializerIndex = serializerIdx;
                        action.entityIndex = GhostComponentIndex[index].EntityIndex;
                        action.compBackupOffset = backupOffset;

                        if (comp < numBaseComponents)
                            actions.Add(action);
                        else
                            childActions.Add(action);
                    }
                    backupOffset += PredictionBackupState.GetDataSize(compSize, chunk.Capacity);
                }

                foreach (var action in actions)
                {
                    if (chunk.Has(ghostChunkComponentTypesPtr[action.compIndex]))
                    {
                        for (int ent = 0; ent < entities.Length; ++ent)
                        {
                            // If this entity did not predict anything there was no rollback and no need to debug it
                            if (!GhostPredictionSystemGroup.ShouldPredict(tick, predictedGhostComponents[ent]))
                                continue;

                            if (entities[ent] != backupEntities[ent])
                                continue;

                            var compData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[action.compIndex], action.compSize).GetUnsafePtr();

                            void* usrDataPtr = null;
                            if (action.userTypeId >= 0 && chunk.Has(userTypes[action.userTypeId]))
                            {
                                var usrData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(userTypes[action.userTypeId], action.userTypeSize).GetUnsafeReadOnlyPtr();
                                usrDataPtr = usrData + action.userTypeSize * ent;
                            }

                            byte* dataPtr = ((byte*) state) + action.compBackupOffset;
                            action.action.Ptr.Invoke(compData + action.compSize * ent, dataPtr + action.compSize * ent,
                                usrDataPtr);
                        }
                    }
                }

                var linkedEntityGroupAccessor = chunk.GetBufferAccessor(linkedEntityGroupType);
                foreach (var action in childActions)
                {
                    for (int ent = 0; ent < chunk.Count; ++ent)
                    {
                        // If this entity did not predict anything there was no rollback and no need to debug it
                        if (!GhostPredictionSystemGroup.ShouldPredict(tick, predictedGhostComponents[ent]))
                            continue;
                        if (entities[ent] != backupEntities[ent])
                            continue;
                        var linkedEntityGroup = linkedEntityGroupAccessor[ent];
                        var childEnt = linkedEntityGroup[action.entityIndex].Value;
                        if (childEntityLookup.TryGetValue(childEnt, out var childChunk) &&
                            childChunk.Chunk.Has(ghostChunkComponentTypesPtr[action.compIndex]))
                        {
                            var compData = (byte*)childChunk.Chunk.GetDynamicComponentDataArrayReinterpret<byte>(ghostChunkComponentTypesPtr[action.compIndex], action.compSize).GetUnsafePtr();

                            void* usrDataPtr = null;
                            if (action.userTypeId >= 0 && chunk.Has(userTypes[action.userTypeId]))
                            {
                                var usrData = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(userTypes[action.userTypeId], action.userTypeSize).GetUnsafeReadOnlyPtr();
                                usrDataPtr = usrData + action.userTypeSize * ent;
                            }

                            byte* dataPtr = ((byte*) state) + action.compBackupOffset;
                            action.action.Ptr.Invoke(compData + action.compSize * childChunk.IndexInChunk, dataPtr + action.compSize * ent, usrDataPtr);
                        }
                    }
                }
            }
        }
    }
}