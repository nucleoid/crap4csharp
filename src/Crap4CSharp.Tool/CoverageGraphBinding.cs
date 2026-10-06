using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

// Private invocation/dependency association, not aggregate graph coverage or phase-A continuity.
internal sealed record BoundInvocationCoverage(string GraphId, OwnedOpenCover Attachment,
    IReadOnlyList<string> ModuleNames, IReadOnlyList<string> CoveredGraphIds);

internal static class CoverageGraphBinding
{
    internal static BoundInvocationCoverage ValidateInvocation(TestInvocationObservation invocation,
        CompilerGraphPhaseResult bound, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stage = "graph";
        try
        {
            var nodes = CompilerGraphExecution.ValidateAndFreeze(bound.FinalGraph);
            var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var test = bindings[invocation.GraphId];
            if (!test.IsTestProject || bound.Observations.Count != nodes.Count ||
                bindings.Values.Select(b => b.Context.AssemblyName).Distinct(StringComparer.Ordinal).Count() != nodes.Count) throw Refuse();
            var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var observations = bound.Observations.ToDictionary(o => o.ProjectPath, paths);
            var closure = new HashSet<string>(StringComparer.Ordinal);
            void Add(string id)
            {
                if (!closure.Add(id)) return;
                foreach (var dependency in nodes.Single(n => n.Id == id).Dependencies) Add(dependency);
            }
            Add(invocation.GraphId);
            stage = "attachment";
            var attachment = OwnedCoverageAttachment.ReadOpenCover(invocation, test.Context,
                observations[test.ProjectPath], token);
            var builds = new Dictionary<string, (string Id, ProjectAnalysisContext Context, InspectedBuildEvidence Build)>(StringComparer.Ordinal);
            long total = 0;
            foreach (var id in closure)
            {
                token.ThrowIfCancellationRequested();
                var binding = bindings[id]; var observation = observations[binding.ProjectPath];
                stage = "compiler-output";
                CompilerOutputBinding.Validate(binding.Context, observation, token);
                var pe = CompilerOutputBinding.Read(observation.Assembly!.Path, ref total, token);
                var pdb = CompilerOutputBinding.Read(observation.Pdb!.Path, ref total, token);
                CompilerOutputBinding.Match(observation.Assembly, pe); CompilerOutputBinding.Match(observation.Pdb, pdb);
                var build = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                builds.Add(build.ModuleIdentity, (id, binding.Context, build));
            }
            stage = "module-xml";
            var root = TestResultBinding.Parse(attachment.Bytes).Root ?? throw Refuse();
            var modules = root.Elements("Modules").Single().Elements().ToArray();
            if (modules.Any(m => m.Name != "Module") || root.Descendants().Count(e => e.Name.LocalName == "Module") != modules.Length ||
                modules.Length > nodes.Count ||
                root.Descendants().Count(e => e.Name.LocalName == "Method") != modules.Sum(m => m.Descendants().Count(e => e.Name.LocalName == "Method")) ||
                root.Descendants().Count(e => e.Name.LocalName == "File") != modules.Sum(m => m.Descendants().Count(e => e.Name.LocalName == "File"))) throw Refuse();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var covered = new List<string>();
            foreach (var module in modules)
            {
                token.ThrowIfCancellationRequested();
                var name = module.Elements("ModuleName").Single().Value;
                if (!names.Add(name) || !builds.TryGetValue(name, out var selected)) throw Refuse();
                stage = "document-mappings";
                var context = selected.Context;
                var sources = context.CompilerDocuments.Select(d => d.LogicalPath).ToArray();
                if (sources.Distinct(paths).Count() != sources.Length) throw Refuse();
                var mappings = context.CompilerDocuments.Select(d => new ManifestReportRootMapping(d.ResolvedPath!, d.LogicalPath)).ToArray();
                var policy = new CapturedPathPolicy(!OperatingSystem.IsWindows(), mappings);
                var pathIndex = new CapturedLogicalPathIndex(sources, policy);
                var files = module.Elements("Files").Single().Elements().ToArray();
                if (files.Any(f => f.Name != "File") || files.Length != module.Descendants().Count(e => e.Name.LocalName == "File") ||
                    files.Select(f => Required(f, "uid")).Distinct(StringComparer.Ordinal).Count() != files.Length ||
                    files.Select(f => Required(f, "fullPath")).Distinct(paths).Count() != files.Length) throw Refuse();
                foreach (var file in files)
                    if (!sources.Contains(pathIndex.Resolve(Required(file, "fullPath")), StringComparer.Ordinal)) throw Refuse();
                stage = "method-xml";
                var rawMethods = module.Descendants("Method").ToArray();
                if (module.Descendants().Count(e => e.Name.LocalName == "Method") != rawMethods.Length || rawMethods.Length > TestResultBinding.MaximumTests ||
                    rawMethods.Select(m => m.Elements("Name").Single().Value).Distinct(StringComparer.Ordinal).Count() != rawMethods.Length) throw Refuse();
                stage = "point-projection";
                var singleModule = new XDocument(new XElement("CoverageSession", new XElement("Modules", new XElement(module))));
                // Coverlet's OpenCover writer projects line observations with dummy
                // columns 1..2 and no IL offset. Require that proven writer shape;
                // normalize ONLY the copied parse document to line evidence. Never
                // label these synthesized columns as exact PDB-column attestation.
                foreach (var point in singleModule.Descendants("SequencePoint"))
                {
                    if (Required(point, "sc") != "1" || Required(point, "ec") != "2" ||
                        Required(point, "el") != Required(point, "sl") || point.Attribute("offset") is not null ||
                        !int.TryParse(Required(point, "vc"), NumberStyles.None, CultureInfo.InvariantCulture, out var visits) || visits < 0) throw Refuse();
                    point.Attribute("sc")!.Remove(); point.Attribute("ec")!.Remove(); point.Attribute("el")!.Remove();
                }
                var bytes = ImmutableArray.Create(System.Text.Encoding.UTF8.GetBytes(singleModule.ToString(SaveOptions.DisableFormatting)));
                stage = "coverage-reader";
                var methods = CoverageReader.Read(bytes.AsSpan(), "coverage.opencover.xml");
                if (methods.Count != rawMethods.Length || methods.Count == 0) throw Refuse();
                stage = "method-validation";
                for (var i = 0; i < rawMethods.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var raw = rawMethods[i]; var method = methods[i];
                    // Coverlet 6.0.4 emits an EMPTY MetadataToken. Do not pretend its
                    // decimal/hex declaration is an actual observed compiler token.
                    // Supported empty tokens bind by unique actual name/PDB points;
                    // other declarations conservatively require a future proven adapter.
                    var fileRef = raw.Descendants("FileRef").Single();
                    var fileId = Required(fileRef, "uid");
                    if (!files.Any(f => Required(f, "uid") == fileId) ||
                        raw.Descendants("SequencePoint").Any(p => Required(p, "fileid") != fileId) ||
                        raw.Attribute("metadataToken") is not null ||
                        raw.Elements("MetadataToken").Single().Value.Length != 0 ||
                        method.SequencePoints.Count == 0 ||
                        raw.Descendants().Count(e => e.Name.LocalName == "SequencePoint") != method.SequencePoints.Count) throw Refuse();
                }
                stage = "pdb-match";
                if (!ArtifactEvidenceInspector.CoverageMatchesBuildCancellable(bytes, "coverage.opencover.xml",
                    selected.Build, sources, policy, token)) throw Refuse();
                covered.Add(selected.Id);
            }
            // Empty/no-point modules are not inferred eligible from a name. Require module
            // evidence only for eligible source inventory with real compiled sequence points.
            stage = "eligibility";
            foreach (var (name, selected) in builds)
            {
                // Required test contexts retain complete physical sources for compiler
                // provenance even when the policy excludes tests from CRAP coverage.
                // Collector/test success is mandatory independently of eligibility.
                if (bindings[selected.Id].IsTestProject && !selected.Context.ExclusionPolicy.IncludeTests) continue;
                if (selected.Context.Sources.Any(s => string.IsNullOrWhiteSpace(s.ResolvedPath) ||
                    !Path.IsPathFullyQualified(s.ResolvedPath) || Path.GetFullPath(s.ResolvedPath) != s.ResolvedPath)) throw Refuse();
                var eligible = selected.Context.Sources.Select(s => s.ResolvedPath!.Replace('\\', '/')).ToHashSet(paths);
                if (selected.Build.Methods.Any(m => m.SequencePoints.Any(p => eligible.Contains(p.Document))) && !names.Contains(name)) throw Refuse();
            }
            token.ThrowIfCancellationRequested();
            return new(invocation.GraphId, attachment, Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(covered.ToArray()));
        }
        catch (ProjectContextException error) when (error.Reason != "coverage.graphUnproven")
        { throw new ProjectContextException("coverage.graphUnproven", "Independent coverage binding prerequisite refused: " + error.Reason); }
        catch (ProjectContextException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or System.Xml.XmlException)
        { throw new ProjectContextException("coverage.graphUnproven", "Coverage binding refused at " + stage + ": " + error.GetType().Name); }
    }

    private static string Required(XElement element, string attribute) => element.Attribute(attribute)?.Value is { Length: > 0 } value ? value : throw Refuse();
    private static ProjectContextException Refuse() => new("coverage.graphUnproven", "Owned OpenCover modules, metadata tokens, files or sequence points do not match the independently built invocation dependency contexts, or eligible module evidence is missing.");
}
