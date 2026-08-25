namespace Unity.NetCode.Tests
{
    internal struct PredictionTestBehaviourTestInput : IInputComponentData
    {
        [GhostField] public int Value;
    }
    internal partial class PredictionTestBehaviour : GhostBehaviour
    {
        GhostComponentRef<PredictionTestBehaviourTestInput> inputRef;
        public int ValueForInput;

        public int PredictedValue;

        public override void GatherInput(float tickedDeltaTime)
        {
            ValueForInput++;
            PredictionTestBehaviourTestInput inputData = default;
            inputData.Value = ValueForInput;
            inputRef.Value = inputData;
        }

        public override void PredictionUpdate(float tickedDeltaTime)
        {
            PredictedValue = this.inputRef.Value.Value;
        }
    }
}
