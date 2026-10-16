using Crap4CSharp.Core;

// A successful promoted preparation is deliberately not a ResultDocument. Only
// its writer proof may create completed/reusable provenance and serialized JSON.
internal sealed class FreshCheckPreparation
{
    private readonly Func<Func<ResultDocument, SerializedFreshTerminal>, Task<SerializedFreshTerminal>>? completeAtWriter;
    private int consumed;

    private FreshCheckPreparation(ResultDocument? immediate, int intendedExitCode,
        Func<Func<ResultDocument, SerializedFreshTerminal>, Task<SerializedFreshTerminal>>? completeAtWriter)
    {
        ImmediateResult = immediate;
        IntendedExitCode = intendedExitCode;
        this.completeAtWriter = completeAtWriter;
    }

    internal ResultDocument? ImmediateResult { get; }
    internal int IntendedExitCode { get; }
    internal bool RequiresWriterProof => completeAtWriter is not null;

    internal static FreshCheckPreparation Immediate(ResultDocument result) => new(result, result.Run.ExitCode, null);

    internal static FreshCheckPreparation Pending(int intendedExitCode,
        Func<Func<ResultDocument, SerializedFreshTerminal>, Task<SerializedFreshTerminal>> completeAtWriter)
    {
        ArgumentNullException.ThrowIfNull(completeAtWriter);
        if (intendedExitCode is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(intendedExitCode));
        return new(null, intendedExitCode, completeAtWriter);
    }

    // The sole production formatter decorates immutable result fields and calls
    // ResultWriter.Serialize synchronously. It must not await or invoke consumer
    // code between the final proof and the guarded commit.
    internal Task<SerializedFreshTerminal> CompleteAtWriterAsync(Func<ResultDocument, SerializedFreshTerminal> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        if (completeAtWriter is null || Interlocked.Exchange(ref consumed, 1) != 0)
            throw new InvalidOperationException("A pending fresh terminal requires one actual writer proof.");
        return completeAtWriter(prepare);
    }
}

internal sealed record SerializedFreshTerminal(ResultDocument Result, string Json);
