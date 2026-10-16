using System.Diagnostics;
using Xunit;

namespace Crap4CSharp.Tests;

internal static class ReparseTestDirectory
{
    internal static void Link(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(path); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5000);
            Assert.Fail("Junction creation timed out.");
        }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
}
