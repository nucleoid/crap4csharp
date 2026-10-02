using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Crap4CSharp.Core;

using var cancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await App.RunAsync(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error, cancellationSource.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

internal static class App
{
    private const string Help = """
        Crap4CSharp - calculate CRAP metrics for C# methods

        Usage:
          crap4csharp [options] [file-or-directory ...]

        Options:
          --coverage <xml>   Use an OpenCover or Cobertura/Coverlet XML report; repeatable.
                             Supplying coverage skips dotnet test.
          --project <path>   Solution or project passed to dotnet test.
          --threshold <n>    Fail when a known CRAP score is strictly greater than n (default: 8).
          --timeout-seconds <n>
                             Maximum time for each external command (default: 300; max: 86400).
          --allow-missing-coverage
                             Allow N/A methods; known scores still gate. By default any N/A is
                             an operational failure to prevent a false-green quality gate.
          --changed          Analyze changed/untracked C# files from git porcelain status.
          --format <value>   Render human (default) or json output.
          --output <path>    Atomically write the versioned JSON result document to path.
          -h, --help         Show help and perform no discovery, tests, or writes.

        Exit codes: 0 success, 1 usage/operational failure, 2 threshold exceeded.
        """;

    public static async Task<int> RunAsync(string[] args, string workingDirectory, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Any(arg => arg is "--help" or "-h")) { await output.WriteLineAsync(Help); return 0; }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var jsonIntent = HasJsonIntent(args);
        var requestedOutput = PreDetectValue(args, "--output");
        Options? options = null;
        ExecutionOutcome outcome;
        try
        {
            options = args.Length == 0 ? new Options() : Parse(args);
            outcome = await ExecuteAsync(options, workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            var cancelled = exception is OperationCanceledException;
            var timedOut = exception is ProcessTimeoutException;
            var reason = exception is ArgumentException ? "arguments.invalid" : cancelled ? "run.cancelled" : timedOut ? "run.timeout" : "execution.failed";
            outcome = ExecutionOutcome.Failure(reason, exception.Message, cancelled, timedOut);
        }

        var format = options?.Format ?? (jsonIntent ? "json" : "human");
        var outputPath = options?.Output ?? requestedOutput;
        var result = BuildResult(outcome, options ?? new Options { Format = format, Output = outputPath }, workingDirectory,
            startedAt, DateTimeOffset.UtcNow, stopwatch.Elapsed);
        var json = ResultWriter.Serialize(result);

        if (options is not null && outputPath is not null && outcome.OutputAliasReason is null)
        {
            try
            {
                await ResultWriter.WriteAtomicAsync(Path.GetFullPath(outputPath, workingDirectory), json, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                outcome = outcome.WithOperationalFailure("output.writeFailed", exception.Message);
                result = BuildResult(outcome, options ?? new Options { Format = format, Output = outputPath }, workingDirectory,
                    startedAt, DateTimeOffset.UtcNow, stopwatch.Elapsed);
                json = ResultWriter.Serialize(result);
            }
        }

        if (format == "json")
        {
            foreach (var diagnostic in outcome.DiagnosticLines) await error.WriteLineAsync(diagnostic);
            try { await output.WriteAsync(json); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                await error.WriteLineAsync($"error: unable to write JSON result: {exception.Message}");
                return 1;
            }
        }
        else
        {
            foreach (var line in outcome.HumanLines) await output.WriteLineAsync(line);
            foreach (var diagnostic in outcome.DiagnosticLines) await error.WriteLineAsync(diagnostic);
            if (outcome.ErrorMessage is not null) await error.WriteLineAsync($"error: {outcome.ErrorMessage}");
            if (outcome.Reason == "arguments.invalid") await error.WriteLineAsync("Run 'crap4csharp --help' for usage.");
        }
        return result.Run.ExitCode;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        var seenFormat = false;
        var seenOutput = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string Value()
            {
                if (++index >= args.Length) throw new ArgumentException($"Missing value for {arg}.");
                return args[index];
            }
            switch (arg)
            {
                case "--coverage": options.Coverage.Add(Value()); break;
                case "--project": options.Project = Value(); break;
                case "--threshold":
                    if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
                        !double.IsFinite(threshold) || threshold < 0) throw new ArgumentException("Threshold must be a finite non-negative number.");
                    options.Threshold = threshold;
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutSeconds) || timeoutSeconds is < 1 or > 86400)
                        throw new ArgumentException("Timeout must be a whole number of seconds from 1 through 86400.");
                    options.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    break;
                case "--changed": options.Changed = true; break;
                case "--allow-missing-coverage": options.AllowMissingCoverage = true; break;
                case "--format":
                    if (seenFormat) throw new ArgumentException("--format may be specified only once.");
                    seenFormat = true;
                    options.Format = Value();
                    if (options.Format is not ("human" or "json")) throw new ArgumentException("Format must be human or json.");
                    break;
                case "--output":
                    if (seenOutput) throw new ArgumentException("--output may be specified only once.");
                    seenOutput = true;
                    options.Output = Value();
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}");
                    options.Inputs.Add(arg);
                    break;
            }
        }
        if (options.Changed && options.Inputs.Count > 0) throw new ArgumentException("--changed cannot be combined with explicit files or directories.");
        return options;
    }

    private static async Task<ExecutionOutcome> ExecuteAsync(Options options, string workingDirectory, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var commands = new List<CommandRecord>();
        var runArtifacts = new List<RunArtifact>();
        var diagnostics = new List<string>();
        var files = options.Changed
            ? await ChangedFilesAsync(workingDirectory, options.Timeout, cancellationToken)
            : SourceDiscovery.Discover(options.Inputs, workingDirectory);
        var reports = options.Coverage.Select(path => Path.GetFullPath(path, workingDirectory)).ToList();
        var project = options.Project is null ? null : Path.GetFullPath(options.Project, workingDirectory);
        var alias = FindOutputAlias(options.Output, workingDirectory, files, reports, project);
        if (alias is not null) return ExecutionOutcome.Failure("output.aliasesInput", $"Output path aliases {alias}.") with
        {
            Files = files,
            Reports = reports,
            OutputAliasReason = alias
        };

        IReadOnlyList<CoverageMethod>[]? coverage = null;
        if (reports.Count > 0)
        {
            foreach (var report in reports)
                if (!File.Exists(report)) throw new FileNotFoundException($"Coverage report not found: {report}", report);
            coverage = reports.Select(CoverageReader.Read).ToArray();
        }
        if (files.Count == 0)
        {
            lines.Add("No C# source files found.");
            return new ExecutionOutcome { Files = files, Reports = reports, HumanLines = lines, Reason = "crap.noEligibleMethods" };
        }

        var testFailed = false;
        if (reports.Count == 0)
        {
            var target = ResolveTestTarget(options.Project, workingDirectory);
            var resultsDirectory = Path.Combine(Path.GetTempPath(), "crap4csharp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultsDirectory);
            lines.Add($"Running coverage: dotnet test {target}");
            lines.Add($"Coverage results: {resultsDirectory}");
            var arguments = new[] { "test", target, "-m:1", "--collect:XPlat Code Coverage", "--results-directory", resultsDirectory,
                "--", "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover" };
            var process = await ProcessRunner.RunAsync("dotnet", arguments, workingDirectory, options.Timeout, cancellationToken);
            commands.Add(new CommandRecord("dotnet", arguments, process.ExitCode));
            if (!string.IsNullOrWhiteSpace(process.StandardOutput)) lines.Add(process.StandardOutput.TrimEnd());
            if (!string.IsNullOrWhiteSpace(process.StandardError)) diagnostics.Add(process.StandardError.TrimEnd());
            testFailed = process.ExitCode != 0;
            reports.AddRange(Directory.EnumerateFiles(resultsDirectory, "*.xml", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("coverage", StringComparison.OrdinalIgnoreCase)));
            runArtifacts.Add(new RunArtifact("coverageResults", resultsDirectory, "retained", reports.Count > 0, false));
            if (reports.Count == 0) throw new InvalidOperationException("dotnet test produced no coverage XML. Add coverlet.collector to the test project or pass --coverage.");
        }

        foreach (var report in reports)
            if (!File.Exists(report)) throw new FileNotFoundException($"Coverage report not found: {report}", report);
        var source = new SourceAnalyzer().AnalyzeFiles(files);
        coverage ??= reports.Select(CoverageReader.Read).ToArray();
        var detailed = CoverageMatcher.ApplyDetailed(source, coverage)
            .OrderBy(result => result.Metric.File, StringComparer.Ordinal)
            .ThenBy(result => result.Metric.DisplayName, StringComparer.Ordinal)
            .ThenBy(result => result.Metric.StartLine)
            .ToArray();
        var display = detailed.Select(result => result.Metric)
            .OrderBy(metric => metric.Crap is null ? 1 : 0).ThenByDescending(metric => metric.Crap)
            .ThenBy(metric => metric.File, StringComparer.Ordinal).ThenBy(metric => metric.StartLine).ToArray();

        lines.Add("CRAP      CC  Coverage  Method");
        foreach (var metric in display)
        {
            var obscuredViolation = metric.Crap is double raw && raw > options.Threshold &&
                raw.ToString("0.00", CultureInfo.InvariantCulture) == options.Threshold.ToString("0.00", CultureInfo.InvariantCulture);
            var crap = metric.Crap?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A";
            if (obscuredViolation) crap += ">";
            var coverageText = metric.Coverage is double value ? value.ToString("P1", CultureInfo.InvariantCulture) : "N/A";
            var relative = Path.GetRelativePath(workingDirectory, metric.File);
            lines.Add($"{crap,8} {metric.Complexity,3} {coverageText,9}  {relative}:{metric.StartLine} {metric.DisplayName}");
            if (obscuredViolation)
                lines.Add($"> raw CRAP {metric.Crap!.Value.ToString("R", CultureInfo.InvariantCulture)} > threshold {options.Threshold.ToString("R", CultureInfo.InvariantCulture)}");
        }
        var violations = detailed.Where(result => result.Metric.Crap > options.Threshold).ToArray();
        var missing = detailed.Count(result => result.Metric.Coverage is null);
        lines.Add($"Methods: {detailed.Length}; known coverage: {detailed.Length - missing}; missing coverage: {missing}; violations (> {options.Threshold.ToString(CultureInfo.InvariantCulture)}): {violations.Length}");

        var checks = new List<CheckResult>();
        checks.Add(testFailed
            ? new CheckResult("testExecution", "operationalError", "tests.failed", true)
            : new CheckResult("testExecution", reports.Count == options.Coverage.Count ? "notApplicable" : "pass",
                reports.Count == options.Coverage.Count ? "tests.skippedExplicitCoverage" : "tests.passed", reports.Count != options.Coverage.Count));
        if (missing > 0)
            checks.Add(options.AllowMissingCoverage
                ? new CheckResult("coverage", "skipped", "coverage.missingAllowed", false)
                : new CheckResult("coverage", "operationalError", detailed.First(result => result.CoverageReason is not null).CoverageReason!, true));
        else checks.Add(new CheckResult("coverage", "pass", "coverage.complete", true));
        checks.Add(new CheckResult("crap", violations.Length > 0 ? "fail" : detailed.Length == 0 ? "notApplicable" : "pass",
            violations.Length > 0 ? "crap.thresholdExceeded" : detailed.Length == 0 ? "crap.noEligibleMethods" : "crap.withinThreshold", true));

        string? error = testFailed ? "dotnet test failed; report shown from available coverage data." :
            missing > 0 && !options.AllowMissingCoverage ? "one or more analyzed methods have N/A coverage; pass --allow-missing-coverage to gate only known scores." : null;
        return new ExecutionOutcome
        {
            Files = files, Reports = reports, SourceMethods = source, Matches = detailed, Checks = checks,
            HumanLines = lines, DiagnosticLines = diagnostics, ErrorMessage = error, Commands = commands, RunArtifacts = runArtifacts
        };
    }

    private static ResultDocument BuildResult(ExecutionOutcome outcome, Options options, string workingDirectory,
        DateTimeOffset startedAt, DateTimeOffset finishedAt, TimeSpan duration)
    {
        var checks = outcome.Checks.Count > 0 ? outcome.Checks :
            [new CheckResult(outcome.Reason == "arguments.invalid" ? "arguments" : "execution",
                outcome.Reason == "run.cancelled" ? "cancelled" : outcome.Reason == "crap.noEligibleMethods" ? "notApplicable" : "operationalError",
                outcome.Reason ?? "execution.failed", true)];
        var metrics = outcome.Matches.Select(match =>
        {
            var source = outcome.SourceMethods.FirstOrDefault(item => item.File == match.Metric.File && item.StartLine == match.Metric.StartLine);
            return new MetricResult("legacy-syntax", NormalizePath(workingDirectory, match.Metric.File), match.Metric.DisplayName,
                source?.Signature, new SourceSpan(match.Metric.StartLine, match.Metric.EndLine), match.Metric.Complexity,
                match.Metric.Coverage, match.Metric.Crap, match.CoverageReason);
        }).OrderBy(metric => metric.ContextId, StringComparer.Ordinal).ThenBy(metric => metric.Path, StringComparer.Ordinal)
          .ThenBy(metric => metric.MethodIdentity, StringComparer.Ordinal).ThenBy(metric => metric.Span.StartLine).ToArray();
        var findings = metrics.Where(metric => metric.Crap > options.Threshold).Select(metric => new FindingResult(
            FindingIdentity.Create(metric.ContextId, metric.Path, metric.MethodIdentity, metric.Span, "crap.thresholdExceeded"),
            "crap.thresholdExceeded", "error", "complexity", metric.ContextId, metric.Path, metric.MethodIdentity,
            metric.Signature, metric.Span, metric.Complexity, metric.Coverage, metric.Crap, options.Threshold, "gt", "fail", []))
            .OrderBy(finding => finding.ContextId, StringComparer.Ordinal).ThenBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.MethodIdentity, StringComparer.Ordinal).ThenBy(finding => finding.Span.StartLine)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal).ToArray();
        var reduced = EvaluationDecisionReducer.Reduce(checks, findings.Length > 0);
        var reasonCounts = metrics.Where(metric => metric.CoverageReason is not null).GroupBy(metric => metric.CoverageReason!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var artifacts = outcome.Files.Select(path => Artifact(path, "source", workingDirectory))
            .Concat(outcome.Reports.Select(path => Artifact(path, "coverage", workingDirectory)))
            .OrderBy(artifact => artifact.Kind, StringComparer.Ordinal).ThenBy(artifact => artifact.Path, StringComparer.Ordinal).ToArray();
        return new ResultDocument(ResultContract.SchemaVersion, "0.1.0", ResultContract.ComplexityRulesetVersion,
            new EvaluationSection("legacy", new PolicyOptions(options.Threshold, options.AllowMissingCoverage),
                new EvaluationScope(".", outcome.Files.Select(path => NormalizePath(workingDirectory, path)).Order(StringComparer.Ordinal).ToArray()),
                [new EvaluationContext("legacy-syntax", "syntaxOnly", null, null, null, null, null)], checks, metrics, findings,
                new CoverageSummary(metrics.Length, metrics.Count(metric => metric.Coverage is not null), metrics.Count(metric => metric.Coverage is null), reasonCounts),
                artifacts, reduced.Decision),
            new RunSection(Guid.NewGuid().ToString("D"), startedAt, finishedAt, duration.TotalMilliseconds,
                outcome.Commands, [], outcome.RunArtifacts,
                new CancellationDetails(outcome.Cancelled, outcome.TimedOut, outcome.Cancelled ? outcome.Reason : outcome.TimedOut ? outcome.Reason : null),
                reduced.Status, reduced.ExitCode));
    }

    private static ArtifactIdentity Artifact(string path, string kind, string workingDirectory)
    {
        try { return new ArtifactIdentity(kind, NormalizePath(workingDirectory, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), "sha256"); }
        catch { return new ArtifactIdentity(kind, NormalizePath(workingDirectory, path), null, "unavailable"); }
    }

    private static string NormalizePath(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool HasJsonIntent(string[] args) => args.Select((value, index) => (value, index))
        .Any(pair => pair.value == "--format" && pair.index + 1 < args.Length && args[pair.index + 1] == "json");
    private static string? PreDetectValue(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++) if (args[index] == option) return args[index + 1];
        return null;
    }
    private static string? FindOutputAlias(string? output, string workingDirectory, IEnumerable<string> files, IEnumerable<string> reports, string? project)
    {
        if (output is null) return null;
        var destination = CanonicalPath(Path.GetFullPath(output, workingDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var input in files.Concat(reports).Concat(project is null ? [] : [project]))
            if (string.Equals(destination, CanonicalPath(Path.GetFullPath(input)), comparison)) return input;
        return null;
    }
    private static string CanonicalPath(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return path; }
    }
    private static bool IsHandled(Exception exception) => exception is ArgumentException or IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or OperationCanceledException;

    private static async Task<IReadOnlyList<string>> ChangedFilesAsync(string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var rootResult = await ProcessRunner.RunAsync("git", ["rev-parse", "--show-toplevel"], workingDirectory, timeout, cancellationToken);
        if (rootResult.ExitCode != 0) throw new InvalidOperationException($"git rev-parse failed: {rootResult.StandardError.Trim()}");
        var root = rootResult.StandardOutput.TrimEnd('\r', '\n');
        if (root.Length == 0) throw new InvalidOperationException("git rev-parse returned an empty repository root.");
        var statusResult = await ProcessRunner.RunAsync("git", ["status", "--porcelain=v1", "--untracked-files=all", "-z"], root, timeout, cancellationToken);
        if (statusResult.ExitCode != 0) throw new InvalidOperationException($"git status failed: {statusResult.StandardError.Trim()}");
        return GitChanges.ParsePorcelainV1Z(Encoding.UTF8.GetBytes(statusResult.StandardOutput), root);
    }

    private static string ResolveTestTarget(string? requested, string workingDirectory)
    {
        if (requested is not null)
        {
            var path = Path.GetFullPath(requested, workingDirectory);
            if (!File.Exists(path)) throw new FileNotFoundException($"Project or solution not found: {requested}", path);
            return path;
        }
        var solutions = Directory.EnumerateFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(workingDirectory, "*.slnx", SearchOption.TopDirectoryOnly)).ToArray();
        if (solutions.Length == 1) return solutions[0];
        if (solutions.Length > 1) throw new InvalidOperationException("Multiple solutions found; specify --project.");
        var projects = Directory.EnumerateFiles(workingDirectory, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
        if (projects.Length == 1) return projects[0];
        if (projects.Length > 1) throw new InvalidOperationException("Multiple projects found; specify --project.");
        throw new InvalidOperationException("No solution or project found in the working directory; specify --project.");
    }

    private sealed class Options
    {
        public List<string> Coverage { get; } = [];
        public List<string> Inputs { get; } = [];
        public string? Project { get; set; }
        public double Threshold { get; set; } = 8;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);
        public bool Changed { get; set; }
        public bool AllowMissingCoverage { get; set; }
        public string Format { get; set; } = "human";
        public string? Output { get; set; }
    }

    private sealed record ExecutionOutcome
    {
        public IReadOnlyList<string> Files { get; init; } = [];
        public IReadOnlyList<string> Reports { get; init; } = [];
        public IReadOnlyList<SourceMethod> SourceMethods { get; init; } = [];
        public IReadOnlyList<CoverageMatcher.DetailedMatch> Matches { get; init; } = [];
        public IReadOnlyList<CheckResult> Checks { get; init; } = [];
        public IReadOnlyList<string> HumanLines { get; init; } = [];
        public IReadOnlyList<string> DiagnosticLines { get; init; } = [];
        public IReadOnlyList<CommandRecord> Commands { get; init; } = [];
        public IReadOnlyList<RunArtifact> RunArtifacts { get; init; } = [];
        public string? Reason { get; init; }
        public string? ErrorMessage { get; init; }
        public string? OutputAliasReason { get; init; }
        public bool Cancelled { get; init; }
        public bool TimedOut { get; init; }

        public static ExecutionOutcome Failure(string reason, string message, bool cancelled = false, bool timedOut = false) =>
            new() { Reason = reason, ErrorMessage = message, Cancelled = cancelled, TimedOut = timedOut };

        public ExecutionOutcome WithOperationalFailure(string reason, string message) => this with
        {
            Checks = Checks.Concat([new CheckResult("resultOutput", "operationalError", reason, true)]).ToArray(),
            Reason = reason,
            ErrorMessage = message
        };
    }
}
