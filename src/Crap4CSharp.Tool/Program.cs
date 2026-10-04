using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length == 3 && args[0] == ProjectContextLoader.LoaderCommand)
    return await MsBuildLoaderBootstrap.RunAsync(args[1], args[2], CancellationToken.None);

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
        Crap4CSharp - calculate CRAP metrics for C# authored callables

        Usage:
          crap4csharp [options] [file-or-directory ...]
          crap4csharp analyze --syntax-only [options] [file-or-directory ...]
          crap4csharp check --ruleset callables-v1 [options]

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
                             Legacy ordinary-methods-v1 inspection opt-out for N/A methods.
                             Not accepted by callables-v1.
          --changed          Analyze changed/untracked C# files from git porcelain status.
          --ruleset <id>     Select ordinary-methods-v1 (legacy option-only invocation) or
                             callables-v1 (analyze/check commands; analyze default).
          --callable-exemptions <json>
                             Load exact-match callable exemptions. CLI files are untrusted
                             local input and cannot approve enforcement by themselves.
          --syntax-only      Analyze captured source and coverage without launching child processes.
          --reuse-artifacts <manifest>
                             Replay a hash-bound captured bundle without loading projects or running processes.
          --format <value>   Render human (default) or json output.
          --output <path>    Atomically write the versioned JSON result document to path.
          -h, --help         Show help and perform no discovery, tests, or writes.

        Exit codes: 0 success, 1 usage/operational failure, 2 threshold exceeded.
        """;

    public static async Task<int> RunAsync(string[] args, string workingDirectory, TextWriter output, TextWriter error,
        CancellationToken cancellationToken, ProcessExecutor? processExecutor = null)
    {
        if (args.Any(arg => arg is "--help" or "-h")) { await output.WriteLineAsync(Help); return 0; }
        if (args.Length > 0 && args[0] is "analyze" or "check")
            return await RunCallableCommandAsync(args, workingDirectory, output, error, cancellationToken);

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

    private static async Task<int> RunCallableCommandAsync(string[] args, string workingDirectory,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        ModernOptions? options = null;
        try
        {
            options = ParseModern(args);
            if (options.Command == "check")
                throw new ArgumentException(options.Ruleset == ComplexityRules.OrdinaryMethodsV1
                    ? "check requires callables-v1; ordinary-methods-v1 is available through the legacy option-only invocation."
                    : "check orchestration is reserved for issue #10; use analyze --syntax-only for the issue #7 adapter.");
            cancellationToken.ThrowIfCancellationRequested();
            var result = options.ReuseArtifacts is not null
                ? AnalyzeCommand.Replay(options.ReuseArtifacts, workingDirectory, options.Output,
                    startedAt, stopwatch.Elapsed, cancellationToken)
                : options.Ruleset == ComplexityRules.OrdinaryMethodsV1
                ? await AnalyzeLegacyCapturedInputsAsync(options, workingDirectory, startedAt, stopwatch, cancellationToken)
                : AnalyzeCapturedInputs(options, workingDirectory, startedAt, stopwatch);
            var json = ResultWriter.Serialize(result);
            if (options.Output is not null)
            {
                var inputs = options.Inputs.Concat(options.Coverage)
                    .Concat(options.ReuseArtifacts is null ? [] : [options.ReuseArtifacts])
                    .Concat(options.Exemptions is null ? [] : [options.Exemptions]);
                if (FindOutputAlias(options.Output, workingDirectory, inputs, [], null) is not null)
                    throw new ArgumentException("Output path aliases a source, coverage, or exemption input.");
                await ResultWriter.WriteAtomicAsync(Path.GetFullPath(options.Output, workingDirectory), json,
                    CancellationToken.None);
            }
            if (options.Format == "json") await output.WriteAsync(json);
            else RenderCallableHuman(result, output);
            return result.Run.ExitCode;
        }
        catch (Exception exception) when (IsHandled(exception))
        {
            var reason = exception is ArgumentException ? "arguments.invalid" :
                exception is InvalidDataException ? "artifact.invalid" :
                exception is OperationCanceledException ? "run.cancelled" : "execution.failed";
            var ruleset = options?.Ruleset ?? PreDetectValue(args, "--ruleset") switch
            {
                ComplexityRules.OrdinaryMethodsV1 => ComplexityRules.OrdinaryMethodsV1,
                _ => ComplexityRules.CallablesV1
            };
            var result = BuildCallableFailure(args[0], ruleset, reason, startedAt, stopwatch.Elapsed);
            var format = options?.Format ?? (HasJsonIntent(args) ? "json" : "human");
            var destination = options?.Output ?? PreDetectValue(args, "--output");
            var mayWrite = false;
            if (destination is not null && args.Count(arg => arg == "--output") == 1)
            {
                if (options is not null)
                {
                    try
                    {
                        var inputs = options.Inputs.Concat(options.Coverage)
                            .Concat(options.ReuseArtifacts is null ? [] : [options.ReuseArtifacts])
                            .Concat(options.Exemptions is null ? [] : [options.Exemptions]);
                        mayWrite = FindOutputAlias(destination, workingDirectory, inputs, [], null) is null;
                        if (mayWrite && options.ReuseArtifacts is not null)
                        {
                            mayWrite = false;
                            ArtifactBundle.RejectOutputAliasForLocator(options.ReuseArtifacts, destination,
                                workingDirectory);
                            mayWrite = true;
                        }
                    }
                    catch (Exception aliasException) when (IsHandled(aliasException)) { }
                }
                else mayWrite = IsAbsentOutputDestination(destination, workingDirectory);
            }
            if (destination is not null && !mayWrite)
                result = result with
                {
                    Evaluation = result.Evaluation with
                    {
                        Checks = result.Evaluation.Checks.Concat(
                            [new CheckResult("output", "operationalError", "output.notWritten", true)]).ToArray()
                    }
                };
            var json = ResultWriter.Serialize(result);
            if (mayWrite)
            {
                try
                {
                    await ResultWriter.WriteAtomicAsync(Path.GetFullPath(destination!, workingDirectory), json,
                        CancellationToken.None);
                }
                catch (Exception writeException) when (IsHandled(writeException))
                {
                    await error.WriteLineAsync($"error: unable to write result document: {writeException.Message}");
                }
            }
            if (format == "json") await output.WriteAsync(json);
            else RenderCallableHuman(result, output);
            await error.WriteLineAsync($"error: {exception.Message}");
            return 1;
        }
    }

    private static ModernOptions ParseModern(string[] args)
    {
        var options = new ModernOptions { Command = args[0] };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var arg = args[index];
            string Value()
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {arg}.");
                return args[index];
            }
            switch (arg)
            {
                case "--syntax-only": options.SyntaxOnly = true; break;
                case "--reuse-artifacts":
                    if (!seen.Add(arg)) throw new ArgumentException("--reuse-artifacts may be specified only once.");
                    options.ReuseArtifacts = Value();
                    break;
                case "--project":
                    if (!seen.Add(arg)) throw new ArgumentException("--project may be specified only once.");
                    options.Project = Value();
                    break;
                case "--coverage": options.Coverage.Add(Value()); break;
                case "--coverage-path-map":
                    if (index + 2 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                        args[index + 2].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--coverage-path-map requires exactly two operands: <report-root> <local-root>.");
                    options.CoveragePathMappings.Add(new CoveragePathMapping(args[++index], args[++index]));
                    break;
                case "--coverage-path-case":
                    if (!seen.Add(arg)) throw new ArgumentException("--coverage-path-case may be specified only once.");
                    options.CoveragePathCase = Value() switch
                    {
                        "auto" => CoveragePathCase.Auto,
                        "sensitive" => CoveragePathCase.Sensitive,
                        "insensitive" => CoveragePathCase.Insensitive,
                        _ => throw new ArgumentException("Coverage path case must be auto, sensitive, or insensitive.")
                    };
                    break;
                case "--ruleset":
                    if (!seen.Add(arg)) throw new ArgumentException("--ruleset may be specified only once.");
                    options.Ruleset = Value();
                    break;
                case "--callable-exemptions":
                    if (!seen.Add(arg)) throw new ArgumentException("--callable-exemptions may be specified only once.");
                    options.Exemptions = Value();
                    break;
                case "--threshold":
                    if (!seen.Add(arg)) throw new ArgumentException("--threshold may be specified only once.");
                    if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
                        !double.IsFinite(threshold) || threshold < 0)
                        throw new ArgumentException("Threshold must be a finite non-negative number.");
                    options.Threshold = threshold;
                    break;
                case "--allow-missing-coverage": options.AllowMissingCoverage = true; break;
                case "--format":
                    if (!seen.Add(arg)) throw new ArgumentException("--format may be specified only once.");
                    options.Format = Value();
                    if (options.Format is not ("human" or "json")) throw new ArgumentException("Format must be human or json.");
                    break;
                case "--output":
                    if (!seen.Add(arg)) throw new ArgumentException("--output may be specified only once.");
                    options.Output = Value();
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}");
                    options.Inputs.Add(arg);
                    break;
            }
        }
        if (options.Ruleset is not (ComplexityRules.CallablesV1 or ComplexityRules.OrdinaryMethodsV1))
            throw new ArgumentException($"Unknown ruleset '{options.Ruleset}'.");
        if (options.Ruleset == ComplexityRules.CallablesV1 && options.AllowMissingCoverage)
            throw new ArgumentException("--allow-missing-coverage is only available with ordinary-methods-v1.");
        if (options.ReuseArtifacts is not null)
        {
            if (options.Command != "analyze" || options.SyntaxOnly || options.Inputs.Count > 0 || options.Coverage.Count > 0 ||
                options.CoveragePathMappings.Count > 0 || options.CoveragePathCase != CoveragePathCase.Auto ||
                options.Exemptions is not null || options.Project is not null || options.AllowMissingCoverage ||
                seen.Contains("--ruleset") || seen.Contains("--threshold"))
                throw new ArgumentException("--reuse-artifacts cannot be combined with live source, project, coverage, mapping, case, syntax-only, or exemption inputs.");
        }
        else if (options.Command == "analyze" && !options.SyntaxOnly)
            throw new ArgumentException("analyze requires either --reuse-artifacts or --syntax-only.");
        if (options.Command == "analyze" && options.SyntaxOnly && options.Project is not null)
            throw new ArgumentException("--project is not available with analyze --syntax-only.");
        return options;
    }

    private static async Task<ResultDocument> AnalyzeLegacyCapturedInputsAsync(ModernOptions modern,
        string workingDirectory, DateTimeOffset startedAt, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        if (modern.Coverage.Count == 0)
            throw new ArgumentException("Legacy syntax-only analyze requires at least one explicit --coverage report.");
        if (modern.Exemptions is not null)
            throw new ArgumentException("--callable-exemptions is not applicable to ordinary-methods-v1.");
        var options = new Options
        {
            Threshold = modern.Threshold,
            AllowMissingCoverage = modern.AllowMissingCoverage,
            Format = modern.Format,
            Output = modern.Output,
            InvocationMode = "analyze",
            Ruleset = ComplexityRules.OrdinaryMethodsV1
        };
        options.Inputs.AddRange(modern.Inputs);
        options.Coverage.AddRange(modern.Coverage);
        options.CoveragePathMappings.AddRange(modern.CoveragePathMappings);
        options.CoveragePathCase = modern.CoveragePathCase;
        var outcome = await ExecuteAsync(options, workingDirectory, cancellationToken,
            (_, _, _, _, _) => throw new InvalidOperationException("syntax-only analyze cannot launch child processes"),
            TextWriter.Null, TextWriter.Null, () => { });
        return BuildResult(outcome, options, workingDirectory, startedAt, DateTimeOffset.UtcNow, stopwatch.Elapsed);
    }

    private static ResultDocument AnalyzeCapturedInputs(ModernOptions options, string workingDirectory,
        DateTimeOffset startedAt, Stopwatch stopwatch)
    {
        var canonicalizer = new ExistingPathCanonicalizer(PathIdentityPolicy.Current);
        var files = SourceDiscovery.Discover(options.Inputs, workingDirectory, PathIdentityPolicy.Current, canonicalizer);
        if (files.Count == 0) throw new ArgumentException("No C# source files found.");
        var reports = options.Coverage.Select(path => Path.GetFullPath(path, workingDirectory)).ToArray();
        foreach (var report in reports)
            if (!File.Exists(report)) throw new FileNotFoundException($"Coverage report not found: {report}", report);
        var contextId = StableId("syntax-only", "unknown", "unknown", "AnyCPU", ComplexityRules.CallablesV1);
        var context = new CallableAnalysisContext(".", "unknown", "unknown", "AnyCPU",
            contextId, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        var references = CaptureSyntaxReferences();
        var inventories = files.Select(path => CallableInventory.Analyze(File.ReadAllText(path),
            NormalizePath(workingDirectory, path), context, references)).ToArray();
        var inventory = CallableInventory.Merge(inventories);

        var roots = files.Select(path => Path.GetDirectoryName(path)!).Distinct(PathIdentityPolicy.Current.Comparer).ToArray();
        var preparedMappings = options.CoveragePathMappings.Select(mapping => mapping with
        {
            LocalRoot = CanonicalExistingPath(mapping.LocalRoot, workingDirectory, PathIdentityPolicy.Current,
                canonicalizer: canonicalizer)
        }).ToArray();
        var captured = SourcePathCapture.Capture(files, roots, files, preparedMappings.Select(item => item.LocalRoot),
            workingDirectory: workingDirectory);
        var resolver = new CoveragePathResolver(PathIdentityPolicy.Current, captured, preparedMappings,
            options.CoveragePathCase);
        var reads = reports.Select(report => CoverageReader.ReadDetailed(report, resolver)).ToArray();
        if (reads.Select(item => item.ReportId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new ArgumentException("Syntax-only callables-v1 cannot safely combine distinct coverage reports without explicit build-context identity.");
        ThrowHardCoverageDiagnostic(reads.SelectMany(read => read.Diagnostics));
        var logicalPaths = captured.Entries.ToDictionary(item => item.LocalPath, item => item.LogicalPath,
            PathIdentityPolicy.Current.Comparer);
        var methods = reads.SelectMany(read => read.Methods).Select(method => method with
        {
            ContextId = contextId,
            File = method.File is not null && logicalPaths.TryGetValue(method.File, out var logical) ? logical : method.File
        }).ToArray();
        var resolved = CallableCoverageResolver.Resolve(inventory, methods);
        var byId = resolved.Observations.ToDictionary(item => item.ObservationId!, StringComparer.Ordinal);
        var callables = inventory.Callables.Select(item =>
        {
            var observation = byId[item.ObservationId];
            var coverage = observation.Status == "known" && observation.Points.Count > 0
                ? (double)observation.Points.Count(point => point.Visited) / observation.Points.Count : (double?)null;
            var crap = item.Complexity is int complexity && coverage is double known
                ? CrapCalculator.Calculate(complexity, known) : (double?)null;
            return new CallableResult(item.CallableId, item.ObservationId, KindName(item.Kind), item.ParentId,
                contextId, item.Path.Replace('\\', '/'), item.Span, item.Complexity,
                item.Applicability == CallableApplicability.Applicable ? "applicable" : "not-applicable",
                observation.Status, coverage, crap, observation.Reason, item.CoverageCapability, item.BodyChecksum)
            {
                Ruleset = item.Ruleset,
                SemanticSignature = item.SemanticIdentity?.ReportSignature,
                Documents = [item.Path.Replace('\\', '/')],
                MappingEvidenceKind = observation.Status == "known" ? "semanticSourceIdentity" : null
            };
        }).OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Span.Start).ToArray();
        var families = CallableFamilyEvaluator.Evaluate(inventory, resolved.Observations, options.Threshold);
        callables = callables.Select(item => item with
        {
            FamilyIds = families.Where(family => family.MemberObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
                .Select(family => family.FamilyId).Order(StringComparer.Ordinal).ToArray()
        }).ToArray();
        var metrics = callables.Select(item => new MetricResult(item.ContextId, item.Path, item.CallableId, null,
            new SourceSpan(item.Span.StartLine, item.Span.EndLine), item.Complexity ?? 0, item.Coverage, item.Crap,
            item.CoverageReason)).ToArray();
        var thresholdFindings = callables.Where(item => item.Crap > options.Threshold).Select(item =>
        {
            var span = new SourceSpan(item.Span.StartLine, item.Span.EndLine);
            return new FindingResult(FindingIdentity.Create(item.ContextId, item.Path, item.CallableId, span, "crap.thresholdExceeded"),
                item.CallableId, "crap.thresholdExceeded", "error", "complexity", item.ContextId, item.Path,
                item.CallableId, null, span, item.Complexity ?? 0, item.Coverage, item.Crap, item.CoverageReason,
                options.Threshold, "gt", "fail", []);
        }).Concat(families.Where(item => item.IsViolation).Select(item =>
        {
            var root = callables.Single(callable => callable.CallableId == item.RootCallableId);
            var span = new SourceSpan(root.Span.StartLine, root.Span.EndLine);
            return new FindingResult(FindingIdentity.Create(contextId, root.Path, item.FamilyId, span, CallableFamilyEvaluator.Rule),
                item.FamilyId, CallableFamilyEvaluator.Rule, "error", "complexity", contextId, root.Path,
                item.FamilyId, null, span, item.Complexity, item.Coverage, item.Crap, null, options.Threshold, "gt", "fail", []);
        })).ToArray();
        var unknown = callables.Where(item => item.Applicability == "applicable" && item.CoverageStatus != "known").ToArray();
        var unsupportedFindings = unknown.Where(item => item.CoverageReason is
                CoverageReasonCodes.UnsupportedGeneratedMapping or CoverageReasonCodes.UnsupportedCallable or
                CoverageReasonCodes.AmbiguousCallableOwnership).Select(item =>
        {
            var code = item.CoverageReason!;
            var span = new SourceSpan(item.Span.StartLine, item.Span.EndLine);
            return new FindingResult(FindingIdentity.Create(item.ContextId, item.Path, item.ObservationId, span, code),
                item.CallableId, code, "error", "completeness", item.ContextId, item.Path, item.CallableId, null,
                span, item.Complexity ?? 0, null, null, code, options.Threshold, "gt", "unknown", [code]);
        }).ToArray();
        var findings = thresholdFindings.Concat(unsupportedFindings)
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var exemptionValidation = options.Exemptions is null
            ? new CallableExemptionValidation([], [], false)
            : CallableExemptions.Validate(File.ReadAllBytes(Path.GetFullPath(options.Exemptions, workingDirectory)),
                ExemptionTrust.LocalUnreviewed, inventory, resolved.Observations, families);
        var checks = new List<CheckResult>
        {
            new("testExecution", "notApplicable", "tests.skippedSyntaxOnly", false),
            exemptionValidation.Errors.Count > 0
                ? new("callableExemptions", "operationalError", exemptionValidation.Errors[0], true)
                : new("callableExemptions", "notApplicable", options.Exemptions is null
                    ? "exemption.none" : "exemption.localUnreviewed", false),
            unknown.Length == 0 ? new("coverage", "pass", "coverage.complete", true)
                : new("coverage", "operationalError", unknown[0].CoverageReason ?? CoverageReasonCodes.Unavailable, true),
            new("crap", thresholdFindings.Length > 0 ? "fail" : callables.Any(item => item.Crap is not null) ? "pass" : "notApplicable",
                thresholdFindings.Length > 0 ? "crap.thresholdExceeded" : callables.Any(item => item.Crap is not null)
                    ? "crap.withinThreshold" : "crap.noKnownScores", true)
        };
        var reduced = EvaluationDecisionReducer.Reduce(checks, thresholdFindings.Length > 0);
        var reasonCounts = unknown.GroupBy(item => item.CoverageReason ?? CoverageReasonCodes.Unavailable, StringComparer.Ordinal)
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
        var artifacts = files.Select(path => Artifact(path, "source", workingDirectory, false))
            .Concat(reports.Select(path => Artifact(path, "coverage", workingDirectory, false)))
            .OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var evaluation = new EvaluationSection("analyze", new PolicyOptions(options.Threshold, options.AllowMissingCoverage),
            new EvaluationScope(".", files.Select(path => NormalizePath(workingDirectory, path)).Order(StringComparer.Ordinal).ToArray()),
            [new EvaluationContext(contextId, "syntaxOnly", null, "unknown", "unknown", null, null)], checks, metrics,
            findings, new CoverageSummary(callables.Length, callables.Count(item => item.CoverageStatus == "known"), unknown.Length,
                reasonCounts), artifacts, reduced.Decision)
        {
            Callables = callables,
            Families = families,
            CallableExemptions = exemptionValidation.Matches,
            ExemptionErrors = exemptionValidation.Errors,
            CoveragePathPolicy = new CoveragePathPolicyResult(options.CoveragePathCase.ToString().ToLowerInvariant(),
                resolver.MappingIdentities.Select(mapping => new CoveragePathMappingResult(
                    mapping.ReportRoot, mapping.LocalRoot, mapping.Id)).ToArray()),
            CoverageDiagnostics = reads.SelectMany(item => item.Diagnostics).Concat(resolved.Diagnostics)
                .GroupBy(item => item.Id, StringComparer.Ordinal).Select(item => item.First())
                .OrderBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray()
        };
        var run = new RunSection(Guid.NewGuid().ToString("D"), startedAt, DateTimeOffset.UtcNow,
            stopwatch.Elapsed.TotalMilliseconds, [], [], [], new CancellationDetails(false, false, null),
            reduced.Status, reduced.ExitCode);
        return new ResultDocument(ResultContract.SchemaVersion, ToolVersion, ComplexityRules.CallablesV1, evaluation, run);
    }

    private static ResultDocument BuildCallableFailure(string command, string ruleset, string reason,
        DateTimeOffset startedAt, TimeSpan duration)
    {
        var check = new CheckResult(reason == "arguments.invalid" ? "arguments" : "execution",
            reason == "run.cancelled" ? "cancelled" : "operationalError", reason, true);
        var decision = new EvaluationDecision(false, "unknown", reason);
        var evaluation = new EvaluationSection(command, new PolicyOptions(8, false),
            new EvaluationScope(".", []), [], [check], [], [],
            new CoverageSummary(0, 0, 0, new Dictionary<string, int>(StringComparer.Ordinal)), [], decision)
        {
            Callables = ruleset == ComplexityRules.CallablesV1 ? [] : null,
            Families = ruleset == ComplexityRules.CallablesV1 ? [] : null,
            CallableExemptions = ruleset == ComplexityRules.CallablesV1 ? [] : null,
            ExemptionErrors = ruleset == ComplexityRules.CallablesV1 ? [] : null
        };
        var run = new RunSection(Guid.NewGuid().ToString("D"), startedAt, DateTimeOffset.UtcNow,
            duration.TotalMilliseconds, [], [], [],
            new CancellationDetails(reason == "run.cancelled", false, reason == "run.cancelled" ? reason : null),
            reason == "run.cancelled" ? "cancelled" : "operationalError", 1);
        return new ResultDocument(ResultContract.SchemaVersion, ToolVersion, ruleset, evaluation, run);
    }

    private static string StableId(params string[] values) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))).ToLowerInvariant();

    private static IReadOnlyList<MetadataReference> CaptureSyntaxReferences() => new[]
        {
            typeof(object).Assembly.Location,
            typeof(Enumerable).Assembly.Location,
            typeof(Task).Assembly.Location
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal)
        .Select(path => MetadataReference.CreateFromImage(File.ReadAllBytes(path))).ToArray();

    private static string KindName(CallableKind kind) => kind switch
    {
        CallableKind.PropertyGet => "property-get", CallableKind.PropertySet => "property-set",
        CallableKind.PropertyInit => "property-init", CallableKind.IndexerGet => "indexer-get",
        CallableKind.IndexerSet => "indexer-set", CallableKind.IndexerInit => "indexer-init",
        CallableKind.EventAdd => "event-add", CallableKind.EventRemove => "event-remove",
        CallableKind.LocalFunction => "local-function", CallableKind.AnonymousMethod => "anonymous-method",
        CallableKind.TopLevel => "top-level", CallableKind.FieldInitializer => "field-initializer",
        CallableKind.EventInitializer => "event-initializer", CallableKind.PropertyInitializer => "property-initializer",
        CallableKind.PrimaryConstructorBaseArguments => "primary-constructor-base-arguments",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static void RenderCallableHuman(ResultDocument result, TextWriter output)
    {
        output.WriteLine($"Ruleset: {result.ComplexityRulesetVersion}");
        output.WriteLine(result.ComplexityRulesetVersion == ComplexityRules.OrdinaryMethodsV1
            ? "Inventory: limited ordinary-methods only"
            : "Inventory: authored callables-v1 regions");
        if (result.Evaluation.Callables is null)
        {
            output.WriteLine("CRAP      CC  Coverage  Method");
            foreach (var metric in result.Evaluation.Metrics)
            {
                var crap = metric.Crap?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A";
                var coverage = metric.Coverage is double value
                    ? value.ToString("P1", CultureInfo.InvariantCulture) : "N/A";
                output.WriteLine($"{crap,8} {metric.Complexity,3} {coverage,9}  {metric.Path}:{metric.Span.StartLine} {metric.MethodIdentity}");
            }
        }
        foreach (var item in result.Evaluation.Callables ?? [])
            output.WriteLine($"{item.Path}:{item.Span.StartLine} {item.Kind} {item.CallableId} CRAP={item.Crap?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A"}");
        output.WriteLine($"Completeness: known={result.Evaluation.Coverage.Known}; unknown={result.Evaluation.Coverage.Unknown}");
        output.WriteLine($"Findings: {result.Evaluation.Findings.Count}");
        foreach (var finding in result.Evaluation.Findings)
            output.WriteLine($"  {finding.Code} {finding.Path}:{finding.Span.StartLine} {finding.EntityKey}");
        foreach (var error in result.Evaluation.ExemptionErrors ?? [])
            output.WriteLine($"  exemption error: {error}");
        output.WriteLine($"Decision: {result.Evaluation.Decision.PolicyDecision} ({result.Evaluation.Decision.Reason}); exit={result.Run.ExitCode}");
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
                case "--ruleset":
                    var ruleset = Value();
                    if (ruleset != ComplexityRules.OrdinaryMethodsV1)
                        throw new ArgumentException("Legacy option-only invocation requires ordinary-methods-v1.");
                    options.Ruleset = ruleset;
                    break;
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
        lines.Add($"Ruleset: {options.Ruleset}");
        lines.Add("Inventory: limited ordinary-methods only");
        var commands = new List<CommandRecord>();
        var runArtifacts = new List<RunArtifact>();
        var generatedReports = new List<string>();
        var pathPolicy = PathIdentityPolicy.Current;
        var canonicalizer = new ExistingPathCanonicalizer(pathPolicy);
        var canonicalWorkingDirectory = CanonicalExistingPath(workingDirectory, workingDirectory, pathPolicy,
            canonicalizer: canonicalizer);
        var preparedMappings = options.CoveragePathMappings.Select(mapping => mapping with
        {
            LocalRoot = CanonicalExistingPath(mapping.LocalRoot, workingDirectory, pathPolicy,
                canonicalizer: canonicalizer)
        }).ToArray();
        var resolvedInputs = options.Inputs.Select(input => CanonicalExistingPath(input, workingDirectory, pathPolicy,
            canonicalizer: canonicalizer)).ToArray();
        var declaredRoots = DeclaredSourceRoots(resolvedInputs, canonicalWorkingDirectory);
        var explicitFiles = resolvedInputs.Where(File.Exists).ToArray();
        if (options.Changed)
        {
            var preflightInventory = SourcePathCapture.Capture([], declaredRoots, [],
                preparedMappings.Select(mapping => mapping.LocalRoot), workingDirectory: canonicalWorkingDirectory);
            _ = new CoveragePathResolver(PathIdentityPolicy.Current, preflightInventory,
                preparedMappings, options.CoveragePathCase);
        }
        var files = options.Changed
            ? await ChangedFilesAsync(workingDirectory, options.Timeout, cancellationToken, processExecutor)
            : SourceDiscovery.Discover(options.Inputs, workingDirectory, pathPolicy, canonicalizer);
        var sourceInventory = SourcePathCapture.Capture(files, declaredRoots, explicitFiles,
            preparedMappings.Select(mapping => mapping.LocalRoot), workingDirectory: canonicalWorkingDirectory);
        var sourceLogicalPaths = sourceInventory.Entries.ToDictionary(
            entry => entry.LocalPath, entry => entry.LogicalPath, PathIdentityPolicy.Current.Comparer);
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
            SourceLogicalPaths = sourceLogicalPaths,
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
                    SourceLogicalPaths = sourceLogicalPaths,
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
                        SourceLogicalPaths = sourceLogicalPaths,
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
                LogicalPath = sourceLogicalPaths[PathIdentityPolicy.Current.Normalize(method.File)]
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
                SourceLogicalPaths = sourceLogicalPaths,
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
                SourceLogicalPaths = sourceLogicalPaths,
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
        string ResultPath(string path) => outcome.SourceLogicalPaths.TryGetValue(PathIdentityPolicy.Current.Normalize(path), out var logical)
            ? logical
            : NormalizePath(workingDirectory, path);
        var metrics = outcome.Matches.Select(match =>
        {
            return new MetricResult("legacy-syntax", ResultPath(match.Metric.File), match.Metric.DisplayName,
                match.Source.Signature, new SourceSpan(match.Metric.StartLine, match.Metric.EndLine), match.Metric.Complexity,
                match.Metric.Coverage, match.Metric.Crap, match.CoverageReason)
            {
                CoverageStatus = match.Metric.CoverageStatus
            };
        }).OrderBy(metric => metric.ContextId, StringComparer.Ordinal).ThenBy(metric => metric.Path, StringComparer.Ordinal)
          .ThenBy(metric => metric.MethodIdentity, StringComparer.Ordinal).ThenBy(metric => metric.Span.StartLine).ToArray();
        var findings = outcome.Matches.Where(match => match.Metric.Crap > options.Threshold).Select(match =>
        {
            var path = ResultPath(match.Metric.File);
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
        var artifacts = outcome.Files.Select(path => Artifact(path, "source", workingDirectory, generated: false, ResultPath(path)))
            .Concat(outcome.Reports.Select(path => Artifact(path, "coverage", workingDirectory, generatedReports.Contains(path))))
            .Distinct()
            .OrderBy(artifact => artifact.Kind, StringComparer.Ordinal).ThenBy(artifact => artifact.Path, StringComparer.Ordinal).ToArray();
        var pathMappings = options.ValidatedCoveragePathMappings.Select(mapping => new CoveragePathMappingResult(
            mapping.ReportRoot, mapping.LocalRoot, mapping.Id))
            .OrderBy(mapping => mapping.ReportRoot, StringComparer.Ordinal).ThenBy(mapping => mapping.LocalRoot, StringComparer.Ordinal).ToArray();
        var evaluation = new EvaluationSection(options.InvocationMode, new PolicyOptions(options.Threshold, options.AllowMissingCoverage),
                new EvaluationScope(".", outcome.Files.Select(ResultPath).Order(StringComparer.Ordinal).ToArray()),
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
        return new ResultDocument(ResultContract.SchemaVersion, ToolVersion, options.Ruleset,
            evaluation, run);
    }

    private static ArtifactIdentity Artifact(string path, string kind, string workingDirectory, bool generated, string? logicalOverride = null)
    {
        try
        {
            var contentIdentity = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var logicalPath = generated ? $"<generated>/coverage/{contentIdentity}.xml" : logicalOverride ?? NormalizePath(workingDirectory, path);
            return new ArtifactIdentity(kind, logicalPath, contentIdentity, "sha256");
        }
        catch
        {
            var logicalPath = generated ? "<generated>/coverage/unavailable.xml" : logicalOverride ?? NormalizePath(workingDirectory, path);
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

    private static IReadOnlyList<string> DeclaredSourceRoots(IEnumerable<string> resolvedInputs, string workingDirectory)
    {
        var values = resolvedInputs.ToArray();
        if (values.Length == 0) return [workingDirectory];
        return values.Select(path => File.Exists(path) ? Path.GetDirectoryName(path)! : path)
            .Distinct(PathIdentityPolicy.Current.Comparer).Order(StringComparer.Ordinal).ToArray();
    }

    internal static string CanonicalExistingPath(string path, string workingDirectory,
        PathIdentityPolicy? pathPolicy = null, Func<string, bool>? exists = null,
        ExistingPathCanonicalizer? canonicalizer = null)
    {
        pathPolicy ??= PathIdentityPolicy.Current;
        var normalized = pathPolicy.Normalize(path, workingDirectory);
        var accepted = exists?.Invoke(normalized) ?? (File.Exists(normalized) || Directory.Exists(normalized));
        return accepted ? (canonicalizer ?? new ExistingPathCanonicalizer(pathPolicy)).NormalizeExisting(normalized) : normalized;
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

    private sealed class ModernOptions
    {
        public required string Command { get; init; }
        public string Ruleset { get; set; } = ComplexityRules.CallablesV1;
        public bool SyntaxOnly { get; set; }
        public string? ReuseArtifacts { get; set; }
        public string? Project { get; set; }
        public double Threshold { get; set; } = 8;
        public bool AllowMissingCoverage { get; set; }
        public string Format { get; set; } = "human";
        public string? Output { get; set; }
        public string? Exemptions { get; set; }
        public List<string> Coverage { get; } = [];
        public List<CoveragePathMapping> CoveragePathMappings { get; } = [];
        public CoveragePathCase CoveragePathCase { get; set; } = CoveragePathCase.Auto;
        public List<string> Inputs { get; } = [];
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
        public string InvocationMode { get; set; } = "legacy";
        public string Ruleset { get; set; } = ComplexityRules.OrdinaryMethodsV1;
    }

    private sealed record ExecutionOutcome
    {
        public IReadOnlyList<string> Files { get; init; } = [];
        public IReadOnlyList<string> Reports { get; init; } = [];
        public IReadOnlyDictionary<string, string> SourceLogicalPaths { get; init; } =
            new Dictionary<string, string>(PathIdentityPolicy.Current.Comparer);
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
