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

    // Validate only the safe saved task projection against an independently selected
    // required graph ID. The caller still owes compiler, runner, TRX, module, coverage
    // and complete schedule proof; no raw-log or runtime attestation is inferred here.
    internal static void ValidateSaved(ImmutableArray<byte> bytes, string requiredGraphId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (bytes.IsDefaultOrEmpty || bytes.Length > TestChildEvidence.MaximumTransportBytes ||
                string.IsNullOrWhiteSpace(requiredGraphId)) throw SavedRefuse();
            using var document = JsonDocument.Parse(bytes.AsMemory(), new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw SavedRefuse();
            var fields = new HashSet<string>(StringComparer.Ordinal)
            {
                "provider", "graphId", "compilerTasks", "vstestTasks", "buildSucceeded", "rawExitCode",
                "privateLogSha256", "supportedTaskAssociations", "runtimeLoadingAttested", "reusableRecipe"
            };
            foreach (var property in value.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (!fields.Remove(property.Name)) throw SavedRefuse();
            }
            if (fields.Count != 0 || value.GetProperty("provider").GetString() != "selected-sdk-msbuild-child-v1" ||
                value.GetProperty("graphId").GetString() != requiredGraphId ||
                value.GetProperty("compilerTasks").GetInt32() != 0 || value.GetProperty("vstestTasks").GetInt32() != 1 ||
                !value.GetProperty("buildSucceeded").GetBoolean() || value.GetProperty("rawExitCode").GetInt32() != 0 ||
                !value.GetProperty("supportedTaskAssociations").GetBoolean() ||
                value.GetProperty("runtimeLoadingAttested").GetBoolean() || value.GetProperty("reusableRecipe").GetBoolean())
                throw SavedRefuse();
            var hash = value.GetProperty("privateLogSha256").GetString();
            if (hash is null || hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw SavedRefuse();
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw SavedRefuse(); }
    }

    private static PolicyException SavedRefuse() => new("provenance.savedTestChildUnproven",
        "Saved test-child evidence is missing, unsupported, unsuccessful or differs from the required current test context.");

    private static ProjectContextException Refuse() => new("test.childExportUnproven",
        "Safe test-child evidence export requires the supported successful raw invocation and its unchanged independently observed private log.");
}
