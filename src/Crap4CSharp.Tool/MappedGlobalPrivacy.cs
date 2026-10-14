namespace Crap4CSharp.Core;

internal static class MappedGlobalPrivacy
{
    private static readonly string[] SensitiveSuffixes =
    ["PASSWORD", "PASSWD", "CLIENTSECRET", "SECRET", "CREDENTIAL", "CREDENTIALS", "CONNECTIONSTRING",
     "APIKEY", "ACCESSTOKEN", "AUTHORIZATIONTOKEN", "BEARERTOKEN", "REFRESHTOKEN", "PRIVATEKEY", "ACCESSKEY",
     "SIGNINGKEY", "NUGETKEY", "SASTOKEN", "PFXPASS", "AUTHCOOKIE", "SESSIONCOOKIE",
     "PERSONALACCESSTOKEN", "PERSONALACCESSTOKENVALUE"];

    internal static void Validate(IEnumerable<KeyValuePair<string, string>> properties)
    {
        foreach (var (name, _) in properties)
        {
            var key = string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
            if (key is "TOKEN" or "PWD" or "PAT" or "SAS" or "COOKIE" or "COOKIES" or "SIGNATURE" ||
                SensitiveSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal)))
                throw new ProjectContextException("context.sensitiveGlobalProperty",
                    "Credential-bearing mapped global properties cannot be included in exported execution identities. Use the consumer's approved private credential mechanism.");
        }
    }
}
