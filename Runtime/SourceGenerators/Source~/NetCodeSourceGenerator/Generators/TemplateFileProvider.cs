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
    /// TemplateFileProvider cache netcode templates and provide them to the code generation system on demand.
    /// Templates are extracted from different sources:
    /// - templates embedded in the generator dll (the default ones)
    /// - templates that came from additional files (2021+)
    /// - diredtly from disk, using full/relative path (legacy, 2020.X).
    /// </summary>
    internal class TemplateFileProvider : CodeGenerator.ITemplateFileProvider
    {
        readonly private HashSet<string> defaultTemplates;
        readonly private Dictionary<string, SourceText> customTemplates;
        readonly private IDiagnosticReporter diagnostic;
        public PathResolver pathResolver { get; set; }

        public TemplateFileProvider(IDiagnosticReporter diagnosticReporter)
        {
            defaultTemplates = new HashSet<string>();
            customTemplates = new Dictionary<string, SourceText>(256);
            diagnostic = diagnosticReporter;
            pathResolver = null;

            var thisAssembly = Assembly.GetExecutingAssembly();
            var resourceNames = thisAssembly.GetManifestResourceNames();
            foreach (var resource in resourceNames)
                defaultTemplates.Add(resource);
        }

        /// <summary>
        /// Parse the additional files passed to the compilation and add any custom template to the
        /// the internal map.
        /// Valid template are considered files with `.netcode.additionalfile` extension and which have a first
        /// line starting with `#templateid: TEMPLATE_ID
        /// </summary>
        /// <param name="additionalFiles"></param>
        public void AddAdditionalTemplates(ImmutableArray<AdditionalText> additionalFiles)
        {
            foreach (var additionalText in additionalFiles.Where(f => f.Path.EndsWith(NetCodeSourceGenerator.NETCODE_ADDITIONAL_FILE)))
            {
                var text = additionalText.GetText();
                if (text == null || text.Lines.Count == 0)
                    continue;
                var line = text.Lines[0].ToString();
                if (!line.ToLower().StartsWith("#templateid:"))
                {
                    diagnostic.LogError($"Template {additionalText.Path} does not contains a template id declaration. Custom templates must start with a #TEMPLATEID: MYTEMPLATEID line.");
                    continue;
                }
                var templateID = line.Substring("#templateid:".Length).Trim();
                customTemplates.Add(templateID, additionalText.GetText());
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
            if (customTemplates.TryGetValue(resourcePath, out var additionalText))
                return additionalText.ToString();

            if (defaultTemplates.Contains(resourcePath))
                return SourceText.From(LoadTemplateFromEmbeddedResources(resourcePath)).ToString();
            ;
            if (pathResolver != null)
            {
                resourcePath = pathResolver.ResolvePath(resourcePath);
                return File.ReadAllText(resourcePath);
            }
            throw new FileNotFoundException($"Cannot fine template with resource id {resourcePath}");
        }

        private Stream LoadTemplateFromEmbeddedResources(string resourcePath)
        {
            //The templates in the resources begin with the namespace
            var thisAssembly = Assembly.GetExecutingAssembly();
            return thisAssembly.GetManifestResourceStream(resourcePath);
        }
    }
}
