using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode;

#region __END_HEADER__
#endregion
public struct __GHOST_COLLECTION_PREFIX__GhostSerializerCollection : IGhostSerializerCollection
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public string[] CreateSerializerNameList()
    {
        var arr = new string[]
        {
            #region __GHOST_SERIALIZER_NAME__
            "__GHOST_SERIALIZER_TYPE__",
            #endregion
        };
        return arr;
    }

    public int Length => __GHOST_SERIALIZER_COUNT__;
#endif
    public static int FindGhostType<T>()
        where T : struct, ISnapshotData<T>
    {
        #region __GHOST_FIND_TYPE__
        if (typeof(T) == typeof(__GHOST_SNAPSHOT_TYPE__))
            return __GHOST_SERIALIZER_INDEX__;
        #endregion
        return -1;
    }
    public int FindSerializer(EntityArchetype arch)
    {
        #region __GHOST_FIND_CHECK__
        if (m___GHOST_SERIALIZER_TYPE__.CanSerialize(arch))
            return __GHOST_SERIALIZER_INDEX__;
        #endregion
        throw new ArgumentException("Invalid serializer type");
    }

    public void BeginSerialize(ComponentSystemBase system)
    {
        #region __GHOST_BEGIN_SERIALIZE__
        m___GHOST_SERIALIZER_TYPE__.BeginSerialize(system);
        #endregion
    }

    public int CalculateImportance(int serializer, ArchetypeChunk chunk)
    {
        switch (serializer)
        {
            #region __GHOST_CALCULATE_IMPORTANCE__
            case __GHOST_SERIALIZER_INDEX__:
                return m___GHOST_SERIALIZER_TYPE__.CalculateImportance(chunk);
            #endregion
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public bool WantsPredictionDelta(int serializer)
    {
        switch (serializer)
        {
            #region __GHOST_WANTS_PREDICTION_DELTA__
            case __GHOST_SERIALIZER_INDEX__:
                return m___GHOST_SERIALIZER_TYPE__.WantsPredictionDelta;
            #endregion
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public int GetSnapshotSize(int serializer)
    {
        switch (serializer)
        {
            #region __GHOST_SNAPSHOT_SIZE__
            case __GHOST_SERIALIZER_INDEX__:
                return m___GHOST_SERIALIZER_TYPE__.SnapshotSize;
            #endregion
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public int Serialize(SerializeData data)
    {
        switch (data.ghostType)
        {
            #region __GHOST_INVOKE_SERIALIZE__
            case __GHOST_SERIALIZER_INDEX__:
            {
                return GhostSendSystem<__GHOST_COLLECTION_PREFIX__GhostSerializerCollection>.InvokeSerialize<__GHOST_SERIALIZER_TYPE__, __GHOST_SNAPSHOT_TYPE__>(m___GHOST_SERIALIZER_TYPE__, data);
            }
            #endregion
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }
    #region __GHOST_SERIALIZER_INSTANCE__
    private __GHOST_SERIALIZER_TYPE__ m___GHOST_SERIALIZER_TYPE__;
    #endregion
}

public struct Enable__GHOST_SYSTEM_PREFIX__GhostSendSystemComponent : IComponentData
{}
public class __GHOST_SYSTEM_PREFIX__GhostSendSystem : GhostSendSystem<__GHOST_COLLECTION_PREFIX__GhostSerializerCollection>
{
    protected override void OnCreate()
    {
        base.OnCreate();
        RequireSingletonForUpdate<Enable__GHOST_SYSTEM_PREFIX__GhostSendSystemComponent>();
    }
}
