using Unity.Entities;
using Unity.Collections;

namespace Unity.NetCode
{
    /// <summary>
    /// A BlobAsset containing all the meta data required for ghosts.
    /// </summary>
    public struct GhostPrefabMetaData
    {
        public enum GhostMode
        {
            Interpolated = 1,
            Predicted = 2,
            Both = 3
        }
        public int Importance;
        public GhostMode SupportedModes;
        public GhostMode DefaultMode;
        public bool StaticOptimization;
        public BlobString Name;
        public BlobArray<ulong> ServerComponentList;
        /// <summary>
        /// A list of components which should be removed from the prefab when using it on the server. The main use-case is to support ClientAndServer data.
        /// </summary>
        public BlobArray<ulong> RemoveOnServer;
        /// <summary>
        /// A list of components which should be removed from the prefab when using it on the client. The main use-case is to support ClientAndServer data.
        /// </summary>
        public BlobArray<ulong> RemoveOnClient;
        /// <summary>
        /// A list of components which should be disabled when the prefab is used to instantiate a predicted ghost. This is used so we can have a single client prefab.
        /// </summary>
        public BlobArray<ulong> DisableOnPredictedClient;
        /// <summary>
        /// A list of components which should be disabled when the prefab is used to instantiate an interpolated ghost. This is used so we can have a single client prefab.
        /// </summary>
        public BlobArray<ulong> DisableOnInterpolatedClient;
    }

    /// <summary>
    /// A component added to all ghost prefabs. It contains the meta-data required to use the prefab as a ghost.
    /// </summary>
    public struct GhostPrefabMetaDataComponent : IComponentData
    {
        public BlobAssetReference<GhostPrefabMetaData> Value;
    }


    /// <summary>
    /// A buffer added to the ghost prefab collection singleton containing references to all ghost prefabs.
    /// </summary>
    public struct GhostPrefabBuffer : IBufferElementData
    {
        public Entity Value;
    }

    /// <summary>
    /// Component added to the ghost collection in order to inditify teh singleton containing a GhostPrefabBuffer with
    /// the prefabs for all ghosts.
    /// </summary>
    public struct GhostPrefabCollectionComponent : IComponentData
    {
    }
}
