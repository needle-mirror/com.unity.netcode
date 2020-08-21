using System;

namespace Unity.NetCode
{
    [AttributeUsage(AttributeTargets.Field)]
    public class GhostFieldAttribute : Attribute
    {
        public int Quantization { get; set; }
        public bool Composite { get; set; }
        public bool Interpolate { get; set; }
        public int SubType { get; set; }
        public bool SendData { get; set; }


        public GhostFieldAttribute()
        {
            Quantization = -1;
            Interpolate = false;
            Composite = false;
            SubType = 0;
            SendData = true;
        }
    }
}
