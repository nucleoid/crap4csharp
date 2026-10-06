namespace Crap4CSharp.Core;

// Raw invocation observations only. Neither exit zero nor artifact filenames establish
// successful tests, coverage provenance or reusable binding. The caller owes those gates.
internal sealed record TestInvocationObservation(string GraphId, string ProjectPath,
    string ResultsDirectory, LoggedProcessResult Process);

internal static class TestExecutionAdapter
{
    internal static async Task<IReadOnlyList<TestInvocationObservation>> RunAsync(CheckExecutionPlan plan,
        string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        => await RunCoreAsync(plan, null, ownedDirectory, timeout, cancellationToken);

    internal static async Task<IReadOnlyList<TestInvocationObservation>> RunVerifiedAsync(CheckExecutionPlan plan,
        CompilerGraphPhaseResult bound, string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var proof = bound.RetainedProof ?? throw Refuse("Bound compiler execution did not retain phase evidence through tests.");
        try { CompilerGraphExecution.ValidateFinalGraph(plan.PreparedGraph, bound.FinalGraph); }
        catch (ProjectContextException) { throw Refuse("Bound compiler evidence belongs to a different immutable test plan."); }
        proof.ValidateCurrent(cancellationToken);
        return await RunCoreAsync(plan, proof, ownedDirectory, timeout, cancellationToken);
    }

    private static async Task<IReadOnlyList<TestInvocationObservation>> RunCoreAsync(CheckExecutionPlan plan,
        CompilerRetainedProof? proof, string ownedDirectory, TimeSpan timeout, CancellationToken cancellationToken)
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
        var launches = required.Select((node, index) => (Node: node, Arguments: CreateArguments(node), Index: index)).ToArray();
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
        foreach (var launch in launches)
        {
            token.ThrowIfCancellationRequested();
            proof?.ValidateCurrent(token);
            var entry = Path.Combine(directory, launch.Index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            // Caller ownership plus empty-root validation prevents ambient result reuse;
            // this is not an atomic hostile-filesystem publication guarantee.
            Directory.CreateDirectory(entry);
            var resultDirectory = Path.Combine(entry, "results");
            var logs = Path.Combine(entry, "logs");
            Directory.CreateDirectory(resultDirectory); Directory.CreateDirectory(logs);
            var sdk = await ProjectContextLoader.ResolveSdkAsync(Path.GetDirectoryName(launch.Node.ProjectPath)!, timeout, token);
            var arguments = launch.Arguments.Concat(new[] { "--logger", "trx;LogFileName=tests.trx", "--results-directory", resultDirectory,
                "--collect", "XPlat Code Coverage", "--", "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover" }).ToArray();
            var process = await ProcessRunner.RunLoggedAsync(sdk.HostPath, arguments, Path.GetDirectoryName(launch.Node.ProjectPath)!, timeout, token, logs);
            // Preserve nonzero outcomes and complete owned logs. A later evidence gate
            // may inspect partial coverage; this adapter never converts failure to success.
            results.Add(new(launch.Node.Id, launch.Node.ProjectPath, resultDirectory, process));
            proof?.ValidateCurrent(token);
        }
        token.ThrowIfCancellationRequested();
        if (proof is not null) await proof.ValidateFinalAsync(token);
        return Array.AsReadOnly(results.ToArray());
    }

    internal static IReadOnlyList<string> CreateArguments(ProjectBuildGraphNode node)
    {
        var arguments = new List<string> { "test", node.ProjectPath, "--no-build", "--no-restore", "-m:1", "-nr:false" };
        arguments.AddRange(MappedGlobalArguments.Create(node.GlobalProperties));
        arguments.AddRange(MappedGlobalArguments.Create(new Dictionary<string,string>
            { ["Configuration"] = node.Configuration, ["Platform"] = node.Platform, ["TargetFramework"] = node.TargetFramework }));
        // Mapped globals follow CLI switches and can override --no-build. Force the
        // effective supported VSTest property AFTER every mapped value and selector.
        arguments.Add("-p:VSTestNoBuild=true");
        arguments.Add("-p:BuildProjectReferences=false");
        return Array.AsReadOnly(arguments.ToArray());
    }
    private static bool Contains(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent)
            ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }
    private static ProjectContextException Refuse(string message) => new("test.planInvalid", message);
}
