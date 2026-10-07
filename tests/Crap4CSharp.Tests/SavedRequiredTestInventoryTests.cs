using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SavedRequiredTestInventoryTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("reordered")]
    [InlineData("duplicate")]
    [InlineData("provider")]
    [InlineData("reusable")]
    [InlineData("unknown-root")]
    [InlineData("unknown-test")]
    [InlineData("missing-field")]
    [InlineData("null")]
    [InlineData("wrong-type")]
    [InlineData("failed")]
    [InlineData("zero-tests")]
    [InlineData("trx-alias")]
    [InlineData("project-traversal")]
    [InlineData("module-duplicate")]
    [InlineData("module-alias")]
    [InlineData("unknown-module")]
    [InlineData("empty-identity")]
    [InlineData("duplicate-json")]
    [InlineData("oversized")]
    [InlineData("invalid-json")]
    [InlineData("cancelled")]
    public void StrictSavedInventoryBindsExactRequiredOrderWithoutClaimingCompletedReuse(string change)
    {
        var root = JsonNode.Parse("""
        {"provider":"finite-sdk-required-test-inventory-v1","tests":[
          {"graphId":"a","project":"A/A.csproj","trx":"required-test-a-trx","assembly":"required-test-a-pe","pdb":"required-test-a-pdb","child":"required-test-a-child","moduleIdentity":"A","mvid":"mvid-a","debugIdentity":"debug-a","passed":1,"rawExitCode":0,"modules":[{"graphId":"production","artifact":"required-test-a-coverage-production"}]},
          {"graphId":"b","project":"B/B.csproj","trx":"required-test-b-trx","assembly":"required-test-b-pe","pdb":"required-test-b-pdb","child":"required-test-b-child","moduleIdentity":"B","mvid":"mvid-b","debugIdentity":"debug-b","passed":2,"rawExitCode":0,"modules":[]}],"reusableRecipe":false}
        """)!.AsObject();
        var tests = root["tests"]!.AsArray(); var first = tests[0]!.AsObject();
        switch (change)
        {
            case "missing": tests.RemoveAt(1); break;
            case "extra": tests.Add(tests[1]!.DeepClone()); break;
            case "reordered": var item = tests[0]; tests.RemoveAt(0); tests.Add(item); break;
            case "duplicate": tests[1]!["graphId"] = "a"; break;
            case "provider": root["provider"] = "other"; break;
            case "reusable": root["reusableRecipe"] = true; break;
            case "unknown-root": root["command"] = "do-not-execute"; break;
            case "unknown-test": first["command"] = "do-not-execute"; break;
            case "missing-field": first.Remove("child"); break;
            case "null": first["modules"] = null; break;
            case "wrong-type": first["passed"] = "1"; break;
            case "failed": first["rawExitCode"] = 1; break;
            case "zero-tests": first["passed"] = 0; break;
            case "trx-alias": first["trx"] = "required-test-b-trx"; break;
            case "project-traversal": first["project"] = "../A.csproj"; break;
            case "module-duplicate": first["modules"]!.AsArray().Add(first["modules"]![0]!.DeepClone()); break;
            case "module-alias": first["modules"]![0]!["artifact"] = "required-test-b-coverage-production"; break;
            case "unknown-module": first["modules"]![0]!["command"] = "do-not-execute"; break;
            case "empty-identity": first["mvid"] = ""; break;
        }
        var json = root.ToJsonString();
        if (change == "duplicate-json") json = json.Replace("\"reusableRecipe\":false", "\"reusableRecipe\":false,\"reusableRecipe\":false", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (change == "oversized") bytes = new byte[256 * 1024 + 1];
        if (change == "invalid-json") bytes = [0];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (change == "cancelled") cancellation.Cancel();
        object? Parse()
        {
            var type = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.SavedRequiredTestInventory");
            Assert.True(type is not null, "Missing strict saved required-test inventory parser; capture declarations alone cannot enable current reuse.");
            var method = type.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static); Assert.NotNull(method);
            try { return method.Invoke(null, [bytes, new string[] { "a", "b" }, cancellation.Token]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        if (change == "same") Assert.NotNull(Parse());
        else if (change == "cancelled") Assert.Throws<OperationCanceledException>(() => Parse());
        else Assert.Equal("provenance.savedRequiredInventoryUnproven", Assert.Throws<PolicyException>(() => Parse()).Code);
    }
}
