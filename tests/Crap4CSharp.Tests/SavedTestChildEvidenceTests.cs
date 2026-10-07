using System.Reflection;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SavedTestChildEvidenceTests
{
    // Structural saved-projection controls, not proof of an actual test execution.
    [Theory]
    [InlineData("none")]
    [InlineData("provider")]
    [InlineData("graph")]
    [InlineData("compiler")]
    [InlineData("vstest")]
    [InlineData("failed")]
    [InlineData("exit")]
    [InlineData("hash")]
    [InlineData("uppercase-hash")]
    [InlineData("unsupported")]
    [InlineData("runtime")]
    [InlineData("reusable")]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("oversize")]
    public void SavedChildProjectionRequiresExactSuccessfulIdentityAndSupportedShape(string mutation)
    {
        var method = typeof(TestChildEvidenceExport).GetMethod("ValidateSaved", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var value = new JsonObject
        {
            ["provider"] = "selected-sdk-msbuild-child-v1", ["graphId"] = "required-test",
            ["compilerTasks"] = 0, ["vstestTasks"] = 1, ["buildSucceeded"] = true,
            ["rawExitCode"] = 0, ["privateLogSha256"] = new string('a', 64),
            ["supportedTaskAssociations"] = true, ["runtimeLoadingAttested"] = false, ["reusableRecipe"] = false
        };
        switch (mutation)
        {
            case "provider": value["provider"] = "unproven"; break;
            case "graph": value["graphId"] = "other-test"; break;
            case "compiler": value["compilerTasks"] = 1; break;
            case "vstest": value["vstestTasks"] = 2; break;
            case "failed": value["buildSucceeded"] = false; break;
            case "exit": value["rawExitCode"] = 1; break;
            case "hash": value["privateLogSha256"] = new string('z', 64); break;
            case "uppercase-hash": value["privateLogSha256"] = new string('A', 64); break;
            case "unsupported": value["supportedTaskAssociations"] = false; break;
            case "runtime": value["runtimeLoadingAttested"] = true; break;
            case "reusable": value["reusableRecipe"] = true; break;
            case "extra": value["command"] = "do not execute captured commands"; break;
            case "missing": value.Remove("compilerTasks"); break;
        }
        var json = value.ToJsonString();
        if (mutation == "duplicate") json = json.Replace("\"compilerTasks\":0", "\"compilerTasks\":1,\"compilerTasks\":0", StringComparison.Ordinal);
        if (mutation == "null") json = "null";
        if (mutation == "oversize") json += new string(' ', 65537);
        var bytes = ImmutableArray.Create(Encoding.UTF8.GetBytes(json));
        if (mutation == "none") method.Invoke(null, [bytes, "required-test", TestContext.Current.CancellationToken]);
        else
        {
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null,
                [bytes, "required-test", TestContext.Current.CancellationToken]));
            Assert.Equal("provenance.savedTestChildUnproven", Assert.IsType<PolicyException>(error.InnerException).Code);
        }
    }
}
