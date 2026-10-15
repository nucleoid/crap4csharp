using Crap4CSharp.Core;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

// Root solution spelling is not the mapped project-platform identity.
// Resolve the root once for both explicit restore and every current proof path;
// the loader remains responsible for independently evaluated project mappings.
internal static class FreshProjectPlatform
{
    internal static async Task<string> ResolveAsync(string target, string configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Path.GetExtension(target).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(target);
            var model = await SolutionSerializers.SlnXml.OpenAsync(stream, token);
            token.ThrowIfCancellationRequested();
            return model.Platforms.FirstOrDefault(platform => platform is "Any CPU" or "AnyCPU")
                ?? model.Platforms.FirstOrDefault() ?? "Any CPU";
        }
        // Preserve the existing direct-project and classic-solution selector.
        // SDK/MSBuild assemblies are loaded only by the isolated loader worker.
        return "AnyCPU";
    }
}
