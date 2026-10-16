using System.Security.Cryptography;
using System.Text.Json;

namespace Crap4CSharp.Core;

// Observed MSBuild task scope only, never universal compilation/runtime attestation.
internal sealed record TestChildObservation(string ProjectPath, int CompilerTasks, int VSTestTasks,
    bool BuildSucceeded, string BinlogSha256)
{
    public int UnsupportedTasks { get; init; }
    public string? UnsupportedDetail { get; init; }
}
internal sealed record TestChildReadRequest(string SdkPath, string Binlog, string ProjectPath);
internal sealed record TestChildReadResponse(TestChildObservation? Observation, string? FailureReason, string? FailureDetail = null);

internal static class TestChildEvidence
{
    internal const string Command = "__crap4csharp-test-child-evidence-v1";
    internal const int MaximumTransportBytes = 65536;

    internal static async Task<TestChildObservation> ReadAsync(string binlog, string project,
        TimeSpan timeout, CancellationToken token, bool nativeSdkIdentityProbe = false, ProjectContextSdkResolution? frozenSdk = null)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(binlog);
        try
        {
            for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw Refuse("test.childLogUnproven");
            if (!ArtifactBundle.IsRegularInput(path) || new FileInfo(path).Length > CompilerEvidence.MaximumBinlogBytes)
                throw Refuse("test.childLogUnproven");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Refuse("test.childLogUnproven");
        }
        var sdk = await SdkExecutionIdentity.ResolveForEffectAsync(Path.GetDirectoryName(project)!, timeout, token, nativeSdkIdentityProbe, frozenSdk);
        var scratch = Directory.CreateTempSubdirectory("crap4csharp-test-child-").FullName;
        Exception? primaryFailure = null;
        try
        {
            var request = Path.Combine(scratch, "request.json"); var response = Path.Combine(scratch, "response.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new TestChildReadRequest(sdk.Path, path,
                Path.GetFullPath(project)), ProjectContextLoader.JsonOptions), token);
            var result = await ProcessRunner.RunProtocolAsync(sdk.HostPath,
                [typeof(TestChildEvidence).Assembly.Location, Command, request, response], Path.GetDirectoryName(project)!,
                timeout, token, maximumBytesPerStream: 4096);
            if (!File.Exists(response) || new FileInfo(response).Length > MaximumTransportBytes) throw Refuse("test.childProtocolInvalid");
            var parsed = ParseResponse(await File.ReadAllTextAsync(response, token));
            if (parsed?.FailureReason is { } reason) throw new ProjectContextException(reason,
                "Test-child worker refused: " + reason + ". " + parsed.FailureDetail);
            if (result.ExitCode != 0 || parsed?.Observation is null) throw Refuse("test.childProtocolInvalid");
            token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            if (stream.Length > CompilerEvidence.MaximumBinlogBytes ||
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != parsed.Observation.BinlogSha256) throw Refuse("test.childLogUnproven");
            return parsed.Observation;
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try { Directory.Delete(scratch, true); }
            // Retain only this invocation's private scratch when cleanup fails.
            // Cleanup must never replace a worker refusal, deadline or cancellation.
            catch (Exception error) when (primaryFailure is not null && (error is IOException or UnauthorizedAccessException)) { }
        }
    }

    internal static TestChildReadResponse ParseResponse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<TestChildReadResponse>(payload, ProjectContextLoader.JsonOptions)
                ?? throw Refuse("test.childProtocolInvalid");
        }
        catch (JsonException)
        {
            // The adapter retains the already-observed raw test process when
            // this typed refusal reaches its operational-error handler. Never
            // include malformed response contents in the public failure.
            throw Refuse("test.childProtocolInvalid");
        }
    }

    internal static void Validate(TestChildObservation observation, LoggedProcessResult raw)
    {
        if (observation.CompilerTasks != 0) throw Refuse("test.compilationObserved");
        if (observation.UnsupportedTasks != 0) throw new ProjectContextException("test.executionHookUnsupported",
            "Unsupported actual task association: " + observation.UnsupportedDetail);
        if (observation.VSTestTasks != 1 || observation.BuildSucceeded != (raw.ExitCode == 0)) throw Refuse("test.childAssociationUnproven");
    }
    private static ProjectContextException Refuse(string reason) => new(reason,
        "Actual selected-SDK test-child task evidence refused: " + reason + "; inspect private owned diagnostics.");
}
