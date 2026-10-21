using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

// Adversarial protocol DATA controls. No synthetic task, image or byte identity
// here issues native/producer authority. The real-Csc regression is separate.
public sealed class CompilerRecipeObservationTests
{
    [Fact]
    public void Actual_terminal_is_required_and_late_events_cannot_claim_command_match()
    {
        using var fixture = TestDirectory.Create("crap-recipe-terminal-data");
        var (capture, prefix) = Capture(fixture.Path);
        capture.Arguments(["A.cs"], TestContext.Current.CancellationToken);
        capture.Command(prefix + " A.cs", TestContext.Current.CancellationToken);
        Assert.Equal("unproven", capture.Finish().CommandBinding);
        capture.Close();
        capture.Arguments(["A.cs"], TestContext.Current.CancellationToken);
        var result = capture.Finish();
        Assert.Equal("unproven", result.CommandBinding);
        Assert.Contains("recipe.eventAfterTaskTerminal", result.UnsupportedReasons);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("1:2:3:4:5", true)]
    public void Context_and_exact_order_are_required_for_same_command(string key, bool reorder)
    {
        using var fixture = TestDirectory.Create("crap-recipe-order-data");
        var (capture, prefix) = Capture(fixture.Path, key);
        capture.Arguments(["A.cs", "B.cs"], TestContext.Current.CancellationToken);
        capture.Command(prefix + (reorder ? " B.cs A.cs" : " A.cs B.cs"), TestContext.Current.CancellationToken);
        capture.Close();
        Assert.Equal("unproven", capture.Finish().CommandBinding);
    }

    [Theory]
    [InlineData("Optimize", "1")]
    [InlineData("Deterministic", "PRIVATE_INVALID")]
    public void Malformed_booleans_do_not_fall_through_numeric_or_export_their_values(string name, string value)
    {
        using var fixture = TestDirectory.Create("crap-recipe-boolean-data");
        var (capture, _) = Capture(fixture.Path);
        capture.Option(name, [value], TestContext.Current.CancellationToken);
        capture.Option("KeyFile", ["PRIVATE_KEY_FILE"], TestContext.Current.CancellationToken);
        capture.Close();
        var result = capture.Finish();
        Assert.Empty(result.Options);
        Assert.Contains("recipe.scalarOptionUnsupported", result.UnsupportedReasons);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_preserves_the_actual_caller_token()
    {
        using var fixture = TestDirectory.Create("crap-recipe-cancel-data");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var (capture, _) = Capture(fixture.Path);
        var error = Assert.ThrowsAny<OperationCanceledException>(() => capture.Arguments(["A.cs"], cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Fact]
    public void Typed_payload_is_consumable_and_binds_order_alias_embed_and_same_artifact_bytes()
    {
        using var fixture = TestDirectory.Create("crap-recipe-payload-data");
        var project = Path.Combine(fixture.Path, "App.csproj");
        var a = Path.Combine(fixture.Path, "A.cs"); var b = Path.Combine(fixture.Path, "B.cs");
        var reference = Path.Combine(fixture.Path, "Ref.dll"); var assembly = Path.Combine(fixture.Path, "out.dll");
        var pdb = Path.ChangeExtension(assembly, ".pdb");
        string[] arguments = ["/noconfig", "/nostdlib+", "/target:library", "/debug:portable", "/deterministic+", "/out:" + assembly,
            "/link:Alias=" + reference, "/embed:" + b, b, a];
        var (capture, prefix) = Capture(fixture.Path);
        capture.Arguments(arguments, TestContext.Current.CancellationToken);
        capture.Command(prefix + " " + string.Join(" ", arguments.Select(QuoteArgument)), TestContext.Current.CancellationToken);
        capture.Close();
        var observation = new CompilerObservation(project, [new("Sources", [b, a]), new("References", [reference])
            { ReferenceProperties = [new(reference, "Alias", true)] }])
        {
            CommandLineArguments = arguments, EmitParameters = [new("EmbeddedFiles", [b])], InvocationRecipe = capture.Finish(),
            Assembly = new(assembly, Hash("assembly-DATA"), 1), Pdb = new(pdb, Hash("pdb-DATA"), 1)
        };
        var sources = new Dictionary<string, ManifestInput>(PathIdentityPolicy.Current.Comparer)
            { [a] = Input("source", "A.cs"), [b] = Input("compiler-source", "B.cs") };
        var referenceInput = Input("reference", CompilerReferenceSnapshot.LogicalReference(Hash("Ref-DATA"), "Alias", true)) with
            { Sha256 = Hash("Ref-DATA"), Locator = "inputs/" + Hash("Ref-DATA") + ".bin", ReferenceAliases = "Alias", EmbedInteropTypes = true };
        CompilerRecipePayload.File[] files = [new("compiler-common", Path.Combine(fixture.Path, "sdk", "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll"), "build-recipe-compiler-common", ImmutableArray.Create((byte)1)),
            new("compiler-csharp", Path.Combine(fixture.Path, "sdk", "Roslyn", "bincore", "Microsoft.CodeAnalysis.CSharp.dll"), "build-recipe-compiler-csharp", ImmutableArray.Create((byte)2))];
        observation = WithImageIdentities(observation, fixture.Path);
        var images = observation.CompilerImages.Select(image => files.SingleOrDefault(file => file.Role == image.Role) is { } file
            ? image with { Sha256 = file.Sha256, Length = file.Bytes.Length } : image).ToArray();
        observation = observation with { CompilerImages = images, InvocationRecipe = observation.InvocationRecipe! with
            { Images = images.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() } };
        var first = CompilerRecipePayload.Create(observation, "context", "build", fixture.Path, sources, [referenceInput], files, TestContext.Current.CancellationToken);
        var second = CompilerRecipePayload.Create(observation, "context", "build", fixture.Path, sources, [referenceInput], files, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first.ToArray()); var root = json.RootElement;
        Assert.Equal("typed-input-data-only", root.GetProperty("disposition").GetString());
        Assert.Equal(new[] { b, a }, root.GetProperty("sources").EnumerateArray().Select(row => row.GetProperty("path").GetString()));
        Assert.Equal(arguments.Take(6), root.GetProperty("scalarArguments").EnumerateArray().Select(row => row.GetString()));
        var row = Assert.Single(root.GetProperty("references").EnumerateArray());
        Assert.Equal("Alias", Assert.Single(row.GetProperty("aliases").EnumerateArray()).GetString());
        Assert.True(row.GetProperty("embedInteropTypes").GetBoolean());
        Assert.Equal(b, Assert.Single(root.GetProperty("embeddedSourcePaths").EnumerateArray()).GetString());
        Assert.Equal("sha256:" + files[0].Sha256, root.GetProperty("compilerCommon").GetProperty("sha256").GetString());
        Assert.Equal(arguments.Length, root.GetProperty("argumentRoles").GetArrayLength());
        var error = Assert.Throws<ProjectContextException>(() => CompilerRecipePayload.Create(observation, "context", "build", fixture.Path,
            sources, [referenceInput with { EmbedInteropTypes = null }], files, TestContext.Current.CancellationToken));
        Assert.Equal("compiler.recipeBindingUnproven", error.Reason);
    }

    [Theory]
    [InlineData("unknown-option")]
    [InlineData("foreign-output")]
    [InlineData("foreign-map")]
    [InlineData("source-order")]
    [InlineData("reference-alias")]
    [InlineData("embedded-source")]
    public void Unknown_or_foreign_role_DATA_refuses_before_export(string change)
    {
        using var fixture = TestDirectory.Create("crap-recipe-refusal-data");
        var source = Path.Combine(fixture.Path, "A.cs"); var reference = Path.Combine(fixture.Path, "Ref.dll");
        var output = Path.Combine(fixture.Path, "out.dll");
        var arguments = new List<string> { "/noconfig", "/nostdlib+", "/target:library", "/debug:portable", "/deterministic+", "/out:" + output, "/reference:" + reference, source };
        var originalArguments = arguments.ToArray();
        if (change == "unknown-option") arguments.Insert(2, "/keyfile:PRIVATE_KEY");
        if (change == "foreign-output") arguments[5] = "/out:" + Path.Combine(fixture.Path, "foreign.dll");
        if (change == "foreign-map") arguments.Insert(2, "/pathmap:" + Path.GetPathRoot(fixture.Path) + "=/_/");
        if (change == "source-order") arguments.Add(source);
        if (change == "reference-alias") arguments[6] = "/reference:Foreign=" + reference;
        if (change == "embedded-source") arguments.Insert(2, "/embed:" + Path.Combine(fixture.Path, "missing.cs"));
        var observation = new CompilerObservation(Path.Combine(fixture.Path, "App.csproj"), [new("Sources", [source]), new("References", [reference])])
            { CommandLineArguments = arguments, Assembly = new(output, Hash("output"), 1) };
        // Hold all unrelated requirements valid: the same DATA observation passes
        // before the single adversarial role/option mutation above.
        Assert.Equal(originalArguments.Length, CompilerRecipePayload.ProjectArguments(observation with { CommandLineArguments = originalArguments },
            fixture.Path, TestContext.Current.CancellationToken).ArgumentRoles.Count);
        if (change is "unknown-option" or "foreign-map")
        {
            var profile = CompilerRecipePayload.ProjectArguments(observation, fixture.Path, TestContext.Current.CancellationToken);
            Assert.False(profile.IsSupported);
            Assert.DoesNotContain("PRIVATE_KEY", JsonSerializer.Serialize(profile), StringComparison.Ordinal);
        }
        else
            Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
                CompilerRecipePayload.ProjectArguments(observation, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("unknown")]
    [InlineData("map")]
    [InlineData("language")]
    public void Unsupported_profile_is_safe_DATA_and_raw_worker_values_do_not_serialize_as_normal_observation(string profile)
    {
        using var fixture = TestDirectory.Create("crap-recipe-optional-data");
        var observed = SupportedObservation(fixture.Path);
        var arguments = observed.CommandLineArguments.ToList();
        if (profile == "exe") arguments[2] = "/target:exe";
        if (profile == "unknown") arguments.Insert(2, "/keyfile:PRIVATE_UNKNOWN_KEY");
        if (profile == "map") arguments.Insert(2, "/pathmap:" + Path.GetPathRoot(fixture.Path) + "=/_/");
        if (profile == "language") arguments.Insert(2, "/langversion:999.0");
        observed = RebindArguments(observed, arguments);
        var selected = CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken);
        Assert.False(selected.IsSupported);
        var first = CompilerRecipePayload.Unsupported(observed, "context", "build", selected, TestContext.Current.CancellationToken);
        var second = CompilerRecipePayload.Unsupported(observed, "context", "build", selected, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);
        using var data = JsonDocument.Parse(first.ToArray());
        Assert.Equal("unsupported-input-profile", data.RootElement.GetProperty("disposition").GetString());
        Assert.False(data.RootElement.TryGetProperty("scalarArguments", out _));
        Assert.False(data.RootElement.TryGetProperty("compilerCommon", out _));
        Assert.DoesNotContain("PRIVATE_UNKNOWN_KEY", Encoding.UTF8.GetString(first.AsSpan()), StringComparison.Ordinal);
        var normal = JsonSerializer.Serialize(observed, ProjectContextLoader.JsonOptions);
        Assert.DoesNotContain("commandLineArguments", normal, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_UNKNOWN_KEY", normal, StringComparison.Ordinal);
        // Raw values are deliberately available only in the private worker envelope.
        var transport = new CompilerReadResponse(observed, null) { CommandLineArguments = arguments };
        var response = JsonSerializer.Deserialize<CompilerReadResponse>(JsonSerializer.Serialize(transport, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!;
        Assert.Equal(arguments, response.CommandLineArguments);
        Assert.Empty(response.Observation!.CommandLineArguments);
    }

    [Theory]
    [InlineData("argument-digest")]
    [InlineData("terminal")]
    [InlineData("image-identity")]
    [InlineData("source-role")]
    [InlineData("output-role")]
    public void Unsupported_option_never_downgrades_integrity_failure(string corruption)
    {
        using var fixture = TestDirectory.Create("crap-recipe-optional-integrity-data");
        var observed = SupportedObservation(fixture.Path);
        var arguments = observed.CommandLineArguments.ToList(); arguments.Insert(2, "/keyfile:PRIVATE_UNKNOWN_KEY");
        observed = RebindArguments(observed, arguments);
        Assert.False(CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken).IsSupported);
        if (corruption == "argument-digest") observed = observed with { InvocationRecipe = observed.InvocationRecipe! with { Arguments = [] } };
        if (corruption == "terminal") observed = observed with { InvocationRecipe = observed.InvocationRecipe! with { CommandBinding = "unproven" } };
        if (corruption == "image-identity") observed = observed with { CompilerImages = observed.CompilerImages.Select((image, index) => index == 0 ? image with { Sha256 = "malformed" } : image).ToArray() };
        if (corruption == "source-role") { arguments[^1] = Path.Combine(fixture.Path, "foreign.cs"); observed = RebindArguments(observed, arguments); }
        if (corruption == "output-role") { arguments[6] = "/out:" + Path.Combine(fixture.Path, "foreign.dll"); observed = RebindArguments(observed, arguments); }
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    private static CompilerObservation SupportedObservation(string root)
    {
        var source = Path.Combine(root, "A.cs"); var reference = Path.Combine(root, "Ref.dll"); var output = Path.Combine(root, "out.dll");
        string[] arguments = ["/noconfig", "/nostdlib+", "/target:library", "/debug:portable", "/deterministic+", "/out:" + output, "/reference:" + reference, source];
        var (capture, prefix) = Capture(root);
        capture.Arguments(arguments, TestContext.Current.CancellationToken);
        capture.Command(prefix + " " + string.Join(" ", arguments.Select(QuoteArgument)), TestContext.Current.CancellationToken); capture.Close();
        return WithImageIdentities(new(Path.Combine(root, "App.csproj"), [new("Sources", [source]), new("References", [reference])])
            { InvocationRecipe = capture.Finish(), CommandLineArguments = arguments, Assembly = new(output, Hash("assembly"), 1),
                Pdb = new(Path.ChangeExtension(output, ".pdb"), Hash("pdb"), 1) }, root);
    }
    private static CompilerObservation RebindArguments(CompilerObservation observation, IReadOnlyList<string> arguments) => observation with
    {
        CommandLineArguments = arguments.ToArray(),
        InvocationRecipe = observation.InvocationRecipe! with { Arguments = CompilerRecipeObservation.Identities(arguments, TestContext.Current.CancellationToken) }
    };
    private static CompilerObservation WithImageIdentities(CompilerObservation observation, string root)
    {
        var images = CompilerEvidence.CompilerImagePaths(SelectedSdk(root))
            .Select(image => new CompilerImageFile(image.Role, image.Path, 1, Hash(image.Role))).ToArray();
        return observation with { CompilerImages = images, InvocationRecipe = observation.InvocationRecipe! with
            { Images = images.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() } };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prior_graph_retains_only_known_args_or_safe_reasons_and_restores_current_SDK_roles(bool unsupported)
    {
        using var fixture = TestDirectory.Create("crap-recipe-prior-data");
        var original = SupportedObservation(fixture.Path);
        if (unsupported) original = RebindArguments(original, original.CommandLineArguments.Prepend("/keyfile:PRIVATE_UNKNOWN_KEY").ToArray());
        var retained = CompilerRecipePayload.Retain(original, fixture.Path, TestContext.Current.CancellationToken);
        var data = JsonSerializer.Serialize(retained, ProjectContextLoader.JsonOptions);
        Assert.DoesNotContain("PRIVATE_UNKNOWN_KEY", data, StringComparison.Ordinal);
        var decoded = JsonSerializer.Deserialize<CompilerRetainedRecipe>(data, ProjectContextLoader.JsonOptions)!;
        var prior = original with { InvocationRecipe = null, CommandLineArguments = [], EmitParameters = [], CompilerImages = [] };
        var restored = CompilerRecipePayload.Restore(decoded, prior, fixture.Path, SelectedSdk(fixture.Path), TestContext.Current.CancellationToken);
        var profile = CompilerRecipePayload.Classify(restored, fixture.Path, TestContext.Current.CancellationToken);
        Assert.Equal(!unsupported, profile.IsSupported);
        Assert.Equal(original.InvocationRecipe!.Arguments, restored.InvocationRecipe!.Arguments);
        Assert.Equal(CompilerEvidence.CompilerImagePaths(SelectedSdk(fixture.Path)).Select(image => image.Path), restored.CompilerImages.Select(image => image.Path));
        if (unsupported)
        {
            Assert.Empty(restored.CommandLineArguments);
            Assert.Equal(CompilerRecipePayload.Unsupported(original, "context", "build", CompilerRecipePayload.Classify(original, fixture.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
                CompilerRecipePayload.Unsupported(restored, "context", "build", profile, TestContext.Current.CancellationToken));
        }
        else Assert.Equal(original.CommandLineArguments, restored.CommandLineArguments);
        var malformed = decoded with { UnsupportedReasons = ["forged.unsupported.reason"] };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Restore(malformed, prior, fixture.Path, SelectedSdk(fixture.Path), TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("14.0", true)]
    [InlineData("latest", true)]
    [InlineData("999.0", false)]
    public void Target_language_DATA_is_not_interpreted_by_the_host_Roslyn_parser(string version, bool supported)
    {
        using var fixture = TestDirectory.Create("crap-recipe-target-language-data");
        var observed = SupportedObservation(fixture.Path);
        observed = RebindArguments(observed, observed.CommandLineArguments.Prepend("/langversion:" + version).ToArray());
        var profile = CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken);
        Assert.Equal(supported, profile.IsSupported);
        if (supported) Assert.Equal("/langversion:" + version, profile.ScalarArguments[0]);
        var (capture, _) = Capture(fixture.Path); capture.Option("LangVersion", [version], TestContext.Current.CancellationToken); capture.Close();
        var options = capture.Finish();
        if (supported) Assert.Equal(new CompilerScalarOption("LangVersion", "enum", version), Assert.Single(options.Options));
        else
        {
            Assert.Empty(options.Options);
            Assert.Contains("recipe.languageVersionUnsupported", options.UnsupportedReasons);
        }
    }

    private static ProjectContextSdkResolution SelectedSdk(string root) => new("test-DATA", Path.Combine(root, "sdk"), "test-DATA", root, Path.Combine(root, "dotnet"));

    private static string QuoteArgument(string value) => "\"" + value + "\"";
    private static ManifestInput Input(string role, string logical) => new(role, logical, "inputs/" + Hash(logical) + ".bin", 1, Hash(logical), "utf-8", false);
    private static (CompilerRecipeObservation Capture, string Prefix) Capture(string root, string key = "1:2:3:4:5")
    {
        var sdk = Path.Combine(root, "sdk"); var host = Path.Combine(root, "dotnet");
        return (new(sdk, host, key, Path.Combine(sdk, "Roslyn", "Microsoft.CSharp.Core.targets")),
            "\"" + host + "\" exec \"" + Path.Combine(sdk, "Roslyn", "bincore", "csc.dll") + "\"");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
