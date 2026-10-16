namespace Crap4CSharp.Core;

internal static class SdkExecutionIdentity
{
    internal static ProjectContextSdkResolution Validate(ProjectContextSdkResolution frozen, ProjectContextSdkResolution current)
    {
        var paths = PathIdentityPolicy.Current.Comparer;
        if (frozen.Version != current.Version || frozen.MsBuildVersion != current.MsBuildVersion ||
            frozen.ResolutionRoute != current.ResolutionRoute || !paths.Equals(frozen.Path, current.Path) ||
            !paths.Equals(frozen.DotNetRoot, current.DotNetRoot) || !paths.Equals(frozen.HostPath, current.HostPath))
            throw new ProjectContextException("context.sdkMismatch", "The current SDK selection differs from the invocation's frozen execution identity.");
        return frozen;
    }

    internal static async Task<ProjectContextSdkResolution> ResolveForEffectAsync(string directory, TimeSpan timeout,
        CancellationToken token, bool nativeProbe, ProjectContextSdkResolution? frozen)
    {
        var current = await ProjectContextLoader.ResolveSdkAsync(directory, timeout, token, nativeProbe);
        return frozen is null ? current : Validate(frozen, current);
    }
}
