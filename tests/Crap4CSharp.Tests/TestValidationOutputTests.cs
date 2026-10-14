using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestValidationOutputTests
{
    [Fact]
    public void DefaultOutputStaysInOwnedFixture()
    {
        using var fixture = TestDirectory.Create("crap4csharp-validation-output");
        var path = TestValidationOutput.CreateDirectory("receipt", fixture.Path, null);
        Assert.Equal(fixture.Path, Path.GetDirectoryName(path));
    }

    [Fact]
    public void ExplicitOutputIsUniqueAndDoesNotOverwriteOtherTests()
    {
        using var fixture = TestDirectory.Create("crap4csharp-validation-output");
        var root = Path.Combine(fixture.Path, "external");
        var first = TestValidationOutput.CreateDirectory("receipt", fixture.Path, root);
        File.WriteAllText(Path.Combine(first, "sentinel"), "preserved");
        var second = TestValidationOutput.CreateDirectory("receipt", fixture.Path, root);
        Assert.NotEqual(first, second);
        Assert.Equal(root, Path.GetDirectoryName(second));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(first, "sentinel")));
    }

    [Fact]
    public void RelativeExternalOutputRefuses()
    {
        using var fixture = TestDirectory.Create("crap4csharp-validation-output");
        Assert.Throws<InvalidOperationException>(() => TestValidationOutput.CreateDirectory("receipt", fixture.Path, "relative"));
    }
}
