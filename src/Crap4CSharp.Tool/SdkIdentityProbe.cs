using System.Text.Json;

namespace Crap4CSharp.Core;

// Invocation-owned identity probe, not a graph/compilation/proof cache.
internal static class SdkIdentityProbe
{
    internal const string Command = "__crap4csharp-sdk-identity-v1";
    internal sealed record Request(string Directory, string Host);

    internal static async Task<SdkIdentityObservation> QueryAsync(string directory, string host,
        TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathRooted(host)) return new("unsupported", 0);
        var scratch = Directory.CreateTempSubdirectory("crap4csharp-sdk-identity-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var input = Path.Combine(scratch, "request.json");
            var output = Path.Combine(scratch, "response.json");
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new Request(Path.GetFullPath(directory), host)), token);
            var process = await ProcessRunner.RunProtocolAsync(host,
                [typeof(SdkIdentityProbe).Assembly.Location, Command, input, output], directory, timeout,
                token, maximumBytesPerStream: 16384);
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length > 16384)
                throw new ProjectContextException("context.sdkResolutionFailed", "The isolated SDK identity probe did not return bounded evidence.");
            var observation = JsonSerializer.Deserialize<SdkIdentityObservation>(await File.ReadAllTextAsync(output, token));
            if (observation is null || observation.Status is not ("resolved" or "failed" or "unsupported"))
                throw new ProjectContextException("context.sdkResolutionFailed", "The isolated SDK identity response is invalid.");
            if (observation.Status == "resolved" && (observation.Code != 0 || observation.Version != "10.0.103" ||
                string.IsNullOrEmpty(observation.Path) || !Directory.Exists(observation.Path) ||
                string.IsNullOrEmpty(observation.MsBuildVersion) || string.IsNullOrEmpty(observation.DotNetRoot) ||
                !PathIdentityPolicy.Current.Comparer.Equals(observation.HostPath, Path.GetFullPath(host))))
                throw new ProjectContextException("context.sdkResolutionFailed", "The resolved SDK identity is incomplete or host-incompatible.");
            token.ThrowIfCancellationRequested();
            return observation;
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal static async Task<int> RunWorkerAsync(string input, string output)
    {
        if (!File.Exists(input) || new FileInfo(input).Length > 16384) return 1;
        var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(input));
        if (request is null) return 1;
        var observation = NativeSdkIdentity.TryObserve(request.Directory, request.Host, CancellationToken.None);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(observation));
        return 0; // Valid private response, not public execution acceptance.
    }
}
