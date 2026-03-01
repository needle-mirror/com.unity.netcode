#if UNITY_6000_3_OR_NEWER // Required to use GameObject bridge with EntityID
using NUnit.Framework;

namespace Unity.NetCode.Tests
{
    /// <summary>
    /// Tests that the input value is incrementing as expected each tick and that there's only a diff of +1 between each tick.
    /// This also tests the <see cref="GhostBehaviour.TryGetPreviousInputData"/> and make sure it's consistent with the provided inputs
    /// </summary>
    internal partial class TestInputBehaviour : GhostBehaviour
    {
        public GhostComponentRef<TestInput> InputData;
        public struct TestInput : IInputComponentData
        {
            public int Value;
        }
        public override void GatherInput(float tickedDeltaTime)
        {
            InputData.Value = new TestInput { Value = InputData.Value.Value + 1 };
        }

        public override void PredictionUpdate(float tickedDeltaTime)
        {
            var data = InputData.Value;
            var currentTick = Ghost.NetworkTime.ServerTick;
            if(data.Value > 4)
            {
                var hasPrevious = TryGetPreviousInputData<TestInputBehaviour.TestInput>(out var prevData);
                currentTick.Subtract(3u);
                Assert.IsTrue(hasPrevious, $"should have previous input for tick {currentTick.ToFixedString()}");
                Assert.AreEqual(data.Value-1, prevData.Value, $"{Ghost.World.IsServer()} - previous data does not match");
                var hasDataAtTick = TryGetInputDataAtTick<TestInputBehaviour.TestInput>(currentTick, out var dataAtTick);
                Assert.IsTrue(hasDataAtTick, $"should have input for tick {currentTick.ToFixedString()}");
                Assert.AreEqual(data.Value-3, dataAtTick.Value);
            }
        }
    }
}
#endif
