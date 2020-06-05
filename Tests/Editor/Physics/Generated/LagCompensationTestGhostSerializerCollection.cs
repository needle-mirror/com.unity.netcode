using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode;

public struct LagCompensationTestGhostSerializerCollection : IGhostSerializerCollection
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
    public static int FindGhostType<T>()
        where T : struct, ISnapshotData<T>
    {
        if (typeof(T) == typeof(LagCompensationTestPlayerSnapshotData))
            return 0;
        if (typeof(T) == typeof(LagCompensationTestCubeSnapshotData))
            return 1;
        return -1;
    }

    public void BeginSerialize(ComponentSystemBase system)
    {
        m_LagCompensationTestPlayerGhostSerializer.BeginSerialize(system);
        m_LagCompensationTestCubeGhostSerializer.BeginSerialize(system);
    }

    public int CalculateImportance(int serializer, ArchetypeChunk chunk)
    {
        switch (serializer)
        {
            case 0:
                return m_LagCompensationTestPlayerGhostSerializer.CalculateImportance(chunk);
            case 1:
                return m_LagCompensationTestCubeGhostSerializer.CalculateImportance(chunk);
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public int GetSnapshotSize(int serializer)
    {
        switch (serializer)
        {
            case 0:
                return m_LagCompensationTestPlayerGhostSerializer.SnapshotSize;
            case 1:
                return m_LagCompensationTestCubeGhostSerializer.SnapshotSize;
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public int Serialize(ref DataStreamWriter dataStream, SerializeData data)
    {
        switch (data.ghostType)
        {
            case 0:
            {
                return GhostSendSystem<LagCompensationTestGhostSerializerCollection>.InvokeSerialize<LagCompensationTestPlayerGhostSerializer, LagCompensationTestPlayerSnapshotData>(m_LagCompensationTestPlayerGhostSerializer, ref dataStream, data);
            }
            case 1:
            {
                return GhostSendSystem<LagCompensationTestGhostSerializerCollection>.InvokeSerialize<LagCompensationTestCubeGhostSerializer, LagCompensationTestCubeSnapshotData>(m_LagCompensationTestCubeGhostSerializer, ref dataStream, data);
            }
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }
    private LagCompensationTestPlayerGhostSerializer m_LagCompensationTestPlayerGhostSerializer;
    private LagCompensationTestCubeGhostSerializer m_LagCompensationTestCubeGhostSerializer;
}

public struct EnableLagCompensationTestGhostSendSystemComponent : IComponentData
{}
public class LagCompensationTestGhostSendSystem : GhostSendSystem<LagCompensationTestGhostSerializerCollection>
{
    protected override void OnCreate()
    {
        base.OnCreate();
        RequireSingletonForUpdate<EnableLagCompensationTestGhostSendSystemComponent>();
    }

    public override bool IsEnabled()
    {
        return HasSingleton<EnableLagCompensationTestGhostSendSystemComponent>();
    }
}
