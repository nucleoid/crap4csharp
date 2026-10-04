using System.Collections.Immutable;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record CapturedScope(IReadOnlyList<string> Sources);
internal sealed record CapturedPolicy(double Threshold, bool AllowMissingCoverage);

internal static class CapturedEvaluationInputs
{
    public static (CapturedScope Scope, CapturedPolicy Policy) Read(ArtifactBundle bundle)
    {
        var scope = ParseScope(Bytes(bundle, "scope"));
        var policy = ParsePolicy(Bytes(bundle, "policy"));
        if (bundle.Manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1 && policy.AllowMissingCoverage)
            throw new InvalidDataException("callables-v1 captured policy cannot allow missing coverage.");
        var available = bundle.Manifest.Contexts.SelectMany(context => context.Inputs
                .Where(input => input.Role == "source" && !input.Generated)
                .Select(input => DeclaredRepositorySourcePath(context, input)))
            .ToHashSet(StringComparer.Ordinal);
        if (scope.Sources.Any(source => !available.Contains(source)))
            throw new InvalidDataException("Captured scope names a source outside the captured context inventory.");
        return (scope, policy);
    }

    internal static string RepositorySourcePath(string project, string logicalPath)
    {
        var source = CanonicalIdentity.NormalizeLogicalPath(logicalPath);
        if (source.StartsWith('<')) return source;
        var normalizedProject = CanonicalIdentity.NormalizeLogicalPath(project);
        var separator = normalizedProject.LastIndexOf('/');
        return separator < 0 ? source : CanonicalIdentity.NormalizeLogicalPath(
            normalizedProject[..(separator + 1)] + source);
    }

    internal static string DeclaredRepositorySourcePath(ManifestContext context, ManifestInput input) =>
        input.RepositoryPath is null ? RepositorySourcePath(context.Project, input.LogicalPath) :
            CanonicalIdentity.NormalizeLogicalPath(input.RepositoryPath);

    private static ImmutableArray<byte> Bytes(ArtifactBundle bundle, string kind)
    {
        var artifacts = bundle.Manifest.Artifacts.Where(item => item.Kind == kind &&
            item.ContextId is null && item.BuildId is null && item.ExecutionId is null).ToArray();
        if (artifacts.Length != 1)
            throw new InvalidDataException($"Captured {kind} artifact must be declared exactly once.");
        var artifact = artifacts[0];
        return bundle.Bytes.TryGetValue(artifact.Locator, out var bytes) ? bytes
            : throw new InvalidDataException($"Captured {kind} bytes are missing.");
    }

    private static CapturedScope ParseScope(ImmutableArray<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1 ||
                !root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Captured scope has an unsupported shape.");
            var values = sources.EnumerateArray().Select(item => CanonicalIdentity.NormalizeLogicalPath(item.GetString()
                    ?? throw new InvalidDataException("Captured scope contains a null source.")))
                .Order(StringComparer.Ordinal).ToArray();
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new InvalidDataException("Captured scope contains duplicate source identities.");
            return new CapturedScope(values);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or
            ArgumentException or FormatException or OverflowException)
        { throw new InvalidDataException("Captured scope JSON is malformed.", exception); }
    }

    private static CapturedPolicy ParsePolicy(ImmutableArray<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.String && schema.GetString() == RepositoryPolicy.Version)
            {
                var parsed = RepositoryPolicyParser.Parse(bytes.AsSpan(), "policy.json");
                return new CapturedPolicy(parsed.Policy.Threshold, false);
            }
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("Captured policy has an unsupported shape.");
            var threshold = root.GetProperty("threshold").GetDouble();
            if (!double.IsFinite(threshold) || threshold < 0)
                throw new InvalidDataException("Captured policy threshold must be finite and non-negative.");
            return new CapturedPolicy(threshold, root.GetProperty("allowMissingCoverage").GetBoolean());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or
            FormatException or OverflowException)
        { throw new InvalidDataException("Captured policy JSON is malformed.", exception); }
    }
}
