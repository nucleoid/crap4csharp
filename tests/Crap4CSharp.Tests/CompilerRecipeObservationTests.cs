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
            CommandLineArguments = arguments,
            EmitParameters = [new("EmbeddedFiles", [b])],
            InvocationRecipe = capture.Finish(),
            Assembly = new(assembly, Hash("assembly-DATA"), 1),
            Pdb = new(pdb, Hash("pdb-DATA"), 1)
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
        observation = observation with
        {
            CompilerImages = images,
            InvocationRecipe = observation.InvocationRecipe! with
            { Images = images.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() }
        };
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
        {
            InvocationRecipe = capture.Finish(),
            CommandLineArguments = arguments,
            Assembly = new(output, Hash("assembly"), 1),
            Pdb = new(Path.ChangeExtension(output, ".pdb"), Hash("pdb"), 1)
        }, root);
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
        return observation with
        {
            CompilerImages = images,
            InvocationRecipe = observation.InvocationRecipe! with
            { Images = images.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() }
        };
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

    [Theory]
    [InlineData("task-file", "recipe.bindingUnsupported")]
    [InlineData("command-prefix", "recipe.bindingUnsupported")]
    [InlineData("task-option", "recipe.scalarProfileUnsupported")]
    [InlineData("bare-embed", "recipe.emitProfileUnsupported")]
    [InlineData("non-source-embed", "recipe.emitProfileUnsupported")]
    public void Optional_profiles_keep_ordinary_observation_and_export_only_fixed_unsupported_DATA(string change, string reason)
    {
        using var fixture = TestDirectory.Create("crap-optional-recipe-profile-data");
        var observed = SupportedObservation(fixture.Path);
        var ordinaryParameters = observed.Parameters; var ordinaryAssembly = observed.Assembly; var ordinaryPdb = observed.Pdb;
        var arguments = observed.CommandLineArguments.ToList();
        if (change is "bare-embed" or "non-source-embed")
        {
            var embedded = change == "bare-embed" ? observed.Parameters.Single(p => p.Name == "Sources").Items[0] : Path.Combine(fixture.Path, "not-a-source.txt");
            arguments.Insert(0, change == "bare-embed" ? "/embed" : "/embed:" + embedded);
            observed = RebindArguments(observed, arguments) with { EmitParameters = [new("EmbeddedFiles", [embedded])] };
        }
        else
        {
            var sdk = Path.Combine(fixture.Path, "sdk"); var host = Path.Combine(fixture.Path, "dotnet");
            var task = change == "task-file" ? Path.Combine(fixture.Path, "custom-compiler.targets") : Path.Combine(sdk, "Roslyn", "Microsoft.CSharp.Core.targets");
            var capture = new CompilerRecipeObservation(sdk, host, "1:2:3:4:5", task);
            capture.Arguments(arguments, TestContext.Current.CancellationToken);
            var prefix = change == "command-prefix" ? QuoteArgument(Path.Combine(fixture.Path, "custom-compiler")) :
                QuoteArgument(host) + " exec " + QuoteArgument(Path.Combine(sdk, "Roslyn", "bincore", "csc.dll"));
            capture.Command(prefix + " " + string.Join(" ", arguments.Select(QuoteArgument)), TestContext.Current.CancellationToken);
            if (change == "task-option") capture.Option("Optimize", ["PRIVATE_UNSUPPORTED_VALUE"], TestContext.Current.CancellationToken);
            capture.Close(); observed = WithImageIdentities(observed with { InvocationRecipe = capture.Finish() }, fixture.Path);
        }
        var profile = CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken);
        Assert.Contains(reason, profile.UnsupportedReasons); Assert.False(profile.IsSupported);
        var data = CompilerRecipePayload.Unsupported(observed, "context", "build", profile, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("PRIVATE_UNSUPPORTED_VALUE", Encoding.UTF8.GetString(data.AsSpan()), StringComparison.Ordinal);
        Assert.DoesNotContain("compilerCommon", Encoding.UTF8.GetString(data.AsSpan()), StringComparison.Ordinal);
        Assert.Same(ordinaryParameters, observed.Parameters); Assert.Same(ordinaryAssembly, observed.Assembly); Assert.Same(ordinaryPdb, observed.Pdb);
        var retained = CompilerRecipePayload.Retain(observed, fixture.Path, TestContext.Current.CancellationToken);
        var prior = observed with { InvocationRecipe = null, CommandLineArguments = [], EmitParameters = [], CompilerImages = [] };
        var restored = CompilerRecipePayload.Restore(retained, prior, fixture.Path, SelectedSdk(fixture.Path), TestContext.Current.CancellationToken);
        Assert.Equal(data, CompilerRecipePayload.Unsupported(restored, "context", "build",
            CompilerRecipePayload.Classify(restored, fixture.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("task-file")]
    [InlineData("command-prefix")]
    public void Unproven_optional_binding_does_not_hide_a_corrupted_observed_argument_identity(string change)
    {
        using var fixture = TestDirectory.Create("crap-optional-recipe-integrity-data");
        var observed = SupportedObservation(fixture.Path);
        var reason = change == "task-file" ? "recipe.selectedSdkTaskFileUnproven" : "recipe.compilerCommandPrefixUnproven";
        observed = observed with
        {
            InvocationRecipe = observed.InvocationRecipe! with
            {
                TaskBinding = change == "task-file" ? "unproven" : "selected-sdk-core-target",
                CommandBinding = "unproven",
                UnsupportedReasons = observed.InvocationRecipe.UnsupportedReasons.Append(reason).Append("recipe.commandArgumentsUnproven").ToArray(),
                Arguments = observed.InvocationRecipe.Arguments.Select((argument, index) => index == 0 ? argument with { Sha256 = new string('0', 64) } : argument).ToArray()
            }
        };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void Supported_three_thousand_sources_and_three_hundred_references_use_actual_JSON_size()
    {
        using var fixture = TestDirectory.Create("crap-optional-recipe-scale-data");
        var observed = SupportedObservation(fixture.Path);
        var sourcePaths = Enumerable.Range(0, 3000).Select(index => Path.Combine(fixture.Path, "Source" + index.ToString("D4") + ".cs")).ToArray();
        var referencePaths = Enumerable.Range(0, 300).Select(index => Path.Combine(fixture.Path, "Ref" + index.ToString("D4") + ".dll")).ToArray();
        var arguments = observed.CommandLineArguments.Take(6).Concat(referencePaths.Select(path => "/reference:" + path)).Concat(sourcePaths).ToArray();
        observed = RebindArguments(observed with { Parameters = [new("Sources", sourcePaths), new("References", referencePaths)] }, arguments);
        Assert.True(CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken).IsSupported);
        var sources = sourcePaths.ToDictionary(path => path, path => Input("source", Path.GetFileName(path)), PathIdentityPolicy.Current.Comparer);
        var references = referencePaths.Select(path =>
        {
            var hash = Hash(path);
            return Input("reference", CompilerReferenceSnapshot.LogicalReference(hash, null, false)) with { Sha256 = hash, Locator = "inputs/" + hash + ".bin" };
        }).ToArray();
        var files = observed.CompilerImages.Where(image => image.Role is "compiler-common" or "compiler-csharp").Select((image, index) =>
            new CompilerRecipePayload.File(image.Role, image.Path, "build-recipe-" + image.Role, ImmutableArray.Create((byte)(index + 1)))).ToArray();
        var imageFiles = observed.CompilerImages.Select(image => files.SingleOrDefault(file => file.Role == image.Role) is { } file ? image with { Length = file.Bytes.Length, Sha256 = file.Sha256 } : image).ToArray();
        observed = observed with
        {
            CompilerImages = imageFiles,
            InvocationRecipe = observed.InvocationRecipe! with
            { Images = imageFiles.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() }
        };
        Assert.True(CompilerRecipePayload.TryCreate(observed, "context", "build", fixture.Path, sources, references, files,
            TestContext.Current.CancellationToken, out var data));
        using var parsed = JsonDocument.Parse(data.AsMemory());
        Assert.Equal(3000, parsed.RootElement.GetProperty("sources").GetArrayLength());
        Assert.Equal(300, parsed.RootElement.GetProperty("references").GetArrayLength());
        Assert.True(data.Length <= CompilerEvidence.MaximumTransportBytes);
    }

    [Fact]
    public void Optional_capture_size_limit_keeps_ordinary_inputs_and_retains_compact_unsupported_roundtrip()
    {
        using var fixture = TestDirectory.Create("crap-optional-recipe-size-data");
        var ordinary = SupportedObservation(fixture.Path);
        var (capture, prefix) = Capture(fixture.Path);
        capture.Arguments(Enumerable.Repeat("A.cs", 32769).ToArray(), TestContext.Current.CancellationToken);
        capture.Command(prefix + " A.cs", TestContext.Current.CancellationToken); capture.Close();
        Assert.True(capture.SizeDeclined); Assert.Empty(capture.RawArguments);
        var observation = WithImageIdentities(ordinary with { CommandLineArguments = [], InvocationRecipe = capture.Finish() }, fixture.Path);
        var profile = CompilerRecipePayload.Classify(observation, fixture.Path, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "recipe.sizeUnsupported" }, profile.UnsupportedReasons);
        var compact = CompilerRecipePayload.SizeUnsupported(observation);
        Assert.Same(ordinary.Parameters, compact.Parameters); Assert.Same(ordinary.Assembly, compact.Assembly);
        var retained = CompilerRecipePayload.Retain(compact, fixture.Path, TestContext.Current.CancellationToken);
        Assert.Empty(retained.KnownArguments); Assert.Empty(retained.Observation.Arguments); Assert.Empty(retained.Observation.Images);
        var restored = CompilerRecipePayload.Restore(retained, ordinary with { InvocationRecipe = null, CommandLineArguments = [], CompilerImages = [] },
            fixture.Path, SelectedSdk(fixture.Path), TestContext.Current.CancellationToken);
        Assert.Equal(profile.UnsupportedReasons, CompilerRecipePayload.Classify(restored, fixture.Path, TestContext.Current.CancellationToken).UnsupportedReasons);
    }

    [Fact]
    public async Task Missing_optional_SDK_image_is_fixed_unsupported_DATA_and_cancellation_is_not_downgraded()
    {
        using var fixture = TestDirectory.Create("crap-optional-recipe-image-data");
        var missing = await CompilerEvidence.CaptureCompilerImages(SelectedSdk(fixture.Path), TestContext.Current.CancellationToken);
        Assert.Empty(missing.Files); Assert.Equal("recipe.compilerImageUnavailable", missing.UnsupportedReason);
        var observation = SupportedObservation(fixture.Path);
        observation = observation with
        {
            CompilerImages = [],
            InvocationRecipe = observation.InvocationRecipe! with
            { Images = [], UnsupportedReasons = observation.InvocationRecipe.UnsupportedReasons.Append(missing.UnsupportedReason!).ToArray() }
        };
        Assert.Equal(new[] { "recipe.compilerImageUnavailable" }, CompilerRecipePayload.Classify(observation, fixture.Path, TestContext.Current.CancellationToken).UnsupportedReasons);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompilerEvidence.CaptureCompilerImages(SelectedSdk(fixture.Path), cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
    }

    [Fact]
    public void Exact_JSON_admission_counts_escaping_and_keeps_original_mandatory_prior_limit()
    {
        var payload = new { value = new string('"', 2048) };
        var actual = JsonSerializer.SerializeToUtf8Bytes(payload, ProjectContextLoader.JsonOptions);
        Assert.True(CompilerRecipeBudget.TrySerialize(payload, ProjectContextLoader.JsonOptions, actual.Length, out var admitted));
        Assert.Equal(actual, admitted);
        Assert.False(CompilerRecipeBudget.TrySerialize(payload, ProjectContextLoader.JsonOptions, actual.Length - 1, out var refused));
        Assert.Empty(refused);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var empty = new FreshPriorBuild.Recipe("p", "", "", []);
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty, options).Length;
        var full = empty with { Workspace = new string('x', FreshPriorBuild.MaximumBytes - overhead) };
        var fullBytes = JsonSerializer.SerializeToUtf8Bytes(full, options);
        Assert.Equal(FreshPriorBuild.MaximumBytes, fullBytes.Length);
        FreshPriorBuild.ValidateSlices(full, fullBytes.Length);
        FreshPriorBuild.ValidateSlices(full, fullBytes.Length + FreshPriorBuild.MaximumOptionalBytes);
        Assert.Equal("check.priorBuildInvalid", Assert.Throws<PolicyException>(() =>
            FreshPriorBuild.ValidateSlices(full, fullBytes.Length + FreshPriorBuild.MaximumOptionalBytes + 1)).Code);
        Assert.Equal("check.priorBuildInvalid", Assert.Throws<PolicyException>(() =>
            FreshPriorBuild.ValidateSlices(full with { Workspace = full.Workspace + "x" }, fullBytes.Length + 1)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optional_size_fallback_does_not_erase_proven_mismatch_or_late_event(bool late)
    {
        using var fixture = TestDirectory.Create("crap-optional-recipe-size-integrity-data");
        var observed = SupportedObservation(fixture.Path);
        var (capture, prefix) = Capture(fixture.Path);
        capture.Arguments(["A.cs"], TestContext.Current.CancellationToken);
        capture.Command(prefix + (late ? " A.cs" : " B.cs"), TestContext.Current.CancellationToken);
        capture.DeclineSize(); capture.Close();
        if (late) capture.Arguments([], TestContext.Current.CancellationToken);
        observed = WithImageIdentities(observed with { CommandLineArguments = [], InvocationRecipe = capture.Finish() }, fixture.Path);
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Late_or_duplicate_emit_events_remain_errors_after_optional_size_decline(bool late)
    {
        using var fixture = TestDirectory.Create("crap-emit-terminal-data");
        var (capture, prefix) = Capture(fixture.Path);
        capture.Arguments(["A.cs"], TestContext.Current.CancellationToken);
        capture.Command(prefix + " A.cs", TestContext.Current.CancellationToken);
        Assert.True(capture.BeginEmitParameter("EmbeddedFiles"));
        capture.DeclineSize();
        if (late) capture.Close();
        Assert.False(capture.BeginEmitParameter(late ? "PdbFile" : "EmbeddedFiles"));
        if (!late) capture.Close();
        var observation = SupportedObservation(fixture.Path) with { InvocationRecipe = capture.Finish(), CommandLineArguments = [] };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipeObservation.WithoutOptionalRecipe(observation, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("dotnet-host")]
    [InlineData("csc")]
    [InlineData("compiler-deps")]
    [InlineData("compiler-runtimeconfig")]
    [InlineData("sdk-csc-target")]
    [InlineData("sdk-csc-task")]
    public void Explicit_prior_rejects_drift_in_every_other_selected_SDK_image_without_rebuilding(string role)
    {
        using var fixture = TestDirectory.Create("crap-prior-sdk-pool-data");
        var sdk = SelectedSdk(fixture.Path);
        var pool = CompilerEvidence.CompilerImagePaths(sdk);
        var identities = pool.Select(image =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(image.Path)!);
            var content = Encoding.UTF8.GetBytes("actual DATA bytes " + image.Role);
            File.WriteAllBytes(image.Path, content);
            return new CompilerImageIdentity(image.Role, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        }).ToArray();
        FreshPriorBuild.RequireCurrentRecipeImages(identities, sdk, TestContext.Current.CancellationToken);
        var selected = pool.Single(image => image.Role == role).Path;
        var stamp = File.GetLastWriteTimeUtc(selected); var before = File.ReadAllBytes(selected);
        File.WriteAllBytes(selected, before.Select(value => (byte)(value ^ 1)).ToArray()); File.SetLastWriteTimeUtc(selected, stamp);
        Assert.Equal(before.Length, new FileInfo(selected).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(selected));
        Assert.Equal("check.priorBuildInvalid", Assert.Throws<PolicyException>(() =>
            FreshPriorBuild.RequireCurrentRecipeImages(identities, sdk, TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public void Optional_absence_keeps_original_mandatory_transport_and_is_not_a_supported_recipe()
    {
        using var fixture = TestDirectory.Create("crap-optional-absence-data");
        var original = SupportedObservation(fixture.Path);
        var absent = CompilerRecipeObservation.WithoutOptionalRecipe(original, TestContext.Current.CancellationToken);
        Assert.Null(absent.InvocationRecipe); Assert.Same(original.Parameters, absent.Parameters); Assert.Same(original.Assembly, absent.Assembly);
        var json = JsonSerializer.SerializeToUtf8Bytes(new CompilerReadResponse(absent, null), ProjectContextLoader.JsonOptions);
        using var parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.GetProperty("observation").TryGetProperty("invocationRecipe", out _));
        Assert.False(parsed.RootElement.TryGetProperty("commandLineArguments", out _));
        Assert.False(parsed.RootElement.TryGetProperty("emitParameters", out _));
        var corrupt = original with
        {
            InvocationRecipe = original.InvocationRecipe! with
            { Arguments = original.InvocationRecipe.Arguments.Select((value, index) => index == 0 ? value with { Sha256 = new string('0', 64) } : value).ToArray() }
        };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipeObservation.WithoutOptionalRecipe(corrupt, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void Worker_exact_mandatory_capacity_uses_absence_without_hiding_malformed_argv()
    {
        using var fixture = TestDirectory.Create("crap-worker-optional-absence-data");
        var original = SupportedObservation(fixture.Path);
        var absent = CompilerRecipeObservation.WithoutOptionalRecipe(original, TestContext.Current.CancellationToken);
        var mandatory = new CompilerReadResponse(absent, null);
        var limit = JsonSerializer.SerializeToUtf8Bytes(mandatory, ProjectContextLoader.JsonOptions).Length;
        var response = new CompilerReadResponse(original, null)
        { CommandLineArguments = original.CommandLineArguments, EmitParameters = original.EmitParameters };
        var bytes = CompilerEvidenceBootstrap.SerializeResponse(ref response, TestContext.Current.CancellationToken, limit);
        var actual = Assert.IsType<CompilerObservation>(response.Observation); Assert.Null(actual.InvocationRecipe);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(mandatory, ProjectContextLoader.JsonOptions), bytes);
        var corrupt = original with
        {
            InvocationRecipe = original.InvocationRecipe! with
            { Arguments = original.InvocationRecipe.Arguments.Select((value, index) => index == 0 ? value with { Sha256 = new string('0', 64) } : value).ToArray() }
        };
        response = new CompilerReadResponse(corrupt, null)
        { CommandLineArguments = corrupt.CommandLineArguments, EmitParameters = corrupt.EmitParameters };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerEvidenceBootstrap.SerializeResponse(ref response, TestContext.Current.CancellationToken, limit)).Reason);
    }

    [Fact]
    public void Prior_optional_contribution_matches_actual_node_JSON_and_retained_unsupported_absence_is_valid()
    {
        using var fixture = TestDirectory.Create("crap-prior-contribution-data");
        var observed = SupportedObservation(fixture.Path);
        Assert.True(FreshPriorBuild.TryOptionalContribution(observed, fixture.Path, TestContext.Current.CancellationToken, out var contribution));
        var output = new FreshPriorBuild.SavedOutput(new FreshPriorBuild.SavedPath("A.dll", "path-data"), Hash("output"), 1);
        var node = new FreshPriorBuild.SavedNode("node-data", [], output, output, null, []);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var mandatory = JsonSerializer.SerializeToUtf8Bytes(node, options);
        var retained = CompilerRecipePayload.Retain(observed, fixture.Path, TestContext.Current.CancellationToken);
        var full = JsonSerializer.SerializeToUtf8Bytes(node with { CompilerRecipe = retained }, options);
        Assert.Equal(full.Length - mandatory.Length, contribution);
        var arguments = observed.CommandLineArguments.Concat(["/unknown:PRIVATE_UNSUPPORTED"]).ToArray();
        var unsupported = RebindArguments(observed, arguments);
        retained = CompilerRecipePayload.Retain(unsupported, fixture.Path, TestContext.Current.CancellationToken);
        var prior = unsupported with { InvocationRecipe = null, CommandLineArguments = [], EmitParameters = [], CompilerImages = [] };
        var restored = CompilerRecipePayload.Restore(retained, prior, fixture.Path, SelectedSdk(fixture.Path), TestContext.Current.CancellationToken);
        Assert.NotEmpty(restored.InvocationRecipe!.Arguments); Assert.Empty(restored.CommandLineArguments);
        Assert.Null(CompilerRecipeObservation.WithoutOptionalRecipe(restored, TestContext.Current.CancellationToken).InvocationRecipe);
    }

    private static ProjectContextSdkResolution SelectedSdk(string root) => new("test-DATA", Path.Combine(root, "sdk"), "test-DATA", root, Path.Combine(root, "dotnet"));

    private static string QuoteArgument(string value) => "\"" + value + "\"";
    private static ManifestInput Input(string role, string logical) => new(role, logical, "inputs/" + Hash(logical) + ".bin", 1, Hash(logical), "utf-8", false);
    [Theory]
    [InlineData(false, "global")]
    [InlineData(true, "global")]
    [InlineData(false, "GLOBAL")]
    [InlineData(true, "GLOBAL")]
    [InlineData(false, "GlObAl")]
    [InlineData(true, "GlObAl")]
    public void SDK_per_alias_switches_join_exact_reference_and_remain_unsupported_DATA(bool reverse, string globalKeyword)
    {
        using var fixture = TestDirectory.Create("crap-recipe-multi-alias-data");
        var observed = SupportedObservation(fixture.Path);
        var reference = observed.Parameters.Single(input => input.Name == "References").Items.Single();
        var parameters = observed.Parameters.Select(input => input.Name == "References" ? input with
        { ReferenceProperties = [new(reference, "Foo," + globalKeyword, false)] } : input).ToArray();
        string[] aliases = reverse ? ["/reference:" + reference, "/reference:Foo=" + reference] : ["/reference:Foo=" + reference, "/reference:" + reference];
        observed = RebindArguments(observed with { Parameters = parameters }, observed.CommandLineArguments.Take(6).Concat(aliases).Concat(observed.CommandLineArguments.Skip(7)).ToArray());
        Assert.Equal(new[] { "recipe.emitProfileUnsupported" }, CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken).UnsupportedReasons);
        var retained = CompilerRecipePayload.Retain(observed, fixture.Path, TestContext.Current.CancellationToken);
        Assert.Empty(retained.KnownArguments);
        Assert.DoesNotContain(reference, Encoding.UTF8.GetString(CompilerRecipePayload.Unsupported(observed, "context", "build",
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken).AsSpan()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GLOBAL")]
    [InlineData("GlObAl")]
    public void Task_global_keyword_joins_only_the_actual_unprefixed_command(string globalKeyword)
    {
        using var fixture = TestDirectory.Create("crap-recipe-global-keyword-data");
        var observed = SupportedObservation(fixture.Path);
        var reference = observed.Parameters.Single(input => input.Name == "References").Items.Single();
        var parameters = observed.Parameters.Select(input => input.Name == "References" ? input with
        { ReferenceProperties = [new(reference, globalKeyword, false)] } : input).ToArray();
        observed = observed with { Parameters = parameters };
        Assert.True(CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken).IsSupported);
        Assert.Null(Assert.Single(CompilerReferenceSnapshot.ResolveSemanticReferences(observed, TestContext.Current.CancellationToken)).Aliases);
        // The primary task emits no alias prefix for this metadata. Do not
        // reinterpret a foreign explicit argv alias by folding its spelling.
        var arguments = observed.CommandLineArguments.ToArray();
        Assert.Equal("/reference:" + reference, arguments[6]);
        arguments[6] = "/reference:" + globalKeyword + "=" + reference;
        var foreign = RebindArguments(observed, arguments);
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(foreign, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign-alias")]
    [InlineData("foreign-path")]
    [InlineData("embed-flag")]
    public void Unsupported_multi_alias_profile_still_refuses_reference_tamper(string change)
    {
        using var fixture = TestDirectory.Create("crap-recipe-multi-alias-integrity-data");
        var observed = SupportedObservation(fixture.Path);
        var reference = observed.Parameters.Single(input => input.Name == "References").Items.Single();
        var parameters = observed.Parameters.Select(input => input.Name == "References" ? input with
        { ReferenceProperties = [new(reference, "Foo,global", false)] } : input).ToArray();
        var aliases = new List<string> { "/reference:Foo=" + reference, "/reference:" + reference };
        if (change == "missing") aliases.RemoveAt(1);
        if (change == "duplicate") aliases.Insert(1, aliases[0]);
        if (change == "foreign-alias") aliases[0] = "/reference:Bar=" + reference;
        if (change == "foreign-path") aliases[1] = "/reference:" + Path.Combine(fixture.Path, "foreign.dll");
        if (change == "embed-flag") aliases[1] = "/link:" + reference;
        observed = RebindArguments(observed with { Parameters = parameters }, observed.CommandLineArguments.Take(6).Concat(aliases).Concat(observed.CommandLineArguments.Skip(7)).ToArray());
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_custom_generated_output_is_unsupported_but_foreign_argument_is_refused(bool foreign)
    {
        using var fixture = TestDirectory.Create("crap-recipe-generated-output-data");
        var observed = SupportedObservation(fixture.Path);
        var output = Path.Combine(fixture.Path, "obj", "out.dll"); var generated = Path.Combine(fixture.Path, "Generated");
        observed = observed with
        {
            Assembly = observed.Assembly! with { Path = output },
            Pdb = observed.Pdb! with { Path = Path.ChangeExtension(output, ".pdb") },
            EmitParameters = [new("GeneratedFilesOutputPath", [generated])]
        };
        var arguments = observed.CommandLineArguments.ToArray(); arguments[5] = "/out:" + output;
        observed = RebindArguments(observed, arguments.Append("/generatedfilesout:" + (foreign ? generated + "-foreign" : generated)).ToArray());
        if (foreign) Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken)).Reason);
        else Assert.Equal(new[] { "recipe.emitProfileUnsupported" }, CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken).UnsupportedReasons);
    }

    [Fact]
    public void Missing_generated_output_argument_cannot_be_hidden_by_the_unsupported_directory_profile()
    {
        using var fixture = TestDirectory.Create("crap-recipe-generated-missing-data");
        var observed = SupportedObservation(fixture.Path);
        var generated = Path.GetFullPath(Path.Combine(fixture.Path, "..", "unsupported-generated"));
        observed = observed with { EmitParameters = [new("GeneratedFilesOutputPath", [generated])] };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() =>
            CompilerRecipePayload.Classify(observed, fixture.Path, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void Unsupported_prior_keeps_public_bytes_after_SDK_drift_but_supported_or_malformed_prior_is_refused()
    {
        using var fixture = TestDirectory.Create("crap-recipe-unsupported-prior-drift-data");
        var observed = SupportedObservation(fixture.Path); var sdk = SelectedSdk(fixture.Path);
        var images = CompilerEvidence.CompilerImagePaths(sdk).Select(image =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(image.Path)!);
            var content = Encoding.UTF8.GetBytes("actual role bytes " + image.Role); File.WriteAllBytes(image.Path, content);
            return new CompilerImageFile(image.Role, image.Path, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        }).ToArray();
        observed = observed with
        {
            CompilerImages = images,
            InvocationRecipe = observed.InvocationRecipe! with
            { Images = images.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).ToArray() }
        };
        var supported = CompilerRecipePayload.Retain(observed, fixture.Path, TestContext.Current.CancellationToken);
        var arguments = observed.CommandLineArguments.ToArray(); arguments[2] = "/target:exe";
        var unsupported = RebindArguments(observed, arguments);
        var retained = CompilerRecipePayload.Retain(unsupported, fixture.Path, TestContext.Current.CancellationToken);
        var prior = observed with { InvocationRecipe = null, CompilerImages = [], CommandLineArguments = [], EmitParameters = [] };
        var path = images.Single(image => image.Role == "compiler-deps").Path;
        var stamp = File.GetLastWriteTimeUtc(path); var before = File.ReadAllBytes(path);
        File.WriteAllBytes(path, before.Select(value => (byte)(value ^ 1)).ToArray()); File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(before.Length, new FileInfo(path).Length); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        var restored = FreshPriorBuild.RestoreCurrentRecipe(retained, prior, fixture.Path, sdk, TestContext.Current.CancellationToken);
        Assert.Equal(CompilerRecipePayload.Unsupported(unsupported, "context", "build", CompilerRecipePayload.Classify(unsupported, fixture.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            CompilerRecipePayload.Unsupported(restored, "context", "build", CompilerRecipePayload.Classify(restored, fixture.Path, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal("check.priorBuildInvalid", Assert.Throws<PolicyException>(() => FreshPriorBuild.RestoreCurrentRecipe(supported, prior, fixture.Path, sdk, TestContext.Current.CancellationToken)).Code);
        var malformed = retained with { Observation = retained.Observation with { Images = retained.Observation.Images.Select((image, index) => index == 0 ? image with { Role = "foreign" } : image).ToArray() } };
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() => FreshPriorBuild.RestoreCurrentRecipe(malformed, prior, fixture.Path, sdk, TestContext.Current.CancellationToken)).Reason);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FreshPriorBuild.RestoreCurrentRecipe(retained, prior, fixture.Path, sdk, cancelled.Token));
    }

    [Fact]
    public void Fresh_SDK_image_drift_is_DATA_but_saved_image_verification_and_phase_failures_stay_strict()
    {
        using var fixture = TestDirectory.Create("crap-recipe-fresh-image-drift-data");
        var observed = SupportedObservation(fixture.Path);
        var context = ProjectAnalysisContext.Create(observed.ProjectPath, "app", "net10.0", "Release", "AnyCPU",
            Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, [], Microsoft.CodeAnalysis.SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn"));
        ImmutableArray<byte> ChangedImage(string path) => ImmutableArray.Create((byte)42);
        var captured = CompilerRecipePayload.CaptureFreshFiles(observed, context, "build", ChangedImage, TestContext.Current.CancellationToken);
        Assert.True(captured.CompilerImagesChanged); Assert.Empty(captured.Files);
        var changed = CompilerRecipePayload.ImagesChangedUnsupported(observed, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "recipe.compilerImagesChanged" }, CompilerRecipePayload.Classify(changed, fixture.Path, TestContext.Current.CancellationToken).UnsupportedReasons);
        Assert.Empty(changed.CompilerImages);
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() => CompilerRecipePayload.CaptureFiles(observed, context, "build", ChangedImage, TestContext.Current.CancellationToken)).Reason);
        var phase = Path.Combine(fixture.Path, "phase.txt");
        var withPhase = observed with { Parameters = observed.Parameters.Append(new CompilerParameter("AdditionalFiles", [phase])).ToArray() };
        var readCount = 0;
        ImmutableArray<byte> NoRead(string path) { readCount++; return ChangedImage(path); }
        Assert.Equal("compiler.recipeBindingUnproven", Assert.Throws<ProjectContextException>(() => CompilerRecipePayload.CaptureFreshFiles(withPhase, context, "build", NoRead, TestContext.Current.CancellationToken)).Reason);
        Assert.Equal(0, readCount);
        var withContextPhase = context with { PhaseInputs = [new(phase, "additionalFile", false)] };
        Assert.Throws<IOException>(() => CompilerRecipePayload.CaptureFreshFiles(withPhase, withContextPhase, "build", path => throw new IOException("actual DATA phase read failure"), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CompilerRecipePayload.CaptureFreshFiles(observed, context, "build", ChangedImage, cancelled.Token));
    }

    private static (CompilerRecipeObservation Capture, string Prefix) Capture(string root, string key = "1:2:3:4:5")
    {
        var sdk = Path.Combine(root, "sdk"); var host = Path.Combine(root, "dotnet");
        return (new(sdk, host, key, Path.Combine(sdk, "Roslyn", "Microsoft.CSharp.Core.targets")),
            "\"" + host + "\" exec \"" + Path.Combine(sdk, "Roslyn", "bincore", "csc.dll") + "\"");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
