using Crap4CSharp.Core;

namespace Crap4CSharp.Core;

// An internal preparation stage, not a completed public fresh-check implementation.
internal static class FreshProjectPreparation
{
    public static Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, bool noRestore,
        CancellationToken cancellationToken) => LoadAsync(request, noRestore, cancellationToken,
            ProjectContextLoader.DiscoverAsync, ProjectBuildPreparation.RestoreAsync, ProjectContextLoader.LoadAsync);

    internal static Task<ProjectContextLoadResult> LoadAsync(ProjectContextLoadRequest request, bool noRestore,
        CancellationToken cancellationToken,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>> discover,
        Func<string, TimeSpan, CancellationToken, Task<ProcessResult>> restore,
        Func<ProjectContextLoadRequest, CancellationToken, Task<ProjectContextLoadResult>> load) =>
        load(request, cancellationToken);
}
