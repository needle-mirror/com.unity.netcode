
#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID

using System;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Used to store a reference to the original prefab the ghost has been created from at editor time. An AssetPostProcessor is in charge of
    /// creating instances of that ScriptableObject and parent it to the prefab. You can see "child" objects in the project view for each of the networked prefabs, it's this
    /// ScriptableObject.
    /// There is a another options, that is to collect them during scene-post processing and store in registry
    /// scriptable instead.
    /// The GhostAdapter then can store the GUID as usual. That second approach, also provide a natural hook to register
    /// these prefabs on the fly when the scene is loaded.
    /// This works hand in hand with the <see cref="PrefabsRegistry"/>
    /// </summary>
    // A lot of this will be updated with entities integration. This is still TBD.
    internal class GhostPrefabReference : ScriptableObject
    {
        [SerializeField] public UnityEngine.GameObject Prefab;
        [SerializeField] public GhostAdapter Ghost;

        // TODO-next what if the object is never referenced in the scene, so never loaded? We need a way to set "this list of prefabs is still loaded"
        void OnEnable()
        {
            // TODO-next this auto registration might fail if OnEnable doesn't happen in the same order?
            // TODO-next check how NGO does auto prefab registration https://github.com/Unity-Technologies/com.unity.netcode.gameobjects/blob/develop/com.unity.netcode.gameobjects/Editor/Configuration/NetworkPrefabProcessor.cs
            // TODO-next this is already handled in a PR coming further down the line. Keeping as is right now, tests are passing. But this is most likely flaky if you try to use this on your own in different ways
            // TODO-next could call automatic prefab registration only after NetworkStreamInGame is set. Should check this once we handle scene switching and networkStreamInGame.
            if (Application.isPlaying && !s_IsPostProcessing && !Ghost.SkipAutomaticPrefabRegistration)
                Netcode.RegisterPrefab(Prefab);
            // TODO-next auto prefab registration isn't going to be great for memory constrained devices. First iteration is going to effectively load all networked prefabs in memory on startup all the time. Need to address asap. P0 for mobile. Not great for addressable or even just normal scene loading.
        }

        internal static bool s_IsPostProcessing;
    }

#if UNITY_EDITOR
    internal class GhostPrefabPostProcessor : UnityEditor.AssetPostprocessor
    {
        void OnPostprocessPrefab(UnityEngine.GameObject g)
        {
            var adapter = g.GetComponent<GhostAdapter>();
            if (adapter == null)
                return;

            try
            {
                GhostPrefabReference.s_IsPostProcessing = true;
                adapter.prefabReference = ScriptableObject.CreateInstance<GhostPrefabReference>();

                adapter.prefabReference.Prefab = g;
                adapter.prefabReference.Ghost = adapter;
                context.AddObjectToAsset("GhostPrefabReference", adapter.prefabReference);
            }
            finally
            {
                GhostPrefabReference.s_IsPostProcessing = false;
            }
        }
    }
#endif // UNITY_EDITOR
}

#endif
