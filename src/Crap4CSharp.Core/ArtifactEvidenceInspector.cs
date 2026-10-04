using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

public sealed record InspectedBuildEvidence(string ModuleIdentity, string Mvid, string DebugIdentity,
    IReadOnlyDictionary<string, string> Documents);
public sealed record InspectedTestEvidence(int Total, int Passed, int Failed, int Skipped);
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
            return new InspectedBuildEvidence(Path.GetFileNameWithoutExtension(moduleName), mvid, debugIdentity,
                documents);
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
                passed + failed > executed)
                throw new InvalidDataException("Captured TRX counters are inconsistent.");
            return new InspectedTestEvidence(total, passed, failed, skipped);
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

    private static int Attribute(XElement element, string name) =>
        int.TryParse(element.Attribute(name)?.Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}
