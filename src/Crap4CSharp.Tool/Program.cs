using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
    internal delegate Task<ProcessResult> ProcessExecutor(string fileName, IEnumerable<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken);

    private const string Help = """
        Crap4CSharp - calculate CRAP metrics for C# methods

        Usage:
          crap4csharp [options] [file-or-directory ...]

        Options:
          --coverage <xml>   Use an OpenCover or Cobertura/Coverlet XML report; repeatable.
                             Supplying coverage skips dotnet test.
          --coverage-path-map <report-root> <local-root>
                             Translate an absolute report root to an existing selected-source
                             directory; repeatable. Longest component prefix wins.
          --coverage-path-case <auto|sensitive|insensitive>
                             Compare foreign report roots using dialect defaults (auto), or the
                             explicit policy. Local source identity is unchanged.
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

    public static async Task<int> RunAsync(string[] args, string workingDirectory, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, ProcessExecutor? processExecutor = null)
    {
        if (args.Any(arg => arg is "--help" or "-h")) { await output.WriteLineAsync(Help); return 0; }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var jsonIntent = HasJsonIntent(args);
        var requestedOutput = PreDetectValue(args, "--output");
        Options? options = null;
        var rawAliasChecked = false;
        var aliasChecked = false;
        var absentPreParsedOutputIsSafe = IsAbsentOutputDestination(requestedOutput, workingDirectory) &&
            args.Count(arg => arg == "--output") == 1;
        ExecutionOutcome outcome;
        try
        {
            options = args.Length == 0 ? new Options() : Parse(args);
            var implicitTargets = options.Project is null && options.Coverage.Count == 0
                ? PotentialTestTargets(workingDirectory) : [];
            var rawAlias = FindOutputAlias(options.Output, workingDirectory,
                options.Inputs.Concat(implicitTargets), options.Coverage, options.Project);
            if (rawAlias is not null)
            {
                outcome = ExecutionOutcome.Failure("output.aliasesInput", $"Output path aliases {rawAlias}.") with
                {
                    OutputAliasReason = rawAlias,
                    AliasChecked = true
                };
            }
            else
            {
                rawAliasChecked = true;
                var progress = options.Format == "json" ? error : output;
                outcome = await ExecuteAsync(options, workingDirectory, cancellationToken,
                    processExecutor ?? ProcessRunner.RunAsync, progress, error, () => aliasChecked = true);
            }
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            var cancelled = exception is OperationCanceledException;
            var timedOut = exception is ProcessTimeoutException;
            var reason = exception is ArgumentException ? "arguments.invalid" : exception is CoveragePathException pathException
                ? pathException.Code : cancelled ? "run.cancelled" : timedOut ? "run.timeout" : "execution.failed";
            var message = exception is CoveragePathException ? $"{reason}: {exception.Message}" : exception.Message;
            outcome = ExecutionOutcome.Failure(reason, message, cancelled, timedOut) with
            {
                AliasChecked = aliasChecked,
                CoverageDiagnostics = exception is CoveragePathException
                    ? [CoverageDiagnostic.Create(reason, CoverageDiagnosticStage.Path, CoverageDiagnosticSeverity.Error,
                        CoverageDiagnosticScope.Report, message: reason)]
                    : []
            };
        }

        var format = options?.Format ?? (jsonIntent ? "json" : "human");
        // A pre-detected value is only evidence when parsing failed. Once parsing succeeds,
        // only the value accepted by the parser may select a write destination.
        var outputPath = options is not null ? options.Output : requestedOutput;
        var canWriteResult = outputPath is not null && outcome.OutputAliasReason is null &&
            (outcome.AliasChecked || ((rawAliasChecked || options is null) && absentPreParsedOutputIsSafe));
        if (outputPath is not null && outcome.OutputAliasReason is null && !canWriteResult)
            outcome = outcome.WithOutputNotWritten(outputPath);
        var result = BuildResult(outcome, options ?? new Options { Format = format, Output = outputPath }, workingDirectory,
            startedAt, DateTimeOffset.UtcNow, stopwatch.Elapsed);
        var json = ResultWriter.Serialize(result);

        if (outputPath is not null && canWriteResult)
        {
            try
            {
                await ResultWriter.WriteAtomicAsync(Path.GetFullPath(outputPath, workingDirectory), json, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
            {
                outcome = outcome.WithOperationalFailure("output.writeFailed", exception.Message);
                result = BuildResult(outcome, options ?? new Options { Format = format, Output = outputPath }, workingDirectory,
                    startedAt, DateTimeOffset.UtcNow, stopwatch.Elapsed);
                json = ResultWriter.Serialize(result);
            }
        }

        try
        {
            if (format == "json")
            {
                if (outcome.ErrorMessage is not null) await error.WriteLineAsync($"error: {outcome.ErrorMessage}");
                await output.WriteAsync(json);
            }
            else
            {
                foreach (var line in outcome.HumanLines) await output.WriteLineAsync(line);
                if (outcome.ErrorMessage is not null) await error.WriteLineAsync($"error: {outcome.ErrorMessage}");
                if (outcome.Reason == "arguments.invalid") await error.WriteLineAsync("Run 'crap4csharp --help' for usage.");
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            try { await error.WriteLineAsync($"error: unable to write terminal output: {exception.Message}"); }
            catch (Exception diagnosticException) when (diagnosticException is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
            return 1;
        }
        return result.Run.ExitCode;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        var seenFormat = false;
        var seenOutput = false;
        var seenCoveragePathCase = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string Value()
            {
                if (++index >= args.Length) throw new ArgumentException($"Missing value for {arg}.");
                if (args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {arg}.");
                return args[index];
            }
            switch (arg)
            {
                case "--coverage": options.Coverage.Add(Value()); break;
                case "--coverage-path-map":
                    if (index + 2 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                        args[index + 2].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--coverage-path-map requires exactly two operands: <report-root> <local-root>.");
                    options.CoveragePathMappings.Add(new CoveragePathMapping(args[++index], args[++index]));
                    break;
                case "--coverage-path-case":
                    if (seenCoveragePathCase) throw new ArgumentException("--coverage-path-case may be specified only once.");
                    seenCoveragePathCase = true;
                    options.CoveragePathCase = Value() switch
                    {
                        "auto" => CoveragePathCase.Auto,
                        "sensitive" => CoveragePathCase.Sensitive,
                        "insensitive" => CoveragePathCase.Insensitive,
                        _ => throw new ArgumentException("Coverage path case must be auto, sensitive, or insensitive.")
                    };
                    break;
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

    private static async Task<ExecutionOutcome> ExecuteAsync(Options options, string workingDirectory, CancellationToken cancellationToken,
        ProcessExecutor processExecutor, TextWriter progress, TextWriter diagnosticOutput, Action markAliasChecked)
    {
        var lines = new List<string>();
        var commands = new List<CommandRecord>();
        var runArtifacts = new List<RunArtifact>();
        var generatedReports = new List<string>();
        var preparedMappings = options.CoveragePathMappings.Select(mapping => mapping with
        {
            LocalRoot = Path.GetFullPath(mapping.LocalRoot, workingDirectory)
        }).ToArray();
        var declaredRoots = DeclaredSourceRoots(options.Inputs, workingDirectory);
        var explicitFiles = options.Inputs.Where(input => File.Exists(Path.GetFullPath(input, workingDirectory)))
            .Select(input => Path.GetFullPath(input, workingDirectory)).ToArray();
        if (options.Changed)
        {
            var preflightInventory = SourcePathCapture.Capture([], declaredRoots, [],
                preparedMappings.Select(mapping => mapping.LocalRoot));
            _ = new CoveragePathResolver(PathIdentityPolicy.Current, preflightInventory,
                preparedMappings, options.CoveragePathCase);
        }
        var files = options.Changed
            ? await ChangedFilesAsync(workingDirectory, options.Timeout, cancellationToken, processExecutor)
            : SourceDiscovery.Discover(options.Inputs, workingDirectory);
        var sourceInventory = SourcePathCapture.Capture(files, declaredRoots, explicitFiles,
            preparedMappings.Select(mapping => mapping.LocalRoot));
        var pathResolver = new CoveragePathResolver(PathIdentityPolicy.Current, sourceInventory,
            preparedMappings, options.CoveragePathCase);
        options.ValidatedCoveragePathMappings = pathResolver.MappingIdentities;
        var reports = options.Coverage.Select(path => Path.GetFullPath(path, workingDirectory)).ToList();
        var project = options.Project is null ? null : Path.GetFullPath(options.Project, workingDirectory);
        var implicitTargets = project is null && reports.Count == 0 ? PotentialTestTargets(workingDirectory) : [];
        var alias = FindOutputAlias(options.Output, workingDirectory, files.Concat(implicitTargets), reports, project);
        if (alias is not null) return ExecutionOutcome.Failure("output.aliasesInput", $"Output path aliases {alias}.") with
        {
            Files = files,
            Reports = reports,
            OutputAliasReason = alias,
            AliasChecked = true
        };
        markAliasChecked();

        IReadOnlyList<SourceMethod> source = [];
        CoverageMatcher.DetailedMatch[] detailed = [];
        var coverageDiagnostics = new List<CoverageDiagnostic>();
        var coverageEvidence = new List<CoverageRunEvidence>();
        var checks = new List<CheckResult>();
        try
        {
            IReadOnlyList<CoverageMethod>[]? coverage = null;
            if (reports.Count > 0)
            {
                foreach (var report in reports)
                    if (!File.Exists(report)) throw new FileNotFoundException($"Coverage report not found: {report}", report);
                var reads = reports.Select(report => CoverageReader.ReadDetailed(report, pathResolver)).ToArray();
                coverage = reads.Select(read => read.Methods).ToArray();
                CaptureCoverageEvidence(reads, coverageDiagnostics, coverageEvidence);
                ThrowHardCoverageDiagnostic(coverageDiagnostics);
            }
            if (files.Count == 0)
            {
                lines.Add("No C# source files found.");
                return new ExecutionOutcome
                {
                    Files = files,
                    Reports = reports,
                    HumanLines = lines,
                    Reason = "crap.noEligibleMethods",
                    CoverageDiagnostics = coverageDiagnostics,
                    CoverageEvidence = coverageEvidence,
                    AliasChecked = true
                };
            }

            var testFailed = false;
            if (reports.Count == 0)
            {
                var target = ResolveTestTarget(options.Project, workingDirectory);
                var resultsDirectory = Path.Combine(Path.GetTempPath(), "crap4csharp", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(resultsDirectory);
                await progress.WriteLineAsync($"Running coverage: dotnet test {target}");
                await progress.WriteLineAsync($"Coverage results: {resultsDirectory}");
                var arguments = new[] { "test", target, "-m:1", "--collect:XPlat Code Coverage", "--results-directory", resultsDirectory,
                "--", "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover" };
                commands.Add(new CommandRecord("dotnet", arguments, null));
                runArtifacts.Add(new RunArtifact("coverageResults", resultsDirectory, "retained", false, false));
                var process = await processExecutor("dotnet", arguments, workingDirectory, options.Timeout, cancellationToken);
                commands[^1] = new CommandRecord("dotnet", arguments, process.ExitCode);
                if (!string.IsNullOrWhiteSpace(process.StandardOutput)) await progress.WriteAsync(process.StandardOutput);
                if (!string.IsNullOrWhiteSpace(process.StandardError)) await diagnosticOutput.WriteAsync(process.StandardError);
                testFailed = process.ExitCode != 0;
                generatedReports.AddRange(Directory.EnumerateFiles(resultsDirectory, "*.xml", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).Contains("coverage", StringComparison.OrdinalIgnoreCase)));
                reports.AddRange(generatedReports);
                runArtifacts[^1] = new RunArtifact("coverageResults", resultsDirectory, "retained", reports.Count > 0, false);
                if (reports.Count == 0)
                {
                    var noCoverageChecks = new List<CheckResult>
                {
                    new("testExecution", testFailed ? "operationalError" : "pass", testFailed ? "tests.failed" : "tests.passed", true),
                    new("coverage", "operationalError", "coverage.notProduced", true)
                };
                    return new ExecutionOutcome
                    {
                        Files = files,
                        Reports = reports,
                        Checks = noCoverageChecks,
                        ErrorMessage = "dotnet test produced no coverage XML. Add coverlet.collector to the test project or pass --coverage.",
                        Reason = "coverage.notProduced",
                        Commands = commands,
                        RunArtifacts = runArtifacts,
                        GeneratedReports = generatedReports,
                        AliasChecked = true
                    };
                }
            }

            foreach (var report in reports)
                if (!File.Exists(report)) throw new FileNotFoundException($"Coverage report not found: {report}", report);
            source = new SourceAnalyzer().AnalyzeFiles(files).Select(method => method with
            {
                LogicalPath = NormalizePath(workingDirectory, method.File)
            }).ToArray();
            if (coverage is null)
            {
                var reads = reports.Select(report => CoverageReader.ReadDetailed(report, pathResolver)).ToArray();
                coverage = reads.Select(read => read.Methods).ToArray();
                CaptureCoverageEvidence(reads, coverageDiagnostics, coverageEvidence);
                ThrowHardCoverageDiagnostic(coverageDiagnostics);
            }
            var matchResult = CoverageMatcher.ApplyDetailedResult(source, coverage);
            coverageDiagnostics.AddRange(matchResult.Diagnostics);
            detailed = matchResult.Matches
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
            foreach (var match in detailed.Where(match => match.CoverageReason is not null))
                lines.Add($"N/A reason: {Path.GetRelativePath(workingDirectory, match.Metric.File)}:{match.Metric.StartLine} {match.CoverageReason}");
            var violations = detailed.Where(result => result.Metric.Crap > options.Threshold).ToArray();
            var missing = detailed.Count(result => result.Metric.Coverage is null);
            lines.Add($"Methods: {detailed.Length}; known coverage: {detailed.Length - missing}; missing coverage: {missing}; violations (> {options.Threshold.ToString(CultureInfo.InvariantCulture)}): {violations.Length}");

            checks.Add(testFailed
                ? new CheckResult("testExecution", "operationalError", "tests.failed", true)
                : new CheckResult("testExecution", reports.Count == options.Coverage.Count ? "notApplicable" : "pass",
                    reports.Count == options.Coverage.Count ? "tests.skippedExplicitCoverage" : "tests.passed", reports.Count != options.Coverage.Count));
            if (missing > 0)
                checks.Add(options.AllowMissingCoverage
                    ? new CheckResult("coverage", "skipped", "coverage.missingAllowed", false)
                    : new CheckResult("coverage", "operationalError", detailed.First(result => result.CoverageReason is not null).CoverageReason!, true));
            else checks.Add(new CheckResult("coverage", "pass", "coverage.complete", true));
            var knownScores = detailed.Count(result => result.Metric.Crap is not null);
            checks.Add(new CheckResult("crap", violations.Length > 0 ? "fail" : knownScores == 0 ? "notApplicable" : "pass",
                violations.Length > 0 ? "crap.thresholdExceeded" : knownScores == 0 ? "crap.noKnownScores" : "crap.withinThreshold", true));

            string? operationError = testFailed ? "dotnet test failed; report shown from available coverage data." :
                missing > 0 && !options.AllowMissingCoverage ? "one or more analyzed methods have N/A coverage; pass --allow-missing-coverage to gate only known scores." : null;
            return new ExecutionOutcome
            {
                Files = files,
                Reports = reports,
                SourceMethods = source,
                Matches = detailed,
                Checks = checks,
                HumanLines = lines,
                ErrorMessage = operationError,
                Commands = commands,
                RunArtifacts = runArtifacts,
                GeneratedReports = generatedReports,
                CoverageDiagnostics = coverageDiagnostics,
                CoverageEvidence = coverageEvidence,
                AliasChecked = true
            };
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            var cancelled = exception is OperationCanceledException || cancellationToken.IsCancellationRequested;
            var timedOut = !cancelled && exception is ProcessTimeoutException;
            var reason = exception is CoveragePathException pathException ? pathException.Code :
                cancelled ? "run.cancelled" : timedOut ? "run.timeout" : "execution.failed";
            var failureCheck = new CheckResult(commands.Count > 0 ? "testExecution" : "execution",
                cancelled ? "cancelled" : "operationalError", reason, true);
            return new ExecutionOutcome
            {
                Files = files,
                Reports = reports,
                GeneratedReports = generatedReports,
                SourceMethods = source,
                Matches = detailed,
                Checks = checks.Concat([failureCheck]).ToArray(),
                HumanLines = lines,
                ErrorMessage = exception is CoveragePathException ? $"{reason}: {exception.Message}" : exception.Message,
                Reason = reason,
                CancellationReason = cancelled || timedOut ? reason : null,
                Commands = commands,
                RunArtifacts = runArtifacts,
                CoverageDiagnostics = coverageDiagnostics,
                CoverageEvidence = coverageEvidence,
                AliasChecked = true,
                Cancelled = cancelled,
                TimedOut = timedOut
            };
        }
    }

    private static ResultDocument BuildResult(ExecutionOutcome outcome, Options options, string workingDirectory,
        DateTimeOffset startedAt, DateTimeOffset finishedAt, TimeSpan duration)
    {
        var checks = EffectiveChecks(outcome);
        var metrics = outcome.Matches.Select(match =>
        {
            return new MetricResult("legacy-syntax", NormalizePath(workingDirectory, match.Metric.File), match.Metric.DisplayName,
                match.Source.Signature, new SourceSpan(match.Metric.StartLine, match.Metric.EndLine), match.Metric.Complexity,
                match.Metric.Coverage, match.Metric.Crap, match.CoverageReason)
            {
                CoverageStatus = match.Metric.CoverageStatus
            };
        }).OrderBy(metric => metric.ContextId, StringComparer.Ordinal).ThenBy(metric => metric.Path, StringComparer.Ordinal)
          .ThenBy(metric => metric.MethodIdentity, StringComparer.Ordinal).ThenBy(metric => metric.Span.StartLine).ToArray();
        var findings = outcome.Matches.Where(match => match.Metric.Crap > options.Threshold).Select(match =>
        {
            var path = NormalizePath(workingDirectory, match.Metric.File);
            var span = new SourceSpan(match.Metric.StartLine, match.Metric.EndLine);
            return new FindingResult(
            FindingIdentity.Create("legacy-syntax", path, match.Metric.DisplayName, span, "crap.thresholdExceeded"),
            EntityIdentity.Create("legacy-syntax", path, match.Source.CanonicalSignature, "crap.thresholdExceeded"),
            "crap.thresholdExceeded", "error", "complexity", "legacy-syntax", path, match.Metric.DisplayName,
            match.Source.Signature, span, match.Metric.Complexity, match.Metric.Coverage, match.Metric.Crap, match.CoverageReason,
            options.Threshold, "gt", "fail", [])
            {
                CoverageStatus = match.Metric.CoverageStatus
            };
        })
            .OrderBy(finding => finding.ContextId, StringComparer.Ordinal).ThenBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.MethodIdentity, StringComparer.Ordinal).ThenBy(finding => finding.Span.StartLine)
            .ThenBy(finding => finding.Code, StringComparer.Ordinal).ToArray();
        var reduced = EvaluationDecisionReducer.Reduce(checks, findings.Length > 0);
        var reasonCounts = metrics.Where(metric => metric.CoverageReason is not null).GroupBy(metric => metric.CoverageReason!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var generatedReports = outcome.GeneratedReports.ToHashSet(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var artifacts = outcome.Files.Select(path => Artifact(path, "source", workingDirectory, generated: false))
            .Concat(outcome.Reports.Select(path => Artifact(path, "coverage", workingDirectory, generatedReports.Contains(path))))
            .Distinct()
            .OrderBy(artifact => artifact.Kind, StringComparer.Ordinal).ThenBy(artifact => artifact.Path, StringComparer.Ordinal).ToArray();
        var pathMappings = options.ValidatedCoveragePathMappings.Select(mapping => new CoveragePathMappingResult(
            mapping.ReportRoot, NormalizePath(workingDirectory, mapping.LocalRoot), mapping.Id))
            .OrderBy(mapping => mapping.ReportRoot, StringComparer.Ordinal).ThenBy(mapping => mapping.LocalRoot, StringComparer.Ordinal).ToArray();
        var evaluation = new EvaluationSection("legacy", new PolicyOptions(options.Threshold, options.AllowMissingCoverage),
                new EvaluationScope(".", outcome.Files.Select(path => NormalizePath(workingDirectory, path)).Order(StringComparer.Ordinal).ToArray()),
                [new EvaluationContext("legacy-syntax", "syntaxOnly", null, null, null, null, null)], checks, metrics, findings,
                new CoverageSummary(metrics.Length, metrics.Count(metric => metric.Coverage is not null), metrics.Count(metric => metric.Coverage is null), reasonCounts),
                artifacts, reduced.Decision)
        {
            CoveragePathPolicy = new CoveragePathPolicyResult(options.CoveragePathCase.ToString().ToLowerInvariant(), pathMappings),
            CoverageDiagnostics = outcome.CoverageDiagnostics.GroupBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .Select(group => group.First()).OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToArray()
        };
        var run = new RunSection(Guid.NewGuid().ToString("D"), startedAt, finishedAt, duration.TotalMilliseconds,
                outcome.Commands, [], outcome.RunArtifacts,
                new CancellationDetails(outcome.Cancelled, outcome.TimedOut, outcome.CancellationReason),
                reduced.Status, reduced.ExitCode)
        {
            CoverageEvidence = outcome.CoverageEvidence.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray()
        };
        return new ResultDocument(ResultContract.SchemaVersion, ToolVersion, ResultContract.ComplexityRulesetVersion,
            evaluation, run);
    }

    private static ArtifactIdentity Artifact(string path, string kind, string workingDirectory, bool generated)
    {
        try
        {
            var contentIdentity = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var logicalPath = generated ? $"<generated>/coverage/{contentIdentity}.xml" : NormalizePath(workingDirectory, path);
            return new ArtifactIdentity(kind, logicalPath, contentIdentity, "sha256");
        }
        catch
        {
            var logicalPath = generated ? "<generated>/coverage/unavailable.xml" : NormalizePath(workingDirectory, path);
            return new ArtifactIdentity(kind, logicalPath, null, "unavailable");
        }
    }

    private static string ToolVersion => typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string NormalizePath(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool HasJsonIntent(string[] args) => args.Select((value, index) => (value, index))
        .Any(pair => pair.value == "--format" && pair.index + 1 < args.Length && args[pair.index + 1] == "json");
    private static string? PreDetectValue(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++) if (args[index] == option) return args[index + 1];
        return null;
    }
    private static bool IsAbsentOutputDestination(string? output, string workingDirectory)
    {
        if (output is null) return false;
        try
        {
            var path = Path.GetFullPath(output, workingDirectory);
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    private static string? FindOutputAlias(string? output, string workingDirectory, IEnumerable<string> files, IEnumerable<string> reports, string? project)
    {
        if (output is null) return null;
        var destinationPath = Path.GetFullPath(output, workingDirectory);
        if (IsProjectInputExtension(destinationPath)) return destinationPath;
        var destination = CanonicalPath(destinationPath);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var input in files.Concat(reports).Concat(project is null ? [] : [project]))
        {
            var inputPath = Path.GetFullPath(input, workingDirectory);
            var canonicalInput = CanonicalPath(inputPath);
            if (destination is null || canonicalInput is null || string.Equals(destination, canonicalInput, comparison)) return input;
        }
        return null;
    }

    private static bool IsProjectInputExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".csproj" or ".sln" or ".slnx" or ".props" or ".targets";
    private static string? CanonicalPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (root is null) return null;
            var current = root;
            foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0) continue;
                var candidate = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
                var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
                current = target?.FullName ?? candidate;
            }
            return Path.GetFullPath(current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
    private static IReadOnlyList<string> PotentialTestTargets(string workingDirectory) =>
        Directory.EnumerateFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(workingDirectory, "*.slnx", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(workingDirectory, "*.csproj", SearchOption.TopDirectoryOnly))
            .ToArray();

    private static IReadOnlyList<CheckResult> EffectiveChecks(ExecutionOutcome outcome) => outcome.Checks.Count > 0
        ? outcome.Checks
        : [new CheckResult(outcome.Reason == "arguments.invalid" ? "arguments" : "execution",
            outcome.Reason == "run.cancelled" ? "cancelled" : outcome.Reason == "crap.noEligibleMethods" ? "notApplicable" : "operationalError",
            outcome.Reason ?? "execution.failed", true)];
    private static bool IsHandled(Exception exception) => exception is ArgumentException or IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or OperationCanceledException;

    private static IReadOnlyList<string> DeclaredSourceRoots(IEnumerable<string> inputs, string workingDirectory)
    {
        var values = inputs.ToArray();
        if (values.Length == 0) return [Path.GetFullPath(workingDirectory)];
        return values.Select(value => Path.GetFullPath(value, workingDirectory))
            .Select(path => File.Exists(path) ? Path.GetDirectoryName(path)! : path)
            .Distinct(PathIdentityPolicy.Current.Comparer).Order(StringComparer.Ordinal).ToArray();
    }

    private static void CaptureCoverageEvidence(
        IEnumerable<CoverageReadResult> reads,
        ICollection<CoverageDiagnostic> diagnostics,
        ICollection<CoverageRunEvidence> evidence)
    {
        var evidenceIds = evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var read in reads)
        {
            foreach (var diagnostic in read.Diagnostics) diagnostics.Add(diagnostic);
            foreach (var method in read.Methods.Where(method => method.PathResolution is not null))
            {
                var resolution = method.PathResolution!;
                var id = resolution.Diagnostic?.EvidenceRefs.FirstOrDefault() ??
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", read.ReportId,
                        method.ObservationId, resolution.Status, resolution.MappingId)))).ToLowerInvariant();
                if (evidenceIds.Add(id))
                    evidence.Add(new CoverageRunEvidence(id, read.ReportId, method.ObservationId, method.ReportedFile,
                        resolution.LocalPath, resolution.Status.ToString().ToLowerInvariant(), resolution.MappingId));
            }
            foreach (var diagnostic in read.Diagnostics)
            {
                foreach (var evidenceRef in diagnostic.EvidenceRefs)
                {
                    if (!evidenceIds.Add(evidenceRef)) continue;
                    evidence.Add(new CoverageRunEvidence(evidenceRef, read.ReportId, diagnostic.ObservationId,
                        null, null, diagnostic.Code, diagnostic.MappingId));
                }
            }
        }
    }

    private static void ThrowHardCoverageDiagnostic(IEnumerable<CoverageDiagnostic> diagnostics)
    {
        var hard = diagnostics.FirstOrDefault(diagnostic => diagnostic.Code is CoverageReasonCodes.InvalidPath or
            CoverageReasonCodes.PathOutsideRoot or CoverageReasonCodes.PathMappingConflict);
        if (hard is not null) throw new CoveragePathException(hard.Code, hard.Message);
    }

    private static async Task<IReadOnlyList<string>> ChangedFilesAsync(string workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken, ProcessExecutor processExecutor)
    {
        var rootResult = await processExecutor("git", ["rev-parse", "--show-toplevel"], workingDirectory, timeout, cancellationToken);
        if (rootResult.ExitCode != 0) throw new InvalidOperationException($"git rev-parse failed: {rootResult.StandardError.Trim()}");
        var root = rootResult.StandardOutput.TrimEnd('\r', '\n');
        if (root.Length == 0) throw new InvalidOperationException("git rev-parse returned an empty repository root.");
        var statusResult = await processExecutor("git", ["status", "--porcelain=v1", "--untracked-files=all", "-z"], root, timeout, cancellationToken);
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
        public List<CoveragePathMapping> CoveragePathMappings { get; } = [];
        public List<string> Inputs { get; } = [];
        public string? Project { get; set; }
        public double Threshold { get; set; } = 8;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);
        public bool Changed { get; set; }
        public bool AllowMissingCoverage { get; set; }
        public CoveragePathCase CoveragePathCase { get; set; } = CoveragePathCase.Auto;
        public IReadOnlyList<CoveragePathMappingIdentity> ValidatedCoveragePathMappings { get; set; } = [];
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
        public IReadOnlyList<CommandRecord> Commands { get; init; } = [];
        public IReadOnlyList<RunArtifact> RunArtifacts { get; init; } = [];
        public IReadOnlyList<string> GeneratedReports { get; init; } = [];
        public IReadOnlyList<CoverageDiagnostic> CoverageDiagnostics { get; init; } = [];
        public IReadOnlyList<CoverageRunEvidence> CoverageEvidence { get; init; } = [];
        public string? Reason { get; init; }
        public string? ErrorMessage { get; init; }
        public string? OutputAliasReason { get; init; }
        public string? CancellationReason { get; init; }
        public bool AliasChecked { get; init; }
        public bool Cancelled { get; init; }
        public bool TimedOut { get; init; }

        public static ExecutionOutcome Failure(string reason, string message, bool cancelled = false, bool timedOut = false) =>
            new()
            {
                Reason = reason,
                ErrorMessage = message,
                CancellationReason = cancelled || timedOut ? reason : null,
                Cancelled = cancelled,
                TimedOut = timedOut
            };

        public ExecutionOutcome WithOperationalFailure(string reason, string message) => this with
        {
            Checks = EffectiveChecks(this).Concat([new CheckResult("resultOutput", "operationalError", reason, true)]).ToArray(),
            Reason = reason,
            ErrorMessage = message
        };

        public ExecutionOutcome WithOutputNotWritten(string path) => this with
        {
            Checks = EffectiveChecks(this).Concat([new CheckResult("resultOutput", "skipped", "output.notWritten", false)]).ToArray(),
            ErrorMessage = string.IsNullOrWhiteSpace(ErrorMessage)
                ? $"result file {path} was not written; existing content may be stale"
                : $"{ErrorMessage}{Environment.NewLine}error: result file {path} was not written; existing content may be stale"
        };
    }
}
