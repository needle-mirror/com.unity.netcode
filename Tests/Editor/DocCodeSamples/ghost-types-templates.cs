using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    struct ghost_types_templates
    {
        #region UnsafeFixed
        public const int MyFixedArrayLength = 3;
        [GhostField(Quantization = 100)]public unsafe fixed float MyFixedArray[MyFixedArrayLength];
        #endregion

        #region UnsafeHelperMethod
        public unsafe ref float MyFixedArrayRef(int index)
        {
            if (index < 0 || index >= MyFixedArrayLength)
                throw new InvalidOperationException($"MyFixedArrayRef<float>[{index}] is out of bounds (Length:{MyFixedArrayLength})!");
            return ref MyFixedArray[index];
        }
        #endregion

        #region RPC
        public struct MyRpc : IRpcCommand
        {
            //limit the list replicated elements to 32. The limit is enforced
            [GhostFixedListCapacity(Capacity=32)]
            public FixedList4096Bytes<float> floats;
        }

        public struct MyComponent : IComponentData
        {
            //limit the list replicated elements to 32. The limit is enforced
            [GhostFixedListCapacity(Capacity=32)]
            public FixedList4096Bytes<float> floats;
        }
        #endregion

        #region Union
        [StructLayout(LayoutKind.Explicit)]
        public struct Union
        {
            [FieldOffset(0)] [GhostField(SendData = false)] public StructA State1;
            [FieldOffset(0)] [GhostField(Quantization = 0, Smoothing = SmoothingAction.Clamp, Composite = true)] public StructB State2;
            [FieldOffset(0)] [GhostField(SendData = false)] public StructC State3;
            public struct StructA
            {
                public int A, B;
                public float C;
            }
            public struct StructB
            {
                public ulong A, B, C, D;
            }
            public struct StructC
            {
                public double A, B;
            }
            public static void Assertions()
            {
                UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructA>());
                UnityEngine.Debug.Assert(UnsafeUtility.SizeOf<StructB>() >= UnsafeUtility.SizeOf<StructC>());
            }
        }
        #endregion
    }
}
