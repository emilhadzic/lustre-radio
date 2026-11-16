using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using LustreRadio.Core.History;

namespace LustreRadio.Audio;

/// <summary>Podesavanja pripreme zvuka (plan faze B, 2.1 i 2.7). Vrednosti pragova su privremene.</summary>
public sealed record CoordinatorOptions
{
    /// <summary>Velicina bafera decka.</summary>
    public double BufferSeconds { get; init; } = 5;

    /// <summary>Dopuna pocinje kad u baferu ostane manje od ovoga.</summary>
    public double RefillBelowSeconds { get; init; } = 4;

    /// <summary>Jedan deo dekodiranja (oko 85 ms na 48 kHz).</summary>
    public int ChunkFrames { get; init; } = 4096;

    /// <summary>N2: koliko zvuka mora da se skupi u praznom decku pre nastavka (privremeno 1 s).</summary>
    public double ResumeSeconds { get; init; } = 1;

    /// <summary>Koliko cesto se ponovo pita za pesmu kad nijedna nije mogla da se pripremi.</summary>
    public double RetryWhenEmptySeconds { get; init; } = 1;
}

/// <summary>
/// Samo za simulaciju u testovima (virtuelni sat): izvor koji "stoji" ne vraca zvuk dok ne prodje zadato vreme.
/// Pravi dekoderi ovo ne implementiraju; u pravom radu zastoj je stvarno blokirana radna nit.
/// </summary>
public interface IVirtualStall
{
    bool IsStalled(long clockFrames);
}

/// <summary>
/// Koordinator (nit za pripremu, plan faze B, 3): vodi dva bafera decka, jedini upisuje zvuk u njih, priprema sledecu
/// pesmu i predaje je niti za mesanje kroz <see cref="HandoffSlot"/>. Nikad ne poziva dekoder i ne cita fajlove sa
/// muzikom: to radi radna nit za dekodiranje, u sopstveni bafer, a koordinator samo prepisuje gotove delove
/// (uz broj generacije decka; deo stare generacije se odbacuje).
/// </summary>
public sealed class Coordinator : IDisposable
{
    private enum State
    {
        Free,
        Preparing,
        Published,
        Mixing,
    }

    private sealed class Slot(DeckFeed feed)
    {
        public DeckFeed Feed { get; } = feed;

        public State State { get; set; }

        public PreparedTrack? Item { get; set; }

        public ISampleSource? Source { get; set; }

        public bool SourceEnded { get; set; }

        public long Decoded { get; set; }

        public bool Filling { get; set; }
    }

    private readonly AudioFormat _format;
    private readonly Func<QueuedTrack?> _next;
    private readonly IPlaybackEvents? _history;
    private readonly Action<string> _log;
    private readonly Slot[] _slots;
    private readonly int _capacity;
    private readonly long _refillBelow;
    private readonly long _retryFrames;
    private readonly WorkerHost _worker;
    private readonly Queue<ISampleSource> _toDispose = new();
    private readonly bool _threaded;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;
    private long _nextPrepareAt;
    private volatile bool _exhausted;
    private long _lateChunks;
    private bool _disposed;

    public Coordinator(AudioFormat format, Func<QueuedTrack?> next, CoordinatorOptions? options = null, IPlaybackEvents? history = null, bool threaded = false, Action<string>? log = null)
    {
        _format = format;
        _next = next ?? throw new ArgumentNullException(nameof(next));
        Options = options ?? new CoordinatorOptions();
        _history = history;
        _log = log ?? (_ => { });
        _threaded = threaded;
        _capacity = (int)Math.Max(16, format.FramesFromSeconds(Options.BufferSeconds));
        _refillBelow = format.FramesFromSeconds(Options.RefillBelowSeconds);
        _retryFrames = format.FramesFromSeconds(Options.RetryWhenEmptySeconds);
        _slots = [new Slot(new DeckFeed(_capacity)), new Slot(new DeckFeed(_capacity))];
        _worker = threaded ? new ThreadedWorker(Options.ChunkFrames) : new InlineWorker(Options.ChunkFrames);
    }

    public CoordinatorOptions Options { get; }

    public AudioFormat Format => _format;

    /// <summary>Mesto za predaju sledece pesme niti za mesanje.</summary>
    public HandoffSlot Handoff { get; } = new();

    /// <summary>Nijedna pesma trenutno ne moze da se pripremi (pokusava se ponovo na 1 s).</summary>
    public bool Exhausted => _exhausted;

    public bool Threaded => _threaded;

    /// <summary>Delovi zvuka koji su stigli za staru generaciju decka i odbaceni su.</summary>
    public long LateChunksDiscarded => Interlocked.Read(ref _lateChunks);

    /// <summary>Pesma je preuzeta od niti za mesanje (poziva se sa niti koordinatora, nikad sa audio-niti).</summary>
    public event Action<QueuedTrack>? TrackStarted;

    public void Start()
    {
        if (!_threaded || _thread is not null)
        {
            return;
        }

        ((ThreadedWorker)_worker).Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.Koordinator" };
        _thread.Start();
    }

    /// <summary>
    /// Bez posebnih niti (priprema zvuka unapred, testovi): radi sve sto moze sada. <paramref name="clockFrames"/> je sat
    /// izrazen u frejmovima (u testovima: koliko je zvuka vec pripremljeno).
    /// </summary>
    public void ServiceInline(long clockFrames)
    {
        if (_threaded)
        {
            throw new InvalidOperationException("Koordinator radi na svojoj niti.");
        }

        for (var i = 0; i < 10_000; i++)
        {
            ((InlineWorker)_worker).Pump(clockFrames);
            if (!Step(clockFrames))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Posle zaustavljanja zvuka: pripremljene stavke koje nisu krenule se odbacuju (zapis <c>odbacena</c>).
    /// </summary>
    public void DiscardPending(StopReason reason)
    {
        StopThread();
        foreach (var slot in _slots)
        {
            if (slot.Item is { } item && slot.State is State.Preparing or State.Published)
            {
                if (slot.State == State.Published && !Handoff.Retract(item))
                {
                    continue; // nit za mesanje ju je uzela; za nju zapis pravi sekvencer
                }

                _history?.Discarded(item.Track.PlaybackId, item.TrackNumber, reason);
                slot.State = State.Free;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopThread();
        _worker.Dispose();
        foreach (var slot in _slots)
        {
            DisposeQuietly(slot.Source);
            slot.Source = null;
        }

        while (_toDispose.Count > 0)
        {
            DisposeQuietly(_toDispose.Dequeue());
        }

        _stop.Dispose();
    }

    private void StopThread()
    {
        _stop.Set();
        if (_thread is not null && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private long Now => _format.FramesFromSeconds(_clock.Elapsed.TotalSeconds);

    private void Run()
    {
        var worker = (ThreadedWorker)_worker;
        while (!_stop.IsSet)
        {
            bool progress;
            try
            {
                progress = Step(Now);
            }
            catch (Exception ex)
            {
                _log("Greska koordinatora: " + ex.Message);
                progress = false;
            }

            if (!progress)
            {
                worker.ResultReady.Wait(TimeSpan.FromMilliseconds(20));
                worker.ResultReady.Reset();
            }
        }
    }

    /// <summary>Jedan korak koordinatora. Vraca true ako je nesto uradio.</summary>
    private bool Step(long now)
    {
        var progress = false;
        while (_worker.TryGetResult(out var result))
        {
            Handle(result, now);
            progress = true;
        }

        foreach (var slot in _slots)
        {
            // nit za mesanje je preuzela objavljenu pesmu
            if (slot.State == State.Published && !ReferenceEquals(Handoff.Peek(), slot.Item))
            {
                slot.State = State.Mixing;
                TrackStarted?.Invoke(slot.Item!.Track);
                progress = true;
            }

            // nit za mesanje je pustila deck: zvuk koji jos stigne za tu pesmu se odbacuje (nova generacija)
            if (slot.State == State.Mixing && slot.Feed.Released)
            {
                if (slot.Source is { } source)
                {
                    _toDispose.Enqueue(source);
                }

                slot.Source = null;
                slot.Item = null;
                slot.Feed.Reset();
                slot.State = State.Free;
                progress = true;
            }

            if (slot.State == State.Preparing && (slot.SourceEnded || slot.Feed.FreeFrames == 0) && Handoff.Publish(slot.Item!))
            {
                slot.State = State.Published;
                progress = true;
            }
        }

        if (!_worker.Idle)
        {
            return progress;
        }

        // prioritet: deck koji svira i prazni se, pa deck koji se priprema, pa zatvaranje starih izvora, pa nova pesma
        var refill = _slots.Where(s => (s.State is State.Mixing or State.Published) && !s.SourceEnded && s.Source is not null)
            .Where(s => s.Filling || s.Feed.Available < _refillBelow).OrderBy(s => s.Feed.Available).FirstOrDefault();
        if (refill is not null && refill.Feed.FreeFrames >= Options.ChunkFrames)
        {
            refill.Filling = true;
            _worker.Submit(WorkerJob.Decode(Array.IndexOf(_slots, refill), refill.Feed.Generation, refill.Source!));
            return true;
        }

        if (refill is not null)
        {
            refill.Filling = false; // pun
        }

        var preparing = _slots.FirstOrDefault(s => s.State == State.Preparing && !s.SourceEnded && s.Feed.FreeFrames > 0);
        if (preparing is not null)
        {
            _worker.Submit(WorkerJob.Decode(Array.IndexOf(_slots, preparing), preparing.Feed.Generation, preparing.Source!));
            return true;
        }

        if (_toDispose.Count > 0)
        {
            _worker.Submit(WorkerJob.DisposeSource(_toDispose.Dequeue()));
            return true;
        }

        var pending = _slots.Any(s => s.State is State.Preparing or State.Published);
        var free = _slots.FirstOrDefault(s => s.State == State.Free);
        if (!pending && free is not null && now >= _nextPrepareAt)
        {
            free.State = State.Preparing;
            free.Item = null;
            free.SourceEnded = false;
            free.Decoded = 0;
            free.Filling = false;
            _worker.Submit(WorkerJob.Prepare(Array.IndexOf(_slots, free), free.Feed.Generation, _next));
            return true;
        }

        return progress;
    }

    private void Handle(WorkerResult result, long now)
    {
        if (result.Kind == WorkerJobKind.DisposeSource)
        {
            return;
        }

        var slot = _slots[result.SlotIndex];
        if (result.Generation != slot.Feed.Generation)
        {
            // zakasneli deo za deck koji je u medjuvremenu dodeljen drugoj pesmi: odbacuje se
            Interlocked.Increment(ref _lateChunks);
            if (result.Track is { } stale)
            {
                DisposeQuietly(stale.Source);
            }

            return;
        }

        if (result.Kind == WorkerJobKind.Prepare)
        {
            if (result.Track is not { } track)
            {
                if (result.Error is not null)
                {
                    _log("Priprema pesme nije uspela: " + result.Error.Message);
                }

                _exhausted = true;
                _nextPrepareAt = now + _retryFrames;
                slot.State = State.Free;
                return;
            }

            _exhausted = false;
            var number = long.TryParse(track.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (track.Source.Format != _format)
            {
                _log("Format izvora se razlikuje od formata engine-a: " + track.Id);
                _history?.Discarded(track.PlaybackId, number, StopReason.NotLoaded);
                _toDispose.Enqueue(track.Source);
                slot.State = State.Free;
                return;
            }

            slot.Source = track.Source;
            slot.Item = new PreparedTrack(track, slot.Feed, number);
            return;
        }

        // Decode
        if (slot.State is State.Free || slot.Source is null)
        {
            Interlocked.Increment(ref _lateChunks);
            return;
        }

        if (result.Frames > 0)
        {
            slot.Feed.Write(result.Buffer.AsSpan(0, result.Frames * 2));
            slot.Decoded += result.Frames;
        }

        if (result.Ended || result.Error is not null)
        {
            slot.SourceEnded = true;
            var reason = result.Error switch
            {
                null => slot.Item?.LengthFrames is long length && slot.Decoded < length - (_format.SampleRate / 10) ? StopReason.UnexpectedEnd : StopReason.None,
                IOException or UnauthorizedAccessException => StopReason.FileUnavailable,
                _ => StopReason.FileError,
            };
            slot.Feed.MarkEnded(reason);
        }
    }

    private static void DisposeQuietly(ISampleSource? source)
    {
        try
        {
            source?.Dispose();
        }
        catch (Exception)
        {
            // zatvaranje neispravnog izvora ne sme da zaustavi rad
        }
    }
}

internal enum WorkerJobKind
{
    Prepare,
    Decode,
    DisposeSource,
}

internal sealed record WorkerJob(WorkerJobKind Kind, int SlotIndex, int Generation, ISampleSource? Source, Func<QueuedTrack?>? Next)
{
    public static WorkerJob Prepare(int slot, int generation, Func<QueuedTrack?> next) => new(WorkerJobKind.Prepare, slot, generation, null, next);

    public static WorkerJob Decode(int slot, int generation, ISampleSource source) => new(WorkerJobKind.Decode, slot, generation, source, null);

    public static WorkerJob DisposeSource(ISampleSource source) => new(WorkerJobKind.DisposeSource, -1, 0, source, null);
}

internal sealed record WorkerResult(WorkerJobKind Kind, int SlotIndex, int Generation, float[] Buffer, int Frames, bool Ended, Exception? Error, QueuedTrack? Track);

/// <summary>Radna nit za dekodiranje: otvara i dekodira u sopstveni bafer. Jedini deo koji sme da se zaglavi.</summary>
internal abstract class WorkerHost(int chunkFrames) : IDisposable
{
    private readonly ConcurrentQueue<WorkerResult> _results = new();

    protected int ChunkFrames { get; } = chunkFrames;

    public abstract bool Busy { get; }

    /// <summary>Nema posla u toku i svi rezultati su preuzeti (tek tada sme nov posao: bafer radne niti je jedan).</summary>
    public bool Idle => !Busy && _results.IsEmpty;

    public abstract void Submit(WorkerJob job);

    public bool TryGetResult(out WorkerResult result) => _results.TryDequeue(out result!);

    public virtual void Dispose()
    {
    }

    protected void Post(WorkerResult result) => _results.Enqueue(result);

    /// <param name="clockFrames">Samo bez posebne niti: virtuelni sat za izvore koji oponasaju zastoj (inace -1).</param>
    protected WorkerResult Execute(WorkerJob job, float[] buffer, long clockFrames = -1)
    {
        switch (job.Kind)
        {
            case WorkerJobKind.Prepare:
                try
                {
                    return new WorkerResult(job.Kind, job.SlotIndex, job.Generation, buffer, 0, false, null, job.Next!());
                }
                catch (Exception ex)
                {
                    return new WorkerResult(job.Kind, job.SlotIndex, job.Generation, buffer, 0, false, ex, null);
                }

            case WorkerJobKind.Decode:
                var got = 0;
                try
                {
                    while (got < ChunkFrames)
                    {
                        if (clockFrames >= 0 && got > 0 && job.Source is IVirtualStall stall && stall.IsStalled(clockFrames))
                        {
                            break; // oponasan zastoj: deo se predaje do mesta zastoja
                        }

                        var read = job.Source!.Read(buffer.AsSpan(got * 2, (ChunkFrames - got) * 2));
                        if (read <= 0)
                        {
                            return new WorkerResult(job.Kind, job.SlotIndex, job.Generation, buffer, got, true, null, null);
                        }

                        got += read;
                    }

                    return new WorkerResult(job.Kind, job.SlotIndex, job.Generation, buffer, got, false, null, null);
                }
                catch (Exception ex)
                {
                    return new WorkerResult(job.Kind, job.SlotIndex, job.Generation, buffer, got, true, ex, null);
                }

            default:
                try
                {
                    job.Source?.Dispose();
                }
                catch (Exception)
                {
                    // neispravan izvor
                }

                return new WorkerResult(job.Kind, -1, 0, buffer, 0, false, null, null);
        }
    }
}

/// <summary>Bez posebne niti: posao se izvrsava kad koordinator pozove <see cref="Pump"/> (osim ako izvor "stoji").</summary>
internal sealed class InlineWorker(int chunkFrames) : WorkerHost(chunkFrames)
{
    private readonly float[] _buffer = new float[chunkFrames * 2];
    private WorkerJob? _job;

    public override bool Busy => _job is not null;

    public override void Submit(WorkerJob job) => _job = job;

    public void Pump(long clockFrames)
    {
        if (_job is not { } job)
        {
            return;
        }

        if (job.Source is IVirtualStall stall && job.Kind == WorkerJobKind.Decode && stall.IsStalled(clockFrames))
        {
            return;
        }

        _job = null;
        Post(Execute(job, _buffer, clockFrames));
    }
}

/// <summary>Prava radna nit (u radu programa).</summary>
internal sealed class ThreadedWorker : WorkerHost
{
    private readonly float[] _buffer;
    private readonly SemaphoreSlim _jobReady = new(0);
    private readonly Thread _thread;
    private volatile WorkerJob? _job;
    private volatile bool _stop;

    public ThreadedWorker(int chunkFrames)
        : base(chunkFrames)
    {
        _buffer = new float[chunkFrames * 2];
        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.RadnaNitDekodera" };
    }

    public ManualResetEventSlim ResultReady { get; } = new();

    public override bool Busy => _job is not null;

    public void Start() => _thread.Start();

    public override void Submit(WorkerJob job)
    {
        _job = job;
        _jobReady.Release();
    }

    public override void Dispose()
    {
        _stop = true;
        _jobReady.Release();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        while (true)
        {
            _jobReady.Wait();
            if (_stop)
            {
                return;
            }

            if (_job is not { } job)
            {
                continue;
            }

            var result = Execute(job, _buffer);
            Post(result);
            _job = null;
            ResultReady.Set();
        }
    }
}
