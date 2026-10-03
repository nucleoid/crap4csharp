using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Crap4CSharp.Core;

public enum ChangeScopeMode { All, Worktree, Staged, Base }
public enum ScopeSourceState { Worktree, Head }
public enum ScopeGranularity { Method, File }
public enum ScopeChangeKind { Added, Modified, Deleted, Renamed, Copied }
public enum ScopeCompleteness { Complete, ContextIncomplete }

public sealed record LineRange(int StartLine, int EndLine)
{
    public bool Intersects(int startLine, int endLine) => StartLine <= endLine && EndLine >= startLine;
}

public sealed record CapturedSource(string LogicalPath, ImmutableArray<byte> Bytes, string ContentIdentity)
{
    public string Text => StrictUtf8.GetString(Bytes.AsSpan());

    public static CapturedSource Create(string logicalPath, ReadOnlySpan<byte> bytes)
    {
        _ = StrictUtf8.GetString(bytes);
        return new CapturedSource(logicalPath, ImmutableArray.Create(bytes.ToArray()),
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
}

public sealed record ChangedFile(
    string? OldPath,
    string? NewPath,
    ScopeChangeKind Kind,
    string? OldIdentity,
    string? NewIdentity,
    CapturedSource? OldSource,
    CapturedSource? NewSource,
    IReadOnlyList<LineRange> AddedRanges,
    IReadOnlyList<LineRange> DeletedRanges,
    string? OldObjectIdentity = null,
    string? NewObjectIdentity = null)
{
    public static ChangedFile Modified(string path, string oldIdentity, string newIdentity,
        IReadOnlyList<LineRange> added, IReadOnlyList<LineRange> deleted) =>
        new(path, path, ScopeChangeKind.Modified, oldIdentity, newIdentity, null, null, added, deleted);

    public static ChangedFile Renamed(string oldPath, string newPath, string oldIdentity, string newIdentity,
        IReadOnlyList<LineRange> added, IReadOnlyList<LineRange> deleted) =>
        new(oldPath, newPath, ScopeChangeKind.Renamed, oldIdentity, newIdentity, null, null, added, deleted);
}

public sealed record ScopeRevision(
    string? RequestedBase,
    string? RequestedHead,
    string? ResolvedBase,
    string? ResolvedHead,
    string? CurrentHead,
    string? MergeBase,
    string IndexIdentity);

public sealed record CapturedChangeScope(
    ChangeScopeMode Mode,
    ScopeSourceState SourceState,
    string RepositoryRoot,
    ScopeRevision Revision,
    IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<string> Diagnostics,
    ScopeCompleteness Completeness = ScopeCompleteness.Complete);

public sealed class ScopeException(string reason, string message, Exception? innerException = null)
    : InvalidOperationException($"{reason}: {message}", innerException)
{
    public string Reason { get; } = reason;
}

public sealed record GitScopeRequest(
    ChangeScopeMode Mode,
    string? BaseRef = null,
    string? HeadRef = null,
    ScopeSourceState SourceState = ScopeSourceState.Worktree);

public static class ScopeExecutionContract
{
    public const string UnsupportedSnapshotReason = "scope.executionSnapshotUnsupported";

    public static string? ValidateBeforeConsumerLoad(ChangeScopeMode mode, ScopeSourceState sourceState, Action consumerLoad)
    {
        if (mode == ChangeScopeMode.Staged || sourceState == ScopeSourceState.Head)
            return UnsupportedSnapshotReason;
        consumerLoad();
        return null;
    }

    public static async Task<IReadOnlyList<CheckResult>> RunRequiredChecksBeforeScoringAsync(
        int selectedMethodCount, Func<Task<IReadOnlyList<CheckResult>>> runRequiredChecks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selectedMethodCount);
        ArgumentNullException.ThrowIfNull(runRequiredChecks);
        return await runRequiredChecks();
    }
}
