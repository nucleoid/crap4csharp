using System.Collections.Immutable;
using System.Text.Json;

namespace Crap4CSharp.Core;

// Safe child-event projection only. Raw binlogs may contain secrets and stay private.
// This does not attest runtime loading or establish a future reuse recipe.
internal static class TestChildEvidenceExport
{
    internal static ImmutableArray<byte> Capture(TestInvocationObservation invocation,
        TestContextOutcome outcome, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var child = invocation.ChildBuild ?? throw Refuse();
            if (outcome.GraphId != invocation.GraphId || outcome.State != "completed" ||
                outcome.Process != invocation.Process || outcome.ResultsDirectory != invocation.ResultsDirectory ||
                outcome.LogDirectory is null || invocation.Process.ExitCode != 0 ||
                !PathIdentityPolicy.Current.Comparer.Equals(child.ProjectPath, invocation.ProjectPath)) throw Refuse();
            TestChildEvidence.Validate(child, invocation.Process);
            if (child.BinlogSha256.Length != 64 || child.BinlogSha256.Any(c => !char.IsAsciiHexDigit(c))) throw Refuse();
            long total = 0;
            var bytes = CompilerOutputBinding.Read(Path.Combine(outcome.LogDirectory, "test-child.binlog"), ref total, token);
            if (CanonicalIdentity.Sha256(bytes.AsSpan()) != child.BinlogSha256) throw Refuse();
            return ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(new
            {
                provider = "selected-sdk-msbuild-child-v1",
                graphId = invocation.GraphId,
                compilerTasks = child.CompilerTasks,
                vstestTasks = child.VSTestTasks,
                buildSucceeded = child.BuildSucceeded,
                rawExitCode = invocation.Process.ExitCode,
                privateLogSha256 = child.BinlogSha256,
                supportedTaskAssociations = child.UnsupportedTasks == 0,
                runtimeLoadingAttested = false,
                reusableRecipe = false
            }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ProjectContextException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or OverflowException)
        { throw Refuse(); }
    }

    private static ProjectContextException Refuse() => new("test.childExportUnproven",
        "Safe test-child evidence export requires the supported successful raw invocation and its unchanged independently observed private log.");
}
