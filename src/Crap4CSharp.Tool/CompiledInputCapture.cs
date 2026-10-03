using Crap4CSharp.Core;

namespace Crap4CSharp.Core;

public sealed record CompiledInputIdentity(string LogicalPath, string ContentIdentity, bool IsGenerated);
public sealed record CompiledInputEvidence(
    string ContextId,
    string AssemblyPath,
    string AssemblyContentIdentity,
    string? SymbolsPath,
    string? SymbolsContentIdentity,
    IReadOnlyList<CompiledInputIdentity> Inputs,
    bool Complete,
    string? FailureReason);

public static class CompiledInputCapture
{
    public static ProjectContextCapabilities Validate(ProjectAnalysisContext context, CompiledInputEvidence evidence)
    {
        if (!evidence.Complete || evidence.ContextId != context.ContextId)
            return new ProjectContextCapabilities(context.Capabilities.DesignTimeComplete, false, false,
                evidence.FailureReason ?? "context.compiledInputBindingIncomplete");
        var duplicate = evidence.Inputs.GroupBy(source => source.LogicalPath, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            return new ProjectContextCapabilities(context.Capabilities.DesignTimeComplete, false, false,
                "context.compiledInputDuplicate");
        var expected = context.Sources.Where(source => context.ExclusionPolicy.IncludeGenerated || !source.IsGenerated)
            .Select(source => (source.LogicalPath, source.ContentIdentity)).OrderBy(value => value.LogicalPath, StringComparer.Ordinal).ToArray();
        var observed = evidence.Inputs.Where(source => context.ExclusionPolicy.IncludeGenerated || !source.IsGenerated)
            .Select(source => (source.LogicalPath, source.ContentIdentity)).OrderBy(value => value.LogicalPath, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(observed))
            return new ProjectContextCapabilities(context.Capabilities.DesignTimeComplete, false, false,
                "context.compiledInputDrift");
        if (!File.Exists(evidence.AssemblyPath) || evidence.SymbolsPath is not null && !File.Exists(evidence.SymbolsPath))
            return new ProjectContextCapabilities(context.Capabilities.DesignTimeComplete, false, false,
                "context.compiledOutputUnavailable");
        if (ProjectAnalysisContext.ContentHash(File.ReadAllBytes(evidence.AssemblyPath)) != evidence.AssemblyContentIdentity ||
            evidence.SymbolsPath is not null && ProjectAnalysisContext.ContentHash(File.ReadAllBytes(evidence.SymbolsPath)) != evidence.SymbolsContentIdentity)
            return new ProjectContextCapabilities(context.Capabilities.DesignTimeComplete, false, false,
                "context.compiledOutputDrift");
        return new ProjectContextCapabilities(context.Capabilities.DesignTimeComplete, true, false,
            "Fresh compiled inputs are bound; a reusable membership/environment recipe has not been captured.");
    }
}
