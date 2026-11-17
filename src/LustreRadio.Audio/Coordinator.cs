using LustreRadio.Core.Diagnostics;
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

    /// <summary>Pragovi za zastoje (T, T2, P, D, G); privremeni.</summary>
    public StallOptions Stall { get; init; } = new();

    /// <summary>
    /// D8-1: pesma sme da krene kad je <c>pripremljena</c> potvrdjena u dnevniku, ili posle ovoliko sekundi
    /// (ako je bar u crnoj kutiji). 0 = bez cekanja (priprema unapred bez niti: testovi, offline).
    /// </summary>
    public double DurableWaitSeconds { get; init; } = 2;

    /// <summary>Koliko cesto se proverava da li stavke plana i dalje smeju (licenca, program).</summary>
    public double ValidateEverySeconds { get; init; } = 0.5;
}

/// <summary>Dekodiranje pesme je prestalo pre kraja (slucaj 1 iz plana 2.7): podaci za brojanje i proveru greske.</summary>
public sealed record DecodeFailure(QueuedTrack Track, StopReason Reason, Exception? Error, long PositionFrames);

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

        public PlannedItem? Planned { get; set; }

        public long Ticket { get; set; } = -1;

        public long PreparedSeq { get; set; } = -1;

        public long PreparedAt { get; set; } = -1;

        /// <summary>Posao "otvori pesmu ovog decka" (jedan po decku, bez zauzimanja memorije pri svakom pokusaju).</summary>
        public Func<QueuedTrack?>? OpenJob { get; set; }
    }

    private readonly AudioFormat _format;
    private readonly IPlanner _planner;
    private readonly List<PlannedItem> _plan = [];
    private readonly List<long> _handles = [];
    private readonly List<TicketEvent> _ticketEvents = [];
    private readonly List<string> _before = [];
    private string? _playingId;
    private string? _planReason;
    private long _nextValidateAt;
    private long _nextSelectAt;
    private volatile PlanSnapshot _planSnapshot = PlanSnapshot.Empty;
    private readonly IPlaybackEvents? _history;
    private readonly Action<string> _log;
    private readonly Slot[] _slots;
    private readonly int _capacity;
    private readonly long _refillBelow;
    private readonly long _retryFrames;
    private WorkerHost _worker;
    private readonly List<WorkerHost> _abandonedWorkers = [];
    private readonly List<string> _stuckTracks = [];
    private readonly long[] _starvedSince = [-1, -1];
    private long _jobStartedAt;
    private long _jobCpuStart = -1;
    private long _nextStallCheckAt;
    private bool _stallEvaluated;
    private Task<DiskProbeOutcome>? _probe;
    private long _probeStartedAt;
    private DiskProbeOutcome? _probeResult;
    private bool _restartAtBoundary;
    private bool _restartRaised;
    private volatile string? _stallStatus;
    private long _stuckCalls;
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
        : this(format, new FuncPlanner(next ?? throw new ArgumentNullException(nameof(next))), options, history, threaded, log)
    {
    }

    /// <summary>Sa planerom (B4): izbor na niti koordinatora, otvaranje na radnoj niti, plan do 3 stavke.</summary>
    public Coordinator(AudioFormat format, IPlanner planner, CoordinatorOptions? options = null, IPlaybackEvents? history = null, bool threaded = false, Action<string>? log = null)
    {
        _format = format;
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        Options = options ?? new CoordinatorOptions();
        _history = history;
        _log = log ?? (_ => { });
        _threaded = threaded;
        _capacity = (int)Math.Max(16, format.FramesFromSeconds(Options.BufferSeconds));
        _refillBelow = format.FramesFromSeconds(Options.RefillBelowSeconds);
        _retryFrames = format.FramesFromSeconds(Options.RetryWhenEmptySeconds);
        _slots = [new Slot(new DeckFeed(_capacity)), new Slot(new DeckFeed(_capacity))];
        foreach (var slot in _slots)
        {
            slot.OpenJob = () => _planner.Open(slot.Planned!); // jednom: koraci koordinatora ne zauzimaju memoriju
        }

        _worker = NewWorker();
    }

    /// <summary>Kartice izmedju niti za mesanje i koordinatora (D8-3).</summary>
    public PlayTickets Tickets { get; } = new();

    /// <summary>Plan (sledece pesme) za ekran; menja se samo kad se plan promeni.</summary>
    public PlanSnapshot Plan => _planSnapshot;

    /// <summary>Proba diska (u programu: citanje mimo kesa sa istog volumena). Bez nje se nikad ne preskace zbog zastoja.</summary>
    public IDiskProbe? DiskProbe { get; set; }

    /// <summary>Stanje radne niti od Windows-a (D7-5). Bez njega je stanje "neodredjeno": ceka se.</summary>
    public IThreadInspector? ThreadInspector { get; set; }

    /// <summary>Broj napustenih radnih niti u ovom procesu (najvise <see cref="StallOptions.MaxAbandoned"/>).</summary>
    public int Abandoned => _abandonedWorkers.Count;

    /// <summary>Najniza popunjenost decka koji svira (frejmova), merena u svakom koraku koordinatora (B-78).</summary>
    public long MinMixingFillFrames { get; private set; } = long.MaxValue;

    /// <summary>Broj zaglavljenih poziva dekodera (slucaj 2) u ovom procesu.</summary>
    public long StuckCalls => Interlocked.Read(ref _stuckCalls);

    /// <summary>Pesme koje su se zaglavile u ovom pokretanju (za karantin pri ponovnom pokretanju).</summary>
    public IReadOnlyList<string> StuckTracks => _stuckTracks;

    /// <summary>
    /// Zasto se trenutno ceka (za "Proveru sistema"): "ceka-disk", "uzrok-nije-utvrdjen", "ceka-procesor"; null kad se ne ceka.
    /// </summary>
    public string? StallStatus => _stallStatus;

    /// <summary>Kontrolisano ponovno pokretanje (D7-4): odmah, ili na prvoj granici pesama. Javlja se sa niti koordinatora.</summary>
    public event Action<RestartRequest>? RestartRequested;

    /// <summary>Dekodiranje je prestalo pre kraja pesme (greska, nedostupan fajl, prerani kraj).</summary>
    public event Action<DecodeFailure>? DecodeFailed;

    private WorkerHost NewWorker() => _threaded ? new ThreadedWorker(Options.ChunkFrames) : new InlineWorker(Options.ChunkFrames);

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

        // Rad bez niti zamenjuje nit koordinatora (samo testovi i priprema unapred): cuvar ga ne broji kao audio-nit.
        var audio = AudioThreadGuard.IsAudioThread;
        AudioThreadGuard.UnmarkCurrentThread();
        try
        {
            for (var i = 0; i < 10_000; i++)
            {
                ((InlineWorker)_worker).Pump(clockFrames);
                if (!Step(clockFrames))
                {
                    return;
                }
            }
        }
        finally
        {
            if (audio)
            {
                AudioThreadGuard.MarkCurrentThread();
            }
        }
    }

    /// <summary>
    /// Poziva se na pocetku svakog koraka koordinatora (njegova nit, oko 20 ms). Za posao koji ne sme na audio-nit,
    /// a njen rezultat audio-nit cita kao jednu vrednost (npr. "da li jos radimo po rasporedu").
    /// </summary>
    public Action? StepHook { get; set; }

    /// <summary>
    /// Posle zaustavljanja zvuka: pripremljene stavke koje nisu krenule se odbacuju (zapis <c>odbacena</c>).
    /// </summary>
    public void DiscardPending(StopReason reason)
    {
        StopThread();
        DetectTakes();
        EmitTickets();
        foreach (var slot in _slots)
        {
            if (slot.State is State.Preparing or State.Published)
            {
                if (slot.State == State.Published && slot.Item is { } item && !Handoff.Retract(item))
                {
                    continue; // nit za mesanje ju je uzela; za nju zapis pravi kartica
                }

                Discard(slot, reason, releaseReservation: false);
            }
        }

        _plan.Clear();
        PublishPlan();
    }

    /// <summary>Stavka u decku koja nije krenula: odbacena (zapis samo ako je bila "pripremljena"), deck slobodan.</summary>
    private void Discard(Slot slot, StopReason reason, bool releaseReservation)
    {
        if (slot.Ticket >= 0)
        {
            _history?.Discarded(Tickets.PlaybackIdOf(slot.Ticket), Tickets.TrackOf(slot.Ticket), reason);
            Tickets.MarkWithdrawn(slot.Ticket);
            Tickets.Release(slot.Ticket);
            _handles.Remove(slot.Ticket);
        }

        if (slot.Planned is { } planned)
        {
            if (releaseReservation)
            {
                _planner.Release(planned);
            }

            _plan.Remove(planned);
        }

        if (slot.Source is { } source && !slot.SourceEnded)
        {
            _toDispose.Enqueue(source);
        }
        else if (slot.Source is { } ended)
        {
            _toDispose.Enqueue(ended);
        }

        slot.Source = null;
        slot.Item = null;
        slot.Planned = null;
        slot.Ticket = -1;
        slot.PreparedSeq = -1;
        slot.PreparedAt = -1;
        slot.Feed.Reset();
        slot.State = State.Free;
    }

    private void PublishPlan()
    {
        if (_planner is FuncPlanner)
        {
            return; // bez stvarnog plana (testovi zvuka, Bench): nema sta da se prikaze
        }

        _planSnapshot = new PlanSnapshot([.. _plan], _plan.Count < 3 ? _planReason : null);
    }

    private void DetectTakes()
    {
        foreach (var slot in _slots)
        {
            if (slot.State == State.Published && !ReferenceEquals(Handoff.Peek(), slot.Item))
            {
                slot.State = State.Mixing;
                _playingId = slot.Planned?.TrackId ?? slot.Item?.Track.Id;
                if (slot.Planned is { } started)
                {
                    _plan.Remove(started);
                    PublishPlan();
                }

                TrackStarted?.Invoke(slot.Item!.Track);
                if (_restartAtBoundary && !_restartRaised)
                {
                    _restartRaised = true;
                    RestartRequested?.Invoke(new RestartRequest(false, "treci zaglavljen poziv dekodera; granica pesama", [.. _stuckTracks]));
                }
            }
        }
    }

    /// <summary>
    /// Dogadjaji iz kartica u istoriju (crnu kutiju), u redosledu u kom ih je nit za mesanje upisala. Zapis <c>pretapanje</c>
    /// nastaje SAMO iz kartice B (u njoj su i podaci pesme A): jedan zapis, jedan rid (D8-2). Kartica se oslobadja tek kad
    /// je zavrsena i kad su svi njeni dogadjaji prepisani.
    /// </summary>
    private bool EmitTickets()
    {
        if (_handles.Count == 0)
        {
            return false;
        }

        _ticketEvents.Clear();
        foreach (var handle in _handles)
        {
            Tickets.Collect(handle, _ticketEvents);
        }

        if (_ticketEvents.Count > 1)
        {
            _ticketEvents.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        }

        foreach (var e in _ticketEvents)
        {
            switch (e.Kind)
            {
                case JournalKind.Started:
                    _history?.Started(e.PlaybackId, e.Track, e.Frame);
                    break;
                case JournalKind.Crossfade:
                    _history?.Crossfade(e.PlaybackId, e.Track, e.PeerPlaybackId, e.PeerTrack, e.Frame);
                    break;
                case JournalKind.Ended:
                    _history?.Ended(e.PlaybackId, e.Track, e.Frame, e.PositionMs);
                    break;
                case JournalKind.Stopped:
                    _history?.Stopped(e.PlaybackId, e.Track, e.Reason, e.Frame, e.PositionMs);
                    break;
            }
        }

        for (var i = _handles.Count - 1; i >= 0; i--)
        {
            var handle = _handles[i];
            if (Tickets.StateOf(handle) == PlayTickets.Done)
            {
                Tickets.Collect(handle, _ticketEvents); // ishod je upisan pre stanja; nista ne ostaje
                Tickets.Release(handle);
                _handles.RemoveAt(i);
            }
        }

        return _ticketEvents.Count > 0;
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
        foreach (var abandoned in _abandonedWorkers)
        {
            abandoned.Dispose();
        }
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

            if (!progress && _worker is ThreadedWorker worker)
            {
                worker.ResultReady.Wait(TimeSpan.FromMilliseconds(20));
                worker.ResultReady.Reset();
            }
        }
    }

    /// <summary>Jedan korak koordinatora. Vraca true ako je nesto uradio.</summary>
    private bool Step(long now)
    {
        StepHook?.Invoke();
        var progress = false;
        while (_worker.TryGetResult(out var result))
        {
            Handle(result, now);
            progress = true;
        }

        DetectTakes();
        progress |= EmitTickets();
        progress |= MaintainPlan(now);
        foreach (var slot in _slots)
        {

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

            if (slot.State == State.Preparing && slot.Item is not null && (slot.SourceEnded || slot.Feed.FreeFrames < Options.ChunkFrames))
            {
                progress |= TryPublish(slot, now);
            }
        }

        foreach (var s in _slots)
        {
            if (s.State == State.Mixing && !s.Feed.Ended && s.Feed.Available < MinMixingFillFrames)
            {
                MinMixingFillFrames = s.Feed.Available;
            }
        }

        progress |= WatchStalls(now);
        if (!_worker.Idle)
        {
            return progress;
        }

        // prioritet: deck koji svira i prazni se, pa deck koji se priprema, pa zatvaranje starih izvora, pa nova pesma
        // bez LINQ-a: koordinator u stalnom radu ne zauzima memoriju
        Slot? refill = null;
        foreach (var s in _slots)
        {
            if ((s.State is State.Mixing or State.Published) && !s.SourceEnded && s.Source is not null
                && (s.Filling || s.Feed.Available < _refillBelow) && (refill is null || s.Feed.Available < refill.Feed.Available))
            {
                refill = s;
            }
        }

        if (refill is not null && refill.Feed.FreeFrames >= Options.ChunkFrames)
        {
            refill.Filling = true;
            Submit(WorkerJob.Decode(Array.IndexOf(_slots, refill), refill.Feed.Generation, refill.Source!, refill.Item?.Track.Path), now);
            return true;
        }

        if (refill is not null)
        {
            refill.Filling = false; // pun
        }

        Slot? preparing = null;
        foreach (var s in _slots)
        {
            if (s.State == State.Preparing && !s.SourceEnded && s.Source is not null && s.Feed.FreeFrames >= Options.ChunkFrames)
            {
                preparing = s;
                break;
            }
        }

        if (preparing is not null)
        {
            Submit(WorkerJob.Decode(Array.IndexOf(_slots, preparing), preparing.Feed.Generation, preparing.Source!, preparing.Item?.Track.Path), now);
            return true;
        }

        if (_toDispose.Count > 0)
        {
            Submit(WorkerJob.DisposeSource(_toDispose.Dequeue()), now);
            return true;
        }

        var pending = false;
        Slot? free = null;
        foreach (var s in _slots)
        {
            pending |= s.State is State.Preparing or State.Published;
            if (free is null && s.State == State.Free)
            {
                free = s;
            }
        }

        if (!pending && free is not null && now >= _nextPrepareAt && _plan.Count > 0)
        {
            var item = _plan[0];
            free.State = State.Preparing;
            free.Item = null;
            free.Planned = item;
            free.SourceEnded = false;
            free.Decoded = 0;
            free.Filling = false;
            free.Ticket = -1;
            free.PreparedSeq = -1;
            free.PreparedAt = -1;
            Submit(WorkerJob.Prepare(Array.IndexOf(_slots, free), free.Feed.Generation, free.OpenJob!), now);
            return true;
        }

        return progress;
    }

    private void Handle(WorkerResult result, long now)
    {
        ResetStall();
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
            var planned = slot.Planned;
            if (result.Track is not { } track)
            {
                if (result.Error is not null)
                {
                    _log("Priprema pesme nije uspela: " + result.Error.Message);
                }

                // pesma ne moze da se otvori (fajl nestao, menjan, dekoder): ispada iz plana bez traga u rotaciji.
                // Planer iz funkcije ima jednu stalnu stavku: samo se ponovo pokusava (bez novih objekata).
                if (planned is not null && _planner is not FuncPlanner)
                {
                    _plan.Remove(planned);
                    if (_planner is not FuncPlanner)
                    {
                        _planner.Release(planned);
                    }

                    PublishPlan();
                }

                if (_planner is FuncPlanner)
                {
                    _exhausted = true;
                    _nextPrepareAt = now + _retryFrames;
                }

                slot.Planned = null;
                slot.State = State.Free;
                return;
            }

            _exhausted = false;
            if (_planner is FuncPlanner && planned is not null)
            {
                _plan.Remove(planned); // jedna stavka; sledeca se bira posle
            }

            var number = long.TryParse(track.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (track.Source.Format != _format)
            {
                _log("Format izvora se razlikuje od formata engine-a: " + track.Id);
                _toDispose.Enqueue(track.Source);
                if (planned is not null)
                {
                    _plan.Remove(planned);
                    _planner.Release(planned);
                    PublishPlan();
                }

                slot.Planned = null;
                slot.State = State.Free;
                return;
            }

            slot.Source = track.Source;
            slot.Item = new PreparedTrack(track, slot.Feed, number);
            return;
        }

        // Decode
        if (slot.State is State.Free || slot.Source is null || slot.SourceEnded)
        {
            Interlocked.Increment(ref _lateChunks);
            return;
        }

        if (result.Frames > 0)
        {
            // posao se zadaje samo kad ima mesta za ceo deo; deo koji ne stane bio bi izgubljen zvuk
            var written = slot.Feed.Write(result.Buffer.AsSpan(0, result.Frames * 2));
            if (written < result.Frames)
            {
                _log($"GRESKA: deo zvuka nije stao u bafer decka ({result.Frames - written} frejmova).");
            }

            slot.Decoded += written;
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
            if (reason != StopReason.None && slot.Item is { } failed)
            {
                DecodeFailed?.Invoke(new DecodeFailure(failed.Track, reason, result.Error, slot.Decoded));
            }
        }
    }

    /// <summary>
    /// D8-1: pre objave se upisuje <c>pripremljena</c> (dobija karticu); pesma se objavljuje kad je zapis potvrdjen u
    /// dnevniku ili posle <see cref="CoordinatorOptions.DurableWaitSeconds"/>. Bez slobodne kartice se ceka.
    /// </summary>
    private bool TryPublish(Slot slot, long now)
    {
        var item = slot.Item!;
        if (slot.Ticket < 0)
        {
            var handle = Tickets.Plan(item.Track.PlaybackId, item.TrackNumber);
            if (handle < 0)
            {
                return false; // svih 16 kartica u upotrebi: koordinator jos nije prepisao zavrsene (B-210)
            }

            slot.Ticket = handle;
            item.Ticket = handle;
            _handles.Add(handle);
            var p = slot.Planned;
            slot.PreparedSeq = _history is IDurableEvents durable
                ? durable.PreparedDurable(item.Track.PlaybackId, item.TrackNumber, p?.Source ?? 0, p?.Part ?? 0, p?.Tier ?? 0, p?.Program)
                : Prepared(item, p);
            slot.PreparedAt = now;
        }

        var allowed = Options.DurableWaitSeconds <= 0
            || _history is not IDurableEvents
            || (_history is IDurableEvents d && d.IsConfirmed(slot.PreparedSeq))
            || now - slot.PreparedAt >= Frames(Options.DurableWaitSeconds);
        if (!allowed)
        {
            return false;
        }

        Tickets.MarkHanded(slot.Ticket);
        if (!Handoff.Publish(item))
        {
            return false;
        }

        slot.State = State.Published;
        return true;
    }

    private long Prepared(PreparedTrack item, PlannedItem? p)
    {
        _history?.Prepared(item.Track.PlaybackId, item.TrackNumber, p?.Source ?? 0, p?.Part ?? 0, p?.Tier ?? 0, p?.Program);
        return 0;
    }

    /// <summary>
    /// Plan (2.8): do 3 stavke, koliko stvarno ima dozvoljenih pesama. Stavka koja vise ne sme (licenca, fajl, drugi program)
    /// i jos nije krenula se odbacuje bez traga u rotaciji; plan se dopunjava.
    /// </summary>
    private bool MaintainPlan(long now)
    {
        var changed = false;
        if (_planner is not FuncPlanner && now >= _nextValidateAt)
        {
            _nextValidateAt = now + Frames(Options.ValidateEverySeconds);
            foreach (var slot in _slots)
            {
                if (slot.State is State.Preparing or State.Published && slot.Planned is { } inDeck && !_planner.IsValid(inDeck))
                {
                    if (slot.State == State.Published && slot.Item is { } published && !Handoff.Retract(published))
                    {
                        continue; // vec preuzeta: svira
                    }

                    if (_worker.CurrentJob is { } job && job.SlotIndex == Array.IndexOf(_slots, slot))
                    {
                        continue; // radna nit jos radi na njoj; odbacuje se u sledecem koraku
                    }

                    Discard(slot, StopReason.NotLoaded, releaseReservation: true);
                    changed = true;
                }
            }

            for (var i = _plan.Count - 1; i >= 0; i--)
            {
                var item = _plan[i];
                if (!InDeck(item) && !_planner.IsValid(item))
                {
                    _plan.RemoveAt(i);
                    _planner.Release(item);
                    changed = true;
                }
            }
        }

        var (size, reason) = _planner.Capacity();
        if (reason != _planReason)
        {
            _planReason = reason;
            changed = true;
        }
        while (_plan.Count < size && now >= _nextSelectAt)
        {
            _before.Clear();
            if (_playingId is not null)
            {
                _before.Add(_playingId);
            }

            foreach (var p in _plan)
            {
                _before.Add(p.TrackId);
            }

            var next = _planner.Select(_before);
            if (next is null)
            {
                _nextSelectAt = now + _retryFrames;
                if (_plan.Count == 0 && _planner is not FuncPlanner)
                {
                    _exhausted = !AnyLoaded();
                }

                break;
            }

            if (_planner is not FuncPlanner)
            {
                _exhausted = false;
            }

            _plan.Add(next);
            changed = true;
        }

        if (size == 0 && _plan.Count == 0 && _planner is not FuncPlanner)
        {
            _exhausted = !AnyLoaded();
        }

        if (changed)
        {
            PublishPlan();
        }

        return changed;
    }

    private bool InDeck(PlannedItem item)
    {
        foreach (var s in _slots)
        {
            if (ReferenceEquals(s.Planned, item))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyLoaded()
    {
        foreach (var s in _slots)
        {
            if (s.State is State.Preparing or State.Published)
            {
                return true;
            }
        }

        return false;
    }

    private void Submit(WorkerJob job, long now)
    {
        _jobStartedAt = now;
        _jobCpuStart = _worker is ThreadedWorker t && ThreadInspector is { } inspector && t.NativeId != 0 ? inspector.CpuTicks(t.NativeId) : -1;
        _stallEvaluated = false;
        _worker.Submit(job);
    }

    private void ResetStall()
    {
        _stallEvaluated = false;
        _probeResult = null;
        _stallStatus = null;
    }

    private long Frames(double seconds) => _format.FramesFromSeconds(seconds);

    /// <summary>
    /// Slucaj 2 (zaglavljen poziv) i spora pesma (plan 2.7). Blokiran poziv sam po sebi nikad ne dovodi do preskakanja:
    /// preskace se samo uz ishod probe "disk radi" (ili dekoder koji se vrti u krug). Ishodi "ne odgovara" i
    /// "neodredjeno" i nit bez procesora: ceka se, ista pesma nastavlja kad zvuk stigne.
    /// </summary>
    private bool WatchStalls(long now)
    {
        var stall = Options.Stall;
        if (_probe is { IsCompleted: true } done)
        {
            _probeResult = done.Result;
            _probe = null;
        }
        else if (_probe is not null && now - _probeStartedAt > Frames(stall.ProbeSeconds + 0.5))
        {
            _probeResult = DiskProbeOutcome.NotResponding; // proba sama stoji
        }

        if (_worker.CurrentJob is { Kind: WorkerJobKind.Decode or WorkerJobKind.Prepare } job && now - _jobStartedAt >= Frames(stall.BlockedCallSeconds))
        {
            if (!_stallEvaluated || now >= _nextStallCheckAt)
            {
                _stallEvaluated = true;
                _nextStallCheckAt = now + Frames(stall.ProbeRepeatSeconds);
                var state = _worker is ThreadedWorker t && ThreadInspector is { } inspector && t.NativeId != 0
                    ? inspector.Inspect(t.NativeId, _jobCpuStart, TimeSpan.FromSeconds((now - _jobStartedAt) / (double)_format.SampleRate), stall)
                    : ThreadInspector is { } fake ? fake.Inspect(0, _jobCpuStart, TimeSpan.FromSeconds((now - _jobStartedAt) / (double)_format.SampleRate), stall)
                    : WorkerThreadState.Undetermined;
                switch (state)
                {
                    case WorkerThreadState.Spinning:
                        AbandonStuck(job, "dekoder se vrti u krug");
                        return true;
                    case WorkerThreadState.Blocked:
                        StartProbe(job.Path, now);
                        break;
                    case WorkerThreadState.NoCpu:
                        _stallStatus = "ceka-procesor";
                        break;
                    default:
                        _stallStatus = "uzrok-nije-utvrdjen";
                        break;
                }
            }

            if (_probeResult is { } outcome)
            {
                _probeResult = null;
                switch (outcome)
                {
                    case DiskProbeOutcome.DiskWorks:
                        AbandonStuck(job, "zaglavljen dekoder (disk radi)");
                        return true;
                    case DiskProbeOutcome.NotResponding:
                        _stallStatus = "ceka-disk";
                        break;
                    default:
                        _stallStatus = "uzrok-nije-utvrdjen";
                        break;
                }
            }

            return false;
        }

        // Spora pesma: deck koji svira je prazan duze od T, a poziv dekodera se vraca.
        var nextReady = false;
        foreach (var s in _slots)
        {
            nextReady |= s.State == State.Published;
        }

        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (slot.State != State.Mixing || slot.SourceEnded || slot.Feed.Ended)
            {
                _starvedSince[i] = -1;
                continue;
            }

            if (slot.Feed.Available == 0 && _starvedSince[i] < 0)
            {
                _starvedSince[i] = now;
            }
            else if (slot.Feed.Available >= Frames(Options.ResumeSeconds))
            {
                _starvedSince[i] = -1;
            }

            if (_starvedSince[i] < 0 || now - _starvedSince[i] < Frames(stall.SlowTrackSeconds) || !nextReady)
            {
                continue;
            }

            if (_probeResult is null && _probe is null && now >= _nextStallCheckAt)
            {
                _nextStallCheckAt = now + Frames(stall.ProbeRepeatSeconds);
                StartProbe(slot.Item?.Track.Path, now);
            }

            if (_probeResult is DiskProbeOutcome.DiskWorks)
            {
                _probeResult = null;
                slot.SourceEnded = true;
                slot.Feed.MarkEnded(StopReason.DecoderStall);
                _starvedSince[i] = -1;
                return true;
            }

            if (_probeResult is { } other)
            {
                _probeResult = null;
                _stallStatus = other == DiskProbeOutcome.NotResponding ? "ceka-disk" : "uzrok-nije-utvrdjen";
            }
        }

        return false;
    }

    private void StartProbe(string? path, long now)
    {
        if (_probe is not null)
        {
            return;
        }

        if (DiskProbe is not { } probe)
        {
            _probeResult = DiskProbeOutcome.Undetermined;
            return;
        }

        var timeout = TimeSpan.FromSeconds(Options.Stall.ProbeSeconds);
        if (!_threaded)
        {
            _probeResult = probe.Probe(path, timeout);
            return;
        }

        _probeStartedAt = now;
        _probe = Task.Factory.StartNew(() => probe.Probe(path, timeout), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Zaglavljen dekoder: nit se napusta (poziv ne moze da se prekine), pravi se nova. Najvise 3 napustene;
    /// aktivna nit se nikad ne napusta kad vec postoje 3 – tada odmah kontrolisano ponovno pokretanje (D7-4).
    /// </summary>
    private void AbandonStuck(WorkerJob job, string why)
    {
        Interlocked.Increment(ref _stuckCalls);
        var slot = job.SlotIndex >= 0 ? _slots[job.SlotIndex] : null;
        if (slot?.Item is { } stuckItem)
        {
            _stuckTracks.Add(stuckItem.Track.Id);
        }

        _log($"Zastoj dekodera ({why}); napustenih niti: {_abandonedWorkers.Count}.");
        if (_abandonedWorkers.Count >= Options.Stall.MaxAbandoned)
        {
            if (!_restartRaised)
            {
                _restartRaised = true;
                RestartRequested?.Invoke(new RestartRequest(true, "zaglavila se i aktivna radna nit (vec 3 napustene)", [.. _stuckTracks]));
            }

            return;
        }

        _worker.Abandon();
        _abandonedWorkers.Add(_worker);
        _worker = NewWorker();
        if (_worker is ThreadedWorker fresh)
        {
            fresh.Start();
        }

        ResetStall();
        if (slot is not null && job.Generation == slot.Feed.Generation)
        {
            if (slot.State == State.Published && slot.Item is { } published && Handoff.Retract(published))
            {
                slot.State = State.Preparing;
            }

            if (slot.State == State.Mixing || slot.State == State.Published)
            {
                // zaglavljena pesma svira: odsvira se ono sto je u baferu, sa stisavanjem od 50 ms, pa sledeca
                slot.SourceEnded = true;
                slot.Source = null; // zatvara ga napustena nit kad se odblokira
                slot.Feed.MarkEnded(StopReason.DecoderStall);
            }
            else
            {
                // zaglavljena pesma se tek pripremala: odbacuje se i priprema se druga
                if (slot.Item is { } item)
                {
                    _history?.Discarded(item.Track.PlaybackId, item.TrackNumber, StopReason.DecoderStall);
                }

                slot.Source = null;
                slot.Item = null;
                slot.Feed.Reset();
                slot.State = State.Free;
            }
        }

        if (_abandonedWorkers.Count >= Options.Stall.MaxAbandoned)
        {
            _restartAtBoundary = true;
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

internal readonly record struct WorkerJob(WorkerJobKind Kind, int SlotIndex, int Generation, ISampleSource? Source, Func<QueuedTrack?>? Next, string? Path = null)
{
    public static WorkerJob Prepare(int slot, int generation, Func<QueuedTrack?> next) => new(WorkerJobKind.Prepare, slot, generation, null, next);

    public static WorkerJob Decode(int slot, int generation, ISampleSource source, string? path) => new(WorkerJobKind.Decode, slot, generation, source, null, path);

    public static WorkerJob DisposeSource(ISampleSource source) => new(WorkerJobKind.DisposeSource, -1, 0, source, null);
}

internal readonly record struct WorkerResult(WorkerJobKind Kind, int SlotIndex, int Generation, float[] Buffer, int Frames, bool Ended, Exception? Error, QueuedTrack? Track);

/// <summary>Radna nit za dekodiranje: otvara i dekodira u sopstveni bafer. Jedini deo koji sme da se zaglavi.</summary>
internal abstract class WorkerHost(int chunkFrames) : IDisposable
{
    private readonly object _sync = new();
    private WorkerResult _result;
    private volatile bool _hasResult;

    protected int ChunkFrames { get; } = chunkFrames;

    public abstract bool Busy { get; }

    /// <summary>Posao u toku (ili null).</summary>
    public abstract WorkerJob? CurrentJob { get; }

    /// <summary>Napustanje: nit vise ne predaje rezultate; kad se odblokira, zatvara izvor i zavrsava se.</summary>
    public abstract void Abandon();

    /// <summary>Nema posla u toku i svi rezultati su preuzeti (tek tada sme nov posao: bafer radne niti je jedan).</summary>
    public bool Idle => !Busy && !_hasResult;

    public abstract void Submit(WorkerJob job);

    public bool TryGetResult(out WorkerResult result)
    {
        lock (_sync)
        {
            result = _result;
            if (!_hasResult)
            {
                return false;
            }

            _hasResult = false;
            _result = default;
            return true;
        }
    }

    public virtual void Dispose()
    {
    }

    /// <summary>Jedan posao u isto vreme, pa je i rezultat najvise jedan.</summary>
    protected void Post(WorkerResult result)
    {
        lock (_sync)
        {
            _result = result;
            _hasResult = true;
        }
    }

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

    public override WorkerJob? CurrentJob => _job;

    public override void Submit(WorkerJob job) => _job = job;

    public override void Abandon() => _job = null; // bez niti: zaglavljen posao se samo odbacuje

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
    private readonly object _jobSync = new();
    private WorkerJob? _job;
    private volatile bool _stop;
    private volatile bool _abandoned;
    private volatile int _nativeId;

    public ThreadedWorker(int chunkFrames)
        : base(chunkFrames)
    {
        _buffer = new float[chunkFrames * 2];
        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.RadnaNitDekodera" };
    }

    public ManualResetEventSlim ResultReady { get; } = new();

    public override bool Busy
    {
        get
        {
            lock (_jobSync)
            {
                return _job is not null;
            }
        }
    }

    public override WorkerJob? CurrentJob
    {
        get
        {
            lock (_jobSync)
            {
                return _job;
            }
        }
    }

    /// <summary>Windows broj niti (za stanje niti i procesorsko vreme).</summary>
    public int NativeId => _nativeId;

    public void Start()
    {
        _thread.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (_nativeId == 0 && watch.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(1); // broj niti je potreban pre prvog posla (procesorsko vreme na pocetku poziva)
        }
    }

    public override void Abandon()
    {
        _abandoned = true;
        TryCancelIo(_nativeId); // samo dodatak; ne oslanjamo se na njega
    }

    public override void Submit(WorkerJob job)
    {
        lock (_jobSync)
        {
            _job = job;
        }

        _jobReady.Release();
    }

    public override void Dispose()
    {
        _stop = true;
        _jobReady.Release();
        if (!_abandoned)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static void TryCancelIo(int nativeId)
    {
        if (nativeId == 0)
        {
            return;
        }

        var handle = OpenThread(0x0001, false, (uint)nativeId); // THREAD_TERMINATE je potreban za CancelSynchronousIo
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = CancelSynchronousIo(handle);
        _ = CloseHandle(handle);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(int access, bool inherit, uint threadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CancelSynchronousIo(IntPtr thread);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private void Run()
    {
        _nativeId = (int)NativeThread.GetCurrentThreadId();
        while (true)
        {
            _jobReady.Wait();
            if (_stop)
            {
                return;
            }

            WorkerJob job;
            lock (_jobSync)
            {
                if (_job is not { } current)
                {
                    continue;
                }

                job = current;
            }

            var result = Execute(job, _buffer);
            if (_abandoned)
            {
                // napustena nit: rezultat se ne predaje; izvor se zatvara i nit se zavrsava
                try
                {
                    job.Source?.Dispose();
                    result.Track?.Source.Dispose();
                }
                catch (Exception)
                {
                    // neispravan izvor
                }

                return;
            }

            Post(result);
            lock (_jobSync)
            {
                _job = null;
            }

            ResultReady.Set();
        }
    }
}
