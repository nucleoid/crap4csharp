using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PackagedResultTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-package-tests", Guid.NewGuid().ToString("N"));

    public PackagedResultTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public async Task InstalledPackageCoversTerminalResultMatrix()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var packages = Path.Combine(temporary, "packages");
        var tools = Path.Combine(temporary, "tools");
        Directory.CreateDirectory(packages);
        var configuration = typeof(PackagedResultTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var pack = await Run("dotnet", ["pack", Path.Combine(repository, "src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj"),
            "-c", configuration, "--no-build", "-o", packages], repository);
        AssertSuccess("pack", pack);
        var install = await Run("dotnet", ["tool", "install", "--tool-path", tools, "--add-source", packages,
            "--ignore-failed-sources", "Crap4CSharp.Tool"], repository);
        AssertSuccess("install", install);

        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = Write("coverage.xml", $"<CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid=\"1\" fullPath=\"{System.Security.SecurityElement.Escape(source)}\" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name><SequencePoints><SequencePoint vc=\"1\" sl=\"1\" fileid=\"1\" /></SequencePoints><FileRef uid=\"1\" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>");
        var malformed = Write("malformed.xml", "<coverage>");
        var project = Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? "crap4csharp.exe" : "crap4csharp");

        var pass = await Run(executable, ["--format", "json", "--coverage", coverage, source], temporary);
        AssertDocument(pass, 0);
        using (var document = JsonDocument.Parse(pass.Output))
        {
            Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(typeof(global::App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                document.RootElement.GetProperty("toolVersion").GetString());
        }

        var missing = await Run(executable, ["--format", "json", "--coverage", "missing.xml", source], temporary);
        AssertDocument(missing, 1);
        var malformedResult = await Run(executable, ["--format", "json", "--coverage", malformed, source], temporary);
        AssertDocument(malformedResult, 1);

        var fakeDotnet = await BuildFakeDotnet(repository);
        var fakePath = Path.GetDirectoryName(fakeDotnet)! + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var noCoverage = await Run(executable, ["--format", "json", "--project", project, source], temporary,
            new Dictionary<string, string?> { ["PATH"] = fakePath, ["FAKE_DOTNET_MODE"] = "success" });
        AssertDocument(noCoverage, 1, "tests.passed", "coverage.notProduced");
        var failed = await Run(executable, ["--format", "json", "--project", project, source], temporary,
            new Dictionary<string, string?> { ["PATH"] = fakePath, ["FAKE_DOTNET_MODE"] = "failed" });
        AssertDocument(failed, 1, "tests.failed", "coverage.notProduced");
        var timeout = await Run(executable, ["--format", "json", "--timeout-seconds", "1", "--project", project, source], temporary,
            new Dictionary<string, string?> { ["PATH"] = fakePath, ["FAKE_DOTNET_MODE"] = "timeout" });
        AssertDocument(timeout, 1, "run.timeout");
    }

    private async Task<string> BuildFakeDotnet(string repository)
    {
        var root = Path.Combine(temporary, "fake-dotnet");
        Directory.CreateDirectory(root);
        var project = Write(Path.Combine("fake-dotnet", "FakeDotnet.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>dotnet</AssemblyName><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(Path.Combine("fake-dotnet", "Program.cs"),
            "var mode = Environment.GetEnvironmentVariable(\"FAKE_DOTNET_MODE\"); if (mode == \"timeout\") await Task.Delay(TimeSpan.FromSeconds(30)); return mode == \"failed\" ? 1 : 0;");
        var build = await Run("dotnet", ["build", project, "-c", "Release", "--nologo", "-m:1"], repository);
        AssertSuccess("fake dotnet build", build);
        return Path.Combine(root, "bin", "Release", "net10.0", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private static void AssertDocument((int ExitCode, string Output, string Error) result, int exitCode, params string[] reasons)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(exitCode, document.RootElement.GetProperty("run").GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("CRAP", result.Output);
        var actualReasons = document.RootElement.GetProperty("evaluation").GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("reason").GetString()).ToArray();
        foreach (var reason in reasons) Assert.Contains(reason, actualReasons);
    }

    private static void AssertSuccess(string operation, (int ExitCode, string Output, string Error) result) =>
        Assert.True(result.ExitCode == 0, $"{operation} failed ({result.ExitCode}). stdout: {result.Output} stderr: {result.Error}");

    private static async Task<(int ExitCode, string Output, string Error)> Run(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        if (environment is not null)
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
}
