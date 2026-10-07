using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

// Ownership and OpenCover format only. Full graph module/sequence-point association
// and authored/reference preservation through tests are NOT established by this record.
internal sealed record OwnedOpenCover(string Path, string Sha256, ImmutableArray<byte> Bytes);

internal static class OwnedCoverageAttachment
{
    internal const long MaximumCoverageBytes = TestResultBinding.MaximumTrxBytes;
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static OwnedOpenCover ReadOpenCover(TestInvocationObservation invocation,
        ProjectAnalysisContext context, CompilerObservation compiler, CancellationToken token)
        => ReadCore(invocation, context, compiler, token, false);

    internal static OwnedOpenCover ReadFailedOpenCover(TestInvocationObservation invocation,
        ProjectAnalysisContext context, CompilerObservation compiler, CancellationToken token)
        => ReadCore(invocation, context, compiler, token, true);

    private static OwnedOpenCover ReadCore(TestInvocationObservation invocation,
        ProjectAnalysisContext context, CompilerObservation compiler, CancellationToken token, bool failed)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var trxHash = failed ? TestResultBinding.ValidateFailedTrx(invocation, context, compiler, token).TrxSha256 :
                TestResultBinding.ValidateSuccessfulTrx(invocation, context, compiler, token).TrxSha256;
            var trxPath = System.IO.Path.Combine(invocation.ResultsDirectory, "tests.trx");
            var trxBytes = TestResultBinding.ReadTrx(trxPath, token);
            if (Hash(trxBytes) != trxHash) throw Refuse();
            var root = TestResultBinding.Parse(trxBytes).Root ?? throw Refuse();
            var settings = Single(root, "TestSettings");
            var deployment = Single(settings, "Deployment");
            var deploymentName = Segment(Value(deployment, "runDeploymentRoot"));
            var entries = Single(Single(root, "ResultSummary"), "CollectorDataEntries");
            var collector = Single(entries, "Collector");
            if (entries.Elements().Count() != 1 || Value(collector, "uri") != "datacollector://microsoft/CoverletCodeCoverage/1.0") throw Refuse();
            var agent = Segment(Value(collector, "agentName"));
            var attachments = Single(collector, "UriAttachments");
            var attachment = Single(attachments, "UriAttachment");
            var link = Single(attachment, "A");
            if (attachments.Elements().Count() != 1 || attachment.Elements().Count() != 1) throw Refuse();
            // Reject hidden/foreign containers rather than ignoring contradictory evidence.
            foreach (var name in new[] { "TestSettings", "Deployment", "CollectorDataEntries", "Collector", "UriAttachments", "UriAttachment", "A" })
                if (root.Descendants().Count(e => e.Name.LocalName == name) != 1) throw Refuse();
            var href = Value(link, "href");
            var parts = href.Split('/');
            if (parts.Length != 2 || Segment(parts[0]) != agent || Segment(parts[1]) != "coverage.opencover.xml") throw Refuse();
            var directory = System.IO.Path.GetFullPath(invocation.ResultsDirectory);
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, deploymentName, "In", agent, parts[1]));
            var prefix = directory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, Paths == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw Refuse();
            // Reads exact TRX-declared deployment copy only; GUID copies and ambient XML
            // are never candidates. Multiple declarations are conservatively unsupported.
            var bytes = TestResultBinding.ReadTrx(path, token);
            var document = TestResultBinding.Parse(bytes);
            if (document.Root?.Name != XName.Get("CoverageSession") || ArtifactEvidenceInspector.InspectCoverage(bytes).Format != "opencover") throw Refuse();
            if (!trxBytes.AsSpan().SequenceEqual(TestResultBinding.ReadTrx(trxPath, token).AsSpan())) throw Refuse();
            token.ThrowIfCancellationRequested();
            return new(path, Hash(bytes), bytes);
        }
        catch (ProjectContextException error) when (error.Reason != "coverage.attachmentUnproven") { throw Refuse(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or XmlException or OverflowException)
        { throw Refuse(); }
    }

    private static string Segment(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.Length > 255 ||
            value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '%' or '?' or '#' or '*' or '"' or '<' or '>' or '|') ||
            value.Trim() != value || value.EndsWith('.') || value.EndsWith(' ')) throw Refuse();
        return value;
    }
    private static XElement Single(XElement parent, string name) => parent.Elements(Ns + name).Single();
    private static string Value(XElement element, string name) => element.Attribute(name)?.Value is { Length: > 0 } value ? value : throw Refuse();
    private static string Hash(ImmutableArray<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes.AsSpan())).ToLowerInvariant();
    private static ProjectContextException Refuse() => new("coverage.attachmentUnproven", "Required OpenCover collector attachment is missing, ambiguous, unsafe, changed or unsupported; only the exact invocation-owned TRX deployment attachment is accepted.");
}
