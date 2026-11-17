using System.Text.Json;
using Microsoft.Build.Locator;

namespace Crap4CSharp.Core;

// Explicit private diagnostic command, never a graph/provenance provider.
internal static class WarmAcquisitionDiagnosticBootstrap
{
    internal const string Command = "__warm-acquisition-diagnostic";
    internal sealed record Request(ProjectContextLoadRequest Loader, bool CancelOnLiveMessage);
    internal sealed record Result(string Status, string[] Trace, string[] Diagnostics);

    internal static async Task<int> RunAsync(string requestPath, string responsePath)
    {
        Result result;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(requestPath), ProjectContextLoader.JsonOptions)
                ?? throw new InvalidDataException("Missing diagnostic request.");
            var loader = request.Loader;
            if (string.IsNullOrWhiteSpace(loader.SdkPath) || !Directory.Exists(loader.SdkPath))
                throw new InvalidDataException("Selected SDK unavailable.");
            if (!string.IsNullOrWhiteSpace(loader.DotNetRoot)) Environment.SetEnvironmentVariable("DOTNET_ROOT", loader.DotNetRoot);
            if (!string.IsNullOrWhiteSpace(loader.DotNetHostPath))
            {
                Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", loader.DotNetHostPath);
                Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(loader.DotNetHostPath) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            }
            MSBuildLocator.RegisterMSBuildPath(loader.SdkPath);
            result = await WarmAcquisitionDiagnostic.RunAsync(request);
        }
        catch (Exception exception)
        {
            result = new Result("setup-failed", [], [exception.ToString()]);
        }
        await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(result, ProjectContextLoader.JsonOptions));
        // Exit zero means a diagnostic response was produced, NOT acquisition acceptance.
        return 0;
    }
}
