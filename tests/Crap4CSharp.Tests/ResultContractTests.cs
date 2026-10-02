using System.Text.Json;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ResultContractTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-result-tests", Guid.NewGuid().ToString("N"));

    public ResultContractTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public async Task JsonSuccessIsOneCompleteVersionedDocument()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);

        var result = await RunApp("--format", "json", "--coverage", coverage, source);

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("\n", result.Output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("ordinary-methods-v1", root.GetProperty("complexityRulesetVersion").GetString());
        Assert.Equal("legacy", root.GetProperty("evaluation").GetProperty("invocationMode").GetString());
        Assert.Equal("pass", root.GetProperty("evaluation").GetProperty("decision").GetProperty("policyDecision").GetString());
        Assert.Equal(0, root.GetProperty("run").GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain("CRAP", result.Output);
    }

    [Fact]
    public async Task JsonIntentRendersArgumentErrorsAsJson()
    {
        var result = await RunApp("--format", "json", "--threshold", "not-a-number");

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("operationalError", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal("arguments.invalid", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain("error:", result.Output);
    }

    [Fact]
    public async Task OutputFileMatchesJsonStdoutAndReplacesExistingFile()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);
        var outputPath = Write("result.json", "old");

        var result = await RunApp("--format", "json", "--output", outputPath, "--coverage", coverage, source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(result.Output, await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OutputCannotAliasAnInputAndDoesNotModifyIt()
    {
        var source = Write("Source.cs", "class C { int M() => 1; }");
        var coverage = WriteOpenCover("coverage.xml", source, visits: 1);
        var before = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        var timestamp = File.GetLastWriteTimeUtc(source);

        var result = await RunApp("--format", "json", "--output", source, "--coverage", coverage, source);

        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("output.aliasesInput", document.RootElement.GetProperty("evaluation").GetProperty("checks")[0].GetProperty("reason").GetString());
        Assert.Equal(before, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(source));
    }

    private async Task<(int ExitCode, string Output, string Error)> RunApp(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await global::App.RunAsync(args, temporary, output, error, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    private string WriteOpenCover(string relative, string source, int visits) =>
        Write(relative, $"""
            <CoverageSession><Modules><Module><ModuleName>Fixture</ModuleName><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(source)}" /></Files><Classes><Class><FullName>C</FullName><Methods><Method><Name>C.M()</Name><SequencePoints><SequencePoint vc="{visits}" sl="1" sc="11" el="1" ec="24" offset="0" fileid="1" /></SequencePoints><FileRef uid="1" /></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }
}
