using Unity.Entities;
using UnityEngine;
using System;
using System.Collections.Generic;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// Add the component to a ghost prefab to configure the presentation gameobject for the ghost.
    /// </summary>
    /// <remarks>
    /// If <see cref="ServerPrefab"/> or <see cref="ClientPrefab"/> are not null, the baking
    /// create a new additional entity with an <see cref="UnmanagedGhostPresentationGameObjectPrefab"/> component that contains the prefab references.
    /// It also add to the converted entity an <see cref="GhostPresentationGameObjectPrefabReference"/> that references the new created entity.
    /// It finally register itself has a producer of IRegisterPlayableData.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL(Authoring.HelpURLs.GhostPresentationGameObjectAuthoring)]
    [AddComponentMenu("Multiplayer/Ghost Presentation GameObject Authoring", 100)]
    public class GhostPresentationGameObjectAuthoring : MonoBehaviour
#if !UNITY_DISABLE_MANAGED_COMPONENTS
        , IRegisterPlayableData
#endif
    {
        /// <summary>
        /// The GameObject prefab which should be used as a visual representation of an entity on the server.
        /// See <see cref="UnmanagedGhostPresentationGameObjectPrefab"/> for further information.
        /// </summary>
        public GameObject ServerPrefab;
        /// <summary>
        /// The GameObject prefab which should be used as a visual representation of an entity on the client.
        /// See <see cref="UnmanagedGhostPresentationGameObjectPrefab"/> for further information.
        /// </summary>
        public GameObject ClientPrefab;
        private EntityManager regEntityManager;
        private Entity regEntity;

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        /// <summary>
        /// Implementation of <see cref="IRegisterPlayableData"/>. Should not be called directly. It is invoked as part
        /// of the GhostAnimationController initialization.
        /// </summary>
        /// <typeparam name="T">PlayableComponent type</typeparam>
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            regEntityManager.AddComponentData(regEntity, default(T));
        }
#endif
    }

    [BakingVersion("cmarastoni", 1)]
    class GhostPresentationGameObjectBaker : Baker<GhostPresentationGameObjectAuthoring>
#if !UNITY_DISABLE_MANAGED_COMPONENTS
        , IRegisterPlayableData
#endif
    {
        private HashSet<Type> m_AddedTypes;
        public override void Bake(GhostPresentationGameObjectAuthoring authoring)
        {
#if UNITY_DISABLE_MANAGED_COMPONENTS
            throw new System.InvalidOperationException("GhostPresentationGameObjects require managed components to be enabled");
#else
            bool isPrefab = !authoring.gameObject.scene.IsValid() || (GetComponent<GhostAuthoringComponent>()?.ForcePrefabConversion ?? false);

            var target = this.GetNetcodeTarget(isPrefab);

            var clientPrefab = (target == NetcodeConversionTarget.Server) ? null : authoring.ClientPrefab;
            var serverPrefab = (target == NetcodeConversionTarget.Client) ? null : authoring.ServerPrefab;
            if (serverPrefab == null && clientPrefab == null)
                return;
            var presPrefab = CreateAdditionalEntity(TransformUsageFlags.None);
            AddComponent(presPrefab, new UnmanagedGhostPresentationGameObjectPrefab
            {
                Client = clientPrefab,
                Server = serverPrefab
            });

            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GhostPresentationGameObjectPrefabReference{Prefab = presPrefab});

            // Register all the components needed for animation data
            m_AddedTypes = new HashSet<Type>();
            if (clientPrefab != null)
            {
                var anim = GetComponent<GhostAnimationController>(clientPrefab);
                if (anim != null && anim.AnimationGraphAsset != null)
                    anim.AnimationGraphAsset.RegisterPlayableData(this);
            }
            if (serverPrefab != null)
            {
                var anim = GetComponent<GhostAnimationController>(serverPrefab);
                if (anim != null && anim.AnimationGraphAsset != null)
                    anim.AnimationGraphAsset.RegisterPlayableData(this);
            }
#endif
        }

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            if (m_AddedTypes.Contains(typeof(T)))
                return;
            m_AddedTypes.Add(typeof(T));
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, default(T));
        }
#endif
    }
}
