using System;
using Unity.Collections;
using Unity.Entities;

namespace Unity.NetCode
{
    /// <summary>
    /// The hash of all the ghost component data which exists in the scene. This can be
    /// used to sort the subscenes so the ghost IDs of the pre-spawned scene objects line
    /// up deterministically.
    /// </summary>
    public struct SubSceneGhostComponentHash : ISharedComponentData
    {
        public ulong Value;
    }

    /// <summary>
    /// Unique within a subscene and used to deterministically assign ghost id to pre-spawned ghost entities.
    /// </summary>
    public struct PreSpawnedGhostIndex : IComponentData
    {
        public int Value;
    }

    /// <summary>
    /// Added to SubScene entity when all the baselines and the pre-spawned ghosts has been processed.
    /// </summary>
    public struct PrespawnsSceneInitialized : IComponentData
    {
    }

    /// <summary>
    /// Added during conversion to all subscenes that contains pre-spawned ghosts.
    /// </summary>
    public struct SubSceneWithPrespawnGhosts : IComponentData
    {
        /// <summary>
        /// Deterministic unique Hash used to query for all the ghost belonging to the scene
        /// </summary>
        public ulong SubSceneHash;

        /// <summary>
        /// Computed at runtime, when the scene is processed
        /// </summary>
        public ulong BaselinesHash;

        /// <summary>
        /// Total number of prespawns in the scene
        /// </summary>
        public int PrespawnCount;
    }

#if UNITY_EDITOR
    // When sub-scene are open for edit the SubSceneSectionData is not present on the entity.
    // This component is added instead to these entity to track which section they are referring to.
    // The SceneGUI and Section are necessary to correctly add the SceneSection component to the pre-spawned ghosts when
    // they are re-spawned (because of relevancy for example)
    public struct LiveLinkPrespawnSectionReference : IComponentData
    {
        public Hash128 SceneGUID;
        public int Section;
    }
#endif

    /// <summary>
    /// Tag component added to subscene entity when the prespawn baselines has been serialized.
    /// </summary>
    public struct SubScenePrespawnBaselineResolved : IComponentData
    {
    }

    /// <summary>
    /// Buffer added during conversion to all the ghost with a PrespawnId component
    /// The buffer will contains the a pre-serialized ghost snapshot, generated at the time the PrespawnGhostBaselineSystem
    /// process the entity.
    /// The Prespawn baselines are used for bandwidth optimization for late joining player. The server will send only the
    /// prespawn ghost that has changed in respect to that baseline to the new client.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PrespawnGhostBaseline : IBufferElementData
    {
        public byte Value;
    }

    /// <summary>
    /// For each loaded and process subscene, the server authoritatively populate this buffer.
    /// The client will receive it as part of the snapshot stream and will use the information to correctly
    /// process any matching loaded subscene.
    /// Hash are used to verify on the client that everything match the server to correctly use the prefab baseline
    /// optimization.
    /// InternalBufferCapacity allocated to almost max out chunk memory.
    /// </summary>
    [InternalBufferCapacity(600)]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendDataForChildEntity = false)]
    public struct PrespawnSceneLoaded : IBufferElementData
    {
        [GhostField]public ulong SubSceneHash;
        [GhostField]public ulong BaselineHash;
        [GhostField]public int FirstGhostId;
        [GhostField]public int PrespawnCount;
    }

    /// <summary>
    /// Added to the PrespawnGhostIdAllocator singleton entity.
    /// GhostId allocation map for prespawn object. Used by the server to track the subset of ghost ids that are associated
    /// to a scene that contains prespawned ghosts.
    /// InternalBufferCapacity is set to (approximately) max out the chunk.
    /// </summary>
    [InternalBufferCapacity(950)]
    public struct PrespawnGhostIdRange : IBufferElementData
    {
        public ulong SubSceneHash;
        public int FirstGhostId;
        //the number of prespawns
        public short Count;
        // 1 when the range is reserved, 0 when it can be re-used
        public short Reserved;
    }

    /// <summary>
    /// System state component added to all subscenes with ghost. Used for tracking when a subscene is unloaded on both client and server
    /// </summary>
    struct SubSceneWithGhostStateComponent : ISystemStateComponentData
    {
        public ulong SubSceneHash;
        public Hash128 SceneGUID;
        public int SectionIndex;
        public int FirstGhostId;
        public int PrespawnCount;
        public int Streaming;
    }

    /// <summary>
    /// Component added by the server to to the NetworkStream entity. Used to track witch prespawned ghost sections
    /// has been loaded/acked by the client.
    /// The server streams prespawned ghost only for the sections that as been notified ready by the client.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct PrespawnSectionAck : IBufferElementData
    {
        public ulong SceneHash;
    }
}
