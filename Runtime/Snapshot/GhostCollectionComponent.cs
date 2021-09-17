using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.NetCode.LowLevel.Unsafe;

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

        [StructLayout(LayoutKind.Sequential)]
        public struct ComponentInfo
        {
            //The Component StableTypeHash
            public ulong StableHash;
            //Serializer variant to use. If 0, the default for that type is used.
            public ulong Variant;
            //The SendMask override for the component if different than -1
            public int SendMaskOverride;
            //Override default SendToChildEntity for the component if different than -1
            public int SendToChildEntityOverride;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ComponentReference
        {
            public ComponentReference(int index, ulong hash)
            {
                EntityIndex = index;
                StableHash = hash;
            }
            //The entity index in the linkedEntityGroup
            public int EntityIndex;
            //The component stable hash
            public ulong StableHash;
        }

        public int Importance;
        public GhostMode SupportedModes;
        public GhostMode DefaultMode;
        public bool StaticOptimization;
        public BlobString Name;
        //Array of components for each children in the hierarchy
        public BlobArray<ComponentInfo> ServerComponentList;
        public BlobArray<int> NumServerComponentsPerEntity;
        /// <summary>
        /// A list of (child index, components) pair which should be removed from the prefab when using it on the server. The main use-case is to support ClientAndServer data.
        /// </summary>
        public BlobArray<ComponentReference> RemoveOnServer;
        /// <summary>
        /// A list of (child index, components) pair  which should be removed from the prefab when using it on the client. The main use-case is to support ClientAndServer data.
        /// </summary>
        public BlobArray<ComponentReference> RemoveOnClient;
        /// <summary>
        /// A list of (child index, components) pair  which should be disabled when the prefab is used to instantiate a predicted ghost. This is used so we can have a single client prefab.
        /// </summary>
        public BlobArray<ComponentReference> DisableOnPredictedClient;
        /// <summary>
        /// A list of (child index, components) pair  which should be disabled when the prefab is used to instantiate an interpolated ghost. This is used so we can have a single client prefab.
        /// </summary>
        public BlobArray<ComponentReference> DisableOnInterpolatedClient;
    }

    /// <summary>
    /// A component added to all ghost prefabs. It contains the meta-data required to use the prefab as a ghost.
    /// </summary>
    [DontSupportPrefabOverrides]
    public struct GhostPrefabMetaDataComponent : IComponentData
    {
        public BlobAssetReference<GhostPrefabMetaData> Value;
    }

    /// <summary>
    /// A component added to ghost prefabs which require runtime stripping of components before they can be used.
    /// The component is removed when the runtime stripping is performed.
    /// </summary>
    public struct GhostPrefabRuntimeStrip : IComponentData
    {}

    /// <summary>
    /// A component used to identify the singleton which owns the ghost collection lists and data.
    /// The singleton contains buffers for GhostCollectionPrefab, GhostCollectionPrefabSerializer,
    /// GhostCollectionComponentIndex and GhostComponentSerializer.State
    /// </summary>
    public struct GhostCollection : IComponentData
    {
        public int NumLoadedPrefabs;
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        public int NumPredictionErrorNames;
        #endif
        public bool IsInGame;
    }

    /// <summary>
    /// A list of all prefabs which can be used for ghosts. This is populated with all ghost prefabs on the server
    /// and that list is sent for clients. Having a prefab in this list does not guarantee that there is a serializer
    /// for it yet.
    /// Added to the GhostCollection singleton entity.
    /// </summary>
    [InternalBufferCapacity(ghostCollectionPrefabBufferSize)]
    public struct GhostCollectionPrefab : IBufferElementData
    {
        internal const int ghostCollectionPrefabBufferSize = 96;

        public enum LoadingState
        {
            NotLoading = 0,
            LoadingActive,
            LoadingNotActive
        }
        public GhostTypeComponent GhostType;
        public Entity GhostPrefab;
        public ulong Hash;
        /// <summary>
        /// Game code should set this to LoadingActive if the prefab is currently being loaded. The collection system
        /// will set it to LoadingNotActive every frame, so game code must reset it to LoadingActive every frame the
        /// prefab is still loading.
        /// </summary>
        public LoadingState Loading;
    }
    /// <summary>
    /// A list of all serializer data for the prefabs in GhostCollectionPrefab. This list can be shorter if not all
    /// serializers are created yet.
    /// Added to the GhostCollection singleton entity.
    /// </summary>
    [InternalBufferCapacity(GhostCollectionPrefab.ghostCollectionPrefabBufferSize)]
    public struct GhostCollectionPrefabSerializer : IBufferElementData
    {
        public ulong TypeHash;
        public int FirstComponent;
        public int NumComponents;
        public int NumChildComponents;
        public int SnapshotSize;
        public int ChangeMaskBits;
        public int PredictionOwnerOffset;
        public int OwnerPredicted;
        public byte PartialComponents;
        public byte PartialSendToOwner;
        public bool StaticOptimization;
        public int BaseImportance;
        public GhostSpawnBuffer.Type FallbackPredictionMode;
        public int IsGhostGroup;
        public int MaxBufferSnapshotSize;
        public int NumBuffers;
        public Profiling.ProfilerMarker profilerMarker;
    }

    /// <summary>
    /// This list contains the set of uniques component which support serialization. Used to map the DynamicComponentTypeHandle
    /// to a concrete ComponentType in jobs.
    /// Added to the GhostCollection singleton entity.
    /// </summary>
    [InternalBufferCapacity(128)]
    public struct GhostCollectionComponentType : IBufferElementData
    {
        public ComponentType Type;
        public int FirstSerializer;
        public int LastSerializer;
    }

    /// <summary>
    /// This list contains the set of entity + component for all serialization rules in GhostCollectionPrefabSerializer.
    /// GhostCollectionPrefabSerializer contains a FirstComponent and NumComponents which identifies the set of components
    /// to use from this array.
    /// Added to the GhostCollection singleton entity.
    /// </summary>
    [InternalBufferCapacity(128)]
    public struct GhostCollectionComponentIndex : IBufferElementData
    {
        public int EntityIndex;
        // index in the GhostComponentCollection, used to retrieve the component type from the DynamicTypeHandle
        public int ComponentIndex;
        // index in the GhostComponentSerializer.State collection, used to get the type of serializer to use
        public int SerializerIndex;
        // current send mask for that component, used to not send/receive components in some configuration
        public GhostComponentSerializer.SendMask SendMask;
        // state if the component can be sent or not for children entities. By default is set 1 (always send)
        public int SendForChildEntity;
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        public int PredictionErrorBaseIndex;
        #endif
    }

}
