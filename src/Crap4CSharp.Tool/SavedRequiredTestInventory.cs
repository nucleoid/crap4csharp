using System.Text.Json;

namespace Crap4CSharp.Core;

internal sealed record SavedRequiredModule(string GraphId, string Artifact);
internal sealed record SavedRequiredTest(string GraphId, string Project, string Trx, string Assembly,
    string Pdb, string Child, string ModuleIdentity, string Mvid, string DebugIdentity, int Passed,
    IReadOnlyList<SavedRequiredModule> Modules);

// Structural declaration only. Independently selected CURRENT schedule is required;
// the caller still owes artifact bytes/kinds, compiler/TRX/child/module/coverage and
// production-execution agreement. Parsing never establishes completed reuse.
internal static class SavedRequiredTestInventory
{
    internal const int MaximumBytes = 256 * 1024;
    internal static IReadOnlyList<SavedRequiredTest> Parse(byte[] bytes,
        IReadOnlyList<string> requiredGraphIds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaximumBytes ||
                requiredGraphIds.Count is 0 or > 1024 || requiredGraphIds.Any(string.IsNullOrWhiteSpace) ||
                requiredGraphIds.Distinct(StringComparer.Ordinal).Count() != requiredGraphIds.Count) throw Refuse();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
            var root = document.RootElement;
            Fields(root, "provider", "tests", "reusableRecipe");
            if (Text(root, "provider") != "finite-sdk-required-test-inventory-v1" ||
                root.GetProperty("reusableRecipe").GetBoolean()) throw Refuse();
            var tests = root.GetProperty("tests");
            if (tests.ValueKind != JsonValueKind.Array || tests.GetArrayLength() != requiredGraphIds.Count) throw Refuse();
            var results = new List<SavedRequiredTest>();
            foreach (var test in tests.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                Fields(test, "graphId", "project", "trx", "assembly", "pdb", "child", "moduleIdentity",
                    "mvid", "debugIdentity", "passed", "rawExitCode", "modules");
                var graphId = Text(test, "graphId");
                if (graphId != requiredGraphIds[results.Count]) throw Refuse();
                var id = "required-test-" + graphId;
                var project = Text(test, "project");
                if (CanonicalIdentity.NormalizeLogicalPath(project) != project) throw Refuse();
                var trx = Text(test, "trx"); var assembly = Text(test, "assembly");
                var pdb = Text(test, "pdb"); var child = Text(test, "child");
                if (trx != id + "-trx" || assembly != id + "-pe" || pdb != id + "-pdb" || child != id + "-child" ||
                    test.GetProperty("rawExitCode").GetInt32() != 0) throw Refuse();
                var passed = test.GetProperty("passed").GetInt32();
                if (passed <= 0) throw Refuse();
                var modules = test.GetProperty("modules");
                if (modules.ValueKind != JsonValueKind.Array || modules.GetArrayLength() > 1024) throw Refuse();
                var savedModules = new List<SavedRequiredModule>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var module in modules.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    Fields(module, "graphId", "artifact");
                    var moduleId = Text(module, "graphId"); var artifact = Text(module, "artifact");
                    if (!seen.Add(moduleId) || artifact != id + "-coverage-" + moduleId) throw Refuse();
                    savedModules.Add(new(moduleId, artifact));
                }
                results.Add(new(graphId, project, trx, assembly, pdb, child, Text(test, "moduleIdentity"),
                    Text(test, "mvid"), Text(test, "debugIdentity"), passed, savedModules.AsReadOnly()));
            }
            token.ThrowIfCancellationRequested();
            return results.AsReadOnly();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or
            OverflowException or ArgumentException)
        { throw Refuse(); }
    }

    private static string Text(JsonElement value, string name)
    {
        var result = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > 4096 || result.Any(char.IsControl)) throw Refuse();
        return result;
    }
    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refuse();
        var fields = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!fields.Remove(property.Name)) throw Refuse();
        if (fields.Count != 0) throw Refuse();
    }
    private static PolicyException Refuse() => new("provenance.savedRequiredInventoryUnproven",
        "Saved required-test inventory is malformed, unsupported or differs from the independently required current schedule.");
}
