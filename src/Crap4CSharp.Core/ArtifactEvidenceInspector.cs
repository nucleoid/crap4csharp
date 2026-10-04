using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

public sealed record InspectedBuildEvidence(string ModuleIdentity, string Mvid, string DebugIdentity,
    IReadOnlyDictionary<string, string> Documents, string LanguageVersion, IReadOnlyList<string> PreprocessorSymbols,
    IReadOnlyList<InspectedMethodEvidence> Methods);
public sealed record InspectedMethodEvidence(int MetadataToken, string TypeName, string MethodName,
    IReadOnlyList<InspectedSequencePoint> SequencePoints);
public sealed record InspectedSequencePoint(string Document, int Offset, int StartLine, int StartColumn,
    int EndLine, int EndColumn);
public sealed record InspectedTestEvidence(int Total, int Passed, int Failed, int Skipped,
    IReadOnlyList<string> StorageModules);
public sealed record InspectedCoverageEvidence(string Format, string CoordinateKind,
    IReadOnlyList<string> ModuleIdentities);

public static class ArtifactEvidenceInspector
{
    public static InspectedBuildEvidence InspectBuild(ImmutableArray<byte> assemblyBytes,
        ImmutableArray<byte> pdbBytes)
    {
        try
        {
            using var peStream = new MemoryStream(assemblyBytes.ToArray(), writable: false);
            using var pe = new PEReader(peStream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) throw new InvalidDataException("Captured assembly has no managed metadata.");
            var metadata = pe.GetMetadataReader();
            var module = metadata.GetModuleDefinition();
            var moduleName = metadata.GetString(module.Name);
            var mvid = metadata.GetGuid(module.Mvid).ToString("D").ToLowerInvariant();
            var codeViewEntries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView)
                .ToArray();
            if (codeViewEntries.Length != 1)
                throw new InvalidDataException("Captured assembly must contain exactly one CodeView debug identity.");
            var codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntries[0]);

            using var pdbProvider = MetadataReaderProvider.FromPortablePdbImage(pdbBytes);
            var pdbReader = pdbProvider.GetMetadataReader();
            var header = pdbReader.DebugMetadataHeader
                ?? throw new InvalidDataException("Captured PDB has no portable debug identity.");
            var pdbId = new BlobContentId(header.Id);
            if (codeView.Guid != pdbId.Guid)
                throw new InvalidDataException("Captured assembly and portable PDB identities do not match.");
            var debugIdentity = CanonicalIdentity.Tuple("portable-pdb-debug-identity-v1",
                codeView.Guid.ToString("D"), codeView.Age.ToString(System.Globalization.CultureInfo.InvariantCulture),
                codeViewEntries[0].Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
                pdbId.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
            var documents = new Dictionary<string, string>(StringComparer.Ordinal);
            var sha256 = new Guid("8829d00f-11b8-4213-878b-770e8597ac16");
            foreach (var handle in pdbReader.Documents)
            {
                var document = pdbReader.GetDocument(handle);
                if (document.HashAlgorithm.IsNil || pdbReader.GetGuid(document.HashAlgorithm) != sha256)
                    throw new InvalidDataException("Captured PDB document uses an unsupported checksum algorithm.");
                var name = pdbReader.GetString(document.Name).Replace('\\', '/');
                var checksum = Convert.ToHexString(pdbReader.GetBlobBytes(document.Hash)).ToLowerInvariant();
                if (!documents.TryAdd(name, checksum))
                    throw new InvalidDataException("Captured PDB contains duplicate document identities.");
            }
            var options = CompilationOptions(pdbReader);
            if (!options.TryGetValue("language-version", out var languageVersion) ||
                !options.TryGetValue("define", out var define))
                throw new InvalidDataException("Captured PDB has no supported compilation-options record.");
            var symbols = define.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.Ordinal).ToArray();
            var methods = new List<InspectedMethodEvidence>();
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(typeHandle);
                var typeName = TypeName(metadata, typeHandle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    var debugHandle = MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(methodHandle));
                    if (debugHandle.IsNil) continue;
                    var debug = pdbReader.GetMethodDebugInformation(debugHandle);
                    var points = debug.GetSequencePoints().Where(point => !point.IsHidden).Select(point =>
                    {
                        var documentHandle = point.Document.IsNil ? debug.Document : point.Document;
                        if (documentHandle.IsNil)
                            throw new InvalidDataException("Captured PDB sequence point has no document.");
                        var document = pdbReader.GetDocument(documentHandle);
                        return new InspectedSequencePoint(pdbReader.GetString(document.Name).Replace('\\', '/'),
                            point.Offset, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn);
                    }).ToArray();
                    if (points.Length > 0)
                        methods.Add(new InspectedMethodEvidence(MetadataTokens.GetToken(methodHandle), typeName,
                            metadata.GetString(method.Name), points));
                }
            }
            return new InspectedBuildEvidence(Path.GetFileNameWithoutExtension(moduleName), mvid, debugIdentity,
                documents, languageVersion, symbols, methods);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or ArgumentException)
        { throw new InvalidDataException("Captured PE/PDB evidence is malformed.", exception); }
    }

    public static InspectedTestEvidence InspectTrx(ImmutableArray<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            var summaries = document.Descendants().Where(element => element.Name.LocalName == "ResultSummary").ToArray();
            if (summaries.Length != 1)
                throw new InvalidDataException("Captured TRX must contain exactly one result summary.");
            var outcome = summaries[0].Attribute("outcome")?.Value;
            if (outcome is not ("Completed" or "Passed"))
                throw new InvalidDataException("Captured TRX did not complete successfully.");
            var counterElements = summaries[0].Descendants().Where(element => element.Name.LocalName == "Counters")
                .ToArray();
            if (counterElements.Length != 1)
                throw new InvalidDataException("Captured TRX must contain exactly one result counter set.");
            var counters = counterElements[0];
            var total = Attribute(counters, "total");
            var passed = Attribute(counters, "passed");
            var failed = Attribute(counters, "failed") + Attribute(counters, "error") +
                Attribute(counters, "timeout") + Attribute(counters, "aborted") +
                Attribute(counters, "disconnected") + Attribute(counters, "passedButRunAborted") +
                Attribute(counters, "notRunnable") + Attribute(counters, "inconclusive");
            var notExecuted = Attribute(counters, "notExecuted");
            var executed = Attribute(counters, "executed");
            var skipped = notExecuted;
            if (total <= 0 || passed < 0 || failed < 0 || skipped < 0 || executed + notExecuted != total ||
                passed + failed > executed || passed == 0)
                throw new InvalidDataException("Captured TRX counters are inconsistent.");
            var storageModules = document.Descendants().Where(element => element.Name.LocalName == "UnitTest")
                .Select(element => element.Attribute("storage")?.Value)
                .Concat(document.Descendants().Where(element => element.Name.LocalName == "TestMethod")
                    .Select(element => element.Attribute("codeBase")?.Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFileNameWithoutExtension(value!.Replace('\\', '/')))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (storageModules.Length == 0)
                throw new InvalidDataException("Captured TRX has no test storage identity.");
            return new InspectedTestEvidence(total, passed, failed, skipped, storageModules);
        }
        catch (XmlException exception)
        { throw new InvalidDataException("Captured TRX is malformed.", exception); }
    }

    public static InspectedCoverageEvidence InspectCoverage(ImmutableArray<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 100_000_000 });
            var document = XDocument.Load(reader);
            var root = document.Root?.Name.LocalName;
            return root switch
            {
                "CoverageSession" => new("opencover", "sequence-point",
                    document.Descendants().Where(element => element.Name.LocalName == "Module")
                        .Select(module => module.Elements().FirstOrDefault(element =>
                            element.Name.LocalName is "ModuleName" or "FullName")?.Value)
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                "coverage" => new("cobertura", "line",
                    document.Descendants().Where(element => element.Name.LocalName == "package")
                        .Select(package => package.Attribute("name")?.Value)
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                _ => throw new InvalidDataException("Captured coverage XML has an unsupported root.")
            };
        }
        catch (XmlException exception)
        { throw new InvalidDataException("Captured coverage XML is malformed.", exception); }
    }

    public static bool CoverageMatchesBuild(ImmutableArray<byte> bytes, string logicalPath,
        InspectedBuildEvidence build, IReadOnlyList<string> logicalSourcePaths, CapturedPathPolicy pathPolicy) =>
        CoverageMatchesBuildCancellable(bytes, logicalPath, build, logicalSourcePaths, pathPolicy, default);

    public static bool CoverageMatchesBuildCancellable(ImmutableArray<byte> bytes, string logicalPath,
        InspectedBuildEvidence build, IReadOnlyList<string> logicalSourcePaths, CapturedPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        // A Coverlet document may contain every instrumented project in the test graph.
        // This artifact is bound to one manifest build, so foreign modules are neither
        // evidence for nor evidence against that build.
        var methods = CoverageReader.Read(bytes.AsSpan(), logicalPath)
            .Where(method => string.Equals(method.ModuleIdentity, build.ModuleIdentity, StringComparison.Ordinal))
            .ToArray();
        if (methods.Length == 0) return false;
        var pathIndex = new CapturedLogicalPathIndex(logicalSourcePaths, pathPolicy);
        var methodIndex = build.Methods.GroupBy(MethodKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var reportedLineCounts = new Dictionary<string, Dictionary<int, int>>(
            pathPolicy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = pathIndex.Resolve(method.File);
            if (logical is null || !logicalSourcePaths.Contains(logical,
                    pathPolicy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)) continue;
            if (!reportedLineCounts.TryGetValue(logical, out var counts))
                reportedLineCounts.Add(logical, counts = []);
            foreach (var line in method.SequencePoints.Select(point => point.Line).Distinct())
                counts[line] = counts.GetValueOrDefault(line) + 1;
        }

        if (InspectCoverage(bytes).Format != "cobertura")
            return methods.All(reported => MatchingMethodCount(reported, methodIndex, pathIndex,
                reportedLineCounts, cancellationToken) == 1);

        // Cobertura expands one relative filename against each captured <source> root.
        // Those paths are alternatives for one observation, not independent methods.
        // Exactly one alternative must resolve to exactly one PDB method.
        foreach (var alternatives in methods.GroupBy(CoverageObservationKey, StringComparer.Ordinal))
        {
            if (alternatives.Sum(reported =>
                    MatchingMethodCount(reported, methodIndex, pathIndex, reportedLineCounts,
                        cancellationToken)) != 1) return false;
        }
        return true;
    }

    private static int MatchingMethodCount(CoverageMethod reported,
        IReadOnlyDictionary<string, InspectedMethodEvidence[]> methodIndex, CapturedLogicalPathIndex pathIndex,
        IReadOnlyDictionary<string, Dictionary<int, int>> reportedLineCounts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = methodIndex.TryGetValue(MethodKey(reported.TypeName, reported.MethodName), out var indexed)
            ? indexed : [];
        if (reported.MethodToken is { Length: > 0 } tokenText &&
            int.TryParse(tokenText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tokenText[2..] : tokenText,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var token))
            candidates = candidates.Where(method => method.MetadataToken == token).ToArray();
        return candidates.Count(method => PointsMatch(reported, method, pathIndex, reportedLineCounts,
            cancellationToken));
    }

    private static string MethodKey(InspectedMethodEvidence method) => MethodKey(method.TypeName, method.MethodName);
    private static string MethodKey(string typeName, string methodName) =>
        CanonicalIdentity.Tuple("coverage-method-index-v1", typeName.Replace('+', '/'), methodName);

    private static string CoverageObservationKey(CoverageMethod method) => CanonicalIdentity.Tuple(
        "coverage-observation-v1", method.ModuleIdentity, method.TypeName, method.MethodName,
        method.ParameterCount?.ToString(System.Globalization.CultureInfo.InvariantCulture), method.MethodToken,
        string.Join("\0", method.SequencePoints.Select(point => string.Join(":", point.Line, point.Visits,
            point.StartColumn, point.EndLine, point.EndColumn, point.Offset))));

    private static int Attribute(XElement element, string name) =>
        int.TryParse(element.Attribute(name)?.Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static Dictionary<string, string> CompilationOptions(MetadataReader reader)
    {
        var kind = new Guid("b5feec05-8cd0-4a83-96da-466284bb4bd8");
        var records = reader.CustomDebugInformation.Select(handle => reader.GetCustomDebugInformation(handle))
            .Where(value => !value.Kind.IsNil && reader.GetGuid(value.Kind) == kind).ToArray();
        if (records.Length != 1)
            throw new InvalidDataException("Captured PDB must contain exactly one compilation-options record.");
        var values = Encoding.UTF8.GetString(reader.GetBlobBytes(records[0].Value)).TrimEnd('\0').Split('\0');
        if (values.Length % 2 != 0)
            throw new InvalidDataException("Captured PDB compilation-options record is malformed.");
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Length; index += 2)
            if (!output.TryAdd(values[index], values[index + 1]))
                throw new InvalidDataException("Captured PDB compilation-options record has duplicate keys.");
        return output;
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return TypeName(reader, declaring) + "/" + name;
        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    private static bool PointsMatch(CoverageMethod reported, InspectedMethodEvidence method,
        CapturedLogicalPathIndex pathIndex,
        IReadOnlyDictionary<string, Dictionary<int, int>> reportedLineCounts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reported.File is null || reported.SequencePoints.Count == 0) return false;
        var reportedLogical = pathIndex.Resolve(reported.File);
        if (reportedLogical is null) return false;
        var resolvedPoints = method.SequencePoints.Select(point =>
            (Point: point, Logical: pathIndex.Resolve(point.Document)))
            .Where(item => item.Logical == reportedLogical).ToArray();
        if (resolvedPoints.Length == 0) return false;
        var points = resolvedPoints.Select(item => item.Point).ToArray();
        if (reported.SequencePoints.All(point => point.StartColumn is null &&
                (point.EndLine is null || point.EndLine == point.Line) &&
                point.EndColumn is null && point.Offset is null))
        {
            var lines = reported.SequencePoints.Select(point => point.Line).Distinct().ToArray();
            // Coverlet omits compiler boundary/brace points (a one-column span) and
            // projects every retained sequence point to line evidence. Require the
            // complete statement-line set so a trimmed or stale report cannot improve
            // the coverage denominator.
            var statementPoints = points.Where(point => point.StartLine != point.EndLine ||
                point.EndColumn != point.StartColumn + 1).ToArray();
            if (lines.Length == 0 || statementPoints.Length == 0 || statementPoints.Any(point =>
                    point.EndLine < point.StartLine || point.EndLine - point.StartLine > 100_000)) return false;
            var lineSet = lines.ToHashSet();
            if (lines.Any(line => !points.Any(point => line >= point.StartLine && line <= point.EndLine)))
                return false;
            // State-machine MoveNext bodies include compiler control points that Coverlet
            // intentionally does not project. They remain containment-only because those
            // generated methods are not directly scored as authored callables.
            if (method.TypeName.Contains("/<", StringComparison.Ordinal))
                return true;
            if (lines.Any(line => !statementPoints.Any(point => line >= point.StartLine && line <= point.EndLine)))
                return false;
            foreach (var point in statementPoints)
                for (var line = point.StartLine; line <= point.EndLine; line++)
                    if (!lineSet.Contains(line) && !(method.MethodName is ".ctor" or ".cctor" &&
                            reportedLineCounts.TryGetValue(reportedLogical, out var counts) &&
                            counts.GetValueOrDefault(line) > (lineSet.Contains(line) ? 1 : 0))) return false;
            return true;
        }
        return reported.SequencePoints.Count == points.Length && reported.SequencePoints.All(reportPoint =>
            points.Any(point => point.StartLine == reportPoint.Line &&
                point.StartColumn == reportPoint.StartColumn &&
                point.EndLine == reportPoint.EndLine &&
                point.EndColumn == reportPoint.EndColumn &&
                point.Offset == reportPoint.Offset));
    }

}
