using System.Diagnostics;
using System.Text;
using Crap4CSharp.Core;

internal sealed record TrustedPolicyResolution(string Trust, string Revision, ParsedRepositoryPolicy Policy,
    BaselineDocument? Baseline, IReadOnlyDictionary<string, byte[]> ExemptionBytes,
    IReadOnlyDictionary<string, string> ContentHashes);

internal static class TrustedPolicyLoader
{
    public static TrustedPolicyResolution LoadFromBase(string baseRef, string policyPath,
        Func<IReadOnlyList<string>, byte[]>? gitExecutor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        var normalizedPolicy = Normalize(policyPath);
        var git = gitExecutor ?? (arguments => Git(arguments, Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(15)));
        var mergeBase = Encoding.UTF8.GetString(git(["merge-base", "HEAD", baseRef])).Trim();
        if (mergeBase.Length != 40 || mergeBase.Any(character => !Uri.IsHexDigit(character)))
            throw new PolicyException("policy.baseInvalid", "Git merge-base did not resolve to a full commit SHA.");
        var policyBytes = git(["show", $"{mergeBase}:{normalizedPolicy}"]);
        var parsed = RepositoryPolicyParser.Parse(policyBytes, normalizedPolicy);
        BaselineDocument? baseline = null;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        { [normalizedPolicy] = CanonicalIdentity.Sha256(policyBytes) };
        if (parsed.Policy.BaselinePath is { } baselinePath)
        {
            var bytes = git(["show", $"{mergeBase}:{baselinePath}"]);
            baseline = BaselineDocument.Parse(bytes);
            BaselineDocument.Validate(baseline, parsed.Hash, parsed.Policy.Ruleset);
            hashes[baselinePath] = CanonicalIdentity.Sha256(bytes);
        }
        else if (parsed.Policy.Mode == RepositoryPolicyMode.Incremental)
            throw new PolicyException("policy.baselineRequired", "Incremental trusted policy requires a baseline in the same immutable tree.");
        var exemptions = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in parsed.Policy.ExemptionFiles)
        {
            var bytes = git(["show", $"{mergeBase}:{path}"]);
            exemptions.Add(path, bytes);
            hashes[path] = CanonicalIdentity.Sha256(bytes);
        }
        return new TrustedPolicyResolution("base-trusted", mergeBase, parsed, baseline, exemptions, hashes);
    }

    public static TrustedPolicyResolution LoadLocal(string repositoryRoot, string policyPath)
    {
        var normalized = Normalize(policyPath);
        var root = Path.GetFullPath(repositoryRoot);
        var full = ResolveRegularFile(root, normalized);
        var bytes = File.ReadAllBytes(full);
        var parsed = RepositoryPolicyParser.Parse(bytes, normalized);
        BaselineDocument? baseline = null;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        { [normalized] = CanonicalIdentity.Sha256(bytes) };
        if (parsed.Policy.BaselinePath is { } baselinePath)
        {
            var baselineBytes = File.ReadAllBytes(ResolveRegularFile(root, baselinePath));
            baseline = BaselineDocument.Parse(baselineBytes);
            BaselineDocument.Validate(baseline, parsed.Hash, parsed.Policy.Ruleset);
            hashes[baselinePath] = CanonicalIdentity.Sha256(baselineBytes);
        }
        else if (parsed.Policy.Mode == RepositoryPolicyMode.Incremental)
            throw new PolicyException("policy.baselineRequired", "Incremental policy requires a baseline.");
        var exemptions = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in parsed.Policy.ExemptionFiles)
        {
            var exemptionBytes = File.ReadAllBytes(ResolveRegularFile(root, path));
            exemptions.Add(path, exemptionBytes);
            hashes[path] = CanonicalIdentity.Sha256(exemptionBytes);
        }
        return new TrustedPolicyResolution("local-unreviewed", "none", parsed, baseline, exemptions, hashes);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new PolicyException("policy.pathInvalid", "Policy path must be bounded repository-relative.");
        try { return CanonicalIdentity.NormalizeLogicalPath(path); }
        catch (ArgumentException exception) { throw new PolicyException("policy.pathInvalid", exception.Message); }
    }

    private static string ResolveRegularFile(string root, string logical)
    {
        var current = root;
        foreach (var part in logical.Split('/'))
        {
            current = Path.Combine(current, part);
            var relative = Path.GetRelativePath(root, current);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative)) throw new PolicyException("policy.pathInvalid", "Policy path escapes repository root.");
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new PolicyException("policy.fileMissing", $"Policy overlay is missing: {logical}");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new PolicyException("policy.pathInvalid", $"Policy overlay path contains a symbolic link: {logical}");
        }
        if (!File.Exists(current) || Directory.Exists(current))
            throw new PolicyException("policy.fileMissing", $"Policy overlay is not a regular file: {logical}");
        return current;
    }

    private static byte[] Git(IReadOnlyList<string> arguments, string root, TimeSpan timeout)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new PolicyException("policy.gitFailed", "Unable to start Git.");
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errors = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            process.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
            Task.WhenAll(copy, errors).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new PolicyException("policy.gitTimeout", exception.Message);
        }
        if (process.ExitCode != 0)
            throw new PolicyException("policy.gitFailed", errors.Result.Trim());
        return output.ToArray();
    }
}

internal sealed record PolicyOverrides(double? Threshold = null, IReadOnlyList<string>? Frameworks = null,
    IReadOnlyList<string>? ProductionProjects = null, IReadOnlyList<string>? TestProjects = null,
    IReadOnlyList<string>? Exclusions = null, string? ExemptionFile = null, string? Configuration = null,
    string? Scope = null, bool? IncludeTests = null, bool? IncludeGenerated = null);

internal sealed record PolicyOverrideValidation(bool Allowed, IReadOnlyList<string> Reasons);

internal static class PolicyOverrideValidator
{
    public static PolicyOverrideValidation Validate(RepositoryPolicy policy, PolicyOverrides proposed)
    {
        var reasons = new List<string>();
        if (proposed.Threshold is { } threshold && threshold > policy.Threshold)
            reasons.Add("policy.thresholdOverrideForbidden");
        if (proposed.Configuration is { } configuration && configuration != policy.Configuration)
            reasons.Add("policy.configurationOverrideForbidden");
        if (proposed.Frameworks is { } frameworks && !policy.AllowedOverrides.Frameworks &&
            !frameworks.Order(StringComparer.Ordinal).SequenceEqual(policy.TargetFrameworks.Order(StringComparer.Ordinal)))
            reasons.Add("policy.frameworkOverrideForbidden");
        if (proposed.ProductionProjects is { } production && !policy.ProductionProjects.All(production.Contains))
            reasons.Add("policy.productionProjectNarrowingForbidden");
        if (proposed.TestProjects is { } tests && !policy.TestProjects.All(tests.Contains))
            reasons.Add("policy.testProjectNarrowingForbidden");
        if (proposed.Exclusions is { } exclusions && !exclusions.SequenceEqual(policy.Exclusions))
            reasons.Add("policy.exclusionOverrideForbidden");
        if (proposed.ExemptionFile is not null) reasons.Add("policy.exemptionOverrideForbidden");
        if (proposed.Scope is { } scope && scope != policy.Scope && !policy.AllowedOverrides.Scope)
            reasons.Add("policy.scopeOverrideForbidden");
        if (proposed.IncludeTests is not null && !policy.AllowedOverrides.IncludeTests)
            reasons.Add("policy.includeTestsOverrideForbidden");
        if (proposed.IncludeGenerated is not null && !policy.AllowedOverrides.IncludeGenerated)
            reasons.Add("policy.includeGeneratedOverrideForbidden");
        var ordered = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new PolicyOverrideValidation(ordered.Length == 0, ordered);
    }
}
