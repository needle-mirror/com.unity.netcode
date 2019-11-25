using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode;

#region __END_HEADER__
#endregion
public struct __GHOST_COLLECTION_PREFIX__GhostDeserializerCollection : IGhostDeserializerCollection
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
    public void Initialize(World world)
    {
        #region __GHOST_INITIALIZE_DESERIALIZE__
        var cur__GHOST_SPAWNER_TYPE__ = world.GetOrCreateSystem<__GHOST_SPAWNER_TYPE__>();
        m___GHOST_SNAPSHOT_TYPE__NewGhostIds = cur__GHOST_SPAWNER_TYPE__.NewGhostIds;
        m___GHOST_SNAPSHOT_TYPE__NewGhosts = cur__GHOST_SPAWNER_TYPE__.NewGhosts;
        cur__GHOST_SPAWNER_TYPE__.GhostType = __GHOST_SERIALIZER_INDEX__;
        #endregion
    }

    public void BeginDeserialize(JobComponentSystem system)
    {
        #region __GHOST_BEGIN_DESERIALIZE__
        m___GHOST_SNAPSHOT_TYPE__FromEntity = system.GetBufferFromEntity<__GHOST_SNAPSHOT_TYPE__>();
        #endregion
    }
    public bool Deserialize(int serializer, Entity entity, uint snapshot, uint baseline, uint baseline2, uint baseline3,
        DataStreamReader reader,
        ref DataStreamReader.Context ctx, NetworkCompressionModel compressionModel)
    {
        switch (serializer)
        {
            #region __GHOST_INVOKE_DESERIALIZE__
            case __GHOST_SERIALIZER_INDEX__:
                return GhostReceiveSystem<__GHOST_COLLECTION_PREFIX__GhostDeserializerCollection>.InvokeDeserialize(m___GHOST_SNAPSHOT_TYPE__FromEntity, entity, snapshot, baseline, baseline2,
                baseline3, reader, ref ctx, compressionModel);
            #endregion
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }
    public void Spawn(int serializer, int ghostId, uint snapshot, DataStreamReader reader,
        ref DataStreamReader.Context ctx, NetworkCompressionModel compressionModel)
    {
        switch (serializer)
        {
            #region __GHOST_INVOKE_SPAWN__
            case __GHOST_SERIALIZER_INDEX__:
                m___GHOST_SNAPSHOT_TYPE__NewGhostIds.Add(ghostId);
                m___GHOST_SNAPSHOT_TYPE__NewGhosts.Add(GhostReceiveSystem<__GHOST_COLLECTION_PREFIX__GhostDeserializerCollection>.InvokeSpawn<__GHOST_SNAPSHOT_TYPE__>(snapshot, reader, ref ctx, compressionModel));
                break;
            #endregion
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }

    #region __GHOST_DESERIALIZER_INSTANCE__
    private BufferFromEntity<__GHOST_SNAPSHOT_TYPE__> m___GHOST_SNAPSHOT_TYPE__FromEntity;
    private NativeList<int> m___GHOST_SNAPSHOT_TYPE__NewGhostIds;
    private NativeList<__GHOST_SNAPSHOT_TYPE__> m___GHOST_SNAPSHOT_TYPE__NewGhosts;
    #endregion
}
public struct Enable__GHOST_SYSTEM_PREFIX__GhostReceiveSystemComponent : IComponentData
{}
public class __GHOST_SYSTEM_PREFIX__GhostReceiveSystem : GhostReceiveSystem<__GHOST_COLLECTION_PREFIX__GhostDeserializerCollection>
{
    protected override void OnCreate()
    {
        base.OnCreate();
        RequireSingletonForUpdate<Enable__GHOST_SYSTEM_PREFIX__GhostReceiveSystemComponent>();
    }
}
