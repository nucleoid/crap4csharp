namespace Crap4CSharp.Core;

// Shared MSBuild argv transport for actual compiler and test invocations. Raw
// values remain private; escaping is MSBuild property escaping, not shell quoting.
internal static class MappedGlobalArguments
{
    internal static IReadOnlyList<string> Create(IReadOnlyDictionary<string,string> properties)
    {
        var result = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, raw) in properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) || !names.Add(key) || raw.Contains('\0'))
                throw new ProjectContextException("compiler.globalPropertyInvalid", "A mapped global property cannot be transported.");
            var value = raw;
            foreach (var character in new[] { '%', ';', ',', '$', '@', '\'', '(', ')', '*', '?' })
                value = value.Replace(character.ToString(), "%" + ((int)character).ToString("X2"), StringComparison.Ordinal);
            result.Add($"-p:{key}={value}");
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
