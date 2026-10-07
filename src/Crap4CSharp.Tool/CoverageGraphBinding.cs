using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

// Private coverage associations, not runtime loading or reusable-recipe attestation.
internal sealed record BoundModuleCoverage(string GraphId, IReadOnlyList<int> ActualMethodTokens,
    ImmutableArray<byte> LineProjection);
internal sealed record BoundInvocationCoverage(string GraphId, OwnedOpenCover Attachment,
    IReadOnlyList<string> ModuleNames, IReadOnlyList<string> CoveredGraphIds,
    IReadOnlyList<BoundModuleCoverage> Modules);
internal sealed record BoundGraphCoverage(IReadOnlyList<BoundInvocationCoverage> Invocations,
    IReadOnlyList<string> EligibleGraphIds);

internal static class CoverageGraphBinding
{
    internal static BoundInvocationCoverage ValidateInvocation(TestInvocationObservation invocation,
        CompilerGraphPhaseResult bound, CancellationToken token)
        => ValidateInvocationCore(invocation, bound, token, false);

    internal static BoundInvocationCoverage ValidateFailedInvocation(TestInvocationObservation invocation,
        CompilerGraphPhaseResult bound, CancellationToken token)
        => ValidateInvocationCore(invocation, bound, token, true);

    private static BoundInvocationCoverage ValidateInvocationCore(TestInvocationObservation invocation,
        CompilerGraphPhaseResult bound, CancellationToken token, bool failed)
    {
        token.ThrowIfCancellationRequested();
        var stage = "graph";
        try
        {
            if (invocation.ChildBuild is null) throw Refuse();
            TestChildEvidence.Validate(invocation.ChildBuild, invocation.Process);
            var childPaths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (!childPaths.Equals(invocation.ChildBuild.ProjectPath, invocation.ProjectPath)) throw Refuse();
            var nodes = CompilerGraphExecution.ValidateAndFreeze(bound.FinalGraph);
            var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var test = bindings[invocation.GraphId];
            if (!test.IsTestProject || !childPaths.Equals(invocation.ProjectPath, test.ProjectPath) || bound.Observations.Count != nodes.Count ||
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
            var attachment = failed ? OwnedCoverageAttachment.ReadFailedOpenCover(invocation, test.Context,
                observations[test.ProjectPath], token) : OwnedCoverageAttachment.ReadOpenCover(invocation, test.Context,
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
            var moduleBindings = new List<BoundModuleCoverage>();
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
                if (rawMethods.Length == 0)
                {
                    // Actual Coverlet can emit a named empty module. It is not zero
                    // coverage: accept only independently empty executable inventory.
                    if (files.Length != 0 || module.Descendants("SequencePoint").Any()) throw Refuse();
                    var capturedSources = context.CompilerDocuments.Select(d =>
                    {
                        var content = CompilerOutputBinding.Read(d.ResolvedPath!, ref total, token);
                        if (CanonicalIdentity.Sha256(content.AsSpan()) != d.ContentIdentity) throw Refuse();
                        return CapturedSource.Create(d.LogicalPath, content.AsSpan());
                    }).ToArray();
                    var eligiblePaths = context.Sources.Select(s => s.LogicalPath).ToHashSet(StringComparer.Ordinal);
                    var references = observations[bindings[selected.Id].ProjectPath].Parameters.Single(p => p.Name == "References").Items
                        .Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(CompilerOutputBinding.Read(p, ref total, token))).ToArray();
                    if (SemanticEligibility.HasApplicable(capturedSources.Where(s => eligiblePaths.Contains(s.LogicalPath)).ToArray(),
                        capturedSources, new CallableAnalysisContext(context.ProjectPath, context.TargetFramework, context.Configuration,
                            context.Platform, selected.Id, context.ParseOptions), references)) throw Refuse();
                    continue; // Do not manufacture coverage/module token observations.
                }
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
                var matched = ArtifactEvidenceInspector.MatchedMethodTokens(bytes, "coverage.opencover.xml",
                    selected.Build, sources, policy, token) ?? throw Refuse();
                covered.Add(selected.Id);
                moduleBindings.Add(new(selected.Id, matched, bytes));
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
            return new(invocation.GraphId, attachment, Array.AsReadOnly(names.Order(StringComparer.Ordinal).ToArray()),
                Array.AsReadOnly(covered.ToArray()), Array.AsReadOnly(moduleBindings.ToArray()));
        }
        catch (ProjectContextException error) when (error.Reason != "coverage.graphUnproven")
        { throw new ProjectContextException("coverage.graphUnproven", "Independent coverage binding prerequisite refused: " + error.Reason); }
        catch (ProjectContextException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or System.Xml.XmlException)
        { throw new ProjectContextException("coverage.graphUnproven", "Coverage binding refused at " + stage + ": " + error.GetType().Name); }
    }

    internal static BoundGraphCoverage ValidateAggregate(TestExecutionRun run,
        CompilerGraphPhaseResult bound, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (run.Failure is not null || bound.RetainedProof is null) throw AggregateRefuse();
            bound.RetainedProof.ValidateCurrent(token);
            var nodes = CompilerGraphExecution.ValidateAndFreeze(bound.FinalGraph);
            var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var required = bindings.Values.Where(b => b.IsTestProject).Select(b => b.GraphId).ToHashSet(StringComparer.Ordinal);
            if (run.Count != required.Count || run.Outcomes.Count != required.Count ||
                run.Select(i => i.GraphId).Distinct(StringComparer.Ordinal).Count() != run.Count ||
                run.Outcomes.Select(o => o.GraphId).Distinct(StringComparer.Ordinal).Count() != run.Outcomes.Count ||
                !required.SetEquals(run.Select(i => i.GraphId)) || !required.SetEquals(run.Outcomes.Select(o => o.GraphId))) throw AggregateRefuse();
            var outcomes = run.Outcomes.ToDictionary(o => o.GraphId, StringComparer.Ordinal);
            var reports = new List<BoundInvocationCoverage>();
            var covered = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var invocation in run)
            {
                token.ThrowIfCancellationRequested();
                var outcome = outcomes[invocation.GraphId];
                if (outcome.State != "completed" || outcome.Process != invocation.Process ||
                    outcome.ResultsDirectory != invocation.ResultsDirectory || invocation.Process.ExitCode != 0) throw AggregateRefuse();
                var report = ValidateInvocation(invocation, bound, token);
                reports.Add(report);
                foreach (var module in report.Modules)
                {
                    if (!covered.TryGetValue(module.GraphId, out var tokens)) covered.Add(module.GraphId, tokens = []);
                    // Union membership only. Repeated reports never sum denominators or
                    // infer visits for missing methods; original per-module observations remain.
                    tokens.UnionWith(module.ActualMethodTokens);
                }
            }
            var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var observations = bound.Observations.ToDictionary(o => o.ProjectPath, paths);
            if (observations.Count != nodes.Count) throw AggregateRefuse();
            var eligibleGraphIds = new List<string>();
            long total = 0;
            foreach (var binding in bindings.Values)
            {
                token.ThrowIfCancellationRequested();
                if (binding.IsTestProject && !binding.Context.ExclusionPolicy.IncludeTests) continue;
                var context = binding.Context;
                if (context.Sources.Any(s => string.IsNullOrWhiteSpace(s.ResolvedPath) ||
                    !Path.IsPathFullyQualified(s.ResolvedPath) || Path.GetFullPath(s.ResolvedPath) != s.ResolvedPath)) throw AggregateRefuse();
                var eligible = context.Sources.Select(s => s.ResolvedPath!.Replace('\\', '/')).ToHashSet(paths);
                var compiler = observations[binding.ProjectPath];
                CompilerOutputBinding.Validate(context, compiler, token);
                var pe = CompilerOutputBinding.Read(compiler.Assembly!.Path, ref total, token);
                var pdb = CompilerOutputBinding.Read(compiler.Pdb!.Path, ref total, token);
                CompilerOutputBinding.Match(compiler.Assembly, pe); CompilerOutputBinding.Match(compiler.Pdb, pdb);
                var build = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                var methods = build.Methods.Where(m => m.SequencePoints.Any(p => eligible.Contains(p.Document)))
                    .Select(m => m.MetadataToken).ToHashSet();
                if (methods.Count == 0) continue;
                eligibleGraphIds.Add(binding.GraphId);
                if (!covered.TryGetValue(binding.GraphId, out var represented) || !methods.IsSubsetOf(represented))
                    throw AggregateRefuse("Missing eligible methods for " + context.AssemblyName + ": " +
                        string.Join(",", build.Methods.Where(m => methods.Contains(m.MetadataToken) &&
                            (represented is null || !represented.Contains(m.MetadataToken))).Take(8).Select(m => m.TypeName + "::" + m.MethodName)));
            }
            bound.RetainedProof.ValidateCurrent(token);
            return new(Array.AsReadOnly(reports.ToArray()), Array.AsReadOnly(eligibleGraphIds.Order(StringComparer.Ordinal).ToArray()));
        }
        catch (ProjectContextException error) when (error.Reason != "coverage.aggregateUnproven")
        { throw AggregateRefuse("Prerequisite refused: " + error.Reason + ". " + error.Message); }
        catch (ProjectContextException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or KeyNotFoundException or OverflowException or System.Xml.XmlException)
        { throw AggregateRefuse(); }
    }

    private static ProjectContextException AggregateRefuse(string? detail = null) => new("coverage.aggregateUnproven",
        "Required successful invocation membership and complete eligible compiler/PDB method coverage are not independently bound across the graph." +
        (detail is null ? "" : " " + detail));

    private static string Required(XElement element, string attribute) => element.Attribute(attribute)?.Value is { Length: > 0 } value ? value : throw Refuse();
    private static ProjectContextException Refuse() => new("coverage.graphUnproven", "Owned OpenCover modules, metadata tokens, files or sequence points do not match the independently built invocation dependency contexts, or eligible module evidence is missing.");
}
