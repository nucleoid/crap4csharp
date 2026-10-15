using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Crap4CSharp.Core;

internal static partial class GitScopeResolver
{
    // Scope precision only: XML/JSON/configuration and response-file line separators
    // are equivalent after CRLF -> LF. Compiler inputs still bind their exact bytes.
    // Source files and arbitrary clean/smudge transformations never use this path.
    private static async Task<string?> ConfigurationLineEndingIdentity(string path, int identityLength, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > GitProtocolBytes) return null; // Preserve conservative raw identity for large inputs.
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, token);
        if (stream.ReadByte() != -1 || stream.Length != length)
            throw new ScopeException("scope.changedDuringCapture", "A configuration input changed during capture.");
        if (bytes.AsSpan().Contains((byte)0)) return null; // No newline guessing for binary/UTF-16 data.
        var written = 0;
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            if ((offset & 65535) == 0) token.ThrowIfCancellationRequested();
            if (bytes[offset] == 13 && offset + 1 < bytes.Length && bytes[offset + 1] == 10) continue;
            bytes[written++] = bytes[offset];
        }
        if (written == bytes.Length) return null;
        using var hash = IncrementalHash.CreateHash(identityLength == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + written.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(bytes.AsSpan(0, written));
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
