using System.Text;
using System.Diagnostics;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-tests", Guid.NewGuid().ToString("N"));

    public CoreTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public void FormulaUsesCoverageFraction()
    {
        Assert.Equal(20, CrapCalculator.Calculate(4, 0), precision: 8);
        Assert.Equal(4, CrapCalculator.Calculate(4, 1), precision: 8);
        Assert.Equal(6, CrapCalculator.Calculate(4, 0.5), precision: 8);
        Assert.Equal(8, CrapCalculator.Calculate(8, 1), precision: 8);
        Assert.Equal(9, CrapCalculator.Calculate(9, 1), precision: 8);
        Assert.False(CrapCalculator.Calculate(4, 0.5) > 8);
        Assert.False(CrapCalculator.Calculate(8, 1) > 8);
        Assert.True(CrapCalculator.Calculate(9, 1) > 8);
    }

    [Fact]
    public void RoslynAnalyzerCountsBranchesAndOnlyOrdinaryMethods()
    {
        var file = Write("Example.cs", """
            class Example {
              public Example() { }
              public int Property => 1;
              public int Work(int x) => x > 0 && x < 10 ? 1 : 0;
              public async System.Threading.Tasks.Task<int> Async<T>(T value) { if (value is null) return 0; return await System.Threading.Tasks.Task.FromResult(1); }
            }
            interface IExample { void NotConcrete(); }
            abstract class Base { public abstract void AlsoNotConcrete(); }
            """);
        var methods = new SourceAnalyzer().AnalyzeFiles([file]);
        Assert.Equal(2, methods.Count);
        Assert.Equal(3, methods.Single(method => method.MethodName == "Work").Complexity);
        Assert.Contains(methods, method => method.DisplayName == "Example.Async(T)");
    }

    [Fact]
    public void AmbiguousOverloadCoverageRemainsUnknown()
    {
        var file = Write("Overloads.cs", "class C { int M()=>1; int M(int x)=>x; }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var report = new[] { new CoverageMethod(file, "C", "M", null, [new CoveragePoint(1, 1)]) };
        var metrics = CoverageMatcher.Apply(source, [report]);
        Assert.All(metrics, metric => Assert.Null(metric.Coverage));
    }

    [Fact]
    public void SequencePointLineConfidentlyMatchesOverload()
    {
        var file = Write("Overloads.cs", """
            class C {
              int M() {
                return 1;
              }
              int M(int x) {
                return x;
              }
            }
            """);
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var report = new[] { new CoverageMethod(file, "C", "M", null, [new CoveragePoint(6, 1)]) };
        var metrics = CoverageMatcher.Apply(source, [report]);
        Assert.Null(metrics.Single(metric => metric.StartLine == 2).Coverage);
        Assert.Equal(1, metrics.Single(metric => metric.StartLine == 5).Coverage);
    }

    [Fact]
    public void MatchesNestedGenericTypeNames()
    {
        var file = Write("Generic.cs", "namespace Example; class Outer<T> { class Inner<U> { int M() => 1; } }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var report = new[] { new CoverageMethod(file, "Example.Outer`1+Inner`1", "M", 0, [new CoveragePoint(1, 1)]) };
        var metric = Assert.Single(CoverageMatcher.Apply(source, [report]));
        Assert.Equal(1, metric.Coverage);
    }

    [Fact]
    public void ReportsMergeDistinctSequencePointUnionAndVisitedState()
    {
        var file = Write("Union.cs", "class C {\n int M() {\n  var x = 1;\n  x++;\n  return x;\n }\n}");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var first = new[] { new CoverageMethod(file, "C", "M", 0,
            [new CoveragePoint(3, 1, 3), new CoveragePoint(4, 0, 3)], "A") };
        var duplicateWithVisit = new[] { new CoverageMethod(file, "C", "M", 0,
            [new CoveragePoint(3, 1, 3), new CoveragePoint(4, 1, 3)], "A") };
        var overlap = new[] { new CoverageMethod(file, "C", "M", 0,
            [new CoveragePoint(4, 1, 3), new CoveragePoint(5, 0, 3)], "A") };

        var metric = Assert.Single(CoverageMatcher.Apply(source, [first, duplicateWithVisit, overlap]));
        Assert.Equal(2d / 3d, metric.Coverage!.Value, precision: 8);
    }

    [Fact]
    public void ConflictingAssemblyCandidatesRemainUnknown()
    {
        var file = Write("Assembly.cs", "class C { int M() => 1; }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var a = new[] { new CoverageMethod(file, "C", "M", 0, [new CoveragePoint(1, 1)], "Assembly.A") };
        var b = new[] { new CoverageMethod(file, "C", "M", 0, [new CoveragePoint(1, 1)], "Assembly.B") };
        Assert.Null(Assert.Single(CoverageMatcher.Apply(source, [a, b])).Coverage);
    }

    [Fact]
    public void NamespaceAndGeneratedMethodNearMatchesRemainUnknown()
    {
        var file = Write("Conservative.cs", "namespace A; class C { async System.Threading.Tasks.Task<int> Work() => await System.Threading.Tasks.Task.FromResult(1); }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var wrongNamespace = new[] { new CoverageMethod(file, "B.C", "Work", 0, [new CoveragePoint(1, 1)], "A") };
        var stateMachine = new[] { new CoverageMethod(file, "A.C+<Work>d__0", "MoveNext", 0, [new CoveragePoint(1, 1)], "A") };
        Assert.Null(Assert.Single(CoverageMatcher.Apply(source, [wrongNamespace, stateMachine])).Coverage);
    }

    [Fact]
    public void ZeroEligiblePointsRemainUnknown()
    {
        var file = Write("Empty.cs", "class C { int M() => 1; }");
        var source = new SourceAnalyzer().AnalyzeFiles([file]);
        var report = new[] { new CoverageMethod(file, "C", "M", 0, [], "A") };
        Assert.Null(Assert.Single(CoverageMatcher.Apply(source, [report])).Coverage);
    }

    [Fact]
    public void SyntaxErrorsFailAnalysis()
    {
        var file = Write("Broken.cs", "class C { int M( => 1; }");
        var exception = Assert.Throws<InvalidDataException>(() => new SourceAnalyzer().AnalyzeFiles([file]));
        Assert.Contains("C# parse error", exception.Message);
    }

    [Fact]
    public void GitPorcelainHandlesSpacesRenameUntrackedAndDeleted()
    {
        var spaced = Write("new name.cs", "class A {}");
        var untracked = Write("untracked.cs", "class B {}");
        Write("tests/ignored.cs", "class Ignored {}");
        var bytes = Encoding.UTF8.GetBytes("R  new name.cs\0old name.cs\0?? untracked.cs\0?? tests/ignored.cs\0 D deleted.cs\0");
        var files = GitChanges.ParsePorcelainV1Z(bytes, temporary);
        Assert.Equal([spaced, untracked], files);
    }

    [Fact]
    public void DiscoveryExcludesBuildGeneratedGitAndTests()
    {
        var included = Write("src/Good.cs", "class Good {}");
        Write("src/Generated.g.cs", "class Generated {}");
        Write("obj/Bad.cs", "class Bad {}");
        Write("tests/BadTests.cs", "class BadTests {}");
        Write(".git/Bad.cs", "class Bad {}");
        Assert.Equal([included], SourceDiscovery.Discover([], temporary));
    }

    [Fact]
    public void ExplicitInvalidInputFailsButExcludedDirectorySourceIsAccepted()
    {
        var text = Write("notes.txt", "not source");
        var underTests = Write("tests/Explicit.cs", "class Explicit {}");
        Assert.Throws<ArgumentException>(() => SourceDiscovery.Discover([text], temporary));
        Assert.Equal([underTests], SourceDiscovery.Discover([underTests], temporary));
    }

    [Fact]
    public void AnalysisDoesNotModifySourceFiles()
    {
        var file = Write("ReadOnly.cs", "class C { int M() => 1; }");
        var before = File.ReadAllBytes(file);
        var timestamp = File.GetLastWriteTimeUtc(file);
        _ = new SourceAnalyzer().AnalyzeFiles([file]);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void ReadsOpenCoverAndCoberturaSafely()
    {
        var source = Write("Code.cs", "class C { int M()=>1; }");
        var openCover = Write("open.xml", $"""
            <CoverageSession><Modules><Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(source)}" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /><SequencePoint vc="1" sl="16707566" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);
        var cobertura = Write("cobertura.xml", $"""
            <coverage><packages><package><classes><class name="C" filename="{System.Security.SecurityElement.Escape(source)}"><methods><method name="M" signature="()"><lines><line number="1" hits="0" /></lines></method></methods></class></classes></package></packages></coverage>
            """);
        var openMethod = Assert.Single(CoverageReader.Read(openCover));
        Assert.Equal("C", openMethod.TypeName);
        Assert.Equal("M", openMethod.MethodName);
        Assert.Single(openMethod.SequencePoints);
        Assert.Single(CoverageReader.Read(cobertura));
    }

    [Fact]
    public async Task MissingCoverageIsOperationalFailureUnlessExplicitlyAllowed()
    {
        var source = Write("Gate.cs", "class C {\n int Covered() => 1;\n int Missing() => 2;\n}");
        var report = WriteOpenCover("gate.xml", source, "C", "C.Covered()", 2, 1);

        var strict = await RunApp("--coverage", report, source);
        Assert.Equal(1, strict.ExitCode);
        Assert.Contains("have N/A coverage", strict.Error);

        var allowed = await RunApp("--allow-missing-coverage", "--coverage", report, source);
        Assert.Equal(0, allowed.ExitCode);

        var allowedButKnownViolation = await RunApp("--allow-missing-coverage", "--threshold", "0", "--coverage", report, source);
        Assert.Equal(2, allowedButKnownViolation.ExitCode);
    }

    [Fact]
    public async Task ExplicitCoverageSkipsTestsAndMalformedOrMissingReportsFail()
    {
        var source = Write("ExplicitCoverage.cs", "class C { int M() => 1; }");
        var malformed = Write("bad.xml", "<coverage>");
        var resultRoot = Path.Combine(Path.GetTempPath(), "crap4csharp");
        var before = Directory.Exists(resultRoot)
            ? Directory.EnumerateDirectories(resultRoot).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(1, (await RunApp("--coverage", malformed, source)).ExitCode);
        Assert.Equal(1, (await RunApp("--coverage", Path.Combine(temporary, "missing.xml"), source)).ExitCode);

        var after = Directory.Exists(resultRoot)
            ? Directory.EnumerateDirectories(resultRoot).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task MissingExplicitCoverageFailsWithoutSourceFilesAndSkipsTests()
    {
        var result = await RunApp("--coverage", Path.Combine(temporary, "missing.xml"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Coverage report not found", result.Error);
        Assert.DoesNotContain("Running coverage", result.Output);
    }

    [Fact]
    public async Task MalformedExplicitCoverageFailsWithoutSourceFilesAndSkipsTests()
    {
        var malformed = Write("bad.xml", "<coverage>");

        var result = await RunApp("--coverage", malformed);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Malformed coverage XML", result.Error);
        Assert.DoesNotContain("Running coverage", result.Output);
    }

    [Fact]
    public async Task GitCommandFailureIsAnOperationalFailureWithDiagnostics()
    {
        var result = await RunApp("--changed");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("git rev-parse failed", result.Error);
    }

    [Fact]
    public async Task NoArgumentsCatchOperationalFailureAfterSourceDiscovery()
    {
        Write("src/OnlySource.cs", "class OnlySource { int M() => 1; }");

        var result = await RunApp();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No solution or project found", result.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("86401")]
    [InlineData("not-a-number")]
    public async Task TimeoutOptionRequiresBoundedPositiveWholeSeconds(string value)
    {
        var result = await RunApp("--timeout-seconds", value);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Timeout must be a whole number of seconds from 1 through 86400", result.Error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("86400")]
    public async Task TimeoutOptionAcceptsDocumentedBounds(string value)
    {
        var result = await RunApp("--timeout-seconds", value);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No C# source files found", result.Output);
    }

    [Fact]
    public async Task ChangedModeRunsGitStatusThroughNulSafeProcessPath()
    {
        var source = Write("src/name with spaces.cs", "class C { int M() => 1; }");
        var report = WriteOpenCover("coverage.xml", source, "C", "C.M()", 1, 1);
        var init = await ProcessRunner.RunAsync("git", ["init", "--quiet"], temporary, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(0, init.ExitCode);

        var result = await RunApp("--changed", "--coverage", report);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Path.Combine("src", "name with spaces.cs"), result.Output);
    }

    [Fact]
    public async Task ProcessRunnerTimesOutWithoutHanging()
    {
        if (OperatingSystem.IsWindows()) return;

        var started = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ProcessTimeoutException>(() => ProcessRunner.RunAsync(
            "/bin/sh", ["-c", "sleep 30"], temporary, TimeSpan.FromMilliseconds(200), CancellationToken.None));

        Assert.Contains("timed out", exception.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessRunnerCancellationKillsChildProcessTree()
    {
        if (OperatingSystem.IsWindows()) return;

        var pidFile = Path.Combine(temporary, "child.pid");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(
            "/bin/sh", ["-c", "sleep 30 & echo $! > \"$1\"; wait", "sh", pidFile], temporary,
            TimeSpan.FromSeconds(10), cancellation.Token));

        var childPid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await WaitUntilProcessExits(childPid, TimeSpan.FromSeconds(3)), $"Child process {childPid} was not terminated.");
    }

    private async Task<(int ExitCode, string Output, string Error)> RunApp(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await global::App.RunAsync(args, temporary, output, error, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private string WriteOpenCover(string relative, string source, string type, string method, int line, int visits) =>
        Write(relative, $"""
            <CoverageSession><Modules><Module><ModuleName>TestAssembly</ModuleName><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(source)}" /></Files><Classes><Class><FullName>{type}</FullName><Methods><Method><Name>{method}</Name><SequencePoints><SequencePoint vc="{visits}" sl="{line}" sc="2" el="{line}" ec="20" offset="0" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }

    private static async Task<bool> WaitUntilProcessExits(int processId, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(50);
        }
        return false;
    }
}
