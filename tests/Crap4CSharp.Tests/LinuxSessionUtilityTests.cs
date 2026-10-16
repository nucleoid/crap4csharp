using System.ComponentModel;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class LinuxSessionUtilityTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "Linux session utility acceptance", SkipUnless = nameof(IsLinux))]
    public async Task OwnedSessionResolvesUtilitiesFromEffectiveChildPath()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux utility fixture.");
        using var fixture = TestDirectory.Create("crap4csharp-session-utilities");
        var marker = Path.Combine(fixture.Path, "utility.txt");
        var real = FindInstalled("setsid");
        var shell = FindInstalled("sh");
        var launcher = fixture.Write("setsid", "#!/bin/sh\nprintf 'used\\n' > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nexec '" + real.Replace("'", "'\\''", StringComparison.Ordinal) + "' \"$@\"\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(fixture.Path, "sh"), shell);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet");
        var helper = Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");
        var result = await ProcessRunner.RunWithEnvironmentAsync(host, [helper, "exit-code", "73"], fixture.Path,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken, new Dictionary<string, string> { ["PATH"] = fixture.Path });
        Assert.Equal(73, result.ExitCode);
        Assert.True(File.Exists(marker), "Invocation bypassed the effective PATH utility.");
        Assert.Equal("used\n", File.ReadAllText(marker));
    }

    [Theory(Skip = "Linux session utility acceptance", SkipUnless = nameof(IsLinux))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRequiredSessionUtilityFailsBeforeConsumerLaunch(bool missingShell)
    {
        using var fixture = TestDirectory.Create("crap4csharp-missing-session-utility");
        if (missingShell) File.CreateSymbolicLink(Path.Combine(fixture.Path, "setsid"), FindInstalled("setsid"));
        var marker = Path.Combine(fixture.Path, "consumer.json");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet");
        var helper = Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");
        var error = await Assert.ThrowsAsync<Win32Exception>(() => ProcessRunner.RunWithEnvironmentAsync(host,
            [helper, "hold", marker, Path.Combine(fixture.Path, "release")], fixture.Path, TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken, new Dictionary<string, string> { ["PATH"] = fixture.Path }));
        Assert.Contains(missingShell ? "sh" : "setsid", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    private static string FindInstalled(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, name)).First(File.Exists);
}
