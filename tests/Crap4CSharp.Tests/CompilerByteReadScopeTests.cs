using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerByteReadScopeTests
{
    [Fact]
    public void RepeatedReadsStillChargeClosureBudgetAndACompletedScopeCannotReturnBytes()
    {
        using var fixture = TestDirectory.Create("crap4csharp-byte-scope-budget");
        var file = fixture.Write("reference.dll", "reference");
        var reads = new CompilerByteReadScope(TestContext.Current.CancellationToken);
        long total = 0;
        var bytes = reads.Read(file, ref total);
        Assert.Equal(bytes, reads.Read(file, ref total));
        Assert.Equal(bytes.Length * 2, total);
        total = CompilerPhaseSnapshot.MaximumClosureBytes;
        Assert.Equal("compiler.outputLimit", Assert.Throws<ProjectContextException>(() => reads.Read(file, ref total)).Reason);
        reads.Complete();
        Assert.Throws<ProjectContextException>(() => reads.Read(file, ref total));
    }

    [Fact]
    public void ANewScopeReadsPreservedTimestampChangesRatherThanReusingPreviousBytes()
    {
        using var fixture = TestDirectory.Create("crap4csharp-byte-scope-boundary");
        var file = fixture.Write("reference.dll", "original!");
        var time = File.GetLastWriteTimeUtc(file);
        long total = 0;
        var first = new CompilerByteReadScope(TestContext.Current.CancellationToken);
        var original = first.Read(file, ref total);
        first.Complete();
        File.WriteAllText(file, "modified!");
        File.SetLastWriteTimeUtc(file, time);
        var second = new CompilerByteReadScope(TestContext.Current.CancellationToken);
        var changed = second.Read(file, ref total);
        Assert.False(original.AsSpan().SequenceEqual(changed.AsSpan()));
        Assert.Equal("modified!", System.Text.Encoding.UTF8.GetString(changed.AsSpan()));
        second.Complete();
    }

    [Fact]
    public void AnAncestorRedirectAfterReadRefusesOnCompletion()
    {
        using var fixture = TestDirectory.Create("crap4csharp-byte-scope-redirect");
        var file = fixture.Write("inputs/reference.dll", "reference");
        fixture.Write("other/reference.dll", "reference");
        var original = Path.GetDirectoryName(file)!;
        long total = 0;
        var reads = new CompilerByteReadScope(TestContext.Current.CancellationToken);
        reads.Read(file, ref total);
        Directory.Move(original, original + "-retained");
        ReparseTestDirectory.Link(original, Path.Combine(fixture.Path, "other"));
        try { Assert.Throws<ProjectContextException>(() => reads.Complete()); }
        finally { Directory.Delete(original); }
    }
}
