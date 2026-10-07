using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

public static class SemanticEligibility
{
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
            var references = context.Inputs.Where(i => i.Role == "reference").Select(i => MetadataReference.CreateFromImage(bytes[i.Locator])).ToArray();
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
        return eligibleSources.Any(s => CallableInventory.Analyze(s.Text, s.LogicalPath, context, references, trees)
            .Callables.Any(c => c.Applicability == CallableApplicability.Applicable));
    }
}
