using UnityEngine;
using Unity.Entities;

/// <summary>
/// TestNetCodeAuthoring
/// </summary>
internal class TestNetCodeAuthoring : MonoBehaviour
{
    /// <summary>
    /// Interface for TestNetCodeAuthoring.IConverter
    /// </summary>
    internal interface IConverter
    {
        /// <summary>
        /// Bake function
        /// </summary>
        /// <param name="gameObject">gameobject</param>
        /// <param name="baker">baker</param>
        void Bake(GameObject gameObject, IBaker baker);
    }
    /// <summary>
    /// IConverter
    /// </summary>
    public IConverter Converter;
}

class TestNetCodeAuthoringBaker : Baker<TestNetCodeAuthoring>
{
    public override void Bake(TestNetCodeAuthoring authoring)
    {
        if (authoring.Converter != null)
            authoring.Converter.Bake(authoring.gameObject, this);
    }
}
