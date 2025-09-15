using System.Threading.Tasks;
using NUnit.Framework;
using VerifyCS = NetCodeAnalyzer.Tests.CSharpAnalyzerVerifier<
    NetCodeAnalyzer.SystemApiQueryAnalyzer>;

namespace NetCodeAnalyzer.Tests;

[TestFixture]
public class SystemApiQueryAnalyzerTests
{
    [Test]
    public async Task QueryWithIgnoreDisabledAndSimulate_ThrowsWarning()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
            public class TestSystemGroup : ComponentSystemGroup
            {
            }

            [UpdateInGroup(typeof(TestSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnUpdate(ref SystemState state)
                {
                    foreach (var test in SystemAPI.Query<RefRW<TestComponent>>().WithAll<Simulate>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
                    {
                    }
                }
            }
";

        var expected = VerifyCS.Diagnostic(NetcodeDiagnostics.k_NetC0001Descriptor)
            .WithLocation(19, 114);
        await VerifyCS.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task NoWarningWhenNotPredictionGroup()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(ComponentSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnUpdate(ref SystemState state)
                {
                    foreach (var test in SystemAPI.Query<RefRW<TestComponent>>().WithAll<Simulate>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
                    {
                    }
                }
            }
";

        await VerifyCS.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task NoWarningWithoutSimulate()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnUpdate(ref SystemState state)
                {
                    foreach (var test in SystemAPI.Query<RefRW<TestComponent>>().WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
                    {
                    }
                }
            }
";

        await VerifyCS.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task NoWarningWithoutIgnore()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnUpdate(ref SystemState state)
                {
                    foreach (var test in SystemAPI.Query<RefRW<TestComponent>>().WithAll<Simulate>())
                    {
                    }
                }
            }
";

        await VerifyCS.VerifyAnalyzerAsync(text);
    }
}
