using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Unity.NetCode.Generators
{
    /// <summary>
    /// TemplateRegistry import all the netcode templates files, validate and provide them to the generation systems.
    /// </summary>
    internal class TemplateRegistry
    {
        const string k_TemplateId = "#templateid:";
        public readonly Dictionary<TypeDescription, TypeTemplate> TypeTemplates = new (16);
        private readonly Dictionary<string, SourceText> allTemplates = new (16);
        private readonly IDiagnosticReporter diagnostic;

        public TemplateRegistry(IDiagnosticReporter diagnosticReporter)
        {
            diagnostic = diagnosticReporter;
        }

        public void AddTypeTemplates(IEnumerable<TypeRegistryEntry> types)
        {
            foreach (var entry in types)
            {
                AddTypeTemplateEntry(entry);
            }
        }

        private void AddTypeTemplateEntry(in TypeRegistryEntry entry)
        {
            var typeDescription = new TypeDescription
            {
                TypeFullName = entry.Type,
                Key = entry.Type,
                Attribute = new TypeAttribute
                {
                    subtype = entry.SubType,
                    quantization = entry.Quantized ? 1 : -1,
                    smoothing = (uint)entry.Smoothing,
                    aggregateChangeMask = entry.Composite
                }
            };
            var template = new TypeTemplate
            {
                SupportsQuantization = entry.Quantized,
                Composite = entry.Composite,
                SupportCommand = entry.SupportCommand,
                TemplatePath = entry.Template,
                TemplateOverridePath = entry.TemplateOverride
            };
            TypeTemplates.Add(typeDescription, template);
        }

        public string FormatAllKnownTypes()
        {
            return $"[{TypeTemplates.Count}:{string.Join(",", TypeTemplates.Keys)}]";
        }

        public string FormatAllKnownSubTypes()
        {
            var aggregate = string.Join(",", TypeTemplates
                .Where(x => x.Key.Attribute.subtype != 0)
                .Select(x => $"[{x.Key.Attribute.subtype}: {x.Key} at {x.Value.TemplatePath}]"));
            return $"[{TypeTemplates.Count}:{aggregate}]";
        }

        /// <summary>
        /// Parse the additional files passed to the compilation and add any custom template to the
        /// the internal map.
        /// Valid template are considered files with `.netcode.additionalfile` extension and which have a first
        /// line starting with `#templateid: TEMPLATE_ID
        /// </summary>
        /// <param name="additionalFiles"></param>
        /// <param name="typeRegistryEntries"></param>
        public void AddAdditionalTemplates(ImmutableArray<AdditionalText> additionalFiles,
            List<TypeRegistryEntry> typeRegistryEntries, HashSet<string> generatorTemplates)
        {
            var missingUserTypes = new List<TypeRegistryEntry>(typeRegistryEntries);
            var templateIds = new Dictionary<string, AdditionalText>(additionalFiles.Length);

            foreach (var additionalText in additionalFiles)
            {
                var isNetCodeTemplate = additionalText.Path.EndsWith(NetCodeSourceGenerator.NETCODE_ADDITIONAL_FILE, StringComparison.Ordinal);
                if (isNetCodeTemplate)
                {
                    var text = additionalText.GetText();
                    if (text == null || text.Lines.Count == 0)
                    {
                        diagnostic.LogError($"All NetCode AdditionalFiles must be valid Templates, but '{additionalText.Path}' does not contain any text!");
                        continue;
                    }

                    var line = text.Lines[0].ToString();
                    if (!line.StartsWith(k_TemplateId, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostic.LogError($"All NetCode AdditionalFiles must be valid Templates, but '{additionalText.Path}' does not start with a correct Template definition (a '#templateid:MyNamespace.MyType' line).");
                        continue;
                    }

                    var templateId = line.Substring(k_TemplateId.Length).Trim();
                    if (string.IsNullOrWhiteSpace(templateId))
                    {
                        diagnostic.LogError($"NetCode AdditionalFile '{additionalText.Path}' is a valid Template, but the `{k_TemplateId}` is empty!");
                        continue;
                    }
                    templateIds.Add(templateId, additionalText);
                }
                else
                {
                    diagnostic.LogDebug($"Ignoring AdditionalFile '{additionalText.Path}' as it is not a NetCode type!");
                }
            }

            foreach (var generatorTemplate in generatorTemplates)
            {
                if (!templateIds.TryGetValue(generatorTemplate, out var file))
                    diagnostic.LogError($"Missing internal Netcode package template {generatorTemplate}!");
                else
                {
                    templateIds.Remove(generatorTemplate);
                    allTemplates.Add(generatorTemplate, file.GetText());
                }
            }
            var unusedTemplates = new Dictionary<string, AdditionalText>(templateIds);
            // Ensure all of the `TypeRegistryEntry`s are linked to additional files templates
            foreach (var typeRegistryEntry in typeRegistryEntries)
            {
                if (!string.IsNullOrEmpty(typeRegistryEntry.Template))
                {
                    if(!templateIds.TryGetValue(typeRegistryEntry.Template, out var file))
                    {
                        diagnostic.LogError($"Unable to find the `Template` associated with '{typeRegistryEntry}'. There are {additionalFiles.Length} additionalFiles:[{string.Join(",", additionalFiles.Select(x => x.Path))}]!");
                    }
                    else
                    {
                        unusedTemplates.Remove(typeRegistryEntry.Template);
                        if(!allTemplates.ContainsKey(typeRegistryEntry.Template))
                            allTemplates.Add(typeRegistryEntry.Template, file.GetText());}
                }

                if (!string.IsNullOrEmpty(typeRegistryEntry.TemplateOverride))
                {
                    if(!templateIds.TryGetValue(typeRegistryEntry.TemplateOverride, out var file))
                    {
                        diagnostic.LogError($"Unable to find the `TemplateOverride` associated with '{typeRegistryEntry}'. There are {additionalFiles.Length} additionalFiles:[{string.Join(",", additionalFiles.Select(x => x.Path))}]!");
                    }
                    else
                    {
                        unusedTemplates.Remove(typeRegistryEntry.TemplateOverride);
                        if(!allTemplates.ContainsKey(typeRegistryEntry.TemplateOverride))
                            allTemplates.Add(typeRegistryEntry.TemplateOverride, file.GetText());}
                }
            }

            // Ensure there are no additional files not matched by any template. This is more a warning than an error.
            foreach(var missingMatch in unusedTemplates)
                diagnostic.LogError($"NetCode AdditionalFile '{missingMatch.Value.Path}' (named '{missingMatch.Key}') is a valid Template, but it cannot be matched with any Netcode package or UserDefinedTemplate template definition (probably a typo). Known user templates:[{GetKnownCustomUserTemplates()}].");

            string GetKnownCustomUserTemplates()
            {
                return string.Join(",", typeRegistryEntries.Select(x => $"{x.Type}[{x.Template}]"));
            }
        }


        /// <summary>
        /// Get the template data for the given template identifier.
        /// </summary>
        /// <param name="resourcePath"></param>
        /// <returns>
        /// The System.IO.Stream from which reading the template content.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// If the template path/id cannot be resolved
        /// </exception>
        public string GetTemplateData(string resourcePath)
        {
            if (allTemplates.TryGetValue(resourcePath, out var additionalText))
                return additionalText.ToString();

            throw new FileNotFoundException($"Cannot find template with resource id '{resourcePath}'! CustomTemplates:[{string.Join(",", allTemplates)}]");
        }

        private Stream LoadTemplateFromEmbeddedResources(string resourcePath)
        {
            //The templates in the resources begin with the namespace
            var thisAssembly = Assembly.GetExecutingAssembly();
            return thisAssembly.GetManifestResourceStream(resourcePath);
        }
    }
}
