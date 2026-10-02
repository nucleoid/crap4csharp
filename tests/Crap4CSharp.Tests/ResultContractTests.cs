using System.ComponentModel;
using System.Globalization;
using System.Reflection;
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
        Assert.Equal(typeof(global::App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            root.GetProperty("toolVersion").GetString());
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
        Assert.Contains("Threshold must be", result.Error);
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
    public async Task OutputAliasIsRejectedBeforeDiscoveryFailure()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var invalid = Write("notes.txt", "not C#");
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);

        var result = await RunApp("--format", "json", "--output", source, source, invalid);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("output.aliasesInput", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task OutputAliasThroughSymlinkedParentIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(temporary, "real");
        Directory.CreateDirectory(real);
        var source = Path.Combine(real, "Source.cs");
        await File.WriteAllTextAsync(source, "class C { int M() => 1; }", TestContext.Current.CancellationToken);
        var link = Path.Combine(temporary, "link");
        Directory.CreateSymbolicLink(link, real);

        var result = await RunApp("--format", "json", "--output", Path.Combine(link, "Source.cs"), source);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("class C { int M() => 1; }", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OutputCannotAliasImplicitlyDiscoveredProject()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var project = Write("Auto.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var before = await File.ReadAllBytesAsync(project, TestContext.Current.CancellationToken);

        var result = await RunApp("--format", "json", "--output", project, source);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(project, TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("output.aliasesInput", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EarlyFailureMarksExistingOutputStaleWithoutReplacingIt(bool argumentFailure)
    {
        var resultPath = Write("result.json", "old result");
        var result = argumentFailure
            ? await RunApp("--format", "json", "--output", resultPath, "--threshold", "invalid")
            : await RunApp("--format", "json", "--output", resultPath, "missing-directory");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("old result", await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken));
        Assert.Contains("existing content may be stale", result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Contains(document.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("reason").GetString() == "output.notWritten");
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task SafeAbsentOutputIsFinalizedForEarlyFailures(bool argumentFailure)
    {
        var resultPath = Path.Combine(temporary, argumentFailure ? "argument-error.json" : "discovery-error.json");
        var result = argumentFailure
            ? await RunApp("--format", "json", "--output", resultPath, "--threshold", "invalid")
            : await RunApp("--format", "json", "--output", resultPath, "missing-directory");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(result.Output, await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(result.Output);
        Assert.DoesNotContain(document.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("reason").GetString() == "output.notWritten");
    }

    [Fact]
    public async Task GeneratedCoverageUsesStableLogicalIdentityAcrossRuns()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        async Task<ProcessResult> Coverage(string _, IEnumerable<string> arguments, string __, TimeSpan ___, CancellationToken ____)
        {
            var values = arguments.ToArray();
            var results = values[Array.IndexOf(values, "--results-directory") + 1];
            Directory.CreateDirectory(results);
            await File.WriteAllTextAsync(Path.Combine(results, "coverage.opencover.xml"),
                File.ReadAllText(WriteOpenCover("generated.xml", source, visits: 1)), TestContext.Current.CancellationToken);
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        var first = await RunAppAt(temporary, Coverage, "--format", "json", source);
        var second = await RunAppAt(temporary, Coverage, "--format", "json", source);

        using var firstDocument = JsonDocument.Parse(first.Output);
        using var secondDocument = JsonDocument.Parse(second.Output);
        var firstEvaluation = firstDocument.RootElement.GetProperty("evaluation");
        Assert.Equal(firstEvaluation.GetRawText(), secondDocument.RootElement.GetProperty("evaluation").GetRawText());
        var coverageArtifact = Assert.Single(firstEvaluation.GetProperty("artifacts").EnumerateArray(),
            artifact => artifact.GetProperty("kind").GetString() == "coverage");
        Assert.StartsWith("<generated>/coverage/", coverageArtifact.GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().Replace('\\', '/'), coverageArtifact.GetProperty("path").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OperationalExceptionAfterDiscoveryRetainsAccumulatedEvidence()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Task<ProcessResult> CannotStart(string _, IEnumerable<string> __, string ___, TimeSpan ____, CancellationToken _____) =>
            throw new Win32Exception("dotnet unavailable");

        var result = await RunAppAt(temporary, CannotStart, "--format", "json", source);

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Single(document.RootElement.GetProperty("evaluation").GetProperty("scope").GetProperty("sources").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("commands").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray());
    }

    [Fact]
    public async Task CancellationTruthSurvivesCleanupException()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Task<ProcessResult> DrainFailure(string _, IEnumerable<string> __, string ___, TimeSpan ____, CancellationToken _____) =>
            throw new InvalidOperationException("could not drain cancelled process");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::App.RunAsync(["--format", "json", source], temporary, output, error,
            cancellation.Token, DrainFailure);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("cancelled", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("run").GetProperty("cancellation").GetProperty("cancelled").GetBoolean());
        Assert.Equal("run.cancelled", document.RootElement.GetProperty("run").GetProperty("cancellation").GetProperty("reason").GetString());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public async Task NormalizedEvaluationIsCultureInvariant()
    {
        var source = Write("Culture.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("culture.xml", source, visits: 1);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var evaluations = new List<string>();
        try
        {
            foreach (var cultureName in new[] { "en-US", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                var result = await RunApp("--format", "json", "--threshold", "8.0001", "--coverage", coverage, source);
                using var document = JsonDocument.Parse(result.Output);
                evaluations.Add(document.RootElement.GetProperty("evaluation").GetRawText());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        Assert.All(evaluations, evaluation => Assert.Equal(evaluations[0], evaluation));
    }

    [Fact]
    public void PublishedSchemaRequiresTypedRunEvidence()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "docs", "result-schema-v1.json")));
        var definitions = schema.RootElement.GetProperty("$defs");
        Assert.Equal("#/$defs/command", definitions.GetProperty("run").GetProperty("properties").GetProperty("commands").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/runArtifact", definitions.GetProperty("run").GetProperty("properties").GetProperty("artifacts").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/cancellation", definitions.GetProperty("run").GetProperty("properties").GetProperty("cancellation").GetProperty("$ref").GetString());
        var required = definitions.GetProperty("cancellation").GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("cancelled", required);
        Assert.Contains("timedOut", required);
        Assert.Contains("reason", required);
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

    [Fact]
    public async Task RealTerminalDocumentsMatchNormalizedSnapshotsAndSchema()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        const string source = "tests/Crap4CSharp.Tests/Fixtures/results/source.cs";
        const string coverage = "tests/Crap4CSharp.Tests/Fixtures/results/coverage.xml";
        var pass = await RunAppAt(repository, null, "--format", "json", "--coverage", coverage, source);
        var violation = await RunAppAt(repository, null, "--format", "json", "--threshold", "0.5", "--coverage", coverage, source);
        var operational = await RunAppAt(repository, null, "--format", "json", "--threshold", "invalid");
        var empty = Path.Combine(temporary, "empty");
        Directory.CreateDirectory(empty);
        var notApplicable = await RunAppAt(empty, null, "--format", "json");
        Task<ProcessResult> Cancel(string _, IEnumerable<string> __, string ___, TimeSpan ____, CancellationToken token) =>
            throw new OperationCanceledException("cancelled", token);
        var cancelled = await RunAppAt(repository, Cancel, "--format", "json", source);

        Assert.Equal([0, 2, 1, 1, 0], new[] { pass.ExitCode, violation.ExitCode, operational.ExitCode, cancelled.ExitCode, notApplicable.ExitCode });
        AssertEvaluationSnapshot("pass.json", pass.Output);
        AssertEvaluationSnapshot("violation.json", violation.Output);
        AssertEvaluationSnapshot("operational-error.json", operational.Output);
        AssertEvaluationSnapshot("cancelled.json", cancelled.Output);
        AssertEvaluationSnapshot("not-applicable.json", notApplicable.Output);
        foreach (var json in new[] { pass.Output, violation.Output, operational.Output, cancelled.Output, notApplicable.Output })
            AssertConformsToPublishedSchema(repository, json);
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
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("commands").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("evaluation").GetProperty("scope").GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task CancelledRunFinalizesJsonAndOutputWithIndependentToken()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var resultPath = Path.Combine(temporary, "result.json");
        Task<ProcessResult> Cancel(string _, IEnumerable<string> __, string ___, TimeSpan ____, CancellationToken token) =>
            throw new OperationCanceledException("cancelled", token);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::App.RunAsync(["--format", "json", "--output", resultPath, source], temporary,
            output, error, new CancellationToken(canceled: true), Cancel);

        Assert.Equal(1, exitCode);
        Assert.Equal(output.ToString(), await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("cancelled", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("commands").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray());
        Assert.Equal("run.cancelled", document.RootElement.GetProperty("run").GetProperty("cancellation").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task MissingCollectorKeepsChildDiagnosticsCommandAndArtifact()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Task<ProcessResult> NoCoverage(string _, IEnumerable<string> __, string ___, TimeSpan ____, CancellationToken _____) =>
            Task.FromResult(new ProcessResult(1, "child stdout\n", "child stderr\n"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::App.RunAsync(["--format", "json", source], temporary, output, error,
            TestContext.Current.CancellationToken, NoCoverage);

        Assert.Equal(1, exitCode);
        Assert.Contains("Running coverage", error.ToString());
        Assert.Contains("child stdout", error.ToString());
        Assert.Contains("child stderr", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("commands").EnumerateArray());
        Assert.Single(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray());
        var reasons = document.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("reason").GetString()).ToArray();
        Assert.Contains("tests.failed", reasons);
        Assert.Contains("coverage.notProduced", reasons);
    }

    [Fact]
    public async Task OutputWriteFailureRetainsOriginalTerminalReason()
    {
        var result = await RunApp("--format", "json", "--output", temporary);

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var reasons = document.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("reason").GetString()).ToArray();
        Assert.Contains("crap.noEligibleMethods", reasons);
        Assert.Contains("output.writeFailed", reasons);
    }

    [Fact]
    public async Task SameLineMethodsKeepTheirOwnSignatures()
    {
        var source = Write("SameLine.cs", "class C { int A() => 1; int B(int value) => value; }");
        var coverage = Write("same-line.xml", $"<CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid=\"1\" fullPath=\"{System.Security.SecurityElement.Escape(source)}\" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.A()</Name><SequencePoints><SequencePoint vc=\"1\" sl=\"1\" sc=\"1\" el=\"1\" ec=\"20\" offset=\"0\" fileid=\"1\" /></SequencePoints><FileRef uid=\"1\" /></Method><Method><Name>C.B(System.Int32)</Name><SequencePoints><SequencePoint vc=\"1\" sl=\"1\" sc=\"21\" el=\"1\" ec=\"50\" offset=\"1\" fileid=\"1\" /></SequencePoints><FileRef uid=\"1\" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>");

        var result = await RunApp("--format", "json", "--coverage", coverage, source);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var metrics = document.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray().ToArray();
        Assert.Equal("A()", metrics.Single(metric => metric.GetProperty("methodIdentity").GetString() == "C.A()").GetProperty("signature").GetString());
        Assert.Equal("B(int)", metrics.Single(metric => metric.GetProperty("methodIdentity").GetString() == "C.B(int)").GetProperty("signature").GetString());
    }

    private async Task<(int ExitCode, string Output, string Error)> RunApp(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await global::App.RunAsync(args, temporary, output, error, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAppAt(
        string workingDirectory, global::App.ProcessExecutor? processExecutor, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await global::App.RunAsync(args, workingDirectory, output, error,
            TestContext.Current.CancellationToken, processExecutor);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static void AssertEvaluationSnapshot(string fixture, string documentJson)
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var path = Path.Combine(repository, "tests", "Crap4CSharp.Tests", "Fixtures", "results", fixture);
        using var expected = JsonDocument.Parse(File.ReadAllText(path));
        using var actual = JsonDocument.Parse(documentJson);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement.GetProperty("evaluation")),
            $"Normalized evaluation did not match {fixture}.\nExpected: {expected.RootElement}\nActual: {actual.RootElement.GetProperty("evaluation")}");
    }

    private static void AssertConformsToPublishedSchema(string repository, string documentJson)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "docs", "result-schema-v1.json")));
        using var document = JsonDocument.Parse(documentJson);
        var errors = new List<string>();
        ValidateSchema(document.RootElement, schema.RootElement, schema.RootElement, "$", errors);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static void ValidateSchema(JsonElement instance, JsonElement schema, JsonElement rootSchema, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = rootSchema;
            foreach (var segment in reference.GetString()![2..].Split('/'))
                target = target.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            ValidateSchema(instance, target, rootSchema, path, errors);
        }
        if (schema.TryGetProperty("allOf", out var allOf))
            foreach (var child in allOf.EnumerateArray()) ValidateSchema(instance, child, rootSchema, path, errors);
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(instance, constant))
            errors.Add($"{path}: value does not equal const {constant}.");
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(instance, item)))
            errors.Add($"{path}: value is not in enum.");
        if (schema.TryGetProperty("type", out var type))
        {
            var valid = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(item => IsType(instance, item.GetString()!))
                : IsType(instance, type.GetString()!);
            if (!valid) { errors.Add($"{path}: expected type {type}, got {instance.ValueKind}."); return; }
        }
        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                    if (!instance.TryGetProperty(name, out _)) errors.Add($"{path}: missing required property {name}.");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (instance.TryGetProperty(property.Name, out var value))
                        ValidateSchema(value, property.Value, rootSchema, $"{path}.{property.Name}", errors);
        }
        if (instance.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in instance.EnumerateArray()) ValidateSchema(item, items, rootSchema, $"{path}[{index++}]", errors);
        }
        if (instance.ValueKind == JsonValueKind.String)
        {
            var value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) && value.Length < minLength.GetInt32()) errors.Add($"{path}: shorter than minLength.");
            if (schema.TryGetProperty("pattern", out var pattern) && !System.Text.RegularExpressions.Regex.IsMatch(value, pattern.GetString()!)) errors.Add($"{path}: does not match pattern.");
        }
        if (instance.ValueKind == JsonValueKind.Number)
        {
            var value = instance.GetDouble();
            if (schema.TryGetProperty("minimum", out var minimum) && value < minimum.GetDouble()) errors.Add($"{path}: below minimum.");
            if (schema.TryGetProperty("maximum", out var maximum) && value > maximum.GetDouble()) errors.Add($"{path}: above maximum.");
        }
    }

    private static bool IsType(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => false
    };

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
