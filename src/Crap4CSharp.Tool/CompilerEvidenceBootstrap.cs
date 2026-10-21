using System.Text.Json;
using Microsoft.Build.Locator;

namespace Crap4CSharp.Core;

internal static class CompilerEvidenceBootstrap
{
    internal static async Task<int> RunAsync(string requestPath, string responsePath, CancellationToken token)
    {
        CompilerReadResponse response;
        try
        {
            if (new FileInfo(requestPath).Length > 65536) throw new InvalidDataException();
            var request = JsonSerializer.Deserialize<CompilerReadRequest>(await File.ReadAllTextAsync(requestPath, token), ProjectContextLoader.JsonOptions)
                ?? throw new InvalidDataException();
            MSBuildLocator.RegisterMSBuildPath(request.SdkPath);
            var observed = CompilerBinlogReader.Read(request, token);
            response = new(observed, null) { CommandLineArguments = observed.CommandLineArguments, EmitParameters = observed.EmitParameters };
        }
        catch (OperationCanceledException) { response = new(null, "compiler.cancelled"); }
        catch (ProjectContextException exception) { response = new(null, exception.Reason); }
        catch (Exception) { response = new(null, "compiler.binlogInvalid"); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ProjectContextLoader.JsonOptions);
        if (bytes.Length > CompilerEvidence.MaximumTransportBytes)
        {
            response = new(null, "compiler.evidenceLimit");
            bytes = JsonSerializer.SerializeToUtf8Bytes(response, ProjectContextLoader.JsonOptions);
        }
        await File.WriteAllBytesAsync(responsePath, bytes, token);
        return response.Observation is not null ? 0 : 1;
    }
}
