using System.Text.Json;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class PrivateDiagnosticsCleanupTests
{
    internal static void AssertSealed(string root)
    {
        using var marker = new FileStream(System.IO.Path.Combine(root, PrivateDiagnosticLease.MarkerName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var document = JsonDocument.Parse(marker);
        var value = document.RootElement;
        Assert.Equal(PrivateDiagnosticLease.Schema, value.GetProperty("schemaVersion").GetString());
        Assert.Equal(root, value.GetProperty("directory").GetString());
        var started = value.GetProperty("startedAt").GetDateTimeOffset();
        var finished = value.GetProperty("finishedAt").GetDateTimeOffset();
        Assert.InRange(finished, started, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ActiveInvocationCannotBeCleaned()
    {
        using var fixture = new OwnedPrivateRoot();
        using var lease = new PrivateDiagnosticLease(fixture.Path, "fixture");
        Assert.Throws<IOException>(() => PrivateDiagnosticsCleanup.Delete(fixture.Path, 7,
            CancellationToken.None, DateTimeOffset.UtcNow.AddDays(8)));
        Assert.True(File.Exists(fixture.ProtectedFile));
    }

    [Fact]
    public void AgedClosedExactRootIsRemovedWithoutTouchingAnotherRoot()
    {
        using var fixture = new OwnedPrivateRoot();
        using var unrelated = new OwnedPrivateRoot();
        using (new PrivateDiagnosticLease(fixture.Path, "fixture")) { }
        PrivateDiagnosticsCleanup.Delete(fixture.Path, 7, CancellationToken.None,
            DateTimeOffset.UtcNow.AddDays(8));
        Assert.False(Directory.Exists(fixture.Path));
        Assert.True(File.Exists(unrelated.ProtectedFile));
    }

    [Theory]
    [InlineData("young")]
    [InlineData("unmarked")]
    [InlineData("malformed")]
    [InlineData("wrong-root")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("unknown-field")]
    [InlineData("zero-age")]
    [InlineData("cancelled")]
    public void RefusalsPreserveAllPrivateContents(string defect)
    {
        using var fixture = new OwnedPrivateRoot();
        using (new PrivateDiagnosticLease(fixture.Path, "fixture")) { }
        var marker = System.IO.Path.Combine(fixture.Path, PrivateDiagnosticLease.MarkerName);
        var now = DateTimeOffset.UtcNow.AddDays(8);
        var days = 7;
        using var cancellation = new CancellationTokenSource();
        switch (defect)
        {
            case "young": now = DateTimeOffset.UtcNow; break;
            case "unmarked": File.Delete(marker); break;
            case "malformed": File.WriteAllText(marker, "{"); break;
            case "zero-age": days = 0; break;
            case "cancelled": cancellation.Cancel(); break;
            default:
                var text = File.ReadAllText(marker);
                if (defect == "duplicate") text = text.Replace("{", "{\"schemaVersion\":\"duplicate\",", StringComparison.Ordinal);
                else if (defect == "unknown-field") text = text.Replace("{", "{\"unexpected\":true,", StringComparison.Ordinal);
                else
                {
                    var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
                    value[defect == "wrong-root" ? "directory" : "finishedAt"] = JsonSerializer.SerializeToElement(
                        defect == "wrong-root" ? fixture.Path + "-other" : now.AddDays(1).ToString("O"));
                    text = JsonSerializer.Serialize(value);
                }
                File.WriteAllText(marker, text);
                break;
        }
        Assert.ThrowsAny<Exception>(() => PrivateDiagnosticsCleanup.Delete(fixture.Path, days, cancellation.Token, now));
        Assert.True(File.Exists(fixture.ProtectedFile));
    }

    [Fact]
    public void ConsumerDirectoryIsRefusedEvenWithACopiedMarker()
    {
        using var consumer = TestDirectory.Create("crap4csharp-cleanup-consumer");
        var source = consumer.Write("Source.cs", "public class Consumer { }");
        using (new PrivateDiagnosticLease(consumer.Path, "fixture")) { }
        Assert.Throws<ArgumentException>(() => PrivateDiagnosticsCleanup.Delete(consumer.Path, 7,
            CancellationToken.None, DateTimeOffset.UtcNow.AddDays(8)));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void OldMarkerDatesCannotAgeAFreshRoot()
    {
        using var fixture = new OwnedPrivateRoot();
        using (new PrivateDiagnosticLease(fixture.Path, "fixture")) { }
        var marker = System.IO.Path.Combine(fixture.Path, PrivateDiagnosticLease.MarkerName);
        var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(marker))!;
        value["startedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddDays(-9));
        value["finishedAt"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddDays(-8));
        File.WriteAllText(marker, JsonSerializer.Serialize(value));
        Assert.Throws<IOException>(() => PrivateDiagnosticsCleanup.Delete(fixture.Path, 7, CancellationToken.None));
        Assert.True(File.Exists(fixture.ProtectedFile));
    }

    [Fact]
    public void UnsealedInactiveMarkerIsRefusedWithoutDeletingEvidence()
    {
        using var fixture = new OwnedPrivateRoot();
        using (new PrivateDiagnosticLease(fixture.Path, "fixture")) { }
        var marker = System.IO.Path.Combine(fixture.Path, PrivateDiagnosticLease.MarkerName);
        var value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(marker))!;
        value["finishedAt"] = JsonSerializer.SerializeToElement<object?>(null);
        File.WriteAllText(marker, JsonSerializer.Serialize(value));
        Assert.Throws<IOException>(() => PrivateDiagnosticsCleanup.Delete(fixture.Path, 7,
            CancellationToken.None, DateTimeOffset.UtcNow.AddDays(8)));
        Assert.True(File.Exists(fixture.ProtectedFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedRootsAndContentsRefuseWithoutDeletingTheirTarget(bool rootLink)
    {
        using var fixture = new OwnedPrivateRoot();
        using var target = new OwnedPrivateRoot();
        using (new PrivateDiagnosticLease(fixture.Path, "fixture")) { }
        var link = rootLink
            ? Directory.CreateTempSubdirectory(PrivateDiagnosticLease.Prefix).FullName
            : System.IO.Path.Combine(fixture.Path, "linked");
        if (rootLink) Directory.Delete(link);
        ReparseTestDirectory.Link(link, target.Path);
        try
        {
            Assert.Throws<IOException>(() => PrivateDiagnosticsCleanup.Delete(rootLink ? link : fixture.Path, 7,
                CancellationToken.None, DateTimeOffset.UtcNow.AddDays(8)));
            Assert.True(File.Exists(target.ProtectedFile));
            Assert.True(File.Exists(fixture.ProtectedFile));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CommandRejectsUnknownOrRepeatedArgumentsWithoutDeletion()
    {
        using var fixture = new OwnedPrivateRoot();
        using (new PrivateDiagnosticLease(fixture.Path, "fixture")) { }
        foreach (var args in new[]
        {
            new[] { "cleanup", "--private-diagnostics", fixture.Path, "--unknown", "1" },
            new[] { "cleanup", "--private-diagnostics", fixture.Path, "--private-diagnostics", fixture.Path },
            new[] { "cleanup", "--private-diagnostics", fixture.Path, "--older-than-days", "0" }
        })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, await App.RunAsync(args, fixture.Path, output, error, CancellationToken.None));
            Assert.Equal("", output.ToString());
            Assert.Contains("cleanup refused", error.ToString());
            Assert.True(File.Exists(fixture.ProtectedFile));
        }
    }

    private sealed class OwnedPrivateRoot : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory(PrivateDiagnosticLease.Prefix).FullName;
        internal string ProtectedFile => System.IO.Path.Combine(Path, "evaluation-bundle", "evidence.txt");
        internal OwnedPrivateRoot()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ProtectedFile)!);
            File.WriteAllText(ProtectedFile, "current invocation fixture evidence");
        }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
