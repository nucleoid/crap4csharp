using System.Reflection;
using System.Runtime.ExceptionServices;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompletedRevalidationDeclarationTests
{
    private const string Provider = "finite-sdk-completed-tests-v1";
    private static ManifestContext Context() => new("context", "Library/Library.csproj", "net10.0", "Debug",
        "AnyCPU", "source", "context", "closure", true, true, [])
    {
        CurrentRevalidation = new(Provider, "Library/Library.csproj", "Library/bin/Debug/net10.0/Library.dll",
            "Library/bin/Debug/net10.0/Library.pdb")
    };

    private static void Validate(ManifestContext context)
    {
        var type = typeof(App).Assembly.GetType("CompletedRevalidationDeclaration");
        Assert.NotNull(type);
        var method = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { method.Invoke(null, [context, "Library/Library.csproj", "Library/bin/Debug/net10.0/Library.dll",
            "Library/bin/Debug/net10.0/Library.pdb"]); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
    }

    [Fact]
    public void IndependentlyEvaluatedCanonicalDeclarationAccepts() => Validate(Context());

    [Theory]
    [InlineData("missing")]
    [InlineData("incomplete")]
    [InlineData("provider")]
    [InlineData("project")]
    [InlineData("context-project")]
    [InlineData("assembly")]
    [InlineData("pdb")]
    [InlineData("reference")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("dot")]
    [InlineData("traversal")]
    [InlineData("case")]
    public void CoherentDeclarationSubstitutionRefuses(string mutation)
    {
        var original = Context(); var recipe = original.CurrentRevalidation!;
        var changed = mutation switch
        {
            "missing" => original with { CurrentRevalidation = null },
            "incomplete" => original with { ReuseRecipeComplete = false },
            "provider" => original with { CurrentRevalidation = recipe with { Provider = "sdk-project-context-output-v1" } },
            "project" => original with { CurrentRevalidation = recipe with { Project = "Other/Other.csproj" } },
            "context-project" => original with { Project = "Other/Other.csproj" },
            "assembly" => original with { CurrentRevalidation = recipe with { AssemblyPath = "Library/bin/Other.dll" } },
            "pdb" => original with { CurrentRevalidation = recipe with { PdbPath = "Library/bin/Other.pdb" } },
            "reference" => original with { CurrentRevalidation = recipe with { References = [new("references/saved.dll", "Other.dll")] } },
            "absolute" => original with { CurrentRevalidation = recipe with { AssemblyPath = "/Library/bin/Debug/net10.0/Library.dll" } },
            "backslash" => original with { CurrentRevalidation = recipe with { Project = "Library\\Library.csproj" } },
            "dot" => original with { CurrentRevalidation = recipe with { Project = "Library/./Library.csproj" } },
            "traversal" => original with { CurrentRevalidation = recipe with { Project = "Other/../Library/Library.csproj" } },
            "case" => original with { CurrentRevalidation = recipe with { AssemblyPath = "library/bin/Debug/net10.0/Library.dll" } },
            _ => throw new InvalidOperationException()
        };
        Assert.Equal("provenance.completedDeclarationUnproven", Assert.Throws<PolicyException>(() => Validate(changed)).Code);
    }
}
