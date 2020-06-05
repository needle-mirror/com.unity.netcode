using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Networking.Transport;
using Unity.NetCode;

public struct GhostTypeTestGhostSerializerCollection : IGhostSerializerCollection
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
    public static int FindGhostType<T>()
        where T : struct, ISnapshotData<T>
    {
        if (typeof(T) == typeof(GhostTypeIndex0TestSnapshotData))
            return 0;
        if (typeof(T) == typeof(GhostTypeIndex1TestSnapshotData))
            return 1;
        return -1;
    }

    public void BeginSerialize(ComponentSystemBase system)
    {
        m_GhostTypeIndex0TestGhostSerializer.BeginSerialize(system);
        m_GhostTypeIndex1TestGhostSerializer.BeginSerialize(system);
    }

    public int CalculateImportance(int serializer, ArchetypeChunk chunk)
    {
        switch (serializer)
        {
            case 0:
                return m_GhostTypeIndex0TestGhostSerializer.CalculateImportance(chunk);
            case 1:
                return m_GhostTypeIndex1TestGhostSerializer.CalculateImportance(chunk);
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public int GetSnapshotSize(int serializer)
    {
        switch (serializer)
        {
            case 0:
                return m_GhostTypeIndex0TestGhostSerializer.SnapshotSize;
            case 1:
                return m_GhostTypeIndex1TestGhostSerializer.SnapshotSize;
        }

        throw new ArgumentException("Invalid serializer type");
    }

    public int Serialize(ref DataStreamWriter dataStream, SerializeData data)
    {
        switch (data.ghostType)
        {
            case 0:
            {
                return GhostSendSystem<GhostTypeTestGhostSerializerCollection>.InvokeSerialize<GhostTypeIndex0TestGhostSerializer, GhostTypeIndex0TestSnapshotData>(m_GhostTypeIndex0TestGhostSerializer, ref dataStream, data);
            }
            case 1:
            {
                return GhostSendSystem<GhostTypeTestGhostSerializerCollection>.InvokeSerialize<GhostTypeIndex1TestGhostSerializer, GhostTypeIndex1TestSnapshotData>(m_GhostTypeIndex1TestGhostSerializer, ref dataStream, data);
            }
            default:
                throw new ArgumentException("Invalid serializer type");
        }
    }
    private GhostTypeIndex0TestGhostSerializer m_GhostTypeIndex0TestGhostSerializer;
    private GhostTypeIndex1TestGhostSerializer m_GhostTypeIndex1TestGhostSerializer;
}

public struct EnableGhostTypeTestGhostSendSystemComponent : IComponentData
{}
public class GhostTypeTestGhostSendSystem : GhostSendSystem<GhostTypeTestGhostSerializerCollection>
{
    protected override void OnCreate()
    {
        base.OnCreate();
        RequireSingletonForUpdate<EnableGhostTypeTestGhostSendSystemComponent>();
    }

    public override bool IsEnabled()
    {
        return HasSingleton<EnableGhostTypeTestGhostSendSystemComponent>();
    }
}
