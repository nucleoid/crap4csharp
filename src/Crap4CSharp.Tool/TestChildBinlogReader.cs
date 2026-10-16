using System.Security.Cryptography;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace Crap4CSharp.Core;

internal static class TestChildBinlogReader
{
    internal static TestChildObservation Read(TestChildReadRequest request, CancellationToken token)
    {
        using var stream = File.OpenRead(request.Binlog);
        if (stream.Length > CompilerEvidence.MaximumBinlogBytes) throw Refuse("test.childEvidenceLimit");
        var time = File.GetLastWriteTimeUtc(request.Binlog);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); stream.Position = 0;
        var events = new BinaryLogReplayEventSource();
        var expectedSdk = Path.GetFullPath(request.SdkPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expectedVSTest = Path.Combine(request.SdkPath, "Microsoft.TestPlatform.targets");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var compilers = 0; var vstests = 0; var eventCount = 0; var tasks = 0;
        var unsupported = 0; string? unsupportedDetail = null;
        void Unsupported(TaskStartedEventArgs value)
        {
            unsupported++;
            var detail = value.TaskName + "; invocation source " + value.TaskFile;
            unsupportedDetail ??= detail[..Math.Min(detail.Length, 2048)];
        }
        var finished = false; var succeeded = false;
        var active = new HashSet<string>(StringComparer.Ordinal);
        static string Key(BuildEventContext? value) => value is null ? "null" :
            $"{value.SubmissionId}:{value.NodeId}:{value.ProjectContextId}:{value.TargetId}:{value.TaskId}";
        events.BuildFinished += (_, value) => { if (finished) throw Refuse("test.childAssociationUnproven"); finished = true; succeeded = value.Succeeded; };
        events.TaskStarted += (_, value) =>
        {
            token.ThrowIfCancellationRequested();
            if (++tasks > 32768 || !active.Add(Key(value.BuildEventContext))) throw Refuse("test.childAssociationUnproven");
            if (value.TaskName is "Csc" or "Vbc") { compilers++; return; }
            if (value.TaskName == "Exec") { Unsupported(value); return; }
            if (value.TaskName is "VSTestTask" or "VSTestTask2")
            {
                if (string.IsNullOrWhiteSpace(value.ProjectFile) || string.IsNullOrWhiteSpace(value.TaskFile) ||
                    !Path.GetFullPath(value.ProjectFile).Equals(request.ProjectPath, comparison) ||
                    !Path.GetFullPath(value.TaskFile).Equals(expectedVSTest, comparison)) throw Refuse("test.childAssociationUnproven");
                vstests++;
                return;
            }
            // Authored instrumentation supports only these ordinary bounded-task seams.
            // Exec and unknown/custom origins are explicitly unsupported, not assumed
            // to preserve the scope of observed compiler tasks.
            if (string.IsNullOrWhiteSpace(value.TaskFile) || (!Path.GetFullPath(value.TaskFile).StartsWith(expectedSdk, comparison) &&
                value.TaskName is not ("Message" or "Warning" or "Error" or "WriteLinesToFile" or "Touch" or "MSBuild" or "CallTarget")))
                Unsupported(value);
        };
        events.TaskFinished += (_, value) => { if (!active.Remove(Key(value.BuildEventContext))) throw Refuse("test.childAssociationUnproven"); };
        events.AnyEventRaised += (_, _) => { token.ThrowIfCancellationRequested(); if (++eventCount > 200000) throw Refuse("test.childEvidenceLimit"); };
        events.Replay(stream, token);
        if (!finished || active.Count != 0 || vstests != 1 || File.GetLastWriteTimeUtc(request.Binlog) != time) throw Refuse("test.childAssociationUnproven");
        // Replay owns/closes its input stream. Reopen the exact owned path and compare
        // bytes, rather than trusting timestamps or attempting to seek a disposed stream.
        using var finalStream = File.OpenRead(request.Binlog);
        if (finalStream.Length > CompilerEvidence.MaximumBinlogBytes ||
            Convert.ToHexString(SHA256.HashData(finalStream)).ToLowerInvariant() != hash) throw Refuse("test.childLogUnproven");
        token.ThrowIfCancellationRequested();
        return new(request.ProjectPath, compilers, vstests, succeeded, hash)
            { UnsupportedTasks = unsupported, UnsupportedDetail = unsupportedDetail };
    }
    private static ProjectContextException Refuse(string reason) => new(reason, "Private selected-SDK test-child event association refused.");
}
