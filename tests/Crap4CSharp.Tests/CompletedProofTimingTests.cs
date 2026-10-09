using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompletedProofTimingTests
{
    [Fact]
    public void DisabledWorkerDiagnosticsLeavePrivateAndPublicTransportUnchanged()
    {
        var request = new ProjectContextLoadRequest("Target.csproj", "Debug", "AnyCPU", [], false, false, TimeSpan.FromSeconds(30));
        var result = new ProjectContextLoadResult(true, [], []);
        Assert.DoesNotContain("collectTiming", JsonSerializer.Serialize(request, ProjectContextLoader.JsonOptions));
        Assert.DoesNotContain("workerTimings", JsonSerializer.Serialize(result, ProjectContextLoader.JsonOptions));
        var observed = request with { CollectTiming = true, Timing = new CompletedProofTiming(), TimingPrefix = "private-prefix" };
        var transport = JsonSerializer.Serialize(observed, ProjectContextLoader.JsonOptions);
        Assert.True(JsonSerializer.Deserialize<ProjectContextLoadRequest>(transport, ProjectContextLoader.JsonOptions)!.CollectTiming);
        Assert.DoesNotContain("private-prefix", transport);
        Assert.DoesNotContain("Timing", JsonSerializer.Serialize(observed));
        Assert.Null(JsonSerializer.Deserialize<ProjectContextLoadRequest>(transport, ProjectContextLoader.JsonOptions)!.Timing);
    }

    [Fact]
    public void NumericWorkerObservationsRoundTripOnlyInPrivateTransport()
    {
        var result = new ProjectContextLoadResult(true, [], [])
        { WorkerTimings = [new("worker.compilation", 12.5), new("worker.projectSdk", 2.5)] };
        var transport = JsonSerializer.Serialize(result, ProjectContextLoader.JsonOptions);
        var copy = JsonSerializer.Deserialize<ProjectContextLoadResult>(transport, ProjectContextLoader.JsonOptions)!;
        Assert.Equal(result.WorkerTimings, copy.WorkerTimings);
        Assert.DoesNotContain("WorkerTimings", JsonSerializer.Serialize(result));
        Assert.True(copy.Success);
    }

    [Fact]
    public void UntrustedWorkerDiagnosticsAreBoundedFilteredAndCannotSupplyPathsOrNonfiniteNumbers()
    {
        var timing = new CompletedProofTiming();
        timing.ImportWorker("owned", [new("worker.workspace", 1), new("/private/path", 1),
            new("worker.graph", double.NaN), new("worker.graph", double.PositiveInfinity), new("worker.graph", -1)]);
        Assert.Equal(new CompletedProofTiming.Span("owned.worker.workspace", 1), Assert.Single(timing.Spans));
        timing.ImportWorker("owned", Enumerable.Repeat(new CompletedProofTiming.Span("worker.graph", 0), 5000).ToArray());
        Assert.Equal(4097, timing.Spans.Count);
        timing.ImportWorker("owned", [new("worker.projectSdk", 2.5),
            new("worker.projectSdk", double.NaN), new("worker.projectSdk", double.PositiveInfinity),
            new("worker.projectSdk", -1), new("worker.projectSdk/private-path", 1)]);
        Assert.Equal(4098, timing.Spans.Count);
        Assert.Equal(new CompletedProofTiming.Span("owned.worker.projectSdk", 2.5), timing.Spans[^1]);
        foreach (var phase in new[] { "worker.authoredBefore", "worker.authoredAfter", "worker.trackedInputs", "worker.generatorDiscovery", "worker.generatorValidation" })
        {
            var before = timing.Spans.Count;
            timing.ImportWorker("owned", [new(phase, 1), new(phase, double.NaN), new(phase, double.PositiveInfinity), new(phase, -1), new(phase + "/private-path", 1)]);
            Assert.Equal(before + 1, timing.Spans.Count);
            Assert.Equal(new CompletedProofTiming.Span("owned." + phase, 1), timing.Spans[^1]);
        }
    }
}
