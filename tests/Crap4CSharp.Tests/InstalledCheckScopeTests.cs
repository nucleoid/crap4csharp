using System.Reflection;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

// Installed counterparts for changed and empty base scopes.
public sealed partial class InstalledCheckTests
{
    [Theory]
    [InlineData("committed-change")]
    [InlineData("uncommitted-change")]
    [InlineData("clean-empty")]
    public async Task ExactInstalledExecutableChecksChangedAndEmptyBaseScopes(string scenario)
    {
        using var installation = TestDirectory.Create("crap4csharp-installed-scopes");
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
        AssertInstalledToolSelection(tools, packagePath, version, suppliedPackage is not null);
        var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? "crap4csharp.exe" : "crap4csharp");
        Assert.True(File.Exists(executable));

        using var consumer = TestDirectory.Create("crap4csharp-installed-base-scope");
        var sources = CreateConsumer(consumer, "pass");
        File.WriteAllText(sources["policy.json"], File.ReadAllText(sources["policy.json"])
            .Replace("\"mode\":\"strict\"", "\"mode\":\"incremental\",\"baseline\":\"baseline.json\"", StringComparison.Ordinal)
            .Replace("\"scope\":\"all\"", "\"scope\":\"base\"", StringComparison.Ordinal));
        File.WriteAllText(sources["Library/Library.csproj"], File.ReadAllText(sources["Library/Library.csproj"])
            .Replace("</PropertyGroup>", "<DefineConstants>$(DefineConstants);INSTALLED_SELECTED</DefineConstants></PropertyGroup>", StringComparison.Ordinal));
        static string SelectedSource(string body) => "#if INSTALLED_SELECTED\npublic static class Library { public static int Value(int n) => " + body + "; }\n#else\npublic static class Library { public static int WrongSymbol() => 0; }\n#endif\n";
        File.WriteAllText(sources["Library/Code.cs"], SelectedSource("42"));
        var parsedPolicy = RepositoryPolicyParser.Parse(Encoding.UTF8.GetBytes(File.ReadAllText(sources["policy.json"])), "policy.json");
        var compatibility = TrustedPolicyLoader.BoundCompatibilityHash(parsedPolicy, new Dictionary<string, byte[]>());
        sources.Add("baseline.json", Path.Combine(consumer.Path, "baseline.json"));
        // An empty initial adoption fixture, committed with its policy before
        // any tested change. Never regenerate it from the tested result.
        File.WriteAllBytes(sources["baseline.json"], BaselineDocument.Serialize(new BaselineDocument(
            BaselineDocument.Version, ComplexityRules.CallablesV1, compatibility, new string('a', 64), "fixture-adoption", [])));
        foreach (var arguments in new[] { new[] { "init", "-q" }, new[] { "config", "user.name", "Fixture" },
            new[] { "config", "user.email", "fixture@example.invalid" }, new[] { "config", "core.autocrlf", "false" },
            new[] { "add", "." }, new[] { "commit", "-qm", "reviewed scope fixture" } })
            Success(await Run("git", arguments, consumer.Path), "scope fixture Git initialization");
        var reviewed = await Run("git", ["rev-parse", "HEAD"], consumer.Path);
        Success(reviewed, "reviewed immutable base");
        var baseCommit = reviewed.Output.Trim();
        if (scenario != "clean-empty")
            File.WriteAllText(sources["Library/Code.cs"], SelectedSource("n > 0 ? n : 42"));
        if (scenario == "committed-change")
        {
            Success(await Run("git", ["add", "Library/Code.cs"], consumer.Path), "stage committed consumer change");
            Success(await Run("git", ["commit", "-qm", "changed selected callable"], consumer.Path), "commit consumer change");
        }
        var hashes = sources.ToDictionary(p => p.Key, p => ProjectAnalysisContext.ContentHash(File.ReadAllBytes(p.Value)));
        var before = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, baseCommit),
            consumer.Path, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        Assert.True(before.Completeness == ScopeCompleteness.Complete, string.Join(Environment.NewLine, before.Diagnostics));
        if (scenario == "clean-empty") Assert.Empty(before.Files);
        else Assert.Contains(before.Files, file => file.NewPath == "Library/Code.cs");
        if (scenario == "committed-change") Assert.NotEqual(baseCommit, before.Revision.CurrentHead);
        else Assert.Equal(baseCommit, before.Revision.CurrentHead);
        string[] argumentsForCheck = ["check", "--project", "Tests/Tests.csproj", "--policy", "policy.json",
            "--base", baseCommit, "--scope", "base", "--configuration", "Debug", "--framework", "net10.0",
            "--format", "json", "--artifact-output", "bundle", "--timeout-seconds", "300"];
        var process = await Run(executable, argumentsForCheck, consumer.Path);
        using var terminal = Terminal(process, 0, repository, version);
        Assert.Equal(exactToolVersion, terminal.RootElement.GetProperty("toolVersion").GetString());
        Assert.All(sources, pair => Assert.Equal(hashes[pair.Key], ProjectAnalysisContext.ContentHash(File.ReadAllBytes(pair.Value))));
        var evaluation = terminal.RootElement.GetProperty("evaluation");
        var checks = evaluation.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "testExecution" && check.GetProperty("status").GetString() == "pass");
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "coverage" && check.GetProperty("status").GetString() == "pass");
        if (scenario == "clean-empty")
            Assert.Contains(checks, check => check.GetProperty("name").GetString() == "crap" &&
                check.GetProperty("status").GetString() == "notApplicable" && check.GetProperty("reason").GetString() == "crap.noApplicableChangedCallables");
        else
            Assert.Contains(evaluation.GetProperty("metrics").EnumerateArray(), metric =>
                metric.GetProperty("signature").GetString()!.Contains("::Value(System.Int32)", StringComparison.Ordinal) && metric.GetProperty("coverage").GetDouble() == 1);
        foreach (var path in new[] { "Library/build-count.txt", "Tests/build-count.txt" })
            Assert.Single(File.ReadAllLines(Path.Combine(consumer.Path, path)));
        Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(consumer.Path, "Tests/bin"), "test-count.txt", SearchOption.AllDirectories))));
        var manifest = Assert.Single(terminal.RootElement.GetProperty("run").GetProperty("artifacts").EnumerateArray(), a => a.GetProperty("kind").GetString() == "manifest");
        var bundle = ArtifactBundle.Load(manifest.GetProperty("path").GetString()!, consumer.Path);
        var production = Assert.Single(bundle.Manifest.Contexts);
        Assert.Equal("net10.0", production.TargetFramework);
        Assert.Equal("Debug", production.Configuration);
        Assert.Equal("AnyCPU", production.Platform);
        Assert.Contains("INSTALLED_SELECTED", production.ParseOptions!.PreprocessorSymbols);
        Assert.Equal(baseCommit, evaluation.GetProperty("policyTrust").GetProperty("revision").GetString());
        var after = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, baseCommit),
            consumer.Path, TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        Assert.Equal(before.Revision.CurrentHead, after.Revision.CurrentHead);
        Assert.Equal(before.Revision.IndexIdentity, after.Revision.IndexIdentity);
        Assert.Equal(ScopeCompleteness.Complete, after.Completeness);
        Assert.Equal(before.Files.Select(f => (f.NewPath, f.OldPath, f.Kind, f.OldIdentity, f.NewIdentity)),
            after.Files.Select(f => (f.NewPath, f.OldPath, f.Kind, f.OldIdentity, f.NewIdentity)));
        var evidence = TestValidationOutput.CreateDirectory("installed-base-scope", installation.Path);
        File.WriteAllText(Path.Combine(evidence, scenario + ".json"), JsonSerializer.Serialize(new
        {
            scenario, packageVersion = version, packageSha256, candidateHead = repositoryHead.Output.Trim(), baseCommit,
            processExit = process.ExitCode, sourceChanged = false, configuration = production.Configuration,
            framework = production.TargetFramework, platform = production.Platform,
            terminalSha256 = ProjectAnalysisContext.ContentHash(Encoding.UTF8.GetBytes(process.Output)),
            qualification = "Installed exact candidate scope/symbol/framework counterpart; not original180 or whole-suite acceptance."
        }));
    }
}
