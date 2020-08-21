using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using Unity.Collections;
using TypeInfo = Unity.Entities.TypeManager.TypeInfo;

namespace Unity.NetCode.Editor
{
    public class RpcGenerator
    {
        private TypeInformation m_TypeInformation;
        private GhostCodeGen m_RpcGenerator;
        private TypeTemplate m_Template;
        public TypeInformation TypeInformation => m_TypeInformation;

        public bool Composite => m_Template?.Composite ?? false;

        public RpcGenerator()
        {
            m_RpcGenerator = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/RpcCommandSerializer.cs");
        }
        public RpcGenerator(TypeInformation information)
        {
            m_RpcGenerator = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/RpcCommandSerializer.cs");
            m_TypeInformation = information;
        }

        public RpcGenerator(TypeInformation information, TypeTemplate template)
        {
            m_RpcGenerator = new GhostCodeGen("Packages/com.unity.netcode/Editor/CodeGenTemplates/RpcCommandSerializer.cs");
            m_TypeInformation = information;
            m_Template = template;
        }

        public void AppendTarget(RpcGenerator typeGenerator)
        {
            m_RpcGenerator.Append(typeGenerator.m_RpcGenerator);
        }

        public void GenerateFields(CodeGenerator.Context context, string parent = null)
        {
            if (m_Template == null)
                return;

            if (!context.typeCodeGenCache.TryGetValue(m_Template.TemplatePath + m_Template.TemplateOverridePath,
                out var generator))
            {
                generator = new GhostCodeGen(m_Template.TemplatePath);

                if (!string.IsNullOrEmpty(m_Template.TemplateOverridePath))
                    generator.AddTemplateOverrides(m_Template.TemplateOverridePath);

                context.typeCodeGenCache.Add(m_Template.TemplatePath + m_Template.TemplateOverridePath, generator);
            }
            var fieldName = string.IsNullOrEmpty(parent)
                ? m_TypeInformation.FieldInfo.Name
                : $"{parent}.{m_TypeInformation.FieldInfo.Name}";

            generator.Replacements.Clear();
            generator.Replacements.Add("RPC_FIELD_NAME", fieldName);
            generator.Replacements.Add("RPC_FIELD_TYPE_NAME", m_TypeInformation.Type.GetFieldTypeName());

            generator.GenerateFragment("RPC_READ", generator.Replacements, m_RpcGenerator);
            generator.GenerateFragment("RPC_WRITE", generator.Replacements, m_RpcGenerator);

            if(m_TypeInformation.Type.Scope != null)
            {
                context.collectionAssemblies.Add(m_TypeInformation.Type.Scope.Name);
            }
        }

        public void GenerateSerializer(CodeGenerator.Context context, Mono.Cecil.TypeDefinition typeDefinition)
        {
            var replacements = new Dictionary<string, string>();
            replacements.Add("RPC_NAME", typeDefinition.FullName.Replace(".", "").Replace("/", "_"));
            replacements.Add("RPC_NAMESPACE", context.generatedNs);
            replacements.Add("RPC_COMPONENT_TYPE", typeDefinition.FullName.Replace("/", "."));

            context.collectionAssemblies.Add(typeDefinition.Module.Assembly.Name.Name);
            if (typeDefinition.Namespace != null && typeDefinition.Namespace != "")
            {
                context.imports.Add(typeDefinition.Namespace);
            }

            foreach (var ns in context.imports)
            {
                replacements["RPC_USING"] = CodeGenNamespaceUtils.GetValidNamespaceForType(context.generatedNs, ns);
                m_RpcGenerator.GenerateFragment("RPC_USING_STATEMENT", replacements);
            }

            var serializerName = typeDefinition.FullName.Replace("/", "+") + "Serializer.cs";
            m_RpcGenerator.GenerateFile("", context.outputFolder, serializerName, replacements, context.batch);
        }

        public override string ToString()
        {
            var debugInformation = m_TypeInformation.ToString();
            debugInformation += m_Template?.ToString();
            debugInformation += m_RpcGenerator?.ToString();
            return debugInformation;
        }
    }
}
