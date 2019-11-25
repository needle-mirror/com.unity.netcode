using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.NetCode
{
    public abstract class GhostSnapshotValue
    {
        public static List<GhostSnapshotValue> GameSpecificTypes = new List<GhostSnapshotValue>();

        internal static GhostSnapshotValue[] DefaultTypes = new GhostSnapshotValue[]
        {
            new GhostSnapshotValueQuaternion(),
            new GhostSnapshotValueFloat(),
            new GhostSnapshotValueFloat2(),
            new GhostSnapshotValueFloat3(),
            new GhostSnapshotValueFloat4(),
            new GhostSnapshotValueInt(),
            new GhostSnapshotValueUInt(),
            new GhostSnapshotValueBool(),
            new GhostSnapshotValueNativeString64(),
            new GhostSnapshotValueEntity()
        };

        public virtual void AddImports(HashSet<string> imports)
        {
        }

        public abstract bool SupportsQuantization { get; }

        public virtual bool CanProcess(FieldInfo field, string componentName, string fieldName)
        {
            return CanProcess(field.FieldType, componentName, fieldName);
        }
        public abstract bool CanProcess(System.Type type, string componentName, string fieldName);

        public abstract string GetTemplatePath(int quantization);

        protected const string k_TemplateRootPath = "Packages/com.unity.netcode/Editor/CodeGenTemplates/SnapshotValue";
    }

    class GhostSnapshotValueQuaternion : GhostSnapshotValue
    {
        public override bool SupportsQuantization => true;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(quaternion);
        }

        public override string GetTemplatePath(int quantization)
        {
            if (quantization < 1)
                return $"{k_TemplateRootPath}/GhostSnapshotValueQuaternionUnquantized.cs";
            return $"{k_TemplateRootPath}/GhostSnapshotValueQuaternion.cs";
        }
    }

    class GhostSnapshotValueFloat : GhostSnapshotValue
    {
        public override bool SupportsQuantization => true;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(float);
        }

        public override string GetTemplatePath(int quantization)
        {
            if (quantization < 1)
                return $"{k_TemplateRootPath}/GhostSnapshotValueFloatUnquantized.cs";
            return $"{k_TemplateRootPath}/GhostSnapshotValueFloat.cs";
        }
    }

    class GhostSnapshotValueFloat2 : GhostSnapshotValue
    {
        public override bool SupportsQuantization => true;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(float2);
        }

        public override string GetTemplatePath(int quantization)
        {
            if (quantization < 1)
                return $"{k_TemplateRootPath}/GhostSnapshotValueFloat2Unquantized.cs";
            return $"{k_TemplateRootPath}/GhostSnapshotValueFloat2.cs";
        }
    }

    class GhostSnapshotValueFloat3 : GhostSnapshotValue
    {
        public override bool SupportsQuantization => true;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(float3);
        }

        public override string GetTemplatePath(int quantization)
        {
            if (quantization < 1)
                return $"{k_TemplateRootPath}/GhostSnapshotValueFloat3Unquantized.cs";
            return $"{k_TemplateRootPath}/GhostSnapshotValueFloat3.cs";
        }
    }

    class GhostSnapshotValueFloat4 : GhostSnapshotValue
    {
        public override bool SupportsQuantization => true;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(float4);
        }
        public override string GetTemplatePath(int quantization)
        {
            if (quantization < 1)
                return $"{k_TemplateRootPath}/GhostSnapshotValueFloat4Unquantized.cs";
            return $"{k_TemplateRootPath}/GhostSnapshotValueFloat4.cs";
        }
    }

    class GhostSnapshotValueInt : GhostSnapshotValue
    {
        public override bool SupportsQuantization => false;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(int) ||
                type == typeof(short) ||
                type == typeof(sbyte) ||
                type.IsEnum;
        }

        public override string GetTemplatePath(int quantization)
        {
            return $"{k_TemplateRootPath}/GhostSnapshotValueInt.cs";
        }
    }

    class GhostSnapshotValueUInt : GhostSnapshotValue
    {
        public override bool SupportsQuantization => false;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(uint) ||
                type == typeof(ushort) ||
                type == typeof(byte);
        }

        public override string GetTemplatePath(int quantization)
        {
            return $"{k_TemplateRootPath}/GhostSnapshotValueUInt.cs";
        }
    }

    class GhostSnapshotValueBool : GhostSnapshotValue
    {
        public override bool SupportsQuantization => false;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(bool);
        }

        public override string GetTemplatePath(int quantization)
        {
            return $"{k_TemplateRootPath}/GhostSnapshotValueBool.cs";
        }
    }

    class GhostSnapshotValueNativeString64 : GhostSnapshotValue
    {
        public override bool SupportsQuantization => false;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(NativeString64);
        }

        public override void AddImports(HashSet<string> imports)
        {
            imports.Add("Unity.Entities");
        }

        public override string GetTemplatePath(int quantization)
        {
            return $"{k_TemplateRootPath}/GhostSnapshotValueNativeString64.cs";
        }
    }

    class GhostSnapshotValueEntity : GhostSnapshotValue
    {
        public override bool SupportsQuantization => false;

        public override bool CanProcess(System.Type type, string componentName, string fieldName)
        {
            return type == typeof(Entity);
        }

        public override void AddImports(HashSet<string> imports)
        {
            imports.Add("Unity.Entities");
        }

        public override string GetTemplatePath(int quantization)
        {
            return $"{k_TemplateRootPath}/GhostSnapshotValueEntity.cs";
        }
    }
}
