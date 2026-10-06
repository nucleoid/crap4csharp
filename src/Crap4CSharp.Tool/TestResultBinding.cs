using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Crap4CSharp.Core;

// Private successful-result gate. Coverage attachments and phase-A reference/authored
// preservation THROUGH execution are separate obligations, not asserted by this record.
internal sealed record BoundTestResult(string GraphId, int Passed, string TrxSha256,
    string AssemblySha256, string PdbSha256);

internal static class TestResultBinding
{
    internal const long MaximumTrxBytes = 16 * 1024 * 1024;
    internal const int MaximumTests = 32768;
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static BoundTestResult ValidateSuccessfulTrx(TestInvocationObservation invocation,
        ProjectAnalysisContext context, CompilerObservation compiler, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (invocation.Process.ExitCode != 0 || string.IsNullOrWhiteSpace(invocation.GraphId) ||
                !Paths.Equals(Canonical(invocation.ProjectPath), Canonical(context.ResolvedProjectPath)) ||
                !Paths.Equals(Canonical(compiler.ProjectPath), invocation.ProjectPath)) throw Refuse();
            // Independent actual intermediate/copy PE/PDB and complete compiler input gate,
            // not a storage filename stem or a self-declared test-module hash.
            CompilerOutputBinding.Validate(context, compiler, token);
            var directory = Canonical(invocation.ResultsDirectory);
            var path = Path.Combine(directory, "tests.trx");
            var trxs = Directory.EnumerateFiles(directory, "*.trx").Take(2).ToArray();
            if (trxs.Length != 1 || !Paths.Equals(trxs[0], path)) throw Refuse();
            var bytes = ReadTrx(path, token);
            ValidateDocument(bytes, Canonical(context.EvaluatedTargetPath), token);
            return new(invocation.GraphId, CountTests(bytes), Hash(bytes), compiler.Assembly!.Sha256, compiler.Pdb!.Sha256);
        }
        catch (ProjectContextException error) when (error.Reason != "test.resultsUnproven") { throw Refuse(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or XmlException or OverflowException)
        { throw Refuse(); }
    }

    internal static void ValidateDocument(ImmutableArray<byte> bytes, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            target = Canonical(target);
            var root = Parse(bytes).Root ?? throw Refuse();
            if (root.Name != Ns + "TestRun") throw Refuse();
            var results = Single(root, "Results").Elements().ToArray();
            var definitions = Single(root, "TestDefinitions").Elements().ToArray();
            var entries = Single(root, "TestEntries").Elements().ToArray();
            if (results.Length == 0 || results.Length > MaximumTests || results.Length != definitions.Length || results.Length != entries.Length ||
                results.Any(e => e.Name != Ns + "UnitTestResult") || definitions.Any(e => e.Name != Ns + "UnitTest") ||
                entries.Any(e => e.Name != Ns + "TestEntry")) throw Refuse();
            var summary = Single(root, "ResultSummary");
            if (Value(summary, "outcome") is not ("Completed" or "Passed") ||
                root.Descendants().Any(e => e.Name.LocalName is "ErrorInfo" or "RunInfo")) throw Refuse();
            var counters = Single(summary, "Counters");
            foreach (var name in new[] { "total", "executed", "passed" })
                if (Number(counters, name, true) != results.Length) throw Refuse();
            foreach (var name in new[] { "failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending" })
                if (Number(counters, name, false) != 0) throw Refuse();
            // VSTest legitimately emits completed=0 even when total/executed/passed>0.
            var completed = Number(counters, "completed", false);
            if (completed != 0 && completed != results.Length) throw Refuse();
            var byId = new Dictionary<Guid, XElement>();
            var executionIds = new HashSet<Guid>();
            foreach (var definition in definitions)
            {
                token.ThrowIfCancellationRequested();
                if (!byId.TryAdd(Id(definition, "id"), definition)) throw Refuse();
                var method = Single(definition, "TestMethod");
                // codeBase is case-preserved physical evidence. VSTest's storage is
                // lowercased even on Linux: compare its spelling, NEVER open that path.
                if (!Paths.Equals(Canonical(Value(method, "codeBase")), target) ||
                    !StringComparer.OrdinalIgnoreCase.Equals(Canonical(Value(definition, "storage")), target)) throw Refuse();
                if (!executionIds.Add(Id(Single(definition, "Execution"), "id"))) throw Refuse();
            }
            var byExecution = new Dictionary<Guid, XElement>();
            foreach (var entry in entries)
                if (!byExecution.TryAdd(Id(entry, "executionId"), entry)) throw Refuse();
            var resultIds = new HashSet<Guid>();
            var resultExecutions = new HashSet<Guid>();
            var lists = Single(root, "TestLists").Elements(Ns + "TestList").Select(e => Id(e, "id")).ToHashSet();
            foreach (var result in results)
            {
                token.ThrowIfCancellationRequested();
                var testId = Id(result, "testId"); var executionId = Id(result, "executionId");
                if (Value(result, "outcome") != "Passed" || !resultIds.Add(testId) || !resultExecutions.Add(executionId) ||
                    !byId.TryGetValue(testId, out var definition) || !byExecution.TryGetValue(executionId, out var entry) ||
                    Id(Single(definition, "Execution"), "id") != executionId || Id(entry, "testId") != testId ||
                    Value(result, "testName") != Value(definition, "name") ||
                    Id(result, "testListId") != Id(entry, "testListId") || !lists.Contains(Id(result, "testListId"))) throw Refuse();
            }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException or OverflowException)
        { throw Refuse(); }
    }

    private static XDocument Parse(ImmutableArray<byte> bytes)
    {
        if (bytes.IsDefaultOrEmpty || bytes.Length > MaximumTrxBytes) throw Refuse();
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumTrxBytes });
        return XDocument.Load(reader);
    }
    private static int CountTests(ImmutableArray<byte> bytes) => Single(Parse(bytes).Root!, "Results").Elements().Count();
    private static ImmutableArray<byte> ReadTrx(string path, CancellationToken token)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refuse();
        if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
        var time = File.GetLastWriteTimeUtc(path).Ticks;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var length = stream.Length;
        if (!stream.CanSeek || length <= 0 || length > MaximumTrxBytes) throw Refuse();
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
            if (count == 0) throw Refuse();
            offset += count;
        }
        if (stream.ReadByte() != -1 || stream.Length != length || File.GetLastWriteTimeUtc(path).Ticks != time) throw Refuse();
        token.ThrowIfCancellationRequested();
        return System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    }
    private static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw Refuse();
        return path;
    }
    private static XElement Single(XElement parent, string name) => parent.Elements(Ns + name).Single();
    private static string Value(XElement element, string name) => element.Attribute(name)?.Value is { Length: > 0 } value ? value : throw Refuse();
    private static Guid Id(XElement element, string name) => Guid.TryParse(Value(element, name), out var value) && value != Guid.Empty ? value : throw Refuse();
    private static int Number(XElement element, string name, bool required)
    {
        var value = element.Attribute(name)?.Value;
        if (value is null && !required) return 0;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0 ? count : throw Refuse();
    }
    private static string Hash(ImmutableArray<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes.AsSpan())).ToLowerInvariant();
    private static ProjectContextException Refuse() => new("test.resultsUnproven", "Required VSTest results, individual identities, counters or independently bound test module are missing, failed or inconsistent.");
}
