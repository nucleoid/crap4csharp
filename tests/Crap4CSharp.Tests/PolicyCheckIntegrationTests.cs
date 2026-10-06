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

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", PolicyLogical,
            "--base", "HEAD", "--format", "json"], repository, output, error,
            TestContext.Current.CancellationToken);

        Assert.True(exit == 0, error + Environment.NewLine + output);
        using var result = JsonDocument.Parse(output.ToString());
        var run = result.RootElement.GetProperty("run");
        var reportedDuration = run.GetProperty("durationMilliseconds").GetDouble();
        Assert.True(reportedDuration >= elapsed.Elapsed.TotalMilliseconds * .5,
            $"CLI execution took {elapsed.Elapsed.TotalMilliseconds} ms but reported only {reportedDuration} ms.");
        Assert.True(run.GetProperty("finishedAt").GetDateTimeOffset() >=
            run.GetProperty("startedAt").GetDateTimeOffset().AddMilliseconds(reportedDuration - 1));
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

    [Fact]
    public async Task CurrentRevalidationRejectsRecipeForAnotherDeclaredProductionProject()
    {
        var repository = RepositoryRoot();
        const string projectLogical = "tests/Crap4CSharp.ProvenanceFixture/Crap4CSharp.ProvenanceFixture.csproj";
        using var directory = TestDirectory.Create("crap4csharp-project-recipe-binding");
        var (_, manifest, context, _, bytes) = await CreateBundle(repository, projectLogical,
            PolicyLogical, typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location,
            Path.Combine(directory.Path, "bundle"));
        // Keep the genuine current recipe, source paths and PE/PDB, but assert that
        // these observations belong to a different policy production project.
        var forged = manifest with { Contexts = [context with
            { Project = "Shadow/Crap4CSharp.ProvenanceFixture.csproj" }] };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CurrentEvidenceAdapter.CaptureSupportedAsync(forged, repository,
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        Assert.Contains("project", error.Message, StringComparison.OrdinalIgnoreCase);

        // Keep the approved policy's declaration, but point the independent recipe at
        // a sibling project. Publication seals the forged bundle so the CLI must
        // reject its semantics rather than merely failing an integrity hash.
        var forgedRecipe = manifest with { Contexts = [context with
        { CurrentRevalidation = context.CurrentRevalidation! with
            { Project = "Shadow/Crap4CSharp.ProvenanceFixture.csproj" } }] };
        var locator = ArtifactCaptureAdapter.PublishNew(forgedRecipe, bytes,
            Path.Combine(directory.Path, "forged-recipe"));
        var output = new StringWriter();
        var stderr = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator,
            "--policy", PolicyLogical, "--base", "HEAD", "--format", "json"], repository,
            output, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, result.RootElement.GetProperty("run").GetProperty("exitCode").GetInt32());
        Assert.Contains("recipe project", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("historical")]
    [InlineData("missing")]
    [InlineData("provider")]
    [InlineData("incomplete")]
    public async Task UnsupportedCurrentRecipeHasExplicitCliReasonBeforeGitOrProjectLoad(string shape)
    {
        var repository = RepositoryRoot();
        const string projectLogical = "tests/Crap4CSharp.ProvenanceFixture/Crap4CSharp.ProvenanceFixture.csproj";
        using var directory = TestDirectory.Create("crap4csharp-unsupported-recipe");
        var (_, manifest, context, _, bytes) = await CreateBundle(repository, projectLogical,
            PolicyLogical, typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location,
            Path.Combine(directory.Path, "bundle"));
        var unsupported = shape switch
        {
            "historical" => context with { CurrentRevalidation = null, ReuseRecipeComplete = false },
            "missing" => context with { CurrentRevalidation = null },
            "provider" => context with { CurrentRevalidation = context.CurrentRevalidation! with { Provider = "unknown-v99" } },
            _ => context with { ReuseRecipeComplete = false }
        };
        var altered = manifest with { Contexts = [unsupported] };
        var locator = ArtifactCaptureAdapter.PublishNew(altered, bytes, Path.Combine(directory.Path, "unsupported"));
        // The empty root has no Git metadata and no project. A recipe refusal must
        // take precedence over both Git acquisition and a real loader invocation.
        var root = Path.Combine(directory.Path, "empty-root");
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(repository, PolicyLogical), Path.Combine(root, "policy.json"));
        foreach (var command in new[] { "check", "baseline" })
        {
            var output = new StringWriter();
            var args = command == "check"
                ? new[] { "check", "--reuse-artifacts", locator, "--policy", "policy.json", "--base", "not-a-real-ref", "--format", "json" }
                : new[] { "baseline", "create", "--reuse-artifacts", locator, "--policy", "policy.json", "--output", "candidate.json", "--format", "json" };
            var exit = await global::App.RunAsync(args, root, output, TextWriter.Null, TestContext.Current.CancellationToken);
            Assert.Equal(1, exit);
            using var result = JsonDocument.Parse(output.ToString());
            var reason = command == "check"
                ? result.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString()
                : result.RootElement.GetProperty("reason").GetString();
            Assert.Equal("provenance.revalidationRecipeUnsupported", reason);
            Assert.False(File.Exists(Path.Combine(root, "candidate.json")));
        }
    }

    [Fact]
    public async Task BaselineNumericExemptionVersionProducesOneStructuredFailureAndNoCandidate()
    {
        using var fixture = TestDirectory.Create("crap4csharp-baseline-malformed-exemption");
        const string projectLogical = "App/App.csproj";
        var project = fixture.Write(projectLogical, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Crap4CSharp.ProvenanceFixture</AssemblyName><DebugType>portable</DebugType></PropertyGroup></Project>");
        fixture.Write("App/CompiledEvidence.cs", await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs"), TestContext.Current.CancellationToken));
        fixture.Write("policy.json", JsonSerializer.Serialize(new
        {
            schemaVersion = RepositoryPolicy.Version, mode = "strict", productionProjects = new[] { projectLogical },
            testProjects = new[] { projectLogical }, configuration = BuildConfiguration, targetFrameworks = new[] { "net10.0" },
            scope = "all", threshold = 100, missingCoverage = "fail", requiredChecks = new[] { "tests", "coverage", "crap" },
            exclusions = Array.Empty<string>(), ruleset = ComplexityRules.CallablesV1, exemptionFiles = new[] { "exemptions.json" }
        }));
        fixture.Write("exemptions.json", "{\"version\":1,\"entries\":[]}");
        foreach (var args in new[] { new[] { "init", "--quiet" }, new[] { "config", "user.email", "fixture@example.invalid" },
            new[] { "config", "user.name", "Fixture" }, new[] { "add", "." }, new[] { "commit", "--quiet", "-m", "fixture" } })
        {
            var git = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(git.ExitCode == 0, git.StandardError);
        }
        var restored = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restored.ExitCode == 0, restored.StandardError);
        var built = await ProjectBuildPreparation.BuildAsync(project, BuildConfiguration, "net10.0", "AnyCPU", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(built.ExitCode == 0, built.StandardError);
        var (locator, _, _, _, _) = await CreateBundle(fixture.Path, projectLogical, "policy.json",
            Path.Combine(fixture.Path, "App", "bin", BuildConfiguration, "net10.0", "Crap4CSharp.ProvenanceFixture.dll"),
            Path.Combine(fixture.Path, "bundle"));
        var output = new StringWriter();
        var exit = await global::App.RunAsync(["baseline", "create", "--policy", "policy.json",
            "--reuse-artifacts", locator, "--output", "candidate.json", "--format", "json"], fixture.Path,
            output, TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("baseline-command-result-v1", result.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("exemption.malformed", result.RootElement.GetProperty("reason").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.Path, "candidate.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BaselineDuplicateBuildOrMissingGitReturnsStructuredFailure(bool missingGit)
    {
        var repository = RepositoryRoot();
        const string projectLogical = "tests/Crap4CSharp.ProvenanceFixture/Crap4CSharp.ProvenanceFixture.csproj";
        using var directory = TestDirectory.Create("crap4csharp-baseline-process-failure");
        var (locator, manifest, _, _, bytes) = await CreateBundle(repository, projectLogical,
            PolicyLogical, typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location,
            Path.Combine(directory.Path, "bundle"));
        if (!missingGit)
        {
            // Deliberately malformed externally supplied bundle. The honest publisher
            // rejects this, so seal its manifest directly without weakening publication.
            var invalid = ManifestIdentity.Seal(manifest with
                { Builds = [.. manifest.Builds, manifest.Builds[0] with { Id = "duplicate-build" }], ManifestHash = null });
            await File.WriteAllTextAsync(locator, JsonSerializer.Serialize(invalid,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                TestContext.Current.CancellationToken);
        }
        var candidate = Path.Combine(directory.Path, "candidate.json");
        string document;
        int exit;
        if (missingGit)
        {
            // Change only the child environment: never mutate the test runner's PATH.
            var tool = typeof(global::App).Assembly.Location;
            var host = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            var process = await ProcessRunner.RunWithEnvironmentAsync(Path.GetFullPath(host),
                [tool, "baseline", "create", "--policy", PolicyLogical, "--reuse-artifacts", locator,
                    "--output", candidate, "--format", "json"], repository, TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken, new Dictionary<string, string> { ["PATH"] = directory.Path });
            exit = process.ExitCode;
            document = process.StandardOutput;
        }
        else
        {
            var output = new StringWriter();
            exit = await global::App.RunAsync(["baseline", "create", "--policy", PolicyLogical,
                "--reuse-artifacts", locator, "--output", candidate, "--format", "json"], repository,
                output, TextWriter.Null, TestContext.Current.CancellationToken);
            document = output.ToString();
        }
        Assert.Equal(1, exit);
        using var result = JsonDocument.Parse(document);
        Assert.Equal("baseline-command-result-v1", result.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("operationalError", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("baseline.generationFailed", result.RootElement.GetProperty("reason").GetString());
        Assert.False(File.Exists(candidate));
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
    [InlineData("new", 2)]
    [InlineData("deletion", 2)]
    [InlineData("untouched", 0)]
    [InlineData("relaxed", 2)]
    public async Task IncrementalCommittedBaseScopeEnforcesSelectedDebtUnderTrustedPolicy(string scenario, int expectedExit)
    {
        using var fixture = TestDirectory.Create("crap4csharp-incremental-base-matrix");
        const string projectLogical = "App/App.csproj";
        var project = fixture.Write(projectLogical, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Crap4CSharp.ProvenanceFixture</AssemblyName><DebugType>portable</DebugType><IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion></PropertyGroup></Project>");
        var original = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs"), TestContext.Current.CancellationToken);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var baseSource = original.Replace("var value = 1;", "System.GC.KeepAlive(1);" + newline + "        var value = 1;", StringComparison.Ordinal);
        var source = fixture.Write("App/CompiledEvidence.cs", baseSource);
        string Policy(string mode, string scope, double threshold, string? baseline) => JsonSerializer.Serialize(new
        {
            schemaVersion = RepositoryPolicy.Version, mode, productionProjects = new[] { projectLogical },
            testProjects = new[] { projectLogical }, configuration = BuildConfiguration, targetFrameworks = new[] { "net10.0" },
            scope, threshold, missingCoverage = "fail", requiredChecks = new[] { "tests", "coverage", "crap" },
            exclusions = Array.Empty<string>(), ruleset = ComplexityRules.CallablesV1,
            exemptionFiles = Array.Empty<string>(), baseline
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        fixture.Write("policy.json", Policy("strict", "all", 0, null));
        fixture.Write(".gitignore", "App/bin/\nApp/obj/\n*-bundle/\n*-coverage.xml\n");
        async Task<string> Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, fixture.Path, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput.Trim();
        }
        await Git("init", "--quiet");
        await Git("config", "user.email", "fixture@example.invalid");
        await Git("config", "user.name", "Fixture");
        await Git("add", "--", projectLogical, "App/CompiledEvidence.cs", "policy.json", ".gitignore");
        await Git("commit", "--quiet", "-m", "bootstrap strict onboarding");
        var restored = await ProjectBuildPreparation.RestoreAsync(project, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(restored.ExitCode == 0, restored.StandardError);
        async Task Build()
        {
            var built = await ProjectBuildPreparation.BuildAsync(project, BuildConfiguration, "net10.0", "AnyCPU", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.True(built.ExitCode == 0, built.StandardOutput + built.StandardError);
        }
        var assembly = Path.Combine(fixture.Path, "App", "bin", BuildConfiguration, "net10.0", "Crap4CSharp.ProvenanceFixture.dll");
        await Build();
        // Real committed sources and compiled PE/PDB; exact-point coverage and successful
        // TRX/test-module records are transparent protocol controls, not collector acceptance.
        var initialCoverage = await WriteExactPointCoverage(assembly, fixture.Path, "base-coverage.xml", 1);
        var (initialLocator, _, _, _, _) = await CreateBundle(fixture.Path, projectLogical, "policy.json", assembly,
            Path.Combine(fixture.Path, "initial-bundle"), coveragePath: initialCoverage);
        var bootstrapOutput = new StringWriter();
        var bootstrapError = new StringWriter();
        var bootstrapExit = await global::App.RunAsync(["baseline", "create", "--policy", "policy.json",
            "--reuse-artifacts", initialLocator, "--output", "baseline.json", "--format", "json"], fixture.Path,
            bootstrapOutput, bootstrapError, TestContext.Current.CancellationToken);
        Assert.True(bootstrapExit == 2, bootstrapError + Environment.NewLine + bootstrapOutput);
        var baseline = BaselineDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.Path, "baseline.json"), TestContext.Current.CancellationToken));
        Assert.NotEmpty(baseline.Entries);
        fixture.Write("policy.json", Policy("incremental", "base", 0, "baseline.json"));
        await Git("add", "--", "policy.json", "baseline.json");
        await Git("commit", "--quiet", "-m", "approve incremental baseline");
        var baseRevision = await Git("rev-parse", "HEAD");
        if (scenario == "deletion")
            fixture.Write("App/CompiledEvidence.cs", baseSource.Replace("        System.GC.KeepAlive(1);" + newline, "", StringComparison.Ordinal));
        else if (scenario != "untouched")
            fixture.Write("App/CompiledEvidence.cs", baseSource.Replace("    public int M()", "    public int NewDebt() => 42;" + newline + newline + "    public int M()", StringComparison.Ordinal));
        else fixture.Write("README.md", "unrelated documentation-only commit");
        if (scenario == "relaxed") fixture.Write("policy.json", Policy("incremental", "all", 100, "baseline.json"));
        await Git("add", "--", "App/CompiledEvidence.cs", "policy.json");
        if (scenario == "untouched") await Git("add", "--", "README.md");
        await Git("commit", "--quiet", "-m", "current committed scenario");
        var head = await Git("rev-parse", "HEAD");
        Assert.NotEqual(baseRevision, head);
        Assert.Equal(baseRevision, await Git("merge-base", baseRevision, "HEAD"));
        if (scenario == "deletion")
        {
            var diff = await Git("diff", "--numstat", baseRevision, "HEAD", "--", "App/CompiledEvidence.cs");
            Assert.StartsWith("0\t1\t", diff, StringComparison.Ordinal);
        }
        await Build();
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(source);
        var coverage = await WriteExactPointCoverage(assembly, fixture.Path, "current-coverage.xml",
            scenario is "deletion" or "untouched" ? 0 : 1);
        var observedScope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, baseRevision),
            fixture.Path, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(ScopeCompleteness.Complete, observedScope.Completeness);
        Assert.Equal(scenario == "untouched" ? [] : new[] { "App/CompiledEvidence.cs" },
            observedScope.Files.Select(file => file.NewPath).ToArray());
        var (locator, _, _, _, _) = await CreateBundle(fixture.Path, projectLogical, "policy.json", assembly,
            Path.Combine(fixture.Path, "current-bundle"), baseRevision, coverage,
            scopedSources: scenario == "untouched" ? [] : ["App/CompiledEvidence.cs"]);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await global::App.RunAsync(["check", "--reuse-artifacts", locator, "--policy", "policy.json",
            "--base", baseRevision, "--format", "json"], fixture.Path, output, error, TestContext.Current.CancellationToken);
        Assert.True(exit == expectedExit, error + Environment.NewLine + output);
        using var document = JsonDocument.Parse(output.ToString());
        var evaluation = document.RootElement.GetProperty("evaluation");
        Assert.Equal(baseRevision, evaluation.GetProperty("policyTrust").GetProperty("revision").GetString());
        Assert.Equal(0, evaluation.GetProperty("policy").GetProperty("threshold").GetDouble());
        var findings = evaluation.GetProperty("findings").EnumerateArray().ToArray();
        if (scenario == "deletion") Assert.Contains(findings, item => item.GetProperty("code").GetString() == "baseline.coverageWorsened");
        else if (scenario == "untouched") Assert.Empty(findings);
        else Assert.Contains(findings, item => item.GetProperty("code").GetString() == "crap.thresholdExceeded");
        if (scenario == "relaxed") Assert.Contains(evaluation.GetProperty("policyDifferences").EnumerateArray(),
            item => item.GetProperty("path").GetString() == "policy.json" && item.GetProperty("status").GetString() == "changed");
        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
    }

    private static async Task<string> WriteExactPointCoverage(string assemblyPath, string directory, string name, int hits)
    {
        var evidence = ArtifactEvidenceInspector.InspectBuild(
            ImmutableArray.Create(await File.ReadAllBytesAsync(assemblyPath, TestContext.Current.CancellationToken)),
            ImmutableArray.Create(await File.ReadAllBytesAsync(Path.ChangeExtension(assemblyPath, ".pdb"), TestContext.Current.CancellationToken)));
        var documents = evidence.Documents.Keys.Order(StringComparer.Ordinal).Select((document, index) => (document, id: index + 1))
            .ToDictionary(item => item.document, item => item.id);
        var classes = new XElement("Classes", evidence.Methods.Where(method => method.SequencePoints.Count > 0)
            .GroupBy(method => method.TypeName).Select(group => new XElement("Class", new XElement("FullName", group.Key),
                new XElement("Methods", group.Select(method => new XElement("Method",
                    new XElement("Name", (method.MethodName is ".ctor" or ".cctor" ? "System.Void " : "System.Int32 ") + method.TypeName + "::" + method.MethodName + "()"),
                    new XElement("SequencePoints", method.SequencePoints.Select(point => new XElement("SequencePoint",
                        new XAttribute("vc", hits), new XAttribute("offset", point.Offset), new XAttribute("sl", point.StartLine),
                        new XAttribute("sc", point.StartColumn), new XAttribute("el", point.EndLine), new XAttribute("ec", point.EndColumn),
                        new XAttribute("fileid", documents[point.Document]))))))))));
        var xml = new XElement("CoverageSession", new XElement("Modules", new XElement("Module",
            new XElement("ModuleName", evidence.ModuleIdentity), new XElement("Files", documents.Select(item =>
                new XElement("File", new XAttribute("uid", item.Value), new XAttribute("fullPath", item.Key)))), classes)));
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, xml.ToString(), TestContext.Current.CancellationToken);
        return path;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CommittedTrustedExemptionsSurviveEditingAnotherSourceInTheSameProject(bool duplicateAnonymous, bool nestedFamily)
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
        if (nestedFamily)
            code = original.Replace("var value = 1;", "var value = ((System.Func<System.Threading.Tasks.Task<int>>)(async () =>" + newline +
                "            await System.Threading.Tasks.Task.FromResult(1)))().GetAwaiter().GetResult();", StringComparison.Ordinal);
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
        fixture.Write("App.Tests/GateTests.cs", duplicateAnonymous || nestedFamily
            ? $"public class GateTests {{ [Xunit.Fact] public void M() {{ Xunit.Assert.Equal({(duplicateAnonymous ? 2 : 1)}, new Crap4CSharp.ProvenanceFixture.CompiledEvidence().M()); }} }}"
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
        Assert.Equal(nestedFamily ? 2 : 1, unsupported.Length);
        if (nestedFamily)
            Assert.Contains(unsupported, group => group.First().Kind == "Lambda");
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

        if (!duplicateAnonymous)
        {
            // Unknown coverage still proves CC > threshold, but cannot become numeric debt.
            fixture.Write("policy.json", Policy(["exemptions.json"]).Replace("\"threshold\":100", "\"threshold\":0", StringComparison.Ordinal));
            var (candidateLocator, _, _, _, _) = await CreateBundle(fixture.Path, projectLogical, "policy.json",
                assemblyPath, Path.Combine(fixture.Path, "candidate-bundle"),
                coveragePath: Directory.EnumerateFiles(Path.Combine(fixture.Path, "current-coverage"),
                    "coverage.opencover.xml", SearchOption.AllDirectories).Single());
            var candidateOutput = new StringWriter();
            var candidateError = new StringWriter();
            var candidateExit = await global::App.RunAsync(["baseline", "create", "--policy", "policy.json",
                "--reuse-artifacts", candidateLocator, "--output", "candidate.json", "--format", "json"],
                fixture.Path, candidateOutput, candidateError, TestContext.Current.CancellationToken);
            Assert.True(candidateExit == 2, candidateError + Environment.NewLine + candidateOutput);
            using var summary = JsonDocument.Parse(candidateOutput.ToString());
            var omitted = summary.RootElement.GetProperty("omittedKnownViolations").EnumerateArray().ToArray();
            Assert.Contains(omitted, item => item.GetProperty("kind").GetString() == (nestedFamily ? "family" : "method"));
            Assert.All(omitted, item => Assert.Equal("baseline.unknownCoverageCannotReceiveAllowance",
                item.GetProperty("reason").GetString()));
            var candidate = BaselineDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture.Path,
                "candidate.json"), TestContext.Current.CancellationToken));
            Assert.All(omitted, item => Assert.DoesNotContain(candidate.Entries,
                entry => entry.EntityKey == item.GetProperty("entityKey").GetString()));
        }
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
        string bundleDirectory, string? baseRevision = null, string? coveragePath = null, string[]? scopedSources = null)
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
            sources = scopedSources ?? inputs.Where(item => !item.Generated)
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
