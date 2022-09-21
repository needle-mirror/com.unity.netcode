using Unity.Entities;
using UnityEngine;

namespace Unity.NetCode.Hybrid
{
    /// <summary>
    /// Add the component to a ghost prefab to configure the presentation gameobject for the ghost.
    /// </summary>
    /// <remarks>
    /// If <see cref="ServerPrefab"/> or <see cref="ClientPrefab"/> are not null, the baking
    /// create a new additional entity with <see cref="GhostPresentationGameObjectPrefab"/> managed component that contains the prefab references.
    /// It also add to the converted entity an <see cref="GhostPresentationGameObjectPrefabReference"/> that references the new created entity.
    /// It finally register itself has a producer of IRegisterPlayableData.
    /// </remarks>
    public class GhostPresentationGameObjectAuthoring : MonoBehaviour
#if !UNITY_DISABLE_MANAGED_COMPONENTS
        , IRegisterPlayableData
#endif
    {
        /// <summary>
        /// The GameObject prefab which should be used as a visual representation of an entity on the server.
        /// <seealso cref="GhostPresentationGameObjectPrefab"/> for further information.
        /// </summary>
        public GameObject ServerPrefab;
        /// <summary>
        /// The GameObject prefab which should be used as a visual representation of an entity on the client.
        /// <seealso cref="GhostPresentationGameObjectPrefab"/> for further information.
        /// </summary>
        public GameObject ClientPrefab;
        private EntityManager regEntityManager;
        private Entity regEntity;

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        /// <summary>
        /// Implementation of <see cref="IRegisterPlayableData"/>. Should not be called directly. It is invoked as part
        /// of the GhostAnimationController initialization.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            regEntityManager.AddComponentData(regEntity, default(T));
        }
#endif
    }

    class GhostPresentationGameObjectBaker : Baker<GhostPresentationGameObjectAuthoring>
#if !UNITY_DISABLE_MANAGED_COMPONENTS
        , IRegisterPlayableData
#endif
    {
        public override void Bake(GhostPresentationGameObjectAuthoring authoring)
        {
#if UNITY_DISABLE_MANAGED_COMPONENTS
            throw new System.InvalidOperationException("GhostPresentationGameObjects require managed components to be enabled");
#else
            bool isPrefab = !authoring.gameObject.scene.IsValid() || (GetComponent<GhostAuthoringComponent>()?.ForcePrefabConversion ?? false);

            var target = this.GetNetcodeTarget(isPrefab);

            var prefabComponent = new GhostPresentationGameObjectPrefab
            {
                Client = (target == NetcodeConversionTarget.Server) ? null : authoring.ClientPrefab,
                Server = (target == NetcodeConversionTarget.Client) ? null : authoring.ServerPrefab
            };
            if (prefabComponent.Server == null && prefabComponent.Client == null)
                return;
            var presPrefab = CreateAdditionalEntity();
            AddComponentObject(presPrefab, prefabComponent);

            AddComponent(new GhostPresentationGameObjectPrefabReference{Prefab = presPrefab});

            // Register all the components needed for animation data
            if (prefabComponent.Client != null)
            {
                var anim = GetComponent<GhostAnimationController>(prefabComponent.Client);
                if (anim != null && anim.AnimationGraphAsset != null)
                    anim.AnimationGraphAsset.RegisterPlayableData(this);
            }
            if (prefabComponent.Server != null)
            {
                var anim = GetComponent<GhostAnimationController>(prefabComponent.Server);
                if (anim != null && anim.AnimationGraphAsset != null)
                    anim.AnimationGraphAsset.RegisterPlayableData(this);
            }
#endif
        }

#if !UNITY_DISABLE_MANAGED_COMPONENTS
        public void RegisterPlayableData<T>() where T: unmanaged, IComponentData
        {
            AddComponent(default(T));
        }
#endif
    }
}
