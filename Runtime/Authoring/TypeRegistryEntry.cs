namespace Unity.NetCode.Generators
{
    /// <summary>
    ///Type and templated configuration needed to generate serialization code
    /// </summary>
    public class TypeRegistryEntry
    {
        public string Type;
        public string Template;
        public string TemplateOverride;
#pragma warning disable 649
        public int SubType;
#pragma warning restore 649
        public SmoothingAction Smoothing;
        public bool Quantized;
        public bool SupportCommand;
        public bool Composite;
    }
}