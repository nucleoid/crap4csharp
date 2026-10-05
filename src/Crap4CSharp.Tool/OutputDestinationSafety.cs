using System.Text.Json;
using Crap4CSharp.Core;

internal static class OutputDestinationSafety
{
    public static void RejectExistingConsumerFile(string destination, string workingDirectory, bool baseline = false)
    {
        var path = Path.GetFullPath(destination, workingDirectory);
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            for (var component = path; component is not null; component = Path.GetDirectoryName(component))
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Output traverses a symbolic link.");
            if (Directory.Exists(path)) throw new InvalidDataException("Output is a directory.");
            var bytes = ArtifactBundle.ReadBounded(path, 16 * 1024 * 1024, null, "existing output");
            if (baseline)
            {
                _ = BaselineDocument.Parse(bytes);
                return;
            }
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemaVersion", out var schema) && schema.GetString() == ResultContract.SchemaVersion &&
                root.TryGetProperty("evaluation", out var evaluation) && evaluation.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("run", out var run) && run.ValueKind == JsonValueKind.Object)
                return;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or
                                          UnauthorizedAccessException or BaselineException or InvalidOperationException) { }
        throw new PolicyException("output.aliasesPolicyInput",
            "Existing output is not a prior result document; consumer files cannot be overwritten.");
    }
}
