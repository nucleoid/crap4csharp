using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerEvidenceTests
{
    [Fact]
    public async Task WarmMappedBuildObservesActualTargetAddedSourcesWithoutReferenceRebuild()
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-evidence");
        var dep = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="CountBuild" BeforeTargets="CoreCompile"><WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency {}");
        var added = fixture.Write("Shared/Added.cs", "public class Added {}");
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <DefineConstants>$(DefineConstants);FLAVOR_$(Flavor)</DefineConstants></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj" AdditionalProperties="Configuration=Debug" /></ItemGroup>
            <Target Name="AddSource" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="../Shared/Added.cs" Link="obj/Added.cs" /></ItemGroup>
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        var code = fixture.Write("Root/Code.cs", "#if FLAVOR_LEFT\npublic class Root {}\n#else\n#error Mapped Flavor was lost\n#endif");
        var request = new ProjectContextLoadRequest(root, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var graph = await ProjectContextLoader.DiscoverGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(graph.Success, string.Join(" | ", graph.Diagnostics));
        var original = new[] { dep, root, added, code }.ToDictionary(p => p, p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p)));
        foreach (var pass in new[] { 1, 2 })
        foreach (var node in graph.BuildGraph)
        {
            var globals = new Dictionary<string, string>(node.GlobalProperties) { ["Flavor"] = "LEFT" };
            var directory = Path.Combine(fixture.Path, "private", pass.ToString(), node.Id);
            Directory.CreateDirectory(directory);
            var observed = await CompilerEvidence.BuildAsync(node with { GlobalProperties = globals }, directory,
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal(node.ProjectPath, observed.ProjectPath);
            var sources = Assert.Single(observed.Parameters, p => p.Name == "Sources");
            Assert.Contains(sources.Items, p => Path.GetFullPath(p, Path.GetDirectoryName(node.ProjectPath)!) == (node.ProjectPath == root ? added : Path.Combine(fixture.Path, "Dependency", "Code.cs")));
            Assert.Single(observed.Parameters, p => p.Name == "OutputAssembly");
            Assert.Single(observed.Parameters, p => p.Name == "References");
            Assert.NotNull(observed.Assembly);
            Assert.NotNull(observed.Pdb);
            var recipe = Assert.IsType<CompilerInvocationObservation>(observed.InvocationRecipe);
            Assert.NotEmpty(recipe.Arguments);
            Assert.Equal("selected-sdk-core-target", recipe.TaskBinding);
            var commandSupported = new[] { "selected-host-exec-csc-arguments-match", "selected-sdk-csc-exe-arguments-match" }.Contains(recipe.CommandBinding, StringComparer.Ordinal);
            Assert.True(commandSupported, commandSupported ? null : BindingDiagnostic(observed, TestContext.Current.CancellationToken));
            var imageRoles = new[] { "dotnet-host", "csc", "compiler-common", "compiler-csharp", "compiler-deps", "compiler-runtimeconfig", "sdk-csc-target", "sdk-csc-task" };
            Assert.Equal(OperatingSystem.IsWindows() ? imageRoles.Append("csc-launcher") : imageRoles,
                recipe.Images.Select(image => image.Role));
            Assert.All(recipe.Images, image => { Assert.True(image.Length > 0); Assert.Equal(64, image.Sha256.Length); });
            Assert.Equal(new[] { "recipe.loadedCompilerAndTaskImageNotAttested", "recipe.runtimeLoadedImageNotAttested" }, recipe.UnsupportedReasons);
            var projected = CompilerRecipePayload.ProjectArguments(observed, fixture.Path, TestContext.Current.CancellationToken);
            Assert.Equal(sources.Items.Count, projected.ArgumentRoles.Count(role => role.Role == "source"));
            Assert.Equal(observed.CommandLineArguments.Count, projected.ArgumentRoles.Count);
            Assert.Equal(CompilerRecipeObservation.Identities(observed.CommandLineArguments, TestContext.Current.CancellationToken), recipe.Arguments);
            // Artifact-byte capture and fresh/saved recipe equality require the
            // fully loaded contexts exercised by the public prior-build test.
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Assembly.Path)), observed.Assembly.Sha256);
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Pdb.Path)), observed.Pdb.Sha256);
            Assert.Contains(node.ProjectPath == dep ? "Debug" : "Release", observed.Assembly.Path, StringComparison.Ordinal);
            var mismatch = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(
                Path.Combine(directory, "compiler.binlog"), node.ProjectPath == root ? dep : root, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
            Assert.Equal("compiler.associationInvalid", mismatch.Reason);
            var binlog = Path.Combine(directory, "compiler.binlog");
            var fullBytes = File.ReadAllBytes(binlog);
            File.WriteAllBytes(binlog, fullBytes[..80]);
            var truncated = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(binlog,
                node.ProjectPath, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
            Assert.Equal("compiler.binlogInvalid", truncated.Reason);
            File.WriteAllBytes(binlog, fullBytes);
            Assert.Equal(pass, File.ReadAllLines(Path.Combine(Path.GetDirectoryName(node.ProjectPath)!, "count.txt")).Length);
        }
        foreach (var (path, saved) in original)
        {
            Assert.Equal(saved.Item1, File.ReadAllBytes(path));
            Assert.Equal(saved.Item2, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData("multiple-aliases")]
    [InlineData("custom-generated-output")]
    public async Task Actual_SDK_optional_reference_or_emit_profile_preserves_compiler_capture_as_fixed_unsupported_DATA(string profile)
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-optional-real");
        var dependency = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        var dependencyCode = fixture.Write("Dependency/Code.cs", "public class Dependency {}");
        var aliases = profile == "multiple-aliases" ? " Aliases=\"Foo,global\"" : "";
        var generated = profile == "custom-generated-output" ? """
            <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
            <CompilerGeneratedFilesOutputPath>$(MSBuildProjectDirectory)/../Generated</CompilerGeneratedFilesOutputPath>
            """ : "";
        var project = fixture.Write("Root/Root.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>{generated}</PropertyGroup>
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj"{aliases} /></ItemGroup></Project>
            """);
        var code = fixture.Write("Root/Code.cs", profile == "multiple-aliases"
            ? "extern alias Foo; public class Root { public Foo::Dependency ViaAlias() => new(); public Dependency ViaGlobal() => new(); }"
            : "public class Root { public Dependency Value() => new(); }");
        var original = new[] { dependency, dependencyCode, project, code }.ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Stamp: File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var graph = await ProjectContextLoader.DiscoverGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(graph.Success, string.Join(" | ", graph.Diagnostics));
        CompilerObservation? rootObservation = null;
        foreach (var node in graph.BuildGraph)
        {
            var directory = Path.Combine(fixture.Path, "private", node.Id); Directory.CreateDirectory(directory);
            var observed = await CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.NotNull(observed.Assembly); Assert.NotNull(observed.Pdb);
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Assembly.Path)), observed.Assembly.Sha256);
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Pdb.Path)), observed.Pdb.Sha256);
            Assert.Single(observed.Parameters, input => input.Name == "Sources");
            Assert.Single(observed.Parameters, input => input.Name == "References");
            if (node.ProjectPath == project) rootObservation = observed;
        }
        var actual = Assert.IsType<CompilerObservation>(rootObservation);
        Assert.Equal("selected-sdk-core-target", actual.InvocationRecipe!.TaskBinding);
        var commandSupported = new[] { "selected-host-exec-csc-arguments-match", "selected-sdk-csc-exe-arguments-match" }.Contains(actual.InvocationRecipe.CommandBinding, StringComparer.Ordinal);
        Assert.True(commandSupported, commandSupported ? null : BindingDiagnostic(actual, TestContext.Current.CancellationToken));
        Assert.Equal(CompilerRecipeObservation.Identities(actual.CommandLineArguments, TestContext.Current.CancellationToken), actual.InvocationRecipe.Arguments);
        if (profile == "multiple-aliases")
        {
            var reference = Assert.Single(CompilerReferenceSnapshot.ResolveSemanticReferences(actual, TestContext.Current.CancellationToken), input => input.Aliases == "Foo,global");
            Assert.Contains(actual.CommandLineArguments, argument => argument.StartsWith("/reference:Foo=", StringComparison.Ordinal));
            Assert.Contains(actual.CommandLineArguments, argument => argument.StartsWith("/reference:", StringComparison.Ordinal) &&
                !argument.Contains('=') && Path.GetFullPath(argument["/reference:".Length..].Trim('"'), Path.GetDirectoryName(project)!) == reference.Path);
        }
        else
        {
            var declared = Assert.Single(Assert.Single(actual.EmitParameters, input => input.Name == "GeneratedFilesOutputPath").Items);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(declared, Path.GetDirectoryName(project)!));
            Assert.Equal(Path.GetFullPath(Path.Combine(fixture.Path, "Generated")), full);
            var relative = Path.GetRelativePath(Path.GetDirectoryName(actual.Assembly!.Path)!, full);
            Assert.True(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative));
            Assert.Contains(actual.CommandLineArguments, argument => argument.StartsWith("/generatedfilesout:", StringComparison.Ordinal));
        }
        var projection = CompilerRecipePayload.Classify(actual, fixture.Path, TestContext.Current.CancellationToken);
        Assert.False(projection.IsSupported);
        Assert.Equal(new[] { "recipe.emitProfileUnsupported" }, projection.UnsupportedReasons);
        var unsupported = CompilerRecipePayload.Unsupported(actual, "actual-context-DATA", "actual-build-DATA", projection, TestContext.Current.CancellationToken);
        using var parsed = System.Text.Json.JsonDocument.Parse(unsupported.AsMemory());
        Assert.Equal("unsupported-input-profile", parsed.RootElement.GetProperty("disposition").GetString());
        foreach (var absent in new[] { "arguments", "images", "compilerCommon", "compilerCSharp", "scalarArguments", "observedClosureArtifacts" })
            Assert.False(parsed.RootElement.TryGetProperty(absent, out _));
        foreach (var (path, saved) in original)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path)); Assert.Equal(saved.Stamp, File.GetLastWriteTimeUtc(path));
        }
        // Genuine compiler-task capture plus fixed optional DATA only. This is
        // not a public quality, baseline, freshness, native-owner or recipe certificate.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualReferenceOutputParameterAndParentHashAreCaptured(bool captureHash)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-output-capture");
        var project = fixture.Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        fixture.Write("Code.cs", "public class App { public int Value() => 42; }");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var graph = await ProjectContextLoader.DiscoverGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(graph.Success, string.Join(" | ", graph.Diagnostics));
        var directory = Path.Combine(fixture.Path, "private");
        Directory.CreateDirectory(directory);
        var observation = await CompilerEvidence.BuildAsync(Assert.Single(graph.BuildGraph), directory,
            TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        if (!captureHash)
        {
            var parameter = Assert.Single(observation.Parameters, p => p.Name == "OutputRefAssembly");
            var path = Path.GetFullPath(Assert.Single(parameter.Items), fixture.Path);
            Assert.True(File.Exists(path));
            Assert.Contains("refint", path, StringComparison.Ordinal);
            return;
        }
        // Reflection keeps this missing-API regression executable before the implementation.
        var property = typeof(CompilerObservation).GetProperty("ReferenceAssembly",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(property);
        var captured = Assert.IsType<CompilerOutput>(property.GetValue(observation));
        Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(captured.Path)), captured.Sha256);
        Assert.Equal(new FileInfo(captured.Path).Length, captured.Length);
        Assert.NotEqual(observation.Assembly!.Sha256, captured.Sha256);
    }

    [Fact]
    public async Task OversizedBinlogRefusesBeforeWorkerAndPreCancellationDoesNotLaunch()
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-limit");
        var project = fixture.Write("App.csproj", "<Project />");
        var log = Path.Combine(fixture.Path, "large.binlog");
        using (var stream = File.Create(log)) stream.SetLength(CompilerEvidence.MaximumBinlogBytes + 1);
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(log, project,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("compiler.binlogLimit", error.Reason);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompilerEvidence.ReadAsync(log, project,
            TimeSpan.FromSeconds(30), cancelled.Token));
    }

    [Fact]
    public async Task TruncatedBinlogIsTypedRefusalNotUnhandledWorkerCrash()
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-truncated");
        var project = fixture.Write("App.csproj", "<Project />");
        var log = fixture.Write("truncated.binlog", "not a binary log");
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(log, project,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("compiler.binlogInvalid", error.Reason);
    }
    // Fixed same-task DATA from the existing compiler worker only; no replay,
    // raw path/argv export, supported binding or recipe authority is added.
    internal static string BindingDiagnostic(CompilerObservation observed, CancellationToken token,
        IReadOnlyList<string>? projectedReasons = null)
    {
        token.ThrowIfCancellationRequested();
        string[] allowed = ["recipe.bindingUnsupported", "recipe.compilerCommandPrefixUnproven",
            "recipe.argumentOutputMissing", "recipe.commandEventMissing", "recipe.commandArgumentsUnproven",
            "recipe.sizeUnsupported", "recipe.compilerImageUnavailable", "recipe.compilerImagesChanged",
            "recipe.scalarProfileUnsupported", "recipe.emitProfileUnsupported", "recipe.selectedSdkTaskFileUnproven"];
        var reasons = (observed.InvocationRecipe?.UnsupportedReasons ?? []).Concat(projectedReasons ?? [])
            .Where(reason => allowed.Contains(reason, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        reasons.Add(observed.InvocationRecipe?.CommandBinding == "unproven"
            ? "diagnostic.compilerBinding.unproven" : "diagnostic.compilerBinding.selected");
        reasons.Add(CompilerRecipeObservation.NormalizeCommandRouteDiagnostic(observed.CommandRouteDiagnostic)
            ?? "diagnostic.compilerRoute.unavailable");
        return string.Join(" ", reasons);
    }

}
