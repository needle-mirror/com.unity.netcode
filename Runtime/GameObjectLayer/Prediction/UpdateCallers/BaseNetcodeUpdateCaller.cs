#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace Unity.NetCode
{
    /// <summary>
    /// Base system class for our various GhostBehaviour methods (example prediction update, fixed prediction update, input gathering, etc)
    /// </summary>
    internal abstract partial class BaseNetcodeUpdateCaller : SystemBase
    {
        protected EntityQuery m_GhostsToRunOn;

        static Dictionary<Type, ProfilerMarker> s_PredictionProfilerMarker = new();

        internal struct RunBehaviourInfo
        {
            public GhostBehaviour ToRun;
            public GhostBehaviourTypeInfo Info;
        }
        internal List<RunBehaviourInfo>[] m_UpdateBuckets;

        protected abstract void InitQueryForGhosts();
        protected abstract bool HasUpdate(in GhostBehaviourTypeInfo typeInfo);
        protected abstract void RunMethodOnBehaviour(GhostBehaviour behaviour, float deltaTime);

        protected override void OnCreate()
        {
            InitQueryForGhosts();
            m_UpdateBuckets = new List<RunBehaviourInfo>[Netcode.Instance.GhostBehaviourTypeManager.UpdateBucketCount];
            for (int i = 0; i < m_UpdateBuckets.Length; i++)
                m_UpdateBuckets[i] = new List<RunBehaviourInfo>(32);
            //How do I get all the scripting order? The problem is that in the editor the order value assignment can change all the time.
            //What that means is that it is really hard to keep this
            RequireForUpdate(m_GhostsToRunOn);
            RequireForUpdate<NetworkStreamInGame>();
            ghostAdapterIDs = new (50, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            ghostAdapterIDs.Dispose();
        }

        private static readonly ProfilerMarker s_BucketMarker = new ("Execution order buckets sorting");

        NativeList<EntityId> ghostAdapterIDs;
        List<Object> ghostAdapters = new();
        protected override void OnUpdate()
        {
            {
                // Update the ordered buckets
                // TODO-next@physics: should share bucket updating with prediction update loops? and with inputs? Order is constant between prediction, input and fixed prediction. Even if there are new spawns, they shouldn't be updated until their Start has run, which is later in the frame.
                // TODO-release@potentialOptim: bucket sorting is the slowest part of GameObject prediction. With 1000 ghosts, in release mode this is taking 0.25 ms client side and 0.12ms server side (with no logic in the prediction update, this is pure overhead). This is not great. Granted, 1000 predicted ghosts is not realistic, we're expecting max 200 predicted ghosts (and the rest switched dynamically to interpolated). But 1k ghosts server side with a prediction update is realistic. We should find ways to burst this, batch more calls instead of calling methods per GhostBehaviour and not rely on managed calls as much
                // TODO-release@potentialOptim have a way to check if we actually need to recalculate the buckets or if we can just reuse the previous tick's buckets (if there's no new ghosts and no behaviour activating/deactivating
                using var _ = s_BucketMarker.Auto();
                using var links = m_GhostsToRunOn.ToComponentDataArray<GhostGameObjectLink>(Allocator.Temp);
                using var behaviourInfos = m_GhostsToRunOn.ToComponentDataArray<GhostBehaviour.GhostBehaviourTracking>(Allocator.Temp);

                ghostAdapterIDs.SetCapacity(links.Length);

                for (int i = 0; i < links.Length; i++)
                {
                    ghostAdapterIDs.Add(links[i].GhostAdapterId);
                }

                Resources.EntityIdsToObjectList(ghostAdapterIDs.AsArray(), ghostAdapters);
                ghostAdapterIDs.Clear();
                for (int entityIndex = 0; entityIndex < links.Length; entityIndex++)
                {
                    var ghost = ghostAdapters[entityIndex] as GhostAdapter;
                    var behaviours = ghost.m_AllBehaviours;
                    if(behaviours.Length == 0 || (behaviours.Length > 1 && !ghost.gameObject.activeInHierarchy))
                        // if we have more than 1 behaviour, it's useful to early return if the GameObject is not active.
                        // if there's only one, then we'll check for active anyway lower in this logic, so we skip this cost here
                        continue;
                    var ghostBehaviourTracking = behaviourInfos[entityIndex];
                    if (ghostBehaviourTracking.AnyHasUpdate)
                    {
                        var behaviourTypeInfos = ghostBehaviourTracking.allBehaviourTypeInfo;

                        for (int behaviourIndex = 0; behaviourIndex < behaviours.Length; ++behaviourIndex)
                        {
                            GhostBehaviour behaviour = behaviours[behaviourIndex];

                            // TODO-release@potentialOptim see if we can add a batched version of isActiveAndEnabled c++ side, like Object.IsActive(NativeArray<Object>)
                            var ghostBehaviourTypeInfo = behaviourTypeInfos[behaviourIndex];
                            // checking for if behaviour is null since it could be a valid use case to just destroy the monobehaviour, not the whole GO
                            //TODO-next@domino after domino PRs: if we wait for didStart, how do we get a "init value that's on the predicted timeline". Should we add a "PredictedStart"?
                            // See exploration https://docs.google.com/spreadsheets/d/1oGimfj4V6tiXPin1HNgqoqG1yr4uuyRFeOIy6Y0SpSs/edit?gid=0#gid=0
                            /*
                             Even if there's no Start in a given monobehaviour, if that predictionUpdate relies on setup that's in a Start on another GhostBehaviour, that Start won't happen until later and so we want to make sure the order is as expected.
                             // alice.cs
                             void Start()
                             {
                               int a = 123;
                             }
                             // bob.cs (with no Start)
                             void PredictionUpdate()
                             {
                               if (alice.a == 0) throw; // this will throw if we don't check for ghost.didStart... but wouldn't if this was a normal Update()
                             }
                             // Table of truth for following hasStart and didStart logic
                             // has | bhvr did | ghost did || doPrediction
                             // 0        0             0         0
                             // 0        0             1         1
                             // 0        1             0         0 --> invalid
                             // 0        1             1         1 --> invalid
                             // 1        0             0         0
                             // 1        0             1         0
                             // 1        1             0         0 --> invalid, we don't support users disabling GhostObjects
                             // 1        1             1         1
                             */
                            // important: ghost.didStart is used for GhostBehaviours with no Start method
                            if (ghostBehaviourTypeInfo.AnyHasUpdate() && behaviour != null &&
                                ((!ghostBehaviourTypeInfo.HasStart || behaviour.didStart) && ghost.didStart) &&
                                behaviour.isActiveAndEnabled)
                            {
                                m_UpdateBuckets[ghostBehaviourTypeInfo.UpdateBucket].Add(new RunBehaviourInfo{ToRun = behaviour, Info = ghostBehaviourTypeInfo});
                            }
                        }
                    }
                }
            }
            var deltaTime = SystemAPI.Time.DeltaTime; // deltaTime is overriden with netcode's tick deltaTime
            for (var bucketIndex = 0; bucketIndex < m_UpdateBuckets.Length; bucketIndex++)
            {
                ProfilerMarker marker = default;
                Type currentType = null;
                var behavioursInBucket = m_UpdateBuckets[bucketIndex];
                for (int i = 0; i < behavioursInBucket.Count; ++i)
                {
                    try
                    {
                        var behaviour = behavioursInBucket[i];
#if ENABLE_PROFILER
                        if (Profiler.enabled)
                        {
                            var type = behaviour.ToRun.GetType();
                            if (currentType != type)
                            {
                                if (!s_PredictionProfilerMarker.TryGetValue(type, out marker))
                                {
                                    marker = new ProfilerMarker($"{type.Name}"); // no need for more info than the name, the profiler will show in its hierarchy whether it's calling from the input gathering system, the prediction system, etc
                                    s_PredictionProfilerMarker.Add(type, marker);
                                }
                            }

                            currentType = type;
                        }
#endif

                        if (HasUpdate(behaviour.Info)) // we could have this check in the bucket sorting instead, but this barely registers as a blip in the profiler and allows reusing the bucket filtering with our various prediction methods
                        {
                            marker.Begin(behaviour.ToRun.gameObject);
                            RunMethodOnBehaviour(behaviour.ToRun, deltaTime);
                            marker.End();
                        }
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }
                }

                behavioursInBucket.Clear();
            }
        }
    }
}
#endif
