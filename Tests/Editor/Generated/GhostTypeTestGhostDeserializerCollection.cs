using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode;

public struct GhostTypeTestGhostDeserializerCollection : IGhostDeserializerCollection
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public string[] CreateSerializerNameList()
    {
        var arr = new string[]
        {
            "GhostTypeIndex0TestGhostSerializer",
            "GhostTypeIndex1TestGhostSerializer",
        };
        return arr;
    }

    public int Length => 2;
#endif
    public void Initialize(World world)
    {
        var curGhostTypeIndex0TestGhostSpawnSystem = world.GetOrCreateSystem<GhostTypeIndex0TestGhostSpawnSystem>();
        m_GhostTypeIndex0TestSnapshotDataNewGhostIds = curGhostTypeIndex0TestGhostSpawnSystem.NewGhostIds;
        m_GhostTypeIndex0TestSnapshotDataNewGhosts = curGhostTypeIndex0TestGhostSpawnSystem.NewGhosts;
        curGhostTypeIndex0TestGhostSpawnSystem.GhostType = 0;
        var curGhostTypeIndex1TestGhostSpawnSystem = world.GetOrCreateSystem<GhostTypeIndex1TestGhostSpawnSystem>();
        m_GhostTypeIndex1TestSnapshotDataNewGhostIds = curGhostTypeIndex1TestGhostSpawnSystem.NewGhostIds;
        m_GhostTypeIndex1TestSnapshotDataNewGhosts = curGhostTypeIndex1TestGhostSpawnSystem.NewGhosts;
        curGhostTypeIndex1TestGhostSpawnSystem.GhostType = 1;
    }

    public void BeginDeserialize(JobComponentSystem system)
    {
        m_GhostTypeIndex0TestSnapshotDataFromEntity = system.GetBufferFromEntity<GhostTypeIndex0TestSnapshotData>();
        m_GhostTypeIndex1TestSnapshotDataFromEntity = system.GetBufferFromEntity<GhostTypeIndex1TestSnapshotData>();
    }
    public bool Deserialize(int serializer, Entity entity, uint snapshot, uint baseline, uint baseline2, uint baseline3,
        ref DataStreamReader reader, NetworkCompressionModel compressionModel)
    {
        switch (serializer)
        {
            case 0:
                return GhostReceiveSystem<GhostTypeTestGhostDeserializerCollection>.InvokeDeserialize(m_GhostTypeIndex0TestSnapshotDataFromEntity, entity, snapshot, baseline, baseline2,
                baseline3, ref reader, compressionModel);
            case 1:
                return GhostReceiveSystem<GhostTypeTestGhostDeserializerCollection>.InvokeDeserialize(m_GhostTypeIndex1TestSnapshotDataFromEntity, entity, snapshot, baseline, baseline2,
                baseline3, ref reader, compressionModel);
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }
    public void Spawn(int serializer, int ghostId, uint snapshot, ref DataStreamReader reader,
        NetworkCompressionModel compressionModel)
    {
        switch (serializer)
        {
            case 0:
                m_GhostTypeIndex0TestSnapshotDataNewGhostIds.Add(ghostId);
                m_GhostTypeIndex0TestSnapshotDataNewGhosts.Add(GhostReceiveSystem<GhostTypeTestGhostDeserializerCollection>.InvokeSpawn<GhostTypeIndex0TestSnapshotData>(snapshot, ref reader, compressionModel));
                break;
            case 1:
                m_GhostTypeIndex1TestSnapshotDataNewGhostIds.Add(ghostId);
                m_GhostTypeIndex1TestSnapshotDataNewGhosts.Add(GhostReceiveSystem<GhostTypeTestGhostDeserializerCollection>.InvokeSpawn<GhostTypeIndex1TestSnapshotData>(snapshot, ref reader, compressionModel));
                break;
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }

    private BufferFromEntity<GhostTypeIndex0TestSnapshotData> m_GhostTypeIndex0TestSnapshotDataFromEntity;
    private NativeList<int> m_GhostTypeIndex0TestSnapshotDataNewGhostIds;
    private NativeList<GhostTypeIndex0TestSnapshotData> m_GhostTypeIndex0TestSnapshotDataNewGhosts;
    private BufferFromEntity<GhostTypeIndex1TestSnapshotData> m_GhostTypeIndex1TestSnapshotDataFromEntity;
    private NativeList<int> m_GhostTypeIndex1TestSnapshotDataNewGhostIds;
    private NativeList<GhostTypeIndex1TestSnapshotData> m_GhostTypeIndex1TestSnapshotDataNewGhosts;
}
public struct EnableGhostTypeTestGhostReceiveSystemComponent : IComponentData
{}
public class GhostTypeTestGhostReceiveSystem : GhostReceiveSystem<GhostTypeTestGhostDeserializerCollection>
{
    protected override void OnCreate()
    {
        base.OnCreate();
        RequireSingletonForUpdate<EnableGhostTypeTestGhostReceiveSystemComponent>();
    }
}
