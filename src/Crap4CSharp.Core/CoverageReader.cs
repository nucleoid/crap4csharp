using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

public sealed record CoveragePoint(int Line, int Visits, int? StartColumn = null, int? EndLine = null,
    int? EndColumn = null, int? Offset = null);

public sealed record CoverageMethod(string? File, string TypeName, string MethodName, int? ParameterCount,
    IReadOnlyList<CoveragePoint> SequencePoints, string? ModuleIdentity = null)
{
    public string? ReportId { get; init; }
    public string? ObservationId { get; init; }
    public string? ReportedFile { get; init; }
    public CoveragePathResolution? PathResolution { get; init; }
    public string? ContextId { get; init; }
    public string? RawSignature { get; init; }
    public string? MethodToken { get; init; }
    public int? GenericArity { get; init; }
    public IReadOnlyList<string> DocumentIdentities { get; init; } = [];
}

public static class CoverageReader
{
    public static IReadOnlyList<CoverageMethod> Read(ReadOnlySpan<byte> bytes, string logicalReportPath)
    {
        var parsed = ReadCapturedDocument(bytes, logicalReportPath);
        return parsed.Document.Root?.Name.LocalName switch
        {
            "CoverageSession" => ReadOpenCoverCompatibility(parsed.Document, parsed.Directory, parsed.ReportId, true),
            "coverage" => ReadCoberturaCompatibility(parsed.Document, parsed.Directory, parsed.ReportId, true),
            var root => throw new InvalidDataException($"Unsupported coverage XML root '{root ?? "(missing)"}' in {logicalReportPath}")
        };
    }

    public static IReadOnlyList<CoverageMethod> Read(string reportPath)
    {
        var parsed = ReadDocument(reportPath);
        return parsed.Document.Root?.Name.LocalName switch
        {
            "CoverageSession" => ReadOpenCoverCompatibility(parsed.Document, parsed.Directory, parsed.ReportId, false),
            "coverage" => ReadCoberturaCompatibility(parsed.Document, parsed.Directory, parsed.ReportId, false),
            var root => throw new InvalidDataException($"Unsupported coverage XML root '{root ?? "(missing)"}' in {reportPath}")
        };
    }

    public static CoverageReadResult ReadDetailed(string reportPath, CoveragePathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var parsed = ReadDocument(reportPath);
        return parsed.Document.Root?.Name.LocalName switch
        {
            "CoverageSession" => ReadOpenCoverDetailed(parsed.Document, reportPath, parsed.ReportId, resolver),
            "coverage" => ReadCoberturaDetailed(parsed.Document, reportPath, parsed.ReportId, resolver),
            var root => throw new InvalidDataException($"Unsupported coverage XML root '{root ?? "(missing)"}' in {reportPath}")
        };
    }

    private static ParsedDocument ReadDocument(string reportPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(reportPath);
            var bytes = File.ReadAllBytes(fullPath);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 100_000_000
            });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var reportId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new ParsedDocument(document, Path.GetDirectoryName(fullPath)!, reportId);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Malformed coverage XML in {reportPath}: {exception.Message}", exception);
        }
    }

    private static ParsedDocument ReadCapturedDocument(ReadOnlySpan<byte> bytes, string logicalReportPath)
    {
        if (bytes.Length > 100_000_000) throw new InvalidDataException($"Coverage XML exceeds the 100 MB limit: {logicalReportPath}");
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 100_000_000
            });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var reportId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var logical = logicalReportPath.Replace('\\', '/');
            var slash = logical.LastIndexOf('/');
            var root = OperatingSystem.IsWindows() ? @"C:\" : "/";
            var directory = slash < 0 ? root : Path.Combine(root, logical[..slash].Replace('/', Path.DirectorySeparatorChar));
            return new ParsedDocument(document, directory, reportId);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Malformed coverage XML in {logicalReportPath}: {exception.Message}", exception);
        }
    }

    private static CoverageReadResult ReadOpenCoverDetailed(XDocument document, string reportPath, string reportId,
        CoveragePathResolver resolver)
    {
        var output = new List<CoverageMethod>();
        var diagnostics = new List<CoverageDiagnostic>();
        var resolutions = new List<CoveragePathResolution>();
        foreach (var module in document.Descendants().Where(element => element.Name.LocalName == "Module"))
        {
            var moduleIdentity = ChildValue(module, "ModuleName") ?? ChildValue(module, "FullName");
            var fileGroups = module.Descendants().Where(element => element.Name.LocalName == "File")
                .Select(element => (Id: Attr(element, "uid"), Path: Attr(element, "fullPath")))
                .Where(pair => pair.Id is not null)
                .GroupBy(pair => pair.Id!, StringComparer.Ordinal).ToArray();
            var files = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var group in fileGroups)
            {
                var paths = group.Select(pair => pair.Path).Distinct(StringComparer.Ordinal).ToArray();
                if (paths.Length > 1)
                {
                    diagnostics.Add(CoverageDiagnostic.Create(CoverageReasonCodes.InvalidPath,
                        CoverageDiagnosticStage.Path, CoverageDiagnosticSeverity.Error, CoverageDiagnosticScope.Report,
                        reportId, message: $"OpenCover file ID '{group.Key}' identifies multiple paths."));
                    continue;
                }
                files[group.Key] = paths.SingleOrDefault();
            }

            foreach (var method in module.Descendants().Where(element => element.Name.LocalName == "Method"))
            {
                var fullName = ChildValue(method, "Name") ?? string.Empty;
                var typeName = method.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Class")?
                    .Elements().FirstOrDefault(element => element.Name.LocalName == "FullName")?.Value ?? string.Empty;
                var methodName = ParseMethodName(fullName);
                var parameterCount = ParseParameterCount(fullName);
                var genericArity = ParseGenericArity(fullName);
                var rawPoints = method.Descendants().Where(element => element.Name.LocalName == "SequencePoint")
                    .Select(element => new OpenCoverPoint(IntAttr(element, "sl"), IntAttr(element, "vc"),
                        IntAttr(element, "sc"), IntAttr(element, "el"), IntAttr(element, "ec"), IntAttr(element, "offset"),
                        Attr(element, "fileid")))
                    .Where(point => point.Line is > 0 and < 0xFEEFEE && point.Visits is not null).ToArray();
                var fileRefs = method.Descendants().Where(element => element.Name.LocalName == "FileRef")
                    .Select(element => Attr(element, "uid")).Where(id => id is not null).Cast<string>()
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var effectivePoints = rawPoints.Select(point => point with
                {
                    FileId = point.FileId ?? (fileRefs.Length == 1 ? fileRefs[0] : null)
                }).ToArray();
                var documentIds = effectivePoints.Select(point => point.FileId).Where(id => id is not null).Cast<string>()
                    .Concat(effectivePoints.Length == 0 ? fileRefs : []).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray();
                var points = effectivePoints.Select(point => point.ToCoveragePoint()).ToArray();
                var observationId = ObservationId(reportId, moduleIdentity, typeName, methodName, parameterCount,
                    documentIds, points);

                if (documentIds.Length > 1 || (fileRefs.Length > 1 && rawPoints.Any(point => point.FileId is null)))
                {
                    diagnostics.Add(CoverageDiagnostic.Create(CoverageReasonCodes.UnsupportedMultiDocumentMapping,
                        CoverageDiagnosticStage.Generated, CoverageDiagnosticSeverity.Warning, CoverageDiagnosticScope.Observation,
                        reportId, observationId, reportedType: typeName, reportedMethodName: methodName,
                        reportedParameterCount: parameterCount, moduleIdentities: moduleIdentity is null ? [] : [moduleIdentity],
                        message: "OpenCover method sequence points reference multiple or indeterminate documents."));
                    continue;
                }

                var fileId = documentIds.SingleOrDefault();
                if (fileId is null || !files.TryGetValue(fileId, out var reportedFile) || reportedFile is null)
                {
                    diagnostics.Add(CoverageDiagnostic.Create(CoverageReasonCodes.MissingPath,
                        CoverageDiagnosticStage.Path, CoverageDiagnosticSeverity.Warning, CoverageDiagnosticScope.Observation,
                        reportId, observationId, reportedType: typeName, reportedMethodName: methodName,
                        reportedParameterCount: parameterCount, moduleIdentities: moduleIdentity is null ? [] : [moduleIdentity],
                        message: fileId is null ? "OpenCover method has no usable file reference." : $"OpenCover file ID '{fileId}' is missing."));
                    output.Add(new CoverageMethod(null, typeName, methodName, parameterCount, points, moduleIdentity)
                    {
                        ReportId = reportId, ObservationId = observationId, RawSignature = fullName,
                        MethodToken = Attr(method, "metadataToken"), GenericArity = genericArity,
                        DocumentIdentities = documentIds
                    });
                    continue;
                }

                var resolution = resolver.Resolve(reportedFile, [], reportPath, reportId, observationId);
                if (resolution.Diagnostic is not null)
                {
                    var enriched = WithObservation(resolution.Diagnostic, typeName, methodName, parameterCount, moduleIdentity);
                    resolution = resolution with { Diagnostic = enriched };
                    diagnostics.Add(enriched);
                }
                resolutions.Add(resolution);
                output.Add(new CoverageMethod(resolution.LocalPath, typeName, methodName, parameterCount, points, moduleIdentity)
                {
                    ReportId = reportId, ObservationId = observationId, ReportedFile = reportedFile, PathResolution = resolution
                    , RawSignature = fullName, MethodToken = Attr(method, "metadataToken"), GenericArity = genericArity,
                    DocumentIdentities = documentIds
                });
            }
        }
        return new CoverageReadResult(output, SortDiagnostics(diagnostics), resolutions, reportId);
    }

    private static CoverageReadResult ReadCoberturaDetailed(XDocument document, string reportPath, string reportId,
        CoveragePathResolver resolver)
    {
        var sourceRoots = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "sources")?
            .Elements().Where(element => element.Name.LocalName == "source")
            .Select(element => element.Value.Trim()).Where(value => value.Length > 0).ToArray() ?? [];
        var output = new List<CoverageMethod>();
        var diagnostics = new List<CoverageDiagnostic>();
        var resolutions = new List<CoveragePathResolution>();
        foreach (var @class in document.Descendants().Where(element => element.Name.LocalName == "class"))
        {
            var moduleIdentity = @class.Ancestors().FirstOrDefault(element => element.Name.LocalName == "package")?
                .Attribute("name")?.Value;
            var typeName = Attr(@class, "name") ?? string.Empty;
            var filename = Attr(@class, "filename");
            var methodsContainer = @class.Elements().FirstOrDefault(element => element.Name.LocalName == "methods");
            if (methodsContainer is null) continue;
            foreach (var method in methodsContainer.Elements().Where(element => element.Name.LocalName == "method"))
            {
                var name = CoverageSignature.StripCustomModifiers(Attr(method, "name") ?? string.Empty);
                var signature = Attr(method, "signature") ?? string.Empty;
                var parameterCount = ParseParameterCount(signature);
                var points = method.Descendants().Where(element => element.Name.LocalName == "line")
                    .Select(element => (Line: IntAttr(element, "number"), Visits: IntAttr(element, "hits")))
                    .Where(point => point.Line is > 0 and < 0xFEEFEE && point.Visits is not null)
                    .Select(point => new CoveragePoint(point.Line!.Value, point.Visits!.Value)).ToArray();
                var observationId = ObservationId(reportId, moduleIdentity, typeName, name, parameterCount,
                    filename is null ? [] : [filename], points);
                CoveragePathResolution? resolution = null;
                if (filename is not null)
                {
                    resolution = resolver.Resolve(filename, sourceRoots, reportPath, reportId, observationId);
                    if (resolution.Diagnostic is not null)
                    {
                        var enriched = WithObservation(resolution.Diagnostic, typeName, name, parameterCount, moduleIdentity);
                        resolution = resolution with { Diagnostic = enriched };
                        diagnostics.Add(enriched);
                    }
                    resolutions.Add(resolution);
                }
                else
                {
                    diagnostics.Add(CoverageDiagnostic.Create(CoverageReasonCodes.MissingPath, CoverageDiagnosticStage.Path,
                        CoverageDiagnosticSeverity.Warning, CoverageDiagnosticScope.Observation, reportId, observationId,
                        reportedType: typeName, reportedMethodName: name, reportedParameterCount: parameterCount,
                        message: "Cobertura class has no filename."));
                }
                output.Add(new CoverageMethod(resolution?.LocalPath, typeName, name, parameterCount, points, moduleIdentity)
                {
                    ReportId = reportId, ObservationId = observationId, ReportedFile = filename, PathResolution = resolution
                    , RawSignature = signature, GenericArity = ParseGenericArity(name),
                    DocumentIdentities = filename is null ? [] : [filename]
                });
            }
        }
        return new CoverageReadResult(output, SortDiagnostics(diagnostics), resolutions, reportId);
    }

    private static CoverageDiagnostic WithObservation(CoverageDiagnostic diagnostic, string typeName, string methodName,
        int? parameterCount, string? moduleIdentity) => CoverageDiagnostic.Create(
            diagnostic.Code, diagnostic.Stage, diagnostic.Severity, diagnostic.Scope,
            diagnostic.ReportId, diagnostic.ObservationId, diagnostic.Path, diagnostic.MethodId, diagnostic.Span,
            diagnostic.CandidatePaths, diagnostic.CandidateMethodIds, typeName, methodName, parameterCount,
            moduleIdentity is null ? [] : [moduleIdentity], diagnostic.MappingId, diagnostic.ExternalRootId,
            diagnostic.ContextId, diagnostic.Message);

    private static IReadOnlyList<CoverageMethod> ReadOpenCoverCompatibility(XDocument document, string baseDirectory,
        string reportId, bool captured)
    {
        var output = new List<CoverageMethod>();
        foreach (var module in document.Descendants().Where(element => element.Name.LocalName == "Module"))
        {
            var moduleIdentity = ChildValue(module, "ModuleName") ?? ChildValue(module, "FullName");
            var files = module.Descendants().Where(element => element.Name.LocalName == "File")
                .Select(element => (Id: Attr(element, "uid"), Path: Attr(element, "fullPath")))
                .Where(pair => pair.Id is not null && pair.Path is not null)
                .ToDictionary(pair => pair.Id!, pair => captured ? pair.Path : ResolveNativePath(pair.Path!, baseDirectory), StringComparer.Ordinal);
            foreach (var method in module.Descendants().Where(element => element.Name.LocalName == "Method"))
            {
                var fullName = ChildValue(method, "Name") ?? string.Empty;
                var typeName = method.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Class")?
                    .Elements().FirstOrDefault(element => element.Name.LocalName == "FullName")?.Value ?? string.Empty;
                var points = method.Descendants().Where(element => element.Name.LocalName == "SequencePoint")
                    .Select(element => new OpenCoverPoint(IntAttr(element, "sl"), IntAttr(element, "vc"), IntAttr(element, "sc"),
                        IntAttr(element, "el"), IntAttr(element, "ec"), IntAttr(element, "offset"), Attr(element, "fileid")))
                    .Where(point => point.Line is > 0 and < 0xFEEFEE && point.Visits is not null).ToArray();
                var fileId = method.Descendants().FirstOrDefault(element => element.Name.LocalName == "FileRef")?.Attribute("uid")?.Value
                    ?? points.Select(point => point.FileId).FirstOrDefault(id => id is not null);
                files.TryGetValue(fileId ?? string.Empty, out var file);
                output.Add(new CoverageMethod(file, typeName, ParseMethodName(fullName), ParseParameterCount(fullName),
                    points.Select(point => point.ToCoveragePoint()).ToArray(), moduleIdentity) { ReportId = reportId });
            }
        }
        return output;
    }

    private static IReadOnlyList<CoverageMethod> ReadCoberturaCompatibility(XDocument document, string baseDirectory,
        string reportId, bool captured)
    {
        var sourceRoots = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "sources")?
            .Elements().Where(element => element.Name.LocalName == "source").Select(element => element.Value.Trim())
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var output = new List<CoverageMethod>();
        foreach (var @class in document.Descendants().Where(element => element.Name.LocalName == "class"))
        {
            var moduleIdentity = @class.Ancestors().FirstOrDefault(element => element.Name.LocalName == "package")?.Attribute("name")?.Value;
            var typeName = Attr(@class, "name") ?? string.Empty;
            var filename = Attr(@class, "filename");
            IReadOnlyList<string?> files;
            if (filename is null) files = [null];
            else if (captured)
            {
                if (IsLexicallyAbsolute(filename) || sourceRoots.Length == 0) files = [filename];
                else files = sourceRoots.Select(sourceRoot => sourceRoot.Replace('\\', '/').TrimEnd('/') + "/" +
                    filename.Replace('\\', '/').TrimStart('/')).Cast<string?>().ToArray();
            }
            else
            {
                var sourceRoot = sourceRoots.FirstOrDefault();
                files = [ResolveNativePath(filename,
                    !string.IsNullOrWhiteSpace(sourceRoot) && Path.IsPathRooted(sourceRoot) ? sourceRoot : baseDirectory)];
            }
            var methodsContainer = @class.Elements().FirstOrDefault(element => element.Name.LocalName == "methods");
            if (methodsContainer is null) continue;
            foreach (var method in methodsContainer.Elements().Where(element => element.Name.LocalName == "method"))
            {
                var name = CoverageSignature.StripCustomModifiers(Attr(method, "name") ?? string.Empty);
                var signature = Attr(method, "signature") ?? string.Empty;
                var points = method.Descendants().Where(element => element.Name.LocalName == "line")
                    .Select(element => (Line: IntAttr(element, "number"), Visits: IntAttr(element, "hits")))
                    .Where(point => point.Line is > 0 and < 0xFEEFEE && point.Visits is not null)
                    .Select(point => new CoveragePoint(point.Line!.Value, point.Visits!.Value)).ToArray();
                output.AddRange(files.Select(file =>
                    new CoverageMethod(file, typeName, name, ParseParameterCount(signature), points, moduleIdentity)
                    { ReportId = reportId }));
            }
        }
        return output;
    }

    private static bool IsLexicallyAbsolute(string value)
    {
        var path = value.Replace('\\', '/');
        return path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
    }

    private static string? ResolveNativePath(string path, string baseDirectory)
    {
        if (!OperatingSystem.IsWindows() && (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))) return null;
        try { return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), baseDirectory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string ObservationId(string reportId, string? module, string type, string method, int? arity,
        IEnumerable<string> documents, IEnumerable<CoveragePoint> points)
    {
        var pointText = points.OrderBy(point => point.Line).ThenBy(point => point.StartColumn).ThenBy(point => point.EndLine)
            .ThenBy(point => point.EndColumn).ThenBy(point => point.Offset)
            .Select(point => string.Join(":", point.Line, point.Visits, point.StartColumn, point.EndLine, point.EndColumn, point.Offset));
        var value = string.Join("\n", reportId, module, type, method, arity,
            string.Join("\0", documents.Order(StringComparer.Ordinal)), string.Join("\0", pointText));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static IReadOnlyList<CoverageDiagnostic> SortDiagnostics(IEnumerable<CoverageDiagnostic> diagnostics) => diagnostics
        .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal).ThenBy(diagnostic => diagnostic.Path, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.ReportedType, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.ReportedMethodName, StringComparer.Ordinal)
        .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToArray();

    private static string ParseMethodName(string fullName)
    {
        fullName = CoverageSignature.StripCustomModifiers(fullName);
        var beforeParameters = fullName.Split('(', 2)[0];
        var separator = beforeParameters.LastIndexOf("::", StringComparison.Ordinal);
        var lastDot = beforeParameters.LastIndexOf('.');
        var start = separator >= 0 ? separator + 2 : lastDot >= 0 ? lastDot + 1 : 0;
        var name = beforeParameters[start..];
        var genericTick = name.IndexOf('`');
        return genericTick >= 0 ? name[..genericTick] : name;
    }

    private static int? ParseParameterCount(string signature)
    {
        if (!CoverageSignature.TryGetParameterContents(signature, out var parameterContents)) return null;
        var contents = parameterContents.Trim();
        if (contents.Length == 0) return 0;
        var depth = 0;
        var count = 1;
        foreach (var character in contents)
        {
            if (character is '<' or '[') depth++;
            else if (character is '>' or ']') depth--;
            else if (character == ',' && depth == 0) count++;
        }
        return count;
    }

    private static int? ParseGenericArity(string signature)
    {
        signature = CoverageSignature.StripCustomModifiers(signature);
        var beforeParameters = signature.Split('(', 2)[0];
        var separator = beforeParameters.LastIndexOf("::", StringComparison.Ordinal);
        var method = separator >= 0 ? beforeParameters[(separator + 2)..] :
            beforeParameters[(beforeParameters.LastIndexOf('.') + 1)..];
        var tick = method.LastIndexOf('`');
        // OpenCover/Cobertura often omit generic arity entirely. Absence is unknown,
        // not evidence that the method is non-generic.
        if (tick < 0) return null;
        var digits = new string(method[(tick + 1)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;
    private static int? IntAttr(XElement element, string name) => int.TryParse(Attr(element, name), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string? ChildValue(XElement element, params string[] path)
    {
        XElement? current = element;
        foreach (var name in path) current = current?.Elements().FirstOrDefault(child => child.Name.LocalName == name);
        return current?.Value;
    }

    private sealed record ParsedDocument(XDocument Document, string Directory, string ReportId);
    private sealed record OpenCoverPoint(int? Line, int? Visits, int? StartColumn, int? EndLine, int? EndColumn,
        int? Offset, string? FileId)
    {
        public CoveragePoint ToCoveragePoint()
        {
            // Coverlet 6 emits 1..2 when the OpenCover projection has only line evidence.
            // Preserve that as columnless evidence instead of pretending the point starts
            // before every indented single-line member.
            var syntheticColumns = StartColumn == 1 && EndColumn == 2;
            return new CoveragePoint(Line!.Value, Visits!.Value,
                syntheticColumns ? null : StartColumn, EndLine, syntheticColumns ? null : EndColumn, Offset);
        }
    }
}
