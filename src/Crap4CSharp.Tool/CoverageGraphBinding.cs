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
        CompilerGraphPhaseResult bound, CancellationToken token, bool failed, CoverageGateScope? scope = null)
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
            if (!test.IsTestProject || !childPaths.Equals(invocation.ProjectPath, test.ProjectPath) || bound.Observations.Count != nodes.Count) throw Refuse();
            var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var observations = bound.ByGraphId();
            var closure = CompilerReferenceGraph.Closure(CompilerReferenceGraph.Observe(bound.FinalGraph, token), invocation.GraphId);
            if (closure.Select(id => bindings[id].Context.AssemblyName).Distinct(StringComparer.Ordinal).Count() != closure.Count) throw Refuse();
            stage = "attachment";
            var attachment = failed ? OwnedCoverageAttachment.ReadFailedOpenCover(invocation, test.Context,
                observations[test.GraphId], token) : OwnedCoverageAttachment.ReadOpenCover(invocation, test.Context,
                observations[test.GraphId], token);
            var builds = new Dictionary<string, (string Id, ProjectAnalysisContext Context, InspectedBuildEvidence Build)>(StringComparer.Ordinal);
            long total = 0;
            foreach (var id in closure)
            {
                token.ThrowIfCancellationRequested();
                var binding = bindings[id]; var observation = observations[binding.GraphId];
                stage = "compiler-output";
                var output = ReadOutput(id, binding.Context, observation, ref total, token, scope);
                var build = output.Build;
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
                var documentBinding = CompilerDocumentBinding.Create(context, observations[selected.Id], selected.Build, token);
                // Preserve raw collector bytes. Only the copied normalized module
                // translates an exact validated PDB/physical alias to its physical source.
                foreach (var file in singleModule.Descendants("File"))
                {
                    var reported = Required(file, "fullPath");
                    var physical = documentBinding.PhysicalPathForReport(reported);
                    if (!paths.Equals(reported.Replace('\\', '/'), physical.Replace('\\', '/')))
                        file.SetAttributeValue("fullPath", physical);
                }
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
                    var references = CompilerReferenceSnapshot.ResolveSemanticReferences(observations[selected.Id], token)
                        .Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(CompilerOutputBinding.Read(p.Path, ref total, token), p.Properties)).ToArray();
                    if (SemanticEligibility.HasApplicable(capturedSources.Where(s => eligiblePaths.Contains(s.LogicalPath)).ToArray(),
                        capturedSources, new CallableAnalysisContext(context.ProjectPath, context.TargetFramework, context.Configuration,
                            context.Platform, selected.Id, context.ParseOptions), references)) throw Refuse();
                    continue; // Do not manufacture coverage/module token observations.
                }
                stage = "normalized-module";
                var matched = ValidateNormalizedModule(bytes, selected.Id, context, selected.Build, observations[selected.Id], token);
                covered.Add(selected.Id);
                moduleBindings.Add(matched);
            }
            // Empty/no-point modules are not inferred eligible from a name. Require module
            // evidence only for eligible source inventory with real compiled sequence points.
            stage = "eligibility";
            ValidateClosurePresence(builds.Values, bindings.Where(p => p.Value.IsTestProject &&
                !p.Value.Context.ExclusionPolicy.IncludeTests).Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
                builds.Values.Where(b => names.Contains(b.Build.ModuleIdentity)).Select(b => b.Id).ToHashSet(StringComparer.Ordinal), observations, token);
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
            var scope = new CoverageGateScope();
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
                var report = ValidateInvocationCore(invocation, bound, token, false, scope);
                reports.Add(report);
                foreach (var module in report.Modules)
                {
                    if (!covered.TryGetValue(module.GraphId, out var tokens)) covered.Add(module.GraphId, tokens = []);
                    // Union membership only. Repeated reports never sum denominators or
                    // infer visits for missing methods; original per-module observations remain.
                    tokens.UnionWith(module.ActualMethodTokens);
                }
            }
            var eligibleGraphIds = ValidateEligibleUnion(covered, bound, token, scope);
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
        ProjectAnalysisContext context, InspectedBuildEvidence build, CompilerObservation observation, CancellationToken token)
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
        var mappings = CompilerDocumentBinding.Create(context, observation, build, token).ReportMappings;
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
            OverflowException or System.Xml.XmlException)
        { throw SavedRefuse(); }
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
            inventory = SavedRequiredTestResults.ValidateWithTiming(bundle, current, bound, root, token, timing, boundary);
        using (timing?.Start(boundary + ".saved.coverage"))
            return (inventory, ValidateSavedNormalizedCore(bundle, bound, token, inventory));
    }

    private static BoundSavedGraphCoverage ValidateSavedNormalizedCore(ArtifactBundle bundle,
        CompilerGraphPhaseResult bound, CancellationToken token, IReadOnlyList<SavedRequiredTest> inventory)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var scope = new CoverageGateScope();
            var nodes = CompilerGraphExecution.ValidateAndFreeze(bound.FinalGraph).ToDictionary(n => n.Id, StringComparer.Ordinal);
            var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
            var observations = bound.ByGraphId();
            var reports = new List<BoundSavedInvocationCoverage>();
            var covered = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            long total = 0;
            foreach (var saved in inventory)
            {
                token.ThrowIfCancellationRequested();
                var closure = CompilerReferenceGraph.Closure(CompilerReferenceGraph.Observe(bound.FinalGraph, token), saved.GraphId);
                if (closure.Select(id => bindings[id].Context.AssemblyName).Distinct(StringComparer.Ordinal).Count() != closure.Count) throw Refuse();
                var builds = new Dictionary<string, (string Id, ProjectAnalysisContext Context, InspectedBuildEvidence Build)>(StringComparer.Ordinal);
                foreach (var id in closure)
                {
                    token.ThrowIfCancellationRequested();
                    var binding = bindings[id]; var observed = observations[binding.GraphId];
                    var output = ReadOutput(id, binding.Context, observed, ref total, token, scope);
                    builds.Add(id, (id, binding.Context, output.Build));
                }
                var modules = new List<BoundModuleCoverage>();
                foreach (var module in saved.Modules)
                {
                    token.ThrowIfCancellationRequested();
                    if (!closure.Contains(module.GraphId)) throw SavedRefuse();
                    var selected = builds[module.GraphId];
                    var artifact = bundle.Manifest.Artifacts.Single(a => a.Id == module.Artifact);
                    var matched = ValidateNormalizedModule(bundle.Bytes[artifact.Locator], module.GraphId, selected.Context,
                        selected.Build, observations[module.GraphId], token);
                    modules.Add(matched);
                    if (!covered.TryGetValue(module.GraphId, out var tokens)) covered.Add(module.GraphId, tokens = []);
                    tokens.UnionWith(matched.ActualMethodTokens);
                }
                ValidateClosurePresence(builds.Values, bindings.Where(p => p.Value.IsTestProject &&
                    !p.Value.Context.ExclusionPolicy.IncludeTests).Select(p => p.Key).ToHashSet(StringComparer.Ordinal),
                    modules.Select(m => m.GraphId).ToHashSet(StringComparer.Ordinal), observations, token);
                reports.Add(new(saved.GraphId, Array.AsReadOnly(modules.ToArray())));
            }
            var eligible = ValidateEligibleUnion(covered, bound, token, scope);
            bound.RetainedProof!.ValidateCurrent(token);
            return new(Array.AsReadOnly(reports.ToArray()), eligible);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is PolicyException or ProjectContextException or IOException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or
            OverflowException or System.Xml.XmlException)
        { throw SavedRefuse(); }
    }

    private static void ValidateClosurePresence(
        IEnumerable<(string Id, ProjectAnalysisContext Context, InspectedBuildEvidence Build)> builds,
        IReadOnlySet<string> excludedTests, IReadOnlySet<string> represented,
        IReadOnlyDictionary<string, CompilerObservation> observations, CancellationToken token)
    {
        var paths = PathIdentityPolicy.Current.Comparer;
        foreach (var selected in builds)
        {
            token.ThrowIfCancellationRequested();
            if (excludedTests.Contains(selected.Id)) continue;
            if (selected.Context.Sources.Any(s => string.IsNullOrWhiteSpace(s.ResolvedPath) ||
                !Path.IsPathFullyQualified(s.ResolvedPath) || Path.GetFullPath(s.ResolvedPath) != s.ResolvedPath)) throw Refuse();
            var eligible = CompilerDocumentBinding.Create(selected.Context, observations[selected.Id], selected.Build, token)
                .EligiblePdbNames(selected.Context.Sources);
            if (represented.Contains(selected.Id)) continue;
            var methods = selected.Build.Methods.Where(m => m.SequencePoints.Any(p => eligible.Contains(p.Document))).ToArray();
            if (methods.Length == 0) continue;
            var optional = ClassifyGeneratedMethods(selected.Context, observations[selected.Id], selected.Build, token);
            if (methods.Any(method => !optional.Contains(method.MetadataToken))) throw Refuse();
        }
    }

    private static IReadOnlyList<string> ValidateEligibleUnion(IReadOnlyDictionary<string, HashSet<int>> covered,
        CompilerGraphPhaseResult bound, CancellationToken token, CoverageGateScope? scope = null)
    {
        var bindings = bound.FinalGraph.GraphContexts.ToDictionary(b => b.GraphId, StringComparer.Ordinal);
        var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var observations = bound.ByGraphId();
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
            var compiler = observations[binding.GraphId];
            var build = ReadOutput(binding.GraphId, context, compiler, ref total, token, scope).Build;
            var eligible = CompilerDocumentBinding.Create(context, compiler, build, token).EligiblePdbNames(context.Sources);
            var methods = build.Methods.Where(m => m.SequencePoints.Any(p => eligible.Contains(p.Document)))
                .Select(m => m.MetadataToken).ToHashSet();
            if (covered.TryGetValue(binding.GraphId, out var represented) && methods.IsSubsetOf(represented))
            {
                if (methods.Count > 0) eligibleGraphIds.Add(binding.GraphId);
                continue;
            }
            var optional = ClassifyGeneratedMethods(context, compiler, build, token);
            methods.ExceptWith(optional);
            if (methods.Count == 0) continue;
            eligibleGraphIds.Add(binding.GraphId);
            if (represented is null || !methods.IsSubsetOf(represented))
                throw AggregateRefuse("Missing eligible methods for " + context.AssemblyName + ": " +
                    string.Join(",", build.Methods.Where(m => methods.Contains(m.MetadataToken) &&
                        (represented is null || !represented.Contains(m.MetadataToken))).Take(8).Select(m => m.TypeName + "::" + m.MethodName)));
        }
        return Array.AsReadOnly(eligibleGraphIds.Order(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlySet<int> ClassifyGeneratedMethods(ProjectAnalysisContext context,
        CompilerObservation compiler, InspectedBuildEvidence build, CancellationToken token)
    {
        if (!build.Methods.Any(method => method.CompilerGenerated || method.MethodName == ".ctor")) return new HashSet<int>();
        long semanticBytes = 0;
        var trees = context.CompilerDocuments.Select(document =>
        {
            var bytes = CompilerOutputBinding.Read(document.ResolvedPath!, ref semanticBytes, token);
            if (CanonicalIdentity.Sha256(bytes.AsSpan()) != document.ContentIdentity) throw AggregateRefuse();
            var source = CapturedSource.Create(document.LogicalPath, bytes.AsSpan());
            return Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source.Text, context.ParseOptions,
                document.LogicalPath, cancellationToken: token);
        }).ToArray();
        var references = CompilerReferenceSnapshot.ResolveSemanticReferences(compiler, token).Select(reference =>
            Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(CompilerOutputBinding.Read(reference.Path, ref semanticBytes, token), reference.Properties)).ToArray();
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(context.AssemblyName, trees, references);
        var documents = CompilerDocumentBinding.Create(context, compiler, build, token).PdbLogicalPaths;
        return CompilerMethodEligibility.Classify(compilation, build, documents, token);
    }

    private static ValidatedCompilerOutput ReadOutput(string id, ProjectAnalysisContext context,
        CompilerObservation observation, ref long total, CancellationToken token, CoverageGateScope? scope)
    {
        token.ThrowIfCancellationRequested();
        var output = scope?.Get(id, context, observation, token)
            ?? CompilerOutputBinding.ValidateAndInspect(context, observation, token);
        // Preserve each caller's original closure cap, including cache hits.
        total = checked(total + output.Pe.Length + output.Pdb.Length);
        if (total > CompilerPhaseSnapshot.MaximumClosureBytes)
            throw new ProjectContextException("compiler.outputLimit", "Compiler output binding exceeded its closure byte limit.");
        return output;
    }

    // Local to the middle of one synchronous gate. Never passed to physical
    // opening/closing validation, retained, or shared with another invocation.
    private sealed class CoverageGateScope
    {
        private readonly Dictionary<string, ValidatedCompilerOutput> outputs = new(StringComparer.Ordinal);
        internal ValidatedCompilerOutput Get(string id, ProjectAnalysisContext context,
            CompilerObservation observation, CancellationToken token)
        {
            if (outputs.TryGetValue(id, out var current) && ReferenceEquals(current.Context, context) &&
                ReferenceEquals(current.Observation, observation)) return current;
            var captured = CompilerOutputBinding.ValidateAndInspect(context, observation, token);
            outputs[id] = captured;
            return captured;
        }
    }

    private static PolicyException SavedRefuse() => new("provenance.savedCoverageUnproven",
        "Saved normalized coverage module reachability, actual compiler/PDB/path/point associations or complete eligible method union are unproven.");

    private static ProjectContextException AggregateRefuse(string? detail = null) => new("coverage.aggregateUnproven",
        "Required successful invocation membership and complete eligible compiler/PDB method coverage are not independently bound across the graph." +
        (detail is null ? "" : " " + detail));

    private static string Required(XElement element, string attribute) => element.Attribute(attribute)?.Value is { Length: > 0 } value ? value : throw Refuse();
    private static ProjectContextException Refuse() => new("coverage.graphUnproven", "Owned OpenCover modules, metadata tokens, files or sequence points do not match the independently built invocation dependency contexts, or eligible module evidence is missing.");
}
