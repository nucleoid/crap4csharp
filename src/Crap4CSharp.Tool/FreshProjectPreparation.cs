using Crap4CSharp.Core;

namespace Crap4CSharp.Core;

// An internal preparation stage, not a completed public fresh-check implementation.
internal static class FreshProjectPreparation
{
    public static Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, bool noRestore,
        CancellationToken cancellationToken) => LoadAsync(request, noRestore, cancellationToken,
            ProjectContextLoader.DiscoverAsync, ProjectBuildPreparation.RestoreAsync, ProjectContextLoader.LoadAsync);

    internal static async Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, bool noRestore,
        CancellationToken cancellationToken,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>> discover,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProcessResult>> restore,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>> load)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        try
        {
            var discovery = await discover(request, deadline.Token);
            if (!discovery.Success) return discovery;
            if (!noRestore)
            {
                var restored = await restore(request, deadline.Token);
                if (restored.ExitCode != 0)
                    return new(false, [], [restored.StandardError, restored.StandardOutput], "context.restoreFailed");
            }
            return await load(request with { MetadataOnly = false }, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new(false, [], ["Project preparation exceeded its operation deadline."], "context.preparationTimeout");
        }
        catch (ProcessTimeoutException)
        {
            return new(false, [], ["Project preparation exceeded its operation deadline."], "context.preparationTimeout");
        }
    }
}
