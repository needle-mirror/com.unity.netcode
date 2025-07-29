using System.Threading.Tasks;
using NUnit.Framework;
using VerifyCS = NetCodeAnalyzer.Tests.CSharpAnalyzerVerifier<
    NetCodeAnalyzer.EntityQueryBuilderAnalyzer>;

namespace NetCodeAnalyzer.Tests;

[TestFixture]
public class EntityQueryBuilderAnalyzerTests
{
    [Test]
    public async Task QueryWithIgnoreDisabledAndSimulate_ThrowsWarning()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.Collections;
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
                public void OnCreate(ref SystemState state)
                {
                    var builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<TestComponent>()
                        .WithAll<Simulate>()
                        .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
                }
            }
";

        var expected = VerifyCS.Diagnostic(NetcodeDiagnostics.k_NetC0001Descriptor)
            .WithLocation(23, 38);
        await VerifyCS.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task NoWarningWhenNotPredictionGroup()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.Collections;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(ComponentSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnCreate(ref SystemState state)
                {
                    var builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<TestComponent>()
                        .WithAll<Simulate>()
                        .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
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
            using Unity.Collections;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnCreate(ref SystemState state)
                {
                    var builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<TestComponent>()
                        .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState);
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
            using Unity.Collections;
            using Unity.NetCode;

            public struct TestComponent : IComponentData
            {
            }

            [UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
            public partial struct TestSystem : ISystem
            {
                public void OnCreate(ref SystemState state)
                {
                    var builder = new EntityQueryBuilder(Allocator.Temp)
                        .WithAll<TestComponent>()
                        .WithAll<Simulate>();;
                }
            }
";

        await VerifyCS.VerifyAnalyzerAsync(text);
    }
}
