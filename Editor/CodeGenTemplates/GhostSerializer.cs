using Unity.Collections.LowLevel.Unsafe;
#region __GHOST_USING_STATEMENT__
using __GHOST_USING__;
#endregion

#region __END_HEADER__
#endregion
public struct __GHOST_NAME__GhostSerializer : IGhostSerializer<__GHOST_NAME__SnapshotData>
{
    #region __GHOST_COMPONENT_TYPE__
    private ComponentType componentType__GHOST_COMPONENT_TYPE_NAME__;
    #endregion
    // FIXME: These disable safety since all serializers have an instance of the same type - causing aliasing. Should be fixed in a cleaner way
    #region __GHOST_COMPONENT_TYPE_DATA__
    [NativeDisableContainerSafetyRestriction][ReadOnly] private ArchetypeChunkComponentType<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
    #endregion
    #region __GHOST_BUFFER_COMPONENT_TYPE_DATA__
    [NativeDisableContainerSafetyRestriction][ReadOnly] private ArchetypeChunkBufferType<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
    #endregion
    #region __GHOST_COMPONENT_TYPE_CHILD_DATA__
    [NativeDisableContainerSafetyRestriction][ReadOnly] private ComponentDataFromEntity<__GHOST_COMPONENT_TYPE__> ghost__GHOST_COMPONENT_TYPE_NAME__Type;
    #endregion


    public int CalculateImportance(ArchetypeChunk chunk)
    {
        return __GHOST_IMPORTANCE__;
    }

    public bool WantsPredictionDelta => true;

    public int SnapshotSize => UnsafeUtility.SizeOf<__GHOST_NAME__SnapshotData>();
    public void BeginSerialize(ComponentSystemBase system)
    {
        #region __GHOST_ASSIGN_COMPONENT_TYPE__
        componentType__GHOST_COMPONENT_TYPE_NAME__ = ComponentType.ReadWrite<__GHOST_COMPONENT_TYPE__>();
        #endregion
        #region __GHOST_ASSIGN_COMPONENT_TYPE_DATA__
        ghost__GHOST_COMPONENT_TYPE_NAME__Type = system.GetArchetypeChunkComponentType<__GHOST_COMPONENT_TYPE__>(true);
        #endregion
        #region __GHOST_ASSIGN_BUFFER_COMPONENT_TYPE_DATA__
        ghost__GHOST_COMPONENT_TYPE_NAME__Type = system.GetArchetypeChunkBufferType<__GHOST_COMPONENT_TYPE__>(true);
        #endregion
        #region __GHOST_ASSIGN_COMPONENT_TYPE_CHILD_DATA__
        ghost__GHOST_COMPONENT_TYPE_NAME__Type = system.GetComponentDataFromEntity<__GHOST_COMPONENT_TYPE__>(true);
        #endregion
    }

    public bool CanSerialize(EntityArchetype arch)
    {
        var components = arch.GetComponentTypes();
        int matches = 0;
        for (int i = 0; i < components.Length; ++i)
        {
            #region __GHOST_COMPONENT_TYPE_CHECK__
            if (components[i] == componentType__GHOST_COMPONENT_TYPE_NAME__)
                ++matches;
            #endregion
        }
        return (matches == __GHOST_COMPONENT_COUNT__);
    }

    public void CopyToSnapshot(ArchetypeChunk chunk, int ent, uint tick, ref __GHOST_NAME__SnapshotData snapshot, GhostSerializerState serializerState)
    {
        snapshot.tick = tick;
        #region __GHOST_ASSIGN_CHUNK_ARRAY__
        var chunkData__GHOST_COMPONENT_TYPE_NAME__ = chunk.GetNativeArray(ghost__GHOST_COMPONENT_TYPE_NAME__Type);
        #endregion
        #region __GHOST_ASSIGN_CHUNK_BUFFER_ARRAY__
        var chunkData__GHOST_COMPONENT_TYPE_NAME__ = chunk.GetBufferAccessor(ghost__GHOST_COMPONENT_TYPE_NAME__Type);
        #endregion
        #region __GHOST_ASSIGN_SNAPSHOT__
        snapshot.Set__GHOST_COMPONENT_TYPE_NAME____GHOST_FIELD_NAME__(chunkData__GHOST_COMPONENT_TYPE_NAME__[ent].__GHOST_FIELD_NAME__, serializerState);
        #endregion
        #region __GHOST_ASSIGN_CHILD_SNAPSHOT__
        snapshot.Set__GHOST_COMPONENT_TYPE_NAME____GHOST_FIELD_NAME__(ghost__GHOST_COMPONENT_TYPE_NAME__Type[chunkDataLinkedEntityGroup[ent][__GHOST_ENTITY_INDEX__].Value].__GHOST_FIELD_NAME__, serializerState);
        #endregion
    }
}
