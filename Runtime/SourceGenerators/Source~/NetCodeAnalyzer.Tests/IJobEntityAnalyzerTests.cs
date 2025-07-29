using System.Threading.Tasks;
using NUnit.Framework;
using VerifyCS = NetCodeAnalyzer.Tests.CSharpAnalyzerVerifier<
    NetCodeAnalyzer.IJobEntityAnalyzer>;

namespace NetCodeAnalyzer.Tests;

[TestFixture]
public class IJobEntityAnalyzerTests
{
    [Test]
    public async Task JobWithIgnoreDisabledAndSimulate_ThrowsWarning()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            [WithAll(typeof(Simulate)), WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
            partial struct SampleJob : IJobEntity
            {
                public void Execute(in TestComponent test)
                {

                }
            }
";
        var expected = VerifyCS.Diagnostic(NetcodeDiagnostics.k_NetC0001Descriptor)
            .WithLocation(5, 53);
        await VerifyCS.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task JobWithIgnoreDisabledAndSimulateExecute_ThrowsWarning()
    {
            const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            [WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
            partial struct SampleJob : IJobEntity
            {
                public void Execute(in TestComponent test, in Simulate simulate)
                {

                }
            }
";
        var expected = VerifyCS.Diagnostic(NetcodeDiagnostics.k_NetC0001Descriptor)
            .WithLocation(5, 26);
        await VerifyCS.VerifyAnalyzerAsync(text, expected);
    }

    [Test]
    public async Task JobWithoutIgnoreDisabled_NoWarning()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            [WithAll(typeof(Simulate))]
            partial struct SampleJob : IJobEntity
            {
                public void Execute(in TestComponent test)
                {

                }
            }
";
        await VerifyCS.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task JobWithoutSimulate_NoWarning()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            [WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)]
            partial struct SampleJob : IJobEntity
            {
                public void Execute(in TestComponent test)
                {

                }
            }
";
        await VerifyCS.VerifyAnalyzerAsync(text);
    }

    [Test]
    public async Task JobWithoutIgnoreExecute_NoWarning()
    {
        const string text = @"
            using Unity.Entities;
            using Unity.NetCode;

            partial struct SampleJob : IJobEntity
            {
                public void Execute(in TestComponent test, in Simulate simulate)
                {

                }
            }
";

        await VerifyCS.VerifyAnalyzerAsync(text);
    }
}
