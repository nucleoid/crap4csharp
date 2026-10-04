using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Crap4CSharp.Core;

internal sealed record ArtifactBundle(RunManifest Manifest,
    IReadOnlyDictionary<string, ImmutableArray<byte>> Bytes, string ManifestPath,
    string BundleRoot, IReadOnlyList<string> ResolvedInputs)
{
    internal const int MaxManifestBytes = 4 * 1024 * 1024;
    internal const int MaxArtifactBytes = 100 * 1024 * 1024;
    internal const long MaxBundleBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 10_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    public static ArtifactBundle Load(string manifestPath, string workingDirectory)
    {
        string fullManifest;
        try { fullManifest = Path.GetFullPath(manifestPath, workingDirectory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("Artifact manifest locator is invalid.", exception); }

        if (!File.Exists(fullManifest))
            throw new InvalidDataException($"Artifact manifest not found: {fullManifest}");
        var root = Path.GetDirectoryName(fullManifest)
            ?? throw new InvalidDataException("Artifact manifest has no bundle root.");
        if (new FileInfo(fullManifest).LinkTarget is not null)
            throw new InvalidDataException("Artifact manifest cannot be a symbolic link.");
        var manifestBytes = ReadBounded(fullManifest, MaxManifestBytes, null, "manifest.json");
        RunManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            ValidateShape(document.RootElement);
            manifest = document.RootElement.Deserialize<RunManifest>(Json)
                ?? throw new InvalidDataException("Artifact manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Artifact manifest contains malformed JSON.", exception);
        }

        var locators = manifest.Artifacts.Select(item => item.Locator)
            .Concat(manifest.Contexts.SelectMany(context => context.Inputs.Select(input => input.Locator)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (locators.Length > MaxEntries) throw new InvalidDataException("Artifact bundle contains too many entries.");
        var declarations = manifest.Artifacts.Select(item => (item.Locator, item.Length))
            .Concat(manifest.Contexts.SelectMany(context => context.Inputs.Select(input => (input.Locator, input.Length))))
            .GroupBy(item => item.Locator, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                var lengths = group.Select(item => item.Length).Distinct().ToArray();
                if (lengths.Length != 1 || lengths[0] < 0 || lengths[0] > MaxArtifactBytes)
                    throw new InvalidDataException($"Artifact has inconsistent or invalid declared length: {group.Key}");
                return lengths[0];
            }, StringComparer.Ordinal);
        var bytes = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var resolved = new List<string> { fullManifest };
        long total = manifestBytes.Length;
        foreach (var locator in locators)
        {
            string normalized;
            try { normalized = CanonicalIdentity.NormalizeLogicalPath(locator); }
            catch (ArgumentException exception)
            { throw new InvalidDataException($"Artifact locator is invalid: {locator}", exception); }
            var path = ResolveOwnedRegularFile(root, normalized);
            if (path is null) continue;
            var declared = declarations[locator];
            if (declared > MaxBundleBytes - total)
                throw new InvalidDataException("Artifact bundle exceeds the 512 MB limit.");
            var value = ReadBounded(path, MaxArtifactBytes, declared, locator);
            total += value.Length;
            bytes.Add(locator, ImmutableArray.Create(value));
            resolved.Add(path);
        }
        return new ArtifactBundle(manifest, bytes, fullManifest, root,
            resolved.Order(StringComparer.Ordinal).ToArray());
    }

    public void RejectOutputAlias(string output, string workingDirectory)
    {
        var candidate = Path.GetFullPath(output, workingDirectory);
        if (RelatedPath(BundleRoot, candidate) || ResolvedInputs.Any(path => RelatedPath(path, candidate)))
            throw new InvalidDataException("Output path aliases the artifact bundle or one of its inputs.");
    }

    public static void RejectOutputAliasForLocator(string manifestPath, string output, string workingDirectory)
    {
        var manifest = Path.GetFullPath(manifestPath, workingDirectory);
        var root = Path.GetDirectoryName(manifest)
            ?? throw new InvalidDataException("Artifact manifest has no bundle root.");
        var candidate = Path.GetFullPath(output, workingDirectory);
        if (RelatedPath(root, candidate) || RelatedPath(manifest, candidate))
            throw new InvalidDataException("Output path aliases the artifact bundle.");
    }

    private static void ValidateShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Artifact manifest must be an object.");
        Require(root, "manifestSchemaVersion", "identityAlgorithm", "manifestHash");
        foreach (var property in new[] { "producer", "capture", "revision", "evaluationInputs" })
            if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Artifact manifest is missing required object '{property}'.");
        Require(root.GetProperty("producer"), "tool", "toolVersion", "complexityRuleset", "contextProtocol",
            "coverageProtocol", "pathProtocol");
        Require(root.GetProperty("capture"), "state", "captureConsistent", "actualBindingComplete", "failureReasons");
        Require(root.GetProperty("revision"), "kind", "repositoryIdentity", "workspaceIdentity");
        Require(root.GetProperty("evaluationInputs"), "scopeHash", "policyHash");
        foreach (var property in new[] { "roots", "contexts", "builds", "executions", "artifacts" })
        {
            if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Artifact manifest is missing required array '{property}'.");
            if (value.GetArrayLength() > MaxEntries)
                throw new InvalidDataException($"Artifact manifest array '{property}' exceeds the entry limit.");
        }
        if (root.GetProperty("contexts").GetArrayLength() == 0)
            throw new InvalidDataException("Artifact manifest contains no analysis context.");
        foreach (var manifestRoot in root.GetProperty("roots").EnumerateArray())
        {
            Require(manifestRoot, "id", "logicalName", "casePolicy");
            if (manifestRoot.GetProperty("id").ValueKind != JsonValueKind.String ||
                manifestRoot.GetProperty("logicalName").ValueKind != JsonValueKind.String ||
                manifestRoot.GetProperty("casePolicy").ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Artifact manifest root has invalid required member types.");
        }
        foreach (var context in root.GetProperty("contexts").EnumerateArray())
        {
            Require(context, "id", "project", "targetFramework", "configuration", "platform", "sourceSetHash",
                "contextHash", "inputClosureHash", "actualCompilerBindingComplete", "reuseRecipeComplete", "inputs",
                "parseOptions", "pathPolicy");
            if (context.GetProperty("inputs").ValueKind != JsonValueKind.Array ||
                context.GetProperty("parseOptions").ValueKind != JsonValueKind.Object ||
                context.GetProperty("pathPolicy").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Artifact context has an invalid required member shape.");
            Require(context.GetProperty("parseOptions"), "languageVersion", "sourceKind", "preprocessorSymbols", "features");
            Require(context.GetProperty("pathPolicy"), "casePolicy", "reportRootMappings");
            var symbols = context.GetProperty("parseOptions").GetProperty("preprocessorSymbols");
            var features = context.GetProperty("parseOptions").GetProperty("features");
            var mappings = context.GetProperty("pathPolicy").GetProperty("reportRootMappings");
            if (symbols.ValueKind != JsonValueKind.Array || symbols.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String) ||
                features.ValueKind != JsonValueKind.Object || features.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String) ||
                mappings.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Artifact context has invalid parse-option or path-policy members.");
            foreach (var mapping in mappings.EnumerateArray())
            {
                Require(mapping, "reportRoot", "logicalRoot");
                if (mapping.GetProperty("reportRoot").ValueKind != JsonValueKind.String ||
                    mapping.GetProperty("logicalRoot").ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Artifact path mapping must contain string roots.");
            }
            foreach (var input in context.GetProperty("inputs").EnumerateArray())
            {
                Require(input, "role", "logicalPath", "locator", "length", "sha256", "generated");
                if (input.GetProperty("logicalPath").ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Artifact input logicalPath must be a string.");
                try { CanonicalIdentity.NormalizeLogicalPath(input.GetProperty("logicalPath").GetString()!); }
                catch (ArgumentException exception)
                { throw new InvalidDataException("Artifact input logicalPath is not canonical.", exception); }
            }
        }
        foreach (var build in root.GetProperty("builds").EnumerateArray())
            Require(build, "id", "contextId", "moduleIdentity", "assemblySha256", "mvid", "pdbSha256", "debugIdentity");
        foreach (var execution in root.GetProperty("executions").EnumerateArray())
            Require(execution, "id", "contextId", "buildId", "completed", "exitCode", "totalTests", "passedTests",
                "failedTests", "skippedTests", "testModuleIdentity", "testAssemblySha256", "testMvid",
                "testPdbSha256", "testDebugIdentity");
        foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
            Require(artifact, "id", "kind", "locator", "length", "sha256");
    }

    private static void Require(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Artifact manifest contains a malformed object.");
        foreach (var name in names)
            if (!value.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidDataException($"Artifact manifest is missing required property '{name}'.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Artifact manifest contains duplicate property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static string? ResolveOwnedRegularFile(string root, string locator)
    {
        var current = root;
        var parts = locator.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            EnsureContained(root, current);
            if (OperatingSystem.IsLinux())
            {
                var kind = LinuxFileKind(current);
                if (kind == UnixFileKind.Missing) return null;
                if (index < parts.Length - 1 && kind != UnixFileKind.Directory)
                    throw new InvalidDataException($"Artifact path component is not a directory: {locator}");
                if (index == parts.Length - 1 && kind != UnixFileKind.Regular)
                    throw new InvalidDataException($"Artifact locator is not a regular file: {locator}");
            }
            if (!File.Exists(current) && !Directory.Exists(current)) return null;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Artifact path contains a symbolic link: {locator}");
            if (index < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException($"Artifact path component is not a directory: {locator}");
            if (index == parts.Length - 1 && (attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException($"Artifact locator is not a regular file: {locator}");
        }
        return current;
    }

    private static UnixFileKind LinuxFileKind(string path)
    {
        if (Statx(AtCurrentWorkingDirectory, path, AtSymlinkNoFollow, StatxType, out var status) == 0)
            return (status.Mode & FileTypeMask) switch
            {
                RegularFile => UnixFileKind.Regular,
                DirectoryFile => UnixFileKind.Directory,
                _ => UnixFileKind.Special
            };
        return Marshal.GetLastPInvokeError() == NoSuchFileOrDirectory
            ? UnixFileKind.Missing
            : throw new InvalidDataException($"Unable to inspect artifact file type: {path}");
    }

    private enum UnixFileKind { Missing, Regular, Directory, Special }
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x0001;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFile = 0x8000;
    private const ushort DirectoryFile = 0x4000;
    private const int NoSuchFileOrDirectory = 2;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(28)]
        public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuffer status);

    internal static byte[] ReadBounded(string path, int maximum, long? expectedLength, string label)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        if (!stream.CanSeek) throw new InvalidDataException($"Artifact is not a seekable regular file: {label}");
        if (stream.Length > maximum || expectedLength is long declared && stream.Length != declared)
            throw new InvalidDataException($"Artifact length does not match its declaration: {label}");
        using var output = new MemoryStream((int)Math.Min(stream.Length, maximum));
        var buffer = new byte[64 * 1024];
        long count = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maximum + 1L - count));
            if (read == 0) break;
            count += read;
            if (count > maximum || expectedLength is long expected && count > expected)
                throw new InvalidDataException($"Artifact exceeded its bounded declared length while reading: {label}");
            output.Write(buffer, 0, read);
        }
        if (expectedLength is long exact && count != exact)
            throw new InvalidDataException($"Artifact was truncated while reading: {label}");
        return output.ToArray();
    }

    private static bool RelatedPath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Terminate(string path) => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var physicalLeft = ResolvePhysicalPrefix(left);
        var physicalRight = ResolvePhysicalPrefix(right);
        return string.Equals(physicalLeft, physicalRight, comparison) ||
            Terminate(physicalLeft).StartsWith(Terminate(physicalRight), comparison) ||
            Terminate(physicalRight).StartsWith(Terminate(physicalLeft), comparison);
    }

    private static string ResolvePhysicalPrefix(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new InvalidDataException("Path has no root.");
        var current = root;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var candidate = Path.Combine(current, parts[index]);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return Path.GetFullPath(parts.Skip(index).Aggregate(current, Path.Combine));
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            current = info.ResolveLinkTarget(true)?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative)) throw new InvalidDataException("Artifact locator escapes the bundle root.");
    }
}
