using System;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Used to store a reference to the original prefab the ghost has been created from at editor time. An AssetPostProcessor is in charge of
    /// creating instances of that ScriptableObject and parent it to the prefab. You can see "child" objects in the project view for each of the networked prefabs, it's this
    /// ScriptableObject.
    /// There is a another options, that is to collect them during scene-post processing and store in registry
    /// scriptable instead.
    /// The GhostObject then can store the GUID as usual. That second approach, also provide a natural hook to register
    /// these prefabs on the fly when the scene is loaded.
    /// This works hand in hand with the <see cref="PrefabsRegistry"/>
    /// </summary>
    // A lot of this will be updated with entities integration. This is still TBD.
    internal class GhostPrefabReference : ScriptableObject
    {
        /// <summary>
        /// The reference to the GameObject at the root of the prefab
        /// </summary>
        /// <remarks>
        /// For some reason the references completely break if this references the <see cref="GhostObject"/> rather than the GameObject.
        /// Even having the GhostObject alongside the GameObject broke many things.
        /// Don't put the GhostObject here!
        /// </remarks>
        [SerializeField] public GameObject Prefab;
        /// <summary>
        /// Cache the setting from the GhostObject here so that we don't need to do an expensive GetComponent to find the setting.
        /// </summary>
        [SerializeField] public bool SkipAutomaticPrefabRegistration;

        // TODO-next@prefabRegistration what if the object is never referenced in the scene, so never loaded? We need a way to set "this list of prefabs is still loaded"
        void OnEnable()
        {
            // TODO-next@prefabRegistration this auto registration might fail if OnEnable doesn't happen in the same order?
            // TODO-next@prefabRegistration check how NGO does auto prefab registration https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/blob/develop/com.unity.netcode.gameobjects/Editor/Configuration/NetworkPrefabProcessor.cs
            // TODO-next@prefabRegistration this is already handled in a PR coming further down the line. Keeping as is right now, tests are passing. But this is most likely flaky if you try to use this on your own in different ways
            // TODO-next@prefabRegistration could call automatic prefab registration only after NetworkStreamInGame is set. Should check this once we handle scene switching and networkStreamInGame.
            if (Application.isPlaying && !s_BlockOnAutoRegistration && !SkipAutomaticPrefabRegistration)
            {
                Netcode.RegisterPrefab(Prefab);
            }
        }

        internal static bool s_BlockOnAutoRegistration;

        internal static void CreateForPrefab(GhostObject prefab)
        {
            GhostPrefabReference prefabReference;
            try
            {
                s_BlockOnAutoRegistration = true;
                prefabReference = CreateInstance<GhostPrefabReference>();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                s_BlockOnAutoRegistration = false;
                return;
            }
            s_BlockOnAutoRegistration = false;

            prefabReference.name = "GhostPrefabReference";
            prefabReference.Prefab = prefab.gameObject;
            prefabReference.SkipAutomaticPrefabRegistration = prefab.SkipAutomaticPrefabRegistration;

            prefab.prefabReference = prefabReference;
        }
    }

#if UNITY_EDITOR
    internal class GhostPrefabPostProcessor : UnityEditor.AssetPostprocessor
    {
        public override uint GetVersion()
        {
            return 8; // NOTE: bump this whenever we're changing the code below
        }

        void OnPostprocessPrefab(GameObject gameObject)
        {
            var ghost = gameObject.GetComponent<GhostObject>();
            if (ghost == null)
                return;

            GhostPrefabReference.CreateForPrefab(ghost);
            context.AddObjectToAsset("GhostPrefabReference", ghost.prefabReference);
        }
    }
#endif // UNITY_EDITOR
}
