using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class MappedGlobalPrivacyTests
{
    [Theory]
    [InlineData("Password")]
    [InlineData("Feed_Client_Secret")]
    [InlineData("MyConnectionString")]
    [InlineData("API-Key")]
    [InlineData("RegistryAccessToken")]
    [InlineData("SigningPrivateKey")]
    [InlineData("Token")]
    [InlineData("Pwd")]
    [InlineData("SigningKey")]
    [InlineData("NuGetKey")]
    [InlineData("StorageSasToken")]
    [InlineData("SAS")]
    [InlineData("PAT")]
    [InlineData("PfxPass")]
    [InlineData("SessionCookie")]
    [InlineData("AuthCookie")]
    [InlineData("Cookie")]
    [InlineData("Signature")]
    [InlineData("AuthToken")]
    [InlineData("GitHubToken")]
    [InlineData("NpmToken")]
    [InlineData("ApiToken")]
    [InlineData("ServiceToken")]
    [InlineData("StorageAccountKey")]
    [InlineData("AccountKey")]
    [InlineData("Passphrase")]
    [InlineData("KeyPassphrase")]
    [InlineData("Bearer")]
    [InlineData("GitHub-Token")]
    [InlineData("AUTH_TOKEN")]
    [InlineData("PrivatePublicKeyToken")]
    public void CredentialNamedPropertiesRefuseWithoutDisclosingTheirNameOrValue(string property)
    {
        const string value = "private-credential-canary";
        var error = Assert.Throws<ProjectContextException>(() => MappedGlobalPrivacy.Validate(
            new Dictionary<string, string> { [property] = value }));
        Assert.Equal("context.sensitiveGlobalProperty", error.Reason);
        Assert.DoesNotContain(property, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, error.Message);
    }

    [Fact]
    public void CompilerAndOrdinaryMappedPropertiesRemainSupported()
        => MappedGlobalPrivacy.Validate(new Dictionary<string, string>
        {
            ["Configuration"] = "Debug", ["Platform"] = "AnyCPU", ["TargetFramework"] = "net10.0",
            ["Flavor"] = "LEFT", ["MetadataToken"] = "42", ["PublicKeyToken"] = "public",
            ["Compatibility"] = "v1", ["Compat"] = "true", ["AssemblyOriginatorKeyFile"] = "public.snk",
            ["KeyboardLayout"] = "US", ["PassThrough"] = "true"
        });
}
