using System.Text.Json;

namespace Crap4CSharp.Core;

// Optional recipe DATA has independent bounds. It never spends the mandatory
// compiler-input counters or authorizes compiler/image execution.
internal sealed class CompilerRecipeBudget
{
    internal const int MaximumCharacters = 2 * 1024 * 1024;
    internal const int MaximumItems = 32768;
    private int characters, items;
    internal bool Exceeded { get; private set; }
    internal bool TryCharge(int length)
    {
        if (length < 0 || Exceeded || items >= MaximumItems || length > MaximumCharacters - characters)
        { Exceeded = true; return false; }
        items++; characters += length; return true;
    }

    // Exact serialized byte admission, including JSON escaping, rather than a
    // per-source heuristic which rejects otherwise supported large projects.
    internal static bool TrySerialize(object value, JsonSerializerOptions options, int maximum, out byte[] bytes)
    {
        if (maximum is <= 0 or > CompilerEvidence.MaximumTransportBytes) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var stream = new BoundedJsonStream(maximum);
        try { JsonSerializer.Serialize(stream, value, options); bytes = stream.ToArray(); return true; }
        catch (OptionalSizeExceeded) { bytes = []; return false; }
    }
    private sealed class OptionalSizeExceeded : Exception { }
    private sealed class BoundedJsonStream(int maximum) : MemoryStream
    {
        private void Require(int count) { if (count > maximum - Length) throw new OptionalSizeExceeded(); }
        public override void Write(byte[] buffer, int offset, int count) { Require(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Require(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Require(1); base.WriteByte(value); }
    }
}
