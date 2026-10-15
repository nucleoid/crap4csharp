using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

public static class SemanticEligibility
{
    public static MetadataReferenceProperties ReferenceProperties(ManifestInput input)
    {
        if (input.Role != "reference") throw new InvalidDataException("Reference properties require a reference input.");
        var text = input.ReferenceAliases;
        if (text is not null && text.Length > 32768) throw new InvalidDataException("Reference aliases exceed the supported limit.");
        var aliases = text is null ? [] : text.Split(',');
        if (aliases.Any(alias => alias.Length == 0 || alias != "global" && !SyntaxFacts.IsValidIdentifier(alias)) ||
            aliases.Distinct(StringComparer.Ordinal).Count() != aliases.Length)
            throw new InvalidDataException("Reference aliases are invalid.");
        return new MetadataReferenceProperties(MetadataImageKind.Assembly,
            System.Collections.Immutable.ImmutableArray.CreateRange(aliases), input.EmbedInteropTypes ?? false);
    }
    public static string ReferenceLogicalPath(string sha256, string? aliases, bool embedded) =>
        "references/" + (aliases is null && !embedded ? sha256 :
            CanonicalIdentity.Tuple("reference-properties-v1", sha256, aliases ?? "", embedded ? "true" : "false")) + ".dll";

    public static PortableExecutableReference CapturedReference(ManifestInput input,
        System.Collections.Immutable.ImmutableArray<byte> bytes)
    {
        var properties = ReferenceProperties(input);
        var names = properties.Aliases;
        if (input.EmbedInteropTypes == false ||
            names.Length == 1 && names[0] == "global" ||
            !names.SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Captured reference properties are not canonical.");
        // Preserve legacy arbitrary default reference names. The reserved canonical
        // namespace always binds properties, including their removal after resealing.
        var path = input.LogicalPath;
        var canonicalName = path.StartsWith("references/", StringComparison.Ordinal) &&
            path.EndsWith(".dll", StringComparison.Ordinal) && path.Length == 79 &&
            path.AsSpan(11, 64).IndexOfAnyExcept("0123456789abcdef") < 0;
        if ((canonicalName || input.ReferenceAliases is not null || properties.EmbedInteropTypes) &&
            path != ReferenceLogicalPath(CanonicalIdentity.Sha256(bytes.AsSpan()),
                input.ReferenceAliases, properties.EmbedInteropTypes))
            throw new InvalidDataException("Captured reference identity does not bind its properties.");
        return MetadataReference.CreateFromImage(bytes, properties);
    }

    internal static bool ValidateEmptyCaptured(ManifestContext context,
        IReadOnlyDictionary<string, System.Collections.Immutable.ImmutableArray<byte>> bytes)
    {
        try
        {
            var options = context.ParseOptions!;
            if (!LanguageVersionFacts.TryParse(options.LanguageVersion, out var version) || options.SourceKind != "Regular") return false;
            var parse = new CSharpParseOptions(version, DocumentationMode.Parse, SourceCodeKind.Regular, options.PreprocessorSymbols)
                .WithFeatures(options.Features);
            var inputs = context.Inputs.Where(i => i.Role is "source" or "compiler-source").ToArray();
            if (inputs.Length == 0) return false;
            var captured = inputs.Select(i => CapturedSource.Create(i.LogicalPath, bytes[i.Locator].AsSpan())).ToArray();
            var eligible = inputs.Where(i => !i.Generated || context.IncludeGenerated).Select(i => CapturedSource.Create(i.LogicalPath, bytes[i.Locator].AsSpan())).ToArray();
            var references = context.Inputs.Where(i => i.Role == "reference")
                .Select(i => CapturedReference(i, bytes[i.Locator])).ToArray();
            return !HasApplicable(eligible, captured, new(context.Project,context.TargetFramework,context.Configuration,context.Platform,context.Id,parse),references);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or KeyNotFoundException or BadImageFormatException)
        { return false; }
    }

    // Empty PDB/report membership is never eligibility evidence. Inspect executable
    // syntax against the full retained compiler tree/reference closure instead.
    public static bool HasApplicable(IReadOnlyList<CapturedSource> eligibleSources,
        IReadOnlyList<CapturedSource> compilerSources, CallableAnalysisContext context,
        IEnumerable<MetadataReference> references)
    {
        var trees = compilerSources.Select(s => CSharpSyntaxTree.ParseText(s.Text, context.ParseOptions, s.LogicalPath)).ToArray();
        // Preserve the public helper's support for eligible sources outside the
        // supplied auxiliary closure; only exact captured members share a model.
        if (trees.Select(t => t.FilePath).Distinct(StringComparer.Ordinal).Count() != trees.Length ||
            eligibleSources.Any(s => !compilerSources.Any(c => c.LogicalPath == s.LogicalPath && c.Text == s.Text)))
            return eligibleSources.Any(s => CallableInventory.Analyze(s.Text, s.LogicalPath, context, references, trees)
                .Callables.Any(c => c.Applicability == CallableApplicability.Applicable));
        var compilation = CSharpCompilation.Create("Crap4CSharp.CallableInventory", trees, references);
        return eligibleSources.Any(s => CallableInventory.AnalyzeCaptured(s, context, compilation)
            .Callables.Any(c => c.Applicability == CallableApplicability.Applicable));
    }
}
