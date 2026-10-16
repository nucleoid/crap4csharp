using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerPhaseInputTests
{
    [Fact]
    public async Task RealWorkspaceRetainsRoleBearingOptionalAssetsImportsAndIndependentReferences()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-inputs");
        var project = fixture.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><AdditionalFiles Include="../Shared/Rules.txt" />
            <EditorConfigFiles Include="../Shared/Rules.editorconfig" /></ItemGroup>
            <Target Name="AddSource" BeforeTargets="CoreCompile"><ItemGroup>
            <Compile Include="../Shared/Added.cs" Link="obj/Added.cs" /></ItemGroup></Target></Project>
            """);
        var source = fixture.Write("App/Code.cs", "public class Code {}");
        var added = fixture.Write("Shared/Added.cs", "public class Added {}");
        var additional = fixture.Write("Shared/Rules.txt", "rules");
        var config = fixture.Write("Shared/Rules.editorconfig", "root = true\n[*]\nindent_style = space\n");
        var import = fixture.Write("Directory.Build.props", "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");
        var sdk = fixture.Write("global.json", "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestPatch\"}}");
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        var context = Assert.Single(loaded.GraphContexts).Context;
        Assert.Contains(context.PhaseInputs, p => p.Path == additional && p.Role == "additionalFile" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Path == config && p.Role == "analyzerConfig" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Path == source && p.Role == "source" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Path == added && p.Role == "source" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Path == project && p.Role == "project" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Path == import && p.Role == "import" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Path == sdk && p.Role == "sdkConfiguration" && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Role == "restoreAssets" && File.Exists(p.Path) && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Role == "analyzer" && p.Path.EndsWith(".dll", StringComparison.Ordinal) && !p.ExpectedGenerated);
        Assert.Contains(context.PhaseInputs, p => p.Role == "source" && p.ExpectedGenerated && p.Path.EndsWith("App.AssemblyInfo.cs", StringComparison.Ordinal));
        Assert.Contains(context.PhaseInputs, p => p.Role == "analyzerConfig" && p.ExpectedGenerated);
        Assert.NotEmpty(context.ReferencePaths);
        Assert.All(context.ReferencePaths, p => { Assert.True(File.Exists(p)); Assert.True(Path.IsPathFullyQualified(p)); });
        Assert.DoesNotContain(context.PhaseInputs, p => p.Role == "reference");
        var copy = JsonSerializer.Deserialize<ProjectAnalysisContext>(JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!;
        Assert.Equal(context.PhaseInputs, copy.PhaseInputs);
        Assert.Equal(context.ReferencePaths, copy.ReferencePaths);
        var publicJson = JsonSerializer.Serialize(context);
        Assert.DoesNotContain("PhaseInputs", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReferencePaths", publicJson, StringComparison.OrdinalIgnoreCase);
        foreach (var (path, saved) in original)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void PrivateInputsDoNotChangePublicIdentityOrCapabilities()
    {
        var context = ProjectAnalysisContext.Create("App.csproj", "App", "net10.0", "Release", "AnyCPU",
            Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp14, [], Microsoft.CodeAnalysis.SourceCodeKind.Regular,
            [], [], new(false, false), new("sdk", "msbuild", "roslyn"));
        var changed = context with { PhaseInputs = [new("private.txt", "additionalFile", false)], ReferencePaths = ["reference.dll"] };
        Assert.Equal(JsonSerializer.Serialize(context), JsonSerializer.Serialize(changed));
        Assert.Equal(context.ContextId, changed.ContextId);
        Assert.Equal(context.Capabilities, changed.Capabilities);
    }
}
