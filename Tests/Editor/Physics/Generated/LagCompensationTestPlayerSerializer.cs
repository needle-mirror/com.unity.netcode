using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Collections;
using Unity.NetCode;
using Unity.NetCode.Physics.Tests;
using Unity.Transforms;

public struct LagCompensationTestPlayerGhostSerializer : IGhostSerializer<LagCompensationTestPlayerSnapshotData>
{
    private ComponentType componentTypeCommandDataInterpolationDelay;
    private ComponentType componentTypeLagCompensationTestCommand;
    private ComponentType componentTypeLagCompensationTestPlayer;
    private ComponentType componentTypeLocalToWorld;
    private ComponentType componentTypeRotation;
    private ComponentType componentTypeTranslation;
    // FIXME: These disable safety since all serializers have an instance of the same type - causing aliasing. Should be fixed in a cleaner way
    [NativeDisableContainerSafetyRestriction][ReadOnly] private ArchetypeChunkComponentType<LagCompensationTestPlayer> ghostLagCompensationTestPlayerType;
    [NativeDisableContainerSafetyRestriction][ReadOnly] private ArchetypeChunkComponentType<Rotation> ghostRotationType;
    [NativeDisableContainerSafetyRestriction][ReadOnly] private ArchetypeChunkComponentType<Translation> ghostTranslationType;


    public int CalculateImportance(ArchetypeChunk chunk)
    {
        return 1;
    }

    public int SnapshotSize => UnsafeUtility.SizeOf<LagCompensationTestPlayerSnapshotData>();
    public void BeginSerialize(ComponentSystemBase system)
    {
        componentTypeCommandDataInterpolationDelay = ComponentType.ReadWrite<CommandDataInterpolationDelay>();
        componentTypeLagCompensationTestCommand = ComponentType.ReadWrite<LagCompensationTestCommand>();
        componentTypeLagCompensationTestPlayer = ComponentType.ReadWrite<LagCompensationTestPlayer>();
        componentTypeLocalToWorld = ComponentType.ReadWrite<LocalToWorld>();
        componentTypeRotation = ComponentType.ReadWrite<Rotation>();
        componentTypeTranslation = ComponentType.ReadWrite<Translation>();
        ghostLagCompensationTestPlayerType = system.GetArchetypeChunkComponentType<LagCompensationTestPlayer>(true);
        ghostRotationType = system.GetArchetypeChunkComponentType<Rotation>(true);
        ghostTranslationType = system.GetArchetypeChunkComponentType<Translation>(true);
    }

    public void CopyToSnapshot(ArchetypeChunk chunk, int ent, uint tick, ref LagCompensationTestPlayerSnapshotData snapshot, GhostSerializerState serializerState)
    {
        snapshot.tick = tick;
        var chunkDataLagCompensationTestPlayer = chunk.GetNativeArray(ghostLagCompensationTestPlayerType);
        var chunkDataRotation = chunk.GetNativeArray(ghostRotationType);
        var chunkDataTranslation = chunk.GetNativeArray(ghostTranslationType);
        snapshot.SetLagCompensationTestPlayerOwner(chunkDataLagCompensationTestPlayer[ent].Owner, serializerState);
        snapshot.SetRotationValue(chunkDataRotation[ent].Value, serializerState);
        snapshot.SetTranslationValue(chunkDataTranslation[ent].Value, serializerState);
    }
}
