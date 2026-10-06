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
    string? Head, string? Base, string? ScopeHead)
{
    public string? StateHash { get; init; }
}
public sealed record ManifestRoot(string Id, string LogicalName, string CasePolicy);
public sealed record ManifestContext(string Id, string Project, string TargetFramework, string Configuration,
    string Platform, string SourceSetHash, string ContextHash, string InputClosureHash,
    bool ActualCompilerBindingComplete, bool ReuseRecipeComplete, IReadOnlyList<ManifestInput> Inputs)
{
    public ManifestParseOptions? ParseOptions { get; init; }
    public ManifestPathPolicy? PathPolicy { get; init; }
    public ManifestCurrentRevalidation? CurrentRevalidation { get; init; }
}
public sealed record ManifestCurrentRevalidation(string Provider, string Project, string AssemblyPath, string PdbPath)
{
    public IReadOnlyList<ManifestCurrentReference> References { get; init; } = [];
}
public sealed record ManifestCurrentReference(string LogicalPath, string WorkspacePath);
public sealed record ManifestParseOptions(string LanguageVersion, string SourceKind,
    IReadOnlyList<string> PreprocessorSymbols, IReadOnlyDictionary<string, string> Features);
public sealed record ManifestPathPolicy(string CasePolicy, IReadOnlyList<ManifestReportRootMapping> ReportRootMappings);
public sealed record ManifestReportRootMapping(string ReportRoot, string LogicalRoot);
public sealed record ManifestInput(string Role, string LogicalPath, string Locator, long Length, string Sha256,
    string? Encoding, bool Generated)
{
    public string? RepositoryPath { get; init; }
}
public sealed record ManifestBuild(string Id, string ContextId, string ModuleIdentity, string AssemblySha256,
    string Mvid, string PdbSha256, string DebugIdentity);
public sealed record ManifestExecution(string Id, string ContextId, string BuildId, bool Completed, int ExitCode,
    int TotalTests, int PassedTests, int FailedTests, int SkippedTests)
{
    public string TestProject { get; init; } = "";
    public string TestModuleIdentity { get; init; } = "";
    public string TestAssemblySha256 { get; init; } = "";
    public string TestMvid { get; init; } = "";
    public string TestPdbSha256 { get; init; } = "";
    public string TestDebugIdentity { get; init; } = "";
}
public sealed record ManifestArtifact(string Id, string Kind, string Locator, long Length, string Sha256,
    string? ContextId, string? BuildId, string? ExecutionId, string? Format, string? CoordinateKind);
public sealed record ManifestEvaluationInputs(string ScopeHash, string PolicyHash, string? BaselineHash,
    string? ExemptionsHash);

public sealed record CurrentInputEvidence(string Role, string LogicalPath, long Length, string Sha256)
{
    public string? RepositoryPath { get; init; }
}
internal sealed record CurrentEvidence(string RepositoryIdentity, string WorkspaceIdentity, string? Head,
    IReadOnlyList<CurrentInputEvidence> Inputs, IReadOnlyDictionary<string, string> ContextHashes,
    bool MembershipRecipeRevalidated = false, string? StateHash = null)
{
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];
}

public sealed record ProvenanceResult(ProvenanceStatus Status, string Basis, bool PostflightVerified,
    bool Reusable, IReadOnlyList<string> Reasons);

public static class ManifestIdentity
{
    public const string SchemaVersion = "1.0";
    public const string CoverageProtocol = "coverage-v1";
    public const string PathProtocol = "paths-v1";
    public const int ProducerMajorVersion = 0;
    public const int ProducerMinorVersion = 1;

    public static string SourceSetHash(ManifestContext context) => CanonicalIdentity.Set("manifest-source-set-v1",
        context.Inputs.Where(input => input.Role == "source").Select(InputIdentity));

    public static string InputClosureHash(ManifestContext context) => CanonicalIdentity.Set("manifest-input-closure-v1",
        context.Inputs.Select(InputIdentity));

    public static string ContextHash(ManifestContext context, string sourceSetHash, string inputClosureHash)
    {
        var parse = context.ParseOptions ?? new ManifestParseOptions("", "", [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var paths = context.PathPolicy ?? new ManifestPathPolicy("", []);
        var values = new List<string>
        {
            CanonicalIdentity.Tuple("context-header", context.Id, context.Project, context.TargetFramework,
                context.Configuration, context.Platform, sourceSetHash, inputClosureHash),
            CanonicalIdentity.Tuple("parse-options", parse.LanguageVersion, parse.SourceKind),
            CanonicalIdentity.Tuple("path-policy", paths.CasePolicy)
        };
        values.AddRange(parse.PreprocessorSymbols.Select(value => CanonicalIdentity.Tuple("preprocessor-symbol", value)));
        values.AddRange(parse.Features.Select(pair => CanonicalIdentity.Tuple("parse-feature", pair.Key, pair.Value)));
        values.AddRange(paths.ReportRootMappings.Select(mapping =>
            CanonicalIdentity.Tuple("report-root-mapping", mapping.ReportRoot, mapping.LogicalRoot)));
        return CanonicalIdentity.Set("manifest-context-v1", values);
    }

    public static string ManifestHash(RunManifest manifest)
    {
        var values = new List<string>
        {
            CanonicalIdentity.Tuple("manifest-header", manifest.ManifestSchemaVersion, manifest.IdentityAlgorithm),
            CanonicalIdentity.Tuple("producer", manifest.Producer.Tool, manifest.Producer.ToolVersion,
                manifest.Producer.ComplexityRuleset, manifest.Producer.ContextProtocol,
                manifest.Producer.CoverageProtocol, manifest.Producer.PathProtocol),
            CanonicalIdentity.Tuple("capture", manifest.Capture.State,
                manifest.Capture.CaptureConsistent.ToString(), manifest.Capture.ActualBindingComplete.ToString()),
            CanonicalIdentity.Tuple("revision", manifest.Revision.Kind, manifest.Revision.RepositoryIdentity,
                manifest.Revision.WorkspaceIdentity, manifest.Revision.Head, manifest.Revision.Base,
                manifest.Revision.ScopeHead, manifest.Revision.StateHash),
            CanonicalIdentity.Tuple("evaluation", manifest.EvaluationInputs.ScopeHash,
                manifest.EvaluationInputs.PolicyHash, manifest.EvaluationInputs.BaselineHash,
                manifest.EvaluationInputs.ExemptionsHash)
        };
        values.AddRange(manifest.Capture.FailureReasons.Select(value => CanonicalIdentity.Tuple("failure", value)));
        values.AddRange(manifest.Roots.Select(value =>
            CanonicalIdentity.Tuple("root", value.Id, value.LogicalName, value.CasePolicy)));
        values.AddRange(manifest.Contexts.Select(value =>
            CanonicalIdentity.Tuple("context", value.Id, value.Project, value.TargetFramework, value.Configuration,
                value.Platform, value.SourceSetHash, value.ContextHash, value.InputClosureHash,
                value.ActualCompilerBindingComplete.ToString(), value.ReuseRecipeComplete.ToString())));
        values.AddRange(manifest.Contexts.Where(value => value.CurrentRevalidation is not null).Select(value =>
            CanonicalIdentity.Tuple("current-revalidation", value.Id, value.CurrentRevalidation!.Provider,
                value.CurrentRevalidation.Project, value.CurrentRevalidation.AssemblyPath,
                value.CurrentRevalidation.PdbPath)));
        values.AddRange(manifest.Contexts.Where(value => value.CurrentRevalidation is not null)
            .SelectMany(value => value.CurrentRevalidation!.References.Select(reference =>
                CanonicalIdentity.Tuple("current-reference", value.Id, reference.LogicalPath,
                    reference.WorkspacePath))));
        values.AddRange(manifest.Contexts.SelectMany(context => context.Inputs.Select(input =>
            CanonicalIdentity.Tuple("context-input", context.Id, InputIdentity(input)))));
        values.AddRange(manifest.Builds.Select(value =>
            CanonicalIdentity.Tuple("build", value.Id, value.ContextId, value.ModuleIdentity, value.AssemblySha256,
                value.Mvid, value.PdbSha256, value.DebugIdentity)));
        values.AddRange(manifest.Executions.Select(value => string.IsNullOrEmpty(value.TestProject)
            ? CanonicalIdentity.Tuple("execution", value.Id, value.ContextId, value.BuildId, value.Completed.ToString(),
                value.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.TotalTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.PassedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.FailedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.SkippedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.TestModuleIdentity, value.TestAssemblySha256, value.TestMvid, value.TestPdbSha256,
                value.TestDebugIdentity)
            : CanonicalIdentity.Tuple("execution-with-test-project-v1", value.Id, value.ContextId, value.BuildId,
                value.Completed.ToString(), value.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.TotalTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.PassedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.FailedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.SkippedTests.ToString(System.Globalization.CultureInfo.InvariantCulture), value.TestProject,
                value.TestModuleIdentity, value.TestAssemblySha256, value.TestMvid, value.TestPdbSha256,
                value.TestDebugIdentity)));
        values.AddRange(manifest.Artifacts.Select(value =>
            CanonicalIdentity.Tuple("artifact", value.Id, value.Kind, value.Locator,
                value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Sha256,
                value.ContextId, value.BuildId, value.ExecutionId, value.Format, value.CoordinateKind)));
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

    private static string InputIdentity(ManifestInput input) => input.RepositoryPath is null
        ? CanonicalIdentity.Tuple("input", input.Role, input.LogicalPath, input.Locator,
            input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), input.Sha256,
            input.Encoding, input.Generated.ToString())
        : CanonicalIdentity.Tuple("input-repository-path-v1", input.Role, input.LogicalPath, input.RepositoryPath,
            input.Locator, input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), input.Sha256,
            input.Encoding, input.Generated.ToString());
}

public static class ProvenanceVerifier
{
    public static ProvenanceResult VerifyCapture(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, string? requiredRuleset = null) =>
        VerifyCaptureCancellable(manifest, artifactBytes, requiredRuleset, default);

    public static ProvenanceResult VerifyCaptureCancellable(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, string? requiredRuleset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(artifactBytes);
        var reasons = StructuralReasons(manifest, artifactBytes, requiredRuleset, cancellationToken);
        // Manifest v1 records only a producer assertion, not an executable
        // membership recipe that a consumer can independently revalidate.
        reasons.Add(ProvenanceReasonCodes.ReuseRecipeIncomplete);
        var invalid = reasons.Any(reason => reason != ProvenanceReasonCodes.ReuseRecipeIncomplete);
        return new ProvenanceResult(invalid ? ProvenanceStatus.Invalid : ProvenanceStatus.Captured,
            invalid ? "none" : "captureConsistency", false, false,
            reasons.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static ProvenanceResult VerifyCurrent(RunManifest manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, CurrentEvidence current,
        bool requireReusableRecipe, string? requiredRuleset = null)
    {
        var reasons = StructuralReasons(manifest, artifactBytes, requiredRuleset, default);
        if (!string.Equals(manifest.Revision.RepositoryIdentity, current.RepositoryIdentity, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.RevisionChanged);
        if (!string.Equals(manifest.Revision.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.WorkspaceChanged);
        if (!string.Equals(manifest.Revision.Head, current.Head, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.RevisionChanged);
        if (!string.Equals(manifest.Revision.StateHash, current.StateHash, StringComparison.Ordinal))
            reasons.Add(ProvenanceReasonCodes.WorkspaceChanged);

        var expectedGroups = manifest.Contexts.SelectMany(context => context.Inputs).Where(IsCurrentWorkspaceInput)
            .GroupBy(InputKey, StringComparer.Ordinal).ToArray();
        if (expectedGroups.Any(group => group.Select(input => (input.Length, input.Sha256)).Distinct().Count() != 1))
            reasons.Add(ProvenanceReasonCodes.SourceChanged);
        var expectedInputs = expectedGroups.Select(group => group.First()).OrderBy(InputKey, StringComparer.Ordinal).ToArray();
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
        IReadOnlyDictionary<string, ImmutableArray<byte>> artifactBytes, string? requiredRuleset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reasons = new List<string>();
        if (manifest.ManifestSchemaVersion != ManifestIdentity.SchemaVersion) reasons.Add(ProvenanceReasonCodes.SchemaUnsupported);
        if (manifest.IdentityAlgorithm != CanonicalIdentity.Algorithm)
            reasons.Add(ProvenanceReasonCodes.IdentityAlgorithmUnsupported);
        if (requiredRuleset is not null && manifest.Producer.ComplexityRuleset != requiredRuleset)
            reasons.Add(ProvenanceReasonCodes.RulesetMismatch);
        if (manifest.Producer.Tool != "crap4csharp" || !SupportedToolVersion(manifest.Producer.ToolVersion) ||
            manifest.Producer.ComplexityRuleset is not (ComplexityRules.OrdinaryMethodsV1 or ComplexityRules.CallablesV1) ||
            manifest.Producer.ContextProtocol != ProjectAnalysisContext.ProtocolVersion ||
            manifest.Producer.CoverageProtocol != ManifestIdentity.CoverageProtocol ||
            manifest.Producer.PathProtocol != ManifestIdentity.PathProtocol)
            reasons.Add(ProvenanceReasonCodes.ProducerUnsupported);
        if (manifest.Capture.State != "completed" || !manifest.Capture.CaptureConsistent)
            reasons.Add(ProvenanceReasonCodes.CaptureIncomplete);
        if (manifest.Capture.FailureReasons.Count > 0)
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
        if (manifest.Roots.Count == 0 || manifest.Roots.Any(root => root.CasePolicy is not ("sensitive" or "insensitive")))
            reasons.Add(ProvenanceReasonCodes.ContextHashChanged);
        if (manifest.ManifestHash is null) reasons.Add(ProvenanceReasonCodes.ManifestHashMissing);
        else if (manifest.ManifestHash != ManifestIdentity.ManifestHash(manifest with { ManifestHash = null }))
            reasons.Add(ProvenanceReasonCodes.ManifestHashChanged);
        foreach (var context in manifest.Contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceHash = ManifestIdentity.SourceSetHash(context);
            var closureHash = ManifestIdentity.InputClosureHash(context);
            if (sourceHash != context.SourceSetHash) reasons.Add(ProvenanceReasonCodes.SourceSetHashChanged);
            if (closureHash != context.InputClosureHash) reasons.Add(ProvenanceReasonCodes.InputClosureHashChanged);
            if (ManifestIdentity.ContextHash(context, sourceHash, closureHash) != context.ContextHash)
                reasons.Add(ProvenanceReasonCodes.ContextHashChanged);
            var contextBuilds = manifest.Builds.Where(build => build.ContextId == context.Id).ToArray();
            var contextExecutions = manifest.Executions.Where(execution => execution.ContextId == context.Id).ToArray();
            var contextCoverage = manifest.Artifacts.Where(artifact => artifact.Kind == "coverage" &&
                artifact.ContextId == context.Id).ToArray();
            if (contextBuilds.Length == 0) reasons.Add(ProvenanceReasonCodes.BuildEvidenceMissing);
            if (contextExecutions.Length == 0) reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);
            if (contextCoverage.Length == 0 || context.Inputs.All(input => input.Role != "source"))
                reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);
            if (context.Inputs.Where(input => input.Role == "source").Any(input =>
                    !string.Equals(input.Encoding, "utf-8", StringComparison.OrdinalIgnoreCase)))
                reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);
            if (context.PathPolicy is { CasePolicy: "sensitive" or "insensitive" } pathPolicy)
            {
                var comparer = pathPolicy.CasePolicy == "sensitive" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                if (context.Inputs.GroupBy(input => input.LogicalPath, comparer).Any(group => group.Count() > 1))
                    reasons.Add(ProvenanceReasonCodes.DuplicateIdentity);
            }
        }
        var inspectedBuilds = new Dictionary<string, InspectedBuildEvidence>(StringComparer.Ordinal);
        foreach (var build in manifest.Builds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assemblies = manifest.Artifacts.Where(artifact => artifact.Kind == "assembly" &&
                artifact.ContextId == build.ContextId && artifact.BuildId == build.Id).ToArray();
            var pdbs = manifest.Artifacts.Where(artifact => artifact.Kind == "pdb" &&
                artifact.ContextId == build.ContextId && artifact.BuildId == build.Id).ToArray();
            if (assemblies.Length != 1 || pdbs.Length != 1)
            {
                reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);
                continue;
            }
            var assembly = assemblies[0];
            var pdb = pdbs[0];
            if (assembly.Sha256 != build.AssemblySha256 ||
                pdb.Sha256 != build.PdbSha256 || !artifactBytes.TryGetValue(assembly.Locator, out var assemblyBytes) ||
                !artifactBytes.TryGetValue(pdb.Locator, out var pdbBytes))
            {
                reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);
                continue;
            }
            try
            {
                var inspected = ArtifactEvidenceInspector.InspectBuild(assemblyBytes, pdbBytes);
                if (inspected.ModuleIdentity != build.ModuleIdentity || inspected.Mvid != build.Mvid ||
                    inspected.DebugIdentity != build.DebugIdentity)
                    reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);
                var context = manifest.Contexts.FirstOrDefault(item => item.Id == build.ContextId);
                var sources = context?.Inputs.Where(input => input.Role == "source").ToArray() ?? [];
                var pathPolicy = context?.PathPolicy is null ? null : new CapturedPathPolicy(
                    context.PathPolicy.CasePolicy == "sensitive", context.PathPolicy.ReportRootMappings);
                var resolvedDocuments = context is null || pathPolicy is null ? null :
                    ResolveDocuments(inspected.Documents, sources.Select(input => input.LogicalPath).ToArray(), pathPolicy);
                if (context is null || resolvedDocuments is null || resolvedDocuments.Count != sources.Length ||
                    sources.Any(input => !resolvedDocuments.TryGetValue(input.LogicalPath, out var hash) ||
                        hash != input.Sha256) ||
                    context.ParseOptions is null ||
                    context.ParseOptions.LanguageVersion != inspected.LanguageVersion ||
                    !context.ParseOptions.PreprocessorSymbols.Order(StringComparer.Ordinal)
                        .SequenceEqual(inspected.PreprocessorSymbols, StringComparer.Ordinal))
                    reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete);
                else inspectedBuilds[build.Id] = inspected;
            }
            catch (InvalidDataException) { reasons.Add(ProvenanceReasonCodes.ActualBindingIncomplete); }
            catch (ArgumentException) { reasons.Add(ProvenanceReasonCodes.InvalidLocator); }
        }
        if (manifest.Executions.Any(item => !item.Completed || item.ExitCode != 0 || item.FailedTests != 0 || item.TotalTests <= 0 ||
                item.PassedTests + item.FailedTests + item.SkippedTests != item.TotalTests ||
                manifest.Artifacts.Count(artifact => artifact.Kind == "test-result" && artifact.ContextId == item.ContextId &&
                    artifact.BuildId == item.BuildId && artifact.ExecutionId == item.Id) != 1 ||
                !manifest.Builds.Any(build => build.Id == item.BuildId && build.ContextId == item.ContextId)))
            reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);
        foreach (var execution in manifest.Executions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trxArtifacts = manifest.Artifacts.Where(artifact => artifact.Kind == "test-result" &&
                artifact.ContextId == execution.ContextId && artifact.BuildId == execution.BuildId &&
                artifact.ExecutionId == execution.Id).ToArray();
            if (trxArtifacts.Length != 1) continue;
            var trx = trxArtifacts[0];
            if (!artifactBytes.TryGetValue(trx.Locator, out var trxBytes)) continue;
            try
            {
                var inspected = ArtifactEvidenceInspector.InspectTrx(trxBytes);
                var testAssemblies = manifest.Artifacts.Where(artifact => artifact.Kind == "test-assembly" &&
                    artifact.ContextId == execution.ContextId && artifact.BuildId == execution.BuildId &&
                    artifact.ExecutionId == execution.Id).ToArray();
                var testPdbs = manifest.Artifacts.Where(artifact => artifact.Kind == "test-pdb" &&
                    artifact.ContextId == execution.ContextId && artifact.BuildId == execution.BuildId &&
                    artifact.ExecutionId == execution.Id).ToArray();
                if (testAssemblies.Length != 1 || testPdbs.Length != 1 ||
                    !artifactBytes.TryGetValue(testAssemblies[0].Locator, out var testAssemblyBytes) ||
                    !artifactBytes.TryGetValue(testPdbs[0].Locator, out var testPdbBytes))
                {
                    reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);
                    continue;
                }
                var testBuild = ArtifactEvidenceInspector.InspectBuild(testAssemblyBytes, testPdbBytes);
                if (inspected.Total != execution.TotalTests || inspected.Passed != execution.PassedTests ||
                    inspected.Failed != execution.FailedTests || inspected.Skipped != execution.SkippedTests ||
                    testAssemblies[0].Sha256 != execution.TestAssemblySha256 ||
                    testPdbs[0].Sha256 != execution.TestPdbSha256 ||
                    testBuild.ModuleIdentity != execution.TestModuleIdentity ||
                    testBuild.Mvid != execution.TestMvid || testBuild.DebugIdentity != execution.TestDebugIdentity ||
                    !inspected.StorageModules.Contains(testBuild.ModuleIdentity, StringComparer.OrdinalIgnoreCase))
                    reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete);
            }
            catch (InvalidDataException) { reasons.Add(ProvenanceReasonCodes.TestExecutionIncomplete); }
        }

        foreach (var coverage in manifest.Artifacts.Where(item => item.Kind == "coverage"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var build = manifest.Builds.FirstOrDefault(item => item.Id == coverage.BuildId);
            var execution = manifest.Executions.FirstOrDefault(item => item.Id == coverage.ExecutionId);
            if (coverage.ContextId is null || build is null || execution is null ||
                string.IsNullOrWhiteSpace(coverage.Format) || string.IsNullOrWhiteSpace(coverage.CoordinateKind) ||
                build.ContextId != coverage.ContextId || execution.ContextId != coverage.ContextId ||
                execution.BuildId != build.Id || !execution.Completed || execution.ExitCode != 0 || execution.FailedTests != 0)
                reasons.Add(ProvenanceReasonCodes.DanglingReference);
            if (build is not null && artifactBytes.TryGetValue(coverage.Locator, out var coverageBytes))
            {
                try
                {
                    var inspected = ArtifactEvidenceInspector.InspectCoverage(coverageBytes);
                    var context = manifest.Contexts.FirstOrDefault(item => item.Id == build.ContextId);
                    var sourcePaths = context?.Inputs.Where(input => input.Role == "source")
                        .Select(input => input.LogicalPath).ToArray() ?? [];
                    var pathPolicy = context?.PathPolicy is null ? null : new CapturedPathPolicy(
                        context.PathPolicy.CasePolicy == "sensitive", context.PathPolicy.ReportRootMappings);
                    if (coverage.Format != inspected.Format || coverage.CoordinateKind != inspected.CoordinateKind ||
                        !inspected.ModuleIdentities.Contains(build.ModuleIdentity, StringComparer.Ordinal) ||
                        !inspectedBuilds.TryGetValue(build.Id, out var inspectedBuild) ||
                        pathPolicy is null ||
                        !ArtifactEvidenceInspector.CoverageMatchesBuildCancellable(coverageBytes, coverage.Locator,
                            inspectedBuild, sourcePaths, pathPolicy, cancellationToken))
                        reasons.Add(ProvenanceReasonCodes.IncompatiblePointRepresentation);
                }
                catch (InvalidDataException) { reasons.Add(ProvenanceReasonCodes.IncompatiblePointRepresentation); }
            }
        }
        ValidateEvaluationArtifact("scope", manifest.EvaluationInputs.ScopeHash, manifest.Artifacts, reasons);
        ValidateEvaluationArtifact("policy", manifest.EvaluationInputs.PolicyHash, manifest.Artifacts, reasons);
        if (manifest.EvaluationInputs.BaselineHash is not null)
            ValidateEvaluationArtifact("baseline", manifest.EvaluationInputs.BaselineHash, manifest.Artifacts, reasons);
        if (manifest.EvaluationInputs.ExemptionsHash is not null)
            ValidateEvaluationArtifact("exemptions", manifest.EvaluationInputs.ExemptionsHash, manifest.Artifacts, reasons);

        foreach (var artifact in manifest.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static bool SupportedToolVersion(string value)
    {
        var stable = value.Split('+', 2)[0].Split('-', 2)[0];
        return Version.TryParse(stable, out var version) && version.Major == ManifestIdentity.ProducerMajorVersion &&
            version.Minor == ManifestIdentity.ProducerMinorVersion;
    }

    private static void ValidateEvaluationArtifact(string kind, string expectedHash,
        IReadOnlyList<ManifestArtifact> artifacts, ICollection<string> reasons)
    {
        if (artifacts.Count(artifact => artifact.Kind == kind && artifact.Sha256 == expectedHash &&
                artifact.ContextId is null && artifact.BuildId is null && artifact.ExecutionId is null) != 1)
            reasons.Add(ProvenanceReasonCodes.ArtifactMissing);
    }

    private static void ValidateUnique(IEnumerable<string> ids, ICollection<string> reasons)
    { if (ids.GroupBy(id => id, StringComparer.Ordinal).Any(group => group.Count() > 1)) reasons.Add(ProvenanceReasonCodes.DuplicateIdentity); }
    private static bool SafeLocator(string locator)
    {
        try { return CanonicalIdentity.NormalizeLogicalPath(locator) == locator.Replace('\\', '/'); }
        catch (ArgumentException) { return false; }
    }
    private static string InputKey(ManifestInput input) => input.Role + "\n" + (input.RepositoryPath ?? input.LogicalPath);
    private static string InputKey(CurrentInputEvidence input) => input.Role + "\n" + (input.RepositoryPath ?? input.LogicalPath);
    private static bool IsCurrentWorkspaceInput(ManifestInput input) => !input.Generated;
    private static IReadOnlyDictionary<string, string>? ResolveDocuments(
        IReadOnlyDictionary<string, string> documents, IReadOnlyList<string> logicalPaths,
        CapturedPathPolicy pathPolicy)
    {
        var comparer = pathPolicy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var resolved = new Dictionary<string, string>(comparer);
        foreach (var document in documents)
        {
            var logical = CapturedLogicalPathResolver.Resolve(document.Key, logicalPaths, pathPolicy);
            var source = logical is null ? null : logicalPaths.SingleOrDefault(path => comparer.Equals(path, logical));
            if (source is null || !resolved.TryAdd(source, document.Value)) return null;
        }
        return resolved;
    }
}
