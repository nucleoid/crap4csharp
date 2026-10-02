using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PackagedResultTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-package-tests", Guid.NewGuid().ToString("N"));

    public PackagedResultTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public async Task InstalledPackageEmitsJsonWithoutConsoleNoise()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var packages = Path.Combine(temporary, "packages");
        var tools = Path.Combine(temporary, "tools");
        Directory.CreateDirectory(packages);
        var pack = await Run("dotnet", ["pack", Path.Combine(repository, "src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj"),
            "-c", "Release", "--no-build", "-o", packages], repository);
        Assert.Equal(0, pack.ExitCode);
        var install = await Run("dotnet", ["tool", "install", "--tool-path", tools, "--add-source", packages,
            "--ignore-failed-sources", "Crap4CSharp.Tool", "--version", "0.1.0"], repository);
        Assert.Equal(0, install.ExitCode);

        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = Write("coverage.xml", $"<CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid=\"1\" fullPath=\"{System.Security.SecurityElement.Escape(source)}\" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name><SequencePoints><SequencePoint vc=\"1\" sl=\"1\" fileid=\"1\" /></SequencePoints><FileRef uid=\"1\" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>");
        var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? "crap4csharp.exe" : "crap4csharp");

        var result = await Run(executable, ["--format", "json", "--coverage", coverage, source], temporary);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.DoesNotContain("CRAP", result.Output);
    }

    private async Task<(int ExitCode, string Output, string Error)> Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.Environment["DOTNET_ROOT"] = "/home/fuego/.dotnet";
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        File.WriteAllText(path, contents);
        return path;
    }
}
