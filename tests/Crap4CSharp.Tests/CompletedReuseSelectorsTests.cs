using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompletedReuseSelectorsTests
{
    private const string Scope = "{\"version\":1,\"sources\":[\"Library/Code.cs\"],\"sourceState\":\"worktree\",\"granularity\":\"file\",\"requestedScope\":\"all\",\"revision\":null}";
    private const string Node = "{\"id\":\"id\",\"project\":\"pathhash\",\"configuration\":\"Debug\",\"platform\":\"AnyCPU\",\"framework\":\"net10.0\",\"dependencies\":[],\"propertiesHash\":\"hash\",\"contextId\":\"context\",\"test\":false,\"includeTests\":false,\"includeGenerated\":false,\"language\":\"Preview\",\"sourceKind\":\"Regular\",\"symbolsHash\":\"symbols\",\"output\":\"output\",\"intermediate\":\"intermediate\",\"referenceOutput\":\"reference\"}";
    private static string Plan(string node) => "{\"graph\":[" + node + "]}";
    private static object Read(string scope, string plan, CancellationToken token = default)
    {
        var type = typeof(App).Assembly.GetType("CompletedReuseSelectors");
        Assert.NotNull(type);
        var method = type.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try { return method.Invoke(null, [Encoding.UTF8.GetBytes(scope), Encoding.UTF8.GetBytes(plan), token])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    [Fact]
    public void SupportedFiniteSelectorsPreserveFileGranularityAndFalseFlags()
    {
        var value = Read(Scope, Plan(Node), TestContext.Current.CancellationToken);
        Assert.Equal("file", value.GetType().GetProperty("Granularity")!.GetValue(value));
        Assert.Equal("all", value.GetType().GetProperty("Scope")!.GetValue(value));
        Assert.Equal(false, value.GetType().GetProperty("IncludeTests")!.GetValue(value));
        Assert.Equal(false, value.GetType().GetProperty("IncludeGenerated")!.GetValue(value));
    }

    [Theory]
    [InlineData("duplicate-scope")]
    [InlineData("unknown-scope")]
    [InlineData("absent-scope")]
    [InlineData("staged")]
    [InlineData("head")]
    [InlineData("granularity")]
    [InlineData("duplicate-source")]
    [InlineData("escaping-source")]
    [InlineData("wrong-source-type")]
    [InlineData("wrong-version")]
    [InlineData("empty-graph")]
    [InlineData("duplicate-flag")]
    [InlineData("missing-flag")]
    [InlineData("wrong-flag-type")]
    [InlineData("mixed-generated")]
    [InlineData("mixed-tests")]
    [InlineData("unknown-node-field")]
    [InlineData("oversize")]
    public void AmbiguousOrUnsupportedDeclarationsRefuse(string mutation)
    {
        var scope = Scope;
        var node = Node;
        var plan = Plan(node);
        switch (mutation)
        {
            case "duplicate-scope": scope = scope.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal); break;
            case "unknown-scope": scope = scope.Replace("\"version\":1", "\"version\":1,\"command\":\"execute\"", StringComparison.Ordinal); break;
            case "absent-scope": scope = scope.Replace("\"requestedScope\":\"all\",", "", StringComparison.Ordinal); break;
            case "staged": scope = scope.Replace("\"all\"", "\"staged\"", StringComparison.Ordinal); break;
            case "head": scope = scope.Replace("\"worktree\"", "\"head\"", StringComparison.Ordinal); break;
            case "granularity": scope = scope.Replace("\"file\"", "\"line\"", StringComparison.Ordinal); break;
            case "duplicate-source": scope = scope.Replace("\"Library/Code.cs\"", "\"Library/Code.cs\",\"Library/Code.cs\"", StringComparison.Ordinal); break;
            case "escaping-source": scope = scope.Replace("Library/Code.cs", "../Code.cs", StringComparison.Ordinal); break;
            case "wrong-source-type": scope = scope.Replace("[\"Library/Code.cs\"]", "false", StringComparison.Ordinal); break;
            case "wrong-version": scope = scope.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal); break;
            case "empty-graph": plan = "{\"graph\":[]}"; break;
            case "duplicate-flag": plan = Plan(node.Replace("\"includeTests\":false", "\"includeTests\":false,\"includeTests\":false", StringComparison.Ordinal)); break;
            case "missing-flag": plan = Plan(node.Replace("\"includeTests\":false,", "", StringComparison.Ordinal)); break;
            case "wrong-flag-type": plan = Plan(node.Replace("\"includeTests\":false", "\"includeTests\":\"false\"", StringComparison.Ordinal)); break;
            case "mixed-generated": plan = Plan(node + "," + node.Replace("\"includeGenerated\":false", "\"includeGenerated\":true", StringComparison.Ordinal)); break;
            case "mixed-tests": plan = Plan(node + "," + node.Replace("\"includeTests\":false", "\"includeTests\":true", StringComparison.Ordinal)); break;
            case "unknown-node-field": plan = Plan(node.Replace("\"id\":\"id\"", "\"id\":\"id\",\"command\":\"execute\"", StringComparison.Ordinal)); break;
            case "oversize": scope = new string(' ', 256 * 1024 + 1); break;
        }
        var error = Assert.Throws<PolicyException>(() => Read(scope, plan, TestContext.Current.CancellationToken));
        Assert.Equal("provenance.completedSelectorsUnsupported", error.Code);
    }

    [Fact]
    public void CancellationPrecedesParsing()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read(Scope, Plan(Node), source.Token));
    }
}
