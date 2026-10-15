using System.Reflection;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class InstalledCheckTests
{
    [Theory]
    [InlineData("compiler.outputsUnproven", "completed", 0, "INCOMPLETE")]
    [InlineData("context.metadataUnavailable", "completed", 0, "INCOMPLETE")]
    [InlineData("coverage.unavailable", "completed", 0, "INCOMPLETE")]
    [InlineData("provenance.stale", "completed", 0, "INCOMPLETE")]
    [InlineData("test.runnerUnsupported", "completed", 0, "INCOMPLETE")]
    [InlineData("tests.incomplete", "failed", null, "INCOMPLETE")]
    [InlineData("run.cancelled", "interrupted", null, "INCOMPLETE")]
    [InlineData("policy.invalid", "completed", 0, "ERROR")]
    [InlineData("arguments.invalid", "completed", 0, "ERROR")]
    [InlineData("unexpected.fixture", "completed", 0, "ERROR")]
    [InlineData("compiler.outputsUnproven", "failed", 1, "FAIL")]
    [InlineData("policy.invalid", "failed", 1, "FAIL")]
    public void DeliveryForgeReasonContractPreservesFailureAndDistinguishesIncompleteEvidence(
        string reason, string state, int? testExit, string expected)
    {
        using var terminal = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = ResultContract.SchemaVersion, toolVersion = "pinned-fixture",
            evaluation = new { invocationMode = "check", checks = new[] { new { reason } } },
            run = new { exitCode = 1, cancellation = new { cancelled = false, timedOut = false },
                testOutcomes = new[] { new { status = state, exitCode = testExit } } }
        }));
        Assert.Equal(expected, NormalizeDeliveryForgeGate(terminal.RootElement, 1, "pinned-fixture", "fixture-base"));
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        Assert.Contains("delivery-forge-reason-map.json", File.ReadAllText(Path.Combine(repository, "docs", "agent-integration.md")));
    }

    [Fact]
    public async Task ExactInstalledExecutableRunsModernCheckReplayReuseAndBaseline()
    {
        using var installation = TestDirectory.Create("crap4csharp-installed-modern");
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var configuration = typeof(InstalledCheckTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var suppliedPackage = Environment.GetEnvironmentVariable("CRAP4CSHARP_CANDIDATE_PACKAGE");
        var suppliedVersion = Environment.GetEnvironmentVariable("CRAP4CSHARP_CANDIDATE_VERSION");
        Assert.Equal(string.IsNullOrWhiteSpace(suppliedPackage), string.IsNullOrWhiteSpace(suppliedVersion));
        if (string.IsNullOrWhiteSpace(suppliedPackage)) suppliedPackage = null;
        if (string.IsNullOrWhiteSpace(suppliedVersion)) suppliedVersion = null;
        var version = suppliedVersion ?? "0.1.0-handoff.g" + Guid.NewGuid().ToString("N");
        var repositoryHead = await Run("git", ["rev-parse", "HEAD"], repository);
        Success(repositoryHead, "candidate source revision");
        var exactToolVersion = version + "+" + repositoryHead.Output.Trim();
        var receiptPaths = new List<string>();
        var receiptDirectory = TestValidationOutput.CreateDirectory("delivery-forge-demo", installation.Path);
        var feed = Path.Combine(installation.Path, "feed");
        var artifacts = Path.Combine(installation.Path, "compiler-artifacts");
        var tools = Path.Combine(installation.Path, "tools");
        Directory.CreateDirectory(feed);
        var project = Path.Combine(repository, "src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj");
        var properties = new[] { "-p:UseArtifactsOutput=true", "-p:ArtifactsPath=" + artifacts, "-p:Version=" + version };
        if (suppliedPackage is null)
        {
            // Isolated outputs prevent changing assemblies loaded by this test process.
            Success(await Run("dotnet", ["restore", project, "--disable-parallel", "-m:1", "-nr:false", .. properties], repository), "isolated candidate restore");
            Success(await Run("dotnet", ["pack", project, "-c", configuration, "--no-restore", "-nr:false", "-m:1", "-o", feed, .. properties], repository), "isolated candidate pack");
        }
        else
        {
            // Release preparation verifies these exact immutable package bytes on
            // both operating systems; never rebuild the supplied candidate.
            Assert.True(File.Exists(suppliedPackage));
            File.Copy(suppliedPackage, Path.Combine(feed, Path.GetFileName(suppliedPackage)));
        }
        using (var package = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(feed, "*.nupkg"))))
        {
            foreach (var name in new[] { "README.md", "LICENSE", "ATTRIBUTION.md" })
            {
                using var content = package.GetEntry(name)!.Open();
                using var reader = new StreamReader(content);
                // One immutable package is exercised on both operating systems.
                // Git checkout line endings may differ from the packing checkout;
                // require identical document content after only newline normalization.
                Assert.Equal(File.ReadAllText(Path.Combine(repository, name)).ReplaceLineEndings("\n"),
                    reader.ReadToEnd().ReplaceLineEndings("\n"));
            }
            using var spec = Assert.Single(package.Entries, e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var metadata = System.Xml.Linq.XDocument.Load(spec).Descendants().Single(e => e.Name.LocalName == "metadata");
            Assert.Equal("Crap4CSharp.Tool", metadata.Elements().Single(e => e.Name.LocalName == "id").Value);
            Assert.Equal(version, metadata.Elements().Single(e => e.Name.LocalName == "version").Value);
            var license = metadata.Elements().Single(e => e.Name.LocalName == "license");
            Assert.Equal("expression", license.Attribute("type")!.Value);
            Assert.Equal("MIT", license.Value);
            var groups = suppliedPackage is null ? new[] { "any" } : new[] { "any", "win-x64", "linux-x64" };
            foreach (var group in groups)
                foreach (var name in new[] { "Crap4CSharp.Core.dll", "Crap4CSharp.Tool.dll", "DotnetToolSettings.xml" })
                    Assert.Contains(package.Entries, e => e.FullName == $"tools/net10.0/{group}/{name}");
            Assert.DoesNotContain(package.Entries, e => e.FullName.StartsWith("tools/", StringComparison.Ordinal) &&
                !groups.Any(group => e.FullName.StartsWith($"tools/net10.0/{group}/", StringComparison.Ordinal)));
        }
        var packagePath = Assert.Single(Directory.GetFiles(feed, "*.nupkg"));
        var packageSha256 = ProjectAnalysisContext.ContentHash(File.ReadAllBytes(packagePath));
        var config = installation.Write("NuGet.Config", "<configuration><packageSources><clear /><add key=\"candidate\" value=\"" +
            SecurityElement.Escape(feed) + "\" /></packageSources></configuration>");
        var environment = new Dictionary<string, string>
        {
            // Local-tool resolver entries include absolute package paths. Keep
            // them with this installation so repeated exact-version acceptance
            // cannot reuse a prior fixture's disposed package directory.
            ["DOTNET_CLI_HOME"] = Path.Combine(installation.Path, "cli-home"),
            ["NUGET_PACKAGES"] = Path.Combine(installation.Path, "package-cache"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(installation.Path, "http-cache")
        };
        Success(await Run("dotnet", ["tool", "install", "Crap4CSharp.Tool", "--version", version,
            "--tool-path", tools, "--configfile", config, "--no-cache"], repository, environment), "exact candidate install");
        var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? "crap4csharp.exe" : "crap4csharp");
        Assert.True(File.Exists(executable));
        AssertInstalledToolSelection(tools, packagePath, version, suppliedPackage is not null);

        foreach (var scenario in new[] { "pass", "test-failure", "metric-violation", "operational-failure" })
        {
            using var consumer = TestDirectory.Create("crap4csharp-installed-consumer");
            var sources = CreateConsumer(consumer, scenario);
            foreach (var arguments in new[] { new[] { "init", "-q" }, new[] { "config", "user.name", "Fixture" },
                new[] { "config", "user.email", "fixture@example.invalid" }, new[] { "add", "." }, new[] { "commit", "-qm", "reviewed fixture" } })
                Success(await Run("git", arguments, consumer.Path), "fixture Git initialization");
            File.WriteAllText(sources["Library/Code.cs"], "public static class Library { public static int Value(int n) => n > 0 ? n : 42; }");
            var hashes = sources.ToDictionary(p => p.Key, p => ProjectAnalysisContext.ContentHash(File.ReadAllBytes(p.Value)));
            var help = await Run(executable, ["--help"], consumer.Path);
            Success(help, "installed help");
            Assert.Contains("cleanup --private-diagnostics <exact-root>", help.Output);
            if (scenario == "pass")
            {
                var cleanup = await Run(executable, ["cleanup", "--private-diagnostics", consumer.Path], consumer.Path);
                Assert.Equal(1, cleanup.ExitCode);
                Assert.True(Directory.Exists(consumer.Path));
                Assert.False(File.Exists(Path.Combine(consumer.Path, PrivateDiagnosticLease.MarkerName)));
                AssertInputs();
                var unmarked = Directory.CreateTempSubdirectory(PrivateDiagnosticLease.Prefix).FullName;
                try
                {
                    var retained = Path.Combine(unmarked, "evidence.txt");
                    File.WriteAllText(retained, "unmarked private fixture evidence");
                    var refusal = await Run(executable, ["cleanup", "--private-diagnostics", unmarked], consumer.Path);
                    Assert.Equal(1, refusal.ExitCode);
                    Assert.Equal("unmarked private fixture evidence", File.ReadAllText(retained));
                    AssertInputs();
                }
                finally { if (Directory.Exists(unmarked)) Directory.Delete(unmarked, recursive: true); }
            }
            AssertInputs();
            Assert.Empty(Directory.GetDirectories(consumer.Path, "obj", SearchOption.AllDirectories));
            var beforeScope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, "HEAD"),
                consumer.Path, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
            var consumerTree = await Run("git", ["rev-parse", "HEAD^{tree}"], consumer.Path);
            Success(consumerTree, "consumer tree identity");
            string[] freshArguments = ["check", "--project", "Tests/Tests.csproj", "--policy",
                scenario == "operational-failure" ? "missing-policy.json" : "policy.json",
                "--base", "HEAD", "--scope", "all", "--format", "json", "--artifact-output", "bundle", "--timeout-seconds", "300"];
            var fresh = await Run(executable, freshArguments, consumer.Path);
            var afterScope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, "HEAD"),
                consumer.Path, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
            var diagnosticRoot = receiptDirectory;
            File.WriteAllText(Path.Combine(diagnosticRoot, "installed-" + scenario + "-scope.json"),
                JsonSerializer.Serialize(new { beforeScope, afterScope }));
            var expected = scenario == "pass" ? 0 : scenario == "metric-violation" ? 2 : 1;
            using var document = Terminal(fresh, expected, repository, version);
            AssertInputs();
            var normalized = NormalizeDeliveryForgeGate(document.RootElement, fresh.ExitCode, exactToolVersion,
                beforeScope.Revision.CurrentHead!);
            Assert.Equal(scenario == "pass" ? "PASS" : scenario == "operational-failure" ? "ERROR" : "FAIL", normalized);
            if (scenario == "pass")
            {
                var missingOutcomes = System.Text.Json.Nodes.JsonNode.Parse(fresh.Output)!;
                missingOutcomes["run"]!.AsObject().Remove("testOutcomes");
                using var missing = JsonDocument.Parse(missingOutcomes.ToJsonString());
                Assert.Equal("INCOMPLETE", NormalizeDeliveryForgeGate(missing.RootElement, 0, exactToolVersion,
                    beforeScope.Revision.CurrentHead!));
            }
            var receiptPath = Path.Combine(receiptDirectory, scenario + ".json");
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
            {
                gateId = "crap4csharp-post-task", scenario, packageVersion = version, packageSha256,
                toolVersion = exactToolVersion, candidateHead = repositoryHead.Output.Trim(),
                reviewedBase = beforeScope.Revision.ResolvedBase, consumerHead = beforeScope.Revision.CurrentHead,
                consumerTree = consumerTree.Output.Trim(), configuration = "Debug", scope = "all",
                command = freshArguments, processExit = fresh.ExitCode, outcome = normalized,
                terminalSha256 = ProjectAnalysisContext.ContentHash(Encoding.UTF8.GetBytes(fresh.Output)),
                inputHashes = hashes, sourceChangedDuringInvocation = false,
                reason = document.RootElement.GetProperty("evaluation").GetProperty("decision").GetProperty("reason").GetString(),
                artifacts = document.RootElement.GetProperty("run").GetProperty("artifacts"),
                qualification = "Individual installed invocation; full package/manifest matrix acceptance requires completed-index.json. Not a Delivery Forge host-profile certification."
            }));
            receiptPaths.Add(receiptPath);
            if (scenario == "operational-failure")
            {
                Assert.Contains(document.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
                    check => check.GetProperty("status").GetString() == "operationalError" &&
                        check.GetProperty("reason").GetString()!.StartsWith("policy.", StringComparison.Ordinal));
                Assert.Empty(Directory.GetFiles(consumer.Path, "build-count.txt", SearchOption.AllDirectories));
                Assert.Empty(Directory.GetDirectories(consumer.Path, "obj", SearchOption.AllDirectories));
                Assert.Empty(document.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray());
                continue;
            }
            AssertOnce();
            if (scenario == "test-failure")
            {
                Assert.Contains(document.RootElement.GetProperty("run").GetProperty("testOutcomes").EnumerateArray(),
                    c => c.GetProperty("status").GetString() == "failed");
                Assert.DoesNotContain(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                    a => a.GetProperty("complete").GetBoolean() && a.GetProperty("reusable").GetBoolean());
                continue;
            }
            var manifest = Assert.Single(document.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(),
                a => a.GetProperty("kind").GetString() == "manifest");
            Assert.True(manifest.GetProperty("complete").GetBoolean());
            Assert.True(manifest.GetProperty("reusable").GetBoolean());
            if (scenario == "metric-violation") continue;
            var locator = manifest.GetProperty("path").GetString()!;
            using var replay = Terminal(await Run(executable, ["analyze", "--reuse-artifacts", locator, "--format", "json"], consumer.Path),
                0, repository, version);
            Assert.Equal(document.RootElement.GetProperty("evaluation").GetProperty("metrics").GetRawText(),
                replay.RootElement.GetProperty("evaluation").GetProperty("metrics").GetRawText());
            using var reused = Terminal(await Run(executable, ["check", "--reuse-artifacts", locator, "--policy", "policy.json",
                "--base", "HEAD", "--format", "json"], consumer.Path), expected, repository, version);
            Assert.Equal(document.RootElement.GetProperty("evaluation").GetProperty("metrics").GetRawText(),
                reused.RootElement.GetProperty("evaluation").GetProperty("metrics").GetRawText());
            Assert.Equal("INCOMPLETE", NormalizeDeliveryForgeGate(replay.RootElement, 0, exactToolVersion,
                beforeScope.Revision.CurrentHead!));
            Assert.Equal("PASS", NormalizeDeliveryForgeGate(reused.RootElement, expected, exactToolVersion,
                beforeScope.Revision.CurrentHead!, currentReuse: true));
            AssertOnce(); AssertInputs();
            if (scenario == "pass")
            {
                Success(await Run(executable, ["baseline", "create", "--reuse-artifacts", locator, "--policy", "policy.json",
                    "--output", "baseline-candidate.json"], consumer.Path), "installed baseline candidate");
                Assert.True(File.Exists(Path.Combine(consumer.Path, "baseline-candidate.json")));
                AssertOnce(); AssertInputs();
                File.AppendAllText(sources["Library/Code.cs"], "\n// Current source drift must invalidate reusable evidence.\n");
                using var stale = Terminal(await Run(executable, ["check", "--reuse-artifacts", locator, "--policy", "policy.json",
                    "--base", "HEAD", "--format", "json"], consumer.Path), 1, repository, version);
                Assert.Equal("INCOMPLETE", NormalizeDeliveryForgeGate(stale.RootElement, 1, exactToolVersion,
                    beforeScope.Revision.CurrentHead!, currentReuse: true));
                AssertOnce();
            }

            void AssertInputs()
            {
                foreach (var pair in sources)
                    Assert.Equal(hashes[pair.Key], ProjectAnalysisContext.ContentHash(File.ReadAllBytes(pair.Value)));
            }
            void AssertOnce()
            {
                foreach (var path in new[] { "Library/build-count.txt", "Tests/build-count.txt" })
                    Assert.Single(File.ReadAllLines(Path.Combine(consumer.Path, path)));
                var count = Assert.Single(Directory.GetFiles(Path.Combine(consumer.Path, "Tests/bin"), "test-count.txt", SearchOption.AllDirectories));
                Assert.Single(File.ReadAllLines(count));
            }
        }
        await VerifyManifestConsumerAsync(repository, feed, config, environment, version);
        Assert.Equal(4, receiptPaths.Count);
        File.WriteAllText(Path.Combine(receiptDirectory, "completed-index.json"), JsonSerializer.Serialize(new
        { status = "completed", packageVersion = version, packageSha256, receipts = receiptPaths,
            qualification = "Installed representative-consumer normalization demonstration; not whole committed-head/hosted CI or Delivery Forge host certification." }));
    }

    private static void AssertInstalledToolSelection(string tools, string packagePath, string version, bool native)
    {
        var store = Path.Combine(tools, ".store");
        var assetsPath = Assert.Single(Directory.GetFiles(store, "project.assets.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(store, "project.assets.ridpackage.json", SearchOption.AllDirectories));
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var target = Assert.Single(assets.RootElement.GetProperty("targets").EnumerateObject(), p => p.Name.Contains('/'));
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        Assert.Equal("net10.0/" + rid, target.Name);
        var library = Assert.Single(target.Value.EnumerateObject());
        Assert.Equal("Crap4CSharp.Tool/" + version, library.Name, ignoreCase: true);
        var group = native ? rid : "any";
        var prefix = "tools/net10.0/" + group + "/";
        var selected = library.Value.GetProperty("tools").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.NotEmpty(selected);
        Assert.All(selected, name =>
        {
            Assert.StartsWith(prefix, name, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
            Assert.DoesNotContain(name.Split('/'), segment => segment is "." or "..");
        });
        // SDK103 persists a minimal tool lockfile. Its .tools keys prove selection;
        // libraries/files would only inventory package contents and may be empty.
        var installed = Path.Combine(Path.GetDirectoryName(assetsPath)!, "crap4csharp.tool", version);
        using var package = ZipFile.OpenRead(packagePath);
        var expected = package.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/'))
            .Select(e => e.FullName).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), selected.Order(StringComparer.Ordinal));
        foreach (var name in selected)
        {
            using var packed = package.GetEntry(name)!.Open();
            using var actual = File.OpenRead(Path.Combine(installed, name.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(packed), System.Security.Cryptography.SHA256.HashData(actual));
        }
        if (native)
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(
                Path.Combine(installed, prefix.Replace('/', Path.DirectorySeparatorChar), "Crap4CSharp.Tool.dll")));
            Assert.True(pe.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size > 0);
            Assert.Equal(OperatingSystem.IsWindows() ? 0x8664 : 0xfd1d, (int)pe.PEHeaders.CoffHeader.Machine);
        }
    }

    private static async Task VerifyManifestConsumerAsync(string repository, string feed, string toolConfig,
        IReadOnlyDictionary<string, string> environment, string version)
    {
        using var consumer = TestDirectory.Create("crap4csharp-manifest-consumer");
        var template = Path.Combine(repository, "samples", "LocalToolConsumer");
        foreach (var source in Directory.GetFiles(template, "*", SearchOption.AllDirectories))
            consumer.Write(Path.GetRelativePath(template, source), File.ReadAllText(source));
        var manifest = Path.Combine(consumer.Path, ".config", "dotnet-tools.json");
        var toolManifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
        toolManifest["tools"]!["crap4csharp.tool"]!["version"] = version;
        File.WriteAllText(manifest, toolManifest.ToJsonString());
        // The manifest restore uses only the isolated exact-candidate feed. Test
        // dependencies remain ordinary reviewed sample setup through NuGet.org.
        Success(await Run("dotnet", ["tool", "restore", "--configfile", toolConfig, "--no-http-cache", "--disable-parallel"], consumer.Path, environment),
            "manifest exact candidate restore");
        var sampleConfig = Path.Combine(consumer.Path, "nuget.config");
        var configXml = System.Xml.Linq.XDocument.Load(sampleConfig);
        configXml.Descendants("add").Single(element => element.Attribute("key")?.Value == "candidate")
            .SetAttributeValue("value", feed);
        configXml.Save(sampleConfig);
        Success(await Run("dotnet", ["restore", "Consumer.slnx", "--disable-parallel", "-m:1", "-nr:false"], consumer.Path), "independent consumer restore");
        consumer.Write(".gitignore", "bin/\nobj/\nbundle/\n");
        foreach (var arguments in new[] { new[] { "init", "-q" }, new[] { "config", "user.name", "Fixture" },
            new[] { "config", "user.email", "fixture@example.invalid" }, new[] { "add", "." }, new[] { "commit", "-qm", "reviewed manifest consumer" } })
            Success(await Run("git", arguments, consumer.Path), "manifest consumer Git setup");
        var reviewed = await Run("git", ["rev-parse", "HEAD"], consumer.Path);
        Success(reviewed, "manifest consumer reviewed base");
        var authored = Directory.GetFiles(consumer.Path, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(consumer.Path, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is ".git" or "bin" or "obj"))
            .ToDictionary(path => path, path => ProjectAnalysisContext.ContentHash(File.ReadAllBytes(path)));
        Success(await Run("dotnet", ["tool", "run", "crap4csharp", "--", "--help"], consumer.Path, environment), "manifest tool help");
        using var result = Terminal(await Run("dotnet", ["tool", "run", "crap4csharp", "--", "check",
            "--project", "Consumer.slnx", "--policy", "crap4csharp.policy.json", "--base", reviewed.Output.Trim(),
            "--scope", "all", "--format", "json", "--artifact-output", "bundle", "--timeout-seconds", "300"], consumer.Path, environment),
            0, repository, version);
        Assert.Contains(result.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray(),
            metric => metric.GetProperty("signature").GetString()!.Contains("::IsNonNegative(System.Int32)", StringComparison.Ordinal) &&
                metric.GetProperty("coverage").GetDouble() == 1);
        Assert.Contains(result.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "testExecution" && check.GetProperty("status").GetString() == "pass");
        Assert.All(authored, pair => Assert.Equal(pair.Value, ProjectAnalysisContext.ContentHash(File.ReadAllBytes(pair.Key))));
        Assert.DoesNotContain(Directory.GetFiles(consumer.Path, "*.csproj", SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("Crap4CSharp.Core", StringComparison.Ordinal) ||
                File.ReadAllText(path).Contains("Crap4CSharp.Tool", StringComparison.Ordinal));
    }

    private static Dictionary<string, string> CreateConsumer(TestDirectory fixture, string scenario)
    {
        var files = new Dictionary<string, string>();
        void Write(string path, string contents) => files.Add(path, fixture.Write(path, contents));
        Write("global.json", "{\"sdk\":{\"version\":\"10.0.103\",\"rollForward\":\"disable\"}}");
        Write(".gitignore", "bin/\nobj/\nbuild-count.txt\nbundle/\nbaseline-candidate.json\n");
        Write("Directory.Build.targets", """
            <Project><Target Name="CountActualCompile" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="once" Overwrite="false" /></Target></Project>
            """);
        Write("Library/Library.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("Library/Code.cs", "public static class Library { public static int Value(int n) => 42; }");
        Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Library/Library.csproj" /></ItemGroup></Project>
            """);
        Write("Tests/Tests.cs", "using Xunit; public class ActualTests { [Fact] public void Test() { " +
            "System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"test-count.txt\"), \"once\\n\"); " +
            "Assert.Equal(" + (scenario == "test-failure" ? "99" : "42") + ", Library.Value(42)); Assert.Equal(42, Library.Value(0)); } }");
        Write("policy.json", """
            {"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],
            "testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"all",
            "threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}
            """.Replace("\"threshold\":8", scenario == "metric-violation" ? "\"threshold\":0.5" : "\"threshold\":8", StringComparison.Ordinal));
        return files;
    }

    private sealed record InstalledProcess(int ExitCode, string Output, string Error);

    private static JsonDocument Terminal(InstalledProcess process, int expected, string repository, string version)
    {
        Assert.True(process.ExitCode == expected, $"Expected {expected}, actual {process.ExitCode}. stdout: {process.Output}\nstderr: {process.Error}");
        ResultContractTests.AssertConformsToPublishedSchema(repository, process.Output);
        var document = JsonDocument.Parse(process.Output);
        Assert.Equal(expected, document.RootElement.GetProperty("run").GetProperty("exitCode").GetInt32());
        Assert.StartsWith(version, document.RootElement.GetProperty("toolVersion").GetString());
        return document;
    }



    private static string NormalizeDeliveryForgeGate(JsonElement terminal, int exitCode,
        string exactToolVersion, string reviewedRevision, bool currentReuse = false)
    {
        if (terminal.GetProperty("schemaVersion").GetString() != ResultContract.SchemaVersion ||
            terminal.GetProperty("toolVersion").GetString() != exactToolVersion ||
            terminal.GetProperty("run").GetProperty("exitCode").GetInt32() != exitCode)
            return "ERROR";
        var evaluation = terminal.GetProperty("evaluation");
        var run = terminal.GetProperty("run");
        if (evaluation.GetProperty("invocationMode").GetString() != "check") return "INCOMPLETE";
        var checks = evaluation.GetProperty("checks").EnumerateArray().ToArray();
        var outcomes = run.TryGetProperty("testOutcomes", out var tests)
            ? tests.EnumerateArray().ToArray() : [];
        // Completed real test failure remains primary over partial-analysis errors.
        if (exitCode == 1 && outcomes.Any(test => test.GetProperty("status").GetString() == "failed" &&
            test.TryGetProperty("exitCode", out var observedExit) && observedExit.ValueKind == JsonValueKind.Number &&
            observedExit.GetInt32() != 0))
            return "FAIL";
        if (exitCode == 1)
        {
            if (outcomes.Any(test => test.GetProperty("status").GetString() == "interrupted" ||
                test.GetProperty("status").GetString() == "failed" &&
                (!test.TryGetProperty("exitCode", out var observedExit) || observedExit.ValueKind == JsonValueKind.Null)))
                return "INCOMPLETE";
            var reasons = checks.Select(check => check.GetProperty("reason").GetString() ?? "").ToArray();
            using var reasonMap = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                Path.GetFullPath("../../../../../", AppContext.BaseDirectory), "docs", "delivery-forge-reason-map.json")));
            var map = reasonMap.RootElement;
            if (map.GetProperty("schemaVersion").GetString() != "delivery-forge-reason-map-v1") return "ERROR";
            bool StartsWithAny(string reason, string group) => map.GetProperty(group).EnumerateArray()
                .Any(prefix => reason.StartsWith(prefix.GetString()!, StringComparison.Ordinal));
            if (reasons.Any(reason => StartsWithAny(reason, "errorPrefixes"))) return "ERROR";
            if (run.GetProperty("cancellation").GetProperty("cancelled").GetBoolean() ||
                run.GetProperty("cancellation").GetProperty("timedOut").GetBoolean() ||
                reasons.Any(reason => StartsWithAny(reason, "incompletePrefixes") ||
                    map.GetProperty("incompleteReasons").EnumerateArray().Any(item => item.GetString() == reason) ||
                    reason.Contains(map.GetProperty("unsupportedFragment").GetString()!, StringComparison.OrdinalIgnoreCase))) return "INCOMPLETE";
            return map.GetProperty("unknownOutcome").GetString()!;
        }
        if (exitCode is not (0 or 2)) return "ERROR";
        if (!evaluation.GetProperty("decision").GetProperty("completed").GetBoolean() ||
            run.GetProperty("status").GetString() != "completed" ||
            !evaluation.TryGetProperty("provenance", out var provenance) ||
            provenance.GetProperty("status").GetString() != "verified" ||
            provenance.GetProperty("basis").GetString() != "currentWorkspaceMatch" ||
            !provenance.GetProperty("postflightVerified").GetBoolean() ||
            !evaluation.TryGetProperty("policyTrust", out var trust) ||
            trust.GetProperty("trust").GetString() != "base-trusted" ||
            trust.GetProperty("revision").GetString() != reviewedRevision)
            return "INCOMPLETE";
        var testsPassed = checks.Any(check => check.GetProperty("name").GetString() == "testExecution" &&
            check.GetProperty("status").GetString() == "pass");
        if (!testsPassed) return "INCOMPLETE";
        if (currentReuse)
        {
            // Explicit current reuse independently validates saved required-test
            // evidence; it must not manufacture newly launched raw outcomes.
            if (!provenance.GetProperty("reusable").GetBoolean() ||
                !checks.Any(check => check.GetProperty("name").GetString() == "testExecution" &&
                    check.GetProperty("reason").GetString() == "tests.capturedSuccessful")) return "INCOMPLETE";
        }
        else if (outcomes.Length == 0 || outcomes.Any(test => test.GetProperty("status").GetString() != "completed" ||
            test.GetProperty("exitCode").GetInt32() != 0)) return "INCOMPLETE";

        foreach (var check in checks.Where(check => check.GetProperty("required").GetBoolean()))
        {
            if (check.GetProperty("status").GetString() == "pass") continue;
            if (exitCode == 2 && check.GetProperty("name").GetString() == "crap" &&
                check.GetProperty("status").GetString() == "fail") continue;
            var reason = check.GetProperty("reason").GetString();
            var name = check.GetProperty("name").GetString();
            var legitimateNa = name == "crap" && reason is ("crap.noApplicableChangedCallables" or
                "crap.noApplicablePolicyCallables" or "crap.noApplicableSourceInventory") ||
                name == "coverage" && reason == "coverage.noApplicableSourceInventory";
            if (check.GetProperty("status").GetString() == "notApplicable" && legitimateNa) continue;
            return "INCOMPLETE";
        }
        var policyDecision = evaluation.GetProperty("decision").GetProperty("policyDecision").GetString();
        if (exitCode == 2)
            return policyDecision == "fail" && evaluation.GetProperty("findings").EnumerateArray().Any(finding =>
                finding.GetProperty("decision").GetString() == "fail") ? "FAIL" : "ERROR";
        return policyDecision == "pass" ? "PASS" : "INCOMPLETE";
    }

    private static async Task<InstalledProcess> Run(string program, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await ProcessRunner.RunProtocolAsync(program, arguments, directory, TimeSpan.FromMinutes(10),
            TestContext.Current.CancellationToken, maximumBytesPerStream: 4 * 1024 * 1024, environment: environment);
        return new(result.ExitCode, Encoding.UTF8.GetString(result.StandardOutput), Encoding.UTF8.GetString(result.StandardError));
    }

    private static void Success(InstalledProcess process, string operation) =>
        Assert.True(process.ExitCode == 0, $"{operation}: {process.ExitCode}. stdout: {process.Output}\nstderr: {process.Error}");
}
