using Microsoft.CodeAnalysis;

namespace Crap4CSharp.Core;

// Invocation-owned source projects, identified by exact SDK graph nodes.
internal sealed class MappedDesignTimeWorkspace : IDisposable
{
    internal AdhocWorkspace Workspace { get; }
    private readonly IReadOnlyDictionary<string, ProjectBuildGraphNode> nodes;
    private readonly IReadOnlyDictionary<string, (string? Output, string? Reference)> outputs;
    private readonly Dictionary<string, ProjectId> projects = new(StringComparer.Ordinal);
    private readonly HashSet<string> unresolvedReferences = new(PathIdentityPolicy.Current.Comparer);

    internal MappedDesignTimeWorkspace(IReadOnlyList<ProjectBuildGraphNode> graph,
        IReadOnlyDictionary<string, (string? Output, string? Reference)> outputs)
    {
        nodes = graph.ToDictionary(node => node.Id, StringComparer.Ordinal);
        this.outputs = outputs;
        if (nodes.Count != outputs.Count || nodes.Keys.Any(id => !outputs.ContainsKey(id))) throw Refuse();
        foreach (var node in nodes.Values) WarmDesignTimeProject.ValidateAcquisitionGlobals(node.GlobalProperties);
        if (outputs.Values.Any(output => string.IsNullOrWhiteSpace(output.Output) || !Path.IsPathFullyQualified(output.Output) ||
            output.Reference is not null && !Path.IsPathFullyQualified(output.Reference))) throw Refuse();
        Workspace = new();
    }

    internal void Remember(string graphId, Project project)
    {
        if (!nodes.TryGetValue(graphId, out var node) ||
            !PathIdentityPolicy.Current.Comparer.Equals(node.ProjectPath, project.FilePath) ||
            !PathIdentityPolicy.Current.Comparer.Equals(outputs[graphId].Output, project.OutputFilePath) ||
            outputs[graphId].Reference is not null && !PathIdentityPolicy.Current.Comparer.Equals(outputs[graphId].Reference, project.OutputRefFilePath) ||
            !projects.TryAdd(graphId, project.Id)) throw Refuse();
    }

    internal ProjectReference? Resolve(string graphId, string path, string? sourceProject,
        MetadataReferenceProperties properties, string? referenceSourceTarget = null)
    {
        var paths = PathIdentityPolicy.Current.Comparer;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id)
        {
            foreach (var dependency in nodes[id].Dependencies)
                if (allowed.Add(dependency)) Add(dependency);
        }
        Add(graphId);
        var physicalOwners = outputs.Where(pair => paths.Equals(pair.Value.Output, path) || paths.Equals(pair.Value.Reference, path)).ToArray();
        var owners = physicalOwners.Where(pair => allowed.Contains(pair.Key)).ToArray();
        if (owners.Length == 0)
        {
            if (physicalOwners.Length == 0 && referenceSourceTarget == "ProjectReference" &&
                !string.IsNullOrWhiteSpace(sourceProject) && Path.IsPathFullyQualified(sourceProject) &&
                allowed.Any(id => paths.Equals(nodes[id].ProjectPath, Path.GetFullPath(sourceProject))))
            {
                // SDK transitive reference discovery can declare a different
                // output/configuration from the required graph. Preserve that
                // exact path as incomplete; never substitute a mapped producer.
                unresolvedReferences.Add(path);
                return null;
            }
            if (physicalOwners.Length > 0 || !string.IsNullOrWhiteSpace(sourceProject)) throw Refuse();
            return null;
        }
        if (string.IsNullOrWhiteSpace(sourceProject) || !Path.IsPathFullyQualified(sourceProject)) throw Refuse();
        if (owners.Length > 1)
        {
            if (owners.Any(owner => !paths.Equals(nodes[owner.Key].ProjectPath, Path.GetFullPath(sourceProject)))) throw Refuse();
            return null; // Explicitly retained as unresolved, never select a producer.
        }
        var owner = owners[0].Key;
        if (!paths.Equals(nodes[owner].ProjectPath, Path.GetFullPath(sourceProject))) throw Refuse();
        if (!allowed.Contains(owner) || !projects.TryGetValue(owner, out var projectId)) throw Refuse();
        return new(projectId, properties.Aliases, properties.EmbedInteropTypes);
    }

    public void Dispose() => Workspace.Dispose();
    internal bool IsUnresolvedReference(string path) => unresolvedReferences.Contains(path);
    internal bool IsGraphOutput(string path) => outputs.Values.Any(output =>
        PathIdentityPolicy.Current.Comparer.Equals(output.Output, path) || PathIdentityPolicy.Current.Comparer.Equals(output.Reference, path));
    private static ProjectContextException Refuse() => new("context.projectReferenceUnproven",
        "Executed project references require unique SDK graph owners, mapped output paths and source-project metadata.");
}
