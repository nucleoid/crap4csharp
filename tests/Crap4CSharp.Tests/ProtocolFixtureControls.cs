// Independent byte-protocol fixture controls, not product runner acceptance.
// Invoke with exact dotnet-host and compiled helper-DLL paths.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal static class ProtocolFixtureControls
{
    public static async Task VerifyAsync(string host, string helper, string directory,
        CancellationToken cancellationToken)
    {
        const int count = 1_048_577; // More than ordinary pipe capacity; non-block-aligned.
        byte[] expected = Enumerable.Range(0, count).Select(i => (byte)(i % 256)).ToArray();
        var binary = await RunAsync(host, helper, directory, "binary-protocol", count,
            null, cancellationToken);
        Require(binary.ExitCode == 0, "Binary fixture did not complete successfully.");
        Require(binary.Output.AsSpan().SequenceEqual(expected), "Binary fixture changed raw bytes.");
        Require(Encoding.UTF8.GetString(binary.Error) == "binary-protocol-complete\n",
            "Binary fixture completion diagnostic differs.");
        // Positively prove why a string-only route cannot represent this protocol.
        Require(!Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(binary.Output)).AsSpan()
            .SequenceEqual(expected), "Fixture unexpectedly became lossless UTF-8 text.");

        byte[] input = Enumerable.Range(0, count + 17).Select(i => (byte)((i * 31) % 256)).ToArray();
        var duplex = await RunAsync(host, helper, directory, "duplex-protocol", count,
            input, cancellationToken);
        Require(duplex.ExitCode == 0, "Duplex fixture did not complete successfully.");
        byte[] prefix = Enumerable.Repeat((byte)'D', count).ToArray();
        string receipt = "\nINPUT:" + input.Length.ToString(CultureInfo.InvariantCulture) + ":" +
            Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() + "\n";
        byte[] expectedOutput = prefix.Concat(Encoding.ASCII.GetBytes(receipt)).ToArray();
        Require(duplex.Output.AsSpan().SequenceEqual(expectedOutput),
            "Duplex stdout prefix/count/input hash differs.");
        Require(duplex.Error.AsSpan().SequenceEqual(prefix), "Duplex stderr bytes/count differ.");
    }

    private static async Task<(int ExitCode, byte[] Output, byte[] Error)> RunAsync(
        string host, string helper, string directory, string mode, int count,
        byte[]? input, CancellationToken cancellationToken)
    {
        // Independent fixture reference: intentionally no product ProcessRunner call.
        // Finite bounded fixture payloads only; this is not an unbounded log adapter.
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(15));
        var info = new ProcessStartInfo(Path.GetFullPath(host))
        {
            WorkingDirectory = Path.GetFullPath(directory), UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string value in new[] { Path.GetFullPath(helper), mode,
            count.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(value);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Fixture failed to launch.");
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, guard.Token);
        Task errorTask = process.StandardError.BaseStream.CopyToAsync(error, guard.Token);
        Task inputTask = WriteInputAsync(process, input, guard.Token);
        Task exitTask = process.WaitForExitAsync(guard.Token);
        Task all = Task.WhenAll(outputTask, errorTask, inputTask, exitTask);
        try
        {
            await all.WaitAsync(guard.Token);
            return (process.ExitCode, output.ToArray(), error.ToArray());
        }
        finally
        {
            // This helper has no descendants in these two modes. Own only this exact
            // process object, never kill unrelated process names or stale PID records.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            guard.Cancel();
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            // Observe all original pump/write/exit tasks, including a failed guard arm.
            try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (all.IsCompleted) { _ = all.Exception; }
            if (!all.IsCompleted) throw new InvalidOperationException("Fixture tasks survived cleanup.");
        }
    }

    private static async Task WriteInputAsync(Process process, byte[]? input, CancellationToken token)
    {
        try
        {
            if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }
        finally { process.StandardInput.Close(); }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
