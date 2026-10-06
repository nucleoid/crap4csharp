using System.Text.Json;
using Microsoft.Build.Evaluation;

namespace Crap4CSharp.Core;

internal static partial class MsBuildProjectLoader
{
    // Evaluation only, under the complete graph globals. No target or Workspace invocation.
    // This is a conservative installed-fixture support gate, not custom-target attestation.
    private static ProjectTestRunnerMetadata EvaluateTestRunner(ProjectBuildGraphNode node, string sdkPath)
    {
        using var collection = new ProjectCollection(new Dictionary<string, string>(node.GlobalProperties, StringComparer.OrdinalIgnoreCase));
        var project = collection.LoadProject(node.ProjectPath);
        if (string.Equals(project.GetPropertyValue("TestingPlatformDotnetTestSupport"), "true", StringComparison.OrdinalIgnoreCase))
            throw Unsupported("Microsoft.Testing.Platform package dispatch is not supported; configure a reviewed VSTest project.");
        for (var directory = Path.GetDirectoryName(node.ProjectPath); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var path = Path.Combine(directory, "global.json");
            if (!File.Exists(path)) continue;
            using var json = ReadMetadataJson(path);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProjectContextException("test.runnerMetadataInvalid", "global.json must be an object.");
            if (json.RootElement.TryGetProperty("test", out var test) && (test.ValueKind != JsonValueKind.Object ||
                (test.TryGetProperty("runner", out var runner) &&
                (runner.ValueKind != JsonValueKind.String || runner.GetString() != "VSTest"))))
                throw Unsupported("The applicable global.json selects an unsupported dotnet test runner.");
            break;
        }
        var instance = project.CreateProjectInstance();
        var standardTargets = Path.GetFullPath(Path.Combine(sdkPath, "Microsoft.TestPlatform.targets"));
        foreach (var name in new[] { "VSTest", "_VSTestConsole", "_VSTestMSBuild" })
            if (!instance.Targets.TryGetValue(name, out var target) || !PathComparer.Equals(Path.GetFullPath(target.Location.File), standardTargets))
                throw Unsupported("The effective VSTest dispatch is absent or overridden by unsupported targets.");
        // These evaluated overrides redirect the standard task even without replacing VSTest.
        var console = project.GetPropertyValue("VSTestConsolePath");
        if (!PathComparer.Equals(Path.GetFullPath(console, Path.GetDirectoryName(node.ProjectPath)!),
                Path.GetFullPath(Path.Combine(sdkPath, "vstest.console.dll"))))
            throw Unsupported("The evaluated VSTest console is not the selected SDK console.");
        var taskAssembly = project.GetPropertyValue("VSTestTaskAssemblyFile");
        if (taskAssembly != "Microsoft.TestPlatform.Build.dll" &&
            !PathComparer.Equals(Path.GetFullPath(taskAssembly, Path.GetDirectoryName(node.ProjectPath)!),
                Path.GetFullPath(Path.Combine(sdkPath, "Microsoft.TestPlatform.Build.dll"))))
            throw Unsupported("A custom VSTest task assembly is unsupported.");
        var assetsPath = project.GetPropertyValue("ProjectAssetsFile");
        using var assets = ReadMetadataJson(Path.GetFullPath(assetsPath, Path.GetDirectoryName(node.ProjectPath)!));
        if (assets.RootElement.ValueKind != JsonValueKind.Object ||
            !assets.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            throw new ProjectContextException("test.runnerMetadataInvalid", "Restore assets lack the evaluated package libraries.");
        var sdkVersion = PackageVersion("Microsoft.NET.Test.Sdk");
        var collectorVersion = PackageVersion("coverlet.collector");
        if (sdkVersion != "18.0.1" || !InstalledImport("microsoft.net.test.sdk", sdkVersion, "Microsoft.NET.Test.Sdk.targets"))
            throw Unsupported("Install the supported Microsoft.NET.Test.Sdk 18.0.1 before checking; no packages are injected.");
        if (collectorVersion != "6.0.4" || !InstalledImport("coverlet.collector", collectorVersion, "coverlet.collector.targets"))
            throw new ProjectContextException("test.collectorUnsupported", "Install the supported coverlet.collector 6.0.4 before checking; no collector is injected.");
        var collectorImport = project.Imports.Select(i => i.ImportedProject.FullPath).Single(path =>
            Path.GetFileName(path) == "coverlet.collector.targets" &&
            path.Replace('\\', '/').Contains("/coverlet.collector/6.0.4/", StringComparison.OrdinalIgnoreCase));
        if (!instance.Targets.TryGetValue("SetXPlatDataCollectorPath", out var collectorTarget) ||
            !PathComparer.Equals(Path.GetFullPath(collectorTarget.Location.File), Path.GetFullPath(collectorImport)))
            throw new ProjectContextException("test.collectorUnsupported", "The effective XPlat collector hook is absent or overridden.");
        return new(node.Id, node.ProjectPath, node.Configuration, node.Platform, node.TargetFramework, "VSTest", sdkVersion, collectorVersion);

        string? PackageVersion(string name)
        {
            var matching = libraries.EnumerateObject().Where(p => p.Name.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("type", out var type) && type.GetString() == "package").ToArray();
            return matching.Length == 1 ? matching[0].Name[(name.Length + 1)..] : null;
        }
        bool InstalledImport(string package, string version, string filename) => project.Imports.Any(import =>
        {
            var path = import.ImportedProject.FullPath;
            return Path.GetFileName(path) == filename && File.Exists(path) && !TraversesReparsePoint(path) &&
                path.Replace('\\', '/').Contains("/" + package + "/" + version + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static ProjectContextException Unsupported(string message) => new("test.runnerUnsupported", message);

    private static JsonDocument ReadMetadataJson(string path)
    {
        if (!File.Exists(path) || TraversesReparsePoint(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new ProjectContextException("test.runnerMetadataInvalid", "Runner metadata is missing, unsafe or exceeds 16 MiB.");
        try
        {
            using var stream = File.OpenRead(path);
            using var retained = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (retained.Length + count > 16 * 1024 * 1024)
                    throw new ProjectContextException("test.runnerMetadataInvalid", "Runner metadata exceeds 16 MiB.");
                retained.Write(buffer, 0, count);
            }
            return JsonDocument.Parse(retained.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            throw new ProjectContextException("test.runnerMetadataInvalid", "Runner metadata is malformed: " + exception.Message);
        }
    }
}
