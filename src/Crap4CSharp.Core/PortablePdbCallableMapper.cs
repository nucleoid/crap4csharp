using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Crap4CSharp.Core;

public sealed record PortablePdbBinding(
    string ContextId,
    Guid ExpectedMvid,
    string ExpectedPortablePdbId,
    IReadOnlyDictionary<string, ImmutableArray<byte>> SourceDocuments);

public sealed record PortablePdbMappingResult(
    string Status,
    string? Reason,
    string? EvidenceKind,
    int? KickoffMethodToken,
    int? GeneratedMethodToken,
    Guid? Mvid,
    string? PortablePdbId);

public static class PortablePdbCallableMapper
{
    private const int MaximumArtifactBytes = 128 * 1024 * 1024;
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    public static PortablePdbMappingResult Map(CallableEntry callable, ImmutableArray<byte> peBytes,
        ImmutableArray<byte> pdbBytes, PortablePdbBinding binding)
    {
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(binding);
        if (callable.Kind is CallableKind.LocalFunction or CallableKind.Lambda or CallableKind.AnonymousMethod or CallableKind.TopLevel)
            return Unsupported(CoverageReasonCodes.UnsupportedGeneratedMapping);
        if (callable.SemanticIdentity is null || callable.ContextId != binding.ContextId)
            return Unsupported(CoverageReasonCodes.ContextMismatch);
        if (peBytes.IsDefaultOrEmpty) return Unsupported("coverage.peUnavailable");
        if (pdbBytes.IsDefaultOrEmpty) return Unsupported(CoverageReasonCodes.PdbUnavailable);
        if (peBytes.Length > MaximumArtifactBytes || pdbBytes.Length > MaximumArtifactBytes)
            return Unsupported("coverage.artifactTooLarge");

        try
        {
            using var peStream = new MemoryStream(peBytes.ToArray(), writable: false);
            using var pe = new PEReader(peStream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) return Unsupported("coverage.peMalformed");
            var metadata = pe.GetMetadataReader();
            var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            if (mvid != binding.ExpectedMvid) return Unsupported(CoverageReasonCodes.ContextMismatch, mvid: mvid);

            using var pdbStream = new MemoryStream(pdbBytes.ToArray(), writable: false);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen);
            var pdb = provider.GetMetadataReader();
            var pdbIdBytes = pdb.DebugMetadataHeader?.Id.ToArray() ?? [];
            var pdbId = Convert.ToHexString(pdbIdBytes).ToLowerInvariant();
            if (!string.Equals(pdbId, binding.ExpectedPortablePdbId, StringComparison.Ordinal))
                return Unsupported(CoverageReasonCodes.PdbIdentityMismatch, mvid, pdbId);
            if (!PeReferencesPdb(pe, pdbIdBytes))
                return Unsupported(CoverageReasonCodes.PdbIdentityMismatch, mvid, pdbId);
            if (!ValidateSourceDocument(callable.Path, pdb, binding.SourceDocuments))
                return Unsupported(CoverageReasonCodes.SourceChecksumMismatch, mvid, pdbId);

            var kickoff = FindMethod(metadata, callable.SemanticIdentity);
            if (kickoff.IsNil) return Unsupported("coverage.kickoffMethodNotFound", mvid, pdbId);
            var generated = FindGeneratedMethod(pdb, kickoff);
            if (generated.IsNil) return Unsupported(CoverageReasonCodes.UnsupportedGeneratedMapping, mvid, pdbId);
            return new PortablePdbMappingResult("supported", null, "portablePdbStateMachine",
                MetadataTokens.GetToken(kickoff), MetadataTokens.GetToken(generated), mvid, pdbId);
        }
        catch (BadImageFormatException)
        {
            return Unsupported(CoverageReasonCodes.PdbMalformed);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            return Unsupported(CoverageReasonCodes.PdbMalformed);
        }
    }

    private static MethodDefinitionHandle FindMethod(MetadataReader reader, CallableSemanticIdentity expected)
    {
        var signatureProvider = new TypeProvider();
        var matches = new List<MethodDefinitionHandle>();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            if (!TypesEqual(TypeName(reader, typeHandle), expected.TypeName)) continue;
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != expected.MetadataName ||
                    method.GetGenericParameters().Count != expected.GenericArity) continue;
                var decoded = method.DecodeSignature(signatureProvider, (object?)null);
                if (decoded.ParameterTypes.Length != expected.Parameters.Count) continue;
                var actual = decoded.ParameterTypes.Select(NormalizeType).ToArray();
                var wanted = expected.Parameters.Select(parameter => NormalizeType(parameter.Type +
                    (parameter.RefKind == "none" ? string.Empty : "&"))).ToArray();
                if (actual.SequenceEqual(wanted, StringComparer.Ordinal)) matches.Add(methodHandle);
            }
        }
        return matches.Count == 1 ? matches[0] : default;
    }

    private static MethodDefinitionHandle FindGeneratedMethod(MetadataReader pdb, MethodDefinitionHandle kickoff)
    {
        var matches = new List<MethodDefinitionHandle>();
        foreach (var debugHandle in pdb.MethodDebugInformation)
        {
            if (pdb.GetMethodDebugInformation(debugHandle).GetStateMachineKickoffMethod() != kickoff) continue;
            matches.Add(MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(debugHandle)));
        }
        return matches.Count == 1 ? matches[0] : default;
    }

    private static bool PeReferencesPdb(PEReader pe, byte[] pdbId)
    {
        if (pdbId.Length < 16) return false;
        var expectedGuid = new Guid(pdbId.AsSpan(0, 16));
        foreach (var entry in pe.ReadDebugDirectory().Where(item => item.Type == DebugDirectoryEntryType.CodeView))
        {
            var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
            if (codeView.Guid == expectedGuid) return true;
        }
        return false;
    }

    private static bool ValidateSourceDocument(string logicalPath, MetadataReader pdb,
        IReadOnlyDictionary<string, ImmutableArray<byte>> sources)
    {
        var supplied = sources.Where(pair => PathEqual(pair.Key, logicalPath)).ToArray();
        if (supplied.Length != 1) return false;
        var documents = pdb.Documents.Select(handle => pdb.GetDocument(handle)).Where(document =>
            PathEqual(pdb.GetString(document.Name), logicalPath)).ToArray();
        if (documents.Length != 1) return false;
        var document = documents[0];
        var algorithm = pdb.GetGuid(document.HashAlgorithm);
        var actual = algorithm == Sha256 ? SHA256.HashData(supplied[0].Value.AsSpan()) :
            algorithm == Sha1 ? SHA1.HashData(supplied[0].Value.AsSpan()) : [];
        return actual.Length > 0 && actual.AsSpan().SequenceEqual(pdb.GetBlobBytes(document.Hash));
    }

    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        if (definition.IsNested)
            return TypeName(reader, definition.GetDeclaringType()) + "+" + name;
        var ns = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var ns = reader.GetString(reference.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool PathEqual(string left, string right) => string.Equals(
        left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.Ordinal) ||
        string.Equals(Path.GetFileName(left), Path.GetFileName(right), StringComparison.Ordinal);
    private static bool TypesEqual(string left, string right) => left == right || left.Replace('+', '.') == right.Replace('+', '.');
    private static string NormalizeType(string value) => value.Replace("global::", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('?');
    private static PortablePdbMappingResult Unsupported(string reason, Guid? mvid = null, string? pdbId = null) =>
        new("unsupported", reason, null, null, null, mvid, pdbId);

    private sealed class TypeProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.SByte => "System.SByte", PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32", PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64", PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String", PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.Void => "System.Void", PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr", PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => typeCode.ToString()
        };
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            TypeName(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            TypeName(reader, handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
