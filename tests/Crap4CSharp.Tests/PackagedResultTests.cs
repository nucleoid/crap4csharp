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
        var packageCache = Path.Combine(temporary, "nuget-packages");
        var tools = Path.Combine(temporary, "tools");
        Directory.CreateDirectory(packages);
        var configuration = typeof(PackagedResultTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var isolatedNuget = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = packageCache,
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(temporary, "nuget-http-cache")
        };
        var pack = await Run("dotnet", ["pack", Path.Combine(repository, "src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj"),
            "-c", configuration, "--no-restore", "-m:1", "-o", packages], repository, isolatedNuget);
        AssertSuccess("pack", pack);
        var nugetConfig = Write("NuGet.Config", $"<configuration><packageSources><clear /><add key=\"local\" value=\"{System.Security.SecurityElement.Escape(packages)}\" /></packageSources></configuration>");
        var install = await Run("dotnet", ["tool", "install", "--tool-path", tools, "--add-source", packages,
            "--configfile", nugetConfig, "--no-cache", "Crap4CSharp.Tool"], repository, isolatedNuget);
        AssertSuccess("install", install);

        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = Write("coverage.xml", $"<CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid=\"1\" fullPath=\"{System.Security.SecurityElement.Escape(source)}\" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name><SequencePoints><SequencePoint vc=\"1\" sl=\"1\" fileid=\"1\" /></SequencePoints><FileRef uid=\"1\" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>");
        var malformed = Write("malformed.xml", "<coverage>");
        var project = Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var executable = Path.Combine(tools, OperatingSystem.IsWindows() ? "crap4csharp.exe" : "crap4csharp");

        var pass = await Run(executable, ["--format", "json", "--coverage", coverage, source], temporary);
        AssertDocument(repository, pass, 0);
        var violation = await Run(executable, ["--format", "json", "--threshold", "0", "--coverage", coverage, source], temporary);
        AssertDocument(repository, violation, 2, "crap.thresholdExceeded");
        using (var document = JsonDocument.Parse(pass.Output))
        {
            Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(typeof(global::App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
                document.RootElement.GetProperty("toolVersion").GetString());
        }

        var missing = await Run(executable, ["--format", "json", "--coverage", "missing.xml", source], temporary);
        AssertDocument(repository, missing, 1);
        var malformedResult = await Run(executable, ["--format", "json", "--coverage", malformed, source], temporary);
        AssertDocument(repository, malformedResult, 1);

        var firstProgram = Write("App1/Program.cs", "class Program { static void Main() { } }");
        var secondProgram = Write("App2/Program.cs", "class Program { static void Main() { } }");
        var multiRootReport = Write("multi-root.xml", $"""
            <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files>
            <File uid="1" fullPath="{System.Security.SecurityElement.Escape(firstProgram)}" />
            <File uid="2" fullPath="{System.Security.SecurityElement.Escape(secondProgram)}" />
            </Files><Classes><Class><FullName>Program</FullName><Methods>
            <Method><Name>Program.Main(System.Int32)</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /></SequencePoints><FileRef uid="1" /></Method>
            <Method><Name>Program.Main(System.Int32)</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="2" /></SequencePoints><FileRef uid="2" /></Method>
            </Methods></Class></Classes></Module></Modules></CoverageSession>
            """);
        var multiRoot = await Run(executable, ["--format", "json", "--allow-missing-coverage", "--coverage", multiRootReport,
            Path.GetDirectoryName(firstProgram)!, Path.GetDirectoryName(secondProgram)!], temporary);
        AssertDocument(repository, multiRoot, 0);
        using (var document = JsonDocument.Parse(multiRoot.Output))
        {
            var metrics = document.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray().ToArray();
            var methodDiagnostics = document.RootElement.GetProperty("evaluation").GetProperty("coverageDiagnostics").EnumerateArray()
                .Where(item => item.GetProperty("scope").GetString() == "method").ToArray();
            var metricPaths = metrics.Select(item => item.GetProperty("path").GetString()!).Order().ToArray();
            Assert.Equal(["App1/Program.cs", "App2/Program.cs"], metricPaths);
            Assert.Equal(metricPaths, methodDiagnostics.Select(item => item.GetProperty("path").GetString()!).Order().ToArray());
            Assert.Equal(2, methodDiagnostics.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
            Assert.All(metrics, metric => Assert.Single(metric.GetProperty("coverageStatus").GetProperty("diagnosticIds").EnumerateArray()));
        }

        var ambiguousReport = Write("ambiguous.xml", """
            <coverage><sources><source>App1</source><source>App2</source></sources><packages><package><classes>
            <class name="Program" filename="Program.cs"><methods><method name="Main" signature="()"><lines><line number="1" hits="1" /></lines></method></methods></class>
            </classes></package></packages></coverage>
            """);
        var ambiguous = await Run(executable, ["--format", "json", "--allow-missing-coverage", "--coverage", ambiguousReport,
            Path.GetDirectoryName(firstProgram)!, Path.GetDirectoryName(secondProgram)!], temporary);
        AssertDocument(repository, ambiguous, 0);
        using (var document = JsonDocument.Parse(ambiguous.Output))
        {
            var diagnostic = Assert.Single(document.RootElement.GetProperty("evaluation").GetProperty("coverageDiagnostics")
                .EnumerateArray(), item => item.GetProperty("code").GetString() == "coverage.ambiguousPath");
            Assert.Equal(["App1/Program.cs", "App2/Program.cs"],
                diagnostic.GetProperty("candidatePaths").EnumerateArray().Select(item => item.GetString()!).ToArray());
        }

        var workspace = Path.Combine(temporary, "external-workspace");
        var firstExternalRoot = Path.Combine(temporary, "outside-a", "shared");
        var secondExternalRoot = Path.Combine(temporary, "outside-b", "shared");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(firstExternalRoot);
        Directory.CreateDirectory(secondExternalRoot);
        var firstExternal = Path.Combine(firstExternalRoot, "Program.cs");
        var secondExternal = Path.Combine(secondExternalRoot, "Program.cs");
        File.WriteAllText(firstExternal, "class First { int M() => 1; }");
        File.WriteAllText(secondExternal, "class Second { int M() => 2; }");
        var externalReport = Write("external-workspace/external.xml", $"""
            <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files>
            <File uid="1" fullPath="{System.Security.SecurityElement.Escape(firstExternal)}" />
            <File uid="2" fullPath="{System.Security.SecurityElement.Escape(secondExternal)}" />
            </Files><Classes>
            <Class><FullName>First</FullName><Methods><Method><Name>First.M(System.Int32)</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class>
            <Class><FullName>Second</FullName><Methods><Method><Name>Second.M(System.Int32)</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="2" /></SequencePoints><FileRef uid="2" /></Method></Methods></Class>
            </Classes></Module></Modules></CoverageSession>
            """);
        var externalAlone = await Run(executable, ["--format", "json", "--allow-missing-coverage", "--coverage", externalReport,
            firstExternal], workspace);
        var externalTogether = await Run(executable, ["--format", "json", "--allow-missing-coverage", "--coverage", externalReport,
            secondExternal, firstExternal], workspace);
        AssertDocument(repository, externalAlone, 0);
        AssertDocument(repository, externalTogether, 0);
        using (var aloneDocument = JsonDocument.Parse(externalAlone.Output))
        using (var togetherDocument = JsonDocument.Parse(externalTogether.Output))
        {
            var alonePath = Assert.Single(aloneDocument.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray())
                .GetProperty("path").GetString();
            var togetherPaths = togetherDocument.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()).ToArray();
            Assert.Contains(alonePath, togetherPaths);
            Assert.Equal(2, togetherPaths.Distinct().Count());
            Assert.DoesNotContain(temporary, alonePath, StringComparison.Ordinal);
        }

        if (!OperatingSystem.IsWindows())
        {
            var firstAlias = Path.Combine(workspace, "AliasA", "Program.cs");
            var secondAlias = Path.Combine(workspace, "AliasB", "Program.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(firstAlias)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondAlias)!);
            File.CreateSymbolicLink(firstAlias, firstExternal);
            File.CreateSymbolicLink(secondAlias, secondExternal);
            var aliasReport = Write("external-workspace/aliases.xml", $"""
                <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files>
                <File uid="1" fullPath="{System.Security.SecurityElement.Escape(firstAlias)}" />
                <File uid="2" fullPath="{System.Security.SecurityElement.Escape(secondAlias)}" />
                </Files><Classes>
                <Class><FullName>First</FullName><Methods><Method><Name>First.M(System.Int32)</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class>
                <Class><FullName>Second</FullName><Methods><Method><Name>Second.M(System.Int32)</Name><SequencePoints><SequencePoint vc="1" sl="1" fileid="2" /></SequencePoints><FileRef uid="2" /></Method></Methods></Class>
                </Classes></Module></Modules></CoverageSession>
                """);
            var aliases = await Run(executable, ["--format", "json", "--allow-missing-coverage", "--coverage", aliasReport,
                firstAlias, secondAlias], workspace);
            AssertDocument(repository, aliases, 0);
            using var document = JsonDocument.Parse(aliases.Output);
            var paths = document.RootElement.GetProperty("evaluation").GetProperty("metrics").EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()).ToArray();
            var diagnosticIds = document.RootElement.GetProperty("evaluation").GetProperty("coverageDiagnostics").EnumerateArray()
                .Where(item => item.GetProperty("scope").GetString() == "method")
                .Select(item => item.GetProperty("id").GetString()).ToArray();
            Assert.Equal(2, paths.Distinct().Count());
            Assert.Equal(2, diagnosticIds.Distinct().Count());
        }

        var fakeDotnet = await BuildFakeDotnet(repository);
        var fakePath = Path.GetDirectoryName(fakeDotnet)! + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var noCoverage = await Run(executable, ["--format", "json", "--project", project, source], temporary,
            new Dictionary<string, string?> { ["PATH"] = fakePath, ["FAKE_DOTNET_MODE"] = "success" });
        AssertDocument(repository, noCoverage, 1, "tests.passed", "coverage.notProduced");
        var failed = await Run(executable, ["--format", "json", "--project", project, source], temporary,
            new Dictionary<string, string?> { ["PATH"] = fakePath, ["FAKE_DOTNET_MODE"] = "failed" });
        AssertDocument(repository, failed, 1, "tests.failed", "coverage.notProduced");
        var timeout = await Run(executable, ["--format", "json", "--timeout-seconds", "1", "--project", project, source], temporary,
            new Dictionary<string, string?> { ["PATH"] = fakePath, ["FAKE_DOTNET_MODE"] = "timeout" });
        AssertDocument(repository, timeout, 1, "run.timeout");
    }

    private async Task<string> BuildFakeDotnet(string repository)
    {
        var root = Path.Combine(temporary, "fake-dotnet");
        Directory.CreateDirectory(root);
        var project = Write(Path.Combine("fake-dotnet", "FakeDotnet.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><AssemblyName>dotnet</AssemblyName><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        Write(Path.Combine("fake-dotnet", "Program.cs"),
            "var mode = Environment.GetEnvironmentVariable(\"FAKE_DOTNET_MODE\"); if (mode == \"timeout\") await Task.Delay(TimeSpan.FromSeconds(30)); return mode == \"failed\" ? 1 : 0;");
        var build = await Run("dotnet", ["build", project, "-c", "Release", "--nologo", "-m:1"], repository);
        AssertSuccess("fake dotnet build", build);
        return Path.Combine(root, "bin", "Release", "net10.0", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private static void AssertDocument(string repository, (int ExitCode, string Output, string Error) result,
        int exitCode, params string[] reasons)
    {
        Assert.Equal(exitCode, result.ExitCode);
        ResultContractTests.AssertConformsToPublishedSchema(repository, result.Output);
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
