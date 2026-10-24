namespace Crap4CSharp.Core;

// Raw invocation observations only. Neither exit zero nor artifact filenames establish
// successful tests, coverage provenance or reusable binding. The caller owes those gates.
internal sealed record TestInvocationObservation(string GraphId, string ProjectPath,
    string ResultsDirectory, LoggedProcessResult Process)
{
    internal TestChildObservation? ChildBuild { get; init; }
}

// Raw process outcomes remain distinct from evidence failures. Passing tests followed
// by drift are completed observations, never an accepted verification result.
internal sealed record TestContextOutcome(string GraphId, string State, string? ResultsDirectory,
    string? LogDirectory, LoggedProcessResult? Process);

internal sealed class TestExecutionRun(IReadOnlyList<TestInvocationObservation> invocations,
    IReadOnlyList<TestContextOutcome> outcomes, Exception? failure) : IReadOnlyList<TestInvocationObservation>
{
    public IReadOnlyList<TestContextOutcome> Outcomes { get; } = outcomes;
    public Exception? Failure { get; } = failure;
    public int Count => invocations.Count;
    public TestInvocationObservation this[int index] => invocations[index];
    public IEnumerator<TestInvocationObservation> GetEnumerator() => invocations.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class TestExecutionAdapter
{
    internal static async Task<TestExecutionRun> RunAsync(CheckExecutionPlan plan,
        string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        => await RunCoreAsync(plan, null, ownedDirectory, timeout, cancellationToken);

    internal static async Task<TestExecutionRun> RunVerifiedAsync(CheckExecutionPlan plan,
        CompilerGraphPhaseResult bound, string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        => await RunVerifiedWithTimingAsync(plan, bound, ownedDirectory, timeout, cancellationToken, null);

    internal static async Task<TestExecutionRun> RunVerifiedWithTimingAsync(CheckExecutionPlan plan,
        CompilerGraphPhaseResult bound, string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        global::CompletedProofTiming? timing)
        => await RunVerifiedWithSdkObserverAsync(plan, bound, ownedDirectory, timeout, cancellationToken, null, timing);

    internal static async Task<TestExecutionRun> RunVerifiedWithSdkObserverAsync(CheckExecutionPlan plan,
        CompilerGraphPhaseResult bound, string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        Func<string, TimeSpan, CancellationToken, bool, Task<ProjectContextSdkResolution>>? observeSdk,
        global::CompletedProofTiming? timing = null)
    {
        var proof = bound.RetainedProof ?? throw Refuse("Bound compiler execution did not retain phase evidence through tests.");
        try { CompilerGraphExecution.ValidateFinalGraph(plan.PreparedGraph, bound.FinalGraph); }
        catch (ProjectContextException) { throw Refuse("Bound compiler evidence belongs to a different immutable test plan."); }
        using (timing?.Start("execution.test.openingPhysical"))
            proof.ValidateCurrent(cancellationToken);
        return await RunCoreAsync(plan, proof, ownedDirectory, timeout, cancellationToken, observeSdk, timing);
    }

    private static async Task<TestExecutionRun> RunCoreAsync(CheckExecutionPlan plan,
        CompilerRetainedProof? proof, string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        Func<string, TimeSpan, CancellationToken, bool, Task<ProjectContextSdkResolution>>? observeSdk = null,
        global::CompletedProofTiming? timing = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        // Freeze/check the complete schedule and property transport BEFORE any SDK or test launch.
        var nodes = CompilerGraphExecution.ValidateAndFreeze(plan.PreparedGraph);
        var required = nodes.Where(n => plan.PreparedGraph.GraphContexts.Single(b => b.GraphId == n.Id).IsTestProject).ToArray();
        if (required.Length == 0 || !required.Select(n => n.Id).SequenceEqual(plan.Tests.Select(t => t.GraphId)))
            throw Refuse("Required test schedule differs from the complete immutable plan.");
        var launches = required.Select((node, index) => (Node: node, Arguments: CreateArguments(node), Index: index,
            IncludeTests: plan.PreparedGraph.GraphContexts.Single(b => b.GraphId == node.Id).Context.ExclusionPolicy.IncludeTests)).ToArray();
        var directory = Path.GetFullPath(ownedDirectory);
        if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
            throw Refuse("Test execution requires an existing empty invocation-owned directory.");
        for (var ancestor = directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw Refuse("Reparse-point test result destinations are unsupported.");
        foreach (var binding in plan.PreparedGraph.GraphContexts)
        {
            var context = binding.Context;
            if (Contains(directory, binding.ProjectPath) ||
                Contains(context.EvaluatedIntermediateDirectory!, directory) ||
                Contains(Path.GetDirectoryName(context.EvaluatedTargetPath!)!, directory) ||
                context.PhaseInputs.Any(input => Contains(directory, input.Path)))
                throw Refuse("Test results must not contain graph inputs or lie inside compiler output directories.");
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<TestInvocationObservation>();
        var outcomes = launches.Select(l => new TestContextOutcome(l.Node.Id, "not-run", null, null, null)).ToArray();
        Exception? failure = null;
        foreach (var launch in launches)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                // RunVerifiedAsync validates before entering this synchronous
                // setup; subsequent launches follow the prior child's closing
                // validation. No consumer process runs between those boundaries.
                var entry = Path.Combine(directory, launch.Index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
                // Empty owned root prevents ambient reuse; not hostile-filesystem attestation.
                Directory.CreateDirectory(entry);
                var resultDirectory = Path.Combine(entry, "results");
                var logs = Path.Combine(entry, "logs");
                Directory.CreateDirectory(resultDirectory); Directory.CreateDirectory(logs);
                ProjectContextSdkResolution currentSdk;
                using (timing?.Start("execution.test.sdk"))
                    currentSdk = await (observeSdk ?? ProjectContextLoader.ResolveSdkAsync)(Path.GetDirectoryName(launch.Node.ProjectPath)!, timeout, token,
                        proof?.NativeSdkIdentityProbe ?? false);
                var frozenSdk = proof?.FrozenSdk ?? plan.PreparedGraph.SelectedSdk;
                var sdk = frozenSdk is null ? currentSdk : SdkExecutionIdentity.Validate(frozenSdk, currentSdk);
                var childLog = Path.Combine(logs, "test-child.binlog");
                if (childLog.IndexOfAny([';', ',']) >= 0) throw Refuse("Test-child logger paths with delimiters are unsupported.");
                var arguments = launch.Arguments.Concat(new[] { "-bl:" + childLog + ";ProjectImports=None", "--logger", "trx;LogFileName=tests.trx", "--results-directory", resultDirectory })
                    .Concat(CreateCollectorArguments(proof, launch.IncludeTests, launch.Node.Id)).ToArray();
                // Preserve interrupted launch diagnostics without manufacturing an exit code.
                outcomes[launch.Index] = new(launch.Node.Id, "failed", resultDirectory, logs, null);
                LoggedProcessResult process;
                using (timing?.Start("execution.test.process"))
                    process = await ProcessRunner.RunLoggedAsync(sdk.HostPath, arguments, Path.GetDirectoryName(launch.Node.ProjectPath)!, timeout, token, logs);
                results.Add(new(launch.Node.Id, launch.Node.ProjectPath, resultDirectory, process));
                outcomes[launch.Index] = new(launch.Node.Id, process.ExitCode == 0 ? "completed" : "failed", resultDirectory, logs, process);
                if (!OperatingSystem.IsWindows() && File.Exists(childLog)) File.SetUnixFileMode(childLog, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                TestChildObservation observed;
                using (timing?.Start("execution.test.evidence"))
                    observed = await TestChildEvidence.ReadAsync(childLog, launch.Node.ProjectPath, timeout, token,
                        proof?.NativeSdkIdentityProbe ?? false, sdk);
                results[^1] = results[^1] with { ChildBuild = observed };
                TestChildEvidence.Validate(observed, process);
                using (timing?.Start("execution.test.closingPhysical"))
                    proof?.ValidateCurrent(token);
            }
            catch (Exception error) when (IsOperational(error))
            {
                failure = error;
                break; // Invalidated evidence stops all remaining launches.
            }
        }
        if (failure is null)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using (timing?.Start("execution.test.finalGraph"))
                    if (proof is not null) await proof.ValidateFinalAsync(token);
            }
            catch (Exception error) when (IsOperational(error)) { failure = error; }
        }
        return new(Array.AsReadOnly(results.ToArray()), Array.AsReadOnly(outcomes), failure);
    }

    internal static IReadOnlyList<string> CreateCollectorArguments(CompilerRetainedProof? proof, bool includeTests, string? graphId = null)
    {
        var arguments = new List<string> { "--collect", "XPlat Code Coverage", "--",
            "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover",
            "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.IncludeTestAssembly=" + (includeTests ? "true" : "false") };
        // RunVerified's existing opening ValidateCurrent has already proved the
        // SAME retained argv/physical sources/PE/PDB map. Unverified/null keeps
        // the collector default; caller-replaceable bound.Observations are not read.
        if (proof?.HasRecordedPathMap == true)
        {
            if (graphId is null) throw Refuse("Mapped collector selection requires the admitted test invocation.");
            var include = CreateLiteralCollectorInclude(proof.RecordedInvocationModuleNames(graphId));
            arguments.Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeAssembliesWithoutSources=None");
            arguments.Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include=" + include);
        }
        return Array.AsReadOnly(arguments.ToArray());
    }

    // Coverlet 6.0.4 splits Include on commas and treats [assembly]* as a
    // wildcard filter. Admit only unambiguous literals; never omit an unsafe
    // closure member or widen it to a wildcard. All types remain included.
    internal static string CreateLiteralCollectorInclude(IReadOnlyList<string> moduleNames)
    {
        if (moduleNames.Count is 0 or > 4096) throw Refuse("Collector closure is empty or too large.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filters = new List<string>();
        var length = 0;
        foreach (var name in moduleNames)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 256 ||
                name.Any(character => !((character is >= 'a' and <= 'z') || (character is >= 'A' and <= 'Z') ||
                    (character is >= '0' and <= '9') || character is '_' or '.')) || !names.Add(name))
                throw Refuse("Collector closure contains an unsupported or ambiguous assembly name.");
            var filter = "[" + name + "]*";
            length += filter.Length + (filters.Count == 0 ? 0 : 1);
            if (length > 16 * 1024) throw Refuse("Collector closure filter exceeds its finite argument bound.");
            filters.Add(filter);
        }
        return string.Join(",", filters.Order(StringComparer.Ordinal));
    }

    internal static IReadOnlyList<string> CreateArguments(ProjectBuildGraphNode node)
    {
        var arguments = new List<string> { "test", node.ProjectPath, "--no-build", "--no-restore", "-m:1", "-nr:false" };
        arguments.AddRange(MappedGlobalArguments.Create(node.GlobalProperties));
        arguments.AddRange(MappedGlobalArguments.Create(new Dictionary<string, string>
        { ["Configuration"] = node.Configuration, ["Platform"] = node.Platform, ["TargetFramework"] = node.TargetFramework }));
        // Mapped globals follow CLI switches and can override --no-build. Force the
        // effective supported VSTest property AFTER every mapped value and selector.
        arguments.Add("-p:VSTestNoBuild=true");
        arguments.Add("-p:BuildProjectReferences=false");
        return Array.AsReadOnly(arguments.ToArray());
    }
    private static bool IsOperational(Exception error) => error is InvalidOperationException or IOException or InvalidDataException or
        UnauthorizedAccessException or OperationCanceledException or ProcessTimeoutException or System.ComponentModel.Win32Exception;

    private static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent)
            ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }
    private static ProjectContextException Refuse(string message) => new("test.planInvalid", message);
}
