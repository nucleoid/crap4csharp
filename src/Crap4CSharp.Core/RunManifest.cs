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
    bool ActualCompilerBindingComplete, bool ReuseRecipeComplete, IReadOnlyList<ManifestInput> Inputs);
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
    IReadOnlyList<CurrentInputEvidence> Inputs, IReadOnlyDictionary<string, string> ContextHashes)
{
    public static CurrentEvidence FromManifest(RunManifest manifest) => new(
        manifest.Revision.RepositoryIdentity, manifest.Revision.WorkspaceIdentity, manifest.Revision.Head,
        manifest.Contexts.SelectMany(context => context.Inputs.Where(input => !input.Generated)
            .Select(input => new CurrentInputEvidence(input.Role, input.LogicalPath, input.Length, input.Sha256)))
            .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray(),
        manifest.Contexts.OrderBy(context => context.Id, StringComparer.Ordinal)
            .ToDictionary(context => context.Id, context => context.ContextHash, StringComparer.Ordinal));
}

public sealed record ProvenanceResult(ProvenanceStatus Status, string Basis, bool PostflightVerified,
    bool Reusable, IReadOnlyList<string> Reasons);

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
        if (!recipeComplete)
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
        if (manifest.ManifestSchemaVersion != "1.0") reasons.Add(ProvenanceReasonCodes.SchemaUnsupported);
        if (manifest.IdentityAlgorithm != CanonicalIdentity.Algorithm)
            reasons.Add(ProvenanceReasonCodes.IdentityAlgorithmUnsupported);
        if (requiredRuleset is not null && manifest.Producer.ComplexityRuleset != requiredRuleset)
            reasons.Add(ProvenanceReasonCodes.RulesetMismatch);
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
        if (manifest.Executions.Any(item => !item.Completed || item.ExitCode != 0 || item.FailedTests != 0 || item.TotalTests <= 0))
            reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);

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
