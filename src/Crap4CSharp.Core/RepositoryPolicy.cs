using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crap4CSharp.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RepositoryPolicyMode>))]
public enum RepositoryPolicyMode { Strict, Incremental }

[JsonConverter(typeof(JsonStringEnumConverter<MissingCoveragePolicy>))]
public enum MissingCoveragePolicy { Fail }

public sealed record PolicyAllowedOverrides(
    bool Frameworks = false,
    bool Scope = false,
    bool IncludeTests = false,
    bool IncludeGenerated = false,
    bool CoveragePathMappings = false);

public sealed record RepositoryPolicy(
    string SchemaVersion,
    RepositoryPolicyMode Mode,
    IReadOnlyList<string> ProductionProjects,
    IReadOnlyList<string> TestProjects,
    string Configuration,
    IReadOnlyList<string> TargetFrameworks,
    string Scope,
    double Threshold,
    MissingCoveragePolicy MissingCoverage,
    IReadOnlyList<string> RequiredChecks,
    IReadOnlyList<string> Exclusions,
    string Ruleset,
    string? BaselinePath,
    IReadOnlyList<string> ExemptionFiles,
    PolicyAllowedOverrides AllowedOverrides)
{
    public const string Version = "repository-policy-v1";
}

public sealed record ParsedRepositoryPolicy(RepositoryPolicy Policy, string Hash, byte[] CanonicalBytes,
    string PolicyPath);

public sealed class PolicyException(string code, string message) : InvalidDataException(message)
{
    public string Code { get; } = code;
}

public static class RepositoryPolicyParser
{
    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "mode", "productionProjects", "testProjects", "configuration", "targetFrameworks",
        "scope", "threshold", "missingCoverage", "requiredChecks", "exclusions", "ruleset", "baseline",
        "exemptionFiles", "allowedOverrides"
    };
    private static readonly HashSet<string> OverrideKeys = new(StringComparer.Ordinal)
    { "frameworks", "scope", "includeTests", "includeGenerated", "coveragePathMappings" };
    private static readonly HashSet<string> Checks = new(["tests", "coverage", "crap"], StringComparer.Ordinal);
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ParsedRepositoryPolicy Parse(ReadOnlySpan<byte> bytes, string policyPath)
    {
        if (bytes.Length is 0 or > 1024 * 1024) throw Error("policy.sizeInvalid", "Policy must be between 1 byte and 1 MB.");
        string normalizedPolicyPath;
        try { normalizedPolicyPath = CanonicalIdentity.NormalizeLogicalPath(policyPath); }
        catch (ArgumentException exception) { throw Error("policy.pathInvalid", exception.Message); }
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var root = document.RootElement;
            Object(root, RootKeys, "policy");
            Require(root, "schemaVersion", "mode", "productionProjects", "testProjects", "configuration",
                "targetFrameworks", "scope", "threshold", "missingCoverage", "requiredChecks", "exclusions", "ruleset");
            if (Text(root, "schemaVersion") != RepositoryPolicy.Version)
                throw Error("policy.schemaUnsupported", "Unsupported repository policy schema version.");
            var mode = Text(root, "mode") switch
            {
                "strict" => RepositoryPolicyMode.Strict,
                "incremental" => RepositoryPolicyMode.Incremental,
                _ => throw Error("policy.modeInvalid", "Policy mode must be strict or incremental.")
            };
            var threshold = Number(root, "threshold");
            if (!double.IsFinite(threshold) || threshold < 0)
                throw Error("policy.thresholdInvalid", "Policy threshold must be finite and non-negative.");
            if (Text(root, "missingCoverage") != "fail")
                throw Error("policy.missingCoverageInvalid", "missingCoverage must be fail; broad missing-coverage waivers are unsupported.");
            var ruleset = Text(root, "ruleset");
            if (ruleset != ComplexityRules.CallablesV1)
                throw Error("policy.rulesetUnsupported", "Reviewed repository policy requires callables-v1.");
            var checks = Strings(root, "requiredChecks", false);
            if (checks.Count == 0 || checks.Any(value => !Checks.Contains(value)))
                throw Error("policy.requiredCheckUnsupported", "requiredChecks may contain only tests, coverage, and crap.");
            var configuration = Text(root, "configuration");
            if (string.IsNullOrWhiteSpace(configuration)) throw Error("policy.configurationInvalid", "Configuration is required.");
            var scope = Text(root, "scope");
            if (scope is not ("all" or "worktree" or "base"))
                throw Error("policy.scopeInvalid", "Policy scope must be all, worktree, or base.");

            var production = Paths(Strings(root, "productionProjects", false), false);
            var tests = Paths(Strings(root, "testProjects", false), false);
            var frameworks = Strings(root, "targetFrameworks", false);
            if (production.Count == 0 || tests.Count == 0 || frameworks.Count == 0)
                throw Error("policy.targetsMissing", "At least one production project, test project, and target framework is required.");
            var exclusions = Paths(Strings(root, "exclusions", true), false);
            var directory = normalizedPolicyPath.Contains('/')
                ? normalizedPolicyPath[..normalizedPolicyPath.LastIndexOf('/')]
                : string.Empty;
            var baseline = OptionalText(root, "baseline") is { } baselineValue
                ? RelativeToPolicy(directory, baselineValue)
                : null;
            if (mode == RepositoryPolicyMode.Incremental && baseline is null)
                throw Error("policy.baselineRequired", "Incremental policy requires a baseline.");
            var exemptions = OptionalStrings(root, "exemptionFiles").Select(path => RelativeToPolicy(directory, path)).ToArray();
            var overrides = ParseOverrides(root);
            var policy = new RepositoryPolicy(RepositoryPolicy.Version, mode, production, tests, configuration,
                frameworks, scope, threshold, MissingCoveragePolicy.Fail, checks, exclusions, ruleset, baseline,
                exemptions, overrides);
            var canonical = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(policy, CanonicalJson) + "\n");
            return new ParsedRepositoryPolicy(policy, CanonicalIdentity.Sha256(canonical), canonical, normalizedPolicyPath);
        }
        catch (JsonException exception)
        {
            throw Error("policy.malformed", $"Policy JSON is malformed: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw Error("policy.malformed", $"Policy JSON has an invalid value: {exception.Message}");
        }
    }

    private static PolicyAllowedOverrides ParseOverrides(JsonElement root)
    {
        if (!root.TryGetProperty("allowedOverrides", out var value)) return new();
        Object(value, OverrideKeys, "allowedOverrides");
        bool Flag(string name) => value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        foreach (var property in value.EnumerateObject())
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Error("policy.overrideInvalid", $"Allowed override '{property.Name}' must be boolean.");
        return new(Flag("frameworks"), Flag("scope"), Flag("includeTests"), Flag("includeGenerated"),
            Flag("coveragePathMappings"));
    }

    private static IReadOnlyList<string> Paths(IReadOnlyList<string> values, bool allowEmpty) =>
        Unique(values.Select(Path), allowEmpty, "policy.pathDuplicate");

    private static string Path(string value)
    {
        if (value.Contains('\\') || value.Split('/').Any(segment => segment is "" or "." or "..") ||
            System.IO.Path.IsPathRooted(value) || value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
            throw Error("policy.pathInvalid", $"Policy path must be bounded repository-relative: {value}");
        try { return CanonicalIdentity.NormalizeLogicalPath(value); }
        catch (ArgumentException exception) { throw Error("policy.pathInvalid", exception.Message); }
    }

    private static string RelativeToPolicy(string directory, string value)
    {
        var relative = Path(value);
        return string.IsNullOrEmpty(directory) ? relative : CanonicalIdentity.NormalizeLogicalPath(directory + "/" + relative);
    }

    private static IReadOnlyList<string> Strings(JsonElement root, string name, bool allowEmpty)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            throw Error("policy.malformed", $"'{name}' must be an array of nonempty strings.");
        return Unique(value.EnumerateArray().Select(item => item.GetString()!), allowEmpty, "policy.duplicateValue");
    }

    private static IReadOnlyList<string> OptionalStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out _) ? Strings(root, name, true) : [];

    private static IReadOnlyList<string> Unique(IEnumerable<string> values, bool allowEmpty, string code)
    {
        var result = values.Order(StringComparer.Ordinal).ToArray();
        if (!allowEmpty && result.Length == 0) throw Error("policy.valueMissing", "Required policy list is empty.");
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) throw Error(code, "Policy list contains duplicates.");
        return result;
    }

    private static void Object(JsonElement value, HashSet<string> keys, string label)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Error("policy.malformed", $"{label} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Error("policy.duplicateKey", $"Duplicate policy key: {property.Name}");
            if (!keys.Contains(property.Name)) throw Error("policy.unknownKey", $"Unknown policy key: {property.Name}");
        }
    }

    private static void Require(JsonElement root, params string[] names)
    {
        foreach (var name in names) if (!root.TryGetProperty(name, out _))
            throw Error("policy.missingKey", $"Missing policy key: {name}");
    }

    private static string Text(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(root.GetProperty(name).GetString())
            ? root.GetProperty(name).GetString()!
            : throw Error("policy.malformed", $"'{name}' must be a nonempty string.");

    private static string? OptionalText(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : throw Error("policy.malformed", $"'{name}' must be a nonempty string.")
        : null;

    private static double Number(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.Number
        ? root.GetProperty(name).GetDouble()
        : throw Error("policy.malformed", $"'{name}' must be a number.");

    private static PolicyException Error(string code, string message) => new(code, message);
}
