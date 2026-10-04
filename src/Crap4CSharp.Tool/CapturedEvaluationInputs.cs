using System.Collections.Immutable;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record CapturedScope(IReadOnlyList<string> Sources);
internal sealed record CapturedPolicy(double Threshold, bool AllowMissingCoverage);

internal static class CapturedEvaluationInputs
{
    public static (CapturedScope Scope, CapturedPolicy Policy) Read(ArtifactBundle bundle)
    {
        if (bundle.Manifest.EvaluationInputs.BaselineHash is not null ||
            bundle.Manifest.EvaluationInputs.ExemptionsHash is not null)
            throw new InvalidDataException("Captured baseline and exemption evaluation is not supported by this reader version.");
        var scope = ParseScope(Bytes(bundle, "scope"));
        var policy = ParsePolicy(Bytes(bundle, "policy"));
        if (bundle.Manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1 && policy.AllowMissingCoverage)
            throw new InvalidDataException("callables-v1 captured policy cannot allow missing coverage.");
        var available = bundle.Manifest.Contexts.SelectMany(context => context.Inputs)
            .Where(input => input.Role == "source" && !input.Generated).Select(input => input.LogicalPath)
            .ToHashSet(StringComparer.Ordinal);
        if (scope.Sources.Any(source => !available.Contains(source)))
            throw new InvalidDataException("Captured scope names a source outside the captured context inventory.");
        return (scope, policy);
    }

    private static ImmutableArray<byte> Bytes(ArtifactBundle bundle, string kind)
    {
        var artifact = bundle.Manifest.Artifacts.SingleOrDefault(item => item.Kind == kind &&
            item.ContextId is null && item.BuildId is null && item.ExecutionId is null)
            ?? throw new InvalidDataException($"Captured {kind} artifact is missing.");
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
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Captured scope JSON is malformed.", exception); }
    }

    private static CapturedPolicy ParsePolicy(ImmutableArray<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("Captured policy has an unsupported shape.");
            var threshold = root.GetProperty("threshold").GetDouble();
            if (!double.IsFinite(threshold) || threshold < 0)
                throw new InvalidDataException("Captured policy threshold must be finite and non-negative.");
            return new CapturedPolicy(threshold, root.GetProperty("allowMissingCoverage").GetBoolean());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Captured policy JSON is malformed.", exception); }
    }
}
