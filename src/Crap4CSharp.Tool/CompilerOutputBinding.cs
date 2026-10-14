using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Private phase-C output gate; references, generator-only trees and event-time source
// bytes remain separate obligations. This does not assert compiled/reusable capability.
internal static class CompilerOutputBinding
{
    internal static void Validate(ProjectAnalysisContext context, CompilerObservation observation, CancellationToken token,
        CompilerFileReadScope? scope = null)
        => _ = ValidateAndInspect(context, observation, token, scope);

    internal static ValidatedCompilerOutput ValidateAndInspect(ProjectAnalysisContext context,
        CompilerObservation observation, CancellationToken token, CompilerFileReadScope? scope = null)
    {
        token.ThrowIfCancellationRequested();
        if (context.GeneratorOnlyTreeCount != 0)
            throw new ProjectContextException("compiler.generatorBindingUnsupported", "Generator-only syntax trees require actual compiler binding that this adapter does not support.");
        CompilerInputBinding.Validate(context, observation, token, scope);
        try
        {
            var paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var directory = Path.GetDirectoryName(Canonical(context.ResolvedProjectPath))!;
            var assembly = observation.Assembly ?? throw Refuse();
            var pdb = observation.Pdb ?? throw Refuse();
            var assemblyPath = Canonical(assembly.Path);
            var pdbPath = Canonical(pdb.Path);
            var actualOutput = observation.Parameters.Single(p => p.Name == "OutputAssembly").Items.Single();
            if (!paths.Equals(Path.GetFullPath(actualOutput, directory), assemblyPath) ||
                !paths.Equals(Path.ChangeExtension(assemblyPath, ".pdb"), pdbPath)) throw Refuse();
            var intermediate = Canonical(context.EvaluatedIntermediateDirectory);
            var prefix = Path.EndsInDirectorySeparator(intermediate) ? intermediate : intermediate + Path.DirectorySeparatorChar;
            if (!assemblyPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw Refuse();
            var target = Canonical(context.EvaluatedTargetPath);
            var targetPdb = Path.ChangeExtension(target, ".pdb");
            long total = 0;
            var peBytes = Read(assemblyPath, ref total, token);
            var pdbBytes = Read(pdbPath, ref total, token);
            Match(assembly, peBytes); Match(pdb, pdbBytes);
            var copiedPe = Read(target, ref total, token);
            var copiedPdb = Read(targetPdb, ref total, token);
            if (!peBytes.AsSpan().SequenceEqual(copiedPe.AsSpan()) || !pdbBytes.AsSpan().SequenceEqual(copiedPdb.AsSpan())) throw Refuse();
            token.ThrowIfCancellationRequested();
            // Inspector owns additional finite PE/PDB copies. File/closure limits do not
            // constitute an OS memory limit or hostile-filesystem atomicity guarantee.
            var inspected = ArtifactEvidenceInspector.InspectBuild(peBytes, pdbBytes);
            ValidatePortableDebugIdentity(peBytes, pdbBytes);
            if (context.SourceKind != SourceCodeKind.Regular || inspected.ModuleIdentity != context.AssemblyName ||
                !LanguageVersionFacts.TryParse(inspected.LanguageVersion, out var language) ||
                language != context.ParseOptions.LanguageVersion ||
                !inspected.PreprocessorSymbols.SequenceEqual(context.PreprocessorSymbols.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw Refuse();
            var documents = context.CompilerDocuments.ToDictionary(d => Canonical(d.ResolvedPath), paths);
            foreach (var (name, hash) in inspected.Documents)
            {
                token.ThrowIfCancellationRequested();
                // Conservatively refuse path-mapped/#line aliases that do not resolve to
                // an independently enumerated compiler source. Sources without points
                // need not have a PDB document; the complete Sources gate ran first.
                var path = Path.GetFullPath(name, directory);
                if (!documents.TryGetValue(path, out var source) || source.ContentIdentity != hash) throw Refuse();
            }
            token.ThrowIfCancellationRequested();
            return new(context, observation, peBytes, pdbBytes, inspected);
        }
        catch (ProjectContextException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or BadImageFormatException)
        { throw Refuse(); }
    }

    private static void ValidatePortableDebugIdentity(ImmutableArray<byte> peBytes, ImmutableArray<byte> pdbBytes)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(peBytes);
        var entry = pe.ReadDebugDirectory().Single(e => e.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView);
        var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
        using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbImage(pdbBytes);
        var header = provider.GetMetadataReader().DebugMetadataHeader ?? throw Refuse();
        var id = new System.Reflection.Metadata.BlobContentId(header.Id);
        if (!entry.IsPortableCodeView || codeView.Age != 1 || codeView.Guid != id.Guid || entry.Stamp != id.Stamp) throw Refuse();
    }

    internal static void Match(CompilerOutput output, ImmutableArray<byte> bytes)
    {
        if (output.Length != bytes.Length || output.Sha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan())).ToLowerInvariant()) throw Refuse();
    }

    private static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw Refuse();
        return path;
    }

    internal static ImmutableArray<byte> Read(string path, ref long total, CancellationToken token)
        => Read(path, ref total, token, null);

    internal static ImmutableArray<byte> Read(string path, ref long total, CancellationToken token, CompilerPathBatch? batch)
    {
        token.ThrowIfCancellationRequested();
        if (batch is not null) batch.Check(path);
        else
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refuse();
        if (!ArtifactBundle.IsRegularInput(path)) throw Refuse();
        var time = File.GetLastWriteTimeUtc(path).Ticks;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var length = stream.Length;
        if (!stream.CanSeek || length > CompilerPhaseSnapshot.MaximumFileBytes) throw Limit();
        total = checked(total + length);
        if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Limit();
        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
            if (count == 0) throw Refuse();
            offset += count;
        }
        token.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1 || stream.Length != length || File.GetLastWriteTimeUtc(path).Ticks != time) throw Refuse();
        return System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    }

    private static ProjectContextException Refuse() => new("compiler.outputsUnproven", "Actual compiler PE/PDB, copied outputs, options or document checksums do not match the final selected context.");
    private static ProjectContextException Limit() => new("compiler.outputLimit", "Compiler output binding exceeded its finite file or closure byte limit.");
}

internal sealed record ValidatedCompilerOutput(ProjectAnalysisContext Context, CompilerObservation Observation,
    ImmutableArray<byte> Pe, ImmutableArray<byte> Pdb, InspectedBuildEvidence Build);
