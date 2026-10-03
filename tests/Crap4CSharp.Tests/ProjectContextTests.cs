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
        Assert.Contains(result.Contexts, context => context.AssemblyName == "Library" && context.TargetFramework == "netstandard2.0");
        var production = Assert.Single(result.Contexts, context => context.AssemblyName == "Production");
        Assert.Contains(production.Sources, source => source.LogicalPath == "Linked/Linked.cs" && source.PhysicalPath.EndsWith("Shared/Linked.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(production.Sources, source => source.PhysicalPath.EndsWith("Excluded.cs", StringComparison.Ordinal));
        Assert.Contains(production.PreprocessorSymbols, symbol => symbol == "FROM_DIRECTORY_BUILD_PROPS");
        Assert.DoesNotContain(result.Contexts, context => context.AssemblyName == "ContextTests");
        Assert.Contains(result.ExcludedProjects ?? [], project => project.ProjectPath.EndsWith("Tests/ContextTests.csproj", StringComparison.Ordinal));
        foreach (var (path, before) in protectedFiles)
        {
            Assert.Equal(before.Item1, File.ReadAllBytes(path));
            Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
        }

        var filtered = await ProjectContextLoader.LoadAsync(request with { Frameworks = ["missing-tfm"] }, CancellationToken.None);
        Assert.False(filtered.Success);
        Assert.Equal("context.frameworkNotFound", filtered.FailureReason);

        var generated = await ProjectContextLoader.LoadAsync(request with { IncludeGenerated = true }, CancellationToken.None);
        Assert.True(generated.Success, string.Join(Environment.NewLine, generated.Diagnostics));
        var generatedProduction = Assert.Single(generated.Contexts, context => context.AssemblyName == "Production");
        Assert.Equal(generatedProduction.Sources.Count, generatedProduction.Sources.Select(source => source.LogicalPath).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(generatedProduction.Sources, source => source.LogicalPath == "Linked/Linked.cs");

        var build = await ProjectBuildPreparation.BuildAsync(selected, "Debug", null, null, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
        var warm = await ProjectContextLoader.LoadAsync(request, CancellationToken.None);
        Assert.True(warm.Success, string.Join(Environment.NewLine, warm.Diagnostics));
        Assert.Contains(warm.Contexts, context => context.AssemblyName == "Library" && context.TargetFramework == "netstandard2.0");
    }

    [Fact]
    public async Task UnrestoredProjectFailsWithStableReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "crap4csharp-unrestored", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Unrestored.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "Code.cs"), "class Code { int M() => 1; }", TestContext.Current.CancellationToken);
        try
        {
            var result = await ProjectContextLoader.LoadAsync(new ProjectContextLoadRequest(project, "Debug", null, [], false, false, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Equal("context.assetsUnavailable", result.FailureReason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ContextIdentityIsIndependentOfCloneRoot()
    {
        var fixture = Path.GetFullPath("Fixtures/ProjectContexts", AppContext.BaseDirectory);
        var temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-clones", Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(temporary, "one");
        var secondRoot = Path.Combine(temporary, "two");
        CopyAuthoredFixture(fixture, firstRoot);
        CopyAuthoredFixture(fixture, secondRoot);
        try
        {
            var firstTarget = Path.Combine(firstRoot, "ContextSolution.slnx");
            var secondTarget = Path.Combine(secondRoot, "ContextSolution.slnx");
            var firstRestore = await ProjectBuildPreparation.RestoreAsync(firstTarget, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var secondRestore = await ProjectBuildPreparation.RestoreAsync(secondTarget, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.True(firstRestore.ExitCode == 0, firstRestore.StandardOutput + firstRestore.StandardError);
            Assert.True(secondRestore.ExitCode == 0, secondRestore.StandardOutput + secondRestore.StandardError);
            var first = await ProjectContextLoader.LoadAsync(new(firstTarget, "Debug", null, [], false, false, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
            var second = await ProjectContextLoader.LoadAsync(new(secondTarget, "Debug", null, [], false, false, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
            Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
            Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics));
            Assert.Equal(first.Contexts.Select(context => (context.ProjectPath, context.TargetFramework, context.ContextId)),
                second.Contexts.Select(context => (context.ProjectPath, context.TargetFramework, context.ContextId)));
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Fact]
    public async Task OverallTimeoutHasStableReason()
    {
        var selected = Path.GetFullPath("Fixtures/ProjectContexts/ContextSolution.slnx", AppContext.BaseDirectory);
        var result = await ProjectContextLoader.LoadAsync(new(selected, "Debug", null, [], false, false, TimeSpan.FromMilliseconds(1)), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("context.loaderTimeout", result.FailureReason);
    }

    [Fact]
    public async Task PerProcessSdkTimeoutHasStableReasonWhenItWinsOverallTimerRace()
    {
        var selected = Path.GetFullPath("Fixtures/ProjectContexts/ContextSolution.slnx", AppContext.BaseDirectory);
        var timeout = TimeSpan.FromSeconds(30);

        var result = await ProjectContextLoader.LoadAsync(
            new(selected, "Debug", null, [], false, false, timeout),
            CancellationToken.None,
            (_, processTimeout, _) => Task.FromException<ProjectContextSdkResolution>(
                new ProcessTimeoutException("dotnet", processTimeout)));

        Assert.False(result.Success);
        Assert.Equal("context.loaderTimeout", result.FailureReason);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("30 seconds", StringComparison.Ordinal));
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

    private static void CopyAuthoredFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
