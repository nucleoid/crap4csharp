// Independent regression-fixture control, not product byte-protocol acceptance.
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProtocolFixtureControlTests
{
    [Fact]
    public async Task PortableHelperPreservesBinaryAndDuplexBytesIndependentlyOfProductRunner()
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")
                ?? throw new InvalidOperationException("Exact .NET host not supplied."),
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        string helper = Path.Combine(AppContext.BaseDirectory, "process-helper", "Helper.dll");
        Assert.True(File.Exists(host), "Exact configured host must exist.");
        Assert.True(File.Exists(helper), "Built portable helper must exist.");
        string directory = Path.Combine(Path.GetTempPath(), "crap4csharp-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await ProtocolFixtureControls.VerifyAsync(host, helper, directory,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            // VerifyAsync owns finite process/pump cleanup before this directory is removed.
            Directory.Delete(directory, recursive: true);
        }
    }
}
