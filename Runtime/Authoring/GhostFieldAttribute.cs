using System;

namespace Unity.NetCode
{
    public class GhostDefaultFieldAttribute : Attribute
    {
        private int m_quantization;
        private bool m_interpolate;
        public int Quantization => m_quantization;
        public bool Interpolate => m_interpolate;

        public GhostDefaultFieldAttribute(int quantizationFactor = -1, bool interpolate = false)
        {
            m_quantization = quantizationFactor;
            m_interpolate = interpolate;
        }
    }
}
