using System.Text.Json;
using Microsoft.Build.Locator;

namespace Crap4CSharp.Core;

internal static class MsBuildLoaderBootstrap
{
    public static async Task<int> RunAsync(string requestPath, string responsePath, CancellationToken cancellationToken)
    {
        ProjectContextLoadResult result;
        try
        {
            var request = JsonSerializer.Deserialize<ProjectContextLoadRequest>(
                await File.ReadAllTextAsync(requestPath, cancellationToken), ProjectContextLoader.JsonOptions)
                ?? throw new InvalidDataException("Missing loader request.");
            if (string.IsNullOrWhiteSpace(request.SdkPath) || !Directory.Exists(request.SdkPath))
                throw new ProjectContextException("context.sdkResolutionFailed", "The resolved SDK path is unavailable.");
            if (!string.IsNullOrWhiteSpace(request.DotNetRoot)) Environment.SetEnvironmentVariable("DOTNET_ROOT", request.DotNetRoot);
            if (!string.IsNullOrWhiteSpace(request.DotNetHostPath))
            {
                Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", request.DotNetHostPath);
                var hostDirectory = Path.GetDirectoryName(request.DotNetHostPath)!;
                Environment.SetEnvironmentVariable("PATH", hostDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
            }
            MSBuildLocator.RegisterMSBuildPath(request.SdkPath);
            result = await MsBuildProjectLoader.LoadAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            var reason = exception is ProjectContextException context ? context.Reason : "context.loaderFailed";
            result = new ProjectContextLoadResult(false, [], [exception.ToString()], reason);
        }
        await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(result, ProjectContextLoader.JsonOptions), cancellationToken);
        return result.Success ? 0 : 1;
    }
}
