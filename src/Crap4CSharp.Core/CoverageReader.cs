using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

public sealed record CoveragePoint(
    int Line,
    int Visits,
    int? StartColumn = null,
    int? EndLine = null,
    int? EndColumn = null,
    int? Offset = null);

public sealed record CoverageMethod(
    string? File,
    string TypeName,
    string MethodName,
    int? ParameterCount,
    IReadOnlyList<CoveragePoint> SequencePoints,
    string? ModuleIdentity = null);

public static class CoverageReader
{
    public static IReadOnlyList<CoverageMethod> Read(string reportPath)
    {
        try
        {
            using var stream = File.OpenRead(reportPath);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 100_000_000
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            return document.Root?.Name.LocalName switch
            {
                "CoverageSession" => ReadOpenCover(document, Path.GetDirectoryName(Path.GetFullPath(reportPath))!),
                "coverage" => ReadCobertura(document, Path.GetDirectoryName(Path.GetFullPath(reportPath))!),
                var root => throw new InvalidDataException($"Unsupported coverage XML root '{root ?? "(missing)"}' in {reportPath}")
            };
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"Malformed coverage XML in {reportPath}: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<CoverageMethod> ReadOpenCover(XDocument document, string baseDirectory)
    {
        var output = new List<CoverageMethod>();
        foreach (var module in document.Descendants().Where(element => element.Name.LocalName == "Module"))
        {
            var moduleIdentity = ChildValue(module, "ModuleName") ?? ChildValue(module, "FullName");
            var files = module.Descendants().Where(element => element.Name.LocalName == "File")
                .Select(element => (Id: Attr(element, "uid"), Path: Attr(element, "fullPath")))
                .Where(pair => pair.Id is not null && pair.Path is not null)
                .ToDictionary(pair => pair.Id!, pair => ResolvePath(pair.Path!, baseDirectory));

            foreach (var method in module.Descendants().Where(element => element.Name.LocalName == "Method"))
            {
                var fullName = ChildValue(method, "Name") ?? string.Empty;
                var typeName = method.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Class")?
                    .Elements().FirstOrDefault(element => element.Name.LocalName == "FullName")?.Value ?? string.Empty;
                var points = method.Descendants().Where(element => element.Name.LocalName == "SequencePoint")
                    .Select(element => (Line: IntAttr(element, "sl"), Visits: IntAttr(element, "vc"),
                        StartColumn: IntAttr(element, "sc"), EndLine: IntAttr(element, "el"),
                        EndColumn: IntAttr(element, "ec"), Offset: IntAttr(element, "offset"),
                        FileId: Attr(element, "fileid")))
                    .Where(point => point.Line is > 0 and < 0xFEEFEE && point.Visits is not null)
                    .ToArray();
                var fileId = method.Descendants().FirstOrDefault(element => element.Name.LocalName == "FileRef")?.Attribute("uid")?.Value
                    ?? points.Select(point => point.FileId).FirstOrDefault(id => id is not null);
                files.TryGetValue(fileId ?? string.Empty, out var file);
                output.Add(new CoverageMethod(file, typeName, ParseMethodName(fullName), ParseParameterCount(fullName),
                    points.Select(point => new CoveragePoint(point.Line!.Value, point.Visits!.Value,
                        point.StartColumn, point.EndLine, point.EndColumn, point.Offset)).ToArray(), moduleIdentity));
            }
        }
        return output;
    }

    private static IReadOnlyList<CoverageMethod> ReadCobertura(XDocument document, string baseDirectory)
    {
        var sourceRoot = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "source")?.Value;
        var output = new List<CoverageMethod>();
        foreach (var @class in document.Descendants().Where(element => element.Name.LocalName == "class"))
        {
            var moduleIdentity = @class.Ancestors().FirstOrDefault(element => element.Name.LocalName == "package")?
                .Attribute("name")?.Value;
            var typeName = Attr(@class, "name") ?? string.Empty;
            var filename = Attr(@class, "filename");
            var file = filename is null ? null : ResolvePath(filename,
                !string.IsNullOrWhiteSpace(sourceRoot) && Path.IsPathRooted(sourceRoot) ? sourceRoot : baseDirectory);
            var methodsContainer = @class.Elements().FirstOrDefault(element => element.Name.LocalName == "methods");
            if (methodsContainer is null) continue;
            foreach (var method in methodsContainer.Elements().Where(element => element.Name.LocalName == "method"))
            {
                var name = Attr(method, "name") ?? string.Empty;
                var signature = Attr(method, "signature") ?? string.Empty;
                var points = method.Descendants().Where(element => element.Name.LocalName == "line")
                    .Select(element => (Line: IntAttr(element, "number"), Visits: IntAttr(element, "hits")))
                    .Where(point => point.Line is > 0 and < 0xFEEFEE && point.Visits is not null)
                    .Select(point => new CoveragePoint(point.Line!.Value, point.Visits!.Value)).ToArray();
                output.Add(new CoverageMethod(file, typeName, name, ParseParameterCount(signature), points, moduleIdentity));
            }
        }
        return output;
    }

    private static string ResolvePath(string path, string baseDirectory) =>
        Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), baseDirectory);

    private static string ParseMethodName(string fullName)
    {
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
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var contents = signature[(open + 1)..close].Trim();
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

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;
    private static int? IntAttr(XElement element, string name) => int.TryParse(Attr(element, name), out var value) ? value : null;
    private static string? ChildValue(XElement element, params string[] path)
    {
        XElement? current = element;
        foreach (var name in path) current = current?.Elements().FirstOrDefault(child => child.Name.LocalName == name);
        return current?.Value;
    }
}
