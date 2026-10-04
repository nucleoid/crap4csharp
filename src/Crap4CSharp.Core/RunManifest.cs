using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Crap4CSharp.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ProvenanceStatus>))]
public enum ProvenanceStatus { Unverified, Captured, Verified, Invalid }

public static class ProvenanceReasonCodes
{
    public const string SchemaUnsupported = "manifest.schemaUnsupported";
    public const string IdentityAlgorithmUnsupported = "manifest.identityAlgorithmUnsupported";
    public const string RulesetMismatch = "manifest.rulesetMismatch";
    public const string CaptureIncomplete = "provenance.captureIncomplete";
    public const string ActualBindingIncomplete = "provenance.actualBindingIncomplete";
    public const string ReuseRecipeIncomplete = "provenance.reuseRecipeIncomplete";
    public const string ContextNotRevalidated = "provenance.contextNotRevalidated";
    public const string ArtifactMissing = "provenance.artifactMissing";
    public const string ArtifactChanged = "provenance.artifactChanged";
    public const string SourceChanged = "provenance.sourceChanged";
    public const string ContextChanged = "provenance.contextChanged";
    public const string RevisionChanged = "provenance.revisionChanged";
    public const string WorkspaceChanged = "provenance.workspaceChanged";
    public const string DanglingReference = "manifest.danglingReference";
    public const string DuplicateIdentity = "manifest.duplicateIdentity";
    public const string InvalidLocator = "manifest.invalidLocator";
    public const string TestExecutionIncomplete = "provenance.testExecutionIncomplete";
    public const string IncompatiblePointRepresentation = "provenance.incompatiblePointRepresentation";
    public const string ProducerUnsupported = "manifest.producerUnsupported";
    public const string ManifestHashMissing = "manifest.hashMissing";
    public const string ManifestHashChanged = "manifest.hashChanged";
    public const string SourceSetHashChanged = "manifest.sourceSetHashChanged";
    public const string ContextHashChanged = "manifest.contextHashChanged";
    public const string InputClosureHashChanged = "manifest.inputClosureHashChanged";
    public const string BuildEvidenceMissing = "provenance.buildEvidenceMissing";
}

public sealed record RunManifest(
    string ManifestSchemaVersion,
    string IdentityAlgorithm,
    ManifestProducer Producer,
    ManifestCapture Capture,
    ManifestRevision Revision,
    IReadOnlyList<ManifestRoot> Roots,
    IReadOnlyList<ManifestContext> Contexts,
    IReadOnlyList<ManifestBuild> Builds,
    IReadOnlyList<ManifestExecution> Executions,
    IReadOnlyList<ManifestArtifact> Artifacts,
    ManifestEvaluationInputs EvaluationInputs,
    string? ManifestHash);

public sealed record ManifestProducer(string Tool, string ToolVersion, string ComplexityRuleset,
    string ContextProtocol, string CoverageProtocol, string PathProtocol);
public sealed record ManifestCapture(string State, bool CaptureConsistent, bool ActualBindingComplete,
    IReadOnlyList<string> FailureReasons);
public sealed record ManifestRevision(string Kind, string RepositoryIdentity, string WorkspaceIdentity,
    string? Head, string? Base, string? ScopeHead);
public sealed record ManifestRoot(string Id, string LogicalName, string CasePolicy);
public sealed record ManifestContext(string Id, string Project, string TargetFramework, string Configuration,
    string Platform, string SourceSetHash, string ContextHash, string InputClosureHash,
    bool ActualCompilerBindingComplete, bool ReuseRecipeComplete, IReadOnlyList<ManifestInput> Inputs)
{
    public ManifestParseOptions? ParseOptions { get; init; }
    public ManifestPathPolicy? PathPolicy { get; init; }
}
public sealed record ManifestParseOptions(string LanguageVersion, string SourceKind,
    IReadOnlyList<string> PreprocessorSymbols, IReadOnlyDictionary<string, string> Features);
public sealed record ManifestPathPolicy(string CasePolicy, IReadOnlyList<ManifestReportRootMapping> ReportRootMappings);
public sealed record ManifestReportRootMapping(string ReportRoot, string LogicalRoot);
public sealed record ManifestInput(string Role, string LogicalPath, string Locator, long Length, string Sha256,
    string? Encoding, bool Generated);
public sealed record ManifestBuild(string Id, string ContextId, string ModuleIdentity, string AssemblySha256,
    string Mvid, string PdbSha256, string DebugIdentity);
public sealed record ManifestExecution(string Id, string ContextId, string BuildId, bool Completed, int ExitCode,
    int TotalTests, int PassedTests, int FailedTests, int SkippedTests);
public sealed record ManifestArtifact(string Id, string Kind, string Locator, long Length, string Sha256,
    string? ContextId, string? BuildId, string? ExecutionId, string? Format, string? CoordinateKind);
public sealed record ManifestEvaluationInputs(string ScopeHash, string PolicyHash, string? BaselineHash,
    string? ExemptionsHash);

public sealed record CurrentInputEvidence(string Role, string LogicalPath, long Length, string Sha256);
public sealed record CurrentEvidence(string RepositoryIdentity, string WorkspaceIdentity, string? Head,
    IReadOnlyList<CurrentInputEvidence> Inputs, IReadOnlyDictionary<string, string> ContextHashes,
    bool MembershipRecipeRevalidated = false);

public sealed record ProvenanceResult(ProvenanceStatus Status, string Basis, bool PostflightVerified,
    bool Reusable, IReadOnlyList<string> Reasons);

public static class ManifestIdentity
{
    public const string SchemaVersion = "1.0";
    public const string CoverageProtocol = "coverage-v1";
    public const string PathProtocol = "paths-v1";

    public static string SourceSetHash(ManifestContext context) => CanonicalIdentity.Set("manifest-source-set-v1",
        context.Inputs.Where(input => input.Role == "source").Select(InputIdentity));

    public static string InputClosureHash(ManifestContext context) => CanonicalIdentity.Set("manifest-input-closure-v1",
        context.Inputs.Select(InputIdentity));

    public static string ContextHash(ManifestContext context, string sourceSetHash, string inputClosureHash)
    {
        var parse = context.ParseOptions ?? new ManifestParseOptions("", "", [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var paths = context.PathPolicy ?? new ManifestPathPolicy("", []);
        return CanonicalIdentity.Set("manifest-context-v1",
        [
            context.Id, context.Project, context.TargetFramework, context.Configuration, context.Platform,
            sourceSetHash, inputClosureHash, parse.LanguageVersion, parse.SourceKind,
            .. parse.PreprocessorSymbols.Select(value => "symbol\n" + value),
            .. parse.Features.Select(pair => $"feature\n{pair.Key}\n{pair.Value}"),
            paths.CasePolicy,
            .. paths.ReportRootMappings.Select(mapping => $"map\n{mapping.ReportRoot}\n{mapping.LogicalRoot}")
        ]);
    }

    public static string ManifestHash(RunManifest manifest)
    {
        var values = new List<string>
        {
            manifest.ManifestSchemaVersion, manifest.IdentityAlgorithm,
            $"producer\n{manifest.Producer.Tool}\n{manifest.Producer.ToolVersion}\n{manifest.Producer.ComplexityRuleset}\n{manifest.Producer.ContextProtocol}\n{manifest.Producer.CoverageProtocol}\n{manifest.Producer.PathProtocol}",
            $"capture\n{manifest.Capture.State}\n{manifest.Capture.CaptureConsistent}\n{manifest.Capture.ActualBindingComplete}",
            $"revision\n{manifest.Revision.Kind}\n{manifest.Revision.RepositoryIdentity}\n{manifest.Revision.WorkspaceIdentity}\n{manifest.Revision.Head}\n{manifest.Revision.Base}\n{manifest.Revision.ScopeHead}",
            $"evaluation\n{manifest.EvaluationInputs.ScopeHash}\n{manifest.EvaluationInputs.PolicyHash}\n{manifest.EvaluationInputs.BaselineHash}\n{manifest.EvaluationInputs.ExemptionsHash}"
        };
        values.AddRange(manifest.Capture.FailureReasons.Select(value => "failure\n" + value));
        values.AddRange(manifest.Roots.Select(value => $"root\n{value.Id}\n{value.LogicalName}\n{value.CasePolicy}"));
        values.AddRange(manifest.Contexts.Select(value =>
            $"context\n{value.Id}\n{value.Project}\n{value.TargetFramework}\n{value.Configuration}\n{value.Platform}\n{value.SourceSetHash}\n{value.ContextHash}\n{value.InputClosureHash}\n{value.ActualCompilerBindingComplete}\n{value.ReuseRecipeComplete}"));
        values.AddRange(manifest.Contexts.SelectMany(context => context.Inputs.Select(input =>
            $"input\n{context.Id}\n{InputIdentity(input)}")));
        values.AddRange(manifest.Builds.Select(value =>
            $"build\n{value.Id}\n{value.ContextId}\n{value.ModuleIdentity}\n{value.AssemblySha256}\n{value.Mvid}\n{value.PdbSha256}\n{value.DebugIdentity}"));
        values.AddRange(manifest.Executions.Select(value =>
            $"execution\n{value.Id}\n{value.ContextId}\n{value.BuildId}\n{value.Completed}\n{value.ExitCode}\n{value.TotalTests}\n{value.PassedTests}\n{value.FailedTests}\n{value.SkippedTests}"));
        values.AddRange(manifest.Artifacts.Select(value =>
            $"artifact\n{value.Id}\n{value.Kind}\n{value.Locator}\n{value.Length}\n{value.Sha256}\n{value.ContextId}\n{value.BuildId}\n{value.ExecutionId}\n{value.Format}\n{value.CoordinateKind}"));
        return CanonicalIdentity.Set("manifest-integrity-v1", values);
    }

    public static RunManifest Seal(RunManifest manifest)
    {
        var contexts = manifest.Contexts.Select(context =>
        {
            var source = SourceSetHash(context);
            var closure = InputClosureHash(context);
            return context with { SourceSetHash = source, InputClosureHash = closure,
                ContextHash = ContextHash(context, source, closure) };
        }).ToArray();
        var unhashed = manifest with { Contexts = contexts, ManifestHash = null };
        return unhashed with { ManifestHash = ManifestHash(unhashed) };
    }

    private static string InputIdentity(ManifestInput input) =>
        $"{input.Role}\n{input.LogicalPath}\n{input.Locator}\n{input.Length}\n{input.Sha256}\n{input.Encoding}\n{input.Generated}";
}

public static class ProvenanceVerifier
{
    public static ProvenanceResult VerifyCapture(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, string? requiredRuleset = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(artifactBytes);
        var reasons = StructuralReasons(manifest, artifactBytes, requiredRuleset);
        var invalid = reasons.Any(reason => reason != ProvenanceReasonCodes.ReuseRecipeIncomplete);
        return new ProvenanceResult(invalid ? ProvenanceStatus.Invalid : ProvenanceStatus.Captured,
            invalid ? "none" : "captureConsistency", false,
            !invalid && manifest.Contexts.All(context => context.ReuseRecipeComplete),
            reasons.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
    }

    public static ProvenanceResult VerifyCurrent(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, CurrentEvidence current,
        bool requireReusableRecipe, string? requiredRuleset = null)
    {
        var reasons = StructuralReasons(manifest, artifactBytes, requiredRuleset);
        if (!string.Equals(manifest.Revision.RepositoryIdentity, current.RepositoryIdentity, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.RevisionChanged);
        if (!string.Equals(manifest.Revision.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.WorkspaceChanged);
        if (!string.Equals(manifest.Revision.Head, current.Head, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.RevisionChanged);

        var expectedInputs = manifest.Contexts.SelectMany(context => context.Inputs).Where(input => !input.Generated)
            .OrderBy(InputKey, StringComparer.Ordinal).ToArray();
        var actualInputs = current.Inputs.OrderBy(InputKey, StringComparer.Ordinal).ToArray();
        if (expectedInputs.Length != actualInputs.Length)
            reasons.Add(ProvenanceReasonCodes.SourceChanged);
        else
            for (var index = 0; index < expectedInputs.Length; index++)
                if (InputKey(expectedInputs[index]) != InputKey(actualInputs[index]) ||
                    expectedInputs[index].Length != actualInputs[index].Length ||
                    expectedInputs[index].Sha256 != actualInputs[index].Sha256)
                    reasons.Add(ProvenanceReasonCodes.SourceChanged);

        foreach (var context in manifest.Contexts)
            if (!current.ContextHashes.TryGetValue(context.Id, out var hash) || hash != context.ContextHash)
                reasons.Add(ProvenanceReasonCodes.ContextChanged);

        var recipeComplete = manifest.Contexts.All(context => context.ReuseRecipeComplete);
        if (recipeComplete && !current.MembershipRecipeRevalidated)
            reasons.Add(ProvenanceReasonCodes.ContextNotRevalidated);
        else if (!recipeComplete)
            reasons.Add(requireReusableRecipe ? ProvenanceReasonCodes.ContextNotRevalidated
                : ProvenanceReasonCodes.ReuseRecipeIncomplete);
        var nonFatal = !requireReusableRecipe ? ProvenanceReasonCodes.ReuseRecipeIncomplete : null;
        var invalid = reasons.Any(reason => reason != nonFatal);
        return new ProvenanceResult(invalid ? ProvenanceStatus.Invalid : ProvenanceStatus.Verified,
            invalid ? "none" : "currentWorkspaceMatch", !invalid, !invalid && recipeComplete,
            reasons.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static List<string> StructuralReasons(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, string? requiredRuleset)
    {
        var reasons = new List<string>();
        if (manifest.ManifestSchemaVersion != ManifestIdentity.SchemaVersion) reasons.Add(ProvenanceReasonCodes.SchemaUnsupported);
        if (manifest.IdentityAlgorithm != CanonicalIdentity.Algorithm)
            reasons.Add(ProvenanceReasonCodes.IdentityAlgorithmUnsupported);
        if (requiredRuleset is not null && manifest.Producer.ComplexityRuleset != requiredRuleset)
            reasons.Add(ProvenanceReasonCodes.RulesetMismatch);
        if (manifest.Producer.Tool != "crap4csharp" ||
            manifest.Producer.ComplexityRuleset is not (ComplexityRules.OrdinaryMethodsV1 or ComplexityRules.CallablesV1) ||
            manifest.Producer.ContextProtocol != ProjectAnalysisContext.ProtocolVersion ||
            manifest.Producer.CoverageProtocol != ManifestIdentity.CoverageProtocol ||
            manifest.Producer.PathProtocol != ManifestIdentity.PathProtocol)
            reasons.Add(ProvenanceReasonCodes.ProducerUnsupported);
        if (manifest.Capture.State != "completed" || !manifest.Capture.CaptureConsistent)
            reasons.Add(ProvenanceReasonCodes.CaptureIncomplete);
        if (!manifest.Capture.ActualBindingComplete || manifest.Contexts.Any(context => !context.ActualCompilerBindingComplete))
            reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);

        ValidateUnique(manifest.Roots.Select(item => item.Id), reasons);
        ValidateUnique(manifest.Contexts.Select(item => item.Id), reasons);
        ValidateUnique(manifest.Builds.Select(item => item.Id), reasons);
        ValidateUnique(manifest.Executions.Select(item => item.Id), reasons);
        ValidateUnique(manifest.Artifacts.Select(item => item.Id), reasons);
        var contexts = manifest.Contexts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var builds = manifest.Builds.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var executions = manifest.Executions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (manifest.Builds.Any(item => !contexts.Contains(item.ContextId)) ||
            manifest.Executions.Any(item => !contexts.Contains(item.ContextId) || !builds.Contains(item.BuildId)) ||
            manifest.Artifacts.Any(item => item.ContextId is not null && !contexts.Contains(item.ContextId) ||
                item.BuildId is not null && !builds.Contains(item.BuildId) ||
                item.ExecutionId is not null && !executions.Contains(item.ExecutionId)))
            reasons.Add(ProvenanceReasonCodes.DanglingReference);
        if (manifest.ManifestHash is null) reasons.Add(ProvenanceReasonCodes.ManifestHashMissing);
        else if (manifest.ManifestHash != ManifestIdentity.ManifestHash(manifest with { ManifestHash = null }))
            reasons.Add(ProvenanceReasonCodes.ManifestHashChanged);
        foreach (var context in manifest.Contexts)
        {
            var sourceHash = ManifestIdentity.SourceSetHash(context);
            var closureHash = ManifestIdentity.InputClosureHash(context);
            if (sourceHash != context.SourceSetHash) reasons.Add(ProvenanceReasonCodes.SourceSetHashChanged);
            if (closureHash != context.InputClosureHash) reasons.Add(ProvenanceReasonCodes.InputClosureHashChanged);
            if (ManifestIdentity.ContextHash(context, sourceHash, closureHash) != context.ContextHash)
                reasons.Add(ProvenanceReasonCodes.ContextHashChanged);
            var contextBuilds = manifest.Builds.Where(build => build.ContextId == context.Id).ToArray();
            var contextExecutions = manifest.Executions.Where(execution => execution.ContextId == context.Id).ToArray();
            if (contextBuilds.Length == 0) reasons.Add(ProvenanceReasonCodes.BuildEvidenceMissing);
            if (contextExecutions.Length == 0) reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);
        }
        if (manifest.Executions.Any(item => !item.Completed || item.ExitCode != 0 || item.FailedTests != 0 || item.TotalTests <= 0 ||
                !manifest.Builds.Any(build => build.Id == item.BuildId && build.ContextId == item.ContextId)))
            reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);

        foreach (var coverage in manifest.Artifacts.Where(item => item.Kind == "coverage"))
        {
            var build = manifest.Builds.SingleOrDefault(item => item.Id == coverage.BuildId);
            var execution = manifest.Executions.SingleOrDefault(item => item.Id == coverage.ExecutionId);
            if (coverage.ContextId is null || build is null || execution is null ||
                build.ContextId != coverage.ContextId || execution.ContextId != coverage.ContextId ||
                execution.BuildId != build.Id || !execution.Completed || execution.ExitCode != 0 || execution.FailedTests != 0)
                reasons.Add(ProvenanceReasonCodes.DanglingReference);
        }

        foreach (var artifact in manifest.Artifacts)
        {
            if (!SafeLocator(artifact.Locator)) { reasons.Add(ProvenanceReasonCodes.InvalidLocator); continue; }
            if (!artifactBytes.TryGetValue(artifact.Locator, out var bytes))
            { reasons.Add(ProvenanceReasonCodes.ArtifactMissing); continue; }
            if (bytes.Length != artifact.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != artifact.Sha256)
                reasons.Add(ProvenanceReasonCodes.ArtifactChanged);
        }
        foreach (var input in manifest.Contexts.SelectMany(context => context.Inputs))
        {
            if (!SafeLocator(input.Locator)) { reasons.Add(ProvenanceReasonCodes.InvalidLocator); continue; }
            if (!artifactBytes.TryGetValue(input.Locator, out var bytes))
            { reasons.Add(ProvenanceReasonCodes.ArtifactMissing); continue; }
            if (bytes.Length != input.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != input.Sha256)
                reasons.Add(ProvenanceReasonCodes.ArtifactChanged);
        }

        foreach (var group in manifest.Artifacts.Where(item => item.Kind == "coverage")
            .GroupBy(item => (item.ContextId, item.BuildId), EqualityComparer<(string?, string?)>.Default))
            if (group.Select(item => item.CoordinateKind).Distinct(StringComparer.Ordinal).Count() > 1)
                reasons.Add(ProvenanceReasonCodes.IncompatiblePointRepresentation);
        if (manifest.Contexts.Any(context => context.ParseOptions is null || context.PathPolicy is null))
            reasons.Add(ProvenanceReasonCodes.ContextHashChanged);
        return reasons;
    }

    private static void ValidateUnique(IEnumerable<string> ids, ICollection<string> reasons)
    { if (ids.GroupBy(id => id, StringComparer.Ordinal).Any(group => group.Count() > 1)) reasons.Add(ProvenanceReasonCodes.DuplicateIdentity); }
    private static bool SafeLocator(string locator)
    {
        try { return CanonicalIdentity.NormalizeLogicalPath(locator) == locator.Replace('\\', '/'); }
        catch (ArgumentException) { return false; }
    }
    private static string InputKey(ManifestInput input) => input.Role + "\n" + input.LogicalPath;
    private static string InputKey(CurrentInputEvidence input) => input.Role + "\n" + input.LogicalPath;
}
