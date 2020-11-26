using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Unity.NetCode
{
    /// <summary>
    /// Used to alter the serialization behavior of struct field.
    /// </summary>
    public class GhostFieldModifier
    {
        public string name;
        public GhostFieldAttribute attribute;
    }
    /// <summary>
    /// A struct wich contains a set of new attributes that will alter the serialization behavior
    /// of a component.
    /// </summary>
    public class GhostComponentModifier
    {
        //The type qualified name (namespace + typename)
        public string typeFullName;
        public GhostComponentAttribute attribute;
        public GhostFieldModifier[] fields;
        public int entityIndex;
    }

    internal struct VariantEntry
    {
        public GhostComponentVariationAttribute Attribute;
        public System.Type VariantType;
    }

    internal static class GhostAuthoringModifiers
    {
        public static Dictionary<string, GhostComponentModifier> GhostDefaultOverrides;
        public static HashSet<string> AssembliesDefaultOverrides;
        public static Dictionary<string, List<VariantEntry>> VariantsCache;

        static GhostAuthoringModifiers()
        {
            InitDefaultOverrides();
            InitVariantCache();
        }

        public static void InitDefaultOverrides()
        {
            GhostAuthoringModifiers.GhostDefaultOverrides = new Dictionary<string, GhostComponentModifier>();
            GhostAuthoringModifiers.AssembliesDefaultOverrides = new HashSet<string>(new []{
                "Unity.NetCode",
                "Unity.Transforms",
            });

            var comp = new GhostComponentModifier
            {
                typeFullName = "Unity.Transforms.Translation",
                attribute = new GhostComponentAttribute{PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.All, SendDataForChildEntity = false},
                fields = new[]
                {
                    new GhostFieldModifier
                    {
                        name = "Value",
                        attribute = new GhostFieldAttribute{Quantization = 100, Smoothing=SmoothingAction.InterpolateAndExtrapolate}
                    }
                },
                entityIndex = 0
            };
            GhostAuthoringModifiers.GhostDefaultOverrides.Add(comp.typeFullName, comp);
            comp = new GhostComponentModifier
            {
                typeFullName = "Unity.Transforms.Rotation",
                attribute = new GhostComponentAttribute{PrefabType = GhostPrefabType.All, OwnerPredictedSendType = GhostSendType.All, SendDataForChildEntity = false},
                fields = new[]
                {
                    new GhostFieldModifier
                    {
                        name = "Value",
                        attribute = new GhostFieldAttribute{Quantization = 1000, Smoothing=SmoothingAction.InterpolateAndExtrapolate}
                    }
                },
                entityIndex = 0
            };
            GhostAuthoringModifiers.GhostDefaultOverrides.Add(comp.typeFullName, comp);
        }

        public static void InitVariantCache()
        {
            VariantsCache = new Dictionary<string, List<VariantEntry>>();
            //Traverse all pertinent assemblies (netcode or netcode referecens) and fetch any struct with GhostComponentVariationAttribute
            var assemblies= System.AppDomain.CurrentDomain.GetAssemblies().Where(a =>
            {
                return a.GetName().Name.StartsWith("Unity.NetCode") ||
                       a.GetReferencedAssemblies().Any(r => r.Name == "Unity.NetCode");
            });
            foreach (var a in assemblies)
            {
                foreach (var t in a.GetExportedTypes())
                {
                    var variant = t.GetCustomAttribute<GhostComponentVariationAttribute>();
                    if (variant == null)
                        continue;
                    variant.VariantHash = GhostComponentVariationAttribute.ComputeVariantHash(t, variant);
                    if (string.IsNullOrEmpty(variant.DisplayName))
                        variant.DisplayName = t.FullName;
                    if (!VariantsCache.TryGetValue(variant.ComponentType.FullName, out var list))
                    {
                        list = new List<VariantEntry>();
                        //Add a default entry
                        list.Add(new VariantEntry
                            {
                                Attribute = new GhostComponentVariationAttribute(variant.ComponentType),
                                VariantType = null
                            }
                        );
                        VariantsCache.Add(variant.ComponentType.FullName, list);
                    }
                    list.Add(new VariantEntry
                    {
                        Attribute = variant,
                        VariantType = t
                    });
                }
            }
        }
    }
}