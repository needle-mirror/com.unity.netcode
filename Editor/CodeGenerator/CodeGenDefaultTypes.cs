using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode.Editor
{
    internal static class CodeGenTypes
    {
        static CodeGenTypes()
        {
            Registry = new TypeRegistry(GetDefaultTypes());
        }
        public static TypeRegistry Registry;

        private static TypeRegistry.CodeGenType[] GetDefaultTypes()
        {
            return new[]
            {
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(int), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(uint), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueUInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(short), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(ushort), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueUInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(byte), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueUInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(sbyte), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(Enum), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueInt.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(bool), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueUInt.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueBool.cs",
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized | TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        SupportsQuantization = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        SupportsQuantization = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float2), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat2.cs",
                        SupportsQuantization = true,
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float2), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized | TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat2.cs",
                        SupportsQuantization = true,
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float2), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat2Unquantized.cs",
                        Composite = true
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float2), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat2Unquantized.cs",
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float3), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat3.cs",
                        SupportsQuantization = true,
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float3), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized | TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat3.cs",
                        SupportsQuantization = true,
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float3), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat3Unquantized.cs",
                        Composite = true
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float3), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat3Unquantized.cs",
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float4), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat4.cs",
                        SupportsQuantization = true,
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float4), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized | TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat4.cs",
                        SupportsQuantization = true,
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float4), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat4Unquantized.cs",
                        Composite = true
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(float4), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFloat4Unquantized.cs",
                        Composite = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(quaternion), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueQuaternion.cs",
                        SupportsQuantization = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(quaternion), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Quantized | TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueQuaternion.cs",
                        SupportsQuantization = true,
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(quaternion), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueQuaternionUnquantized.cs"
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(quaternion), TypeAttribute.Specialized(TypeAttribute.AttributeFlags.Interpolated)),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueQuaternionUnquantized.cs",
                        SupportCommand = false
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(Entity), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueEntity.cs",
                        SupportCommand = true
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(FixedString32), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString32.cs",
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(FixedString64), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString32.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString64.cs",
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(FixedString128), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString32.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString128.cs",
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(FixedString512), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString32.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString512.cs",
                    }
                },
                new TypeRegistry.CodeGenType
                {
                    description = new TypeDescription(typeof(FixedString4096), TypeAttribute.Empty()),
                    template = new TypeTemplate
                    {
                        TemplatePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString32.cs",
                        TemplateOverridePath = $"{TypeRegistry.k_TemplateRootPath}/GhostSnapshotValueFixedString4096.cs",
                    }
                },
            };
        }
    }
}