// IMPORTANT NOTE: This file is shared with NetCode source generators
// NO UnityEngine, UnityEditor or other packages dll references are allowed here.
// IF YOU CHANGE THIS FILE, REMEMBER TO RECOMPILE THE SOURCE GENERATORS

namespace Unity.NetCode.Generators
{
    internal static class CodeGenDefaults
    {
        public const string TemplatesPath = "Packages/com.unity.netcode/Editor/Templates/";
        public const string RpcSerializer = "RpcCommandSerializer.cs";
        public const string CommandSerializer = "CommandDataSerializer.cs";
        public const string ComponentSerializer = "GhostComponentSerializer.cs";
        public const string RegistrationSystem = "GhostComponentSerializerRegistrationSystem.cs";
        public static readonly TypeRegistryEntry[] Types = new[]
        {
            new TypeRegistryEntry
            {
                Type = "System.Int32",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueInt.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.UInt32",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueUInt.cs"
            },

            new TypeRegistryEntry
            {
                Type = "System.Int64",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueInt.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueLong.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.UInt64",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueUInt.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueULong.cs"
            },

            new TypeRegistryEntry
            {
                Type = "System.Int16",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueInt.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.UInt16",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueUInt.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.SByte",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueInt.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.Byte",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueUInt.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.Boolean",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueUInt.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueBool.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.Single",
                Quantized = true,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.Single",
                Quantized = true,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = false,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.Single",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "System.Single",
                Quantized = false,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float2",
                Quantized = true,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat2.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float2",
                Quantized = true,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat2.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float2",
                Quantized = false,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat2Unquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float2",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat2Unquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float3",
                Quantized = true,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat3.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float3",
                Quantized = true,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat3.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float3",
                Quantized = false,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat3Unquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float3",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat3Unquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float4",
                Quantized = true,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat4.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float4",
                Quantized = true,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat4.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float4",
                Quantized = false,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat4Unquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.float4",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloatUnquantized.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFloat4Unquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.quaternion",
                Quantized = true,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueQuaternion.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.quaternion",
                Quantized = true,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = false,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueQuaternion.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.quaternion",
                Quantized = false,
                Smoothing = SmoothingAction.Interpolate,
                SupportCommand = false,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueQuaternionUnquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Mathematics.quaternion",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueQuaternionUnquantized.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Entities.Entity",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueEntity.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Collections.FixedString32Bytes",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString32Bytes.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Collections.FixedString64Bytes",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString32Bytes.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString64Bytes.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Collections.FixedString128Bytes",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString32Bytes.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString128Bytes.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Collections.FixedString512Bytes",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString32Bytes.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString512Bytes.cs"
            },
            new TypeRegistryEntry
            {
                Type = "Unity.Collections.FixedString4096Bytes",
                Quantized = false,
                Smoothing = SmoothingAction.Clamp,
                SupportCommand = true,
                Composite = false,
                Template = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString32Bytes.cs",
                TemplateOverride = $"{TemplatesPath}DefaultTypes/GhostSnapshotValueFixedString4096Bytes.cs"
            }
        };
    }
}
