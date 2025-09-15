#if UNITY_EDITOR
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode.LowLevel.Unsafe;
using Unity.NetCode.Samples.Common.Editor;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if USING_ENTITIES_GRAPHICS
using Unity.Rendering;
#endif

namespace Unity.NetCode.Samples.Common
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [BurstCompile]
    partial class GhostImportanceDrawerSystem : SystemBase
    {
        private const string k_HeatmapGradientKey = "GhostImportanceDrawerSystem_HeatmapGradient";
        private const string k_DrawGridKey = "GhostImportanceDrawerSystem_DrawGrid";
        private const string k_DrawModeKey = "GhostImportanceDrawerSystem_DrawMode";
        private const string k_RenderDistanceKey = "GhostImportanceDrawerSystem_RenderDistance";
        private const string k_VertexColorShaderPath = "Packages/com.unity.netcode/Editor/Drawers/Assets/UnlitVertexColors.shader";
        private const string k_VertexColorShaderZTestPath = "Packages/com.unity.netcode/Editor/Drawers/Assets/UnlitVertexColorsZTest.shader";
        private static bool s_HasGhostImportanceSingleton;
        private static bool s_HasGhostDistanceData;
        private static bool s_SelectedEntityGhostPosition;
        private Mesh m_GridMesh;
        private NativeHashMap<ArchetypeChunk, PrioChunk> m_PrioChunks;
        private NativeList<DrawerHelpers.Vertex> m_GridVertices;
        private NativeList<int> m_GridIndices;
        private Mesh m_LineMesh;
        private NativeList<DrawerHelpers.Vertex> m_LineVertices;
        private NativeList<int> m_LineIndices;

        public enum DrawMode
        {
            None,

            /// <summary>Draws a per-entity importance heatmap.</summary>
            PerEntityImportanceHeatmap,

            /// <summary>Assigns a random color for each chunk, and draws said random color for all entities in that chunk.</summary>
            PerChunk,
        }

        public enum DrawGridMode
        {
            None,
            XY,
            XZ,
        }

        static DebugGhostDrawer.CustomDrawer s_CustomDrawer;
        // UI parameters
        static Entity s_SelectedConnectionEntity;
        static Entity[] s_ConnectionEntities;
        static string[] s_ConnectionIdLabels;
        static DrawMode s_Mode = DrawMode.PerEntityImportanceHeatmap;
        static DrawGridMode s_DrawGrid = DrawGridMode.None;
        static Gradient s_HeatmapGradient = SerializableGradient.DefaultGradient();
        static int s_RenderDistance;

        static void OnGuiDrawOptions()
        {
            if (Application.isPlaying && !s_HasGhostImportanceSingleton)
            {
                EditorGUILayout.HelpBox("The GhostImportance singleton is not present in the server world.", MessageType.Info);
                return;
            }

            int selectedIdx = Array.IndexOf(s_ConnectionEntities, s_SelectedConnectionEntity);
            if (selectedIdx == -1)
                selectedIdx = 0;
            selectedIdx = EditorGUILayout.Popup("Connection entity", selectedIdx, s_ConnectionIdLabels);
            if(selectedIdx < s_ConnectionEntities.Length)
                s_SelectedConnectionEntity = s_ConnectionEntities[selectedIdx];

            if (Application.isPlaying && !s_SelectedEntityGhostPosition)
            {
                EditorGUILayout.HelpBox("The selected connection entity has no GhostConnectionPosition component.", MessageType.Warning);
                return;
            }
            s_Mode = (DrawMode)EditorGUILayout.EnumPopup("Draw entity mode", s_Mode);

            if(Application.isPlaying && !s_HasGhostDistanceData)
                EditorGUILayout.HelpBox("The GhostDistanceData component is required for Grid visualization.", MessageType.Info);
            else
                s_DrawGrid = (DrawGridMode)EditorGUILayout.EnumPopup("Tile draw mode", s_DrawGrid);

            s_HeatmapGradient = EditorGUILayout.GradientField("Heatmap Gradient", s_HeatmapGradient);
            s_RenderDistance = EditorGUILayout.IntSlider("Render Distance", s_RenderDistance, 1, 100);
        }


        [RuntimeInitializeOnLoadMethod]
        [InitializeOnLoadMethod]
        static void InitializeAndLoad()
        {
            if (s_CustomDrawer == null)
                s_CustomDrawer = new DebugGhostDrawer.CustomDrawer("Importance Visualizer", 0, OnGuiDrawOptions, EditorSave);
            DebugGhostDrawer.RegisterDrawAction(s_CustomDrawer);
            EditorLoad();
        }

        static void EditorLoad()
        {
            ResetSelectedConnection();
            s_DrawGrid = (DrawGridMode)EditorPrefs.GetInt(k_DrawGridKey, (int)DrawGridMode.None);
            s_Mode = (DrawMode)EditorPrefs.GetInt(k_DrawModeKey, (int)DrawMode.PerEntityImportanceHeatmap);
            var gradJson = EditorPrefs.GetString(k_HeatmapGradientKey, null);
            try
            {
                s_HeatmapGradient = JsonUtility.FromJson<SerializableGradient>(gradJson).ToGradient();
            }
            catch
            {
                s_HeatmapGradient = SerializableGradient.DefaultGradient();
            }
            s_RenderDistance = EditorPrefs.GetInt(k_RenderDistanceKey, 5);
        }

        static void EditorSave()
        {
            var serialGrad = new SerializableGradient(s_HeatmapGradient);

            EditorPrefs.SetString(k_HeatmapGradientKey, JsonUtility.ToJson(serialGrad));
            EditorPrefs.SetInt(k_DrawGridKey, (int)s_DrawGrid);
            EditorPrefs.SetInt(k_DrawModeKey, (int)s_Mode);
            EditorPrefs.SetInt(k_RenderDistanceKey, s_RenderDistance);
        }

        static void UpdateConnectionList()
        {
            // Check if we need to update the connection list
            var connectionEntitiesCount = 0;
            foreach (var world in ClientServerBootstrap.ServerWorlds)
            {
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>());
                connectionEntitiesCount += query.CalculateEntityCount();
            }

            // If we have no new connections, return
            if (connectionEntitiesCount == s_ConnectionEntities.Length)
                return;

            if (connectionEntitiesCount == 0)
            {
                ResetSelectedConnection();
                return;
            }

            var labelIdPairs = new System.Collections.Generic.List<(string Label, Entity Id)>();
            foreach (var world in ClientServerBootstrap.ServerWorlds)
            {
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamConnection>());
                if (query.IsEmptyIgnoreFilter) continue;
                var connectionEntities = query.ToEntityArray(Allocator.Temp);
                var networkIds = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
                for (var i = 0; i < connectionEntities.Length; i++)
                {
                    var id = connectionEntities[i];
                    var name = world.EntityManager.GetName(id);
                    if (name.Length == 0)
                        name = $"Connection {i}";
                    labelIdPairs.Add((name, id));
                }

                connectionEntities.Dispose();
                networkIds.Dispose();
            }
            labelIdPairs.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.Ordinal));
            s_ConnectionIdLabels = labelIdPairs.ConvertAll(pair => pair.Label).ToArray();
            s_ConnectionEntities = labelIdPairs.ConvertAll(pair => pair.Id).ToArray();

            // Update the selected connection if none are selected and at least one is available
            if (s_SelectedConnectionEntity == default && labelIdPairs.Count > 0)
                s_SelectedConnectionEntity = labelIdPairs[0].Id;
        }

        void CreateDebugMeshObjectsIfNull()
        {
            if(m_GridMesh != null && m_LineMesh != null)
                return;

            // Grid mesh
            m_GridMesh = DrawerHelpers.CreateMesh(nameof(m_GridMesh));
            m_GridMesh.MarkDynamic();

            var gridGameObject = new GameObject(m_GridMesh.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            gridGameObject.AddComponent<MeshFilter>().mesh = m_GridMesh;;
            var gridMeshRenderer = gridGameObject.AddComponent<MeshRenderer>();
            DrawerHelpers.ConfigureMeshRenderer(ref gridMeshRenderer);
            gridMeshRenderer.material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(k_VertexColorShaderPath));
            gridMeshRenderer.material.renderQueue = (int)RenderQueue.Overlay + 20;

            // Line mesh
            m_LineMesh = DrawerHelpers.CreateMesh(nameof(m_LineMesh));
            m_LineMesh.MarkDynamic();

            var lineGameObject = new GameObject(m_LineMesh.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            lineGameObject.AddComponent<MeshFilter>().mesh = m_LineMesh;;
            var lineMeshRenderer = lineGameObject.AddComponent<MeshRenderer>();
            DrawerHelpers.ConfigureMeshRenderer(ref lineMeshRenderer);
            lineMeshRenderer.material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(k_VertexColorShaderZTestPath));
            lineMeshRenderer.material.renderQueue = (int)RenderQueue.Overlay + 21;

        }

        void ClearDebugMeshObjects()
        {
            if (m_GridMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(m_GridMesh);
                m_GridMesh = null;
            }
            if (m_LineMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(m_LineMesh);
                m_LineMesh = null;
            }
        }

        static void ResetSelectedConnection()
        {
            s_SelectedConnectionEntity = default;
            s_ConnectionEntities = Array.Empty<Entity>();
            s_ConnectionIdLabels = new[] { "No connections" };
        }

        [BurstCompile]
        protected override void OnCreate()
        {
            ResetSelectedConnection();
            m_GridVertices = new NativeList<DrawerHelpers.Vertex>(Allocator.Persistent);
            m_GridIndices = new NativeList<int>(Allocator.Persistent);
            m_LineVertices = new NativeList<DrawerHelpers.Vertex>(Allocator.Persistent);
            m_LineIndices = new NativeList<int>(Allocator.Persistent);
            m_PrioChunks = new NativeHashMap<ArchetypeChunk, PrioChunk>(32, Allocator.Persistent);
        }

        [BurstCompile]
        protected override void OnUpdate()
        {
            if(Application.isPlaying && !s_CustomDrawer.Enabled)
            {
                ClearDebugMeshObjects();
                return;
            }

            UpdateConnectionList();
            if (!s_CustomDrawer.Enabled || !Application.isPlaying || s_SelectedConnectionEntity == default)
                return;

            CreateDebugMeshObjectsIfNull();
            m_GridVertices.Clear();
            m_GridIndices.Clear();
            m_LineVertices.Clear();
            m_LineIndices.Clear();
            foreach (var serverWorld in ClientServerBootstrap.ServerWorlds)
            {
                TryDrawWorld(serverWorld, ref this.CheckedStateRef);
            }
            DrawerHelpers.UpdateMesh(ref m_GridMesh, ref m_GridVertices, ref m_GridIndices);
            DrawerHelpers.UpdateMesh(ref m_LineMesh, ref m_LineVertices, ref m_LineIndices);
        }

        protected override void OnStopRunning()
        {
            OnDestroy();
        }

        [BurstCompile]
        protected override void OnDestroy()
        {
            ResetSelectedConnection();
            if (m_GridVertices.IsCreated)
                m_GridVertices.Dispose();
            if (m_GridIndices.IsCreated)
                m_GridIndices.Dispose();
            if (m_PrioChunks.IsCreated)
                m_PrioChunks.Dispose();
            if (m_LineVertices.IsCreated)
                m_LineVertices.Dispose();
            if (m_LineIndices.IsCreated)
                m_LineIndices.Dispose();

            ClearDebugMeshObjects();
        }

        [BurstCompile]
        private void TryDrawWorld(World world, ref SystemState state)
        {
            if (!world.IsCreated) return;
            s_HasGhostImportanceSingleton = true;

            if (!TryGetSingletonCustom(world, out GhostImportance ghostImp) || ghostImp.GhostImportancePerChunkDataType.TypeIndex == default)
            {
                s_HasGhostImportanceSingleton = false;
                return;
            }

            if (!TryGetSingletonCustom(world, out GhostSendSystemData gshData))
                return;

            var scalingMultiplayer = gshData.ImportanceScalingMultiplier;

            GhostConnectionPosition gcp;
            try
            {
                s_SelectedEntityGhostPosition = true;
                gcp = world.EntityManager.GetComponentData<GhostConnectionPosition>(s_SelectedConnectionEntity);
            }
            catch (ArgumentException)
            {
                s_SelectedEntityGhostPosition = false;
                return;
            }

            if (!TryGetSingletonCustom(world, out GhostDistanceData tilingData))
            {
                s_HasGhostDistanceData = false;
                // If we don't have tilingData we generate a default one so that DrawPerEntityHeatmap as a TileSize reference for the render distance.
                tilingData = new GhostDistanceData() { TileSize = new int3(128, 128, 128) };
            }
            else
            {
                s_HasGhostDistanceData = true;
            }

            if (s_DrawGrid != DrawGridMode.None && s_HasGhostDistanceData)
            {
                DrawWorldGrid(scalingMultiplayer, gcp, tilingData);
            }

            switch (s_Mode)
            {
                case DrawMode.PerEntityImportanceHeatmap:
                case DrawMode.PerChunk:
                    DrawPerEntityHeatmap(world, ref state, scalingMultiplayer, gcp, tilingData.TileSize, ghostImp);
                    break;
                case DrawMode.None:
                    return;
                default: throw new ArgumentOutOfRangeException(nameof(s_Mode), s_Mode, nameof(GhostImportanceDrawerSystem));
            }
        }

        [BurstCompile]
        struct FetchPrioChunkJob : IJob
        {
            [ReadOnly] public UnsafeList<PrioChunk> NewPrioChunks;
            public NativeHashMap<ArchetypeChunk, PrioChunk> PrioChunks;

            [BurstCompile]
            public void Execute()
            {
                for (var i = 0; i < NewPrioChunks.Length; i++)
                {
                    PrioChunks.Remove(NewPrioChunks[i].chunk);
                    PrioChunks.Add(NewPrioChunks[i].chunk, NewPrioChunks[i]);
                }
            }
        }

        [BurstCompile]
        private void DrawPerEntityHeatmap(World world, ref SystemState state, ushort scalingMultiplayer, GhostConnectionPosition gcp, int3 tileSize, in GhostImportance ghostImp)
        {
            var ghostSendSystemHandle = world.GetExistingSystem<GhostSendSystem>();
            var ghostSendSystem = world.Unmanaged.GetUnsafeSystemRef<GhostSendSystem>(ghostSendSystemHandle);
            ConnectionStateData connectionStateData;
            JobHandle jobHandle;
            try
            {
                (jobHandle, connectionStateData) = ghostSendSystem.GetConnectionStateData(s_SelectedConnectionEntity);
            }
            catch (ArgumentException) // No connection or invalid connection
            {
                return;
            }

            var job = new FetchPrioChunkJob()
            {
                NewPrioChunks = connectionStateData.PrioChunks,
                PrioChunks = m_PrioChunks
            };

            // Use prioChunksSingleton.jobHandle as a dependency for scheduling
            var fetchPrioChunkJobHandle = job.Schedule(jobHandle);
            fetchPrioChunkJobHandle.Complete();

#if USING_ENTITIES_GRAPHICS
            using var ghostsQuery = world.EntityManager.CreateEntityQuery(typeof(RenderBounds), typeof(GhostInstance), typeof(LocalTransform), ghostImp.GhostImportancePerChunkDataType);
#else
            using var ghostsQuery = world.EntityManager.CreateEntityQuery(typeof(GhostInstance), typeof(LocalTransform), ghostImp.GhostImportancePerChunkDataType);
#endif
            if (ghostsQuery.IsEmptyIgnoreFilter) return;
            var ghostChunks = ghostsQuery.ToArchetypeChunkArray(Allocator.Temp);

            var minPriority = float.MaxValue;
            var maxPriority = float.MinValue;

            var prioChunksValues = m_PrioChunks.GetValueArray(Allocator.Temp);
            foreach (var prioChunk in prioChunksValues)
            {
                if (prioChunk.priority < minPriority) minPriority = prioChunk.priority;
                if (prioChunk.priority > maxPriority) maxPriority = prioChunk.priority;
            }

            minPriority /= scalingMultiplayer;
            maxPriority /= scalingMultiplayer;

            prioChunksValues.Dispose();

            var positionsHandle = state.GetComponentTypeHandle<LocalTransform>(true);

            state.CompleteDependency();
            foreach (var chunk in ghostChunks)
            {
                if (!m_PrioChunks.TryGetValue(chunk, out var prioChunk))
                    continue;
                if (prioChunk.chunk.Count == 0)
                    continue;

                var positions = chunk.GetNativeArray(ref positionsHandle);
                if (positions.Length == 0)
                    continue;

                // Don't draw chunks past render distance
                // +1 chunk for the PerEntityImportanceHeatmap so that all the grid drawn is filled with the PerEntityImportanceHeatmap
                var firstPos = positions[0].Position;
                var distance = math.abs(gcp.Position - firstPos);
                if (distance.x > (s_RenderDistance+1) * tileSize.x ||
                    distance.y > (s_RenderDistance+1) * tileSize.y ||
                    distance.z > (s_RenderDistance+1) * tileSize.z)
                    continue;

                var colors = new Color[positions.Length];

                switch (s_Mode)
                {
                    case DrawMode.PerEntityImportanceHeatmap:
                    {
                        // Color based on priority
                        var f = ((prioChunk.priority / (float)scalingMultiplayer) - minPriority) / (maxPriority - minPriority);
                        for (var i = 0; i < positions.Length; ++i)
                        {
                            colors[i] = s_HeatmapGradient.Evaluate(1f - math.clamp(f, 0f, 1f));
                        }

                        break;
                    }
                    case DrawMode.PerChunk:
                    {
                        // One random color per spatial chunk
                        var rand = Mathematics.Random.CreateFromIndex((uint)prioChunk.chunk.SequenceNumber);
                        var color = new Color(
                            rand.NextFloat(),
                            rand.NextFloat(),
                            rand.NextFloat(),
                            1f);

                        for (var i = 0; i < positions.Length; ++i)
                        {
                            colors[i] = color;
                        }

                        break;
                    }
                    default: throw new ArgumentOutOfRangeException(nameof(s_Mode), s_Mode, nameof(DrawPerEntityHeatmap));
                }

                // When we have just one ghost, draw a wire cross on it instead of a line to the first ghost position
                if (positions.Length == 1)
                {
#if USING_ENTITIES_GRAPHICS

                    var localToWorldHandle = world.EntityManager.GetComponentTypeHandle<LocalToWorld>(true);
                    var renderBoundsHandle = world.EntityManager.GetComponentTypeHandle<RenderBounds>(true);
                    var aabb = chunk.GetNativeArray(ref renderBoundsHandle)[0].Value;
                    var localToWorld = chunk.GetNativeArray(ref localToWorldHandle)[0];
                    DrawerHelpers.DrawWireCross(aabb.Min, aabb.Max, ref m_LineVertices, ref m_LineIndices, localToWorld, colors[0]);
#endif
                    continue;
                }

                for (int i =0; i < positions.Length; ++i)
                {
                    DrawerHelpers.DrawLine(positions[i].Position, firstPos, ref m_LineVertices, ref m_LineIndices, colors[i]);
                }
            }

            ghostChunks.Dispose();
        }

        [BurstCompile]
        private void DrawWorldGrid(ushort scalingMultiplayer, GhostConnectionPosition gcp, GhostDistanceData tilingData)
        {
            var ghostTileIndex = GhostDistancePartitioningSystem.CalculateTile(in tilingData, gcp.Position);
            float3 tileSize = tilingData.TileSize;
            var drawSize = tileSize - tilingData.TileBorderWidth;
            var numTilesPos = new int3(s_RenderDistance);
            var gridCenter = gcp.Position;
            var centerTileIdx = GhostDistancePartitioningSystem.CalculateTile(in tilingData, gridCenter);

            if (s_DrawGrid == DrawGridMode.XZ)
            {
                gridCenter.y = 0;

                for (var x = -numTilesPos.x+1 ; x <= numTilesPos.x+1; x++)
                for (var z = -numTilesPos.z+1; z <= numTilesPos.z+1; z++)
                {
                    var tilePos = ((centerTileIdx + new int3(x, 0, z)) * tileSize) + tilingData.TileCenter;
                    tilePos.y = 0; // Removes tilingData.TileCenter offset
                    tilePos -= new float3(tileSize.x, 0, tileSize.z) * 0.5f;
                    DrawSquare(tilePos, ref m_GridIndices, ref m_GridVertices, false);
                }
            }
            if (s_DrawGrid == DrawGridMode.XY)
            {
                gridCenter.z = 0;

                for (var x = -numTilesPos.x+1; x <= numTilesPos.x+1; x++)
                for (var y = -numTilesPos.y+1; y <= numTilesPos.y+1; y++)
                {
                    var tilePos = ((centerTileIdx + new int3(x, y, 0)) * tileSize) + tilingData.TileCenter;
                    tilePos.z = 0; // Removes tilingData.TileCenter offset
                    tilePos -= new float3(tileSize.x, tileSize.y, 0) * 0.5f;
                    DrawSquare(tilePos, ref m_GridIndices, ref m_GridVertices,true);
                }
            }

            void DrawSquare(float3 tilePos, ref NativeList<int> indices, ref NativeList<DrawerHelpers.Vertex> verts, bool xyPlane)
            {
                var chunkTile = new GhostDistancePartitionShared
                {
                    Index = GhostDistancePartitioningSystem.CalculateTile(in tilingData, tilePos),
                };
                var priority = GhostDistanceImportance.CalculateDefaultScaledPriority(scalingMultiplayer, chunkTile, ghostTileIndex) / (float)scalingMultiplayer;
                var color = s_HeatmapGradient.Evaluate(1f - priority);

                // Use the largest axis for square size
                float size = xyPlane ? math.max(drawSize.x, drawSize.y) : math.max(tileSize.x, drawSize.z);
                DrawerHelpers.DrawWireSquare(tilePos, size, ref indices, ref verts, xyPlane, color);
            }
        }

        [BurstCompile]
        static bool TryGetSingletonCustom<T>(World world, out T item) where T : unmanaged, IComponentData
        {
            using var query = world.EntityManager.CreateEntityQuery(typeof(T));
            query.CompleteDependency();
            try {
                return query.TryGetSingleton(out item);
            }
            catch (Exception)
            {
                item = default;
                return false;
            }
        }
    }
}
#endif

