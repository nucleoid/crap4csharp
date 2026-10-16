using System.Text.Json;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class WarmDesignTimeProjectTests
{
    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("de-DE", "de-DE")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("de-DE", "en-US")]
    [InlineData("fr-FR", "de-DE")]
    [InlineData("en-US", "fr-FR")]
    public void OrdinaryWorkspaceRefusesLocalizedRemovedMetadataWithoutRejectingOtherWarnings(string producerCulture, string consumerCulture)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(consumerCulture);
            var resources = new System.Resources.ResourceManager("Microsoft.CodeAnalysis.MSBuild.WorkspaceMSBuildResources",
                typeof(Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace).Assembly);
            var message = string.Format(resources.GetString("Unresolved_metadata_reference_removed_from_project_0",
                System.Globalization.CultureInfo.GetCultureInfo(producerCulture))!, "missing.dll");
            var error = Assert.Throws<ProjectContextException>(() => MsBuildProjectLoader.ThrowOnWorkspaceFailure(
                [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, message)], "App.csproj"));
            Assert.Equal("context.metadataUnavailable", error.Reason);
            MsBuildProjectLoader.ThrowOnWorkspaceFailure(
                [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "benign warning")], "App.csproj");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void ProbeIsDefaultOffAndOnlyRoundTripsThroughPrivateWorkerTransport()
    {
        var request = new ProjectContextLoadRequest("Target.csproj", "Debug", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.False(request.WarmAcquisitionProbe);
        Assert.False(request.WarmAcquisitionSelectionProbe);
        Assert.False(request.CollectProjectObservation);
        Assert.DoesNotContain("collectProjectObservation", JsonSerializer.Serialize(request, ProjectContextLoader.JsonOptions));
        Assert.DoesNotContain("warmAcquisitionProbe", JsonSerializer.Serialize(request, ProjectContextLoader.JsonOptions));
        Assert.DoesNotContain("warmAcquisitionSelectionProbe", JsonSerializer.Serialize(request, ProjectContextLoader.JsonOptions));
        var selected = request with { WarmAcquisitionSelectionProbe = true };
        Assert.True(JsonSerializer.Deserialize<ProjectContextLoadRequest>(JsonSerializer.Serialize(selected, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!.WarmAcquisitionSelectionProbe);
        Assert.DoesNotContain("WarmAcquisitionSelectionProbe", JsonSerializer.Serialize(selected));
        var enabled = request with { WarmAcquisitionProbe = true, CollectProjectObservation = true };
        var transport = JsonSerializer.Serialize(enabled, ProjectContextLoader.JsonOptions);
        Assert.True(JsonSerializer.Deserialize<ProjectContextLoadRequest>(transport, ProjectContextLoader.JsonOptions)!.WarmAcquisitionProbe);
        Assert.True(JsonSerializer.Deserialize<ProjectContextLoadRequest>(transport, ProjectContextLoader.JsonOptions)!.CollectProjectObservation);
        Assert.DoesNotContain("WarmAcquisitionProbe", JsonSerializer.Serialize(enabled));
        Assert.DoesNotContain("CollectProjectObservation", JsonSerializer.Serialize(enabled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmWorkerRefusesNonGraphEntryBeforeDiscoveryOrTargets(bool selection)
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-nongraph");
        var target = fixture.Write("Probe.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="CountDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/probe-hooks.txt" Lines="observed" Overwrite="false" />
            </Target></Project>
            """);
        fixture.Write("Code.cs", "public class Code { }");
        var files = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(
            path => path, path => (Bytes: File.ReadAllBytes(path), Time: File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(target, "Debug", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2))
        { WarmAcquisitionProbe = !selection, WarmAcquisitionSelectionProbe = selection };
        var result = await ProjectContextLoader.LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("context.warmGraphRequired", result.FailureReason);
        Assert.Empty(result.Contexts);
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, expected) in files)
        {
            Assert.Equal(expected.Bytes, File.ReadAllBytes(path));
            Assert.Equal(expected.Time, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WarmWorkerPreservesTwoRequiredContextIdentityAndRunsEachDesignTimeHookOnce(bool shared)
        => VerifyProducerParityAsync(shared, rich: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WarmWorkerPreservesExecutedProducerOptionsAndAliasedReferences(bool shared)
        => VerifyProducerParityAsync(shared, rich: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PrivateSelectionChoosesWarmForSupportedTwoRequiredContextGraph(bool shared)
        => VerifyProducerParityAsync(shared, rich: true, selection: true);

    [Theory]
    [InlineData("AdditionalProperties", false)]
    [InlineData("SetConfiguration", false)]
    [InlineData("AdditionalProperties", true)]
    [InlineData("SetConfiguration", true)]
    public async Task WarmWorkerRefusesMappedGlobalsBeforeAnyDesignTimeHook(string mapping, bool selection)
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-mapped");
        var dependencyPath = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <DefineConstants Condition="'$(Flavor)' == 'left'">$(DefineConstants);LEFT_FLAVOR</DefineConstants>
            </PropertyGroup><Target Name="CountDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <WriteLinesToFile File="$(IntermediateOutputPath)probe-hooks.txt" Lines="observed" Overwrite="false" />
            </Target></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency { public int M() => 1; }");
        foreach (var name in new[] { "Left", "Right" })
        {
            fixture.Write($"{name}/{name}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject>
                <DefineConstants>$(DefineConstants);TEST_{{name}}</DefineConstants>
                </PropertyGroup><ItemGroup>
                <ProjectReference Include="../Dependency/Dependency.csproj" {{mapping}}="Configuration=Debug" AdditionalProperties="Flavor=left{{(mapping == "AdditionalProperties" ? ";Configuration=Debug" : "")}}" />
                </ItemGroup><Target Name="CountDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="$(IntermediateOutputPath)probe-hooks.txt" Lines="observed" Overwrite="false" />
                </Target></Project>
                """.Replace("AdditionalProperties=\"Configuration=Debug\" AdditionalProperties=", "AdditionalProperties="));
            fixture.Write($"{name}/Code.cs", $"public class {name} {{ public int M() => new Dependency().M(); }}");
        }
        var target = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Left/Left.csproj" /><ProjectReference Include="../Right/Right.csproj" /></ItemGroup>
            <Target Name="CountDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <WriteLinesToFile File="$(IntermediateOutputPath)probe-hooks.txt" Lines="observed" Overwrite="false" />
            </Target></Project>
            """);
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(
            path => path, path => (Bytes: File.ReadAllBytes(path), Time: File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(target, "Release", "Any CPU", [], false, false, TimeSpan.FromMinutes(3))
        { CollectProjectObservation = true };
        var token = TestContext.Current.CancellationToken;
        var restore = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var build = await ProjectBuildPreparation.BuildAsync(target, "Release", framework: null, platform: "Any CPU", TimeSpan.FromMinutes(3), token);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
        Assert.Empty(Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories));
        var refusedBeforeOrdinary = await ProjectContextLoader.LoadGraphAsync(request with { WarmAcquisitionProbe = true }, token);
        Assert.False(refusedBeforeOrdinary.Success);
        Assert.Equal("context.warmMappedGraphUnsupported", refusedBeforeOrdinary.FailureReason);
        Assert.Empty(refusedBeforeOrdinary.Contexts);
        Assert.Empty(Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories));
        var ordinary = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(ordinary.Success, ordinary.FailureReason + ": " + string.Join(" | ", ordinary.Diagnostics));
        var hooks = Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories);
        // Ordinary Workspace can also evaluate a referenced project under its own
        // configuration. Bind warm increments to the actual required graph nodes,
        // not the ordinary loader's ambient hook-file cardinality.
        var counts = hooks.ToDictionary(path => path, path => File.ReadAllLines(path).Length);
        var requiredHooks = ordinary.GraphContexts.Select(context => Path.Combine(
            Assert.IsType<string>(context.Context.EvaluatedIntermediateDirectory), "probe-hooks.txt")).ToHashSet();
        Assert.Equal(4, requiredHooks.Count);
        Assert.All(requiredHooks, path => Assert.Contains(path, counts.Keys));
        var warm = await ProjectContextLoader.LoadGraphAsync(request with
        { WarmAcquisitionProbe = !selection, WarmAcquisitionSelectionProbe = selection }, token);
        if (selection)
        {
            Assert.True(warm.Success, string.Join(" | ", warm.Diagnostics));
            Assert.Contains("Private warm selection: ordinary.", warm.Diagnostics);
            Assert.Equal(JsonSerializer.Serialize(ordinary.GraphContexts, ProjectContextLoader.JsonOptions),
                JsonSerializer.Serialize(warm.GraphContexts, ProjectContextLoader.JsonOptions));
            Assert.Equal(JsonSerializer.Serialize(ordinary.ProjectObservations, ProjectContextLoader.JsonOptions),
                JsonSerializer.Serialize(warm.ProjectObservations, ProjectContextLoader.JsonOptions));
        }
        else
        {
            Assert.False(warm.Success);
            Assert.Equal("context.warmMappedGraphUnsupported", warm.FailureReason);
            Assert.Empty(warm.Contexts);
            Assert.Empty(warm.ProjectObservations);
        }
        foreach (var result in selection ? new[] { ordinary, warm } : new[] { ordinary })
        {
            Assert.Equal(4, result.GraphContexts.Count);
            Assert.Equal(2, result.GraphContexts.Count(context => context.IsTestProject));
            var dependency = Assert.Single(result.GraphContexts, context => context.ProjectPath == dependencyPath);
            Assert.Equal("Debug", dependency.Context.Configuration);
            Assert.Contains("DEBUG", dependency.Context.PreprocessorSymbols);
            Assert.Contains("LEFT_FLAVOR", dependency.Context.PreprocessorSymbols);
            Assert.True(File.Exists(dependency.Context.EvaluatedReferenceAssemblyPath));
            var root = Assert.Single(result.GraphContexts, context => context.ProjectPath == target);
            var unresolved = Assert.Single(root.Context.UnresolvedProjectReferencePaths);
            Assert.Contains(unresolved, root.Context.ReferencePaths);
            Assert.NotEqual(dependency.Context.EvaluatedReferenceAssemblyPath, unresolved);
            Assert.False(root.Context.Capabilities.DesignTimeComplete);
            Assert.False(root.Context.Capabilities.CompiledInputBindingComplete);
            Assert.False(root.Context.Capabilities.ReuseRecipeComplete);
            var launches = 0;
            var refusal = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerGraphExecution.RunPhasedAsync(
                request, result, (_, _) =>
                {
                    launches++;
                    return Task.FromException<CompilerObservation>(new InvalidOperationException("Unresolved transitive reference launched a compiler."));
                }, token));
            Assert.Equal("compiler.graphInvalid", refusal.Reason);
            Assert.Equal(0, launches);
            foreach (var name in new[] { "Left", "Right" })
            {
                var test = Assert.Single(result.GraphContexts, context => context.ProjectPath == Path.Combine(fixture.Path, name, name + ".csproj"));
                Assert.Equal("Release", test.Context.Configuration);
                Assert.DoesNotContain("DEBUG", test.Context.PreprocessorSymbols);
                Assert.Contains("TEST_" + name, test.Context.PreprocessorSymbols);
                Assert.NotEqual(dependency.Context.ContextId, test.Context.ContextId);
            }
        }
        Assert.Equal(hooks.Order(StringComparer.Ordinal),
            Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, count) in counts)
            // Ordinary selection must reproduce exactly the ordinary side effects,
            // with no preceding warm target pass on any node.
            Assert.Equal(selection ? count * 2 : count, File.ReadAllLines(path).Length);
        foreach (var (path, value) in authored)
        {
            Assert.Equal(value.Bytes, File.ReadAllBytes(path));
            Assert.Equal(value.Time, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(false, "emit")]
    [InlineData(true, "emit")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "error")]
    [InlineData(true, "error")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [InlineData(false, "constructor")]
    [InlineData(true, "constructor")]
    [InlineData(false, "unresolved")]
    [InlineData(true, "unresolved")]
    public async Task WarmWorkerRetainsActualGeneratorParityOrRefusalWithoutFallback(bool includeGenerated, string behavior)
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-generators");
        var token = TestContext.Current.CancellationToken;
        var action = behavior switch
        {
            "emit" => "context.AddSource(\"Hidden.cs\", \"public class Hidden { public int Value() => 7; }\");",
            "error" => "context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor(\"FIX001\", \"Failure\", \"generator error\", \"Fixture\", DiagnosticSeverity.Error, true), Location.None));",
            "throw" => "throw new System.InvalidOperationException(\"generator execute failed\");",
            _ => ""
        };
        if (behavior != "unresolved")
        {
            var source = "using Microsoft.CodeAnalysis; [Generator] public sealed class FixtureGenerator : ISourceGenerator { public void Initialize(GeneratorInitializationContext context) {} public void Execute(GeneratorExecutionContext context) { " + action + " } }";
            if (behavior == "constructor") source = source.Replace("public void Initialize",
                "public FixtureGenerator() => throw new System.InvalidOperationException(\"generator constructor failed\"); public void Initialize", StringComparison.Ordinal);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(ISourceGenerator).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("FixtureGenerator", [CSharpSyntaxTree.ParseText(source, cancellationToken: token)],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = File.Create(Path.Combine(fixture.Path, "FixtureGenerator.dll"));
            var emitted = compilation.Emit(stream, cancellationToken: token);
            Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics));
        }
        foreach (var name in new[] { "Left", "Right" })
        {
            fixture.Write($"{name}/{name}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                <ItemGroup><Analyzer Include="../FixtureGenerator.dll" /></ItemGroup>
                <Target Name="CountDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="$(IntermediateOutputPath)probe-hooks.txt" Lines="observed" Overwrite="false" />
                </Target></Project>
                """);
            fixture.Write($"{name}/Code.cs", $"public class {name} {{ public int Value() => 1; }}");
        }
        var target = fixture.Write("Probe.slnx", "<Solution><Project Path=\"Left/Left.csproj\" /><Project Path=\"Right/Right.csproj\" /></Solution>");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(
            path => path, path => (Bytes: File.ReadAllBytes(path), Time: File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(target, "Debug", "Any CPU", [], false, includeGenerated, TimeSpan.FromMinutes(3))
        { CollectProjectObservation = true };
        var restore = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var ordinary = await ProjectContextLoader.LoadGraphAsync(request, token);
        var hooks = Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories);
        Assert.NotEmpty(hooks);
        var before = hooks.ToDictionary(path => path, path => File.ReadAllLines(path).Length);
        var warm = await ProjectContextLoader.LoadGraphAsync(request with { WarmAcquisitionProbe = true }, token);
        Assert.Equal(ordinary.Success, warm.Success);
        if (behavior is "emit" or "empty")
        {
            Assert.True(ordinary.Success, ordinary.FailureReason + ": " + string.Join(" | ", ordinary.Diagnostics));
            Assert.True(warm.Success, warm.FailureReason + ": " + string.Join(" | ", warm.Diagnostics));
            Assert.Equal(2, ordinary.GraphContexts.Count);
            Assert.Equal(2, warm.GraphContexts.Count);
            Assert.All(warm.GraphContexts, entry => Assert.True(entry.IsTestProject));
            foreach (var original in ordinary.GraphContexts)
            {
                var actual = Assert.Single(warm.GraphContexts, entry => entry.GraphId == original.GraphId);
                Assert.Equal(JsonSerializer.Serialize(original.Context, ProjectContextLoader.JsonOptions),
                    JsonSerializer.Serialize(actual.Context, ProjectContextLoader.JsonOptions));
                Assert.Equal(behavior == "emit" ? 1 : 0, actual.Context.GeneratorOnlyTreeCount);
                Assert.Equal(behavior == "emit" && includeGenerated,
                    actual.Context.Sources.Any(source => source.IsGenerated && source.LogicalPath.EndsWith("Hidden.cs", StringComparison.Ordinal)));
            }
            Assert.Equal(2, ordinary.ProjectObservations.Count);
            Assert.Equal(2, warm.ProjectObservations.Count);
            foreach (var original in ordinary.ProjectObservations)
                Assert.Equal(original.Observation, Assert.Single(warm.ProjectObservations, entry => entry.ProjectPath == original.ProjectPath).Observation);
            Assert.Equal(2, hooks.Length);
        }
        else
        {
            Assert.False(ordinary.Success);
            Assert.False(warm.Success);
            Assert.Equal("context.generatedSourceUnavailable", ordinary.FailureReason);
            Assert.Equal(ordinary.FailureReason, warm.FailureReason);
            Assert.Empty(warm.Contexts);
        }
        // A post-target ordinary fallback would add a second hook to the same node.
        Assert.Equal(hooks.Order(StringComparer.Ordinal), Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, count) in before) Assert.Equal(count + 1, File.ReadAllLines(path).Length);
        foreach (var (path, saved) in authored)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Time, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmWorkerRefusesDeletedExecutedMetadataWithoutFallback(bool selection)
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-missing-metadata");
        var token = TestContext.Current.CancellationToken;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("FixtureReference",
            [CSharpSyntaxTree.ParseText("public class FixtureReference { }", cancellationToken: token)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var emittedBytes = new MemoryStream();
        var emitted = compilation.Emit(emittedBytes, cancellationToken: token);
        Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics));
        foreach (var name in new[] { "Left", "Right" })
        {
            fixture.Write($"{name}/{name}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                <ItemGroup><Reference Include="FixtureReference"><HintPath>FixtureReference.dll</HintPath></Reference></ItemGroup>
                <Target Name="RemoveExecutedMetadata" AfterTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="$(IntermediateOutputPath)executed-arguments.txt" Lines="@(CscCommandLineArgs)" Overwrite="true" />
                <WriteLinesToFile File="$(IntermediateOutputPath)probe-hooks.txt" Lines="observed" Overwrite="false" />
                <Delete Files="$(MSBuildProjectDirectory)/FixtureReference.dll" />
                </Target></Project>
                """);
            fixture.Write($"{name}/Code.cs", $"public class {name} {{ }}");
            File.WriteAllBytes(Path.Combine(fixture.Path, name, "FixtureReference.dll"), emittedBytes.ToArray());
        }
        var target = fixture.Write("Probe.slnx", "<Solution><Project Path=\"Left/Left.csproj\" /><Project Path=\"Right/Right.csproj\" /></Solution>");
        var owned = new[] { "Left", "Right" }.Select(name => Path.Combine(fixture.Path, name, "FixtureReference.dll")).ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Time: File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(target, "Debug", "Any CPU", [], false, false, TimeSpan.FromMinutes(3))
        { CollectProjectObservation = true };
        var restore = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var ordinary = await ProjectContextLoader.LoadGraphAsync(request, token);
        var hooks = Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories);
        Assert.NotEmpty(hooks);
        var counts = hooks.ToDictionary(path => path, path => File.ReadAllLines(path).Length);
        foreach (var (path, saved) in owned)
        {
            File.WriteAllBytes(path, saved.Bytes);
            File.SetLastWriteTimeUtc(path, saved.Time);
        }
        var warm = await ProjectContextLoader.LoadGraphAsync(request with { WarmAcquisitionProbe = !selection, WarmAcquisitionSelectionProbe = selection }, token);
        var observation = "ordinary=" + ordinary.Success + ":" + ordinary.FailureReason + " " + string.Join(" | ", ordinary.Diagnostics)
            + " warm=" + warm.Success + ":" + warm.FailureReason + " " + string.Join(" | ", warm.Diagnostics);
        Console.WriteLine("MISSING METADATA OBSERVATION: " + observation);
        var executedArguments = Directory.GetFiles(fixture.Path, "executed-arguments.txt", SearchOption.AllDirectories);
        Assert.NotEmpty(executedArguments);
        foreach (var path in executedArguments)
            Assert.Contains("FixtureReference.dll", File.ReadAllText(path));
        Assert.Contains(owned.Keys, path => !File.Exists(path));
        Assert.Equal(hooks.Order(StringComparer.Ordinal), Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        var increments = counts.Select(pair => File.ReadAllLines(pair.Key).Length - pair.Value).ToArray();
        Assert.All(increments, increment => Assert.InRange(increment, 0, 1));
        Assert.Equal(1, increments.Sum());
        // Both acquisitions must refuse rather than publish a reduced closure.
        Assert.False(ordinary.Success, observation);
        Assert.Equal("context.metadataUnavailable", ordinary.FailureReason);
        Assert.False(warm.Success, observation);
        Assert.Equal("context.warmMetadataUnavailable", warm.FailureReason);
        Assert.Empty(warm.Contexts);
    }

    private static async Task VerifyProducerParityAsync(bool shared, bool rich, bool selection = false)
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-probe");
        fixture.Write("Shared/Linked.cs", "public class Linked { public int Value() => 1; }");
        if (rich)
        {
            const string interop = """
                using System.Runtime.InteropServices;
                [assembly: Guid("72b24b69-1f5b-431e-88a3-75af84631234")]
                [assembly: ImportedFromTypeLib("FixtureInterop")]
                [ComImport, Guid("e84eeea4-7a8a-4986-a076-dba932a51234"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
                public interface IFixtureInterop { void Invoke(); }
                """;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("FixtureInterop", [CSharpSyntaxTree.ParseText(interop)],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = File.Create(Path.Combine(fixture.Path, "FixtureInterop.dll"));
            var emitted = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics));
        }
        if (shared)
        {
            fixture.Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Write("Library/Code.cs", "public class Library { public int M() => 1; }");
        }
        foreach (var name in new[] { "Left", "Right" })
        {
            if (rich)
            {
                fixture.Write($"{name}/extra.txt", "producer input");
                fixture.Write($"{name}/.editorconfig", "root = true\n[*.cs]\nindent_style = space\n");
                fixture.Write($"{name}/Unicode.cs", "// café Ελληνικά\npublic class Unicode" + name + " { public string Value => \"雪\"; }");
            }
            fixture.Write($"{name}/{name}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject>
                <DefineConstants>$(DefineConstants);PROBE_{{name}}</DefineConstants>
                {{(rich ? "<Nullable>enable</Nullable><LangVersion>preview</LangVersion><CodePage>65001</CodePage><ChecksumAlgorithm>SHA256</ChecksumAlgorithm><AllowUnsafeBlocks>true</AllowUnsafeBlocks><WarningLevel>5</WarningLevel><NoWarn>CS1591</NoWarn><Features>strict</Features>" : "")}}
                </PropertyGroup><ItemGroup>
                <Compile Include="../Shared/Linked.cs" Link="Links/Linked.cs" />
                {{(shared ? (rich ? "<ProjectReference Include=\"../Library/Library.csproj\" Aliases=\"global,ProbeLibrary\" />" : "<ProjectReference Include=\"../Library/Library.csproj\" />") : "")}}
                {{(rich ? "<AdditionalFiles Include=\"extra.txt\" /><Reference Include=\"FixtureInterop\"><HintPath>../FixtureInterop.dll</HintPath><EmbedInteropTypes>true</EmbedInteropTypes></Reference>" : "")}}
                </ItemGroup>
                <Target Name="CountDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="$(IntermediateOutputPath)probe-hooks.txt" Lines="observed" Overwrite="false" />
                {{(rich ? "<WriteLinesToFile File=\"$(IntermediateOutputPath)Producer.g.cs\" Lines=\"public class ProducerGenerated { public int Value() =&gt; 7%3B }\" Overwrite=\"true\" /><ItemGroup><Compile Include=\"$(IntermediateOutputPath)Producer.g.cs\" Link=\"Generated/Producer.g.cs\" /></ItemGroup>" : "")}}
                </Target></Project>
                """);
            fixture.Write($"{name}/Code.cs", $"public class {name} {{ public int M() => 1; }}");
        }
        var target = fixture.Write("Probe.slnx", "<Solution><Project Path=\"Left/Left.csproj\" /><Project Path=\"Right/Right.csproj\" /></Solution>");
        var request = new ProjectContextLoadRequest(target, "Debug", "Any CPU", [], false, false, TimeSpan.FromMinutes(3))
        { CollectProjectObservation = true };
        var restore = await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(0, restore.ExitCode);
        var build = await ProjectBuildPreparation.BuildAsync(target, "Debug", framework: null, platform: "Any CPU", TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
        var ordinary = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(ordinary.Success, string.Join(" | ", ordinary.Diagnostics));
        Assert.Equal(2, ordinary.GraphContexts.Count(context => context.IsTestProject));
        var hooks = Directory.GetFiles(fixture.Path, "probe-hooks.txt", SearchOption.AllDirectories);
        Assert.Equal(2, hooks.Length);
        var counts = hooks.ToDictionary(path => path, path => File.ReadAllLines(path).Length);
        var warm = await ProjectContextLoader.LoadGraphAsync(request with
        { WarmAcquisitionProbe = !selection, WarmAcquisitionSelectionProbe = selection }, TestContext.Current.CancellationToken);
        if (selection) Assert.Contains("Private warm selection: warm.", warm.Diagnostics);
        Assert.True(warm.Success, warm.FailureReason + ": " + string.Join(" | ", warm.Diagnostics) +
            " Observed ordinary graph: " + JsonSerializer.Serialize(ordinary.BuildGraph, ProjectContextLoader.JsonOptions));
        Assert.Equal(ordinary.BuildGraph.Select(node => node.Id), warm.BuildGraph.Select(node => node.Id));
        Assert.Equal(ordinary.GraphContexts.Count, warm.GraphContexts.Count);
        foreach (var original in ordinary.GraphContexts)
        {
            var current = Assert.Single(warm.GraphContexts, context => context.GraphId == original.GraphId);
            Assert.Equal(JsonSerializer.Serialize(original.Context, ProjectContextLoader.JsonOptions),
                JsonSerializer.Serialize(current.Context, ProjectContextLoader.JsonOptions));
        }
        Assert.Equal(ordinary.GraphContexts.Count, ordinary.ProjectObservations.Count);
        Assert.Equal(ordinary.ProjectObservations.Count, warm.ProjectObservations.Count);
        foreach (var observed in ordinary.ProjectObservations)
        {
            var actual = Assert.Single(warm.ProjectObservations, entry => entry.ProjectPath == observed.ProjectPath);
            Assert.True(observed.Observation == actual.Observation,
                "EXPECTED PROJECT: " + observed.Observation + "\nACTUAL PROJECT: " + actual.Observation);
        }
        foreach (var (path, count) in counts) Assert.Equal(count + 1, File.ReadAllLines(path).Length);
        // Equality alone could accept two identically impoverished observations.
        // Require the producer's actual non-default values in BOTH acquisitions.
        if (rich)
        foreach (var result in new[] { ordinary, warm })
        foreach (var name in new[] { "Left", "Right" })
        {
            var observation = Assert.Single(result.ProjectObservations,
                entry => entry.ProjectPath == Path.Combine(fixture.Path, name, name + ".csproj"));
            using var json = JsonDocument.Parse(observation.Observation);
            var root = json.RootElement;
            var parse = root.GetProperty("Parse");
            Assert.Equal((int)LanguageVersion.Preview, parse.GetProperty("SpecifiedLanguageVersion").GetInt32());
            Assert.Contains(parse.GetProperty("Features").EnumerateArray(), feature =>
                feature.GetProperty("Key").GetString() == "strict" && feature.GetProperty("Value").GetString() == "true");
            var embedded = Assert.Single(root.GetProperty("MetadataReferences").EnumerateArray(),
                reference => reference.GetProperty("FilePath").GetString() == Path.Combine(fixture.Path, "FixtureInterop.dll"));
            Assert.True(File.Exists(embedded.GetProperty("FilePath").GetString()));
            Assert.True(embedded.GetProperty("EmbedInteropTypes").GetBoolean());
            Assert.Contains(parse.GetProperty("Symbols").EnumerateArray(), symbol => symbol.GetString() == "PROBE_" + name);
            var options = root.GetProperty("Compilation");
            Assert.Equal((int)NullableContextOptions.Enable, options.GetProperty("NullableContextOptions").GetInt32());
            Assert.True(options.GetProperty("AllowUnsafe").GetBoolean());
            Assert.Equal(5, options.GetProperty("WarningLevel").GetInt32());
            Assert.Contains(options.GetProperty("SpecificDiagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("Key").GetString() == "CS1591" &&
                    diagnostic.GetProperty("Value").GetInt32() == (int)ReportDiagnostic.Suppress);
            var documents = root.GetProperty("Documents").EnumerateArray().ToArray();
            var linked = Assert.Single(documents, document => document.GetProperty("FilePath").GetString() == Path.Combine(fixture.Path, "Shared", "Linked.cs"));
            Assert.Equal(new[] { "Links" }, linked.GetProperty("Folders").EnumerateArray().Select(folder => folder.GetString()));
            var generated = Assert.Single(documents, document => document.GetProperty("Name").GetString() == "Producer.g.cs");
            Assert.Equal(new[] { "Generated" }, generated.GetProperty("Folders").EnumerateArray().Select(folder => folder.GetString()));
            var unicode = Assert.Single(documents, document => document.GetProperty("Name").GetString() == "Unicode.cs");
            Assert.Equal(65001, unicode.GetProperty("CodePage").GetInt32());
            Assert.Equal((int)SourceHashAlgorithm.Sha256, unicode.GetProperty("ChecksumAlgorithm").GetInt32());
            Assert.Single(documents, document => document.GetProperty("role").GetString() == "additional" && document.GetProperty("Name").GetString() == "extra.txt");
            Assert.Single(documents, document => document.GetProperty("role").GetString() == "config" && document.GetProperty("Name").GetString() == ".editorconfig");
            if (shared)
            {
                var library = Assert.Single(result.GraphContexts, context =>
                    context.ProjectPath == Path.Combine(fixture.Path, "Library", "Library.csproj"));
                var references = root.GetProperty("MetadataReferences").EnumerateArray().Where(
                    entry => entry.GetProperty("FilePath").GetString() == library.Context.EvaluatedReferenceAssemblyPath).ToArray();
                Assert.Equal(2, references.Length);
                foreach (var reference in references)
                {
                    Assert.True(File.Exists(reference.GetProperty("FilePath").GetString()));
                    Assert.False(reference.GetProperty("EmbedInteropTypes").GetBoolean());
                }
                var aliases = references.SelectMany(reference =>
                {
                    var values = reference.GetProperty("Aliases").EnumerateArray().Select(alias => alias.GetString()).ToArray();
                    return values.Length == 0 ? new[] { "global" } : values;
                }).Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(new[] { "ProbeLibrary", "global" }, aliases);
            }
        }
    }
}
