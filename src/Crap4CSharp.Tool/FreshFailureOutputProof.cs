using Crap4CSharp.Core;

// Permission to replace a prior result is established only after the complete
// prepared compiler graph has passed destination protection. This is not a
// compiler/test success proof, and cannot authorize an early preflight write.
internal sealed class FreshFailureOutputProof(FreshCheckPreflight preflight,
    ProjectContextLoadRequest request, ProjectContextLoadResult originalGraph,
    IReadOnlyList<string> priorInputs)
{
    internal async Task ValidateAsync()
    {
        // Failure/cancellation must not cancel the safety check itself. Bound the
        // independent reload; any incomplete inspection leaves stdout-only output.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var current = await ProjectContextLoader.LoadGraphAsync(request with
            { Timeout = TimeSpan.FromSeconds(30) }, timeout.Token);
        if (!current.Success)
            throw new ProjectContextException(current.FailureReason ?? "context.loadFailed",
                "Failure output requires complete current evaluated input protection.");
        var resultOnly = preflight with { Request = preflight.Request with { ArtifactOutput = null } };
        FreshCheckPreflightLoader.ValidateDestinations(resultOnly.Request, resultOnly.Root,
            priorInputs.Concat(preflight.Trusted.ContentHashes.Keys)
                .Concat(preflight.Request.ArtifactOutput is null ? [] : [preflight.Request.ArtifactOutput]));
        // Never lose an original input when consumer membership changes, and never
        // treat a newly selected source/import/config/output as a disposable result.
        FreshCheckExecution.ProtectPreparedDestinations(resultOnly, originalGraph);
        FreshCheckExecution.ProtectPreparedDestinations(resultOnly, current);
        timeout.Token.ThrowIfCancellationRequested();
    }
}
