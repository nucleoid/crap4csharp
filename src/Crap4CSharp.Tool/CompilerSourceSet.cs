using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Crap4CSharp.Core;

// One private union for semantic/captured documents. Physical Csc Sources remain separate.
// No generated path is opened: bytes come only from the actual Workspace tree vector.
internal sealed class CompilerSourceSet
{
    private readonly Dictionary<string, (ProjectSourceIdentity Identity, ImmutableArray<byte> Bytes, string EncodingIdentity)> generated;
    internal IReadOnlyList<ProjectSourceIdentity> Documents { get; }
    internal IReadOnlyList<ProjectSourceIdentity> GeneratedDocuments { get; }
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private CompilerSourceSet(ProjectAnalysisContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.GeneratedCompilerDocuments is null || context.GeneratorOnlyTreeCount != context.GeneratedCompilerDocuments.Count ||
            context.GeneratedCompilerDocuments.Count > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
        generated = new(PathIdentityPolicy.Current.Comparer);
        var documents = new List<ProjectSourceIdentity>(context.CompilerDocuments);
        var paths = context.CompilerDocuments.Select(d => d.ResolvedPath!).ToHashSet(PathIdentityPolicy.Current.Comparer);
        var logical = context.CompilerDocuments.Select(d => d.LogicalPath).ToHashSet(StringComparer.Ordinal);
        if (paths.Count != documents.Count || logical.Count != documents.Count)
            throw new ProjectContextException("compiler.outputsUnproven",
                "Physical compiler document paths or logical identities are duplicated.");
        long characters = 0, bytes = 0;
        foreach (var document in context.GeneratedCompilerDocuments)
        {
            token.ThrowIfCancellationRequested();
            if (document is null || string.IsNullOrWhiteSpace(document.Path) || document.Path.Length > 4096 ||
                !Path.IsPathFullyQualified(document.Path) || Path.GetFullPath(document.Path) != document.Path ||
                document.Path.Any(char.IsControl) || string.IsNullOrWhiteSpace(document.LogicalPath) ||
                document.BytesBase64 is null || document.Checksum is null || document.EncodingName != "utf-8" ||
                document.BytesBase64.Length > ((CompilerPhaseSnapshot.MaximumFileBytes + 2) / 3) * 4 ||
                document.EncodingPreambleBase64 is not ("" or "77u/") ||
                document.ChecksumAlgorithm is not ("sha1" or "sha256") ||
                !paths.Add(document.Path) || !logical.Add(document.LogicalPath)) throw Refuse();
            if (CanonicalIdentity.NormalizeLogicalPath(document.LogicalPath) != document.LogicalPath) throw Refuse();
            byte[] content;
            try { content = Convert.FromBase64String(document.BytesBase64); }
            catch (FormatException) { throw Refuse(); }
            if (Convert.ToBase64String(content) != document.BytesBase64) throw Refuse();
            var text = Utf8.GetString(content);
            characters = checked(characters + text.Length); bytes = checked(bytes + content.Length);
            if (characters > 16 * 1024 * 1024 || content.Length > CompilerPhaseSnapshot.MaximumFileBytes ||
                bytes > CompilerPhaseSnapshot.MaximumClosureBytes) throw Refuse();
            // SHA1 is private Workspace-transport consistency only. The source
            // identity below is always SHA256, and actual output binding still
            // requires SHA256 PDB documents through ArtifactEvidenceInspector.
            var checksum = document.ChecksumAlgorithm == "sha256" ? SHA256.HashData(content) : SHA1.HashData(content);
            if (Convert.ToHexStringLower(checksum) != document.Checksum) throw Refuse();
            var identity = new ProjectSourceIdentity(document.LogicalPath, document.LogicalPath,
                CanonicalIdentity.Sha256(content), true, false) { ResolvedPath = document.Path };
            generated.Add(document.Path, (identity, ImmutableArray.Create(content),
                CanonicalIdentity.Tuple("generated-source-encoding-v1", document.EncodingName,
                    document.EncodingPreambleBase64, document.ChecksumAlgorithm)));
            documents.Add(identity);
        }
        if (documents.Count > CompilerPhaseSnapshot.MaximumInputs) throw Refuse();
        Documents = Array.AsReadOnly(documents.ToArray());
        GeneratedDocuments = Array.AsReadOnly(generated.Values.Select(d => d.Identity).ToArray());
    }

    internal static CompilerSourceSet Create(ProjectAnalysisContext context, CancellationToken token)
    {
        try { return new(context, token); }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        { throw Refuse(); }
    }
    // Classification only, not compiler proof. Match the private captured-tree
    // origin exactly; physical <external> identities remain physical inputs.
    internal static bool IsCapturedGeneratedSource(ProjectAnalysisContext context, ProjectSourceIdentity source) =>
        source.IsGenerated && source.ResolvedPath is not null &&
        context.GeneratedCompilerDocuments.Any(document =>
            document.Path == source.ResolvedPath && document.LogicalPath == source.LogicalPath &&
            document.LogicalPath == source.PhysicalPath);

    internal bool IsGeneratorTree(ProjectSourceIdentity source) => generated.ContainsKey(source.ResolvedPath!);
    internal string? EncodingIdentity(ProjectSourceIdentity source) =>
        generated.TryGetValue(source.ResolvedPath!, out var document) ? document.EncodingIdentity : null;
    internal ImmutableArray<byte> Read(ProjectSourceIdentity source, ref long total, CancellationToken token,
        CompilerPathBatch? paths = null)
    {
        token.ThrowIfCancellationRequested();
        if (!generated.TryGetValue(source.ResolvedPath!, out var document))
            return CompilerOutputBinding.Read(source.ResolvedPath!, ref total, token, paths);
        if (source != document.Identity) throw Refuse();
        total = checked(total + document.Bytes.Length);
        if (total > CompilerPhaseSnapshot.MaximumClosureBytes) throw Refuse();
        return document.Bytes;
    }

    internal static ProjectGeneratedSourceDocument Capture(SyntaxTree tree, string directory, string project,
        CancellationToken token)
    {
        try { return CaptureCore(tree, directory, project, token); }
        catch (ArgumentException) { throw Refuse(); }
    }

    private static ProjectGeneratedSourceDocument CaptureCore(SyntaxTree tree, string directory, string project,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tree.FilePath)) throw Refuse();
        var text = tree.GetText(token);
        if (text.Encoding?.CodePage != 65001 || text.Length > 16 * 1024 * 1024 ||
            text.ChecksumAlgorithm is not (SourceHashAlgorithm.Sha1 or SourceHashAlgorithm.Sha256)) throw Refuse();
        var path = Path.GetFullPath(tree.FilePath, directory);
        var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
        // This path is an opaque compiler identity, never a file to open. An
        // evaluated intermediate directory may legitimately be outside the project.
        var logical = "<generated>/" + CanonicalIdentity.Tuple("actual-generator-tree-v1", project, relative) + ".cs";
        var body = Utf8.GetBytes(text.ToString());
        var preamble = text.Encoding.GetPreamble();
        if (preamble.Length != 0 && !preamble.AsSpan().SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })) throw Refuse();
        var expected = text.GetChecksum().AsSpan();
        byte[] Hash(byte[] value) => text.ChecksumAlgorithm == SourceHashAlgorithm.Sha256 ? SHA256.HashData(value) : SHA1.HashData(value);
        // The exact actual SourceText checksum decides whether its original bytes
        // include the encoding preamble. Never assume UTF8(ToString) is the source.
        var content = body;
        if (!Hash(content).AsSpan().SequenceEqual(expected))
        {
            content = preamble.Concat(body).ToArray();
            if (preamble.Length == 0 || !Hash(content).AsSpan().SequenceEqual(expected)) throw Refuse();
        }
        return new(path, logical, Convert.ToBase64String(content), "utf-8", Convert.ToBase64String(preamble),
            text.ChecksumAlgorithm == SourceHashAlgorithm.Sha256 ? "sha256" : "sha1", Convert.ToHexStringLower(expected));
    }

    private static ProjectContextException Refuse() => new("compiler.generatorBindingUnsupported",
        "Actual generated tree bytes, encoding, membership or checksum cannot be bound uniquely to this compiler context.");
}
