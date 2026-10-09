using Crap4CSharp.Core;

// Public completion promotion is separate from historical private capture.
// Declarations are not proof: require a real owned bundle and a new complete
// observation against its ORIGINAL revision before advertising reusability.
internal static class FreshCompletedCapture
{
    internal static async Task<FreshCapturedBundle> PromoteAsync(FreshExecutionEvidence execution,
        FreshCapturedBundle captured, CancellationToken token, CompletedProofTiming? timing = null)
    {
        using (timing?.Start("promotion.originalRegate"))
            await FreshManifestCapture.RegateAsync(execution, token, timing, "promotion.originalRegate");
        var root = execution.Preflight.Root;
        string Relative(string physical)
        {
            var path = Path.GetRelativePath(root, physical).Replace('\\', '/');
            if (Path.IsPathRooted(path) || path.Contains(':') ||
                path.Split('/').Any(part => part is "" or "." or "..") ||
                CanonicalIdentity.NormalizeLogicalPath(path) != path) throw Refuse();
            return path;
        }
        var manifest = ManifestIdentity.Seal(captured.Manifest with
        {
            Contexts = captured.Manifest.Contexts.Select(context =>
            {
                var matches = execution.Compiler.FinalGraph.GraphContexts.Where(binding =>
                    binding.GraphId == context.Id).ToArray();
                if (matches.Length != 1) throw Refuse();
                var binding = matches[0];
                var assembly = binding.Context.EvaluatedTargetPath ?? throw Refuse();
                var declaration = new ManifestCurrentRevalidation(CompletedRevalidationDeclaration.Provider,
                    Relative(binding.ProjectPath), Relative(assembly), Relative(Path.ChangeExtension(assembly, ".pdb")));
                var proposed = context with { ReuseRecipeComplete = true, CurrentRevalidation = declaration };
                CompletedRevalidationDeclaration.Validate(proposed, Relative(binding.ProjectPath),
                    Relative(assembly), Relative(Path.ChangeExtension(assembly, ".pdb")));
                return proposed;
            }).ToArray(), ManifestHash = null
        });
        var prospective = captured with { Manifest = manifest };
        string path;
        using (timing?.Start("promotion.prospectivePublish"))
            path = await FreshManifestCapture.PublishAsync(execution, prospective,
                Path.Combine(execution.PrivateDirectory, "prospective-completed-bundle"), token, timing, "promotion.prospectivePublish");
        ArtifactBundle bundle;
        using (timing?.Start("promotion.physicalLoad"))
            bundle = ArtifactBundle.Load(path, execution.PrivateDirectory);
        await ValidateAsync(execution, bundle, token, timing: timing);
        return prospective;
    }

    internal static async Task<ProvenanceResult> ValidateAsync(FreshExecutionEvidence execution,
        ArtifactBundle bundle, CancellationToken token,
        Func<CancellationToken, Task>? beforeFinalValidation = null, CompletedProofTiming? timing = null)
    {
        token.ThrowIfCancellationRequested();
        var original = execution.Preflight;
        var local = original.GitSnapshot is null;
        FreshCheckPreflight preflight;
        using (timing?.Start("current.preflight"))
            preflight = await CompletedReusePreflight.LoadAsync(bundle, original.Root,
                original.Request.PolicyPath, local ? null : original.Trusted.Revision,
                local, null, original.Request.Timeout, token);
        CompletedReusePreflight.ValidateScope(bundle, preflight);
        // Only this live invocation may reuse its original retained compiler proof.
        // Saved-only public consumers continue through the full generic validator.
        var current = await FreshRetainedCompletedProof.ValidateAsync(execution, bundle, preflight, token,
            beforeFinalValidation, timing);
        var proof = ProvenanceVerifier.VerifyCurrent(bundle.Manifest, bundle.Bytes, current, true,
            original.Policy.Ruleset);
        if (proof.Status != ProvenanceStatus.Verified || !proof.Reusable) throw Refuse();
        if (original.Request.Output is not null)
        {
            bundle.RejectOutputAlias(original.Request.Output, original.Root);
            PolicyCheckCommand.RejectOutputAlias(original.Request.Output, original.Root, preflight.Trusted, current);
            OutputDestinationSafety.RejectExistingConsumerFile(original.Request.Output, original.Root);
        }
        token.ThrowIfCancellationRequested();
        return proof;
    }

    private static PolicyException Refuse() => new("check.completedPromotionUnproven",
        "Fresh completed evidence requires an independently verified physical complete bundle before reuse advertisement.");
}
