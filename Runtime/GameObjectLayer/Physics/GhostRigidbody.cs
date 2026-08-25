using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.NetCode
{
    /// <summary>
    /// Only used on predicted Rigidbodies, tracking and rolling back predicted data
    /// Default variant
    /// </summary>
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct GhostRigidbodyData : IComponentData
    {
        [GhostField(Quantization = 1000)] public float3 LinearVelocity;
        [GhostField(Quantization = 1000)] public float3 AngularVelocity;
        [GhostField] public float WakeCounter;
        [GhostField] public bool IsKinematic;
        [GhostField] public bool IsSleeping;

        // [GhostField(Quantization = 0)] public float3 AccumulatedForce;
        // TODO-next@physics users can sync forces? Useful for continuous forces, where we need to rollback to a previous "accumulated force" to replay. Need APIs from physics team to get forces without deltaTime
    }

    [GhostComponentVariation(typeof(GhostRigidbodyData), "GameObject Rigidbody - Unquantized")]
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
    struct GhostRigidbodyDataMaxPrecision
    {
        [GhostField(Quantization = 0)] public float3 LinearVelocity;
        [GhostField(Quantization = 0)] public float3 AngularVelocity;
        [GhostField(Quantization = 0)] public float WakeCounter;
        [GhostField] public bool IsKinematic;
        [GhostField] public bool IsSleeping;

        // [GhostField(Quantization = 0)] public float3 AccumulatedForce;
        // TODO-next@physics users can sync forces? Useful for continuous forces, where we need to rollback to a previous "accumulated force" to replay. Need APIs from physics team to get forces without deltaTime
    }

    [GhostComponentVariation(typeof(GhostRigidbodyData), "GameObject Rigidbody - 0.1 mm Precision")]
    [GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
#if NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL
    public
#endif
        struct GhostRigidbodyDataMediumPrecision
    {
        [GhostField(Quantization = 10_000)] public float3 LinearVelocity;
        [GhostField(Quantization = 10_000)] public float3 AngularVelocity;
        [GhostField(Quantization = 10_000)] public float WakeCounter;
        [GhostField] public bool IsKinematic;
        [GhostField] public bool IsSleeping;

        // [GhostField(Quantization = 0)] public float3 AccumulatedForce;
        // TODO-next@physics users can sync forces? Useful for continuous forces, where we need to rollback to a previous "accumulated force" to replay. Need APIs from physics team to get forces without deltaTime
    }

    internal struct GhostRigidbodyGameObjectTracker : IComponentData
    {
        public UnityObjectRef<Rigidbody> Rigidbody;
    }

    // TODO-next@physics colliders: After discussion with Alex, we'd need to investigate if we support multiple colliders on the same GO. https://unity.slack.com/archives/C06TQ917D/p1783008713374639?thread_ts=1783007827.433019&cid=C06TQ917D there's some physics optimisations users may want to do with this. We could potentially use a IBufferElementData for colliders.
    // TODO-release@physics: supporting runtime destroy/add of Rigidbody https://unity.slack.com/archives/C06TQ917D/p1783008599708409?thread_ts=1783007827.433019&cid=C06TQ917D Can't think of use cases for it. If users create a bug report for it, we can potentially use an IEnableable component for Rigidbody and make users "pre-add" GhostRigidbody on the GO to say "this can eventually have a Rigidbody attached to it".

    /// <summary>
    /// GhostBehaviour to be able to predict and sync GameObject Rigidbodies. Add this to your GameObject through the netcode checkbox on your Rigidbody
    /// to automatically predict and sync it.
    /// To reduce jitter, it's recommended to either use full precision transform syncing (using
    /// the <see cref="Unity.NetCode.GhostAuthoringInspectionComponent"/> (which will increase precision, but also increase bandwidth consumption)
    /// or enable prediction error smoothing.
    /// </summary>
    /// <remarks>
    /// For dynamic setups, it's recommended to use a broadphase type set to ABP in your physics project settings to get the most
    /// performance as possible, especially with prediction rollback and replay.
    /// </remarks>
    [RequireComponent(typeof(Rigidbody))]
    partial class GhostRigidbody : GhostBehaviour
    {
        [HideInInspector]
        internal Rigidbody m_AttachedRigidbody;

        GhostComponentRef<GhostRigidbodyData> m_Data;
        GhostComponentRef<GhostRigidbodyGameObjectTracker> m_RigidbodyTracker;

        public override void Awake()
        {
            base.Awake();
            if (Ghost.IsPrefab())
                return;
            m_AttachedRigidbody = GetComponent<Rigidbody>();
            var tracker = m_RigidbodyTracker.Value;
            tracker.Rigidbody.Value = m_AttachedRigidbody;
            m_RigidbodyTracker.Value = tracker;
            if (CanWriteState)
            {
                // init component data with GO values
                m_Data.Value = new GhostRigidbodyData()
                {
                    IsKinematic = m_AttachedRigidbody.isKinematic,
                    AngularVelocity = m_AttachedRigidbody.angularVelocity,
                    LinearVelocity = m_AttachedRigidbody.linearVelocity,
                    IsSleeping = m_AttachedRigidbody.IsSleeping(),
                    // WakeCounter = m_AttachedRigidbody.wakeCounter, // TODO-next@physicsSleepWake
                };
            }
        }
    }
}
