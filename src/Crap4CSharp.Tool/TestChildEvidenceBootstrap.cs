using System.Text.Json;
using Microsoft.Build.Locator;

namespace Crap4CSharp.Core;

internal static class TestChildEvidenceBootstrap
{
    internal static async Task<int> RunAsync(string requestPath, string responsePath, CancellationToken token)
    {
        TestChildReadResponse response;
        try
        {
            if (new FileInfo(requestPath).Length > TestChildEvidence.MaximumTransportBytes) throw new InvalidDataException();
            var request = JsonSerializer.Deserialize<TestChildReadRequest>(await File.ReadAllTextAsync(requestPath, token), ProjectContextLoader.JsonOptions)
                ?? throw new InvalidDataException();
            MSBuildLocator.RegisterMSBuildPath(request.SdkPath);
            response = new(TestChildBinlogReader.Read(request, token), null);
        }
        catch (OperationCanceledException) { response = new(null, "test.childCancelled"); }
        catch (ProjectContextException error) { response = new(null, error.Reason, error.Message); }
        catch (Exception error) { response = new(null, "test.childLogInvalid",
            error.GetType().Name + ": " + error.Message[..Math.Min(error.Message.Length, 2048)]); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ProjectContextLoader.JsonOptions);
        if (bytes.Length > TestChildEvidence.MaximumTransportBytes)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new TestChildReadResponse(null, "test.childEvidenceLimit"), ProjectContextLoader.JsonOptions);
        await File.WriteAllBytesAsync(responsePath, bytes, token);
        return response.Observation is not null ? 0 : 1;
    }
}
