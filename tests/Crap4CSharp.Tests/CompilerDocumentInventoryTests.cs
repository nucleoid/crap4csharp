using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerDocumentInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalCompilerInventoryRetainsExcludedSdkDocumentsAndTargetAddedAuthoredLink(bool includeGenerated)
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-documents");
        var project = fixture.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="AddSource" BeforeTargets="CoreCompile"><ItemGroup>
            <Compile Include="../Shared/Added.cs" Link="obj/Added.cs" /></ItemGroup></Target></Project>
            """);
        fixture.Write("App/Code.cs", "public class Code {}");
        var added = fixture.Write("Shared/Added.cs", "public class Added {}");
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, includeGenerated, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        var context = Assert.Single(loaded.GraphContexts).Context;
        // Proposed private seam falls back to the current public source inventory on RED.
        var documents = Inventory(context);
        var sdkAssemblyInfo = Assert.Single(documents, d => d.ResolvedPath is not null && d.ResolvedPath.EndsWith("App.AssemblyInfo.cs", StringComparison.Ordinal));
        Assert.True(sdkAssemblyInfo.IsGenerated);
        Assert.Contains(documents, d => d.ResolvedPath is not null && d.ResolvedPath.EndsWith("AssemblyAttributes.cs", StringComparison.Ordinal));
        Assert.All(documents, d =>
        {
            Assert.NotNull(d.ResolvedPath);
            Assert.True(Path.IsPathFullyQualified(d.ResolvedPath));
            Assert.True(File.Exists(d.ResolvedPath));
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(d.ResolvedPath)), d.ContentIdentity);
        });
        var linked = Assert.Single(documents, d => d.ResolvedPath == added);
        Assert.False(linked.IsGenerated);
        Assert.Equal("obj/Added.cs", linked.LogicalPath);
        if (includeGenerated) Assert.Contains(sdkAssemblyInfo, context.Sources);
        else
        {
            Assert.DoesNotContain(sdkAssemblyInfo, context.Sources);
            Assert.Contains(context.Exclusions, e => e.ContentIdentity == sdkAssemblyInfo.ContentIdentity && e.Reason == "generated.defaultExcluded");
        }
        var transported = JsonSerializer.Deserialize<ProjectAnalysisContext>(JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!;
        Assert.Equal(documents, Inventory(transported));
        var publicJson = JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("compilerDocuments", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Path, publicJson, StringComparison.Ordinal);
        Assert.Equal(context.ContextId, transported.ContextId);
        var privateDirectory = Path.Combine(fixture.Path, "private");
        Directory.CreateDirectory(privateDirectory);
        var observed = await CompilerEvidence.BuildAsync(Assert.Single(loaded.BuildGraph), privateDirectory, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        var actualSources = Assert.Single(observed.Parameters, p => p.Name == "Sources").Items
            .Select(p => Path.GetFullPath(p, Path.GetDirectoryName(project)!)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(actualSources, documents.Select(d => d.ResolvedPath!).Order(StringComparer.Ordinal));
        foreach (var (path, saved) in original)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    private static IReadOnlyList<ProjectSourceIdentity> Inventory(ProjectAnalysisContext context) =>
        typeof(ProjectAnalysisContext).GetProperty("CompilerDocuments")?.GetValue(context) as IReadOnlyList<ProjectSourceIdentity> ?? context.Sources;
}
