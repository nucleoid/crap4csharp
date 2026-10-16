using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Private default-off diagnostic observation. This is not an acceptance proof,
// cache or public context hash. Only actual acquired Project fields are observed.
internal static class WarmProjectObservation
{
    internal static async Task<string> CaptureAsync(Project project, CancellationToken token)
    {
        if (project.ParseOptions is not CSharpParseOptions parse || project.CompilationOptions is not CSharpCompilationOptions options)
            throw new ProjectContextException("context.parseOptionsUnavailable", "C# project options are unavailable for observation.");
        var documents = new List<object>();
        foreach (var (role, entries) in new[] {
            ("source", project.Documents.Cast<TextDocument>()),
            ("additional", project.AdditionalDocuments),
            ("config", project.AnalyzerConfigDocuments) })
        foreach (var document in entries.OrderBy(d => d.FilePath, StringComparer.Ordinal).ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var text = await document.GetTextAsync(token);
            documents.Add(new { role, document.FilePath, document.Name, Folders = document.Folders.ToArray(),
                Encoding = text.Encoding?.WebName, CodePage = text.Encoding?.CodePage,
                text.ChecksumAlgorithm, Checksum = Convert.ToHexString(text.GetChecksum().ToArray()),
                SourceKind = document is Document source ? source.SourceCodeKind.ToString() : null });
        }
        return JsonSerializer.Serialize(new {
            project.FilePath, project.Name, project.AssemblyName, project.Language,
            project.OutputFilePath, project.OutputRefFilePath, project.DefaultNamespace,
            Parse = new { parse.LanguageVersion, parse.SpecifiedLanguageVersion, parse.Kind,
                parse.SpecifiedKind, parse.DocumentationMode,
                Symbols = parse.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(),
                Features = parse.Features.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() },
            Compilation = new { options.OutputKind, options.ModuleName, options.ScriptClassName,
                options.MainTypeName, CryptoPublicKey = Convert.ToHexString(options.CryptoPublicKey.ToArray()),
                options.CryptoKeyFile, options.CryptoKeyContainer, options.DelaySign, options.PublicSign,
                options.CheckOverflow, options.Platform, options.OptimizationLevel,
                options.GeneralDiagnosticOption, options.WarningLevel, options.ConcurrentBuild,
                options.Deterministic, options.MetadataImportOptions, options.ReportSuppressedDiagnostics,
                options.NullableContextOptions, options.AllowUnsafe, Usings = options.Usings.ToArray(),
                SpecificDiagnostics = options.SpecificDiagnosticOptions.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
                MetadataResolver = options.MetadataReferenceResolver?.GetType().FullName,
                XmlResolver = options.XmlReferenceResolver?.GetType().FullName,
                SourceResolver = options.SourceReferenceResolver?.GetType().FullName,
                StrongNameProvider = options.StrongNameProvider?.GetType().FullName,
                AssemblyComparer = options.AssemblyIdentityComparer.GetType().FullName },
            MetadataReferences = project.MetadataReferences.Select(reference => new {
                reference.Display, FilePath = (reference as PortableExecutableReference)?.FilePath,
                reference.Properties.Kind, Aliases = reference.Properties.Aliases.ToArray(),
                reference.Properties.EmbedInteropTypes }).OrderBy(reference => reference.Display, StringComparer.Ordinal).ToArray(),
            ProjectReferences = project.ProjectReferences.Select(reference => new {
                FilePath = project.Solution.GetProject(reference.ProjectId)?.FilePath,
                Aliases = reference.Aliases.ToArray(), reference.EmbedInteropTypes
            }).OrderBy(reference => reference.FilePath, StringComparer.Ordinal).ToArray(),
            Analyzers = project.AnalyzerReferences.Select(reference => new {
                reference.FullPath, reference.Display, Type = reference.GetType().FullName
            }).OrderBy(reference => reference.FullPath, StringComparer.Ordinal).ToArray(),
            Documents = documents
        });
    }
}
