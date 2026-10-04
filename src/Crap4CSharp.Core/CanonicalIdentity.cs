using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Crap4CSharp.Core;

public static class CanonicalIdentity
{
    public const string Algorithm = "sha256-canonical-v1";

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Content(string role, string logicalIdentity, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalIdentity);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Algorithm);
        Append(hash, "content");
        Append(hash, role);
        Append(hash, NormalizeLogicalPath(logicalIdentity));
        Append(hash, bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string Set(string domain, IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Algorithm);
        Append(hash, domain);
        foreach (var value in values.Order(StringComparer.Ordinal)) Append(hash, value);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string NormalizeLogicalPath(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException($"Logical path is not canonical and relative: {value}");
        return normalized;
    }

    private static void Append(IncrementalHash hash, string value) => Append(hash, Encoding.UTF8.GetBytes(value));
    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
