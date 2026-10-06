using System.Text.Json;

namespace Crap4CSharp.Core;

// Physical paths and raw property values are private execution transport, not exported evidence.
internal sealed record CompilerParameter(string Name, IReadOnlyList<string> Items);
internal sealed record CompilerOutput(string Path, string Sha256, long Length);
internal sealed record CompilerObservation(string ProjectPath, IReadOnlyList<CompilerParameter> Parameters)
{
    internal CompilerOutput? Assembly { get; init; }
    internal CompilerOutput? Pdb { get; init; }
}
internal sealed record CompilerReadRequest(string SdkPath, string Binlog, string ProjectPath);
internal sealed record CompilerReadResponse(CompilerObservation? Observation, string? FailureReason);
internal static class CompilerEvidence
{
    internal const string Command = "__crap4csharp-compiler-evidence-v1";
    internal const long MaximumBinlogBytes = 64 * 1024 * 1024;
    internal const int MaximumTransportBytes = 8 * 1024 * 1024;

    internal static async Task<CompilerObservation> BuildAsync(ProjectBuildGraphNode node, string ownedDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var directory = Path.GetFullPath(ownedDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var log = Path.Combine(directory, "compiler.binlog");
        if (File.Exists(log)) throw new ProjectContextException("compiler.outputExists", "Compiler capture destination already exists.");
        if (log.IndexOfAny([';', ',']) >= 0)
            throw new ProjectContextException("compiler.outputPathUnsupported", "Compiler log path contains a logger delimiter.");
        var sdk = await ProjectContextLoader.ResolveSdkAsync(Path.GetDirectoryName(node.ProjectPath)!, timeout, token);
        var arguments = new List<string> { "build", node.ProjectPath, "--no-restore", "--no-incremental", "-m:1", "-nr:false" };
        foreach (var (key, value) in node.GlobalProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new ProjectContextException("compiler.globalPropertyInvalid", "A mapped global property name cannot be transported.");
            arguments.Add($"-p:{key}={Escape(value)}");
        }
        arguments.Add("-p:BuildProjectReferences=false");
        arguments.Add("-p:UseSharedCompilation=false");
        arguments.Add("-bl:" + log + ";ProjectImports=None");
        var process = await ProcessRunner.RunLoggedAsync(sdk.HostPath, arguments, Path.GetDirectoryName(node.ProjectPath)!,
            timeout, token, directory);
        if (!OperatingSystem.IsWindows() && File.Exists(log)) File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (process.ExitCode != 0) throw new ProjectContextException("compiler.buildFailed", "The declared nonincremental build failed; inspect private owned logs.");
        var observation = await ReadAsync(log, node.ProjectPath, timeout, token);
        var output = observation.Parameters.Single(p => p.Name == "OutputAssembly").Items.Single();
        var assembly = Path.GetFullPath(output, Path.GetDirectoryName(node.ProjectPath)!);
        return observation with { Assembly = await HashOutput(assembly, token),
            Pdb = await HashOutput(Path.ChangeExtension(assembly, ".pdb"), token) };
    }

    internal static async Task<CompilerObservation> ReadAsync(string binlog, string project, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        if (!File.Exists(binlog)) throw new ProjectContextException("compiler.binlogMissing", "The declared compiler log is missing.");
        if (new FileInfo(binlog).Length > MaximumBinlogBytes)
            throw new ProjectContextException("compiler.binlogLimit", "Compiler log is missing or exceeds the private input limit.");
        var sdk = await ProjectContextLoader.ResolveSdkAsync(Path.GetDirectoryName(project)!, timeout, token);
        var scratch = Path.Combine(Path.GetTempPath(), "crap4csharp-compiler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var request = Path.Combine(scratch, "request.json");
            var response = Path.Combine(scratch, "response.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new CompilerReadRequest(sdk.Path,
                Path.GetFullPath(binlog), Path.GetFullPath(project)), ProjectContextLoader.JsonOptions), token);
            var process = await ProcessRunner.RunProtocolAsync(sdk.HostPath,
                [typeof(CompilerEvidence).Assembly.Location, Command, request, response], Path.GetDirectoryName(project)!, timeout,
                token, maximumBytesPerStream: 4096);
            if (!File.Exists(response) || new FileInfo(response).Length > MaximumTransportBytes)
                throw new ProjectContextException("compiler.protocolInvalid", "Compiler worker did not return bounded evidence.");
            var result = JsonSerializer.Deserialize<CompilerReadResponse>(await File.ReadAllTextAsync(response, token), ProjectContextLoader.JsonOptions);
            if (result?.FailureReason is { } reason) throw new ProjectContextException(reason, "Actual compiler evidence was refused.");
            if (process.ExitCode != 0 || result?.Observation is null)
                throw new ProjectContextException("compiler.protocolInvalid", "Compiler worker failed.");
            return result.Observation;
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static async Task<CompilerOutput> HashOutput(string path, CancellationToken token)
    {
        if (!File.Exists(path)) throw new ProjectContextException("compiler.outputMissing", "The actual compiler PE or portable PDB is missing.");
        await using var stream = File.OpenRead(path);
        var length = stream.Length;
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, token);
        if (stream.Length != length) throw new ProjectContextException("compiler.outputMutated", "A compiler output changed during capture.");
        return new(path, Convert.ToHexString(hash).ToLowerInvariant(), length);
    }

    private static string Escape(string value)
    {
        foreach (var character in new[] { '%', ';', ',', '$', '@', '\'', '(', ')', '*', '?' })
            value = value.Replace(character.ToString(), "%" + ((int)character).ToString("X2"), StringComparison.Ordinal);
        return value;
    }
}
