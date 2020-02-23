using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode;

public struct LagCompensationTestGhostDeserializerCollection : IGhostDeserializerCollection
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public string[] CreateSerializerNameList()
    {
        var arr = new string[]
        {
            "LagCompensationTestPlayerGhostSerializer",
            "LagCompensationTestCubeGhostSerializer",
        };
        return arr;
    }

    public int Length => 2;
#endif
    public void Initialize(World world)
    {
        var curLagCompensationTestPlayerGhostSpawnSystem = world.GetOrCreateSystem<LagCompensationTestPlayerGhostSpawnSystem>();
        m_LagCompensationTestPlayerSnapshotDataNewGhostIds = curLagCompensationTestPlayerGhostSpawnSystem.NewGhostIds;
        m_LagCompensationTestPlayerSnapshotDataNewGhosts = curLagCompensationTestPlayerGhostSpawnSystem.NewGhosts;
        curLagCompensationTestPlayerGhostSpawnSystem.GhostType = 0;
        var curLagCompensationTestCubeGhostSpawnSystem = world.GetOrCreateSystem<LagCompensationTestCubeGhostSpawnSystem>();
        m_LagCompensationTestCubeSnapshotDataNewGhostIds = curLagCompensationTestCubeGhostSpawnSystem.NewGhostIds;
        m_LagCompensationTestCubeSnapshotDataNewGhosts = curLagCompensationTestCubeGhostSpawnSystem.NewGhosts;
        curLagCompensationTestCubeGhostSpawnSystem.GhostType = 1;
    }

    public void BeginDeserialize(JobComponentSystem system)
    {
        m_LagCompensationTestPlayerSnapshotDataFromEntity = system.GetBufferFromEntity<LagCompensationTestPlayerSnapshotData>();
        m_LagCompensationTestCubeSnapshotDataFromEntity = system.GetBufferFromEntity<LagCompensationTestCubeSnapshotData>();
    }
    public bool Deserialize(int serializer, Entity entity, uint snapshot, uint baseline, uint baseline2, uint baseline3,
        ref DataStreamReader reader, NetworkCompressionModel compressionModel)
    {
        switch (serializer)
        {
            case 0:
                return GhostReceiveSystem<LagCompensationTestGhostDeserializerCollection>.InvokeDeserialize(m_LagCompensationTestPlayerSnapshotDataFromEntity, entity, snapshot, baseline, baseline2,
                baseline3, ref reader, compressionModel);
            case 1:
                return GhostReceiveSystem<LagCompensationTestGhostDeserializerCollection>.InvokeDeserialize(m_LagCompensationTestCubeSnapshotDataFromEntity, entity, snapshot, baseline, baseline2,
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
                m_LagCompensationTestPlayerSnapshotDataNewGhostIds.Add(ghostId);
                m_LagCompensationTestPlayerSnapshotDataNewGhosts.Add(GhostReceiveSystem<LagCompensationTestGhostDeserializerCollection>.InvokeSpawn<LagCompensationTestPlayerSnapshotData>(snapshot, ref reader, compressionModel));
                break;
            case 1:
                m_LagCompensationTestCubeSnapshotDataNewGhostIds.Add(ghostId);
                m_LagCompensationTestCubeSnapshotDataNewGhosts.Add(GhostReceiveSystem<LagCompensationTestGhostDeserializerCollection>.InvokeSpawn<LagCompensationTestCubeSnapshotData>(snapshot, ref reader, compressionModel));
                break;
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }

    private BufferFromEntity<LagCompensationTestPlayerSnapshotData> m_LagCompensationTestPlayerSnapshotDataFromEntity;
    private NativeList<int> m_LagCompensationTestPlayerSnapshotDataNewGhostIds;
    private NativeList<LagCompensationTestPlayerSnapshotData> m_LagCompensationTestPlayerSnapshotDataNewGhosts;
    private BufferFromEntity<LagCompensationTestCubeSnapshotData> m_LagCompensationTestCubeSnapshotDataFromEntity;
    private NativeList<int> m_LagCompensationTestCubeSnapshotDataNewGhostIds;
    private NativeList<LagCompensationTestCubeSnapshotData> m_LagCompensationTestCubeSnapshotDataNewGhosts;
}
public struct EnableLagCompensationTestGhostReceiveSystemComponent : IComponentData
{}
public class LagCompensationTestGhostReceiveSystem : GhostReceiveSystem<LagCompensationTestGhostDeserializerCollection>
{
    protected override void OnCreate()
    {
        base.OnCreate();
        RequireSingletonForUpdate<EnableLagCompensationTestGhostReceiveSystemComponent>();
    }
}
