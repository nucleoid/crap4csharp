using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ResultContractTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-result-tests", Guid.NewGuid().ToString("N"));

    public ResultContractTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public async Task JsonSuccessIsOneCompleteVersionedDocument()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);

        var result = await RunApp("--format", "json", "--coverage", coverage, source);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("\n", result.Output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("ordinary-methods-v1", root.GetProperty("complexityRulesetVersion").GetString());
        Assert.Equal("legacy", root.GetProperty("evaluation").GetProperty("invocationMode").GetString());
        Assert.Equal("pass", root.GetProperty("evaluation").GetProperty("decision").GetProperty("policyDecision").GetString());
        Assert.Equal(0, root.GetProperty("run").GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("CRAP", result.Output);
    }

    [Fact]
    public async Task JsonIntentRendersArgumentErrorsAsJson()
    {
        var result = await RunApp("--format", "json", "--threshold", "not-a-number");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("operationalError", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("arguments.invalid", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain("error:", result.Output);
    }

    [Fact]
    public async Task OutputFileMatchesJsonStdoutAndReplacesExistingFile()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);
        var outputPath = Write("result.json", "old");

        var result = await RunApp("--format", "json", "--output", outputPath, "--coverage", coverage, source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(result.Output, await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OutputCannotAliasAnInputAndDoesNotModifyIt()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        var timestamp = File.GetLastWriteTimeUtc(source);

        var result = await RunApp("--format", "json", "--output", source, "--coverage", coverage, source);

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("output.aliasesInput", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public async Task NormalizedEvaluationIsStableWhileRunEnvelopeVaries()
    {
        var source = Write("unicodé source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);

        var first = await RunApp("--format", "json", "--coverage", coverage, source);
        var second = await RunApp("--format", "json", "--coverage", coverage, source);

        using var firstDocument = JsonDocument.Parse(first.Output);
        using var secondDocument = JsonDocument.Parse(second.Output);
        Assert.Equal(firstDocument.RootElement.GetProperty("evaluation").GetRawText(),
            secondDocument.RootElement.GetProperty("evaluation").GetRawText());
        Assert.NotEqual(firstDocument.RootElement.GetProperty("run").GetProperty("invocationId").GetString(),
            secondDocument.RootElement.GetProperty("run").GetProperty("invocationId").GetString());
        Assert.Contains("unicodé source.cs", first.Output);
    }

    [Theory]
    [InlineData("--format")]
    [InlineData("--output")]
    public async Task MachineOutputFlagsCannotBeRepeated(string option)
    {
        var value = option == "--format" ? "json" : "a.json";
        var second = option == "--format" ? "human" : "b.json";
        var result = await RunApp("--format", "json", option, value, option, second);
        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("arguments.invalid", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void DetailedCoverageRetainsConservativeUnknownReasons()
    {
        var sourcePath = Write("Reasons.cs", "class C { int A() => 1; int B() => 2; }");
        var source = new SourceAnalyzer().AnalyzeFiles([sourcePath]);
        var report = new[]
        {
            new CoverageMethod(sourcePath, "C", "A", 0, [], "Fixture"),
            new CoverageMethod(sourcePath, "C", "B", 0, [new CoveragePoint(1, 1)], "One"),
            new CoverageMethod(sourcePath, "C", "B", 0, [new CoveragePoint(1, 1)], "Two")
        };

        var detailed = CoverageMatcher.ApplyDetailed(source, [report]);

        Assert.Equal(CoverageReasonCodes.NoEligiblePoints, detailed.Single(item => item.Metric.MethodName == "A").CoverageReason);
        Assert.Equal(CoverageReasonCodes.ConflictingModule, detailed.Single(item => item.Metric.MethodName == "B").CoverageReason);
    }

    [Fact]
    public async Task PrecisionIsNotRoundedAwayAtTheHumanGate()
    {
        var source = Write("Precision.cs", "class C { int M(int x) { if(x>0){} if(x>1){} if(x>2){} if(x>3){} if(x>4){} if(x>5){} if(x>6){} return x; } }");
        var points = string.Join(string.Empty, Enumerable.Range(0, 86).Select(index =>
            $"<SequencePoint vc=\"{(index == 0 ? 0 : 1)}\" sl=\"1\" sc=\"1\" el=\"1\" ec=\"120\" offset=\"{index}\" fileid=\"1\" />"));
        var coverage = Write("precision.xml", $"<CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid=\"1\" fullPath=\"{System.Security.SecurityElement.Escape(source)}\" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M(System.Int32)</Name><SequencePoints>{points}</SequencePoints><FileRef uid=\"1\" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>");

        var result = await RunApp("--coverage", coverage, source);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("8.00>", result.Output);
        Assert.Contains("raw CRAP", result.Output);
    }

    [Theory]
    [InlineData("pass.json", "pass", "pass", "completed", 0)]
    [InlineData("violation.json", "fail", "fail", "completed", 2)]
    [InlineData("operational-error.json", "operationalError", "unknown", "operationalError", 1)]
    [InlineData("cancelled.json", "cancelled", "unknown", "cancelled", 1)]
    [InlineData("not-applicable.json", "notApplicable", "notApplicable", "completed", 0)]
    public void TerminalStateSnapshotsAreVersionedAndInternallyConsistent(
        string fixture, string checkStatus, string policyDecision, string runStatus, int exitCode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "results", fixture);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(checkStatus, root.GetProperty("checkStatus").GetString());
        Assert.Equal(policyDecision, root.GetProperty("policyDecision").GetString());
        Assert.Equal(runStatus, root.GetProperty("runStatus").GetString());
        Assert.Equal(exitCode, root.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task FailedChildRetainsKnownViolationButExitOneWins()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        async Task<ProcessResult> FailedTest(string _, IEnumerable<string> arguments, string __, TimeSpan ___, CancellationToken ____)
        {
            var values = arguments.ToArray();
            var results = values[Array.IndexOf(values, "--results-directory") + 1];
            Directory.CreateDirectory(results);
            await File.WriteAllTextAsync(Path.Combine(results, "coverage.opencover.xml"),
                File.ReadAllText(WriteOpenCover("generated.xml", source, visits: 1)), TestContext.Current.CancellationToken);
            return new ProcessResult(1, "failed test output", "failed test error");
        }
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::App.RunAsync(["--format", "json", "--threshold", "0", source], temporary,
            output, error, TestContext.Current.CancellationToken, FailedTest);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Single(document.RootElement.GetProperty("evaluation").GetProperty("findings").EnumerateArray());
        Assert.Equal("unknown", document.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("policyDecision").GetString());
        Assert.Equal("operationalError", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Contains("failed test error", error.ToString());
    }

    [Fact]
    public async Task ChildTimeoutProducesOneStructuredOperationalDocument()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Task<ProcessResult> Timeout(string fileName, IEnumerable<string> _, string __, TimeSpan timeout, CancellationToken ___) =>
            throw new ProcessTimeoutException(fileName, timeout);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::App.RunAsync(["--format", "json", source], temporary, output, error,
            TestContext.Current.CancellationToken, Timeout);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("run.timeout", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
        Assert.True(document.RootElement.GetProperty("run").GetProperty("cancellation").GetProperty("timedOut").GetBoolean());
    }

    private async Task<(int ExitCode, string Output, string Error)> RunApp(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await global::App.RunAsync(args, temporary, output, error, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private string WriteOpenCover(string relative, string source, int visits) =>
        Write(relative, $"""
            <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(source)}" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name><SequencePoints><SequencePoint vc="{visits}" sl="1" sc="11" el="1" ec="24" offset="0" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }
}
