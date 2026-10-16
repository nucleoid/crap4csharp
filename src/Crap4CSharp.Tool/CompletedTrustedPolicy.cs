using System.Collections.Immutable;
using Crap4CSharp.Core;

// Complete fresh bundles retain repository policy separately from the tiny
// replay PolicyOptions. Compare independently loaded canonical bytes, never
// substitute EvaluationInputs.PolicyHash or a producer's compatibility claim.
internal static class CompletedTrustedPolicy
{
    internal static void Validate(IReadOnlyList<ManifestArtifact> artifacts,
        IReadOnlyDictionary<string, ImmutableArray<byte>> bytes, byte[] canonicalPolicy,
        IReadOnlyDictionary<string, byte[]> exemptions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (canonicalPolicy.Length == 0 || canonicalPolicy.Length > ArtifactBundle.MaxArtifactBytes ||
            artifacts.Count > 10_000 || exemptions.Count > 10_000) throw Refuse();
        var expected = new Dictionary<string, (string Kind, byte[] Bytes)>(StringComparer.Ordinal)
        { ["trusted-policy"] = ("trusted-policy", canonicalPolicy) };
        foreach (var exemption in exemptions)
        {
            token.ThrowIfCancellationRequested();
            var id = "exemption-" + CanonicalIdentity.Tuple("exemption", exemption.Key);
            if (exemption.Value.Length > ArtifactBundle.MaxArtifactBytes ||
                !expected.TryAdd(id, ("trusted-exemption", exemption.Value))) throw Refuse();
        }
        var relevant = artifacts.Where(artifact => artifact.Kind is "trusted-policy" or "trusted-exemption" ||
            expected.ContainsKey(artifact.Id)).ToArray();
        if (relevant.Length != expected.Count) throw Refuse();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var locators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in relevant)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(artifact.Id) || !locators.Add(artifact.Locator) ||
                !expected.TryGetValue(artifact.Id, out var independent) || artifact.Kind != independent.Kind ||
                artifact.Format != "json" || artifact.ContextId is not null || artifact.BuildId is not null ||
                artifact.ExecutionId is not null || artifact.CoordinateKind is not null ||
                artifact.Length != independent.Bytes.Length ||
                artifact.Sha256 != CanonicalIdentity.Sha256(independent.Bytes) ||
                !bytes.TryGetValue(artifact.Locator, out var retained) || retained.IsDefault ||
                !retained.AsSpan().SequenceEqual(independent.Bytes)) throw Refuse();
        }
        token.ThrowIfCancellationRequested();
    }

    private static PolicyException Refuse() => new("provenance.trustedPolicyMismatch",
        "Completed evidence trusted policy or exemption bytes differ from independently loaded policy.");
}
