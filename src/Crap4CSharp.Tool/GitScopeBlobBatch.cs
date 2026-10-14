using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Crap4CSharp.Core;

internal static partial class GitScopeResolver
{
    private const int GitProtocolBytes = 16777216;
    private sealed record GitBlobInfo(string Identity, long Size);
    private sealed record GitBlobBytes(string Identity, byte[] Bytes);

    // The baseline is an already resolved immutable tree. No worktree/index stat
    // information, filters, or prior invocation cache participates in this read.
    private static async Task<IReadOnlyDictionary<string, GitBlobInfo>> ReadBlobInventory(
        string root, string baseline, TimeSpan timeout, CancellationToken token)
    {
        var entries = SplitNull((await Git(root, timeout, token,
            ["ls-tree", "-r", "-l", "-z", "--full-tree", baseline])).Output);
        var inventory = new Dictionary<string, GitBlobInfo>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var separator = entry.IndexOf('\t');
            if (separator < 0) throw BatchProtocolFailure();
            var fields = entry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4) throw BatchProtocolFailure();
            // Gitlinks and trees retain their existing ordinary capture/refusal.
            if (fields[1] != "blob") continue;
            if (!ValidObjectIdentity(fields[2]) ||
                !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0 ||
                !inventory.TryAdd(entry[(separator + 1)..], new(fields[2], size)))
                throw BatchProtocolFailure();
        }
        return inventory;
    }

    private static async Task<IReadOnlyDictionary<string, GitBlobBytes>> ReadBlobBatch(string root,
        IReadOnlyList<PathChange> records, IReadOnlyDictionary<string, GitBlobInfo> inventory,
        TimeSpan timeout, CancellationToken token)
    {
        var result = new Dictionary<string, GitBlobBytes>(StringComparer.Ordinal);
        var pending = new List<KeyValuePair<string, GitBlobInfo>>();
        long bytes = 0;
        foreach (var path in records.Where(record => record.Kind != ScopeChangeKind.Added)
            .Select(record => record.OldPath).Where(path => path is not null).Cast<string>().Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!inventory.TryGetValue(path, out var info)) continue;
            if (info.Size > GitProtocolBytes) continue;
            // Include framing in the bound. A single near-limit blob uses the
            // original transport, preserving its exact per-file size allowance.
            var framedBytes = info.Size + Encoding.ASCII.GetByteCount(Header(info)) + 1;
            if (framedBytes > GitProtocolBytes) continue;
            if (bytes + framedBytes > GitProtocolBytes)
            {
                await ReadPending();
                pending.Clear();
                bytes = 0;
            }
            pending.Add(new(path, info));
            bytes += framedBytes;
        }
        if (pending.Count > 0) await ReadPending();
        return result;

        async Task ReadPending()
        {
            // Only full hexadecimal OIDs go on stdin: paths containing spaces,
            // tabs, colons, CR or LF cannot alter framing or select another blob.
            var input = Encoding.ASCII.GetBytes(string.Concat(pending.Select(pair => pair.Value.Identity + "\n")));
            var output = (await Git(root, timeout, token, ["cat-file", "--batch"], input)).Output;
            var offset = 0;
            foreach (var pair in pending)
            {
                token.ThrowIfCancellationRequested();
                var header = Encoding.ASCII.GetBytes(Header(pair.Value));
                if (output.Length - offset < header.Length || !output.AsSpan(offset, header.Length).SequenceEqual(header))
                    throw BatchProtocolFailure();
                offset += header.Length;
                var size = checked((int)pair.Value.Size);
                if (output.Length - offset <= size || output[offset + size] != (byte)'\n')
                    throw BatchProtocolFailure();
                result.Add(pair.Key, new(pair.Value.Identity, output.AsSpan(offset, size).ToArray()));
                offset += size + 1;
            }
            if (offset != output.Length) throw BatchProtocolFailure();
        }
    }

    private static string Header(GitBlobInfo info) =>
        info.Identity + " blob " + info.Size.ToString(CultureInfo.InvariantCulture) + "\n";

    private static async Task<string> RawWorktreeBlobIdentity(string path, int identityLength, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(identityLength == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + length.ToString(CultureInfo.InvariantCulture) + "\0"));
        var buffer = new byte[65536];
        long observed = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            hash.AppendData(buffer.AsSpan(0, count));
            observed += count;
        }
        if (observed != length || stream.Length != length)
            throw new ScopeException("scope.changedDuringCapture", "A filtered worktree input changed while its raw identity was captured.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool ValidObjectIdentity(string identity) =>
        identity.Length is 40 or 64 && identity.All(Uri.IsHexDigit);

    private static ScopeException BatchProtocolFailure() =>
        new("scope.malformedDiff", "Git blob inventory or complete batch framing was invalid.");
}
