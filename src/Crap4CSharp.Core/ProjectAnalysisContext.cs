using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Buffers.Binary;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

public sealed record ProjectSourceIdentity(
    string PhysicalPath,
    string LogicalPath,
    string ContentIdentity,
    bool IsGenerated,
    bool IsExternal)
{
    [JsonIgnore]
    public string? ResolvedPath { get; init; }
}

public sealed record ProjectSourceDocument(
    ProjectSourceIdentity Identity,
    string Text,
    IReadOnlyList<string> Folders);

public sealed record ProjectSourceExclusion(string PhysicalPath, string LogicalPath, string Reason, string? ContentIdentity = null);
public sealed record ProjectExclusionPolicy(bool IncludeTests, bool IncludeGenerated);
public sealed record ProjectAdapterIdentity(string SdkVersion, string MsBuildVersion, string RoslynVersion);

public sealed record ProjectContextCapabilities(
    bool DesignTimeComplete,
    bool CompiledInputBindingComplete,
    bool ReuseRecipeComplete,
    string? Limitation);

public sealed record ProjectAnalysisContext(
    string ContextId,
    string SourceSetIdentity,
    string AnalysisMode,
    string ProjectPath,
    string AssemblyName,
    string TargetFramework,
    string Configuration,
    string Platform,
    LanguageVersion LanguageVersion,
    IReadOnlyList<string> PreprocessorSymbols,
    SourceCodeKind SourceKind,
    IReadOnlyList<string> Imports,
    IReadOnlyList<ProjectSourceIdentity> Sources,
    IReadOnlyList<ProjectSourceExclusion> Exclusions,
    ProjectExclusionPolicy ExclusionPolicy,
    ProjectAdapterIdentity Adapter,
    ProjectContextCapabilities Capabilities)
{
    public const string ProtocolVersion = "project-context-v1";

    [JsonIgnore]
    public IReadOnlyList<string> ProtectedPaths { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> GeneratedTransitionPaths { get; init; } = [];

    public static ProjectAnalysisContext Create(
        string projectPath,
        string assemblyName,
        string targetFramework,
        string configuration,
        string platform,
        LanguageVersion languageVersion,
        IEnumerable<string> preprocessorSymbols,
        SourceCodeKind sourceKind,
        IEnumerable<string> imports,
        IEnumerable<ProjectSourceIdentity> sources,
        ProjectExclusionPolicy exclusionPolicy,
        ProjectAdapterIdentity adapter,
        IEnumerable<ProjectSourceExclusion>? exclusions = null,
        ProjectContextCapabilities? capabilities = null)
    {
        var orderedSymbols = preprocessorSymbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var orderedImports = imports.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var orderedSources = sources.OrderBy(source => source.LogicalPath, StringComparer.Ordinal)
            .ThenBy(source => source.PhysicalPath, StringComparer.Ordinal).ToArray();
        var orderedExclusions = (exclusions ?? []).OrderBy(exclusion => exclusion.LogicalPath, StringComparer.Ordinal)
            .ThenBy(exclusion => exclusion.PhysicalPath, StringComparer.Ordinal).ToArray();
        var sourceSet = Hash(orderedSources.Select(source => string.Join("\n", source.PhysicalPath.Replace('\\', '/'),
            source.LogicalPath.Replace('\\', '/'), source.ContentIdentity, source.IsGenerated, source.IsExternal)));
        var exclusionSet = Hash(orderedExclusions.Select(exclusion => string.Join("\n", exclusion.PhysicalPath.Replace('\\', '/'),
            exclusion.LogicalPath.Replace('\\', '/'), exclusion.Reason, exclusion.ContentIdentity)));
        var contextId = Hash([
            ProtocolVersion, projectPath.Replace('\\', '/'), assemblyName, targetFramework, configuration, platform,
            languageVersion.ToString(), sourceKind.ToString(), string.Join(";", orderedSymbols), string.Join(";", orderedImports),
            sourceSet, exclusionSet, exclusionPolicy.IncludeTests.ToString(), exclusionPolicy.IncludeGenerated.ToString(),
            adapter.SdkVersion, adapter.MsBuildVersion, adapter.RoslynVersion
        ]);
        return new ProjectAnalysisContext(contextId, sourceSet, "project", projectPath, assemblyName, targetFramework,
            configuration, platform, languageVersion, orderedSymbols, sourceKind, orderedImports, orderedSources,
            orderedExclusions, exclusionPolicy, adapter,
            capabilities ?? new ProjectContextCapabilities(true, false, false,
                "Design-time inventory only; compiled inputs must be bound to a declared build."));
    }

    [JsonIgnore]
    public CSharpParseOptions ParseOptions => new(LanguageVersion, kind: SourceKind, preprocessorSymbols: PreprocessorSymbols);

    private static string Hash(IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string ContentHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record ContextualSourceMethod(string ContextId, SourceMethod Method);
public sealed record ContextualCoverageReport(string ContextId, IReadOnlyList<CoverageMethod> Methods);
