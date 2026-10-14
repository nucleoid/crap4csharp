using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestChildEvidenceTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"observation\":{\"compilerTasks\":\"private-canary\"}}")]
    public void MalformedWorkerResponseUsesTypedRefusalWithoutDisclosingContents(string payload)
    {
        var error = Assert.Throws<ProjectContextException>(() => TestChildEvidence.ParseResponse(payload));
        Assert.Equal("test.childProtocolInvalid", error.Reason);
        Assert.DoesNotContain("private-canary", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
    }

    [Theory]
    [InlineData(0, 1, true, 0, null)]
    [InlineData(0, 1, false, 1, null)]
    [InlineData(1, 1, true, 0, "test.compilationObserved")]
    [InlineData(0, 0, true, 0, "test.childAssociationUnproven")]
    [InlineData(0, 2, true, 0, "test.childAssociationUnproven")]
    [InlineData(0, 1, true, 1, "test.childAssociationUnproven")]
    [InlineData(0, 1, false, 0, "test.childAssociationUnproven")]
    public void RawFailureAndCompilerEvidenceRemainIndependent(int compilers, int tests, bool buildSucceeded,
        int exit, string? expected)
    {
        var observation = new TestChildObservation(Path.GetFullPath("Tests.csproj"), compilers, tests, buildSucceeded, "hash");
        var stream = new ProcessLogStream("private", 0, [], false);
        var raw = new LoggedProcessResult(exit, stream, stream);
        if (expected is null) TestChildEvidence.Validate(observation, raw);
        else Assert.Equal(expected, Assert.Throws<ProjectContextException>(() => TestChildEvidence.Validate(observation, raw)).Reason);
    }

    [Fact]
    public async Task MissingOversizedMalformedAndPrecancelledOwnedLogsRefuse()
    {
        using var fixture = TestDirectory.Create("crap4csharp-test-child-protocol");
        var project = fixture.Write("Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var path = Path.Combine(fixture.Path, "child.binlog");
        var malformed = fixture.Write("malformed.binlog", "not an MSBuild binary log");
        Assert.Equal("test.childLogInvalid", (await Assert.ThrowsAsync<ProjectContextException>(() =>
            TestChildEvidence.ReadAsync(malformed, project, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken))).Reason);
        Assert.Equal("test.childLogUnproven", (await Assert.ThrowsAsync<ProjectContextException>(() =>
            TestChildEvidence.ReadAsync(path, project, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken))).Reason);
        Assert.Equal("test.childLogUnproven", (await Assert.ThrowsAsync<ProjectContextException>(() =>
            TestChildEvidence.ReadAsync(Path.Combine(fixture.Path, "absent", "child.binlog"), project,
                TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken))).Reason);
        using (var file = File.Create(path)) file.SetLength(CompilerEvidence.MaximumBinlogBytes + 1);
        Assert.Equal("test.childLogUnproven", (await Assert.ThrowsAsync<ProjectContextException>(() =>
            TestChildEvidence.ReadAsync(path, project, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken))).Reason);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestChildEvidence.ReadAsync(path, project, TimeSpan.FromMinutes(1), cancelled.Token));
    }
}
