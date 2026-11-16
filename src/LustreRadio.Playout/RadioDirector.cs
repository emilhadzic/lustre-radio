using System.Globalization;
using LustreRadio.Audio;
using LustreRadio.Core.Diagnostics;
using LustreRadio.Core.History;
using LustreRadio.Core.Loudness;
using LustreRadio.Core.Rotation;
using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Time;
using LustreRadio.Data;

namespace LustreRadio.Playout;

public sealed record DirectorOptions
{
    public double CrossfadeSeconds { get; init; } = 6;

    /// <summary>Ulazak zvuka pri nastavku pesme posle restarta.</summary>
    public double ResumeFadeInSeconds { get; init; } = 3;

    /// <summary>Stariji snimak stanja se ne koristi: krece nova pesma.</summary>
    public TimeSpan SnapshotMaxAge { get; init; } = TimeSpan.FromMinutes(10);

    public LoudnessSettings Loudness { get; init; } = new();

    public RotationSettings Rotation { get; init; } = new();

    public int MaxOpenAttempts { get; init; } = 5;

    /// <summary>Priprema zvuka: bafer decka, N2 (plan faze B, 2.1 i 2.7).</summary>
    public CoordinatorOptions Coordinator { get; init; } = new();
}

/// <summary>Sta je sviralo i dokle je stiglo; upisuje se na svakih nekoliko sekundi.</summary>
public sealed record PlaybackSnapshot
{
    public required string TrackId { get; init; }

    public required double PositionSeconds { get; init; }

    public required DateTime SavedUtc { get; init; }
}

public sealed record NowPlaying(string TrackId, string Title, string Artist, string Program, DayPart? Part, bool Resumed, double DurationSeconds = 0);

/// <summary>
/// "Mozak" radija: na osnovu sata i rasporeda odlucuje da li se svira i koja pesma je sledeca.
/// Svaka pesma prolazi kapiju licenci (kroz katalog) i brzu proveru da fajl nije menjan.
/// </summary>
public sealed class RadioDirector
{
    public const string ScheduleKey = "schedule";
    public const string SnapshotKey = "snapshot";

    private readonly object _sync = new();
    private readonly LustreDatabase _db;
    private readonly IClock _clock;
    private readonly AudioFormat _format;
    private readonly RotationEngine _rotation;
    private readonly DirectorOptions _options;
    private readonly Action<string> _log;
    private readonly RotationState _state = new();
    private readonly HashSet<string> _unplayable = [];

    private WeeklySchedule _schedule;
    private ManualOverride? _override;
    private List<TrackInfo> _catalog = [];
    private Dictionary<string, TrackRecord> _records = [];
    private DateTime _cacheFrom = DateTime.MaxValue;
    private DateTime _cacheUntil = DateTime.MinValue;
    private bool _cacheShouldPlay;
    private ScheduleDecision? _lastOpenDecision;

    public RadioDirector(LustreDatabase db, IClock clock, AudioFormat format, Random random, DirectorOptions? options = null, Action<string>? log = null, IPlaybackEvents? history = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        History = history;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _format = format;
        _rotation = new RotationEngine(random);
        _options = options ?? new DirectorOptions();
        var inner = log ?? (_ => { });
        _log = message =>
        {
            AudioThreadGuard.Touch(AudioViolation.Log);
            inner(message);
        };
        _schedule = db.Settings.GetJson<WeeklySchedule>(ScheduleKey) ?? DefaultSchedule.Create();
        ReloadCatalog();
        LoadHistory();
    }

    public DirectorOptions Options => _options;

    /// <summary>Istorija emitovanja (B2); null u testovima koji je ne proveravaju.</summary>
    public IPlaybackEvents? History { get; }

    /// <summary>Poziva se kad nova pesma stvarno krene (sa niti koordinatora, nikad sa audio-niti; mora biti brzo).</summary>
    public event Action<NowPlaying>? TrackStarted;

    /// <summary>Koordinator javlja da je pripremljena pesma krenula.</summary>
    public void NotifyStarted(object? info)
    {
        if (info is NowPlaying playing)
        {
            TrackStarted?.Invoke(playing);
        }
    }

    public WeeklySchedule Schedule
    {
        get
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                return _schedule;
            }
        }

        set
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                _schedule = value ?? throw new ArgumentNullException(nameof(value));
                _cacheUntil = DateTime.MinValue;
            }
        }
    }

    public ManualOverride? Override
    {
        get
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                return _override;
            }
        }

        set
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                _override = value;
                _cacheUntil = DateTime.MinValue;
            }
        }
    }

    /// <summary>Broj pesama koje trenutno smeju u eter.</summary>
    public int BroadcastableCount
    {
        get
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                return _catalog.Count(t => t.Broadcastable && !t.Blocked);
            }
        }
    }

    /// <summary>Ponovo cita pesme i licence iz baze (posle skeniranja ili izmene licenci).</summary>
    public void ReloadCatalog()
    {
        var tracks = _db.Tracks.GetAll();
        var catalog = TrackCatalog.Build(tracks, _db.Licenses.GetAll()).ToList();
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            _records = tracks.ToDictionary(t => t.Id.ToString(CultureInfo.InvariantCulture));
            _catalog = catalog;
            _unplayable.Clear();
        }
    }

    /// <summary>Odluka rasporeda za ovaj trenutak (za prikaz: kada se zatvara, kada se otvara).</summary>
    public ScheduleDecision CurrentDecision()
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            return ScheduleEngine.Evaluate(_schedule, _override, _clock.NowLocal);
        }
    }

    public int TotalCount
    {
        get
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                return _catalog.Count;
            }
        }
    }

    /// <summary>Da li radio u ovom trenutku treba da svira. Jeftino: racuna raspored samo kad se nesto menja.</summary>
    public bool ShouldPlayNow()
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var now = _clock.NowLocal;
            if (now >= _cacheFrom && now < _cacheUntil)
            {
                return _cacheShouldPlay;
            }

            var decision = ScheduleEngine.Evaluate(_schedule, _override, now);
            _cacheFrom = now;
            _cacheUntil = decision.NextChangeLocal;
            _cacheShouldPlay = decision.ShouldPlay;
            return decision.ShouldPlay;
        }
    }

    /// <summary>
    /// Sledeca pesma po rasporedu i rotaciji, ili null ako se ne svira ili nema nicega dozvoljenog.
    /// closing = true: emitovanje se upravo stisava posle zatvaranja; muzika mora da traje
    /// do kraja stisavanja, pa se bira jos pesama iz poslednjeg programa.
    /// </summary>
    public QueuedTrack? NextTrack(bool closing = false)
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var now = _clock.NowLocal;
            var decision = ScheduleEngine.Evaluate(_schedule, _override, now);
            if (decision.ShouldPlay)
            {
                _lastOpenDecision = decision;
            }
            else if (closing && _lastOpenDecision is not null)
            {
                decision = _lastOpenDecision;
            }

            if (decision.Program is null)
            {
                return null;
            }

            for (var attempt = 0; attempt < _options.MaxOpenAttempts; attempt++)
            {
                var available = _catalog.Select(t => _unplayable.Contains(t.Id) ? t with { Broadcastable = false } : t).ToList();
                var pick = _rotation.Pick(available, _state, decision.Program, new DateTimeOffset(now), _options.Rotation);
                if (pick is null)
                {
                    _log("Nema nijedne pesme koja sme u eter.");
                    return null;
                }

                var record = _records[pick.Track.Id];
                var queued = TryOpen(record, startSeconds: 0);
                if (queued is null)
                {
                    continue;
                }

                _state.Record(pick.Track, new DateTimeOffset(now));
                // B2: istorija vise ne ide u bazu odavde; zapis "pripremljena" ide u crnu kutiju, a bazu puni pisar.
                queued = queued with { PlaybackId = Guid.CreateVersion7() };
                History?.Prepared(queued.PlaybackId, record.Id, (byte)((int)decision.Source + 1), decision.Part is { } part ? (byte)((int)part + 1) : (byte)0, (byte)((int)pick.Tier + 1), decision.Program.Name);
                // B3: pesma se priprema unapred; "sta svira" se javlja tek kad stvarno krene (NotifyStarted).
                return queued with { Info = new NowPlaying(pick.Track.Id, record.Title, record.Artist, decision.Program.Name, decision.Part, Resumed: false, record.DurationSeconds) };
            }

            _log("Vise pesama zaredom nije moglo da se otvori.");
            return null;
        }
    }

    /// <summary>
    /// Nastavak pesme posle restarta, ako je snimak stanja svez, radio treba da svira
    /// i pesma i dalje sme u eter. Inace null (krece nova pesma).
    /// </summary>
    public QueuedTrack? TryResume(PlaybackSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var now = _clock.NowLocal;
            var age = now.ToUniversalTime() - snapshot.SavedUtc;
            if (age < TimeSpan.Zero || age > _options.SnapshotMaxAge)
            {
                return null;
            }

            var decision = ScheduleEngine.Evaluate(_schedule, _override, now);
            if (!decision.ShouldPlay || !_records.TryGetValue(snapshot.TrackId, out var record))
            {
                return null;
            }

            var info = _catalog.FirstOrDefault(t => t.Id == snapshot.TrackId);
            if (info is not { Broadcastable: true, Blocked: false } || record.DurationSeconds - snapshot.PositionSeconds < 2)
            {
                return null;
            }

            var queued = TryOpen(record, snapshot.PositionSeconds);
            if (queued is not null)
            {
                queued = queued with { PlaybackId = Guid.CreateVersion7() };
                History?.Prepared(queued.PlaybackId, record.Id, (byte)((int)decision.Source + 1), decision.Part is { } part ? (byte)((int)part + 1) : (byte)0, 0, decision.Program?.Name);
                queued = queued with { Info = new NowPlaying(snapshot.TrackId, record.Title, record.Artist, decision.Program?.Name ?? string.Empty, decision.Part, Resumed: true, record.DurationSeconds) };
            }

            return queued;
        }
    }

    /// <summary>Pesma je pukla tokom pustanja: belezi se greska; posle tri izlazi iz rotacije.</summary>
    public void ReportFault(string trackId)
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            if (_records.TryGetValue(trackId, out var record))
            {
                var faults = _db.Tracks.RecordFault(record.Id);
                _log($"Greska pri pustanju ({faults}. put): {record.Path}");
                if (faults >= TrackCatalog.QuarantineAfterFaults)
                {
                    _unplayable.Add(trackId);
                }
            }
        }
    }

    public void SaveSnapshot(string trackId, double positionSeconds) =>
        _db.State.SetJson(SnapshotKey, new PlaybackSnapshot
        {
            TrackId = trackId,
            PositionSeconds = positionSeconds,
            SavedUtc = _clock.NowLocal.ToUniversalTime(),
        });

    public PlaybackSnapshot? LoadSnapshot() => _db.State.GetJson<PlaybackSnapshot>(SnapshotKey);

    private QueuedTrack? TryOpen(TrackRecord record, double startSeconds)
    {
        var id = record.Id.ToString(CultureInfo.InvariantCulture);
        try
        {
            // Brza provera pre pustanja: licenca vazi za tacno onaj fajl koji je skeniran.
            AudioThreadGuard.Touch(AudioViolation.FileAccess);
            var info = new FileInfo(record.Path);
            if (!info.Exists || info.Length != record.SizeBytes || info.LastWriteTimeUtc != record.LastWriteUtc)
            {
                _log($"Fajl je nestao ili je menjan posle skeniranja; ne pusta se dok se ponovo ne skenira: {record.Path}");
                _unplayable.Add(id);
                return null;
            }

            var source = Decoders.Open(record.Path, _format, record.DecoderId);
            try
            {
                var total = record.ExactFrames > 0 && record.SampleRate > 0
                    ? (long)Math.Round(record.ExactFrames * (double)_format.SampleRate / record.SampleRate)
                    : source.LengthFrames ?? 0;
                long skip = 0;
                if (startSeconds > 0)
                {
                    skip = _format.FramesFromSeconds(startSeconds);
                    source.ResumeAt(skip);
                }

                ISampleSource shaped = total > 0 ? new ExactLengthSource(source, total - skip) : source;
                var gain = TrackGain.ToLinear(TrackGain.ComputeDb(record.Lufs, record.PeakDbfs, _options.Loudness));
                return new QueuedTrack(id, shaped, gain);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            var faults = _db.Tracks.RecordFault(record.Id);
            _log($"Ne moze da se otvori ({faults}. put): {record.Path}: {ex.Message}");
            _unplayable.Add(id);
            return null;
        }
    }

    private void LoadHistory()
    {
        var since = _clock.NowLocal.ToUniversalTime() - _options.Rotation.TrackGap - TimeSpan.FromDays(1);
        var byId = _catalog.ToDictionary(t => t.Id);
        foreach (var play in _db.History.GetSince(since))
        {
            if (byId.TryGetValue(play.TrackId.ToString(CultureInfo.InvariantCulture), out var info))
            {
                _state.Record(info, new DateTimeOffset(play.StartedUtc));
            }
        }
    }
}
