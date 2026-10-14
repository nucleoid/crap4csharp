using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerReferenceSnapshotTests
{
    [Fact]
    public void AliasedReferenceMetadataSurvivesCaptureAndBindsOnlyItsDeclaredAlias()
    {
        using var fixture = TestDirectory.Create("crap4csharp-aliased-reference");
        var token = TestContext.Current.CancellationToken;
        var producer = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("ProbeLibrary",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("namespace Probe { public class Value {} }", cancellationToken: token)],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        Assert.True(producer.Emit(stream, cancellationToken: token).Success);
        var bytes = System.Collections.Immutable.ImmutableArray.CreateRange(stream.ToArray());
        var path = fixture.Write("ProbeLibrary.dll", "placeholder");
        File.WriteAllBytes(path, bytes.ToArray());
        var project = fixture.Write("Consumer.csproj", "<Project />");
        var observation = new CompilerObservation(project,
            [new CompilerParameter("References", [path]) { ReferenceProperties = [new(path, "ProbeLibrary", false)] }]);
        var reference = Assert.Single(CompilerReferenceSnapshot.ResolveSemanticReferences(observation, token));
        var hash = CanonicalIdentity.Sha256(bytes.AsSpan());
        var input = new ManifestInput("reference", CompilerReferenceSnapshot.LogicalReference(hash, reference.Aliases, false),
            "reference.dll", bytes.Length, hash, null, false) { ReferenceAliases = reference.Aliases };
        var captured = CompilerReferenceSnapshot.CapturedReference(input, bytes);
        Assert.Equal(["ProbeLibrary"], captured.Properties.Aliases);
        var consumer = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Consumer",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("extern alias ProbeLibrary; public class Consumer { public ProbeLibrary::Probe.Value Value; }", cancellationToken: token)],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location), captured],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(consumer.GetDiagnostics(token), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.NotEqual(CompilerReferenceSnapshot.LogicalReference(hash, null, false), input.LogicalPath);
        Assert.Throws<ProjectContextException>(() => CompilerReferenceSnapshot.CapturedReference(input with { ReferenceAliases = "Other" }, bytes));
    }

    [Fact]
    public void ReusedHashBufferDoesNotIncludePreviousFileTailBytes()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-buffer");
        var longFile = fixture.Write("Long.dll", new string('x', 70001));
        var shortFile = fixture.Write("Short.dll", "q");
        var otherFile = fixture.Write("Other.dll", new string('z', 65539));
        var token = TestContext.Current.CancellationToken;
        var scope = new CompilerReferenceSnapshot.ReadScope(token);
        long total = 0;
        foreach (var file in new[] { longFile, shortFile, otherFile })
        {
            var actual = scope.Read(file, ref total);
            Assert.Equal(CanonicalIdentity.Sha256(File.ReadAllBytes(file)), actual.Sha256);
            Assert.Equal(new FileInfo(file).Length, actual.Length);
        }
        scope.Complete();
        Assert.Throws<ProjectContextException>(() => scope.Read(shortFile, ref total));
    }

    [Fact]
    public void SharedReadCannotRebaselineAnotherContextOrSurviveTheNextBoundary()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-gate");
        var reference = fixture.Write("Reference.dll", "original");
        var project = fixture.Write("App.csproj", "<Project />");
        var token = TestContext.Current.CancellationToken;
        var original = CompilerReferenceSnapshot.Capture([reference], token);
        File.WriteAllText(reference, "modified");
        var later = CompilerReferenceSnapshot.Capture([reference], token);
        var observation = new CompilerObservation(project, [new("References", [reference])]);
        var scope = new CompilerReferenceSnapshot.ReadScope(token);
        later.Validate(observation, token, scope);
        Assert.Equal("compiler.referencesUnproven", Assert.Throws<ProjectContextException>(() =>
            original.Validate(observation, token, scope)).Reason);
        scope.Complete();
        File.WriteAllText(reference, "changed-again");
        Assert.Equal("compiler.referencesUnproven", Assert.Throws<ProjectContextException>(() =>
            later.Validate(observation, token, new CompilerReferenceSnapshot.ReadScope(token))).Reason);
    }

    [Fact]
    public void FreshClosingReadRejectsSameSizePreservedTimestampMutationAfterSharedFinalPass()
    {
        using var fixture = TestDirectory.Create("crap4csharp-final-reference-closing");
        var reference = fixture.Write("Reference.dll", "original");
        var firstProject = fixture.Write("First.csproj", "<Project />");
        var secondProject = fixture.Write("Second.csproj", "<Project />");
        var token = TestContext.Current.CancellationToken;
        var first = CompilerReferenceSnapshot.Capture([reference], token);
        var second = CompilerReferenceSnapshot.Capture([reference], token);
        var firstObservation = new CompilerObservation(firstProject, [new("References", [reference])]);
        var secondObservation = new CompilerObservation(secondProject, [new("References", [reference])]);
        var originalBytes = File.ReadAllBytes(reference);
        var originalTime = File.GetLastWriteTimeUtc(reference);
        var shared = new CompilerReferenceSnapshot.ReadScope(token);
        try
        {
            first.Validate(firstObservation, token, shared);
            File.WriteAllText(reference, "modified");
            File.SetLastWriteTimeUtc(reference, originalTime);
            Assert.Equal(originalBytes.Length, new FileInfo(reference).Length);
            Assert.Equal(originalTime, File.GetLastWriteTimeUtc(reference));
            // The same synchronous body can reuse opening bytes, but cannot
            // provide acceptance without the subsequent fresh closing read.
            second.Validate(secondObservation, token, shared);
            shared.Complete();
            var refusal = Assert.Throws<ProjectContextException>(() => second.Validate(secondObservation, token));
            Assert.Equal("compiler.referencesUnproven", refusal.Reason);
        }
        finally
        {
            shared.Abandon();
            File.WriteAllBytes(reference, originalBytes);
            File.SetLastWriteTimeUtc(reference, originalTime);
        }
    }

    // These controls deliberately use arbitrary regular-file bytes/fake reference observations;
    // they prove snapshot integrity, not PE validity, graph resolution or actual compilation.
    [Theory]
    [InlineData("bytes")]
    [InlineData("mtime")]
    [InlineData("deleted")]
    [InlineData("unexpected")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("duplicateParameter")]
    public void ChangedOrUnboundReferenceClosureRefuses(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-snapshot");
        var reference = fixture.Write("Reference.dll", "reference");
        var project = fixture.Write("App.csproj", "<Project />");
        var snapshot = CompilerReferenceSnapshot.Capture([reference], TestContext.Current.CancellationToken);
        var time = File.GetLastWriteTimeUtc(reference);
        IReadOnlyList<string> items = [reference];
        if (mutation == "bytes") { File.WriteAllText(reference, "changed!!"); File.SetLastWriteTimeUtc(reference, time); }
        if (mutation == "mtime") File.SetLastWriteTimeUtc(reference, time.AddMinutes(1));
        if (mutation == "deleted") File.Delete(reference);
        if (mutation == "unexpected") items = [fixture.Write("Other.dll", "other")];
        if (mutation == "missing") items = [];
        if (mutation == "duplicate") items = [reference, reference];
        var parameters = mutation == "duplicateParameter"
            ? new CompilerParameter[] { new("References", items), new("References", items) }
            : [new CompilerParameter("References", items)];
        var error = Assert.Throws<ProjectContextException>(() => snapshot.Validate(new(project, parameters), TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("relative")]
    [InlineData("oversize")]
    public void CaptureRefusesUnresolvedOrUnboundedPreparedPaths(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-capture");
        var reference = fixture.Write("Reference.dll", "reference");
        IReadOnlyList<string> paths = [reference];
        if (mutation == "missing") File.Delete(reference);
        if (mutation == "duplicate") paths = [reference, reference];
        if (mutation == "relative") paths = ["Reference.dll"];
        if (mutation == "oversize") { using var stream = File.OpenWrite(reference); stream.SetLength(CompilerPhaseSnapshot.MaximumFileBytes + 1); }
        var error = Assert.Throws<ProjectContextException>(() => CompilerReferenceSnapshot.Capture(paths, TestContext.Current.CancellationToken));
        Assert.Equal(mutation == "oversize" ? "compiler.inputLimit" : "compiler.referencesUnproven", error.Reason);
    }

    [Fact]
    public void StagedExtensionCannotRebaselineMutatedPreparedBytes()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-stage");
        var prepared = fixture.Write("Prepared.dll", "prepared");
        var snapshot = CompilerReferenceSnapshot.Capture([prepared], TestContext.Current.CancellationToken);
        var dependency = fixture.Write("Dependency.dll", "dependency");
        File.WriteAllText(prepared, "mutation");
        var error = Assert.Throws<ProjectContextException>(() => snapshot.Extend([dependency], TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }

    [Fact]
    public void StagedExtensionPreservesBothClosuresAndOriginalMembership()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-stage-positive");
        var prepared = fixture.Write("Prepared.dll", "prepared");
        var project = fixture.Write("App.csproj", "<Project />");
        var before = CompilerReferenceSnapshot.Capture([prepared], TestContext.Current.CancellationToken);
        var dependency = fixture.Write("Dependency.dll", "dependency");
        var after = before.Extend([dependency], TestContext.Current.CancellationToken);
        before.Validate(new(project, [new("References", [prepared])]), TestContext.Current.CancellationToken);
        after.Validate(new(project, [new("References", [prepared, dependency])]), TestContext.Current.CancellationToken);
        File.WriteAllText(dependency, "mutated");
        var error = Assert.Throws<ProjectContextException>(() => after.VerifyUnchanged(TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
        before.VerifyUnchanged(TestContext.Current.CancellationToken); // Extension did not mutate the old inventory.
    }

    [Fact]
    public void PreCancellationRefusesBeforeReferenceReads()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CompilerReferenceSnapshot.Capture(["missing"], cancelled.Token));
    }

    [Fact]
    public void SharedStagedPathKeepsOriginalIdentityAndDuplicateSuppliedPathsRefuse()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-overlap");
        var path = fixture.Write("Reference.dll", "reference");
        var snapshot = CompilerReferenceSnapshot.Capture([path], TestContext.Current.CancellationToken);
        snapshot.Extend([path], TestContext.Current.CancellationToken).VerifyUnchanged(TestContext.Current.CancellationToken);
        var error = Assert.Throws<ProjectContextException>(() => snapshot.Extend([path, path], TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }

    [Fact]
    public void SymlinkReferenceRefusesWithoutFollowingItsTarget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "File symlink creation requires Windows privileges; directory reparse controls exercise junctions.");
        using var fixture = TestDirectory.Create("crap4csharp-reference-symlink");
        var target = fixture.Write("Target.dll", "reference");
        var link = Path.Combine(fixture.Path, "Link.dll");
        File.CreateSymbolicLink(link, target);
        var error = Assert.Throws<ProjectContextException>(() => CompilerReferenceSnapshot.Capture([link], TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
        Assert.Equal("reference", File.ReadAllText(target));
    }

    [Fact]
    public void LinkedAncestorReferenceRefusesWithoutFollowingItsTarget()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-linked-ancestor");
        var target = fixture.Write("actual/Target.dll", "reference");
        var alias = Path.Combine(fixture.Path, "alias");
        ReparseTestDirectory.Link(alias, Path.GetDirectoryName(target)!);
        try
        {
            var error = Assert.Throws<ProjectContextException>(() => CompilerReferenceSnapshot.Capture(
                [Path.Combine(alias, "Target.dll")], TestContext.Current.CancellationToken));
            Assert.Equal("compiler.referencesUnproven", error.Reason);
            Assert.Equal("reference", File.ReadAllText(target));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void EmptyClosureControl()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-empty");
        var project = fixture.Write("App.csproj", "<Project />");
        var snapshot = CompilerReferenceSnapshot.Capture([], TestContext.Current.CancellationToken);
        snapshot.Validate(new(project, []), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedSdkReferencesBindIndependentActualCscWithoutConsumerMutation(bool relative)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-actual");
        // SDK references on another Windows drive cannot become relative. Keep
        // an explicit same-drive reference so the relative case exercises both
        // forms without replacing any independently captured SDK reference.
        var referenceDirectory = Path.Combine(fixture.Path, "References");
        Directory.CreateDirectory(referenceDirectory);
        File.Copy(typeof(Crap4CSharp.ProvenanceFixture.CompiledEvidence).Assembly.Location,
            Path.Combine(referenceDirectory, "Crap4CSharp.ProvenanceFixture.dll"));
        var project = fixture.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><Reference Include="Crap4CSharp.ProvenanceFixture">
            <HintPath>../References/Crap4CSharp.ProvenanceFixture.dll</HintPath>
            </Reference></ItemGroup></Project>
            """.Replace("</Project>", relative ? """
                <Target Name="RelativeCscReferences" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
                  <ItemGroup>
                    <_RelativeCscReference Include="@(ReferencePathWithRefAssemblies)">
                      <RelativePath>$([System.IO.Path]::GetRelativePath('$(MSBuildProjectDirectory)', '%(FullPath)'))</RelativePath>
                    </_RelativeCscReference>
                    <ReferencePathWithRefAssemblies Remove="@(ReferencePathWithRefAssemblies)" />
                    <ReferencePathWithRefAssemblies Include="@(_RelativeCscReference->'%(RelativePath)')" />
                  </ItemGroup>
                </Target></Project>
                """ : "</Project>", StringComparison.Ordinal));
        fixture.Write("App/Code.cs", "public class Code { public string M() => System.String.Empty; }");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        var context = Assert.Single(loaded.GraphContexts).Context;
        Assert.NotEmpty(context.ReferencePaths);
        var snapshot = CompilerReferenceSnapshot.Capture(context.ReferencePaths, TestContext.Current.CancellationToken);
        var directory = Path.Combine(fixture.Path, "private"); Directory.CreateDirectory(directory);
        var observation = await CompilerEvidence.BuildAsync(Assert.Single(loaded.BuildGraph), directory, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        snapshot.Validate(observation, TestContext.Current.CancellationToken);
        snapshot.VerifyUnchanged(TestContext.Current.CancellationToken);
        var raw = observation.Parameters.Single(parameter => parameter.Name == "References").Items.ToArray();
        if (relative) Assert.Contains(raw, path => !Path.IsPathFullyQualified(path));
        var resolved = CompilerReferenceSnapshot.ResolvePaths(observation, TestContext.Current.CancellationToken);
        Assert.All(resolved, path => Assert.True(Path.IsPathFullyQualified(path)));
        Assert.Equal(context.ReferencePaths.Order(PathIdentityPolicy.Current.Comparer), resolved.Order(PathIdentityPolicy.Current.Comparer));
        Assert.Equal(raw, observation.Parameters.Single(parameter => parameter.Name == "References").Items);
        foreach (var (path, saved) in authored)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void RelativeAndAbsoluteAliasesCannotHideDuplicateCompilerReferences()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-alias");
        var project = fixture.Write("App/App.csproj", "<Project />");
        var reference = fixture.Write("References/ref.dll", "reference");
        var observation = new CompilerObservation(project,
            [new("References", ["../References/ref.dll", reference])]);
        var error = Assert.Throws<ProjectContextException>(() =>
            CompilerReferenceSnapshot.ResolvePaths(observation, TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }
}
