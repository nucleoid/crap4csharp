using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProjectContextTests
{
    [Fact]
    public async Task LoaderCapturesCompileLinksConditionsAndTargetFrameworksWithoutMutation()
    {
        var fixture = Path.GetFullPath("Fixtures/ProjectContexts", AppContext.BaseDirectory);
        var selected = Path.Combine(fixture, "ContextSolution.slnx");
        var protectedFiles = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(selected, "Debug", null, [], false, false, TimeSpan.FromMinutes(2));

        var restore = await ProjectBuildPreparation.RestoreAsync(selected, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, restore.ExitCode);

        var result = await ProjectContextLoader.LoadAsync(request, CancellationToken.None);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(result.Contexts, context => context.TargetFramework == "net10.0");
        Assert.Contains(result.Contexts, context => context.TargetFramework == "netstandard2.1");
        var production = Assert.Single(result.Contexts, context => context.AssemblyName == "Production");
        Assert.Contains(production.Sources, source => source.LogicalPath == "Linked/Linked.cs" && source.PhysicalPath.EndsWith("Shared/Linked.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(production.Sources, source => source.PhysicalPath.EndsWith("Excluded.cs", StringComparison.Ordinal));
        Assert.Contains(production.PreprocessorSymbols, symbol => symbol == "FROM_DIRECTORY_BUILD_PROPS");
        Assert.DoesNotContain(result.Contexts, context => context.AssemblyName == "ContextTests");
        foreach (var (path, before) in protectedFiles)
        {
            Assert.Equal(before.Item1, File.ReadAllBytes(path));
            Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void TargetSelectionNeverGuessesNestedProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "crap4csharp-select", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "nested", "Only.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        try { Assert.Throws<ProjectContextException>(() => ProjectTargetSelector.Select(root, null)); }
        finally { Directory.Delete(root, true); }
    }
}
