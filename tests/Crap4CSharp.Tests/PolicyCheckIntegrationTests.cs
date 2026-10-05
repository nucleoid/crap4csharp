using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PolicyCheckIntegrationTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
    private const string PolicyLogical = "tests/Fixtures/policy-check/policy.json";
#else
    private const string BuildConfiguration = "Release";
    private const string PolicyLogical = "tests/Fixtures/policy-check/policy-release.json";
#endif

    [Fact]
    public async Task TrustedCheckRunsEndToEndAgainstFreshCurrentProjectEvidence()
    {
        var repository = RepositoryRoot();
        const string projectLogical = "tests/Crap4CSharp.ProvenanceFixture/Crap4CSharp.ProvenanceFixture.csproj";
        using var directory = TestDirectory.Create("crap4csharp-policy-check-e2e");
        var (locator, manifest, manifestContext, inputs, bytes) = await CreateBundle(repository, projectLogical,
            PolicyLogical, typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location,
            Path.Combine(directory.Path, "bundle"));
        // Every declared consumer-input role and the actual recipe outputs must be protected,
        // independently of the CLI's early refusal of existing result destinations.
        var declaredFile = Path.Combine(repository, PolicyLogical.Replace('/', Path.DirectorySeparatorChar));
        var declaredBytes = await File.ReadAllBytesAsync(declaredFile, TestContext.Current.CancellationToken);
        var protectedManifest = manifest with { Contexts = [manifestContext with { Inputs =
            [.. inputs, new ManifestInput("configuration", PolicyLogical, "inputs/configuration.bin",
                declaredBytes.Length, CanonicalIdentity.Sha256(declaredBytes), "utf-8", false)
                { RepositoryPath = PolicyLogical }] }] };
        var current = await CurrentEvidenceAdapter.CaptureSupportedAsync(protectedManifest, repository,
            TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Contains(declaredFile, current.ProtectedPaths!);
        Assert.Contains(typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location, current.ProtectedPaths!);
        Assert.Contains(Path.ChangeExtension(typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location, ".pdb"), current.ProtectedPaths!);
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", PolicyLogical,
            "--base", "HEAD", "--format", "json"], repository, output, error,
            TestContext.Current.CancellationToken);

        Assert.True(exit == 0, error + Environment.NewLine + output);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("base-trusted", result.RootElement.GetProperty("evaluation").GetProperty("policyTrust")
            .GetProperty("trust").GetString());
        Assert.Equal("verified", result.RootElement.GetProperty("evaluation").GetProperty("provenance")
            .GetProperty("status").GetString());

        foreach (var consumerFile in new[] { "App.csproj", "Directory.Build.props", "Shared/Util.cs", "global.json" })
        {
            var destination = Path.Combine(directory.Path, "consumer", consumerFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var original = Encoding.UTF8.GetBytes("must not be overwritten by result JSON");
            await File.WriteAllBytesAsync(destination, original, TestContext.Current.CancellationToken);
            var stamp = File.GetLastWriteTimeUtc(destination);
            var guardedOutput = new StringWriter();
            var guardedExit = await global::App.RunAsync(["check", "--reuse-artifacts", locator,
                "--policy", PolicyLogical, "--base", "HEAD", "--format", "json", "--output", destination],
                repository, guardedOutput, TextWriter.Null, TestContext.Current.CancellationToken);
            Assert.Equal(1, guardedExit);
            Assert.Equal(original, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(destination));
        }

        foreach (var forgedPath in new[] { "Crap4CSharp.slnx", "does-not-exist.cs",
                     "src/App/Migrations/Gate.cs" })
        {
            var forgedContext = manifestContext with
            { Inputs = inputs.Select(input => input.Generated ? input : input with
                { RepositoryPath = forgedPath }).ToArray() };
            var forged = manifest with { Contexts = [forgedContext] };
            var pathError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CurrentEvidenceAdapter.CaptureSupportedAsync(forged, repository, TimeSpan.FromMinutes(2),
                    TestContext.Current.CancellationToken));
            Assert.Contains("repository path", pathError.Message, StringComparison.OrdinalIgnoreCase);
        }

        var nonGitManifest = manifest with
        { Revision = new ManifestRevision("none", "none", "workspace", null, null, null) };
        var nonGitLocator = ArtifactCaptureAdapter.PublishNew(nonGitManifest, bytes,
            Path.Combine(directory.Path, "non-git-bundle"));
        var failureOutput = new StringWriter();
        var failed = await global::App.RunAsync(["check", "--reuse-artifacts", nonGitLocator,
            "--policy", PolicyLogical, "--base", "HEAD", "--format", "json"], repository,
            failureOutput, TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Equal(1, failed);
        using var failure = JsonDocument.Parse(failureOutput.ToString());
        Assert.Equal("provenance.gitRevisionRequired", failure.RootElement.GetProperty("evaluation")
            .GetProperty("decision").GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Shared/Util.cs")]
    [InlineData("global.json")]
    public async Task FailedCheckPreservesExistingConsumerInputsBeforeLoader(string destination)
    {
        using var directory = TestDirectory.Create("crap4csharp-failed-check-preservation");
        var path = Path.Combine(directory.Path, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Encoding.UTF8.GetBytes("consumer input that must never become a result document");
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(path);
        var output = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", "missing-manifest.json",
            "--policy", "policy.json", "--base", "HEAD", "--output", destination, "--format", "json"],
            directory.Path, output, TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("output.aliasesPolicyInput", result.RootElement.GetProperty("evaluation").GetProperty("decision")
            .GetProperty("reason").GetString());
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task CommittedBaseScopeSubdirectoryProjectSelectsDebtAndRejectsForgedRepositoryPaths()
    {
        using var fixture = TestDirectory.Create("crap4csharp-committed-base-scope");
        const string projectLogical = "App/App.csproj";
        var project = fixture.Write(projectLogical, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Crap4CSharp.ProvenanceFixture</AssemblyName><LangVersion>latest</LangVersion><DebugType>portable</DebugType></PropertyGroup></Project>");
        var originalSource = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs"), TestContext.Current.CancellationToken);
        var source = fixture.Write("App/CompiledEvidence.cs", originalSource);
        fixture.Write("policy.json", JsonSerializer.Serialize(new
        {
            schemaVersion = RepositoryPolicy.Version, mode = "strict", productionProjects = new[] { projectLogical },
            testProjects = new[] { projectLogical }, configuration = BuildConfiguration, targetFrameworks = new[] { "net10.0" },
            scope = "base", threshold = 0, missingCoverage = "fail", requiredChecks = new[] { "tests", "coverage", "crap" },
            exclusions = Array.Empty<string>(), ruleset = ComplexityRules.CallablesV1, exemptionFiles = Array.Empty<string>()
        }));
        async Task<string> Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput.Trim();
        }
        await Git("init", "--quiet");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "user.name", "Fixture");
        await Git("add", "--", projectLogical, "App/CompiledEvidence.cs", "policy.json");
        await Git("commit", "--quiet", "-m", "trusted base policy");
        var baseRevision = await Git("rev-parse", "HEAD");
        await File.WriteAllTextAsync(source, originalSource.Replace("var value = 1;", "var value = 2;", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await Git("add", "--", "App/CompiledEvidence.cs");
        await Git("commit", "--quiet", "-m", "committed callable edit");
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(source);
        var restored = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restored.ExitCode == 0, restored.StandardOutput + restored.StandardError);
        var built = await ProjectBuildPreparation.BuildAsync(project, BuildConfiguration, "net10.0", "AnyCPU", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(built.ExitCode == 0, built.StandardOutput + built.StandardError);
        var (locator, manifest, context, _, bytes) = await CreateBundle(fixture.Path, projectLogical, "policy.json",
            Path.Combine(fixture.Path, "App", "bin", BuildConfiguration, "net10.0", "Crap4CSharp.ProvenanceFixture.dll"),
            Path.Combine(fixture.Path, "bundle"), baseRevision);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", "policy.json",
            "--base", baseRevision, "--format", "json"], fixture.Path, output, error, TestContext.Current.CancellationToken);
        Assert.True(exit == 2, error + Environment.NewLine + output);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("base-trusted", result.RootElement.GetProperty("evaluation").GetProperty("policyTrust").GetProperty("trust").GetString());
        Assert.Equal("crap.thresholdExceeded", result.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        Assert.NotEmpty(result.RootElement.GetProperty("evaluation").GetProperty("findings").EnumerateArray());
        foreach (var forgedPath in new[] { "App/App.csproj", "App/not-present.cs", "App/Migrations/Gate.cs" })
        {
            var forged = manifest with { Contexts = [context with { Inputs = context.Inputs.Select(input =>
                input.Generated ? input : input with { RepositoryPath = forgedPath }).ToArray() }] };
            var forgedLocator = ArtifactCaptureAdapter.PublishNew(forged, bytes,
                Path.Combine(fixture.Path, "forged-" + Guid.NewGuid().ToString("N")));
            var forgedOutput = new StringWriter();
            var forgedExit = await global::App.RunAsync(["check", "--reuse-artifacts", forgedLocator, "--policy", "policy.json",
                "--base", baseRevision, "--format", "json"], fixture.Path, forgedOutput, TextWriter.Null, TestContext.Current.CancellationToken);
            Assert.Equal(1, forgedExit);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedTrustedExemptionsSurviveEditingAnotherSourceInTheSameProject(bool duplicateAnonymous)
    {
        using var fixture = TestDirectory.Create("crap4csharp-committed-exemption-context");
        const string projectLogical = "App/App.csproj";
        var project = fixture.Write(projectLogical, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Crap4CSharp.ProvenanceFixture</AssemblyName><LangVersion>latest</LangVersion><DebugType>portable</DebugType></PropertyGroup></Project>");
        var original = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs"), TestContext.Current.CancellationToken);
        // Duplicate identity uses an explicit exact-point protocol control below; Coverlet's
        // columnless projection merges parent/lambda points and must remain fail-closed.
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var code = duplicateAnonymous ? original.Replace("var value = 1;",
                "return ((System.Func<int>)(" + newline + "            () => 1))() + ((System.Func<int>)(" + newline +
                "            () => 1))();", StringComparison.Ordinal)
                .Replace("return value;", "", StringComparison.Ordinal)
            : original.Replace("public int M()", "public async System.Threading.Tasks.Task<int> M()", StringComparison.Ordinal)
                .Replace("var value = 1;", "var value = await System.Threading.Tasks.Task.FromResult(1);", StringComparison.Ordinal);
        fixture.Write("App/CompiledEvidence.cs", code);
        var testProject = fixture.Write("App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../App/App.csproj" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" />
              </ItemGroup>
            </Project>
            """);
        fixture.Write("App.Tests/GateTests.cs", duplicateAnonymous
            ? "public class GateTests { [Xunit.Fact] public void M() { Xunit.Assert.Equal(2, new Crap4CSharp.ProvenanceFixture.CompiledEvidence().M()); } }"
            : "public class GateTests { [Xunit.Fact] public async System.Threading.Tasks.Task M() { Xunit.Assert.Equal(1, await new Crap4CSharp.ProvenanceFixture.CompiledEvidence().M()); } }");
        async Task<string> CaptureCoverage(string label)
        {
            var builtTests = await ProcessRunner.RunAsync("dotnet", ["build", testProject, "-c", BuildConfiguration,
                "--no-restore", "-m:1", "-nr:false", "-p:BuildProjectReferences=false"], fixture.Path,
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.True(builtTests.ExitCode == 0, builtTests.StandardOutput + builtTests.StandardError);
            var results = Path.Combine(fixture.Path, label);
            var tested = await ProcessRunner.RunAsync("dotnet", ["test", testProject, "-c", BuildConfiguration,
                "--no-build", "--no-restore", "-m:1", "-nr:false", "--collect:XPlat Code Coverage", "--results-directory", results,
                "--", "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover"], fixture.Path,
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.True(tested.ExitCode == 0, tested.StandardOutput + tested.StandardError);
            var collected = Assert.Single(Directory.EnumerateFiles(results, "coverage.opencover.xml", SearchOption.AllDirectories));
            if (!duplicateAnonymous) return collected;
            // Adapter control, NOT a claim that Coverlet emits exclusive parent/lambda coordinates.
            // The real invocation above exercises the fixture. Model a supported exact-point report
            // independently from its lossy collector projection, binding every point to the actual PDB.
            var path = Path.Combine(fixture.Path, "App", "bin", BuildConfiguration, "net10.0", "Crap4CSharp.ProvenanceFixture.dll");
            var evidence = ArtifactEvidenceInspector.InspectBuild(ImmutableArray.Create(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)),
                ImmutableArray.Create(await File.ReadAllBytesAsync(Path.ChangeExtension(path, ".pdb"), TestContext.Current.CancellationToken)));
            var documents = evidence.Documents.Keys.Order(StringComparer.Ordinal).Select((document, index) => (document, id: index + 1)).ToDictionary(item => item.document, item => item.id);
            var files = new XElement("Files", documents.Select(item => new XElement("File",
                new XAttribute("uid", item.Value), new XAttribute("fullPath", item.Key))));
            var classes = new XElement("Classes");
            foreach (var group in evidence.Methods.Where(method => method.SequencePoints.Count > 0).GroupBy(method => method.TypeName))
            {
                var methods = new XElement("Methods");
                foreach (var method in group)
                {
                    var points = new XElement("SequencePoints", method.SequencePoints.Select(point => new XElement("SequencePoint",
                        new XAttribute("vc", 1), new XAttribute("offset", point.Offset),
                        new XAttribute("sl", point.StartLine), new XAttribute("sc", point.StartColumn),
                        new XAttribute("el", point.EndLine), new XAttribute("ec", point.EndColumn),
                        new XAttribute("fileid", documents[point.Document]))));
                    var returnType = method.MethodName is ".ctor" or ".cctor" ? "System.Void " : "System.Int32 ";
                    methods.Add(new XElement("Method", new XElement("Name", returnType + method.TypeName + "::" + method.MethodName + "()"), points));
                }
                classes.Add(new XElement("Class", new XElement("FullName", group.Key), methods));
            }
            var xml = new XElement("CoverageSession", new XElement("Modules", new XElement("Module",
                new XElement("ModuleName", evidence.ModuleIdentity), files, classes)));
            var control = Path.Combine(results, "exact-point-control.xml");
            await File.WriteAllTextAsync(control, xml.ToString(), TestContext.Current.CancellationToken);
            return control;
        }
        string Policy(string[] exemptions) => JsonSerializer.Serialize(new
        {
            schemaVersion = RepositoryPolicy.Version, mode = "strict", productionProjects = new[] { projectLogical },
            testProjects = new[] { projectLogical }, configuration = BuildConfiguration, targetFrameworks = new[] { "net10.0" },
            scope = "all", threshold = 100, missingCoverage = "fail", requiredChecks = new[] { "tests", "coverage", "crap" },
            exclusions = Array.Empty<string>(), ruleset = ComplexityRules.CallablesV1, exemptionFiles = exemptions
        });
        fixture.Write("policy.json", Policy([]));
        async Task<string> Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput.Trim();
        }
        await Git("init", "--quiet");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "user.name", "Fixture");
        await Git("add", "--", projectLogical, "App/CompiledEvidence.cs", "policy.json");
        await Git("commit", "--quiet", "-m", "exemption fixture bootstrap");
        var restore = await ProjectBuildPreparation.RestoreAsync(testProject, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var build = await ProjectBuildPreparation.BuildAsync(project, BuildConfiguration, "net10.0", "AnyCPU", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
        var assemblyPath = Path.Combine(fixture.Path, "App", "bin", BuildConfiguration, "net10.0", "Crap4CSharp.ProvenanceFixture.dll");
        var (probeLocator, _, reviewedContext, _, _) = await CreateBundle(fixture.Path, projectLogical, "policy.json", assemblyPath,
            Path.Combine(fixture.Path, "reviewed-probe"), coveragePath: await CaptureCoverage("reviewed-coverage"));
        var probe = AnalyzeCommand.Replay(probeLocator, fixture.Path, null, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, TestContext.Current.CancellationToken, allSources: true);
        var unsupported = probe.Evaluation.Callables!.Where(item => item.CoverageReason ==
            (duplicateAnonymous ? CoverageReasonCodes.AmbiguousCallableOwnership : CoverageReasonCodes.UnsupportedGeneratedMapping) &&
                (!duplicateAnonymous || item.Kind == "Lambda"))
            .GroupBy(item => item.CallableId).ToArray();
        Assert.Single(unsupported);
        var entries = unsupported.Select(group =>
        {
            var item = group.First();
            var entry = new Dictionary<string, object>
            {
                ["ruleset"] = item.Ruleset, ["contextId"] = item.ContextId, ["targetFramework"] = "net10.0",
                ["callableId"] = item.CallableId, ["bodyChecksum"] = item.BodyChecksum, ["reasonCode"] = item.CoverageReason!,
                ["justification"] = "reviewed fixture limitation", ["reviewReference"] = "fixture-review",
                ["familyIds"] = item.FamilyIds
            };
            if (duplicateAnonymous) entry["memberCount"] = group.Count();
            return entry;
        }).ToArray();
        fixture.Write("exemptions.json", JsonSerializer.Serialize(new { version = CallableExemptions.Version, entries }));
        fixture.Write("policy.json", Policy(["exemptions.json"]));
        await Git("add", "--", "policy.json", "exemptions.json");
        await Git("commit", "--quiet", "-m", "approve exact unsupported callable exemption");
        var approvedBase = await Git("rev-parse", "HEAD");
        fixture.Write("App/Different.cs", "namespace Crap4CSharp.ProvenanceFixture; public class Different { }");
        await Git("add", "--", "App/Different.cs");
        await Git("commit", "--quiet", "-m", "unrelated authored context change");
        build = await ProjectBuildPreparation.BuildAsync(project, BuildConfiguration, "net10.0", "AnyCPU", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
        var (locator, _, currentContext, _, currentBytes) = await CreateBundle(fixture.Path, projectLogical, "policy.json", assemblyPath,
            Path.Combine(fixture.Path, "current-bundle"), coveragePath: await CaptureCoverage("current-coverage"));
        Assert.NotEqual(reviewedContext.Id, currentContext.Id);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", "policy.json",
            "--base", approvedBase, "--format", "json"], fixture.Path, output, error, TestContext.Current.CancellationToken);
        Assert.True(exit == 0, error + Environment.NewLine + output + Environment.NewLine +
            Encoding.UTF8.GetString(currentBytes["evidence/coverage.xml"].AsSpan()));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Contains(result.RootElement.GetProperty("evaluation").GetProperty("callableExemptions").EnumerateArray(),
            entry => entry.GetProperty("status").GetString() == "exempted-unsupported");
    }

    [Theory]
    [InlineData("obj/CompiledEvidence.cs", "", false, 2)]
    [InlineData("Legacy/CompiledEvidence.cs", "obj/CompiledEvidence.cs", false, 2)]
    [InlineData("CompiledEvidence.cs", "", true, 0)]
    public async Task CommittedCliEnforcesTargetAddedAuthoredInputsAndAcceptsActualIntermediateLayout(
        string physical, string link, bool customIntermediate, int expectedExit)
    {
        using var fixture = TestDirectory.Create("crap4csharp-generated-cli");
        const string projectLogical = "App.csproj";
        if (customIntermediate)
            fixture.Write("Directory.Build.props", "<Project><PropertyGroup><BaseIntermediateOutputPath>artifacts/intermediate/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        var project = fixture.Write(projectLogical, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Crap4CSharp.ProvenanceFixture</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DebugType>portable</DebugType></PropertyGroup>
              <Target Name="AddAuthoredInput" BeforeTargets="CoreCompile">
                <ItemGroup><Compile Include="{{physical}}" Link="{{link}}" /></ItemGroup>
              </Target>
            </Project>
            """);
        var original = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs"), TestContext.Current.CancellationToken);
        var source = fixture.Write(physical, original);
        fixture.Write("policy.json", JsonSerializer.Serialize(new
        {
            schemaVersion = RepositoryPolicy.Version, mode = "strict", productionProjects = new[] { projectLogical },
            testProjects = new[] { projectLogical }, configuration = BuildConfiguration, targetFrameworks = new[] { "net10.0" },
            scope = "all", threshold = customIntermediate ? 100 : 0, missingCoverage = "fail",
            requiredChecks = new[] { "tests", "coverage", "crap" }, exclusions = Array.Empty<string>(),
            ruleset = ComplexityRules.CallablesV1, exemptionFiles = Array.Empty<string>()
        }));
        async Task Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }
        await Git("init", "--quiet");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "user.name", "Fixture");
        await Git("add", "-f", "--", projectLogical, physical, "policy.json");
        if (customIntermediate) await Git("add", "--", "Directory.Build.props");
        await Git("commit", "--quiet", "-m", "trusted target-added authored source");
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(source);
        var restored = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restored.ExitCode == 0, restored.StandardOutput + restored.StandardError);
        var built = await ProjectBuildPreparation.BuildAsync(project, BuildConfiguration, "net10.0", "AnyCPU", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(built.ExitCode == 0, built.StandardOutput + built.StandardError);
        // CLI/current-binding integration uses the existing explicit synthetic test-result and
        // coverage adapter controls, with real compiled PE/PDB and committed consumer inputs.
        var (locator, _, context, _, _) = await CreateBundle(fixture.Path, projectLogical, "policy.json",
            Path.Combine(fixture.Path, "bin", BuildConfiguration, "net10.0", "Crap4CSharp.ProvenanceFixture.dll"),
            Path.Combine(fixture.Path, "bundle"));
        var authored = Assert.Single(context.Inputs, input => input.RepositoryPath == physical);
        Assert.False(authored.Generated);
        if (customIntermediate)
            Assert.Contains(context.Inputs, input => input.Generated && input.RepositoryPath!.StartsWith("artifacts/intermediate/", StringComparison.Ordinal));
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", "policy.json",
            "--base", "HEAD", "--format", "json"], fixture.Path, output, error, TestContext.Current.CancellationToken);
        Assert.True(exit == expectedExit, error + Environment.NewLine + output);
        using var resultDocument = JsonDocument.Parse(output.ToString());
        Assert.Equal("base-trusted", resultDocument.RootElement.GetProperty("evaluation").GetProperty("policyTrust").GetProperty("trust").GetString());
        if (!customIntermediate)
            Assert.Equal("crap.thresholdExceeded", resultDocument.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
    }

    private static async Task<(string Locator, RunManifest Manifest, ManifestContext Context,
        List<ManifestInput> Inputs, Dictionary<string, ImmutableArray<byte>> Bytes)> CreateBundle(
        string repository, string projectLogical, string policyLogical, string assemblyPath,
        string bundleDirectory, string? baseRevision = null, string? coveragePath = null)
    {
        var project = Path.Combine(repository, projectLogical.Replace('/', Path.DirectorySeparatorChar));
        var projectDirectory = Path.GetDirectoryName(project)!;
        var request = new ProjectContextLoadRequest(project, BuildConfiguration, "AnyCPU", ["net10.0"], true, true,
            TimeSpan.FromMinutes(2));
        var loaded = await ProjectContextLoader.LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason ?? string.Join(Environment.NewLine, loaded.Diagnostics));
        var context = Assert.Single(loaded.Contexts);
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var assembly = ImmutableArray.Create(await File.ReadAllBytesAsync(assemblyPath, TestContext.Current.CancellationToken));
        var pdb = ImmutableArray.Create(await File.ReadAllBytesAsync(pdbPath, TestContext.Current.CancellationToken));
        var inspected = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var inputs = new List<ManifestInput>();
        var index = 0;
        foreach (var source in context.Sources.OrderBy(item => item.LogicalPath, StringComparer.Ordinal))
        {
            var physical = Path.GetFullPath(source.ResolvedPath ?? source.PhysicalPath, projectDirectory);
            var value = ImmutableArray.Create(await File.ReadAllBytesAsync(physical, TestContext.Current.CancellationToken));
            var sourceLocator = $"inputs/source-{index++}.bin";
            bytes.Add(sourceLocator, value);
            inputs.Add(new ManifestInput("source", source.LogicalPath, sourceLocator, value.Length,
                CanonicalIdentity.Sha256(value.AsSpan()), "utf-8", source.IsGenerated)
            {
                RepositoryPath = Path.GetRelativePath(repository, physical).Replace('\\', '/')
            });
        }

        var coverage = coveragePath is not null
            ? ImmutableArray.Create(await File.ReadAllBytesAsync(coveragePath, TestContext.Current.CancellationToken))
            : ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "<coverage><packages><package name=\"Crap4CSharp.ProvenanceFixture\"><classes><class name=\"Crap4CSharp.ProvenanceFixture.CompiledEvidence\" filename=\"CompiledEvidence.cs\"><methods><method name=\".ctor\" signature=\"()\"><lines><line number=\"5\" hits=\"1\" /></lines></method><method name=\"M\" signature=\"()\"><lines><line number=\"9\" hits=\"1\" /><line number=\"10\" hits=\"1\" /></lines></method></methods></class></classes></package></packages></coverage>".Replace("filename=\"CompiledEvidence.cs\"",
                "filename=\"" + context.Sources.Single(source => !source.IsGenerated &&
                    source.LogicalPath.EndsWith("CompiledEvidence.cs", StringComparison.Ordinal)).LogicalPath + "\"",
                StringComparison.Ordinal)));
        var trx = ImmutableArray.Create(Encoding.UTF8.GetBytes($$"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest storage="{{inspected.ModuleIdentity}}.dll" /></TestDefinitions>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """));
        var scope = ImmutableArray.Create(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = 1,
            sources = inputs.Where(item => !item.Generated)
                .Select(item => item.RepositoryPath).ToArray()
        })));
        var policyBytes = await File.ReadAllBytesAsync(Path.Combine(repository,
            policyLogical.Replace('/', Path.DirectorySeparatorChar)), TestContext.Current.CancellationToken);
        var policy = RepositoryPolicyParser.Parse(policyBytes, policyLogical);
        var canonicalPolicy = ImmutableArray.Create(policy.CanonicalBytes);
        foreach (var item in new[]
        {
            ("evidence/app.dll", assembly), ("evidence/app.pdb", pdb), ("evidence/test.dll", assembly),
            ("evidence/test.pdb", pdb), ("evidence/results.trx", trx), ("evidence/coverage.xml", coverage),
            ("evaluation/scope.json", scope), ("evaluation/policy.json", canonicalPolicy)
        }) bytes.Add(item.Item1, item.Item2);

        var manifestContext = new ManifestContext(context.ContextId, projectLogical, context.TargetFramework,
            context.Configuration, context.Platform, "", "", "", true, true, inputs)
        {
            ParseOptions = new ManifestParseOptions(inspected.LanguageVersion, context.SourceKind.ToString(),
                inspected.PreprocessorSymbols, new Dictionary<string, string>()),
            PathPolicy = new ManifestPathPolicy("sensitive",
                [new ManifestReportRootMapping("/_/tests/Crap4CSharp.ProvenanceFixture", ""),
                 new ManifestReportRootMapping(projectDirectory.Replace('\\', '/'), ""),
                 .. context.Sources.Select(source => new ManifestReportRootMapping(
                     source.ResolvedPath!.Replace('\\', '/'), source.LogicalPath))]),
            CurrentRevalidation = new ManifestCurrentRevalidation(CurrentEvidenceAdapter.SupportedRecipeProvider,
                projectLogical, Path.GetRelativePath(repository, assemblyPath).Replace('\\', '/'),
                Path.GetRelativePath(repository, pdbPath).Replace('\\', '/'))
        };
        var build = new ManifestBuild("build", context.ContextId, inspected.ModuleIdentity,
            CanonicalIdentity.Sha256(assembly.AsSpan()), inspected.Mvid, CanonicalIdentity.Sha256(pdb.AsSpan()),
            inspected.DebugIdentity);
        var execution = new ManifestExecution("test", context.ContextId, build.Id, true, 0, 1, 1, 0, 0)
        {
            TestProject = projectLogical, TestModuleIdentity = inspected.ModuleIdentity,
            TestAssemblySha256 = CanonicalIdentity.Sha256(assembly.AsSpan()), TestMvid = inspected.Mvid,
            TestPdbSha256 = CanonicalIdentity.Sha256(pdb.AsSpan()), TestDebugIdentity = inspected.DebugIdentity
        };
        ManifestArtifact Artifact(string id, string kind, string locator, ImmutableArray<byte> value,
            string? contextId = null, string? buildId = null, string? executionId = null, string? format = null,
            string? coordinate = null) => new(id, kind, locator, value.Length, CanonicalIdentity.Sha256(value.AsSpan()),
                contextId, buildId, executionId, format, coordinate);
        var artifacts = new[]
        {
            Artifact("scope", "scope", "evaluation/scope.json", scope),
            Artifact("policy", "policy", "evaluation/policy.json", canonicalPolicy),
            Artifact("assembly", "assembly", "evidence/app.dll", assembly, context.ContextId, build.Id),
            Artifact("pdb", "pdb", "evidence/app.pdb", pdb, context.ContextId, build.Id),
            Artifact("test-assembly", "test-assembly", "evidence/test.dll", assembly, context.ContextId, build.Id, execution.Id),
            Artifact("test-pdb", "test-pdb", "evidence/test.pdb", pdb, context.ContextId, build.Id, execution.Id),
            Artifact("test-result", "test-result", "evidence/results.trx", trx, context.ContextId, build.Id, execution.Id, "trx"),
            Artifact("coverage", "coverage", "evidence/coverage.xml", coverage, context.ContextId, build.Id, execution.Id, coveragePath is null ? "cobertura" : "opencover", coveragePath is null ? "line" : "sequence-point")
        };
        var manifest = new RunManifest(ManifestIdentity.SchemaVersion, CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("git", "pending", "pending", "pending", null, "pending"),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [manifestContext], [build], [execution], artifacts,
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256(scope.AsSpan()), policy.Hash, null, null), null);
        var observed = CurrentEvidenceAdapter.Capture(manifest, repository);
        manifest = manifest with
        {
            Revision = new ManifestRevision("git", observed.RepositoryIdentity, observed.WorkspaceIdentity,
                observed.Head, baseRevision, observed.Head) { StateHash = observed.StateHash }
        };
        var locator = ArtifactCaptureAdapter.PublishNew(manifest, bytes, bundleDirectory);
        return (locator, manifest, manifestContext, inputs, bytes);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Crap4CSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
