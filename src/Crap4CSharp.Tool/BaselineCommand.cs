using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record BaselineCommandOptions(string Verb, string Policy, string Manifest, string Output,
    bool Overwrite, TimeSpan Timeout);

internal static class BaselineCommand
{
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, string workingDirectory, TextWriter output,
        TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var root = Path.GetFullPath(workingDirectory);
            OutputDestinationSafety.RejectExistingConsumerFile(options.Output, root, baseline: true);
            var policyPath = RepositoryRelative(root, options.Policy);
            var policyBytes = File.ReadAllBytes(Path.Combine(root, policyPath.Replace('/', Path.DirectorySeparatorChar)));
            var parsedPolicy = RepositoryPolicyParser.Parse(policyBytes, policyPath);
            if (parsedPolicy.Policy.Mode != RepositoryPolicyMode.Strict)
                throw new PolicyException("baseline.strictPolicyRequired", "Baseline generation requires a strict baseline-optional onboarding policy.");
            if (parsedPolicy.Policy.Scope != "all")
                throw new PolicyException("baseline.fullScopeRequired", "Baseline generation requires policy scope 'all'.");
            var exemptionBytes = parsedPolicy.Policy.ExemptionFiles.ToDictionary(path => path,
                path => File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))),
                StringComparer.Ordinal);
            var compatibilityHash = TrustedPolicyLoader.BoundCompatibilityHash(parsedPolicy, exemptionBytes);
            var bundle = ArtifactBundle.Load(options.Manifest, root);
            PolicyCheckCommand.ValidateGeneratedInventory(bundle.Manifest);
            bundle.RejectOutputAlias(options.Output, root);
            var outputPath = Path.GetFullPath(options.Output, root);
            RejectPolicyOrSourceAlias(outputPath, root, policyPath, bundle.Manifest);
            if (File.Exists(outputPath) && !options.Overwrite)
                throw new IOException("Baseline output already exists; pass --overwrite to replace the approved destination.");
            if (Directory.Exists(outputPath)) throw new IOException("Baseline output is a directory.");
            if (bundle.Manifest.Producer.ComplexityRuleset != parsedPolicy.Policy.Ruleset)
                throw new BaselineException("baseline.rulesetMismatch", "Captured ruleset does not match policy.");
            if (bundle.Manifest.EvaluationInputs.PolicyHash != parsedPolicy.Hash)
                throw new BaselineException("baseline.policyMismatch", "Captured evidence was not evaluated under the supplied policy bytes.");
            ValidatePolicyCoverage(bundle.Manifest, parsedPolicy.Policy);
            var structural = ProvenanceVerifier.VerifyCaptureCancellable(bundle.Manifest, bundle.Bytes,
                parsedPolicy.Policy.Ruleset, cancellationToken);
            if (structural.Status == ProvenanceStatus.Invalid)
                throw new InvalidDataException(string.Join(", ", structural.Reasons));
            if (bundle.Manifest.Executions.Count == 0 || bundle.Manifest.Executions.Any(execution =>
                    !execution.Completed || execution.ExitCode != 0 || execution.FailedTests != 0 ||
                    execution.SkippedTests != 0 || execution.TotalTests <= 0 || execution.PassedTests != execution.TotalTests))
                throw new InvalidDataException(ProvenanceReasonCodes.TestExecutionIncomplete);

            var current = await CurrentEvidenceAdapter.CaptureSupportedAsync(bundle.Manifest, root,
                options.Timeout, cancellationToken);
            RejectCurrentOrOverlayAlias(outputPath, root, parsedPolicy, current);
            var provenance = ProvenanceVerifier.VerifyCurrent(bundle.Manifest, bundle.Bytes, current, false,
                parsedPolicy.Policy.Ruleset);
            if (provenance.Status != ProvenanceStatus.Verified)
                throw new InvalidDataException(string.Join(", ", provenance.Reasons));
            var replay = AnalyzeCommand.Replay(bundle, root, DateTimeOffset.UtcNow,
                TimeSpan.Zero, cancellationToken, allSources: true);
            var captured = CapturedEvaluationInputs.Read(bundle);
            ValidateCompleteScope(bundle.Manifest, captured.Scope);
            var observations = Observations(replay, bundle.Manifest)
                .Where(item => !Excluded(parsedPolicy.Policy, item.Path)).ToArray();
            var exemptionResolution = PolicyCheckCommand.ParseExemptions(exemptionBytes, replay);
            var ambiguous = observations.Where(item => item.IdentityAmbiguous).ToArray();
            observations = observations.Where(item => !item.IdentityAmbiguous).ToArray();
            var policyResult = PolicyEvaluator.Evaluate(parsedPolicy.Policy, null, observations,
                exemptionResolution.Active);
            if (policyResult.ExitCode == 1)
                throw new InvalidDataException(string.Join(", ", policyResult.OperationalReasons));
            if (options.Verb == "update") ValidateExistingBaseline(root, parsedPolicy, compatibilityHash);
            var candidate = BaselineDocument.Generate(compatibilityHash, parsedPolicy.Policy.Ruleset,
                bundle.Manifest.Revision.WorkspaceIdentity, bundle.Manifest.Revision.Head ?? "none",
                parsedPolicy.Policy.Threshold, observations);
            var candidateBytes = BaselineDocument.Serialize(candidate);
            await WriteCandidateAsync(outputPath, candidateBytes, options.Overwrite, cancellationToken);
            var summary = new
            {
                schemaVersion = "baseline-command-result-v1",
                command = options.Verb,
                trust = "local-unreviewed",
                approved = false,
                candidatePath = Path.GetRelativePath(root, outputPath).Replace('\\', '/'),
                candidateHash = CanonicalIdentity.Sha256(candidateBytes),
                entries = candidate.Entries.Count,
                skippedAmbiguousIdentities = ambiguous.Length,
                approvedUnsupportedExemptions = exemptionResolution.Active.Count,
                policyHash = parsedPolicy.Hash,
                policyCompatibilityHash = compatibilityHash,
                evidenceManifestHash = bundle.Manifest.ManifestHash,
                decision = policyResult.Decision,
                exitCode = policyResult.ExitCode
            };
            await output.WriteAsync(JsonSerializer.Serialize(summary, Json) + "\n");
            return policyResult.ExitCode;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or
            PolicyException or BaselineException or JsonException or OperationCanceledException or TimeoutException)
        {
            await output.WriteAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = "baseline-command-result-v1", status = "operationalError", exitCode = 1,
                reason = exception is PolicyException policy ? policy.Code : exception is BaselineException baseline
                    ? baseline.Code : exception is OperationCanceledException ? "run.cancelled" : "baseline.generationFailed"
            }, Json) + "\n");
            await error.WriteLineAsync($"error: {exception.Message}");
            return 1;
        }
    }

    internal static BaselineCommandOptions Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "baseline" || args[1] is not ("create" or "update"))
            throw new ArgumentException("Usage: crap4csharp baseline create|update --policy <path> --reuse-artifacts <manifest> --output <candidate> [--overwrite]");
        string? policy = null, manifest = null, output = null;
        var overwrite = false;
        var timeout = TimeSpan.FromSeconds(300);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new ArgumentException($"{option} may be specified only once.");
            string Value()
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {option}.");
                return args[index];
            }
            switch (option)
            {
                case "--policy": policy = Value(); break;
                case "--reuse-artifacts": manifest = Value(); break;
                case "--output": output = Value(); break;
                case "--overwrite": overwrite = true; break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value(), out var seconds) || seconds is < 1 or > 86400)
                        throw new ArgumentException("Timeout must be between 1 and 86400 seconds.");
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--format":
                    if (Value() != "json") throw new ArgumentException("Baseline command output format must be json.");
                    break;
                default: throw new ArgumentException($"Unknown baseline option: {option}");
            }
        }
        if (policy is null || manifest is null || output is null)
            throw new ArgumentException("--policy, --reuse-artifacts, and --output are required.");
        return new BaselineCommandOptions(args[1], policy, manifest, output, overwrite, timeout);
    }

    private static IEnumerable<PolicyObservation> Observations(ResultDocument result, RunManifest manifest)
    {
        foreach (var callable in result.Evaluation.Callables ?? [])
            if (callable.Applicability == "applicable")
            {
                var path = RepositorySourcePath(manifest, callable.ContextId, callable.Path);
                yield return new PolicyObservation(callable.Kind is "Lambda" or "AnonymousMethod" ? "anonymous" : "method",
                    callable.CallableId, "crap.thresholdExceeded", callable.Ruleset, callable.ContextId, path,
                    callable.BodyChecksum, callable.Complexity ?? 0, callable.Coverage, callable.Crap,
                    callable.CoverageReason, true, callable.CoverageReason == CoverageReasonCodes.AmbiguousCallableOwnership);
            }
        foreach (var family in result.Evaluation.Families ?? [])
        {
            var roots = (result.Evaluation.Callables ?? []).Where(item => item.CallableId == family.RootCallableId).ToArray();
            if (roots.Length != 1)
                throw new InvalidDataException("A callable family did not resolve to exactly one captured root.");
            var root = roots[0];
            var path = RepositorySourcePath(manifest, root.ContextId, root.Path);
            yield return new PolicyObservation("family", family.FamilyId, CallableFamilyEvaluator.Rule,
                result.ComplexityRulesetVersion, root.ContextId, path, root.BodyChecksum, family.Complexity,
                family.Coverage, family.Crap, family.IncompleteReasons.FirstOrDefault(), true,
                family.IncompleteReasons.Contains(CoverageReasonCodes.AmbiguousCallableOwnership, StringComparer.Ordinal))
                { RelatedEntityKeys = family.IncompleteCallableIds };
        }
    }

    private static bool Excluded(RepositoryPolicy policy, string path) => policy.Exclusions.Any(exclusion =>
        path == exclusion || path.StartsWith(exclusion.TrimEnd('/') + "/", StringComparison.Ordinal));

    private static string RepositorySourcePath(RunManifest manifest, string contextId, string logicalPath)
    {
        var context = manifest.Contexts.Single(item => item.Id == contextId);
        var input = context.Inputs.Single(item => item.Role == "source" && !item.Generated &&
            item.LogicalPath == logicalPath);
        return CapturedEvaluationInputs.DeclaredRepositorySourcePath(context, input);
    }

    private static void ValidateExistingBaseline(string root, ParsedRepositoryPolicy parsed, string compatibilityHash)
    {
        if (parsed.Policy.BaselinePath is null)
            throw new BaselineException("baseline.updateSourceMissing", "Baseline update requires policy.baseline.");
        var path = Path.Combine(root, parsed.Policy.BaselinePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new BaselineException("baseline.updateSourceMissing", "Existing baseline is missing.");
        BaselineDocument.Validate(BaselineDocument.Parse(File.ReadAllBytes(path)), compatibilityHash,
            parsed.Policy.Ruleset);
    }

    internal static void ValidatePolicyCoverage(RunManifest manifest, RepositoryPolicy policy)
    {
        var contexts = manifest.Contexts.Select(context =>
            (Project: CanonicalIdentity.NormalizeLogicalPath(context.Project), context.TargetFramework,
                context.Configuration)).OrderBy(item => item.Project, StringComparer.Ordinal)
            .ThenBy(item => item.TargetFramework, StringComparer.Ordinal).ToArray();
        var expectedContexts = policy.ProductionProjects.SelectMany(project => policy.TargetFrameworks.Select(framework =>
            (Project: project, TargetFramework: framework, Configuration: policy.Configuration)))
            .OrderBy(item => item.Project, StringComparer.Ordinal).ThenBy(item => item.TargetFramework, StringComparer.Ordinal).ToArray();
        if (!contexts.SequenceEqual(expectedContexts))
            throw new PolicyException("baseline.productionContextMismatch",
                "Captured production project, target framework, or configuration coverage differs from policy.");
        var testProjects = manifest.Executions.Select(item => item.TestProject)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(CanonicalIdentity.NormalizeLogicalPath)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!testProjects.SequenceEqual(policy.TestProjects.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new PolicyException("baseline.testTargetMismatch",
                "Captured test execution targets differ from policy testProjects.");
    }

    internal static void ValidateCompleteScope(RunManifest manifest, CapturedScope captured)
    {
        var completeScope = manifest.Contexts.SelectMany(context => context.Inputs
                .Where(input => input.Role == "source" && !input.Generated)
                .Select(input => CapturedEvaluationInputs.DeclaredRepositorySourcePath(context, input)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!captured.Sources.Order(StringComparer.Ordinal).SequenceEqual(completeScope, StringComparer.Ordinal))
            throw new PolicyException("baseline.scopeIncomplete",
                "Baseline generation requires captured full production source scope.");
    }

    private static string RepositoryRelative(string root, string value)
    {
        var full = Path.GetFullPath(value, root);
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("Policy must be inside the repository root.");
        return CanonicalIdentity.NormalizeLogicalPath(relative);
    }

    private static void RejectPolicyOrSourceAlias(string output, string root, string policyPath, RunManifest manifest)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var protectedPaths = manifest.Contexts.SelectMany(context => context.Inputs)
            .Where(input => !input.Generated).Select(input => Path.GetFullPath(input.LogicalPath, root))
            .Append(Path.GetFullPath(policyPath, root));
        if (protectedPaths.Any(path => string.Equals(path, output, comparison)))
            throw new IOException("Baseline output aliases a policy or source input.");
    }

    private static void RejectCurrentOrOverlayAlias(string output, string root, ParsedRepositoryPolicy policy,
        CurrentEvidence current)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var overlays = policy.Policy.ExemptionFiles
            .Concat(policy.Policy.BaselinePath is null ? [] : [policy.Policy.BaselinePath])
            .Append(policy.PolicyPath)
            .Select(path => Path.GetFullPath(path, root));
        if (current.ProtectedPaths.Concat(overlays)
            .Any(path => string.Equals(Path.GetFullPath(path), output, comparison)))
            throw new IOException("Baseline output aliases a current source or policy overlay.");
    }

    private static async Task WriteCandidateAsync(string destination, byte[] bytes, bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new IOException("Baseline output has no parent.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, overwrite);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }
}
