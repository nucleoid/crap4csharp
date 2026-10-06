using System.Text;

namespace Crap4CSharp.Core;

internal static class ProcessTextCapture
{
    internal static async Task<string> ReadAsync(TextReader reader, string streamName)
    {
        const int limit = ProcessRunner.MaximumCapturedCharactersPerStream;
        var captured = new StringBuilder();
        char[] buffer = new char[4096];
        long observed = 0;
        bool exceeded = false;
        while (true)
        {
            // The owner bounds EOF/drain with its operation deadline and terminates
            // descendants before cleanup. Continue draining after overflow so neither
            // concurrent pipe blocks, without retaining any further protocol content.
            int count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            observed = checked(observed + count);
            if (observed > limit) exceeded = true;
            if (!exceeded) captured.Append(buffer, 0, count);
        }
        if (exceeded)
        {
            var failure = new InvalidDataException($"Process {streamName} exceeded the complete text capture limit of {limit} characters ({observed} observed). Use bounded streaming logs for large command output.");
            failure.Data["stream"] = streamName;
            failure.Data["limitCharacters"] = limit;
            failure.Data["observedCharacters"] = observed;
            throw failure;
        }
        return captured.ToString();
    }
}
