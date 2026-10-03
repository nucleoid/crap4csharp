using System.Security.Cryptography;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CallableCliTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-callable-cli", Guid.NewGuid().ToString("N"));
    public CallableCliTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, true);

    [Fact]
    public async Task AnalyzeDefaultsModernIsPureAndEmitsCallableJson()
    {
        var source = Write("C.cs", "class C { int M() => 1; }");
        var coverage = WriteCoverage(source, 1);
        var before = Snapshot(source, coverage);
        var launches = 0;

        var result = await Run((_, _, _, _, _) => { launches++; throw new InvalidOperationException("process forbidden"); },
            "analyze", "--syntax-only", "--format", "json", "--coverage", coverage, source);

        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(0, launches);
        Assert.Equal(before, Snapshot(source, coverage));
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("callables-v1", document.RootElement.GetProperty("complexityRulesetVersion").GetString());
        Assert.Equal("analyze", document.RootElement.GetProperty("evaluation").GetProperty("invocationMode").GetString());
        var callable = Assert.Single(document.RootElement.GetProperty("evaluation").GetProperty("callables").EnumerateArray());
        Assert.Equal("method", callable.GetProperty("kind").GetString());
        Assert.Equal("known", callable.GetProperty("coverageStatus").GetString());
    }

    [Fact]
    public async Task UnsupportedModernInventoryIsExitOneButKnownViolationsRemain()
    {
        var source = Write("Nested.cs", "class C { int M() { System.Func<int,int> f = x => x > 0 ? 1 : 0; return 1; } }");
        var coverage = WriteCoverage(source, 1);
        var result = await Run(null, "analyze", "--syntax-only", "--format", "json", "--threshold", "0",
            "--allow-missing-coverage",
            "--coverage", coverage, source);
        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var evaluation = document.RootElement.GetProperty("evaluation");
        Assert.Contains(evaluation.GetProperty("findings").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "crap.thresholdExceeded");
        Assert.Contains(evaluation.GetProperty("callables").EnumerateArray(),
            item => item.GetProperty("coverageReason").GetString() == CoverageReasonCodes.UnsupportedGeneratedMapping);
    }

    [Fact]
    public async Task LegacyDefaultAndModernCommandApplicabilityAreExplicit()
    {
        var source = Write("Legacy.cs", "class C { C() { } int M() => 1; }");
        var coverage = WriteCoverage(source, 1);
        var legacy = await Run(null, "--format", "json", "--coverage", coverage, source);
        using var legacyDocument = JsonDocument.Parse(legacy.Output);
        Assert.Equal("ordinary-methods-v1", legacyDocument.RootElement.GetProperty("complexityRulesetVersion").GetString());
        Assert.Single(legacyDocument.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());

        var rejected = await Run(null, "check", "--ruleset", "ordinary-methods-v1", source);
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("callables-v1", rejected.Error);
    }

    [Fact]
    public async Task HelpExposesRulesetAndExemptionContractsWithoutLaunchingAnything()
    {
        var launches = 0;
        var result = await Run((_, _, _, _, _) => { launches++; return Task.FromResult(new ProcessResult(0, "", "")); }, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, launches);
        Assert.Contains("ordinary-methods-v1", result.Output);
        Assert.Contains("callables-v1", result.Output);
        Assert.Contains("--callable-exemptions", result.Output);
        Assert.Contains("analyze --syntax-only", result.Output);
    }

    [Fact]
    public async Task LocalExactExemptionRemainsUnreviewedAndCannotMakeUnsupportedScopePass()
    {
        var source = Write("Exempt.cs", "class C { int M() { System.Func<int> f = () => 1; return 1; } }");
        var coverage = WriteCoverage(source, 1);
        var first = await Run(null, "analyze", "--syntax-only", "--format", "json", "--coverage", coverage, source);
        using var firstDocument = JsonDocument.Parse(first.Output);
        var evaluation = firstDocument.RootElement.GetProperty("evaluation");
        var contextId = evaluation.GetProperty("contexts")[0].GetProperty("id").GetString();
        var lambda = Assert.Single(evaluation.GetProperty("callables").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "lambda");
        var familyId = Assert.Single(evaluation.GetProperty("families").EnumerateArray())
            .GetProperty("familyId").GetString();
        var exemption = Write("exemptions.json", $$"""
            {"version":"callable-exemptions-v1","entries":[{"ruleset":"callables-v1","contextId":"{{contextId}}","targetFramework":"net10.0","callableId":"{{lambda.GetProperty("callableId").GetString()}}","bodyChecksum":"{{lambda.GetProperty("bodyChecksum").GetString()}}","reasonCode":"coverage.unsupportedGeneratedMapping","justification":"inspected locally","reviewReference":"local-review","familyIds":["{{familyId}}"]}]}
            """);

        var result = await Run(null, "analyze", "--syntax-only", "--format", "json", "--coverage", coverage,
            "--callable-exemptions", exemption, source);

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var match = Assert.Single(document.RootElement.GetProperty("evaluation").GetProperty("callableExemptions").EnumerateArray());
        Assert.Equal("local-unreviewed", match.GetProperty("status").GetString());
        Assert.False(match.GetProperty("approvedForEnforcement").GetBoolean());
        Assert.Contains(document.RootElement.GetProperty("evaluation").GetProperty("findings").EnumerateArray(),
            item => item.GetProperty("code").GetString() == CoverageReasonCodes.UnsupportedGeneratedMapping);
    }

    private async Task<(int ExitCode, string Output, string Error)> Run(global::App.ProcessExecutor? executor, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await global::App.RunAsync(args, temporary, output, error, CancellationToken.None, executor);
        return (exit, output.ToString(), error.ToString());
    }

    private string WriteCoverage(string source, int visits) => Write("coverage.xml", $"""
        <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(source)}" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>System.Int32 C::M()</Name><SequencePoints><SequencePoint vc="{visits}" sl="1" sc="11" el="1" ec="24" offset="0" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
        """);

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Snapshot(params string[] paths) => string.Join("|", paths.Select(path =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + ":" + File.GetLastWriteTimeUtc(path).Ticks));
}
