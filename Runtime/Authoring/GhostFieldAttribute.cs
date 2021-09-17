using System;

namespace Unity.NetCode
{
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property)]
    public class GhostFieldAttribute : Attribute
    {
        /// <summary>
        /// Floating point numbers will be multiplied by this number and rounded to an integer, enabling better delta-compression via huffman encoding.
        /// Specifying a Quantization is mandatory for floating point numbers and not supported for integer numbers.
        /// To send a floating point number unquantized, use 0.
        /// Examples:
        /// Quantization=0 implies full precision.
        /// Quantization=1 implies precision of 1f (i.e. round float values to integers).
        /// Quantization=2 implies precision of 0.5f.
        /// Quantization=10 implies precision of 0.1f.
        /// Quantization=20 implies precision of 0.05f.
        /// Quantization=1000 implies precision of 0.001f.
        /// </summary>
        public int Quantization { get; set; }

        /// <summary>
        /// Only applicable on GhostFieldAttributes applied to a non primitive struct containing multiple fields.
        /// If this value is not set (a.k.a. false, the default), a 'change bit' will be included 'per field, for every field inside the nested struct'.
        /// There will be no 'change bit' for the struct itself.
        /// I.e. If a single field inside the sub-struct changes, only that fields 'change bit' will be set.
        /// Otherwise (if this Composite bool is set, a.k.a. true), we instead use a single 'change bit' for 'the entire nested struct'.
        /// I.e. If any fields inside the sub-struct change, the single 'change bit' for the entire struct will be set.
        /// Check the Serialize/Deserialize code-generated methods in Library\NetCodeGenerated_Backup for examples.
        /// </summary>
        public bool Composite { get; set; }

        /// <summary>
        /// <inheritdoc cref="SmoothingAction"/>
        /// Default is <see cref="SmoothingAction.Clamp"/>.
        /// </summary>
        public SmoothingAction Smoothing { get; set; }

        /// <summary>Allows you to specify a custom serializer for this GhostField using the <see cref="GhostFieldSubType"/> API.</summary>
        /// <inheritdoc cref="GhostFieldSubType"/>
        public int SubType { get; set; }
        /// <summary>
        /// Default true. If unset (false), instructs code-generation to not include this field in the serialization data.
        /// I.e. Do not replicate this field.
        /// This is particularly useful for non primitive members (like structs), which will have all fields serialized by default.
        /// </summary>
        public bool SendData { get; set; }

        /// <summary>
        /// The maximum distance between two snapshots for which smoothing will be applied.
        /// If the value changes more than this between two received snapshots the smoothing
        /// action will not be performed.
        /// </summary>
        /// <remarks>
        /// For quaternions the value specified should be sin(theta / 2) - where theta is the maximum angle
        /// you want to apply smoothing for.
        /// </remarks>
        public float MaxSmoothingDistance { get; set; }

        public GhostFieldAttribute()
        {
            Quantization = -1;
            Smoothing = SmoothingAction.Clamp;
            Composite = false;
            SubType = GhostFieldSubType.None;
            SendData = true;
            MaxSmoothingDistance = 0;
        }
    }

    /// <summary>
    /// Add the attribute to prevent a field ICommandData struct to be serialized
    /// </summary>
    [AttributeUsage(AttributeTargets.Field|AttributeTargets.Property, Inherited = true)]
    public class DontSerializeForCommandAttribute : Attribute
    {
    }
}
