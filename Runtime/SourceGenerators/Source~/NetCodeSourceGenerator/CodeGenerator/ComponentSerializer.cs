using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Unity.NetCode.Generators
{
    //ComponentGenerator instances are created by CodeGenerator. The class itseld is not threadsafe but since every
    //SourceGenerator has its own Context it is safe use. Avoid to use shared static variables or state here and verify
    //that in case you need, they are immutable or thread safe.
    //The GhostCodeGen is per context so no special handling is necessary
    internal class ComponentSerializer
    {
        private readonly TypeInformation m_TypeInformation;
        public GhostCodeGen m_TargetGenerator;
        private GhostCodeGen m_ActiveGenerator;
        private readonly TypeTemplate m_Template;
        //The Regex is immutable and threadsafe. The match collection can be used by a single thread only
        private static Regex m_usingRegex = new Regex("(\\w+)(?=;)");
        public TypeInformation TypeInformation => m_TypeInformation;

        private string[,] k_OverridableFragments =
        {
            // fragment + alernative fragment in case of interpolation
            {"GHOST_FIELD", "GHOST_FIELD"},
            {"GHOST_AGGREGATE_WRITE", "GHOST_AGGREGATE_WRITE"},
            {"GHOST_COPY_TO_SNAPSHOT", "GHOST_COPY_TO_SNAPSHOT"},
            {"GHOST_COPY_FROM_SNAPSHOT", "GHOST_COPY_FROM_SNAPSHOT_INTERPOLATE"},
            {"GHOST_RESTORE_FROM_BACKUP", "GHOST_RESTORE_FROM_BACKUP"},
            {"GHOST_PREDICT", "GHOST_PREDICT"},
            {"GHOST_REPORT_PREDICTION_ERROR", "GHOST_REPORT_PREDICTION_ERROR"},
            {"GHOST_GET_PREDICTION_ERROR_NAME", "GHOST_GET_PREDICTION_ERROR_NAME"},
        };

        private string m_OverridableFragmentsList = "";

        static void SnapshotAndFieldReferencesName(string rootPath, TypeInformation typeInformation, GhostCodeGen generator)
        {
            string reference;
            string snapshotName;
            if (string.IsNullOrEmpty(typeInformation.FieldPath))
                reference = $"{typeInformation.FieldName}";
            else
                reference = $"{typeInformation.FieldPath}.{typeInformation.FieldName}";
            reference = reference.Trim();

            if (string.IsNullOrEmpty(typeInformation.SnapshotFieldName))
                snapshotName = reference.Replace('.', '_');
            else
                snapshotName = typeInformation.SnapshotFieldName;

            var fieldAccessor = string.IsNullOrEmpty(reference) ? "" : ".";
            generator.Replacements.Add("GHOST_FIELD_NAME", snapshotName);
            generator.Replacements.Add("GHOST_FIELD_PATH", $"{rootPath}{fieldAccessor}{snapshotName}");
            generator.Replacements.Add("GHOST_FIELD_REFERENCE", $"{rootPath}{fieldAccessor}{reference}");
            generator.Replacements.Add("GHOST_FIELD_TYPE_NAME", typeInformation.FieldTypeName);
        }

        public void GenerateFields(CodeGenerator.Context context,
            string fieldPath = null,
            Dictionary<string, GhostCodeGen.FragmentData> overrides = null,
            Dictionary<string, string> replacements = null)
        {
            if (m_Template == null)
                return;

            var quantization = m_TypeInformation.Attribute.quantization;
            var interpolate = m_TypeInformation.Attribute.smoothing > 0;
            var generator = context.codeGenCache.GetTemplateWithOverride(m_Template.TemplatePath, m_Template.TemplateOverridePath);
            generator = generator.Clone();
            SnapshotAndFieldReferencesName(fieldPath, m_TypeInformation, generator);
            if (quantization > 0)
            {
                generator.Replacements.Add("GHOST_QUANTIZE_SCALE", quantization.ToString());
                generator.Replacements.Add("GHOST_DEQUANTIZE_SCALE",
                    $"{(1.0f / quantization).ToString(CultureInfo.InvariantCulture)}f");
            }
            float maxSmoothingDistSq = m_TypeInformation.Attribute.maxSmoothingDist * m_TypeInformation.Attribute.maxSmoothingDist;
            bool enableExtrapolation = m_TypeInformation.Attribute.smoothing == (uint)TypeAttribute.AttributeFlags.InterpolatedAndExtrapolated;
            generator.Replacements.Add("GHOST_MAX_INTERPOLATION_DISTSQ", maxSmoothingDistSq.ToString(CultureInfo.InvariantCulture));
            // add any custom replacement but can't override internals. As such we use Add here to control that none of the current
            // replacement can be overridden
            if (replacements != null)
            {
                foreach (var replacement in replacements)
                    generator.Replacements.Add(replacement.Key, replacement.Value);
            }
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
            fieldHash = Utilities.TypeHash.CombineFNV1A64(fieldHash, Utilities.TypeHash.FNV1A64(m_TypeInformation.Attribute.aggregateChangeMask?1:0));
            fieldHash = Utilities.TypeHash.CombineFNV1A64(fieldHash, Utilities.TypeHash.FNV1A64(m_TypeInformation.Attribute.subtype));
            fieldHash = Utilities.TypeHash.CombineFNV1A64(fieldHash, (ulong)m_TypeInformation.Attribute.quantization);
            fieldHash = Utilities.TypeHash.CombineFNV1A64(fieldHash, Utilities.TypeHash.FNV1A64((int)m_TypeInformation.Attribute.smoothing));
            context.ghostFieldHash = Utilities.TypeHash.CombineFNV1A64(context.ghostFieldHash, fieldHash);
            m_ActiveGenerator = generator;
        }

        internal Dictionary<string, GhostCodeGen.FragmentData> GenerateCompositeOverrides(CodeGenerator.Context context,
            string rootPath = null)
        {
            var fragments = new Dictionary<string, GhostCodeGen.FragmentData>();
            if (m_Template == null || string.IsNullOrEmpty(m_Template.TemplateOverridePath))
                return null;

            var quantization = m_TypeInformation.Attribute.quantization;
            var interpolate = m_TypeInformation.Attribute.smoothing > 0;
            var generator = context.codeGenCache.GetTemplate(m_Template.TemplateOverridePath);
            generator = generator.Clone();

            // Prefix and Variable Replacements
            SnapshotAndFieldReferencesName(rootPath, m_TypeInformation, generator);
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

            ValidateOverridableFragments(context, generator.Fragments);

            m_ActiveGenerator = generator;
            return fragments;
        }

        private void ValidateOverridableFragments(CodeGenerator.Context context, Dictionary<string, GhostCodeGen.FragmentData> fragments)
        {
            foreach (var fragment in fragments)
            {
                bool supported = false;
                foreach (var goodFrag in k_OverridableFragments)
                    if (fragment.Key.Contains(goodFrag))
                        supported = true;
                if (!supported)
                    context.diagnostic.LogWarning($"{fragment.Key} is not overridable. Supported fragments are: {m_OverridableFragmentsList}");
            }
        }

        public int GenerateMasks(CodeGenerator.Context context, int fieldChangeMaskBits, bool aggregateMask = false, int fieldIndex = 0)
        {
            if (m_ActiveGenerator == null)
                return 0;

            var changeMaskFrag = "GHOST_CALCULATE_CHANGE_MASK";
            var changeMaskFragZero = "GHOST_CALCULATE_CHANGE_MASK_ZERO";
            var ghostWriteFrag = "GHOST_WRITE";
            var ghostReadFrag = "GHOST_READ";
            var generator = m_ActiveGenerator;
            var target = m_TargetGenerator;

            if (fieldChangeMaskBits > 1 && aggregateMask)
                fieldChangeMaskBits = 1;

            if (context.curChangeMaskBits == 32 || (fieldChangeMaskBits > 1 && context.curChangeMaskBits + fieldChangeMaskBits > 32))
            {
                generator.Replacements.Add("GHOST_CURRENT_MASK_BITS", (context.changeMaskBitCount - context.curChangeMaskBits).ToString());
                generator.Replacements.Add("GHOST_CHANGE_MASK_BITS", context.changeMaskBitCount.ToString());
                target.GenerateFragment("GHOST_FLUSH_COMPONENT_CHANGE_MASK", generator.Replacements, target, "GHOST_CALCULATE_CHANGE_MASK");
                target.GenerateFragment("GHOST_FLUSH_COMPONENT_CHANGE_MASK", generator.Replacements, target, "GHOST_WRITE_COMBINED");
                target.GenerateFragment("GHOST_REFRESH_CHANGE_MASK", generator.Replacements, target, "GHOST_READ");
                target.GenerateFragment("GHOST_REFRESH_CHANGE_MASK", generator.Replacements, target, "GHOST_WRITE");
                context.curChangeMaskBits = 0;
            }
            generator.Replacements["GHOST_MASK_INDEX"] = context.curChangeMaskBits.ToString();
            generator.Replacements["GHOST_CHANGE_MASK_BITS"] = context.changeMaskBitCount.ToString();
            generator.Replacements["GHOST_CURRENT_MASK_BITS"] = (context.changeMaskBitCount - context.curChangeMaskBits).ToString();

            // TODO: Remove the special handling for zero, and just always do `mask |= value != 0 ? 1 << bit : 0;`
            var createFragAsZero = context.curChangeMaskBits == 0 && (!aggregateMask || fieldIndex == 0) && !context.forceComposite;
            context.diagnostic.LogDebug($"\tGenerateMasks(context[curChangeMaskBits:{context.curChangeMaskBits}, changeMaskBitCount:{context.changeMaskBitCount}, forceComposite:{context.forceComposite}], fieldChangeMaskBits:{fieldChangeMaskBits}, aggregateMask:{aggregateMask}, fieldIndex:{fieldIndex}) createFragAsZero:{createFragAsZero} for {context.root.FieldTypeName}.{generator.Replacements["GHOST_FIELD_NAME"]}!");

            if (createFragAsZero)
            {
                generator.GenerateFragment(changeMaskFragZero, generator.Replacements, target, "GHOST_CALCULATE_CHANGE_MASK");
                if (!generator.HasFragment("GHOST_WRITE_COMBINED"))
                    generator.GenerateFragment(changeMaskFragZero, generator.Replacements, target, "GHOST_WRITE_COMBINED");
            }
            else
            {
                generator.GenerateFragment(changeMaskFrag, generator.Replacements, target, "GHOST_CALCULATE_CHANGE_MASK");
                if (!generator.HasFragment("GHOST_WRITE_COMBINED"))
                    generator.GenerateFragment(changeMaskFrag, generator.Replacements, target, "GHOST_WRITE_COMBINED");
            }
            // Serialize
            generator.GenerateFragment(ghostWriteFrag, generator.Replacements, target, "GHOST_WRITE");
            var targetFrag = aggregateMask ? "GHOST_AGGREGATE_WRITE" : "GHOST_WRITE_COMBINED";
            if (generator.HasFragment("GHOST_WRITE_COMBINED"))
                generator.GenerateFragment("GHOST_WRITE_COMBINED", generator.Replacements, target, targetFrag);
            else
                generator.GenerateFragment(ghostWriteFrag, generator.Replacements, target, targetFrag);
            // Deserialize
            generator.GenerateFragment(ghostReadFrag, generator.Replacements, target, "GHOST_READ");
            return fieldChangeMaskBits;
        }

        public ComponentSerializer(CodeGenerator.Context context)
        {
            var generator = context.codeGenCache.GetTemplate(CodeGenerator.ComponentSerializer);
            m_TargetGenerator = generator.Clone();
            foreach (var frag in k_OverridableFragments.Cast<string>())
            {
                if (!m_OverridableFragmentsList.Contains(frag))
                    m_OverridableFragmentsList += " " + frag;
            }
        }
        public ComponentSerializer(CodeGenerator.Context context, TypeInformation information) : this(context)
        {
            m_TypeInformation = information;
        }

        public ComponentSerializer(CodeGenerator.Context context, TypeInformation information, TypeTemplate template) : this(context, information)
        {
            m_Template = template;
        }

        public void AppendTarget(ComponentSerializer componentSerializer)
        {
            m_TargetGenerator.Append(componentSerializer.m_TargetGenerator);
        }

        public void GenerateSerializer(CodeGenerator.Context context, TypeInformation type)
        {
            var replacements = new Dictionary<string, string>(32);
            if (type.GhostFields.Count > 0)
            {
                m_TargetGenerator.GenerateFragment("GHOST_COMPONENT_HAS_FIELDS", replacements);
            }
            if (type.ComponentType == ComponentType.Buffer || type.ComponentType == ComponentType.CommandData)
            {
                m_TargetGenerator.GenerateFragment("GHOST_COMPONENT_IS_BUFFER", replacements);
            }
            if (context.changeMaskBitCount > 0)
            {
                m_TargetGenerator.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK_SETUP", replacements, m_TargetGenerator,
                    "GHOST_CALCULATE_CHANGE_MASK", prepend:true);
                m_TargetGenerator.GenerateFragment("GHOST_CALCULATE_CHANGE_MASK_SETUP", replacements, m_TargetGenerator,
                    "GHOST_WRITE_COMBINED", prepend:true);
                if(context.curChangeMaskBits > 0)
                {
                    replacements.Add("GHOST_CURRENT_MASK_BITS", context.curChangeMaskBits.ToString());
                    replacements.Add("GHOST_CHANGE_MASK_BITS", (context.changeMaskBitCount - context.curChangeMaskBits).ToString());
                    m_TargetGenerator.GenerateFragment("GHOST_FLUSH_FINAL_COMPONENT_CHANGE_MASK", replacements);
                    m_TargetGenerator.AppendFragment("GHOST_FLUSH_FINAL_COMPONENT_CHANGE_MASK", m_TargetGenerator, "GHOST_WRITE_COMBINED");
                }
            }

            if (!string.IsNullOrEmpty(type.Namespace))
                context.imports.Add(type.Namespace);
            foreach (var ns in context.imports)
            {
                replacements["GHOST_USING"] = CodeGenerator.GetValidNamespaceForType(context.generatedNs, ns);
                m_TargetGenerator.GenerateFragment("GHOST_USING_STATEMENT", replacements);
            }

            replacements.Clear();

            //getting the right fullyqualified typename to use for hash calculation.
            //for non-generic type, it is namespace.[containtype].typename
            //for generic type, it is namespace.containtype.typename`N[[fullyqualifiedname, assembly],[..]]
            //Now, the assembly qualification is really annoying but this is how Entities at runtime calcule type hashes.
            //so we need to do the same here (because we are not using Type or passing the Type at registration.. that would simplify everything)
            string fullyQualifiedVariantName;
            if (string.IsNullOrWhiteSpace(context.variantTypeFullName))
            {
                fullyQualifiedVariantName = Roslyn.Extensions.GetMetadataQualifiedName(type.Symbol as INamedTypeSymbol);
            }
            else
            {
                fullyQualifiedVariantName = context.variantTypeFullName;
            }


            if (context.variantHash == 0)
            {
                context.variantHash = Helpers.ComputeVariantHash(type.Symbol, type.Symbol);
                context.diagnostic.LogDebug($"{type.TypeFullName} had its type hash reset, so recalculating it to {context.variantHash}!");
            }

            //Calculating the hash for the generic type is a little trickier.
            //At runtime the CLR give names like XXX`1[FullName
            replacements.Add("GHOST_NAME", context.generatorName.Replace(".", "").Replace('+', '_'));
            replacements.Add("GHOST_NAMESPACE", context.generatedNs);
            replacements.Add("GHOST_COMPONENT_TYPE", type.TypeFullName.Replace('+', '.'));
            replacements.Add("GHOST_VARIANT_TYPE", fullyQualifiedVariantName);
            replacements.Add("GHOST_CHANGE_MASK_BITS", context.changeMaskBitCount.ToString());
            replacements.Add("GHOST_FIELD_HASH", context.ghostFieldHash.ToString());
            replacements.Add("GHOST_VARIANT_HASH", context.variantHash.ToString());
            replacements.Add("GHOST_SERIALIZES_ENABLED_BIT", type.ShouldSerializeEnabledBit ? "1" : "0");

            if(type.GhostAttribute != null)
            {
                replacements.Add("GHOST_PREFAB_TYPE", $"GhostPrefabType.{type.GhostAttribute.PrefabType.ToString("G").Replace(",", "| GhostPrefabType.")}");
                if ((type.GhostAttribute.PrefabType&GhostPrefabType.Client) == GhostPrefabType.InterpolatedClient)
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.OnlyInterpolatedClients");
                else if((type.GhostAttribute.PrefabType&GhostPrefabType.Client) == GhostPrefabType.PredictedClient)
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.OnlyPredictedClients");
                else if (type.GhostAttribute.PrefabType == GhostPrefabType.Server)
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.DontSend");
                else if (type.GhostAttribute.SendTypeOptimization == GhostSendType.OnlyInterpolatedClients)
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.OnlyInterpolatedClients");
                else if (type.GhostAttribute.SendTypeOptimization == GhostSendType.OnlyPredictedClients)
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.OnlyPredictedClients");
                else if(type.GhostAttribute.SendTypeOptimization == GhostSendType.AllClients)
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.AllClients");
                else
                    replacements.Add("GHOST_SEND_MASK", "GhostSendType.DontSend");

                var ownerType = type.GhostAttribute.OwnerSendType;
                if (type.ComponentType == ComponentType.CommandData && (ownerType & SendToOwnerType.SendToOwner) != 0)
                {
                    context.diagnostic.LogWarning($"ICommandData {type.TypeFullName} is configured to be sent to ghost owner. It will be ignored");
                    ownerType &= ~SendToOwnerType.SendToOwner;
                }
                replacements.Add("GHOST_SEND_OWNER", "SendToOwnerType." + ownerType);
            }
            else if(type.ComponentType != ComponentType.CommandData)
            {
                replacements.Add("GHOST_PREFAB_TYPE", "GhostPrefabType.All");
                replacements.Add("GHOST_SEND_MASK", "GhostSendType.AllClients");
                replacements.Add("GHOST_SEND_OWNER", "SendToOwnerType.All");
                replacements.Add("GHOST_SEND_CHILD_ENTITY", "0");
            }
            else
            {
                replacements.Add("GHOST_PREFAB_TYPE", "GhostPrefabType.All");
                replacements.Add("GHOST_SEND_MASK", "GhostSendType.OnlyPredictedClients");
                replacements.Add("GHOST_SEND_OWNER", "SendToOwnerType.SendToNonOwner");
                replacements.Add("GHOST_SEND_CHILD_ENTITY", "0");
            }

            if (m_TargetGenerator.Fragments["__GHOST_REPORT_PREDICTION_ERROR__"].Content.Length > 0)
                m_TargetGenerator.GenerateFragment("GHOST_PREDICTION_ERROR_HEADER", replacements, m_TargetGenerator);

            var serializerName = context.generatedFilePrefix + "Serializer.cs";
            m_TargetGenerator.GenerateFile(serializerName, replacements, context.batch);

            context.generatedGhosts.Add($"global::{context.generatedNs}.{replacements["GHOST_NAME"]}");
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
