using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace DocumentationCodeSamples
{
    #region IInputComponentData
    public struct MyInput : IInputComponentData
    {
        [GhostField] public int Value;
    }
    #endregion

    class ghostfield_synchronize
    {
        #region GhostFieldInheritance
        public struct Vector2
        {
            public float x;
            [GhostField(Quantization = 100)] public float y;
        }

        public struct MyComponent : IComponentData
        {
            //Value.x will inherit the quantization value specified by the parent definition (1000).
            //Value.y will maintain its original quantization value (100).
            [GhostField(Quantization = 1000)] public Vector2 Value;
        }
        #endregion

        #region GhostField
        public struct MySerializedComponent : IComponentData
        {
            [GhostField]public int MyIntField;
            [GhostField(Quantization = 1000)] public float MyFloatField;
            [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Interpolate)] public float2 Position;
            public float2 NonSerializedField;
            // ...
        }
        #endregion

        #region DynamicBufferSerialization
        public struct SerialisedBuffer : IBufferElementData
        {
            [GhostField] public int Field0;
            [GhostField(Quantization=1000)] public float Field1;
            [GhostField(Quantization=1000)] public float2 Position;
            // public float2 PublicNonSerialisedField; // This is an explicit error!
            private float2 PrivateNonSerialisedField; // We allow this. Ensure you set this on the client, before reading from it.
            [GhostField(SendData=false)] public int NotSentAndUninitialised; // We allow this. Ensure you set this on the client, before reading from it.
            // ...
        }
        #endregion

        #region ICommandData
        public struct MyCommand : ICommandData
        {
            [GhostField] public NetworkTick Tick {get; set;}
            [GhostField] public int Value;
        }
        #endregion
    }
}
