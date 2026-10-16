using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class NativeSdkGraphTests
{
    [Theory]
    [InlineData("ClientSecret")]
    [InlineData("SigningKey")]
    [InlineData("StorageSasToken")]
    [InlineData("GitHubToken")]
    [InlineData("StorageAccountKey")]
    [InlineData("KeyPassphrase")]
    [InlineData("Bearer")]
    [InlineData("DbPwd")]
    [InlineData("AdoPat")]
    public async Task CredentialMappedDependencyRefusesBeforeDesignTimeHooks(string credentialProperty)
    {
        using var fixture = TestDirectory.Create("crap4csharp-sensitive-sdk-graph");
        fixture.Write("global.json", """{"sdk":{"version":"10.0.103","rollForward":"disable"}}""");
        const string hook = "<Target Name=\"Observe\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' == 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/hooks.txt\" Lines=\"unexpected\" Overwrite=\"false\" /></Target>";
        fixture.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + hook + "</Project>");
        fixture.Write("Dep/Code.cs", "public class Dep { }");
        var project = fixture.Write("Root/Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Dep/Dep.csproj\" AdditionalProperties=\"" + credentialProperty + "=private-hook-canary\" /></ItemGroup>" + hook + "</Project>");
        fixture.Write("Root/Code.cs", "public class Root { }");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(project, "Debug", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2))
            { NativeSdkIdentityProbe = true };
        var restored = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restored.ExitCode == 0, restored.StandardOutput + restored.StandardError);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.False(loaded.Success);
        Assert.Equal("context.sensitiveGlobalProperty", loaded.FailureReason);
        Assert.DoesNotContain("private-hook-canary", string.Join(" | ", loaded.Diagnostics));
        Assert.Empty(Directory.GetFiles(fixture.Path, "hooks.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CurrentNativeSdkChecksPreserveMappedGraphContextsAndDesignTimeHooks()
    {
        using var fixture = TestDirectory.Create("crap4csharp-native-sdk-graph");
        fixture.Write("global.json", """{"sdk":{"version":"10.0.103","rollForward":"disable"}}""");
        const string hook = """
            <Target Name="ObserveDesignTime" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/hooks.txt" Lines="once" Overwrite="false" />
            </Target>
            """;
        fixture.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants>$(DefineConstants);FLAVOR_$(Flavor)</DefineConstants></PropertyGroup>" + hook + "</Project>");
        fixture.Write("Dep/Code.cs", "public class Dep { public int Value => 42; }");
        var project = fixture.Write("Root/Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Dep/Dep.csproj\" AdditionalProperties=\"Configuration=Debug;Flavor=LEFT\" /></ItemGroup>" + hook + "</Project>");
        fixture.Write("Root/Code.cs", "public class Root { public int Value => 42; }");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        var restore = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var ordinary = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(ordinary.Success, string.Join(" | ", ordinary.Diagnostics));
        var hooks = new[] { "Dep", "Root" }.ToDictionary(name => name,
            name => File.ReadAllLines(Path.Combine(fixture.Path, name, "hooks.txt")).Length);
        var timing = new CompletedProofTiming();
        var native = await ProjectContextLoader.LoadGraphAsync(request with
            { NativeSdkIdentityProbe = true, CollectTiming = true, Timing = timing }, token);
        Assert.True(native.Success, native.FailureReason + ": " + string.Join(" | ", native.Diagnostics));
        Assert.Equal(JsonSerializer.Serialize(ordinary.BuildGraph), JsonSerializer.Serialize(native.BuildGraph));
        Assert.Equal(JsonSerializer.Serialize(ordinary.GraphContexts, ProjectContextLoader.JsonOptions),
            JsonSerializer.Serialize(native.GraphContexts, ProjectContextLoader.JsonOptions));
        Assert.Contains(native.GraphContexts, binding => binding.Context.Configuration == "Debug" &&
            binding.Context.PreprocessorSymbols.Contains("FLAVOR_LEFT"));
        Assert.Equal(3, timing.Spans.Count(span => span.Phase.EndsWith("worker.projectSdkNative", StringComparison.Ordinal)));
        foreach (var name in hooks.Keys)
            Assert.Equal(hooks[name] * 2, File.ReadAllLines(Path.Combine(fixture.Path, name, "hooks.txt")).Length);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedUnavailableSdkRefusesBeforeAnyDesignTimeHooksInBothModes(bool nativeProbe)
    {
        using var fixture = TestDirectory.Create("crap4csharp-native-sdk-nested");
        fixture.Write("global.json", """{"sdk":{"version":"10.0.103","rollForward":"disable"}}""");
        const string hook = "<Target Name=\"Observe\" BeforeTargets=\"CoreCompile\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/hooks.txt\" Lines=\"unexpected\" /></Target>";
        fixture.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + hook + "</Project>");
        fixture.Write("Dep/Code.cs", "public class Dep { }");
        var project = fixture.Write("Root/Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Dep/Dep.csproj\" /></ItemGroup>" + hook + "</Project>");
        fixture.Write("Root/Code.cs", "public class Root { }");
        fixture.Write("Dep/global.json", """{"sdk":{"version":"99.0.999","rollForward":"disable"}}""");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2))
            { NativeSdkIdentityProbe = nativeProbe };
        var result = await ProjectContextLoader.DiscoverGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("context.sdkMismatch", result.FailureReason);
        Assert.Empty(Directory.GetFiles(fixture.Path, "hooks.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
    }
}
