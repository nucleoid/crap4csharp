using System.Text.Json;
using Crap4CSharp.Core;

// Only bounded selectors, never commands or saved MSBuild properties. This is
// not proof: callers still resolve the root and recompute the entire current plan.
internal sealed record CompletedReuseSelectors(string Scope, string Granularity,
    bool IncludeTests, bool IncludeGenerated)
{
    internal static CompletedReuseSelectors Read(byte[] scopeBytes, byte[] planBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (scopeBytes.Length is 0 or > 262144 || planBytes.Length is 0 or > 262144) throw Refuse();
            using var scopeDocument = JsonDocument.Parse(scopeBytes, new JsonDocumentOptions { MaxDepth = 8 });
            var scope = scopeDocument.RootElement;
            Keys(scope, ["version", "sources", "sourceState", "granularity", "requestedScope", "revision"]);
            if (scope.GetProperty("version").GetInt32() != 1 || scope.GetProperty("sourceState").GetString() != "worktree") throw Refuse();
            var requested = scope.GetProperty("requestedScope").GetString();
            var granularity = scope.GetProperty("granularity").GetString();
            if (requested is not ("all" or "worktree" or "base") || granularity is not ("file" or "method") ||
                scope.GetProperty("revision").ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) throw Refuse();
            var sources = scope.GetProperty("sources");
            if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > 10000) throw Refuse();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var path = source.GetString();
                if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains(':') || path.Contains('\\') ||
                    CanonicalIdentity.NormalizeLogicalPath(path) != path || !seen.Add(path)) throw Refuse();
            }
            using var planDocument = JsonDocument.Parse(planBytes, new JsonDocumentOptions { MaxDepth = 8 });
            var plan = planDocument.RootElement;
            if (plan.ValueKind != JsonValueKind.Object || plan.EnumerateObject().Count(p => p.Name == "graph") != 1) throw Refuse();
            var graph = plan.GetProperty("graph");
            if (graph.ValueKind != JsonValueKind.Array || graph.GetArrayLength() is 0 or > 10000) throw Refuse();
            bool? includeTests = null, includeGenerated = null;
            foreach (var node in graph.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                Keys(node, ["id", "project", "configuration", "platform", "framework", "dependencies", "propertiesHash", "contextId",
                    "test", "includeTests", "includeGenerated", "language", "sourceKind", "symbolsHash", "output", "intermediate", "referenceOutput"]);
                var tests = node.GetProperty("includeTests").GetBoolean();
                var generated = node.GetProperty("includeGenerated").GetBoolean();
                if (includeTests is not null && includeTests != tests || includeGenerated is not null && includeGenerated != generated) throw Refuse();
                includeTests = tests; includeGenerated = generated;
            }
            token.ThrowIfCancellationRequested();
            return new(requested, granularity, includeTests!.Value, includeGenerated!.Value);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or FormatException or OverflowException)
        { throw Refuse(); }
    }

    private static void Keys(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refuse();
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Refuse();
        if (remaining.Count != 0) throw Refuse();
    }
    private static PolicyException Refuse() => new("provenance.completedSelectorsUnsupported",
        "Completed evidence contains unsupported or ambiguous saved selectors.");
}
