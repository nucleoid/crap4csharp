namespace Crap4CSharp.Tests;

// Diagnostics outside a fixture require an explicit absolute destination. Each
// test gets a unique directory; normal runs keep all output in owned fixtures.
internal static class TestValidationOutput
{
    internal const string EnvironmentVariable = "CRAP4CSHARP_VALIDATION_OUT";

    internal static string CreateDirectory(string name, string ownedFixture)
        => CreateDirectory(name, ownedFixture, Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static string CreateDirectory(string name, string ownedFixture, string? configuredRoot)
    {
        if (name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A validation directory name must be a single path component.", nameof(name));
        var root = string.IsNullOrWhiteSpace(configuredRoot) ? ownedFixture : configuredRoot;
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException(EnvironmentVariable + " must be an absolute directory.");
        var directory = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void WriteOptionalProfile(string name, string json)
    {
        var root = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root)) return;
        var directory = CreateDirectory(name, root, root);
        File.WriteAllText(Path.Combine(directory, "profile.json"), json);
    }
}
