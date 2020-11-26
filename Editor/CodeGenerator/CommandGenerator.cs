using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using Unity.Collections;
using TypeInfo = Unity.Entities.TypeManager.TypeInfo;

namespace Unity.NetCode.Editor
{
    internal class CommandGenerator
    {
        public enum Type
        {
            Rpc,
            Input
        }
        private TypeInformation m_TypeInformation;
        private GhostCodeGen m_CommandGenerator;
        private TypeTemplate m_Template;
        public TypeInformation TypeInformation => m_TypeInformation;

        public bool Composite => m_Template?.Composite ?? false;

        public Type CommandType {get; private set;}

        public CommandGenerator(CodeGenerator.Context context, Type t)
        {
            CommandType = t;
            var template = t == Type.Rpc ?
                "Packages/com.unity.netcode/Editor/CodeGenTemplates/RpcCommandSerializer.cs" :
                "Packages/com.unity.netcode/Editor/CodeGenTemplates/CommandDataSerializer.cs";
            if (!context.typeCodeGenCache.TryGetValue(template, out var generator))
            {
                generator = new GhostCodeGen(template);

                context.typeCodeGenCache.Add(template, generator);
            }
            m_CommandGenerator = generator.Clone();
        }
        public CommandGenerator(CodeGenerator.Context context, Type t, TypeInformation information) : this(context, t)
        {
            m_TypeInformation = information;
        }

        public CommandGenerator(CodeGenerator.Context context, Type t, TypeInformation information, TypeTemplate template) : this(context, t)
        {
            m_TypeInformation = information;
            m_Template = template;
        }

        public void AppendTarget(CommandGenerator typeGenerator)
        {
            m_CommandGenerator.Append(typeGenerator.m_CommandGenerator);
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
                ? m_TypeInformation.FieldName
                : $"{parent}.{m_TypeInformation.FieldName}";

            generator = generator.Clone();
            generator.Replacements.Add("COMMAND_FIELD_NAME", fieldName);
            generator.Replacements.Add("COMMAND_FIELD_TYPE_NAME", m_TypeInformation.Type.GetFieldTypeName());

            generator.GenerateFragment("COMMAND_READ", generator.Replacements, m_CommandGenerator);
            generator.GenerateFragment("COMMAND_WRITE", generator.Replacements, m_CommandGenerator);

            if (CommandType != CommandGenerator.Type.Rpc)
            {
                generator.GenerateFragment("COMMAND_READ_PACKED", generator.Replacements, m_CommandGenerator);
                generator.GenerateFragment("COMMAND_WRITE_PACKED", generator.Replacements, m_CommandGenerator);
            }

            if(m_TypeInformation.Type.Scope != null)
            {
                context.collectionAssemblies.Add(m_TypeInformation.Type.Scope.Name);
            }
        }

        public void GenerateSerializer(CodeGenerator.Context context, Mono.Cecil.TypeDefinition typeDefinition)
        {
            var replacements = new Dictionary<string, string>();
            replacements.Add("COMMAND_NAME", typeDefinition.FullName.Replace(".", "").Replace("/", "_"));
            replacements.Add("COMMAND_NAMESPACE", context.generatedNs);
            replacements.Add("COMMAND_COMPONENT_TYPE", typeDefinition.FullName.Replace("/", "."));

            context.collectionAssemblies.Add(typeDefinition.Module.Assembly.Name.Name);
            if (typeDefinition.Namespace != null && typeDefinition.Namespace != "")
            {
                context.imports.Add(typeDefinition.Namespace);
            }

            foreach (var ns in context.imports)
            {
                replacements["COMMAND_USING"] = CodeGenNamespaceUtils.GetValidNamespaceForType(context.generatedNs, ns);
                m_CommandGenerator.GenerateFragment("COMMAND_USING_STATEMENT", replacements);
            }

            var serializerName = typeDefinition.FullName.Replace("/", "+") + "CommandSerializer.cs";
            m_CommandGenerator.GenerateFile("", context.outputFolder, serializerName, replacements, context.batch);
        }

        public override string ToString()
        {
            var debugInformation = m_TypeInformation.ToString();
            debugInformation += m_Template?.ToString();
            debugInformation += m_CommandGenerator?.ToString();
            return debugInformation;
        }
    }
}
