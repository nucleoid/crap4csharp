using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerPathBatchTests
{
    [Fact]
    public void CompletionRefusesAncestorRedirectedAfterFirstRead()
    {
        using var fixture = TestDirectory.Create("crap4csharp-path-batch");
        var source = fixture.Write("inputs/source.cs", "class C {}");
        var target = fixture.Write("other/source.cs", "class Other {}");
        var original = Path.GetDirectoryName(source)!;
        var batch = new CompilerPathBatch(TestContext.Current.CancellationToken, Refuse);
        batch.Check(source);
        Directory.Move(original, original + "-retained");
        ReparseTestDirectory.Link(original, Path.GetDirectoryName(target)!);
        try
        {
            Assert.Equal("test.pathRedirect", Assert.Throws<ProjectContextException>(() => batch.Complete()).Reason);
            Assert.Equal("test.pathRedirect", Assert.Throws<ProjectContextException>(() =>
                new CompilerPathBatch(TestContext.Current.CancellationToken, Refuse).Check(source)).Reason);
        }
        finally { Directory.Delete(original); }
    }

    [Fact]
    public void EachFileIsCheckedAgainAndCancellationCannotCompleteTheBatch()
    {
        using var fixture = TestDirectory.Create("crap4csharp-path-batch-file");
        var path = fixture.Write("source.cs", "class C {}");
        using var cancellation = new CancellationTokenSource();
        var batch = new CompilerPathBatch(cancellation.Token, Refuse);
        batch.Check(path);
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => batch.Check(path));
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => batch.Complete());
    }

    private static ProjectContextException Refuse() => new("test.pathRedirect", "Redirected input path.");

}
