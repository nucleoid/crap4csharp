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
            response = new(observed, null) { CommandLineArguments = observed.CommandLineArguments, EmitParameters = observed.EmitParameters,
                CommandRouteDiagnostic = observed.CommandRouteDiagnostic };
        }
        catch (OperationCanceledException) { response = new(null, "compiler.cancelled"); }
        catch (ProjectContextException exception) { response = new(null, exception.Reason); }
        catch (Exception) { response = new(null, "compiler.binlogInvalid"); }
        var bytes = SerializeResponse(ref response, token);
        await File.WriteAllBytesAsync(responsePath, bytes, token);
        return response.Observation is not null ? 0 : 1;
    }

    internal static byte[] SerializeResponse(ref CompilerReadResponse response, CancellationToken token,
        int maximumBytes = CompilerEvidence.MaximumTransportBytes)
    {
        token.ThrowIfCancellationRequested();
        if (response.Observation is { } original) CompilerRecipeObservation.RequireKnownIntegrity(original, token);
        // First preserve the complete mandatory observation under its existing
        // transport bound. Optional argv must not consume that capacity.
        var fits = CompilerRecipeBudget.TrySerialize(response, ProjectContextLoader.JsonOptions, maximumBytes, out var bytes);
        if (!fits && response.CommandRouteDiagnostic is not null)
        {
            // Diagnostics must not consume existing mandatory/optional transport capacity.
            response = response with { CommandRouteDiagnostic = null };
            fits = CompilerRecipeBudget.TrySerialize(response, ProjectContextLoader.JsonOptions, maximumBytes, out bytes);
        }
        if (!fits)
        {
            if (response.Observation is { } actual)
                response = new(CompilerRecipeObservation.OmitForSize(actual), null);
            if (!CompilerRecipeBudget.TrySerialize(response, ProjectContextLoader.JsonOptions, maximumBytes, out bytes))
            {
                if (response.Observation is { } mandatory)
                    response = new(CompilerRecipeObservation.WithoutOptionalRecipe(mandatory, token), null);
                if (!CompilerRecipeBudget.TrySerialize(response, ProjectContextLoader.JsonOptions, maximumBytes, out bytes))
                {
                    response = new(null, "compiler.evidenceLimit");
                    bytes = JsonSerializer.SerializeToUtf8Bytes(response, ProjectContextLoader.JsonOptions);
                }
            }
        }
        return bytes;
    }
}
