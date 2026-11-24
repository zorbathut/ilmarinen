using Docker.DotNet.Models;
using Ilmarinen.Docker;
using Ilmarinen.Protocol.Metrics;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Worker.Metrics;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Core.Tests.Metrics;

/// <summary>The sampler against fake host and container sources, driven tick by tick: what it records, how it batches, and that nothing a source does stops it.</summary>
[TestFixture]
public class JobMetricsSamplerTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid AssignmentId = Guid.NewGuid();
    private const string WorkerContainer = "worker000000";

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-19T12:00:00Z");

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private sealed class FakeHost : IHostSource
    {
        public Func<HostSnapshot>? Throwing { get; set; }
        private long _ticks;

        public int Reads { get; private set; }

        public HostSnapshot Read(DateTimeOffset now)
        {
            Reads++;
            if (Throwing != null)
            {
                return Throwing();
            }
            _ticks += 100;
            return new HostSnapshot(now, new CpuTicks(_ticks, 0, 0, 0, 0, 0, 0, 0), 1, 0, new Dictionary<string, long> { ["MemAvailable"] = 1000 }, [], new Pressure(0, null), new Pressure(0, 0), new Pressure(0, 0));
        }

        public HostRecord Describe(DateTimeOffset now)
        {
            return new HostRecord { Ts = now, Hostname = "fake", CpuCount = 4 };
        }
    }

    private sealed class FakeContainers : IContainerSource
    {
        public List<ContainerSeen> Present { get; } = [];
        public HashSet<string> FailingStats { get; } = [];
        public bool ListThrows { get; set; }
        public Dictionary<string, ulong> CpuNanos { get; } = new();
        public int Inspections { get; private set; }

        public Task<IReadOnlyList<ContainerSeen>> ListAsync(string? networkName, CancellationToken cancellationToken)
        {
            if (ListThrows)
            {
                throw new InvalidOperationException("daemon down");
            }
            return Task.FromResult<IReadOnlyList<ContainerSeen>>(Present.ToList());
        }

        public Task<ContainerDetails?> InspectAsync(string id, CancellationToken cancellationToken)
        {
            Inspections++;
            return Task.FromResult<ContainerDetails?>(new ContainerDetails("alpine", DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        public Task<ContainerStatsResponse?> StatsAsync(string id, CancellationToken cancellationToken)
        {
            if (FailingStats.Contains(id))
            {
                throw new TimeoutException("slow daemon");
            }
            CpuNanos[id] = CpuNanos.GetValueOrDefault(id) + 1_000_000_000;
            return Task.FromResult<ContainerStatsResponse?>(new ContainerStatsResponse
            {
                Read = DateTime.UtcNow,
                CPUStats = new CPUStats { CPUUsage = new CPUUsage { TotalUsage = CpuNanos[id] } },
                MemoryStats = new MemoryStats { Usage = 4096, Stats = new Dictionary<string, ulong>() },
                PidsStats = new PidsStats { Current = 2 }
            });
        }

        public Task<int> CountCotenantsAsync(string? networkName, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    // NUnit runs every test on one fixture instance, so each test gets fresh fakes here.
    private ManualTime _time = null!;
    private FakeHost _host = null!;
    private FakeContainers _containers = null!;
    private List<JobMetricsChunk> _sent = null!;
    private JobMetricsSampler _sampler = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTime();
        _host = new FakeHost();
        _containers = new FakeContainers();
        _sent = [];
        // An interval long enough that the timer never fires: each test ticks by hand.
        _sampler = new JobMetricsSampler(JobId, AssignmentId, TimeSpan.FromHours(1), _host, _containers, WorkerContainer, chunk =>
        {
            _sent.Add(chunk);
            return Task.CompletedTask;
        }, new LoggerCapture(), _time);
        _sampler.Start();
    }

    private List<JobMetricsChunk> DrainChunks()
    {
        return _sent.OrderBy(c => c.SequenceNumber).ToList();
    }

    private static List<MetricsRecord> RecordsOf(JobMetricsChunk chunk)
    {
        return chunk.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => MetricsRecords.Parse(l)!).ToList();
    }

    private async Task TickAfterAsync(TimeSpan elapsed)
    {
        _time.Now += elapsed;
        await _sampler.TickAsync();
    }

    [Test]
    public async Task Samples_CarryThePhaseAndStep_AndEachContainerIsAnnouncedOnceWithItsRole()
    {
        _containers.Present.Add(new ContainerSeen(WorkerContainer, "worker"));
        await TickAfterAsync(TimeSpan.FromSeconds(2));

        _sampler.AttachPipeline(() => new PipelineProgress("ilmarinen-net", 0, "build", "stepcontainer00"));
        _containers.Present.Add(new ContainerSeen("stepcontainer00full", "/step"));
        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await _sampler.StopAsync();

        var records = DrainChunks().SelectMany(RecordsOf).ToList();
        var samples = records.OfType<SampleRecord>().ToList();
        Assert.That(samples.Select(s => (s.Phase, s.Step)), Is.EqualTo(new (string, string?)[] { ("prepare", null), ("pipeline", "build"), ("pipeline", "build"), ("pipeline", "build") }), "the last is the final sample taken on stop");
        Assert.That(records.OfType<ContainerRecord>().Select(c => (c.Name, c.Role)), Is.EqualTo(new (string?, string)[] { ("worker", "worker"), ("step", "step") }));
        Assert.That(_containers.Inspections, Is.EqualTo(2), "a container is inspected once, when first seen");
        Assert.That(samples.Last().Containers.Select(c => c.Id), Is.EquivalentTo(new[] { WorkerContainer, "stepcontaine" }), "ids shortened to 12 characters, as Docker shows them");
        Assert.That(samples[2].Host!.CpuUser, Is.Not.Null, "host rates once there is an earlier reading");
    }

    [Test]
    public async Task ABatch_IsSentEveryTenSeconds_HeadedByTheHostRecord_InSequence()
    {
        for (var i = 0; i < 12; i++)
        {
            await TickAfterAsync(TimeSpan.FromSeconds(2));
        }

        var chunks = DrainChunks();
        Assert.That(chunks.Select(c => c.SequenceNumber), Is.EqualTo(new[] { 0, 1 }), "10 s and 20 s in, of 24 s");
        Assert.That(chunks.Select(c => RecordsOf(c).First()), Is.All.InstanceOf<HostRecord>(), "every chunk says what host it came from, in case an earlier one was lost");
        Assert.That(chunks.Select(c => c.JobId), Is.All.EqualTo(JobId));
    }

    [Test]
    public async Task ABatch_IsSentEarlyOnceItReachesTheSizeLimit()
    {
        for (var i = 0; i < 80; i++)
        {
            _containers.Present.Add(new ContainerSeen($"container{i:D3}xxxxxxxx", $"c{i}"));
        }

        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await TickAfterAsync(TimeSpan.FromSeconds(2));

        await _sampler.StopAsync();

        var chunks = DrainChunks();
        Assert.That(chunks, Has.Count.GreaterThan(1), "well under ten seconds, but over the size budget");
        Assert.That(chunks.Select(c => System.Text.Encoding.UTF8.GetByteCount(c.Content)), Is.All.LessThanOrEqualTo(JobMetricsSampler.ChunkBudgetBytes), "SignalR closes the connection on a message over 32 KB, and the budget leaves room for escaping");
        Assert.That(chunks.SelectMany(RecordsOf).OfType<SampleRecord>().ToList(), Has.Count.EqualTo(3), "nothing lost to the split");
    }

    [Test]
    public async Task ASampleTooBigForAChunk_LeavesContainersOut_AndSaysHowMany()
    {
        for (var i = 0; i < 400; i++)
        {
            _containers.Present.Add(new ContainerSeen($"container{i:D3}xxxxxxxx", $"c{i}"));
        }

        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await _sampler.StopAsync();

        var chunks = DrainChunks();
        var sample = chunks.SelectMany(RecordsOf).OfType<SampleRecord>().First();
        Assert.That(sample.ContainersOmitted, Is.GreaterThan(0));
        Assert.That(sample.Containers.Count + sample.ContainersOmitted, Is.EqualTo(400));
        Assert.That(chunks.Select(c => System.Text.Encoding.UTF8.GetByteCount(c.Content)), Is.All.LessThanOrEqualTo(JobMetricsSampler.ChunkBudgetBytes));
    }

    [Test]
    public async Task Stopping_TakesALastSampleAndSendsWhatIsLeft()
    {
        await TickAfterAsync(TimeSpan.FromSeconds(2));

        await _sampler.StopAsync();

        var samples = DrainChunks().SelectMany(RecordsOf).OfType<SampleRecord>().ToList();
        Assert.That(samples, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task StepAndPullEvents_AreRecorded_AndAStepsContainerEndingTakesASample()
    {
        _sampler.AttachPipeline(() => new PipelineProgress("ilmarinen-net", 0, "build", null));
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepStarted, Timestamp = _time.Now, StepIndex = 0, StepName = "build", Image = "alpine", ImageFromEarlierStep = false });
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.PullStarted, Timestamp = _time.Now, Image = "alpine" });
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.PullEnded, Timestamp = _time.Now, Image = "alpine" });
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepContainerEnding, Timestamp = _time.Now, StepIndex = 0, StepName = "build", ContainerId = "abc" });
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepEnded, Timestamp = _time.Now, StepIndex = 0, StepName = "build", Status = "Success" });
        await _sampler.StopAsync();

        var kinds = DrainChunks().SelectMany(RecordsOf).Where(r => r is not HostRecord).Select(r => r switch
        {
            StepRecord s => $"step {s.Event}",
            PullRecord p => $"pull {p.Event}",
            _ => r.Kind
        }).ToList();
        Assert.That(kinds, Is.EqualTo(new[] { "step start", "pull start", "pull end", "sample", "step end", "stepSummary", "sample" }));
    }

    // The extra sample falls milliseconds after the last, and host rates over so short an interval would be noise.
    [Test]
    public async Task AStepsContainerEnding_SamplesTheContainersOnly()
    {
        await TickAfterAsync(TimeSpan.FromSeconds(2));
        var reads = _host.Reads;

        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepContainerEnding, Timestamp = _time.Now, StepIndex = 0, StepName = "build", ContainerId = "abc" });

        Assert.That(_host.Reads, Is.EqualTo(reads));
        await _sampler.StopAsync();
        var samples = DrainChunks().SelectMany(RecordsOf).OfType<SampleRecord>().ToList();
        Assert.That(samples[1].Host, Is.Null);
    }

    [Test]
    public async Task AStepsSummary_CountsWhatItsContainersDidDuringIt_NotTheWorker()
    {
        _containers.Present.Add(new ContainerSeen(WorkerContainer, "worker"));
        _containers.Present.Add(new ContainerSeen("carried00000", "service"));
        await TickAfterAsync(TimeSpan.FromSeconds(2));

        _sampler.AttachPipeline(() => new PipelineProgress("ilmarinen-net", 0, "build", "stepcontainer"));
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepStarted, Timestamp = _time.Now, StepIndex = 0, StepName = "build", Image = "alpine", ImageFromEarlierStep = false });
        _containers.Present.Add(new ContainerSeen("stepcontainer", "step"));
        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await _sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepEnded, Timestamp = _time.Now, StepIndex = 0, StepName = "build", Status = "Success" });
        await _sampler.StopAsync();

        var summary = DrainChunks().SelectMany(RecordsOf).OfType<StepSummaryRecord>().Single();
        // Each stats call adds a second of CPU: the step's container was measured twice during the step, the carried-over service twice more than before it.
        Assert.That(summary.CpuSeconds, Is.EqualTo(4).Within(1e-9));
        Assert.That(summary.Status, Is.EqualTo("Success"));
        Assert.That(summary.Name, Is.EqualTo("build"));
    }

    // The pipeline awaits each event; a connection that never answers must not hold up the build.
    [Test]
    public async Task ASendThatNeverAnswers_DoesntHoldUpThePipeline()
    {
        var started = 0;
        var never = new TaskCompletionSource();
        var sampler = new JobMetricsSampler(JobId, AssignmentId, TimeSpan.FromHours(1), _host, _containers, null, _ =>
        {
            Interlocked.Increment(ref started);
            return never.Task;
        }, new LoggerCapture(), _time);
        sampler.Start();
        for (var i = 0; i < 80; i++)
        {
            _containers.Present.Add(new ContainerSeen($"container{i:D3}xxxxxxxx", $"c{i}"));
        }

        var ticks = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                _time.Now += TimeSpan.FromSeconds(2);
                await sampler.TickAsync();
            }
            await sampler.OnPipelineEventAsync(new PipelineEvent { Kind = PipelineEventKind.StepEnded, Timestamp = _time.Now, StepIndex = 0, StepName = "build", Status = "Success" });
        });

        Assert.That(await Task.WhenAny(ticks, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(ticks), "sampling and events went on with sends outstanding");
        Assert.That(started, Is.GreaterThan(0), "the batch did fill and try to send");
    }

    [Test]
    public async Task FailingSources_AreSkipped_AndSamplingCarriesOn()
    {
        _containers.Present.Add(new ContainerSeen("healthy00000", "a"));
        _containers.Present.Add(new ContainerSeen("slow00000000", "b"));
        _containers.FailingStats.Add("slow00000000");
        await TickAfterAsync(TimeSpan.FromSeconds(2));

        _host.Throwing = () => throw new System.IO.IOException("gone");
        _containers.ListThrows = true;
        await TickAfterAsync(TimeSpan.FromSeconds(2));
        await _sampler.StopAsync();

        var samples = DrainChunks().SelectMany(RecordsOf).OfType<SampleRecord>().ToList();
        Assert.That(samples[0].Containers.Select(c => c.Id), Is.EqualTo(new[] { "healthy00000" }), "a container that didn't answer is left out of that sample only");
        Assert.That(samples[1].Host, Is.Null);
        Assert.That(samples[1].Containers, Is.Empty);
        Assert.That(samples, Has.Count.EqualTo(3), "and the samples kept coming");
    }
}
