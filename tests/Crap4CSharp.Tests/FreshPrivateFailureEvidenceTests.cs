using System.Text;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshPrivateFailureEvidenceTests
{
    [Fact]
    public void RetainsCompleteUnderlyingExceptionOnlyInsideExistingOwnedDirectory()
    {
        using var owned = TestDirectory.Create("crap-private-failure");
        Exception failure;
        try { throw new InvalidDataException("private-input-marker", new IOException("original-inner")); }
        catch (Exception actual) { failure = actual; }
        FreshPrivateFailureEvidence.TryRetain(owned.Path, failure, TestContext.Current.CancellationToken);
        var text = File.ReadAllText(Path.Combine(owned.Path, FreshPrivateFailureEvidence.FileName));
        Assert.Equal("diagnosticStatus=complete\n" + failure, text);
        Assert.Single(Directory.GetFiles(owned.Path));
        Assert.True(Encoding.UTF8.GetByteCount(text) <= FreshPrivateFailureEvidence.MaximumBytes);
    }

    [Fact]
    public void ExistingEvidenceIsNeverOverwrittenAndDiagnosticRefusalStaysOnPrimary()
    {
        using var owned = TestDirectory.Create("crap-private-failure");
        var path = owned.Write(FreshPrivateFailureEvidence.FileName, "first failure evidence");
        var failure = new InvalidDataException("original");
        FreshPrivateFailureEvidence.TryRetain(owned.Path, failure, TestContext.Current.CancellationToken);
        Assert.Equal("first failure evidence", File.ReadAllText(path));
        Assert.IsAssignableFrom<IOException>(failure.Data["PrivateFailureDiagnosticFailure"]);
        Assert.Equal("original", failure.Message);
    }

    [Fact]
    public void ClosedOriginalCancellationCannotCreateDiagnosticFile()
    {
        using var owned = TestDirectory.Create("crap-private-failure");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        FreshPrivateFailureEvidence.TryRetain(owned.Path, new IOException("original"), stopped.Token);
        Assert.Empty(Directory.GetFileSystemEntries(owned.Path));
    }

    [Fact]
    public void OversizedExceptionProducesExplicitUnretainedReasonWithoutTruncatedSuccess()
    {
        using var owned = TestDirectory.Create("crap-private-failure");
        FreshPrivateFailureEvidence.TryRetain(owned.Path, new IOException(new string('\u00e9', 65536)),
            TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(Path.Combine(owned.Path, FreshPrivateFailureEvidence.FileName));
        Assert.True(bytes.Length <= FreshPrivateFailureEvidence.MaximumBytes);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("diagnosticStatus=unretained-over-64KiB\n", text);
        Assert.DoesNotContain("diagnosticStatus=complete", text);
    }
}
