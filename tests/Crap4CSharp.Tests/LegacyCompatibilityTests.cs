using System.Text.Json;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class LegacyCompatibilityTests
{
    [Fact]
    public void OrdinaryMethodsV1MatchesPinnedInventoryAndScores()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-1.json");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        var root = fixture.RootElement;

        var methods = new SourceAnalyzer().AnalyzeText(root.GetProperty("source").GetString()!, "Legacy.cs", CSharpParseOptions.Default)
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        var expected = root.GetProperty("methods").EnumerateArray().Select(item => (
            Name: item.GetProperty("displayName").GetString()!,
            Complexity: item.GetProperty("complexity").GetInt32())).ToArray();

        Assert.Equal(ComplexityRules.OrdinaryMethodsV1, root.GetProperty("ruleset").GetString());
        Assert.Equal(expected, methods.Select(item => (Name: item.DisplayName, item.Complexity)).ToArray());
        Assert.Equal(2, methods.Length);
    }
}
