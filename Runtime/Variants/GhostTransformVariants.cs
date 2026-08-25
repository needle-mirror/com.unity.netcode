using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Scripting;

namespace Unity.NetCode
{
    /// <summary>
    /// The default serialization strategy for the <see cref="Unity.Transforms.LocalTransform"/> components provided by the NetCode package.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "Transform - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct TransformDefaultVariant
    {
        /// <summary>
        /// The position value is replicated with a default quantization unit of 1000 (so roughly 1mm precision per component).
        /// The replicated position value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// The scale value is replicated with a default quantization unit of 1000.
        /// The replicated scale value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;

        /// <summary>
        /// The rotation quaternion is replicated and the resulting floating point data use for replication the rotation is quantized with good precision (10 or more bits per component)
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;
    }
    /// <summary>
    /// A serialization strategy for <see cref="Unity.Transforms.LocalTransform"/> that replicates only the entity
    /// <see cref="Unity.Transforms.LocalTransform.Position"/>.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "PositionOnly - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct PositionOnlyVariant
    {
        /// <summary>
        /// The position value is replicated with a default quantization unit of 1000 (so roughly 1mm precision per component).
        /// The replicated position value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;
    }
    /// <summary>
    /// A serialization strategy for <see cref="Unity.Transforms.LocalTransform"/> that replicates only the entity
    /// <see cref="Unity.Transforms.LocalTransform.Rotation"/>.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "RotationOnly - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct RotationOnlyVariant
    {
        /// <summary>
        /// The rotation quaternion is replicated and the resulting floating point data use for replication the rotation is quantized with good precision (10 or more bits per component)
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;
    }
    /// <summary>
    /// A serialization strategy that replicates the entity <see cref="Unity.Transforms.LocalTransform.Position"/> and
    /// <see cref="Unity.Transforms.LocalTransform.Rotation"/> properties.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "PositionAndRotation - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct PositionRotationVariant
    {
        /// <summary>
        /// The position value is replicated with a default quantization unit of 1000 (so roughly 1mm precision per component).
        /// The replicated position value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// The position value is replicated with a default quantization unit of 100 (so roughly 1cm precision per component).
        /// The replicated position value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;
    }
    /// <summary>
    /// A serialization strategy that replicates the entity <see cref="Unity.Transforms.LocalTransform.Position"/> and
    /// <see cref="Unity.Transforms.LocalTransform.Scale"/> properties.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "PositionScale - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct PositionScaleVariant
    {
        /// <summary>
        /// The position value is replicated with a default quantization unit of 1000 (so roughly 1mm precision per component).
        /// The replicated position value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// The scale value is replicated with a default quantization unit of 1000, and support both interpolation and exrapolation.
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;
    }
    /// <summary>
    /// A serialization strategy that replicates the entity <see cref="Unity.Transforms.LocalTransform.Rotation"/> and
    /// <see cref="Unity.Transforms.LocalTransform.Scale"/> properties.
    /// </summary>
    [Preserve]
    [GhostComponentVariation(typeof(Transforms.LocalTransform), "RotationScale - 3D")]
    [GhostComponent(PrefabType=GhostPrefabType.All, SendTypeOptimization=GhostSendType.AllClients)]
    public struct RotationScaleVariant
    {
        /// <summary>
        /// The position value is replicated with a default quantization unit of 1000 (so roughly 1mm precision per component).
        /// The replicated position value support both interpolation and extrapolation
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;

        /// <summary>
        /// The scale value is replicated with a default quantization unit of 1000, and support both interpolation and exrapolation.
        /// </summary>
        [GhostField(Quantization=1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;
    }


    // Disabling quantization reduces the amount of misprediction jitter by a lot.
    // TODO-next@physics Something we could do to reenable quantization is to quantize again at the end of the simulation on local values, so we always simulate with the same quantized values
    // this way if I receive snapshot tick 10, then the predicted tick 11 should be quantized the same as we'd eventually receive it, so that tick 12 bases itself on the same tick
    /// <summary>
    /// Variant used to disable quantization for your transforms. Useful for cases that require high precision like physics.
    /// This will use a substantial amount of additional bandwidth, you should consider trying to smooth prediction errors before using this variant.
    /// </summary>
    [GhostComponentVariation(typeof(LocalTransform), "Transform 3D - Unquantized")]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
    public struct TransformVariantMaxPrecision : IComponentData
    {
        /// <summary>
        /// Position replicated with max float precision
        /// </summary>
        [GhostField(Quantization = 0, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// Rotation replicated with max float precision
        /// </summary>
        [GhostField(Quantization = 0, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;

        /// <summary>
        /// Scale replicated with max float precision
        /// </summary>
        [GhostField(Quantization = 0, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;
    }

    /// <summary>
    /// Transform variant that uses 10_000 quantization level instead of the default 1_000
    /// </summary>
    [GhostComponentVariation(typeof(LocalTransform), "Transform 3D - 0.1 mm Precision")]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
    public struct TransformVariantMediumPrecision : IComponentData
    {
        /// <summary>
        /// Position replicated with max float precision
        /// </summary>
        [GhostField(Quantization = 10_000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Position;

        /// <summary>
        /// Rotation replicated with max float precision
        /// </summary>
        [GhostField(Quantization = 10_000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public quaternion Rotation;

        /// <summary>
        /// Scale replicated with max float precision
        /// </summary>
        [GhostField(Quantization = 10_000, Smoothing=SmoothingAction.InterpolateAndExtrapolate)]
        public float Scale;
    }

    /// <summary>
    /// System that optionally setup the Netcode default variants used for transform components in case a default is not already present.
    /// The following variants are set by default by the package:
    /// - <see cref="Unity.Transforms.LocalTransform"/>
    /// - <see cref="Unity.Transforms.Translation"/>
    /// - <see cref="Unity.Transforms.Rotation"/>
    /// - <see cref="Unity.Transforms.PostTransformMatrix"/> -- Not replicated by default
    /// </summary>
    /// <remarks>
    /// <para>It will never override the default assignment for the transform components if they are already present in the
    /// <see cref="GhostComponentSerializerCollectionData.DefaultVariants"/> map.</para>
    /// <para>Any system deriving from <see cref="DefaultVariantSystemBase"/> will take precedence, even if they are created
    /// after this system.</para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation |
                       WorldSystemFilterFlags.ThinClientSimulation | WorldSystemFilterFlags.BakingSystem)]
    [CreateAfter(typeof(GhostComponentSerializerCollectionSystemGroup))]
    [UpdateInGroup(typeof(DefaultVariantSystemGroup), OrderLast = true)]
    public sealed partial class TransformDefaultVariantSystem : SystemBase
    {
        protected override void OnCreate()
        {
            var rules = World.GetExistingSystemManaged<GhostComponentSerializerCollectionSystemGroup>().DefaultVariantRules;
            rules.TrySetDefaultVariant(ComponentType.ReadWrite<LocalTransform>(), DefaultVariantSystemBase.Rule.OnlyParents(typeof(TransformDefaultVariant)), this);

            // PostTransformMatrix holds the optional 3D (non-uniform) scale (LocalTransform only supports uniform scale).
            // It is never replicated by default, so existing (entities) projects with ghosts that happen to have a
            // PostTransformMatrix get no surprise bandwidth or client-side scale stomping. Opt in per-prefab by selecting
            // PostTransformMatrix3DScaleVariant, or globally via a DefaultVariantSystemBase. GameObject-layer
            // (GhostObject) prefabs opt in automatically through a per-prefab override added by PrefabRegistry.
            rules.TrySetDefaultVariant(ComponentType.ReadWrite<PostTransformMatrix>(), DefaultVariantSystemBase.Rule.OnlyParents(typeof(DontSerializeVariant)), this);

            Enabled = false;
        }

        protected override void OnUpdate()
        {
        }
    }
}
