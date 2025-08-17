using System;
using UnityEngine;

namespace Unity.NetCode.Samples.Common.Editor
{

    [Serializable]
    internal class SerializableGradient
    {
        [Serializable]
        public struct SerializableColorKey
        {
            public float r, g, b, a;
            public float time;

            public SerializableColorKey(Color color, float time)
            {
                r = color.r;
                g = color.g;
                b = color.b;
                a = color.a;
                this.time = time;
            }

            public SerializableColorKey(GradientColorKey key)
            {
                r = key.color.r;
                g = key.color.g;
                b = key.color.b;
                a = key.color.a;
                time = key.time;
            }

            public GradientColorKey ToGradientColorKey()
            {
                return new GradientColorKey(new Color(r, g, b, a), time);
            }
        }

        [Serializable]
        public struct SerializableAlphaKey
        {
            public float alpha;
            public float time;

            public SerializableAlphaKey(float alpha, float time)
            {
                this.alpha = alpha;
                this.time = time;
            }

            public SerializableAlphaKey(GradientAlphaKey key)
            {
                alpha = key.alpha;
                time = key.time;
            }

            public GradientAlphaKey ToGradientAlphaKey()
            {
                return new GradientAlphaKey(alpha, time);
            }
        }

        public SerializableColorKey[] colorKeys;
        public SerializableAlphaKey[] alphaKeys;
        public GradientMode gradientMode;

        public SerializableGradient() { }

        public SerializableGradient(Gradient g)
        {
            var gColorKeys = g.colorKeys;
            var gAlphaKeys = g.alphaKeys;
            colorKeys = new SerializableColorKey[gColorKeys.Length];
            for (int i = 0; i < gColorKeys.Length; ++i)
                colorKeys[i] = new SerializableColorKey(gColorKeys[i]);
            alphaKeys = new SerializableAlphaKey[gAlphaKeys.Length];
            for (int i = 0; i < gAlphaKeys.Length; ++i)
                alphaKeys[i] = new SerializableAlphaKey(gAlphaKeys[i]);
            gradientMode = g.mode;
        }

        public Gradient ToGradient()
        {
            var grad = new Gradient();
            var gColorKeys = new GradientColorKey[colorKeys.Length];
            for (int i = 0; i < colorKeys.Length; ++i)
                gColorKeys[i] = colorKeys[i].ToGradientColorKey();
            var gAlphaKeys = new GradientAlphaKey[alphaKeys.Length];
            for (int i = 0; i < alphaKeys.Length; ++i)
                gAlphaKeys[i] = alphaKeys[i].ToGradientAlphaKey();
            grad.colorKeys = gColorKeys;
            grad.alphaKeys = gAlphaKeys;
            grad.mode = gradientMode;
            return grad;
        }

        public static Gradient DefaultGradient()
        {
            return new Gradient
            {
                colorKeys = new[]
                {
                    new GradientColorKey(Color.green, 0f),
                    new GradientColorKey(Color.yellow, 0.5f),
                    new GradientColorKey(Color.red, 1f)
                },
                alphaKeys = new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                },
                mode = GradientMode.PerceptualBlend
            };
        }
    }
}
