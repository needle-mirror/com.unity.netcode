using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace Unity.NetCode.Editor
{
    internal class TypeGenerator
    {
        private TypeInformation m_TypeInformation;
        private GhostCodeGen m_TargetGenerator;
        private GhostCodeGen m_ActiveGenerator;
        private TypeTemplate m_Template;
        private static Regex m_usingRegex = new Regex("(\\w+)(?=;)");

        public bool IsContainerType => m_Template == null && m_ActiveGenerator == null;
        public bool Composite => m_Template?.Composite ?? false;
        public TypeInformation TypeInformation => m_TypeInformation;
        public string TemplateOverridePath => m_Template.TemplateOverridePath;

        public bool Quantized => m_Template?.SupportsQuantization ?? false;

        string[,] k_OverridableFragments =
        {
            // fragment + alernative fragment in case of interpolation
            {"GHOST_FIELD", "GHOST_FIELD"},
            {"GHOST_COPY_TO_SNAPSHOT", "GHOST_COPY_TO_SNAPSHOT"},
            {"GHOST_COPY_FROM_SNAPSHOT", "GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE"},
            {"GHOST_RESTORE_FROM_BACKUP", "GHOST_RESTORE_FROM_BACKUP"},
            {"GHOST_PREDICT", "GHOST_PREDICT"},
            {"GHOST_REPORT_PREDICTION_ERROR", "GHOST_REPORT_PREDICTION_ERROR"},
            {"GHOST_GET_PREDICTION_ERROR_NAME", "GHOST_GET_PREDICTION_ERROR_NAME"},
        };

        string m_OverridableFragmentsList = "";

        public void GenerateFields(CodeGenerator.Context context, string parent = null, Dictionary<string, GhostCodeGen.FragmentData> overrides = null)
        {
            if (m_Template == null)
                return;

            var quantization = m_TypeInformation.Attribute.quantization;
            var interpolate = m_TypeInformation.Attribute.smoothing > 0;
            if (!context.typeCodeGenCache.TryGetValue(m_Template.TemplatePath + m_Template.TemplateOverridePath,
                out var generator))
            {
                generator = new GhostCodeGen(m_Template.TemplatePath);

                if (!string.IsNullOrEmpty(m_Template.TemplateOverridePath))
                    generator.AddTemplateOverrides(m_Template.TemplateOverridePath);

                context.typeCodeGenCache.Add(m_Template.TemplatePath + m_Template.TemplateOverridePath, generator);
            }

            generator = generator.Clone();

            // Prefix and Variable Replacements
            var reference = string.IsNullOrEmpty(parent)
                ? m_TypeInformation.FieldName : $"{parent}.{m_TypeInformation.FieldName}";
            var name = reference.Replace('.', '_');

            generator.Replacements.Add("GHOST_FIELD_NAME", $"{name}");
            generator.Replacements.Add("GHOST_FIELD_REFERENCE", $"{reference}");
            generator.Replacements.Add("GHOST_FIELD_TYPE_NAME", m_TypeInformation.Type.GetFieldTypeName());

            if (quantization > 0)
            {
                generator.Replacements.Add("GHOST_QUANTIZE_SCALE", quantization.ToString());
                generator.Replacements.Add("GHOST_DEQUANTIZE_SCALE",
                    $"{(1.0f / quantization).ToString(CultureInfo.InvariantCulture)}f");
            }
            float maxSmoothingDistSq = m_TypeInformation.Attribute.maxSmoothingDist * m_TypeInformation.Attribute.maxSmoothingDist;
            bool enableExtrapolation = m_TypeInformation.Attribute.smoothing == (uint)TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated;
            generator.Replacements.Add("GHOST_MAX_INTERPOLATION_DISTSQ", maxSmoothingDistSq.ToString(CultureInfo.InvariantCulture));

            // Skip fragments which have been overridden already
            for (int i = 0; i < k_OverridableFragments.GetLength(0); i++)
            {
                if (overrides == null || !overrides.ContainsKey(k_OverridableFragments[i, 0]))
                {
                    var fragment = k_OverridableFragments[i, 1];
                    var targetFragment = k_OverridableFragments[i, 0];
                    if (targetFragment == "GHOST_COPY_FROM_SNAPSHOT")
                    {
                        if (interpolate)
                        {
                            m_TargetGenerator.GenerateFragment(enableExtrapolation ? "GHOST_COPY_FROM_SNAPSHOT_ENABLE_EXTRAPOLATION" : "GHOST_COPY_FROM_SNAPSHOT_DISABLE_EXTRAPOLATION",
                                generator.Replacements, m_TargetGenerator, "GHOST_COPY_FROM_SNAPSHOT");
                            // The setup section is optional, so do not generate error if it is not present
                            generator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP", generator.Replacements, m_TargetGenerator,
                                "GHOST_COPY_FROM_SNAPSHOT", null, true);
                            // only generate max distance checks if clamp is enabled
                            if (maxSmoothingDistSq > 0)
                            {
                                generator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ", generator.Replacements, m_TargetGenerator,
                                    "GHOST_COPY_FROM_SNAPSHOT");
                                m_TargetGenerator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_CLAMP_MAX", generator.Replacements, m_TargetGenerator,
                                    "GHOST_COPY_FROM_SNAPSHOT");
                            }
                        }
                        else
                            fragment = "GHOST_COPY_FROM_SNAPSHOT";
                    }
                    generator.GenerateFragment(fragment, generator.Replacements, m_TargetGenerator,
                        targetFragment);
                }
            }

            // Imports
            var imports = generator.GetFragmentTemplate("GHOST_IMPORTS");
            if (!string.IsNullOrEmpty(imports))
            {
                foreach (var import in imports.Split('\n'))
                {
                    if (string.IsNullOrEmpty(import))
                        continue;
                    var matches = m_usingRegex.Matches(import);
                    if (matches.Count == 1)
                    {
                        context.imports.Add(matches[0].Value);
                    }
                }
            }

            ulong fieldHash = 0;
            fieldHash = Entities.TypeHash.CombineFNV1A64(fieldHash, Entities.TypeHash.FNV1A64(m_TypeInformation.Attribute.composite?1:0));
            fieldHash = Entities.TypeHash.CombineFNV1A64(fieldHash, Entities.TypeHash.FNV1A64(m_TypeInformation.Attribute.smoothing > 0 ? 1 : 0));
            fieldHash = Entities.TypeHash.CombineFNV1A64(fieldHash, (ulong)m_TypeInformation.Attribute.subtype);
            fieldHash = Entities.TypeHash.CombineFNV1A64(fieldHash, (ulong)m_TypeInformation.Attribute.quantization);
            context.FieldState.ghostfieldHash = Entities.TypeHash.CombineFNV1A64(context.FieldState.ghostfieldHash, fieldHash);

            if(m_TypeInformation.Type.Scope != null)
            {
                context.collectionAssemblies.Add(m_TypeInformation.Type.Scope.Name);
            }

            m_ActiveGenerator = generator;
        }

        public Dictionary<string, GhostCodeGen.FragmentData> GenerateCompositeOverrides(CodeGenerator.Context context, string parent = null)
        {
            var fragments = new Dictionary<string, GhostCodeGen.FragmentData>();
            if (m_Template == null || string.IsNullOrEmpty(m_Template.TemplateOverridePath))
                return null;

            var quantization = m_TypeInformation.Attribute.quantization;
            var interpolate = m_TypeInformation.Attribute.smoothing > 0;
            if (!context.typeCodeGenCache.TryGetValue(m_Template.TemplateOverridePath,
                out var generator))
            {
                generator = new GhostCodeGen(m_Template.TemplateOverridePath);
                context.typeCodeGenCache.Add(m_Template.TemplateOverridePath, generator);
            }

            generator = generator.Clone();

            // Prefix and Variable Replacements
            var reference = string.IsNullOrEmpty(parent)
                ? m_TypeInformation.FieldName : $"{parent}.{m_TypeInformation.FieldName}";
            var name = reference.Replace('.', '_');

            generator.Replacements.Add("GHOST_FIELD_NAME", $"{name}");
            generator.Replacements.Add("GHOST_FIELD_REFERENCE", $"{reference}");
            generator.Replacements.Add("GHOST_FIELD_TYPE_NAME", m_TypeInformation.Type.GetFieldTypeName());

            if (quantization > 0)
            {
                generator.Replacements.Add("GHOST_QUANTIZE_SCALE", quantization.ToString());
                generator.Replacements.Add("GHOST_DEQUANTIZE_SCALE",
                    $"{(1.0f / quantization).ToString(CultureInfo.InvariantCulture)}f");
            }
            float maxSmoothingDistSq = m_TypeInformation.Attribute.maxSmoothingDist * m_TypeInformation.Attribute.maxSmoothingDist;
            bool enableExtrapolation = m_TypeInformation.Attribute.smoothing == (uint)TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated;
            generator.Replacements.Add("GHOST_MAX_INTERPOLATION_DISTSQ", maxSmoothingDistSq.ToString(CultureInfo.InvariantCulture));

            // Type Info
            if (generator.GenerateFragment("GHOST_FIELD", generator.Replacements, m_TargetGenerator, null, null, true))
                fragments.Add("GHOST_FIELD", m_TargetGenerator.Fragments["__GHOST_FIELD__"]);
            // CopyToSnapshot
            if (generator.GenerateFragment("GHOST_COPY_TO_SNAPSHOT", generator.Replacements, m_TargetGenerator, null, null, true))
                fragments.Add("GHOST_COPY_TO_SNAPSHOT", m_TargetGenerator.Fragments["__GHOST_COPY_TO_SNAPSHOT__"]);

            // CopyFromSnapshot
            if (interpolate)
            {
                if (generator.HasFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE"))
                {
                    m_TargetGenerator.GenerateFragment(enableExtrapolation ? "GHOST_COPY_FROM_SNAPSHOT_ENABLE_EXTRAPOLATION" : "GHOST_COPY_FROM_SNAPSHOT_DISABLE_EXTRAPOLATION",
                        generator.Replacements, m_TargetGenerator, "GHOST_COPY_FROM_SNAPSHOT");
                    // The setup section is optional, so do not generate error if it is not present
                    generator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_SETUP", generator.Replacements, m_TargetGenerator,
                        "GHOST_COPY_FROM_SNAPSHOT", null, true);
                    // only generate max distance checks if clamp is enabled
                    if (maxSmoothingDistSq > 0)
                    {
                        generator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_DISTSQ", generator.Replacements, m_TargetGenerator,
                            "GHOST_COPY_FROM_SNAPSHOT");
                        m_TargetGenerator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE_CLAMP_MAX", generator.Replacements, m_TargetGenerator,
                            "GHOST_COPY_FROM_SNAPSHOT");
                    }
                    generator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE" ,
                        generator.Replacements, m_TargetGenerator, "GHOST_COPY_FROM_SNAPSHOT");
                    fragments.Add("GHOST_COPY_FROM_SNAPSHOT", generator.Fragments["__GHOST_COPY_FROM_SNAPSHOT__"]);
                    fragments.Add("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE", generator.Fragments["__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__"]);
                }
            }
            else
            {
                if (generator.GenerateFragment("GHOST_COPY_FROM_SNAPSHOT",
                    generator.Replacements, m_TargetGenerator, "GHOST_COPY_FROM_SNAPSHOT", null, true))
                {
                    fragments.Add("GHOST_COPY_FROM_SNAPSHOT", generator.Fragments["__GHOST_COPY_FROM_SNAPSHOT__"]);
                    fragments.Add("GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE", generator.Fragments["__GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE__"]);
                }
            }
            // RestoreFromBackup
            if (generator.GenerateFragment("GHOST_RESTORE_FROM_BACKUP", generator.Replacements, m_TargetGenerator, null, null, true))
                fragments.Add("GHOST_RESTORE_FROM_BACKUP", m_TargetGenerator.Fragments["__GHOST_RESTORE_FROM_BACKUP__"]);
            // PredictDelta
            if (generator.GenerateFragment("GHOST_PREDICT", generator.Replacements, m_TargetGenerator, null, null, true))
                fragments.Add("GHOST_PREDICT", m_TargetGenerator.Fragments["__GHOST_PREDICT__"]);

            // ReportPredictionError
            if (generator.GenerateFragment("GHOST_REPORT_PREDICTION_ERROR", generator.Replacements, m_TargetGenerator, null, null, true))
                fragments.Add("GHOST_REPORT_PREDICTION_ERROR", m_TargetGenerator.Fragments["__GHOST_REPORT_PREDICTION_ERROR__"]);
            // GetPredictionErrorName
            if (generator.GenerateFragment("GHOST_GET_PREDICTION_ERROR_NAME", generator.Replacements, m_TargetGenerator, null, null, true))
                fragments.Add("GHOST_GET_PREDICTION_ERROR_NAME", m_TargetGenerator.Fragments["__GHOST_GET_PREDICTION_ERROR_NAME__"]);

            ValidateOverridableFragments(generator.Fragments);

            m_ActiveGenerator = generator;
            return fragments;
        }

        void ValidateOverridableFragments(Dictionary<string, GhostCodeGen.FragmentData> fragments)
        {
            foreach (var fragment in fragments)
            {
                bool supported = false;
                foreach (var goodFrag in k_OverridableFragments)
                    if (fragment.Key.Contains(goodFrag))
                        supported = true;
                if (!supported)
                    UnityEngine.Debug.LogWarning($"{fragment.Key} is not overridable. Supported fragments are: {m_OverridableFragmentsList}");
            }
        }

        public void GenerateMasks(CodeGenerator.Context context, bool composite = false, int index = 0)
        {
            if (m_ActiveGenerator == null)
                return;

            var generator = m_ActiveGenerator;
            var target = m_TargetGenerator;
            var numFields = context.FieldState.numFields;
            var curChangeMask = context.FieldState.curChangeMask;

            // Mask Generation
            if (curChangeMask == 32)
            {
                generator.Replacements.Add("GHOST_CHANGE_MASK_BITS", numFields.ToString());
                target.GenerateFragment("GHOST_FLUSH_COMPONENT_CHANGE_MASK", generator.Replacements, target, "GHOST_CALCULATE_CHANGE_MASK");
                target.GenerateFragment("GHOST_REFRESH_CHANGE_MASK", generator.Replacements, target, "GHOST_READ");
                target.GenerateFragment("GHOST_REFRESH_CHANGE_MASK", generator.Replacements, target, "GHOST_WRITE");
                curChangeMask = 0;
            }
            generator.Replacements.Add("GHOST_MASK_INDEX", curChangeMask.ToString());
            generator.Replacements.Add("GHOST_MASK_BATCH", "");

            // Calculate Change Mask
            if ((!composite && curChangeMask == 0) || (composite && index == 0 && curChangeMask == 0))
                generator.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK_ZERO", generator.Replacements, target, "GHOST_CALCULATE_CHANGE_MASK");
            else
                generator.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK", generator.Replacements, target, "GHOST_CALCULATE_CHANGE_MASK");

            // Serialize
            generator.GenerateFragment("GHOST_WRITE", generator.Replacements, target);
            // Deserialize
            generator.GenerateFragment("GHOST_READ", generator.Replacements, target);

            context.FieldState.curChangeMask = curChangeMask;
        }

        public TypeGenerator(CodeGenerator.Context context)
        {
            var template = "Packages/com.unity.netcode/Editor/CodeGenTemplates/GhostComponentSerializer.cs";
            if (!context.typeCodeGenCache.TryGetValue(template, out var generator))
            {
                generator = new GhostCodeGen(template);

                context.typeCodeGenCache.Add(template, generator);
            }
            m_TargetGenerator = generator.Clone();
            foreach (var frag in k_OverridableFragments.Cast<string>())
            {
                if (!m_OverridableFragmentsList.Contains(frag))
                    m_OverridableFragmentsList += " " + frag;
            };
        }
        public TypeGenerator(CodeGenerator.Context context, TypeInformation information) : this(context)
        {
            m_TypeInformation = information;
        }

        public TypeGenerator(CodeGenerator.Context context, TypeInformation information, TypeTemplate template) : this(context, information)
        {
            m_Template = template;
        }

        public void AppendTarget(TypeGenerator typeGenerator)
        {
            m_TargetGenerator.Append(typeGenerator.m_TargetGenerator);
        }

        public void GenerateSerializer(CodeGenerator.Context context, Mono.Cecil.TypeDefinition generatorType,
            Mono.Cecil.TypeDefinition componentType, GhostComponentAttribute ghostAttributes)
        {
            var replacements = new Dictionary<string, string>();
            if (context.FieldState.curChangeMask > 0)
            {
                replacements.Add("GHOST_CHANGE_MASK_BITS", context.FieldState.curChangeMask.ToString());
                m_TargetGenerator.GenerateFragment("GHOST_FLUSH_FINAL_COMPONENT_CHANGE_MASK", replacements);
            }

            if (componentType.Namespace != null && componentType.Namespace != "")
            {
                context.imports.Add(componentType.Namespace);
            }
            foreach (var ns in context.imports)
            {
                replacements["GHOST_USING"] = CodeGenNamespaceUtils.GetValidNamespaceForType(context.generatedNs, ns);
                m_TargetGenerator.GenerateFragment("GHOST_USING_STATEMENT", replacements);
            }

            context.collectionAssemblies.Add(componentType.Module.Assembly.Name.Name);
            if (generatorType != componentType)
                context.collectionAssemblies.Add(generatorType.Module.Assembly.Name.Name);

            replacements.Clear();
            replacements.Add("GHOST_NAME", generatorType.FullName.Replace(".", "").Replace("/", "_"));
            replacements.Add("GHOST_NAMESPACE", context.generatedNs);
            replacements.Add("GHOST_COMPONENT_TYPE", componentType.FullName.Replace("/", "."));
            replacements.Add("GHOST_CHANGE_MASK_BITS", context.FieldState.numFields.ToString());
            replacements.Add("GHOST_FIELD_HASH", context.FieldState.ghostfieldHash.ToString());
            replacements.Add("GHOST_COMPONENT_EXCLUDE_FROM_COLLECTION_HASH", context.IsRuntimeAssembly ? "0" : "1");
            replacements.Add("GHOST_VARIANT_HASH", context.VariantHash.ToString());

            if (ghostAttributes != null)
            {
                if (ghostAttributes.OwnerPredictedSendType == GhostSendType.Interpolated || (ghostAttributes.PrefabType&GhostPrefabType.Client) == GhostPrefabType.InterpolatedClient)
                    replacements.Add("GHOST_SEND_MASK", "GhostComponentSerializer.SendMask.Interpolated");
                else if (ghostAttributes.OwnerPredictedSendType == GhostSendType.Predicted || (ghostAttributes.PrefabType&GhostPrefabType.Client) == GhostPrefabType.PredictedClient)
                    replacements.Add("GHOST_SEND_MASK", "GhostComponentSerializer.SendMask.Predicted");
                else if (ghostAttributes.PrefabType == GhostPrefabType.Server)
                    replacements.Add("GHOST_SEND_MASK", "GhostComponentSerializer.SendMask.None");
                else
                    replacements.Add("GHOST_SEND_MASK", "GhostComponentSerializer.SendMask.Interpolated | GhostComponentSerializer.SendMask.Predicted");
                replacements.Add("GHOST_SEND_CHILD_ENTITY", ghostAttributes.SendDataForChildEntity?"1":"0");

                var ownerType = ghostAttributes.OwnerSendType;
                if (componentType.IsICommandData() && (ghostAttributes.OwnerSendType & SendToOwnerType.SendToOwner) != 0)
                {
                    UnityEngine.Debug.LogError($"ICommandData {componentType.FullName} is configured to be sent to ghost owner. It will be ignored");
                    ownerType &= ~SendToOwnerType.SendToOwner;
                }
                replacements.Add("GHOST_SEND_OWNER", "SendToOwnerType." + ownerType);
            }
            else if(!componentType.IsICommandData())
            {
                replacements.Add("GHOST_SEND_MASK", "GhostComponentSerializer.SendMask.Interpolated | GhostComponentSerializer.SendMask.Predicted");
                replacements.Add("GHOST_SEND_OWNER", "SendToOwnerType.All");
                replacements.Add("GHOST_SEND_CHILD_ENTITY", "1");
            }
            else
            {
                replacements.Add("GHOST_SEND_MASK", "GhostComponentSerializer.SendMask.Predicted");
                replacements.Add("GHOST_SEND_OWNER", "SendToOwnerType.SendToNonOwner");
                replacements.Add("GHOST_SEND_CHILD_ENTITY", "0");
            }

            if (componentType.IsBufferElementData())
                m_TargetGenerator.GenerateFragment("GHOST_COPY_FROM_BUFFER", replacements, m_TargetGenerator, "COPY_FROM_SNAPSHOT_SETUP");
            else
                m_TargetGenerator.GenerateFragment("GHOST_COPY_FROM_COMPONENT", replacements, m_TargetGenerator, "COPY_FROM_SNAPSHOT_SETUP");


            if (m_TargetGenerator.Fragments["__GHOST_REPORT_PREDICTION_ERROR__"].Content.Length > 0)
            {
                m_TargetGenerator.GenerateFragment("GHOST_PREDICTION_ERROR_HEADER", replacements, m_TargetGenerator);
            }

            var serializerName = generatorType.FullName.Replace("/", "+") + "Serializer.cs";
            m_TargetGenerator.GenerateFile("", context.outputFolder, serializerName, replacements, context.batch);

            context.generatedTypes.Add(replacements["GHOST_NAME"]);
        }

        public override string ToString()
        {
            var debugInformation = m_TypeInformation.ToString();
            debugInformation += m_Template?.ToString();
            debugInformation += m_TargetGenerator?.ToString();
            return debugInformation;
        }

    }
}
