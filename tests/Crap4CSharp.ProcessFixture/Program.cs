// Invocation-owned portable process regression fixture; no shell or external tools.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

if (args.Length == 0) return 64;
switch (args[0])
{
    case "control":
        Console.Out.Write("control\n");
        Console.Error.Write("diagnostic\n");
        return 0;
    case "echo-arguments":
        Console.Out.Write(JsonSerializer.Serialize(new { arguments = args.Skip(1).ToArray(),
            environment = Environment.GetEnvironmentVariable("CRAP4CSHARP_PROCESS_CONTROL") }));
        Console.Error.Write("argument-control\n");
        return 23;
    case "mark":
        await File.WriteAllTextAsync(Path.GetFullPath(args[1]), "consumer-launched");
        return 0;
    case "flood":
    {
        int bytes = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        byte[] stdoutBlock = Encoding.ASCII.GetBytes(new string('O', 4096));
        byte[] stderrBlock = Encoding.ASCII.GetBytes(new string('E', 4096));
        await Task.WhenAll(WriteBytes(Console.OpenStandardOutput(), stdoutBlock, bytes),
            WriteBytes(Console.OpenStandardError(), stderrBlock, bytes));
        return 0;
    }
    case "binary-protocol":
    {
        // Byte protocol is deliberately not UTF-8 text: includes NUL and invalid UTF-8.
        // A byte-preserving adapter must return every byte, or explicitly reject its limit.
        int count = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        byte[] block = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        await WriteBytes(Console.OpenStandardOutput(), block, count);
        Console.Error.Write("binary-protocol-complete\n");
        return 0;
    }
    case "duplex-protocol":
    {
        // Emit more than a pipe buffer BEFORE reading stdin. Parent must start both output
        // pumps and the operation deadline before its asynchronous stdin write.
        int count = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        byte[] block = Encoding.ASCII.GetBytes(new string('D', 4096));
        await Task.WhenAll(WriteBytes(Console.OpenStandardOutput(), block, count),
            WriteBytes(Console.OpenStandardError(), block, count));
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] input = new byte[4096];
        long total = 0;
        using var stdin = Console.OpenStandardInput();
        while (true)
        {
            int read = await stdin.ReadAsync(input);
            if (read == 0) break;
            total += read;
            hash.AppendData(input, 0, read);
        }
        Console.Out.Write("\nINPUT:" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ":" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() + "\n");
        return 0;
    }
    case "hold":
    {
        // PID+start time distinguish a fixture-owned child from a later PID reuse.
        string ready = Path.GetFullPath(args[1]);
        string release = Path.GetFullPath(args[2]);
        using var self = Process.GetCurrentProcess();
        string staging = ready + "." + self.Id + ".pending";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(new
        {
            pid = self.Id, startTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks,
            linuxStartTicks = OperatingSystem.IsLinux() ? LinuxStartTicks(self.Id) : null
        }));
        File.Move(staging, ready, overwrite: false);
        // Bounded safety lifetime; release-file handshake replaces guessed sleep durations.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (!File.Exists(release)) await Task.Delay(25, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return 70;
        }
        return 0;
    }
    case "grandparent":
    {
        // Three actual generations, with positive readiness from BOTH descendants.
        string host = Path.GetFullPath(args[1]);
        string dll = Path.GetFullPath(args[2]);
        string ready = Path.GetFullPath(args[3]);
        string release = Path.GetFullPath(args[4]);
        var info = new ProcessStartInfo(host) { UseShellExecute = false };
        foreach (string value in new[] { dll, "descendant", host, dll, ready, release, "parent-live" })
            info.ArgumentList.Add(value);
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Intermediate child start failed.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready))
        {
            if (child.HasExited) return 72;
            await Task.Delay(25, deadline.Token);
        }
        string intermediate = ready + ".intermediate";
        string staging = intermediate + ".pending";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(new
        {
            pid = child.Id, startTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks,
            linuxStartTicks = OperatingSystem.IsLinux() ? LinuxStartTicks(child.Id) : null
        }));
        File.Move(staging, intermediate, overwrite: false);
        Console.Out.Write("grandchild-ready\n");
        Console.Out.Flush();
        if (args[5] == "grandparent-live") await child.WaitForExitAsync();
        return 0;
    }
    case "descendant":
    {
        // Caller supplies exact resolved dotnet host + helper DLL. No shell/quoting dependency.
        string host = Path.GetFullPath(args[1]);
        string dll = Path.GetFullPath(args[2]);
        string ready = Path.GetFullPath(args[3]);
        string release = Path.GetFullPath(args[4]);
        var info = new ProcessStartInfo(host) { UseShellExecute = false };
        foreach (string value in new[] { dll, "hold", ready, release }) info.ArgumentList.Add(value);
        // Deliberately inherit both redirected pipes, reproducing the actual deadline bypass.
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Child start failed.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(ready))
        {
            if (child.HasExited) return 71;
            await Task.Delay(25, deadline.Token);
        }
        Console.Out.Write("child-ready\n");
        Console.Out.Flush();
        // parent-exit mode leaves pipes held by child; parent-live tests tree cancellation.
        if (args.Length > 5 && args[5] == "parent-live") await child.WaitForExitAsync();
        return 0;
    }
    default:
        return 64;
}

static string LinuxStartTicks(int pid)
{
    string stat = File.ReadAllText($"/proc/{pid}/stat");
    // Field 22 is kernel start ticks; the comm field may itself contain spaces/parentheses.
    return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
}

static async Task WriteBytes(Stream sink, byte[] block, int count)
{
    if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
    while (count > 0)
    {
        int size = Math.Min(count, block.Length);
        await sink.WriteAsync(block.AsMemory(0, size));
        count -= size;
    }
    await sink.FlushAsync();
}
