using System.IO.Pipes;
using LustreRadio.Audio;
using LustreRadio.Core.History;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Audio.Tests;

internal sealed class FakeProbe(DiskProbeOutcome outcome) : IDiskProbe
{
    public int Calls { get; private set; }

    public DiskProbeOutcome Outcome { get; set; } = outcome;

    public DiskProbeOutcome Probe(string? stuckFile, TimeSpan timeout)
    {
        Calls++;
        return Outcome;
    }
}

internal sealed class FakeInspector(WorkerThreadState state) : IThreadInspector
{
    public WorkerThreadState State { get; set; } = state;

    public long CpuTicks(int nativeThreadId) => 0;

    public WorkerThreadState Inspect(int nativeThreadId, long cpuTicksAtStart, TimeSpan callDuration, StallOptions options) => State;
}

/// <summary>Izvor koji stvarno blokira radnu nit (za testove sa pravim nitima): ceka signal ili se vrti u krug.</summary>
internal sealed class BlockingSource(AudioFormat format, long length, bool spin = false) : ISampleSource
{
    private readonly ManualResetEventSlim _release = new();
    private volatile bool _disposed;
    private long _position;

    public long BlockAt { get; init; } = long.MaxValue;

    public AudioFormat Format => format;

    public long? LengthFrames => length;

    public bool Disposed => _disposed;

    public void Release() => _release.Set();

    public int Read(Span<float> buffer)
    {
        if (_position >= BlockAt && !_release.IsSet)
        {
            if (spin)
            {
                while (!_release.IsSet)
                {
                    Thread.SpinWait(1000);
                }
            }
            else
            {
                _release.Wait();
            }
        }

        var frames = (int)Math.Min(buffer.Length / 2, Math.Min(length, _position >= BlockAt ? length : BlockAt) - _position);
        if (frames <= 0)
        {
            return 0;
        }

        buffer[..(frames * 2)].Fill(0.3f);
        _position += frames;
        return frames;
    }

    public void Dispose() => _disposed = true;
}

/// <summary>Faza B, korak B3 (drugi deo): zastoji – T, T2, P, stanje niti, proba diska, napustene niti, ponovno pokretanje.</summary>
public sealed class StallTests(ITestOutputHelper output)
{
    private static readonly AudioFormat Format = AudioFormat.Stereo44100;
    private const int Rate = 44100;

    private static long F(double s) => (long)Math.Round(s * Rate);

    private sealed class Rig
    {
        private readonly Queue<(string Id, ISampleSource Source)> _queue = new();

        public Rig(DiskProbeOutcome probe, WorkerThreadState state, StallOptions? stall = null)
        {
            Probe = new FakeProbe(probe);
            Inspector = new FakeInspector(state);
            Coordinator = new Coordinator(Format, Next, new CoordinatorOptions { Stall = stall ?? new StallOptions() }, Events)
            {
                DiskProbe = Probe,
                ThreadInspector = Inspector,
            };
            Coordinator.RestartRequested += Restarts.Add;
            Sequencer = new CrossfadeSequencer(Format, Coordinator, 6, history: Events);
        }

        public FakeProbe Probe { get; }

        public FakeInspector Inspector { get; }

        public Coordinator Coordinator { get; }

        public CrossfadeSequencer Sequencer { get; }

        public RecordingEvents Events { get; } = new();

        public List<RestartRequest> Restarts { get; } = [];

        public List<string?> Statuses { get; } = [];

        public ScriptedSource Add(string id, double seconds, Func<ScriptedSource, ScriptedSource>? configure = null, double speed = 0, Func<long, float>? signal = null)
        {
            var source = new ScriptedSource(Format, F(seconds), signal ?? (_ => 0.2f)) { Speed = speed };
            source = configure?.Invoke(source) ?? source;
            _queue.Enqueue((id, source));
            return source;
        }

        private QueuedTrack? Next() => _queue.Count == 0 ? null : _queue.Dequeue() is var (id, s) ? new QueuedTrack(id, s) { PlaybackId = Guid.CreateVersion7(), Path = "C:\\muzika\\" + id } : null;

        public float[] Render(double seconds)
        {
            var result = new List<float>();
            var buffer = new float[1024 * 2];
            while (result.Count < F(seconds))
            {
                var n = Sequencer.Read(buffer);
                if (n == 0)
                {
                    break;
                }

                Statuses.Add(Coordinator.StallStatus);
                for (var i = 0; i < n; i++)
                {
                    result.Add(buffer[i * 2]);
                }
            }

            return [.. result];
        }
    }

    [Theory] // B-121: opsti zastoj 8 s i 20 s van pretapanja: nijedna pesma preskocena; ista pesma sa istog mesta
    [InlineData(8.0)]
    [InlineData(20.0)]
    public void B121_GeneralStall_NothingSkipped_SameTrackContinues(double stall)
    {
        var rig = new Rig(DiskProbeOutcome.NotResponding, WorkerThreadState.Blocked);
        var a = rig.Add("1", 40, s => s.StallAt(F(12), F(stall)));
        rig.Add("2", 40);
        rig.Render(40 + stall);
        output.WriteLine($"zastoj {stall} s: tisina {rig.Sequencer.StarvedFrames / (double)Rate:F2} s, probe {rig.Probe.Calls}");
        Assert.Equal(0, rig.Coordinator.Abandoned);
        Assert.Equal(F(40), a.Position);
        Assert.Contains("ceka-disk", rig.Statuses);
        var o = rig.Events.Outcomes();
        Assert.Equal(PlaybackStatus.Played, o[1].Single().Status);
        Assert.True(rig.Sequencer.StarvedFrames > 0);
    }

    [Fact] // B-122, B-127 (slucaj 2): pesma zaglavljena zauvek, disk radi -> posle T2 i probe prelaz na sledecu; bez povratka
    public void B122_StuckForever_DiskWorks_NextTrack_NoReturn()
    {
        var rig = new Rig(DiskProbeOutcome.DiskWorks, WorkerThreadState.Blocked);
        rig.Add("1", 40, s => s.StallAt(F(12), F(1000)), signal: _ => 0.5f);
        rig.Add("2", 40, signal: _ => -0.2f);
        var audio = rig.Render(30);
        var lastA = Array.FindLastIndex(audio, v => v > 0.4f) / (double)Rate;
        var firstB = Array.FindIndex(audio, v => v < -0.1f) / (double)Rate;
        output.WriteLine($"A do {lastA:F2} s; B od {firstB:F2} s; tisina {rig.Sequencer.StarvedFrames / (double)Rate:F3} s");
        Assert.Equal(1, rig.Coordinator.Abandoned);
        Assert.InRange(firstB - lastA, 0, 0.2);
        Assert.DoesNotContain(audio.Skip((int)F(firstB + 0.1)), v => v > 0.4f); // nema povratka na A
        var o = rig.Events.Outcomes();
        Assert.Equal((PlaybackStatus.Stopped, StopReason.DecoderStall), (o[1].Single().Status, o[1].Single().Reason));
        Assert.Equal("1", Assert.Single(rig.Coordinator.StuckTracks));
    }

    [Fact] // B-123, B-127: najvise 3 napustene niti; treca -> ponovno pokretanje na granici pesama
    public void B123_AtMostThreeAbandoned_ThirdSchedulesRestartAtBoundary()
    {
        var rig = new Rig(DiskProbeOutcome.DiskWorks, WorkerThreadState.Blocked);
        for (var i = 1; i <= 6; i++)
        {
            rig.Add($"{i}", 30, s => s.StallAt(F(8), F(1000)));
        }

        rig.Render(120);
        output.WriteLine($"napustenih {rig.Coordinator.Abandoned}; zahteva {rig.Restarts.Count}; zaglavljenih poziva {rig.Coordinator.StuckCalls}");
        Assert.Equal(3, rig.Coordinator.Abandoned);
        var restart = Assert.Single(rig.Restarts);
        Assert.False(restart.Immediate);
        Assert.Equal(["1", "2", "3"], restart.StuckTracks.Take(3));
    }

    [Fact] // B-123, B-127: aktivna (cetvrta) nit zaglavljena pre granice -> odmah ponovno pokretanje, nit se ne napusta
    public void B123_ActiveThreadStuckWhenLimitReached_ImmediateRestart_NotAbandoned()
    {
        var rig = new Rig(DiskProbeOutcome.DiskWorks, WorkerThreadState.Blocked, new StallOptions { MaxAbandoned = 1 });
        rig.Add("1", 4);
        rig.Add("2", 30, s => s.StallAt(0, F(1000)));
        rig.Add("3", 30, s => s.StallAt(0, F(1000)));
        rig.Render(30);
        Assert.Equal(1, rig.Coordinator.Abandoned);
        var restart = Assert.Single(rig.Restarts);
        Assert.True(restart.Immediate);
    }

    [Fact] // B-124 (b): N2 skupljen pre T -> nastavak iste pesme
    public void B124_ResumeBeforeT_SameTrackContinues()
    {
        var rig = new Rig(DiskProbeOutcome.DiskWorks, WorkerThreadState.NoCpu);
        var a = rig.Add("1", 30, s => s.StallAt(F(12), F(5.5)));
        rig.Add("2", 30);
        rig.Render(40);
        Assert.Equal(0, rig.Coordinator.Abandoned);
        Assert.Equal(F(30), a.Position);
        Assert.Equal(PlaybackStatus.Played, rig.Events.Outcomes()[1].Single().Status);
    }

    [Fact] // B-124 (c): spora pesma: T istekao, sledeca spremna, disk radi -> prelaz u T +- 50 ms; nema povratka
    public void B124_SlowTrack_AfterT_NextStarts()
    {
        var rig = new Rig(DiskProbeOutcome.DiskWorks, WorkerThreadState.Blocked);
        rig.Add("1", 60, Slow, signal: _ => 0.5f);
        rig.Add("2", 60, signal: _ => -0.2f);
        var audio = rig.Render(40);
        var firstB = Array.FindIndex(audio, v => v < -0.1f);
        var silenceStart = Array.FindLastIndex(audio, firstB, v => v > 0.01f) + 1;
        var silence = (firstB - silenceStart) / (double)Rate;
        output.WriteLine($"tisina pre B: {silence:F3} s (T = 1 s)");
        Assert.InRange(silence, 0.95, 1.1);
        Assert.Equal((PlaybackStatus.Stopped, StopReason.DecoderStall), (rig.Events.Outcomes()[1].Single().Status, rig.Events.Outcomes()[1].Single().Reason));
    }

    [Fact] // B-124 (d): spora pesma, sledeca nije spremna -> ceka se
    public void B124_SlowTrack_NextNotReady_Waits()
    {
        var rig = new Rig(DiskProbeOutcome.DiskWorks, WorkerThreadState.Blocked);
        var a = rig.Add("1", 25, Slow);
        rig.Render(80);
        Assert.Equal(F(25), a.Position);
        Assert.Equal(PlaybackStatus.Played, rig.Events.Outcomes()[1].Single().Status);
    }

    /// <summary>Spora pesma od 8. sekunde: delovi od 0,2 s, izmedju kratki zastoji od 0,3 s (oko 0,4x brzine; svaki poziv kraci od T2).</summary>
    private static ScriptedSource Slow(ScriptedSource s)
    {
        for (var k = 0; k < 400; k++)
        {
            s.StallAt(F(8 + (0.2 * k)), F(0.3));
        }

        return s;
    }

    [Theory] // B-125, B-193: opsti zastoj, nit bez procesora, neodredjeno -> 0 preskocenih; poruka
    [InlineData(DiskProbeOutcome.DiskWorks, WorkerThreadState.NoCpu, "ceka-procesor")]
    [InlineData(DiskProbeOutcome.DiskWorks, WorkerThreadState.Undetermined, "uzrok-nije-utvrdjen")]
    [InlineData(DiskProbeOutcome.Undetermined, WorkerThreadState.Blocked, "uzrok-nije-utvrdjen")]
    [InlineData(DiskProbeOutcome.NotResponding, WorkerThreadState.Blocked, "ceka-disk")]
    public void B125_B193_NoSkipUnlessDiskWorks(DiskProbeOutcome probe, WorkerThreadState state, string status)
    {
        var rig = new Rig(probe, state);
        var a = rig.Add("1", 30, s => s.StallAt(F(12), F(10)));
        rig.Add("2", 30);
        rig.Render(45);
        Assert.Equal(0, rig.Coordinator.Abandoned);
        Assert.Equal(F(30), a.Position);
        Assert.Contains(status, rig.Statuses);
        if (state != WorkerThreadState.Blocked)
        {
            Assert.Equal(0, rig.Probe.Calls); // bez procesora / neodredjeno: proba se ne pokrece
        }
    }

    [Fact] // B-194: napustena nit se odblokira i pokusa da preda zvuk -> odbaceno; nit zatvara izvor i zavrsava se
    public void B194_AbandonedThreadUnblocks_ResultDropped_SourceClosed()
    {
        var stall = new StallOptions { BlockedCallSeconds = 0.3, ProbeSeconds = 0.1, ProbeRepeatSeconds = 0.2 };
        var stuck = new BlockingSource(Format, F(30)) { BlockAt = F(6) };
        var next = new BlockingSource(Format, F(30));
        var queue = new Queue<QueuedTrack>([new QueuedTrack("1", stuck) { PlaybackId = Guid.NewGuid() }, new QueuedTrack("2", next) { PlaybackId = Guid.NewGuid() }]);
        using var coordinator = new Coordinator(Format, () => queue.Count > 0 ? queue.Dequeue() : null, new CoordinatorOptions { Stall = stall }, threaded: true)
        {
            DiskProbe = new FakeProbe(DiskProbeOutcome.DiskWorks),
            ThreadInspector = new FakeInspector(WorkerThreadState.Blocked),
        };
        coordinator.Start();
        var sequencer = new CrossfadeSequencer(Format, coordinator, 6);
        var buffer = new float[1024 * 2];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (coordinator.Abandoned == 0 && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            sequencer.Read(buffer);
            Thread.Sleep(5);
        }

        Assert.Equal(1, coordinator.Abandoned);
        stuck.Release(); // napustena nit se odblokira
        watch.Restart();
        while (!stuck.Disposed && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(20);
        }

        Assert.True(stuck.Disposed, "napustena nit nije zatvorila izvor");
        coordinator.DiscardPending(StopReason.Shutdown);
    }

    [Fact] // B-195: memorija u najgorem trenutku: 3 napustene + 1 zaglavljena aktivna nit (lazni dekoder)
    public void B195_MemoryWithThreeAbandonedAndOneStuck()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        GC.Collect();
        process.Refresh();
        var before = process.PrivateMemorySize64;
        var stall = new StallOptions { BlockedCallSeconds = 0.2, ProbeSeconds = 0.1, ProbeRepeatSeconds = 0.2 };
        var sources = Enumerable.Range(0, 6).Select(_ => new BlockingSource(Format, F(30)) { BlockAt = 0 }).ToList();
        var queue = new Queue<QueuedTrack>(sources.Select((s, i) => new QueuedTrack($"{i}", s) { PlaybackId = Guid.NewGuid() }));
        var restarts = new List<RestartRequest>();
        using var coordinator = new Coordinator(Format, () => queue.Count > 0 ? queue.Dequeue() : null, new CoordinatorOptions { Stall = stall }, threaded: true)
        {
            DiskProbe = new FakeProbe(DiskProbeOutcome.DiskWorks),
            ThreadInspector = new FakeInspector(WorkerThreadState.Blocked),
        };
        coordinator.RestartRequested += restarts.Add;
        coordinator.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (restarts.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(20);
        }

        process.Refresh();
        var delta = (process.PrivateMemorySize64 - before) / 1048576.0;
        output.WriteLine($"napustenih {coordinator.Abandoned}; zahtev za ponovno pokretanje: {restarts.FirstOrDefault()?.Immediate}; privatna memorija +{delta:F1} MB (lazni dekoder; Media Foundation nije meren)");
        Assert.Equal(3, coordinator.Abandoned);
        Assert.True(Assert.Single(restarts).Immediate);
        sources.ForEach(s => s.Release());
        coordinator.DiscardPending(StopReason.Shutdown);
    }

    [Fact] // B-197: CancelSynchronousIo oslobadja nit zaglavljenu u citanju (imenovana cev koja ne salje podatke)
    public void B197_CancelSynchronousIo_FreesThreadStuckInRead()
    {
        var name = "lustre-b197-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.Out);
        var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.None);
        client.Connect(5000);
        server.WaitForConnection();
        var pipeSource = new PipeSource(Format, client);
        var stall = new StallOptions { BlockedCallSeconds = 0.3, ProbeSeconds = 0.1, ProbeRepeatSeconds = 0.2 };
        var queue = new Queue<QueuedTrack>([new QueuedTrack("1", pipeSource) { PlaybackId = Guid.NewGuid() }]);
        using var coordinator = new Coordinator(Format, () => queue.Count > 0 ? queue.Dequeue() : null, new CoordinatorOptions { Stall = stall }, threaded: true)
        {
            DiskProbe = new FakeProbe(DiskProbeOutcome.DiskWorks),
            ThreadInspector = new FakeInspector(WorkerThreadState.Blocked),
        };
        coordinator.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!pipeSource.Disposed && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(20);
        }

        output.WriteLine($"napustenih {coordinator.Abandoned}; citanje prekinuto: {pipeSource.ReadError?.GetType().Name ?? "nije"}");
        Assert.Equal(1, coordinator.Abandoned);
        Assert.True(pipeSource.Disposed, "nit nije oslobodjena (CancelSynchronousIo)");
        coordinator.DiscardPending(StopReason.Shutdown);
    }

    [Fact] // B-205: lazni dekoder koji se vrti u krug -> prepoznat kao zaglavljen BEZ probe diska; prelaz na sledecu
    public void B205_SpinningDecoder_RecognizedWithoutProbe()
    {
        var stall = new StallOptions { BlockedCallSeconds = 0.5, ProbeSeconds = 0.1, ProbeRepeatSeconds = 0.2 };
        var spinning = new BlockingSource(Format, F(30), spin: true) { BlockAt = 0 };
        var queue = new Queue<QueuedTrack>([new QueuedTrack("1", spinning) { PlaybackId = Guid.NewGuid() }, new QueuedTrack("2", new ScriptedSource(Format, F(30), _ => 0.2f)) { PlaybackId = Guid.NewGuid() }]);
        var probe = new FakeProbe(DiskProbeOutcome.NotResponding);
        var started = new List<string>();
        using var coordinator = new Coordinator(Format, () => queue.Count > 0 ? queue.Dequeue() : null, new CoordinatorOptions { Stall = stall }, threaded: true)
        {
            DiskProbe = probe,
            ThreadInspector = new WindowsThreadInspector(),
        };
        coordinator.TrackStarted += t => started.Add(t.Id);
        coordinator.Start();
        var sequencer = new CrossfadeSequencer(Format, coordinator, 6);
        var buffer = new float[1024 * 2];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!started.Contains("2") && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            sequencer.Read(buffer);
            Thread.Sleep(5);
        }

        spinning.Release();
        output.WriteLine($"napustenih {coordinator.Abandoned}; proba diska pozvana {probe.Calls} puta; za {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(1, coordinator.Abandoned);
        Assert.Equal(0, probe.Calls);
        Assert.Contains("2", started);
        coordinator.DiscardPending(StopReason.Shutdown);
    }

    [Theory] // D7-5: tabela stanja niti (granice D = 20%, G = 50%, privremene)
    [InlineData(System.Diagnostics.ThreadState.Ready, 0.0, WorkerThreadState.NoCpu)]
    [InlineData(System.Diagnostics.ThreadState.Running, 0.05, WorkerThreadState.NoCpu)]
    [InlineData(System.Diagnostics.ThreadState.Wait, 0.05, WorkerThreadState.Blocked)]
    [InlineData(System.Diagnostics.ThreadState.Running, 0.9, WorkerThreadState.Spinning)]
    [InlineData(System.Diagnostics.ThreadState.Wait, 0.3, WorkerThreadState.Undetermined)]
    [InlineData(System.Diagnostics.ThreadState.Running, 0.3, WorkerThreadState.Undetermined)]
    public void ThreadStateTable(System.Diagnostics.ThreadState state, double share, WorkerThreadState expected) =>
        Assert.Equal(expected, WindowsThreadInspector.Classify(state, null, share, new StallOptions()));

    private sealed class PipeSource(AudioFormat format, Stream pipe) : ISampleSource
    {
        private readonly byte[] _bytes = new byte[4096];

        public AudioFormat Format => format;

        public long? LengthFrames => F(30);

        public bool Disposed { get; private set; }

        public Exception? ReadError { get; private set; }

        public int Read(Span<float> buffer)
        {
            try
            {
                var n = pipe.Read(_bytes, 0, _bytes.Length); // blokira: server nista ne salje
                return n == 0 ? 0 : Math.Min(n / 8, buffer.Length / 2);
            }
            catch (Exception ex)
            {
                ReadError = ex;
                throw;
            }
        }

        public void Dispose()
        {
            Disposed = true;
            pipe.Dispose();
        }
    }
}
