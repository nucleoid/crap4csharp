using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ArtifactCaptureTests
{
    private const string CompiledSource = "tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs";
    [Fact]
    public void PublicationSealsVerifiesAndNeverOverwritesADestination()
    {
        using var directory = TestDirectory.Create("crap4csharp-publish");
        var fixture = CreateFixture();
        var target = Path.Combine(directory.Path, "bundle");

        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes, target);
        var loaded = ArtifactBundle.Load(locator, directory.Path);

        Assert.NotNull(loaded.Manifest.ManifestHash);
        Assert.Equal(ProvenanceStatus.Captured,
            ProvenanceVerifier.VerifyCapture(loaded.Manifest, loaded.Bytes).Status);
        Assert.Throws<IOException>(() => ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes, target));
    }

    [Fact]
    public void PublicationReservesManifestLocatorBeforeCreatingStaging()
    {
        using var directory = TestDirectory.Create("crap4csharp-reserved-manifest");
        var fixture = CreateFixture();
        var bytes = fixture.Bytes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        bytes["manifest.json"] = ImmutableArray.Create<byte>(1);

        Assert.Throws<InvalidDataException>(() => ArtifactCaptureAdapter.PublishNew(
            fixture.Manifest, bytes, Path.Combine(directory.Path, "bundle")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void SavedInputListCannotMasqueradeAsRevalidatedCurrentContext()
    {
        using var directory = TestDirectory.Create("crap4csharp-current-evidence");
        WriteCompiledSource(directory.Path);
        var fixture = CreateFixture();
        var manifest = ManifestIdentity.Seal(fixture.Manifest with
        {
            Revision = new ManifestRevision("none", "none",
                CanonicalIdentity.Set("local-workspace-v1", [Path.GetFullPath(directory.Path)]), null, null, null),
            ManifestHash = null
        });

        var current = CurrentEvidenceAdapter.Capture(manifest, directory.Path);
        var result = ProvenanceVerifier.VerifyCurrent(manifest, fixture.Bytes, current, true);

        Assert.False(current.MembershipRecipeRevalidated);
        Assert.Empty(current.ContextHashes);
        Assert.Equal(ProvenanceStatus.Invalid, result.Status);
        Assert.Contains(ProvenanceReasonCodes.ContextNotRevalidated, result.Reasons);
    }

    [Fact]
    public void CurrentVerificationIncludesDeclaredReferenceBytes()
    {
        var fixture = CreateFixture();
        var context = fixture.Manifest.Contexts.Single();
        var referenceBytes = fixture.Bytes["artifacts/app.dll"];
        var reference = new ManifestInput("reference", "refs/app.dll", "artifacts/app.dll",
            referenceBytes.Length, CanonicalIdentity.Sha256(referenceBytes.AsSpan()), null, false);
        var manifest = ManifestIdentity.Seal(fixture.Manifest with
        {
            Contexts = [context with { Inputs = context.Inputs.Append(reference).ToArray() }],
            ManifestHash = null
        });
        var source = context.Inputs.Single();
        var current = new CurrentEvidence(manifest.Revision.RepositoryIdentity, manifest.Revision.WorkspaceIdentity,
            manifest.Revision.Head, [new CurrentInputEvidence(source.Role, source.LogicalPath, source.Length, source.Sha256)],
            new Dictionary<string, string> { [manifest.Contexts.Single().Id] = manifest.Contexts.Single().ContextHash },
            true, manifest.Revision.StateHash);

        var result = ProvenanceVerifier.VerifyCurrent(manifest, fixture.Bytes, current, true);

        Assert.Contains(ProvenanceReasonCodes.SourceChanged, result.Reasons);
    }

    [Fact]
    public void GitCurrentEvidenceRejectsNonRepositoryRoot()
    {
        using var directory = TestDirectory.Create("crap4csharp-wrong-workspace-root");
        var fixture = CreateFixture();
        var manifest = fixture.Manifest with
        { Revision = new ManifestRevision("git", "repo", "worktree", "abc", null, null) };
        var error = Assert.Throws<InvalidDataException>(() => CurrentEvidenceAdapter.Capture(manifest,
            directory.Path, (_, arguments) => arguments.SequenceEqual(new[] { "rev-parse", "--show-toplevel" })
                ? Path.GetDirectoryName(directory.Path)! : ""));
        Assert.Contains("repository root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitCurrentEvidenceBindsStatusSubmodulesAndStagedDiff()
    {
        using var directory = TestDirectory.Create("crap4csharp-git-state");
        WriteCompiledSource(directory.Path);
        Directory.CreateDirectory(Path.Combine(directory.Path, ".git", "worktrees", "fixture"));
        var fixture = CreateFixture();
        string Run(string state, string _, IReadOnlyList<string> arguments) => string.Join(" ", arguments) switch
        {
            "rev-parse --show-toplevel" => directory.Path,
            "rev-parse --git-common-dir" => Path.Combine(directory.Path, ".git"),
            "rev-parse --git-dir" => Path.Combine(directory.Path, ".git", "worktrees", "fixture"),
            "rev-parse HEAD" => "abc",
            "status --porcelain=v1 -z --untracked-files=all -- tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs" => state,
            "submodule status --recursive" => "",
            "diff --cached --binary --full-index -- tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs" => "",
            var command => throw new InvalidOperationException(command)
        };
        var manifest = ManifestIdentity.Seal(fixture.Manifest with
        {
            Revision = new ManifestRevision("git", "repo", "worktree", "abc", null, null),
            ManifestHash = null
        });

        var clean = CurrentEvidenceAdapter.Capture(manifest, directory.Path,
            (root, arguments) => Run("", root, arguments));
        var dirty = CurrentEvidenceAdapter.Capture(manifest, directory.Path,
            (root, arguments) => Run(" M " + CompiledSource, root, arguments));

        Assert.NotNull(clean.StateHash);
        Assert.NotEqual(clean.StateHash, dirty.StateHash);
    }

    [Fact]
    public void LoaderRejectsASymbolicLinkInAnyLocatorComponent()
    {
        if (OperatingSystem.IsWindows()) return; // Creation requires a Windows symlink privilege; Linux exercises the policy.
        using var directory = TestDirectory.Create("crap4csharp-bundle-link");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var artifactDirectory = Path.Combine(directory.Path, "bundle", "artifacts");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.Move(artifactDirectory, outside);
        Directory.CreateSymbolicLink(artifactDirectory, outside);

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Fact]
    public void LoaderAllowsTheCallerToReachAnOwnedBundleThroughAParentSymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create("crap4csharp-bundle-parent-link");
        var fixture = CreateFixture();
        var realParent = Path.Combine(directory.Path, "real");
        Directory.CreateDirectory(realParent);
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(realParent, "bundle"));
        var linkedParent = Path.Combine(directory.Path, "linked");
        Directory.CreateSymbolicLink(linkedParent, realParent);
        var linkedLocator = Path.Combine(linkedParent, "bundle", "manifest.json");

        var loaded = ArtifactBundle.Load(linkedLocator, directory.Path);

        Assert.Equal(ProvenanceStatus.Captured,
            ProvenanceVerifier.VerifyCapture(loaded.Manifest, loaded.Bytes).Status);
        Assert.Equal(Path.GetFileName(locator), Path.GetFileName(loaded.ManifestPath));
    }

    [Fact]
    public void LoaderRejectsAManifestFifoBeforeOpeningIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create("crap4csharp-manifest-fifo");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        File.Delete(locator);
        Assert.Equal(0, MkFifo(locator, Convert.ToUInt32("600", 8)));

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Fact]
    public void LoaderRejectsAnArtifactFifoBeforeOpeningIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create("crap4csharp-bundle-fifo");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var source = Path.Combine(directory.Path, "bundle", "artifacts", "source.bin");
        File.Delete(source);
        Assert.Equal(0, MkFifo(source, Convert.ToUInt32("600", 8)));

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Fact]
    public void LoaderRejectsDuplicatePropertiesAndDoesNotCaseFoldUnknownMembers()
    {
        using var directory = TestDirectory.Create("crap4csharp-bundle-duplicate-json");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var original = File.ReadAllText(locator);
        File.WriteAllText(locator, original.Replace("\"logicalPath\":",
            "\"logicalPath\": null, \"logicalPath\":", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));

        File.WriteAllText(locator, original.Replace("\"logicalPath\":",
            "\"LogicalPath\": null, \"logicalPath\":", StringComparison.Ordinal));
        var loaded = ArtifactBundle.Load(locator, directory.Path);
        Assert.All(loaded.Manifest.Contexts.SelectMany(context => context.Inputs),
            input => Assert.False(string.IsNullOrWhiteSpace(input.LogicalPath)));
    }

    [Fact]
    public void LoaderRejectsNullPathMappingMembersBeforeVerification()
    {
        using var directory = TestDirectory.Create("crap4csharp-bundle-null-mapping");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var json = File.ReadAllText(locator).Replace("\"reportRoot\": \"/_/\"",
            "\"reportRoot\": null", StringComparison.Ordinal);
        File.WriteAllText(locator, json);

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Theory]
    [InlineData("\n", "lf")]
    [InlineData("\r\n", "crlf")]
    public void LoaderRejectsNullRootItemsBeforeDeserialization(string lineEnding, string fixtureName)
    {
        using var directory = TestDirectory.Create($"crap4csharp-bundle-null-root-{fixtureName}");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var original = File.ReadAllText(locator).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal);
        var document = JsonNode.Parse(original)!.AsObject();
        var roots = document["roots"]!.AsArray();
        var originalRootCount = roots.Count;
        roots.Insert(0, null);
        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal) + lineEnding;
        using var invalidFixture = JsonDocument.Parse(json);
        var mutatedRoots = invalidFixture.RootElement.GetProperty("roots");
        Assert.Equal(originalRootCount + 1, mutatedRoots.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, mutatedRoots[0].ValueKind);
        File.WriteAllText(locator, json);

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Fact]
    public void LoaderClassifiesANonCanonicalInputLogicalPathAsArtifactData()
    {
        using var directory = TestDirectory.Create("crap4csharp-bundle-input-path");
        var fixture = CreateFixture();
        var locator = ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes,
            Path.Combine(directory.Path, "bundle"));
        var json = File.ReadAllText(locator).Replace(CompiledSource, "../outside.cs", StringComparison.Ordinal);
        File.WriteAllText(locator, json);

        Assert.Throws<InvalidDataException>(() => ArtifactBundle.Load(locator, directory.Path));
    }

    [Fact]
    public void ThreePhaseValidationAcceptsExpectedGeneratedTransitionButNotAuthoredDrift()
    {
        var authored = new ManifestInput("source", "src/C.cs", "artifacts/source.bin", 3,
            CanonicalIdentity.Sha256([1, 2, 3]), "utf-8", false);
        var generated = new ManifestInput("source", "obj/G.g.cs", "artifacts/generated.bin", 2,
            CanonicalIdentity.Sha256([4, 5]), "utf-8", true);
        var assemblyPath = typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location;
        var assembly = ImmutableArray.Create(File.ReadAllBytes(assemblyPath));
        var pdb = ImmutableArray.Create(File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".pdb")));
        var inspected = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
        var observed = new CompilerInputObservation(ArtifactCaptureAdapter.CompilerEvidenceProvider, "ctx",
            [new CompiledInputIdentity(authored.LogicalPath, authored.Sha256, false),
             new CompiledInputIdentity(generated.LogicalPath, generated.Sha256, true)],
            assembly, pdb, inspected.ModuleIdentity, inspected.Mvid, true, null);

        var fresh = ArtifactCaptureAdapter.ValidatePhases("ctx", [authored], [authored], [generated], observed, false);
        var reusable = ArtifactCaptureAdapter.ValidatePhases("ctx", [authored], [authored], [generated], observed, true);
        var drifted = ArtifactCaptureAdapter.ValidatePhases("ctx", [authored],
            [authored with { Sha256 = CanonicalIdentity.Sha256([1, 2, 4]) }], [generated], observed, true);

        Assert.True(fresh.ActualBindingComplete);
        Assert.False(fresh.Reusable);
        Assert.Equal(ProvenanceReasonCodes.ReuseRecipeIncomplete, fresh.Reason);
        Assert.True(reusable.ActualBindingComplete);
        Assert.True(reusable.Reusable);
        Assert.False(drifted.ActualBindingComplete);
        Assert.Equal("provenance.authoredInputDrift", drifted.Reason);
    }

    [Fact]
    public async Task ConcurrentPublicationHasExactlyOneWinnerAndNeverOverwrites()
    {
        using var directory = TestDirectory.Create("crap4csharp-concurrent-publish");
        var fixture = CreateFixture();
        var target = Path.Combine(directory.Path, "bundle");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await Task.Yield();
            try
            {
                return (Success: true, Error: (Exception?)null,
                    Locator: ArtifactCaptureAdapter.PublishNew(fixture.Manifest, fixture.Bytes, target));
            }
            catch (Exception exception)
            {
                return (Success: false, Error: exception, Locator: (string?)null);
            }
        }));

        Assert.Single(attempts, attempt => attempt.Success);
        Assert.Single(attempts, attempt => !attempt.Success && attempt.Error is IOException);
        var loaded = ArtifactBundle.Load(Path.Combine(target, "manifest.json"), directory.Path);
        Assert.Equal(ProvenanceStatus.Captured, ProvenanceVerifier.VerifyCapture(loaded.Manifest, loaded.Bytes).Status);
    }

    private static FixtureData CreateFixture()
    {
        var source = ImmutableArray.Create(CompiledSourceBytes());
        var coverage = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "<coverage><packages><package name=\"Crap4CSharp.ProvenanceFixture\"><classes><class name=\"Crap4CSharp.ProvenanceFixture.CompiledEvidence\" filename=\"tests/Crap4CSharp.ProvenanceFixture/CompiledEvidence.cs\"><methods><method name=\"M\" signature=\"()\"><lines><line number=\"9\" hits=\"1\" /><line number=\"10\" hits=\"1\" /></lines></method></methods></class></classes></package></packages></coverage>"));
        var assemblyPath = typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location;
        var assembly = ImmutableArray.Create(File.ReadAllBytes(assemblyPath));
        var pdb = ImmutableArray.Create(File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".pdb")));
        var inspected = ArtifactEvidenceInspector.InspectBuild(assembly, pdb);
        var trx = ImmutableArray.Create(Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions><UnitTest storage="Crap4CSharp.ProvenanceFixture.dll" /></TestDefinitions>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0"
                error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """));
        var scope = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            $$"""{"version":1,"sources":["{{CompiledSource}}"]}"""));
        var policy = ImmutableArray.Create(Encoding.UTF8.GetBytes(
            """{"version":1,"threshold":8,"allowMissingCoverage":false}"""));
        var context = new ManifestContext("ctx", "App.csproj", "net10.0", "Debug", "AnyCPU",
            "", "", "", true, true,
            [new ManifestInput("source", CompiledSource, "artifacts/source.bin", source.Length,
                CanonicalIdentity.Sha256(source.AsSpan()), "utf-8", false)])
        {
            ParseOptions = new ManifestParseOptions(inspected.LanguageVersion, "Regular",
                inspected.PreprocessorSymbols,
                new Dictionary<string, string>(StringComparer.Ordinal)),
            PathPolicy = new ManifestPathPolicy("sensitive", [new ManifestReportRootMapping("/_/", "")])
        };
        var manifest = new RunManifest("1.0", CanonicalIdentity.Algorithm,
            new ManifestProducer("crap4csharp", "0.1.0", ComplexityRules.CallablesV1,
                ProjectAnalysisContext.ProtocolVersion, ManifestIdentity.CoverageProtocol, ManifestIdentity.PathProtocol),
            new ManifestCapture("completed", true, true, []),
            new ManifestRevision("none", "none", "workspace", null, null, null),
            [new ManifestRoot("workspace", "workspace", "sensitive")], [context],
            [new ManifestBuild("build", "ctx", inspected.ModuleIdentity,
                CanonicalIdentity.Sha256(assembly.AsSpan()), inspected.Mvid,
                CanonicalIdentity.Sha256(pdb.AsSpan()), inspected.DebugIdentity)],
            [new ManifestExecution("test", "ctx", "build", true, 0, 1, 1, 0, 0)
            {
                TestModuleIdentity = inspected.ModuleIdentity,
                TestAssemblySha256 = CanonicalIdentity.Sha256(assembly.AsSpan()),
                TestMvid = inspected.Mvid,
                TestPdbSha256 = CanonicalIdentity.Sha256(pdb.AsSpan()),
                TestDebugIdentity = inspected.DebugIdentity
            }],
            [new ManifestArtifact("source", "source", "artifacts/source.bin", source.Length,
                    CanonicalIdentity.Sha256(source.AsSpan()), "ctx", null, null, null, null),
             new ManifestArtifact("coverage", "coverage", "artifacts/coverage.xml", coverage.Length,
                    CanonicalIdentity.Sha256(coverage.AsSpan()), "ctx", "build", "test", "cobertura", "line"),
             new ManifestArtifact("assembly", "assembly", "artifacts/app.dll", assembly.Length,
                    CanonicalIdentity.Sha256(assembly.AsSpan()), "ctx", "build", null, null, null),
             new ManifestArtifact("pdb", "pdb", "artifacts/app.pdb", pdb.Length,
                    CanonicalIdentity.Sha256(pdb.AsSpan()), "ctx", "build", null, null, null),
             new ManifestArtifact("test-result", "test-result", "artifacts/results.trx", trx.Length,
                    CanonicalIdentity.Sha256(trx.AsSpan()), "ctx", "build", "test", "trx", null),
             new ManifestArtifact("test-assembly", "test-assembly", "artifacts/app.dll", assembly.Length,
                    CanonicalIdentity.Sha256(assembly.AsSpan()), "ctx", "build", "test", null, null),
             new ManifestArtifact("test-pdb", "test-pdb", "artifacts/app.pdb", pdb.Length,
                    CanonicalIdentity.Sha256(pdb.AsSpan()), "ctx", "build", "test", null, null),
             new ManifestArtifact("scope", "scope", "artifacts/scope.json", scope.Length,
                    CanonicalIdentity.Sha256(scope.AsSpan()), null, null, null, "json", null),
             new ManifestArtifact("policy", "policy", "artifacts/policy.json", policy.Length,
                    CanonicalIdentity.Sha256(policy.AsSpan()), null, null, null, "json", null)],
            new ManifestEvaluationInputs(CanonicalIdentity.Sha256(scope.AsSpan()),
                CanonicalIdentity.Sha256(policy.AsSpan()), null, null), null);
        return new FixtureData(manifest, new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal)
        {
            ["artifacts/source.bin"] = source,
            ["artifacts/coverage.xml"] = coverage,
            ["artifacts/app.dll"] = assembly,
            ["artifacts/app.pdb"] = pdb,
            ["artifacts/results.trx"] = trx,
            ["artifacts/scope.json"] = scope,
            ["artifacts/policy.json"] = policy
        });
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed record FixtureData(RunManifest Manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes);

    private static void WriteCompiledSource(string root)
    {
        var path = Path.Combine(root, CompiledSource.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CompiledSourceBytes());
    }

    private static byte[] CompiledSourceBytes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine(root.FullName, CompiledSource.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            root = root.Parent;
        }
        throw new FileNotFoundException($"Could not locate {CompiledSource}.");
    }
}
