using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

public sealed record InspectedBuildEvidence(string ModuleIdentity, string Mvid, string DebugIdentity);
public sealed record InspectedTestEvidence(int Total, int Passed, int Failed, int Skipped);

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
            return new InspectedBuildEvidence(Path.GetFileNameWithoutExtension(moduleName), mvid, debugIdentity);
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
            var counters = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "Counters")
                ?? throw new InvalidDataException("Captured TRX has no result counters.");
            var total = Attribute(counters, "total");
            var passed = Attribute(counters, "passed");
            var failed = Attribute(counters, "failed") + Attribute(counters, "error") +
                Attribute(counters, "timeout") + Attribute(counters, "aborted");
            var skipped = total - passed - failed;
            if (total <= 0 || passed < 0 || failed < 0 || skipped < 0)
                throw new InvalidDataException("Captured TRX counters are inconsistent.");
            return new InspectedTestEvidence(total, passed, failed, skipped);
        }
        catch (XmlException exception)
        { throw new InvalidDataException("Captured TRX is malformed.", exception); }
    }

    private static int Attribute(XElement element, string name) =>
        int.TryParse(element.Attribute(name)?.Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}
