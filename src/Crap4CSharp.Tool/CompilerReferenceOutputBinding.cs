using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Crap4CSharp.Core;

// Private dependency-reference gate. Does not assert actual event-time bytes or recipe
// completeness. Missing reference-assembly emission is conservatively unsupported.
internal static class CompilerReferenceOutputBinding
{
    internal static void Validate(ProjectAnalysisContext context, CompilerObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var project = Canonical(context.ResolvedProjectPath);
            if (!comparer.Equals(project, Canonical(observation.ProjectPath))) throw Refuse();
            var reference = observation.ReferenceAssembly ?? throw Refuse();
            var implementation = observation.Assembly ?? throw Refuse();
            var referencePath = Canonical(reference.Path);
            var implementationPath = Canonical(implementation.Path);
            var target = Canonical(context.EvaluatedReferenceAssemblyPath);
            var intermediate = Canonical(context.EvaluatedIntermediateDirectory);
            var parameters = observation.Parameters.Where(p => p.Name == "OutputRefAssembly").ToArray();
            if (parameters.Length != 1 || parameters[0].Items.Count != 1 || string.IsNullOrWhiteSpace(parameters[0].Items[0]) ||
                !comparer.Equals(Path.GetFullPath(parameters[0].Items[0], Path.GetDirectoryName(project)!), referencePath) ||
                comparer.Equals(referencePath, target) || comparer.Equals(referencePath, implementationPath) ||
                comparer.Equals(target, implementationPath) || !Contained(intermediate, referencePath) || !Contained(intermediate, target)) throw Refuse();
            long total = 0;
            var bytes = CompilerOutputBinding.Read(referencePath, ref total, token);
            CompilerOutputBinding.Match(reference, bytes);
            var copied = CompilerOutputBinding.Read(target, ref total, token);
            if (!bytes.AsSpan().SequenceEqual(copied.AsSpan())) throw Refuse();
            var implementationBytes = CompilerOutputBinding.Read(implementationPath, ref total, token);
            CompilerOutputBinding.Match(implementation, implementationBytes);
            using var referencePe = new PEReader(bytes);
            using var implementationPe = new PEReader(implementationBytes);
            var referenceReader = referencePe.GetMetadataReader();
            var implementationReader = implementationPe.GetMetadataReader();
            if (!referenceReader.IsAssembly || !implementationReader.IsAssembly ||
                !IsReference(referenceReader) || IsReference(implementationReader) ||
                Identity(referenceReader) != Identity(implementationReader) ||
                referenceReader.GetString(referenceReader.GetAssemblyDefinition().Name) != context.AssemblyName) throw Refuse();
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ProjectContextException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or BadImageFormatException)
        { throw Refuse(); }
    }

    private static (string Name, Version Version, string Culture, string PublicKey, System.Reflection.AssemblyFlags Flags) Identity(MetadataReader reader)
    {
        var definition = reader.GetAssemblyDefinition();
        return (reader.GetString(definition.Name), definition.Version, reader.GetString(definition.Culture),
            Convert.ToHexString(reader.GetBlobBytes(definition.PublicKey)), definition.Flags);
    }

    private static bool IsReference(MetadataReader reader)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            if (constructor.Kind != HandleKind.MemberReference) continue;
            var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
            if (reader.GetString(member.Name) != ".ctor" || member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) == "System.Runtime.CompilerServices" &&
                reader.GetString(type.Name) == "ReferenceAssemblyAttribute") return true;
        }
        return false;
    }

    private static string Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw Refuse();
        return path;
    }
    private static bool Contained(string directory, string path) => path.StartsWith(
        Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static ProjectContextException Refuse() => new("compiler.referenceOutputUnproven", "Actual Csc reference output, independent reference PE identity and evaluated copy are not bound.");
}
