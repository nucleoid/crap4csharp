using System.Text.Json;

namespace Crap4CSharp.Core;

// Public callable selector only. Native scheduling, output ownership, retained
// compiler proofs and prior-build replay continue to use the physical node.Id.
internal static class LogicalContextIdentity
{
    internal static string Create(string root, ProjectBuildGraphNode node)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(node.ProjectPath))
            throw new ArgumentException("Context roles require an independently bound absolute root and project.");
        var role = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(node.ProjectPath));
        if (Path.IsPathRooted(role)) throw new ArgumentException("Context project is outside its bound root.");
        role = CanonicalIdentity.NormalizeLogicalPath(role);
        // Public repository roles retain their evaluated spelling on every OS,
        // as CallableId does. Physical path comparison still uses its own policy.
        MappedGlobalPrivacy.Validate(node.GlobalProperties);
        var globals = node.GlobalProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new[] { p.Key.ToUpperInvariant(), p.Value }).ToArray();
        if (globals.Select(p => p[0]).Distinct(StringComparer.Ordinal).Count() != globals.Length)
            throw new ArgumentException("Mapped global names must be unambiguous.");
        // Preserve every value exactly, including path-valued custom globals.
        // No physical-prefix substitution or inferred mapping is permitted.
        return ProjectAnalysisContext.ContentHash(JsonSerializer.Serialize(new
        {
            selector = "repository-project-mapped-globals-v1", role,
            node.TargetFramework, node.Configuration, node.Platform, globals
        }));
    }
}
