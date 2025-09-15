#if UNITY_EDITOR
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
#if USING_ENTITIES_GRAPHICS
using Unity.Rendering;
#endif
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.NetCode.Samples.Common
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
#if USING_ENTITIES_GRAPHICS
    [UpdateAfter(typeof(UpdatePresentationSystemGroup))]
#endif
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [BurstCompile]
    partial class BoundingBoxDebugGhostDrawerClientSystem : SystemBase
    {
        const string k_ServerColorKey = "BoundingBoxDebugGhostDrawer_ServerColor";
        const string k_PredictedClientColorKey = "BoundingBoxDebugGhostDrawer_PredictedClientColor";
        const string k_InterpolatedClientColorKey = "BoundingBoxDebugGhostDrawer_InterpolatedClientColor";
        const string k_ServerGhostMarkerScaleKey = "BoundingBoxDebugGhostDrawer_ServerGhostMarkerScale";

        public static Color ServerColor = Color.red;
        public static Color PredictedClientColor = Color.green;
        public static Color InterpolatedClientColor = Color.cyan;
        public static float GhostServerMarkerScale = 500;

        static DebugGhostDrawer.CustomDrawer s_CustomDrawer;

        static ProfilerMarker s_CreateGeometryJobMarker = new(nameof(s_CreateGeometryJobMarker));
        static ProfilerMarker s_SetMeshesMarker = new(nameof(s_SetMeshesMarker));
        static ProfilerMarker s_GatherDataMarker = new(nameof(s_GatherDataMarker));
        static GUIContent s_ServerGhostMarkerScale = new GUIContent("Marker Scale", "Some server entities may not be replicated on the client.\n\nThis option draws a 3D '+' marker over all server ghosts, which will allow you to see any not-yet-replicated ghosts in the Game view.\n\n0 disables marker rendering.");
        static readonly VertexAttributeDescriptor[] k_VertexAttributeDescriptors = {new VertexAttributeDescriptor(VertexAttribute.Position)};

        Material m_ServerMat;
        Material m_PredictedClientMat;
        Material m_InterpolatedClientMat;
        Mesh m_ServerMesh;
        Mesh m_PredictedClientMesh;
        Mesh m_InterpolatedClientMesh;
        Entity m_ServerMeshRendererEntity;
        Entity m_ClientPredictedMeshRendererEntity;
        Entity m_ClientInterpolatedMeshRendererEntity;
        MeshRenderer m_ClientInterpolatedMeshRenderer;
        MeshRenderer m_ClientPredictedMeshRenderer;
        MeshRenderer m_ServerMeshRenderer;
        EntityQuery m_InterpolatedGhostQuery;
        EntityQuery m_PredictedGhostQuery;

        [RuntimeInitializeOnLoadMethod]
        [InitializeOnLoadMethod]
        static void InitializeAndLoad()
        {
            s_CustomDrawer = new DebugGhostDrawer.CustomDrawer("Bounding Boxes", 0, OnGuiDrawOptions, EditorSave);
            DebugGhostDrawer.RegisterDrawAction(s_CustomDrawer);

            EditorLoad();
        }

        static void EditorLoad()
        {
            ServerColor = ColorUtility.TryParseHtmlString(EditorPrefs.GetString(k_ServerColorKey, null), out var serverColor) ? serverColor : ServerColor;
            PredictedClientColor = ColorUtility.TryParseHtmlString(EditorPrefs.GetString(k_PredictedClientColorKey, null), out var predictedClientColor) ? predictedClientColor : PredictedClientColor;
            InterpolatedClientColor = ColorUtility.TryParseHtmlString(EditorPrefs.GetString(k_InterpolatedClientColorKey, null), out var interpolatedClientColor) ? interpolatedClientColor : InterpolatedClientColor;
            GhostServerMarkerScale = EditorPrefs.GetFloat(k_ServerGhostMarkerScaleKey, 1f);
        }

        public static void EditorSave()
        {
            EditorPrefs.SetString(k_ServerColorKey, "#" + ColorUtility.ToHtmlStringRGBA(ServerColor));
            EditorPrefs.SetString(k_PredictedClientColorKey, "#" + ColorUtility.ToHtmlStringRGBA(PredictedClientColor));
            EditorPrefs.SetString(k_InterpolatedClientColorKey, "#" + ColorUtility.ToHtmlStringRGBA(InterpolatedClientColor));
            EditorPrefs.SetFloat(k_ServerGhostMarkerScaleKey, GhostServerMarkerScale);
        }

#if USING_ENTITIES_GRAPHICS
        static void UpdateEntityMeshDrawer(EntityManager clientEntityManager, bool enabled, Entity renderEntity, Material material, Color color)
        {
            if (!clientEntityManager.Exists(renderEntity)) return;

            material.color = color;

            var shouldBeVisible = enabled && color.a > 0f;
            var isVisible = !clientEntityManager.HasComponent<DisableRendering>(renderEntity);
            if (shouldBeVisible != isVisible)
            {
                if (shouldBeVisible)
                    clientEntityManager.RemoveComponent<DisableRendering>(renderEntity);
                else
                    clientEntityManager.AddComponent<DisableRendering>(renderEntity);
            }
        }
#endif
        static void UpdateGameObjectMeshDrawer(bool enabled, MeshRenderer renderer, Material material, Color color)
        {
            if (renderer == null) return;

            renderer.enabled = enabled && color.a > 0f;
            material.color = color;
        }

        /// <summary>
        ///     Note that this shader must exist in the build. Add it to the 'Always Included Shaders' list in your project.
        ///     TODO - Make this feature available in builds after the URP/Unlit DOTS_INSTANCING_ON 4.5 error lands.
        /// </summary>
        static string GetUnlitShader()
        {
#if USING_URP
            return "Universal Render Pipeline/Unlit";
#elif USING_HDRP
            return "HDRP/Unlit";
#else
            return "Unlit/Color";
#endif
        }

        static void OnGuiDrawOptions()
        {
            PredictedClientColor = EditorGUILayout.ColorField("Client (Predicted)", PredictedClientColor);
            InterpolatedClientColor = EditorGUILayout.ColorField("Client (Interpolated)", InterpolatedClientColor);
            ServerColor = EditorGUILayout.ColorField("Server", ServerColor);
            GhostServerMarkerScale = EditorGUILayout.Slider(s_ServerGhostMarkerScale, GhostServerMarkerScale, 0, 100);
            EditorGUILayout.HelpBox("Note that `BoundingBoxDebugGhostDrawerSystem` will only draw entities on client ghosts with a `WorldRenderBounds` component or on GameObjects with a GhostDebugMeshBounds, and on server ghost entities with a `LocalToWorld` component.", MessageType.Info);
        }

        static void UpdateIndividualMeshOptimized(Mesh mesh, ref NativeList<float3> newVerts, ref NativeList<int> newIndices)
        {
            const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
            using (s_SetMeshesMarker.Auto())
            {
                if (mesh.vertexCount < newVerts.Length)
                    mesh.SetVertexBufferParams(DrawerHelpers.RoundTo(newVerts.Length, 2048), k_VertexAttributeDescriptors);
                mesh.SetVertexBufferData<float3>(newVerts.AsArray(), 0, 0, newVerts.Length, 0, flags);

                if (mesh.GetIndexCount(0) < newIndices.Length)
                    mesh.SetIndexBufferParams(DrawerHelpers.RoundTo(newIndices.Length, 8192), IndexFormat.UInt32);
                mesh.SetIndexBufferData<int>(newIndices.AsArray(), 0, 0, newIndices.Length, flags);

                var smd = new SubMeshDescriptor
                {
                    topology = MeshTopology.Lines,
                    vertexCount = newVerts.Length,
                    indexCount = newIndices.Length,
                };
                mesh.SetSubMesh(0, smd, flags);


                mesh.UploadMeshData(false);
            }
        }

        protected override void OnStopRunning()
        {
            OnDestroy();
        }

        protected override void OnDestroy()
        {
#if USING_ENTITIES_GRAPHICS
            UpdateEntityMeshDrawer(EntityManager, false, m_ServerMeshRendererEntity, m_ServerMat, ServerColor);
            UpdateEntityMeshDrawer(EntityManager, false, m_ClientPredictedMeshRendererEntity, m_PredictedClientMat, PredictedClientColor);
            UpdateEntityMeshDrawer(EntityManager, false, m_ClientInterpolatedMeshRendererEntity, m_InterpolatedClientMat, InterpolatedClientColor);
#endif
            UpdateGameObjectMeshDrawer(false, m_ServerMeshRenderer, m_ServerMat, ServerColor);
            UpdateGameObjectMeshDrawer(false, m_ClientPredictedMeshRenderer, m_PredictedClientMat, PredictedClientColor);
            UpdateGameObjectMeshDrawer(false, m_ClientInterpolatedMeshRenderer, m_InterpolatedClientMat, InterpolatedClientColor);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PredictedGhostQuery = SystemAPI.QueryBuilder()
                .WithAllRW<PredictedGhost>()
                .WithAllRW<GhostDebugMeshBounds>()
                .WithAllRW<GhostInstance>()
                .WithAllRW<LocalToWorld>().Build();
            m_InterpolatedGhostQuery = SystemAPI.QueryBuilder()
                .WithNone<PredictedGhost>()
                .WithAllRW<GhostDebugMeshBounds>()
                .WithAllRW<GhostInstance>()
                .WithAllRW<LocalToWorld>().Build();
        }

        [BurstCompile]
        struct CreateGeometryJob : IJob
        {
            [ReadOnly]
            public NativeArray<LocalToWorld> ServerL2Ws;

            public NativeList<int> ServerIndices;
            public NativeList<DrawerHelpers.Vertex> ServerVertices;
            public float Scale;

            public void Execute()
            {
                var x = new float3(Scale, 0, 0);
                var y = new float3(0, Scale, 0);
                var z = new float3(0, 0, Scale);
                for (var i = 0; i < ServerL2Ws.Length; i++)
                {
                    var pos = ServerL2Ws[i].Position;
                    DrawerHelpers.DrawLine(pos - x, pos + x, ref ServerVertices, ref ServerIndices);
                    DrawerHelpers.DrawLine(pos - y, pos + y, ref ServerVertices, ref ServerIndices);
                    DrawerHelpers.DrawLine(pos - z, pos + z, ref ServerVertices, ref ServerIndices);
                }
            }
        }

        protected override void OnUpdate()
        {
            var enabled = Enabled && s_CustomDrawer.Enabled && DebugGhostDrawer.HasRequiredWorlds;
#if USING_ENTITIES_GRAPHICS
            UpdateEntityMeshDrawer(EntityManager, enabled, m_ServerMeshRendererEntity, m_ServerMat, ServerColor);
            UpdateEntityMeshDrawer(EntityManager, enabled, m_ClientPredictedMeshRendererEntity, m_PredictedClientMat, PredictedClientColor);
            UpdateEntityMeshDrawer(EntityManager, enabled, m_ClientInterpolatedMeshRendererEntity, m_InterpolatedClientMat, InterpolatedClientColor);
#endif
            UpdateGameObjectMeshDrawer(enabled, m_ServerMeshRenderer, m_ServerMat, ServerColor);
            UpdateGameObjectMeshDrawer(enabled, m_ClientPredictedMeshRenderer, m_PredictedClientMat, PredictedClientColor);
            UpdateGameObjectMeshDrawer(enabled, m_ClientInterpolatedMeshRenderer, m_InterpolatedClientMat, InterpolatedClientColor);
            if (!enabled) return;
#if USING_ENTITIES_GRAPHICS
            CreateRenderEntitiesIfNull();
#endif
            CreateRenderGameObjectIfNull();
            s_GatherDataMarker.Begin();

            Dependency.Complete();

            var serverWorld = ClientServerBootstrap.ServerWorld;
            serverWorld.EntityManager.CompleteAllTrackedJobs();

            var serverSystem = serverWorld.GetOrCreateSystemManaged<BoundingBoxDebugGhostDrawerServerSystem>();

            var numInterpolatedEntities = m_InterpolatedGhostQuery.CalculateEntityCount();
            var numPredictedEntities = m_PredictedGhostQuery.CalculateEntityCount();
            var numEntitiesToIterate = numPredictedEntities + numInterpolatedEntities;

            serverSystem.SpawnedGhostEntityMapSingletonQuery.CompleteDependency();
            var serverSpawnedGhostEntityMap = serverSystem.SpawnedGhostEntityMapSingletonQuery.GetSingleton<SpawnedGhostEntityMap>().Value;

            var serverVertices = new NativeList<DrawerHelpers.Vertex>(numEntitiesToIterate * 10, Allocator.TempJob);
            var serverIndices = new NativeList<int>(numEntitiesToIterate * 26, Allocator.TempJob);

            s_GatherDataMarker.End();

#if USING_ENTITIES_GRAPHICS
            var missingDebugMeshBoundsQuery = SystemAPI.QueryBuilder()
                .WithNone<GhostDebugMeshBounds>()
                .WithAll<GhostInstance, LocalToWorld, RenderBounds>().Build();

            // GameObject side, this needs to be done at the GO's Initialization. You can use GhostDebugMeshBounds's Initialize
            if (missingDebugMeshBoundsQuery.CalculateEntityCount() > 0)
            {
                // Add a GhostDebugMeshBounds component to all predicted ghosts that don't have one.
                // This way, we don't have our size changing as the bounds are moving around. We rely on rotation to tell us the real rotation
                var entities = missingDebugMeshBoundsQuery.ToEntityArray(Allocator.Temp);
                var renderBounds = missingDebugMeshBoundsQuery.ToComponentDataArray<RenderBounds>(Allocator.Temp);
                for (var i = 0; i < entities.Length; i++)
                {
                    var en = entities[i];
                    var bounds = renderBounds[i];
                    if (math.lengthsq(bounds.Value.Extents) > 0.001f)
                    {
                        Bounds b = new Bounds(bounds.Value.Center, bounds.Value.Extents * 2);
                        EntityManager.AddComponentData(en, new GhostDebugMeshBounds { GlobalBounds = b });
                    }
                }
            }
#endif
            // doing the lookup update after the above setup to avoid structural changes errors when spawning new ghosts
            var serverLocalToWorldMap = serverSystem.LocalToWorldsMapR0;
            serverLocalToWorldMap.Update(serverSystem);

            if (numPredictedEntities > 0)
            {
                var predictedClientVertices = new NativeList<DrawerHelpers.Vertex>(numPredictedEntities * 10, Allocator.Temp);
                var predictedClientIndices = new NativeList<int>(numPredictedEntities * 26, Allocator.Temp);

                using (s_CreateGeometryJobMarker.Auto())
                {
                    foreach (var (debugHelper, ghostComponent, transform) in SystemAPI.Query<
                                     RefRO<GhostDebugMeshBounds>, RefRO<GhostInstance>, RefRO<LocalToWorld>>()
                                 .WithAll<PredictedGhost>())
                    {
                        AABB aabb = new AABB
                            { Center = debugHelper.ValueRO.GlobalBounds.center, Extents = debugHelper.ValueRO.GlobalBounds.extents };
                        CreateLineGeometryWithGhosts(in aabb, in transform.ValueRO, in ghostComponent.ValueRO,
                            in serverSpawnedGhostEntityMap, in serverLocalToWorldMap, ref predictedClientVertices,
                            ref predictedClientIndices, ref serverVertices, ref serverIndices);
                    }
                }

                DrawerHelpers.UpdateMesh(ref m_PredictedClientMesh, ref predictedClientVertices, ref predictedClientIndices);

                predictedClientVertices.Dispose();
                predictedClientIndices.Dispose();
            }
            else m_PredictedClientMesh.Clear(true);

            if (numInterpolatedEntities > 0)
            {
                var interpolatedClientVertices = new NativeList<DrawerHelpers.Vertex>(numInterpolatedEntities * 10, Allocator.Temp);
                var interpolatedClientIndices = new NativeList<int>(numInterpolatedEntities * 26, Allocator.Temp);

                using (s_CreateGeometryJobMarker.Auto())
                {
                    foreach (var (debugHelper, ghostComponent, transform) in
                             SystemAPI.Query<RefRO<GhostDebugMeshBounds>, RefRO<GhostInstance>, RefRO<LocalToWorld>>()
                                 .WithNone<PredictedGhost>())
                    {
                        var aabb = new AABB
                        {
                            Center = debugHelper.ValueRO.GlobalBounds.center,
                            Extents = debugHelper.ValueRO.GlobalBounds.extents
                        };
                        CreateLineGeometryWithGhosts(in aabb, in transform.ValueRO, in ghostComponent.ValueRO,
                            in serverSpawnedGhostEntityMap, in serverLocalToWorldMap,
                            ref interpolatedClientVertices, ref interpolatedClientIndices,
                            ref serverVertices, ref serverIndices);
                    }
                }

                DrawerHelpers.UpdateMesh(ref m_InterpolatedClientMesh, ref interpolatedClientVertices, ref interpolatedClientIndices);

                interpolatedClientVertices.Dispose();
                interpolatedClientIndices.Dispose();
            }
            else m_InterpolatedClientMesh.Clear(true);

            // For all server entities, also draw a cross.
            if (GhostServerMarkerScale > 0)
            {
                using (s_CreateGeometryJobMarker.Auto())
                {
                    var serverL2Ws = serverSystem.GhostL2WQuery.ToComponentDataArray<LocalToWorld>(Allocator.TempJob);
                    var scale = GhostServerMarkerScale * .5f;

                    new CreateGeometryJob()
                    {
                        Scale = scale,
                        ServerVertices = serverVertices,
                        ServerIndices = serverIndices,
                        ServerL2Ws = serverL2Ws
                    }.Run();
                    serverL2Ws.Dispose();
                }
            }

            DrawerHelpers.UpdateMesh(ref m_ServerMesh, ref serverVertices, ref serverIndices);

            serverVertices.Dispose();
            serverIndices.Dispose();
        }

        [BurstCompile]
        static void CreateLineGeometryWithGhosts(in AABB aabb, in LocalToWorld ghostL2Wtransform, in GhostInstance ghostInstance, in NativeParallelHashMap<SpawnedGhost, Entity>.ReadOnly serverSpawnedGhostEntityMap,
            in ComponentLookup<LocalToWorld> serverLocalToWorldMap, ref NativeList<DrawerHelpers.Vertex> clientVertices, ref NativeList<int> clientIndices, ref NativeList<DrawerHelpers.Vertex> serverVertices, ref NativeList<int> serverIndices)
        {
            // Client AABB:
            DrawerHelpers.DrawWireCube(aabb.Min, aabb.Max, ref clientVertices, ref clientIndices, ghostL2Wtransform);

            if (serverSpawnedGhostEntityMap.TryGetValue(ghostInstance, out var serverEntity) && serverLocalToWorldMap.TryGetComponent(serverEntity, out var serverL2W))
            {
                var serverPos = serverL2W.Position;
                var serverRot = serverL2W.Rotation;
                var angleDiff = 2 * math.acos(math.dot(serverRot, ghostL2Wtransform.Rotation)); // radians
                if (math.distancesq(aabb.Center, serverPos) > 0.002f || angleDiff > 0.002f)
                {
                    // Server to Client Line:
                    DrawerHelpers.DrawLine(ghostL2Wtransform.Position, serverPos, ref serverVertices, ref serverIndices);

                    // Server AABB:
                    DrawerHelpers.DrawWireCube(aabb.Min, aabb.Max, ref serverVertices, ref serverIndices, serverL2W);
                }
            }
        }

        void SetupMeshAndMaterials()
        {
            m_ServerMesh = DrawerHelpers.CreateMesh(nameof(m_ServerMesh));
            m_InterpolatedClientMesh = DrawerHelpers.CreateMesh(nameof(m_InterpolatedClientMesh));
            m_PredictedClientMesh = DrawerHelpers.CreateMesh(nameof(m_PredictedClientMesh));

            var unlitShaderName = GetUnlitShader();
            var unlitShader = Shader.Find(unlitShaderName);
            if (unlitShader == null)
            {
                unlitShader = QualitySettings.renderPipeline?.defaultMaterial?.shader;
                Debug.LogError($"{nameof(BoundingBoxDebugGhostDrawerClientSystem)}.{nameof(GetUnlitShader)} was unable to find shader '{unlitShaderName}' for this Render Pipeline. Please ensure it's added to the 'Always Included Shaders' list in your project. Trying to use this RP's default material '{unlitShader}' (assuming it exists).");
                if (unlitShader == null)
                {
                    Enabled = false;
                    return;
                }
            }

            // Draw client boxes on top of server boxes.
            m_ServerMat = new Material(unlitShader)
            {
                name = m_ServerMesh.name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_PredictedClientMat = new Material(unlitShader)
            {
                name = m_ServerMesh.name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_InterpolatedClientMat = new Material(unlitShader)
            {
                name = m_ServerMesh.name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_ServerMat.renderQueue = (int)RenderQueue.Overlay + 10;
            m_InterpolatedClientMat.renderQueue = (int)RenderQueue.Overlay + 11;
            m_PredictedClientMat.renderQueue = (int)RenderQueue.Overlay + 12;
        }

        void CreateRenderGameObjectIfNull()
        {
            if (m_ClientInterpolatedMeshRenderer != null) return;

            SetupMeshAndMaterials();

            GameObject serverGo = new GameObject(m_ServerMesh.name)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var serverMeshFilter = serverGo.AddComponent<MeshFilter>();
            m_ServerMeshRenderer = serverGo.AddComponent<MeshRenderer>();
            m_ServerMeshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            m_ServerMeshRenderer.receiveShadows = false;
            m_ServerMeshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            GameObject clientInterpolatedGO = GameObject.Instantiate(serverGo);
            GameObject clientPredictedGO = GameObject.Instantiate(serverGo);

            serverMeshFilter.mesh = m_ServerMesh;
            m_ServerMeshRenderer.material = m_ServerMat;

            clientInterpolatedGO.GetComponent<MeshFilter>().mesh = m_InterpolatedClientMesh;
            m_ClientInterpolatedMeshRenderer = clientInterpolatedGO.GetComponent<MeshRenderer>();
            m_ClientInterpolatedMeshRenderer.material = m_InterpolatedClientMat;
            clientPredictedGO.GetComponent<MeshFilter>().mesh = m_PredictedClientMesh;
            m_ClientPredictedMeshRenderer = clientPredictedGO.GetComponent<MeshRenderer>();
            m_ClientPredictedMeshRenderer.material = m_PredictedClientMat;

            serverGo.name = m_ServerMesh.name;
            clientInterpolatedGO.name = m_InterpolatedClientMesh.name;
            clientPredictedGO.name = m_PredictedClientMesh.name;
        }

#if USING_ENTITIES_GRAPHICS
        void CreateRenderEntitiesIfNull()
        {
            if (EntityManager.Exists(m_ClientInterpolatedMeshRendererEntity)) return;

            SetupMeshAndMaterials();

            m_ServerMeshRendererEntity = EntityManager.CreateEntity(ComponentType.ReadOnly<LocalToWorld>());
            EntityManager.SetComponentData(m_ServerMeshRendererEntity, new LocalToWorld
            {
                Value = float4x4.TRS(new float3(0, 0, 0), quaternion.identity, new float3(1))
            });

            // See runtime-entity-creation.md for details on how this works.
            // Note that the Entities Graphics package doesn't currently support an overload for AddComponents that DOESN'T require a custom RenderMeshArray.
            // https://jira.unity3d.com/browse/PLAT-1272
            // Ideally we'd register these materials + meshes into BatchRenderGroup and therefore not need a RenderMeshArray SharedComponent.
            var materials = new[] {m_ServerMat, m_PredictedClientMat, m_InterpolatedClientMat};
            var meshes = new[] {m_ServerMesh, m_PredictedClientMesh, m_InterpolatedClientMesh};
            RenderMeshUtility.AddComponents(m_ServerMeshRendererEntity, EntityManager,
                new RenderMeshDescription(ShadowCastingMode.Off, false, MotionVectorGenerationMode.ForceNoMotion),
                new RenderMeshArray(materials, meshes),
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            m_ClientPredictedMeshRendererEntity = EntityManager.Instantiate(m_ServerMeshRendererEntity);
            EntityManager.SetComponentData(m_ClientPredictedMeshRendererEntity, MaterialMeshInfo.FromRenderMeshArrayIndices(1, 1));

            m_ClientInterpolatedMeshRendererEntity = EntityManager.Instantiate(m_ServerMeshRendererEntity);
            EntityManager.SetComponentData(m_ClientInterpolatedMeshRendererEntity, MaterialMeshInfo.FromRenderMeshArrayIndices(2, 2));

            EntityManager.SetName(m_ServerMeshRendererEntity, m_ServerMesh.name);
            EntityManager.SetName(m_ClientPredictedMeshRendererEntity, m_PredictedClientMesh.name);
            EntityManager.SetName(m_ClientInterpolatedMeshRendererEntity, m_InterpolatedClientMesh.name);
        }
#endif

    }

    // TODO - Exposing APIs on systems is an anti-pattern, but there is no clear alternative for 'world to world' communication.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    partial class BoundingBoxDebugGhostDrawerServerSystem : SystemBase
    {
        internal ComponentLookup<LocalToWorld> LocalToWorldsMapR0;
        internal EntityQuery SpawnedGhostEntityMapSingletonQuery;
        public EntityQuery GhostL2WQuery;

        protected override void OnCreate()
        {
            LocalToWorldsMapR0 = GetComponentLookup<LocalToWorld>(true);
            SpawnedGhostEntityMapSingletonQuery = GetEntityQuery(ComponentType.ReadOnly<SpawnedGhostEntityMap>());
            GhostL2WQuery = GetEntityQuery(ComponentType.ReadOnly<GhostInstance>(), ComponentType.ReadOnly<LocalToWorld>());
            Enabled = false;
        }

        protected override void OnUpdate() => throw new InvalidOperationException();
    }
}
#endif
