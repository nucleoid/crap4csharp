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
    public async Task ExplicitCompileItemUnderNestedObjRemainsAuthored()
    {
        var root = Path.Combine(Path.GetTempPath(), "crap4csharp-authored-obj", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "Legacy", "obj");
        Directory.CreateDirectory(nested);
        var project = Path.Combine(root, "AuthoredObj.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
              <ItemGroup><Compile Include="Legacy/obj/Gate.g.cs" /></ItemGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nested, "Gate.g.cs"),
            "// <auto-generated/>\npublic static class Gate {}", TestContext.Current.CancellationToken);
        try
        {
            var restore = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            Assert.Equal(0, restore.ExitCode);
            var result = await ProjectContextLoader.LoadAsync(new(project, "Debug", null, [], true, true,
                TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var source = Assert.Single(Assert.Single(result.Contexts).Sources,
                item => item.LogicalPath == "Legacy/obj/Gate.g.cs");
            Assert.False(source.IsGenerated);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("obj/Gate.cs", "")]
    [InlineData("Legacy/Gate.cs", "obj/Gate.cs")]
    public async Task CommittedTargetAddedCompileCannotHideAuthoredCodeAsGenerated(string physical, string link)
    {
        using var fixture = TestDirectory.Create("crap4csharp-target-added-authored");
        var project = fixture.Write("App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
              <Target Name="AddGate" BeforeTargets="CoreCompile">
                <ItemGroup><Compile Include="{{physical}}" Link="{{link}}" /></ItemGroup>
              </Target>
            </Project>
            """);
        var sourcePath = fixture.Write(physical, "public class Gate { public int M(int n) { if(n>0) return 1; if(n<0) return 2; return 0; } }");
        async Task Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Git("init", "--quiet");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "user.name", "Fixture");
        await Git("add", "-f", "--", "App.csproj", physical);
        await Git("commit", "--quiet", "-m", "authored target input");
        var restore = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var loaded = await ProjectContextLoader.LoadAsync(new(project, "Debug", null, [], false, false, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Diagnostics));
        var context = Assert.Single(loaded.Contexts);
        var source = Assert.Single(context.Sources, item => item.ResolvedPath == sourcePath);
        Assert.False(source.IsGenerated);
        Assert.DoesNotContain(context.Exclusions, item => item.LogicalPath == source.LogicalPath);
    }

    [Fact]
    public async Task ActualIntermediateOutputLayoutIsGeneratedWithoutAnObjLogicalPrefix()
    {
        using var fixture = TestDirectory.Create("crap4csharp-artifacts-generated");
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><BaseIntermediateOutputPath>artifacts/intermediate/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        var project = fixture.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fixture.Write("Gate.cs", "public class Gate { public int M() => 1; }");
        var restore = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var loaded = await ProjectContextLoader.LoadAsync(new(project, "Debug", null, [], false, true, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Diagnostics));
        var context = Assert.Single(loaded.Contexts);
        Assert.Contains(context.Sources, source => source.IsGenerated && source.ResolvedPath is not null &&
            source.ResolvedPath.Contains("artifacts" + Path.DirectorySeparatorChar + "intermediate", StringComparison.Ordinal));
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
    public async Task SdkResolutionFailurePreservesStructuredReason()
    {
        var selected = Path.GetFullPath("Fixtures/ProjectContexts/ContextSolution.slnx", AppContext.BaseDirectory);

        var result = await ProjectContextLoader.LoadAsync(
            new(selected, "Debug", null, [], false, false, TimeSpan.FromSeconds(30)),
            CancellationToken.None,
            (_, _, _) => Task.FromException<ProjectContextSdkResolution>(
                new ProjectContextException("context.sdkResolutionFailed", "SDK probe failed.")));

        Assert.False(result.Success);
        Assert.Equal("context.sdkResolutionFailed", result.FailureReason);
        Assert.Contains("SDK probe failed.", result.Diagnostics);
    }

    [Fact]
    public async Task CallerCancellationStillEscapesStructuredFailureBoundary()
    {
        var selected = Path.GetFullPath("Fixtures/ProjectContexts/ContextSolution.slnx", AppContext.BaseDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectContextLoader.LoadAsync(
            new(selected, "Debug", null, [], false, false, TimeSpan.FromSeconds(30)),
            cancellation.Token,
            (_, _, _) => Task.FromCanceled<ProjectContextSdkResolution>(cancellation.Token)));
    }

    [Fact]
    public async Task MissingTargetReturnsStructuredReason()
    {
        var missing = Path.Combine(Path.GetTempPath(), "crap4csharp-missing", Guid.NewGuid().ToString("N"), "Missing.csproj");

        var result = await ProjectContextLoader.LoadAsync(
            new(missing, "Debug", null, [], false, false, TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("context.targetNotFound", result.FailureReason);
    }

    [Fact]
    public async Task AuthoredInputMutationIsDetectedWhenAnAncestorIsNamedBin()
    {
        var root = Path.Combine(Path.GetTempPath(), "crap4csharp-mutation", "bin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "Mutation.csproj");
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(settings, "{\"value\":1}", TestContext.Current.CancellationToken);
        try
        {
            var result = await ProjectContextLoader.LoadAsync(
                new(project, "Debug", null, [], false, false, TimeSpan.FromSeconds(30)),
                CancellationToken.None,
                (_, _, _) =>
                {
                    File.WriteAllText(settings, "{\"value\":2}");
                    return Task.FromException<ProjectContextSdkResolution>(
                        new ProjectContextException("context.sdkResolutionFailed", "SDK probe failed."));
                });

            Assert.False(result.Success);
            Assert.Equal("context.inputsMutated", result.FailureReason);
        }
        finally { Directory.Delete(Path.Combine(Path.GetTempPath(), "crap4csharp-mutation"), true); }
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
