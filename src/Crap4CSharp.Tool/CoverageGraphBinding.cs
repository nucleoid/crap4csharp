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

// Saved observations have no owned raw execution attachment or process result.
internal sealed record BoundSavedInvocationCoverage(string GraphId, IReadOnlyList<BoundModuleCoverage> Modules);
internal sealed record BoundSavedGraphCoverage(IReadOnlyList<BoundSavedInvocationCoverage> Invocations,
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
                var context = selected.Context;
                var files = module.Elements("Files").Single().Elements().ToArray();
                var rawMethods = module.Descendants("Method").ToArray();
                // Preserve the original pre-empty-module shape guards as well as
                // the shared normalized nonempty-module checks below.
                if (files.Any(f => f.Name != "File") || files.Length != module.Descendants().Count(e => e.Name.LocalName == "File") ||
                    rawMethods.Length != module.Descendants().Count(e => e.Name.LocalName == "Method") ||
                    rawMethods.Length > TestResultBinding.MaximumTests ||
                    context.CompilerDocuments.Select(d => d.LogicalPath).Distinct(paths).Count() != context.CompilerDocuments.Count) throw Refuse();
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
                stage = "normalized-module";
                var matched = ValidateNormalizedModule(bytes, selected.Id, context, selected.Build, token);
                covered.Add(selected.Id);
                moduleBindings.Add(matched);
            }
            // Empty/no-point modules are not inferred eligible from a name. Require module
            // evidence only for eligible source inventory with real compiled sequence points.
            stage = "eligibility";
            ValidateClosurePresence(builds.Values, bindings.Where(p => p.Value.IsTestProject &&
                !p.Value.Context.ExclusionPolicy.IncludeTests).Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
                builds.Values.Where(b => names.Contains(b.Build.ModuleIdentity)).Select(b => b.Id).ToHashSet(StringComparer.Ordinal), token);
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
            var eligibleGraphIds = ValidateEligibleUnion(covered, bound, token);
            bound.RetainedProof.ValidateCurrent(token);
            return new(Array.AsReadOnly(reports.ToArray()), eligibleGraphIds);
        }
        catch (ProjectContextException error) when (error.Reason != "coverage.aggregateUnproven")
        { throw AggregateRefuse("Prerequisite refused: " + error.Reason + ". " + error.Message); }
        catch (ProjectContextException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or KeyNotFoundException or OverflowException or System.Xml.XmlException)
        { throw AggregateRefuse(); }
    }

    // Actual fresh line projections and saved normalized bytes use this SAME conservative
    // module/document/method/PDB matcher. This never reconstructs a raw collector report.
    internal static BoundModuleCoverage ValidateNormalizedModule(ImmutableArray<byte> bytes, string graphId,
        ProjectAnalysisContext context, InspectedBuildEvidence build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = TestResultBinding.Parse(bytes).Root ?? throw Refuse();
        if (root.Name != "CoverageSession" || root.Elements().Count() != 1) throw Refuse();
        var module = root.Elements("Modules").Single().Elements("Module").Single();
        if (root.Descendants().Count(e => e.Name.LocalName == "Module") != 1 ||
            root.Element("Modules")!.Elements().Count() != 1 ||
            module.Elements("ModuleName").Single().Value != build.ModuleIdentity) throw Refuse();
        var paths = PathIdentityPolicy.Current.Comparer;
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
        {
            var physical = Required(file, "fullPath");
            // A normalized capture must retain the actual physical compiler document,
            // not silently accept a resealed alternate logical/path-root declaration.
            if (!Path.IsPathFullyQualified(physical) || !context.CompilerDocuments.Any(d =>
                paths.Equals(d.ResolvedPath!.Replace('\\', '/'), physical.Replace('\\', '/'))) ||
                !sources.Contains(pathIndex.Resolve(physical), StringComparer.Ordinal)) throw Refuse();
        }
        var rawMethods = module.Descendants("Method").ToArray();
        if (rawMethods.Length is 0 or > TestResultBinding.MaximumTests ||
            module.Descendants().Count(e => e.Name.LocalName == "Method") != rawMethods.Length ||
            rawMethods.Select(m => m.Elements("Name").Single().Value).Distinct(StringComparer.Ordinal).Count() != rawMethods.Length ||
            root.Descendants().Count(e => e.Name.LocalName == "SequencePoint") !=
                rawMethods.Sum(m => m.Descendants().Count(e => e.Name.LocalName == "SequencePoint"))) throw Refuse();
        var methods = CoverageReader.Read(bytes.AsSpan(), "coverage.opencover.xml");
        if (methods.Count != rawMethods.Length) throw Refuse();
        for (var i = 0; i < rawMethods.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var raw = rawMethods[i]; var method = methods[i];
            var fileId = Required(raw.Descendants("FileRef").Single(), "uid");
            if (!files.Any(f => Required(f, "uid") == fileId) || raw.Attribute("metadataToken") is not null ||
                raw.Elements("MetadataToken").Single().Value.Length != 0 || method.SequencePoints.Count == 0 ||
                raw.Descendants().Count(e => e.Name.LocalName == "SequencePoint") != method.SequencePoints.Count) throw Refuse();
            foreach (var point in raw.Descendants("SequencePoint"))
                if (Required(point, "fileid") != fileId || point.Attribute("sc") is not null || point.Attribute("ec") is not null ||
                    point.Attribute("el") is not null || point.Attribute("offset") is not null ||
                    !int.TryParse(Required(point, "sl"), NumberStyles.None, CultureInfo.InvariantCulture, out var line) ||
                    line <= 0 || line >= 0xFEEFEE ||
                    !int.TryParse(Required(point, "vc"), NumberStyles.None, CultureInfo.InvariantCulture, out var visits) || visits < 0) throw Refuse();
        }
        var matched = ArtifactEvidenceInspector.MatchedMethodTokens(bytes, "coverage.opencover.xml", build, sources, policy, token)
            ?? throw Refuse();
        return new(graphId, matched, bytes);
    }

    internal static BoundSavedGraphCoverage ValidateSavedNormalized(ArtifactBundle bundle, CheckExecutionPlan current,
        CompilerGraphPhaseResult bound, string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var inventory = SavedRequiredTestResults.Validate(bundle, current, bound, root, token);
            return ValidateSavedNormalizedCore(bundle, bound, token, inventory);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is PolicyException or ProjectContextException or IOException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or
            OverflowException or System.Xml.XmlException) { throw SavedRefuse(); }
    }

    // Fresh required-results refusal precedes the coverage wrapper, just as in
    // the original ordered boundary. Own these proofs here; accept no caller proof.
    internal static (IReadOnlyList<SavedRequiredTest> Required, BoundSavedGraphCoverage Coverage)
        ValidateFreshRequiredAndNormalized(ArtifactBundle bundle, CheckExecutionPlan current,
            CompilerGraphPhaseResult bound, string root, CancellationToken token,
            CompletedProofTiming? timing, string boundary)
    {
        IReadOnlyList<SavedRequiredTest> inventory;
        using (timing?.Start(boundary + ".saved.requiredResults"))
            inventory = SavedRequiredTestResults.Validate(bundle, current, bound, root, token);
        using (timing?.Start(boundary + ".saved.coverage"))
            return (inventory, ValidateSavedNormalizedCore(bundle, bound, token, inventory));
    }

    private static BoundSavedGraphCoverage ValidateSavedNormalizedCore(ArtifactBundle bundle,
        CompilerGraphPhaseResult bound, CancellationToken token, IReadOnlyList<SavedRequiredTest> inventory)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var nodes = CompilerGraphExecution.ValidateAndFreeze(bound.FinalGraph).ToDictionary(n => n.Id, StringComparer.Ordinal);
            var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var observations = bound.Observations.ToDictionary(o => o.ProjectPath, PathIdentityPolicy.Current.Comparer);
            if (bindings.Values.Select(b => b.Context.AssemblyName).Distinct(StringComparer.Ordinal).Count() != bindings.Count)
                throw SavedRefuse();
            var reports = new List<BoundSavedInvocationCoverage>();
            var covered = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            long total = 0;
            foreach (var saved in inventory)
            {
                token.ThrowIfCancellationRequested();
                var closure = new HashSet<string>(StringComparer.Ordinal);
                void Add(string id)
                {
                    if (!closure.Add(id)) return;
                    foreach (var dependency in nodes[id].Dependencies) Add(dependency);
                }
                Add(saved.GraphId);
                var builds = new Dictionary<string, (string Id, ProjectAnalysisContext Context, InspectedBuildEvidence Build)>(StringComparer.Ordinal);
                foreach (var id in closure)
                {
                    token.ThrowIfCancellationRequested();
                    var binding = bindings[id]; var observed = observations[binding.ProjectPath];
                    CompilerOutputBinding.Validate(binding.Context, observed, token);
                    var pe = CompilerOutputBinding.Read(observed.Assembly!.Path, ref total, token);
                    var pdb = CompilerOutputBinding.Read(observed.Pdb!.Path, ref total, token);
                    CompilerOutputBinding.Match(observed.Assembly, pe); CompilerOutputBinding.Match(observed.Pdb, pdb);
                    builds.Add(id, (id, binding.Context, ArtifactEvidenceInspector.InspectBuild(pe, pdb)));
                }
                var modules = new List<BoundModuleCoverage>();
                foreach (var module in saved.Modules)
                {
                    token.ThrowIfCancellationRequested();
                    if (!closure.Contains(module.GraphId)) throw SavedRefuse();
                    var selected = builds[module.GraphId];
                    var artifact = bundle.Manifest.Artifacts.Single(a => a.Id == module.Artifact);
                    var matched = ValidateNormalizedModule(bundle.Bytes[artifact.Locator], module.GraphId, selected.Context,
                        selected.Build, token);
                    modules.Add(matched);
                    if (!covered.TryGetValue(module.GraphId, out var tokens)) covered.Add(module.GraphId, tokens = []);
                    tokens.UnionWith(matched.ActualMethodTokens);
                }
                ValidateClosurePresence(builds.Values, bindings.Where(p => p.Value.IsTestProject &&
                    !p.Value.Context.ExclusionPolicy.IncludeTests).Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
                    modules.Select(m => m.GraphId).ToHashSet(StringComparer.Ordinal), token);
                reports.Add(new(saved.GraphId, Array.AsReadOnly(modules.ToArray())));
            }
            var eligible = ValidateEligibleUnion(covered, bound, token);
            bound.RetainedProof!.ValidateCurrent(token);
            return new(Array.AsReadOnly(reports.ToArray()), eligible);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is PolicyException or ProjectContextException or IOException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or
            OverflowException or System.Xml.XmlException) { throw SavedRefuse(); }
    }

    private static void ValidateClosurePresence(
        IEnumerable<(string Id, ProjectAnalysisContext Context, InspectedBuildEvidence Build)> builds,
        IReadOnlySet<string> excludedTests, IReadOnlySet<string> represented, CancellationToken token)
    {
        var paths = PathIdentityPolicy.Current.Comparer;
        foreach (var selected in builds)
        {
            token.ThrowIfCancellationRequested();
            if (excludedTests.Contains(selected.Id)) continue;
            if (selected.Context.Sources.Any(s => string.IsNullOrWhiteSpace(s.ResolvedPath) ||
                !Path.IsPathFullyQualified(s.ResolvedPath) || Path.GetFullPath(s.ResolvedPath) != s.ResolvedPath)) throw Refuse();
            var eligible = selected.Context.Sources.Select(s => s.ResolvedPath!.Replace('\\', '/')).ToHashSet(paths);
            if (selected.Build.Methods.Any(m => m.SequencePoints.Any(p => eligible.Contains(p.Document))) &&
                !represented.Contains(selected.Id)) throw Refuse();
        }
    }

    private static IReadOnlyList<string> ValidateEligibleUnion(IReadOnlyDictionary<string, HashSet<int>> covered,
        CompilerGraphPhaseResult bound, CancellationToken token)
    {
        var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var observations = bound.Observations.ToDictionary(o => o.ProjectPath, paths);
        if (observations.Count != bindings.Count) throw AggregateRefuse();
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
        return Array.AsReadOnly(eligibleGraphIds.Order(StringComparer.Ordinal).ToArray());
    }

    private static PolicyException SavedRefuse() => new("provenance.savedCoverageUnproven",
        "Saved normalized coverage module reachability, actual compiler/PDB/path/point associations or complete eligible method union are unproven.");

    private static ProjectContextException AggregateRefuse(string? detail = null) => new("coverage.aggregateUnproven",
        "Required successful invocation membership and complete eligible compiler/PDB method coverage are not independently bound across the graph." +
        (detail is null ? "" : " " + detail));

    private static string Required(XElement element, string attribute) => element.Attribute(attribute)?.Value is { Length: > 0 } value ? value : throw Refuse();
    private static ProjectContextException Refuse() => new("coverage.graphUnproven", "Owned OpenCover modules, metadata tokens, files or sequence points do not match the independently built invocation dependency contexts, or eligible module evidence is missing.");
}
