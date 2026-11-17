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

    /// <summary>Smart Crossfade (spec. 5.2): koristi analizu krajeva pesme kad postoji; inace obicno pretapanje.</summary>
    public bool SmartCrossfade { get; init; } = true;

    /// <summary>DJ Mix uzivo (spec. 5.3) gde program to trazi i analiza dozvoljava; inace Smart.</summary>
    public bool DjMix { get; init; } = true;

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

    /// <summary>Sledeca pesma iz plana (B4, B-30: posle ponovnog pokretanja ista pesma i ista sledeca).</summary>
    public string? NextTrackId { get; init; }
}

public sealed record NowPlaying(string TrackId, string Title, string Artist, string Program, DayPart? Part, bool Resumed, double DurationSeconds = 0);

/// <summary>
/// "Mozak" radija: na osnovu sata i rasporeda odlucuje da li se svira i koja pesma je sledeca.
/// Svaka pesma prolazi kapiju licenci (kroz katalog) i brzu proveru da fajl nije menjan.
/// </summary>
public sealed class RadioDirector : IPlanner
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
    private readonly Dictionary<string, DateTime> _quarantine = [];

    private WeeklySchedule _schedule;
    private ManualOverride? _override;
    private List<TrackInfo> _catalog = [];
    private Dictionary<string, TrackRecord> _records = [];
    private readonly Core.Voice.VoiceScheduler _voiceScheduler = new(new Random());

    /// <summary>Oznaka stavke plana koja je najava, ne pesma (ne ulazi u istoriju pesama ni u rotaciju).</summary>
    public const string VoicePrefix = "voice:";

    /// <summary>Biblioteka najava (spec. 15); null = bez najava.</summary>
    public VoiceLibrary? Voice { get; set; }

    /// <summary>Pravila emitovanja najava (podrazumevana iz specifikacije).</summary>
    public Core.Voice.VoiceRules VoiceRules { get; set; } = new();
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
                var nowUtc = now.ToUniversalTime();
                var available = _catalog.Select(t => _unplayable.Contains(t.Id) || (_quarantine.TryGetValue(t.Id, out var until) && until > nowUtc) ? t with { Broadcastable = false } : t).ToList();
                var pick = _rotation.Pick(available, _state, decision.Program, new DateTimeOffset(now), _options.Rotation);
                if (pick is null)
                {
                    _log("Nema nijedne pesme koja sme u eter.");
                    return null;
                }

                var record = _records[pick.Track.Id];
                var queued = TryOpen(record, startSeconds: 0);
                queued = queued is null ? null : queued with { Path = record.Path };
                if (queued is null)
                {
                    continue;
                }

                _state.Record(pick.Track, new DateTimeOffset(now));
                // B4: zapis "pripremljena" pise koordinator (pre objave), ne reditelj.
                queued = queued with { PlaybackId = Guid.CreateVersion7() };
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

        TrackRecord chosen;
        string program;
        DayPart? part;
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

            if (_quarantine.TryGetValue(snapshot.TrackId, out var quarantinedUntil) && quarantinedUntil > now.ToUniversalTime())
            {
                return null; // pesma u karantinu se ne nastavlja od snimka stanja
            }

            var info = _catalog.FirstOrDefault(t => t.Id == snapshot.TrackId);
            if (info is not { Broadcastable: true, Blocked: false } || record.DurationSeconds - snapshot.PositionSeconds < 2)
            {
                return null;
            }

            chosen = record;
            program = decision.Program?.Name ?? string.Empty;
            part = decision.Part;
        }

        // Fajl se otvara VAN brave reditelja: zaglavljen fajl ne sme da zaustavi raspored, stanje i komande.
        var queued = TryOpen(chosen, snapshot.PositionSeconds);
        if (queued is not null)
        {
            queued = queued with
            {
                Path = chosen.Path,
                PlaybackId = Guid.CreateVersion7(),
                Info = new NowPlaying(snapshot.TrackId, chosen.Title, chosen.Artist, program, part, Resumed: true, chosen.DurationSeconds),
            };
        }

        return queued;
    }

    /// <summary>
    /// Dekodiranje je prestalo pre kraja (slucaj 1, plan 2.7). Razvrstava gresku; u pozadini (snizen prioritet) proverava
    /// eksplicitnu gresku ili ponovo meri duzinu. U "3 greske" ulazi samo potvrdjena eksplicitna greska.
    /// </summary>
    public Task<FaultCheck?> HandleDecodeFailure(DecodeFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        TrackRecord? record;
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            _records.TryGetValue(failure.Track.Id, out record);
        }

        var kind = DecodeFailures.Classify(failure.Reason, failure.Error);
        if (record is null)
        {
            return Task.FromResult<FaultCheck?>(null);
        }

        _log($"Dekodiranje prestalo ({kind}, {failure.Reason}) na {failure.PositionFrames / (double)_format.SampleRate:F1} s: {record.Path}");
        if (kind is not (DecodeFailureKind.ExplicitDecoderError or DecodeFailureKind.UnexpectedEnd))
        {
            return Task.FromResult<FaultCheck?>(null);
        }

        var reopen = DecoderFactory ?? (r => Decoders.Open(r.Path, _format, r.DecoderId));
        return Task.Factory.StartNew<FaultCheck?>(
            () =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                if (kind == DecodeFailureKind.UnexpectedEnd)
                {
                    var measured = DecodeFailures.Measure(() => reopen(record), TimeSpan.FromMinutes(2));
                    if (measured >= 0 && Math.Abs(measured - failure.PositionFrames) <= _format.SampleRate / 10)
                    {
                        var native = record.SampleRate > 0 ? (long)Math.Round(measured * (double)record.SampleRate / _format.SampleRate) : measured;
                        _db.Tracks.SetExactFrames(record.Id, native);
                        _log($"Duzina iz skeniranja nije bila tacna; ispravljeno: {record.Path}");
                    }
                    else
                    {
                        _log($"Prerani kraj se nije ponovio: {record.Path}");
                    }

                    return null;
                }

                var check = DecodeFailures.Confirm(() => reopen(record), () => Unchanged(record), failure.Error!, failure.PositionFrames, _format, TimeSpan.FromSeconds(60));
                switch (check)
                {
                    case FaultCheck.Confirmed:
                        ReportFault(failure.Track.Id);
                        break;
                    case FaultCheck.FileChanged:
                        _log($"Fajl je menjan posle skeniranja; ne broji se, ponovo se skenira: {record.Path}");
                        AudioThreadGuard.Touch(AudioViolation.Lock);
                        lock (_sync)
                        {
                            _unplayable.Add(failure.Track.Id);
                        }

                        break;
                    default:
                        _log($"Neponovljena greska ({check}); ne broji se: {record.Path}");
                        break;
                }

                return check;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Samo za probni Engine (testovi sa pravim nadzorom, D8-6): poziva se pre otvaranja fajla. U programu je null.</summary>
    internal Action<string>? BeforeOpen { get; set; }

    /// <summary>Samo za testove: otvaranje fajla za proveru greske (u programu: isti dekoder kao pri pustanju).</summary>
    internal Func<TrackRecord, ISampleSource>? DecoderFactory { get; set; }

    private static bool Unchanged(TrackRecord record)
    {
        var info = new FileInfo(record.Path);
        return info.Exists && info.Length == record.SizeBytes && info.LastWriteTimeUtc == record.LastWriteUtc;
    }

    /// <summary>Putanje svih pesama (za probu diska sa istog volumena).</summary>
    public IReadOnlyList<string> LibraryPaths()
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            return [.. _records.Values.Select(r => r.Path)];
        }
    }

    /// <summary>Karantin (D8-4): pesme koje se ne planiraju do zadatog trenutka (do ponoci).</summary>
    public void SetQuarantine(IEnumerable<string> trackIds, DateTime untilUtc)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            foreach (var id in trackIds)
            {
                _quarantine[id] = untilUtc;
            }
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

    public void SaveSnapshot(string trackId, double positionSeconds, string? nextTrackId = null) =>
        _db.State.SetJson(SnapshotKey, new PlaybackSnapshot
        {
            TrackId = trackId,
            PositionSeconds = positionSeconds,
            SavedUtc = _clock.NowLocal.ToUniversalTime(),
            NextTrackId = nextTrackId,
        });

    /// <summary>B-30: posle ponovnog pokretanja prva stavka plana je ista sledeca kao pre (ako i dalje sme).</summary>
    public string? PreferredNext { get; set; }

    // ---------------- planer (B4): izbor na niti koordinatora, otvaranje na radnoj niti ----------------

    private sealed record VoiceTag(VoiceEntry Entry);

    private sealed record PlanTag(TrackRecord Record, RotationReservation Reservation, string? Program, bool Closing)
    {
        /// <summary>Pesma koja svira pre ove (za DJ Mix); null ako se ne zna.</summary>
        public string? PreviousTrackId { get; init; }

        public TransitionMode Transition { get; init; } = TransitionMode.Smart;
    }

    public (int Size, string? Reason) Capacity()
    {
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var now = _clock.NowLocal;
            var decision = ScheduleEngine.Evaluate(_schedule, _override, now);
            if (decision.Program is null && _lastOpenDecision is null)
            {
                return (0, "zatvoreno");
            }

            var nowUtc = now.ToUniversalTime();
            var allowed = _catalog.Count(t => t.Broadcastable && !t.Blocked && !_unplayable.Contains(t.Id) && !(_quarantine.TryGetValue(t.Id, out var u) && u > nowUtc));
            if (allowed >= 3)
            {
                return (3, null);
            }

            var reason = allowed == 0 ? "nema-pesama"
                : _unplayable.Count > 0 || _quarantine.Values.Any(u => u > nowUtc) ? "fajlovi"
                : "licenca";
            return (allowed, reason);
        }
    }

    public PlannedItem? Select(IReadOnlyList<string> before)
    {
        ArgumentNullException.ThrowIfNull(before);
        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var now = _clock.NowLocal;
            var decision = ScheduleEngine.Evaluate(_schedule, _override, now);
            var closing = false;
            if (decision.ShouldPlay)
            {
                _lastOpenDecision = decision;
            }
            else if (_lastOpenDecision is not null)
            {
                decision = _lastOpenDecision; // stisavanje posle zatvaranja: jos pesama iz poslednjeg programa
                closing = true;
            }

            if (decision.Program is null)
            {
                return null;
            }

            var nowUtc = now.ToUniversalTime();
            bool Allowed(TrackInfo t) => t.Broadcastable && !t.Blocked && !_unplayable.Contains(t.Id) && !(_quarantine.TryGetValue(t.Id, out var u) && u > nowUtc);
            var allowedCount = _catalog.Count(Allowed);
            var last = before.Count > 0 ? before[^1] : null;

            // razlicite pesme kad ih ima dovoljno; inace bar ne ista dvaput zaredom (osim kad postoji samo jedna)
            bool Excluded(string id) => allowedCount > before.Count ? before.Contains(id) : allowedCount > 1 && id == last;
            var available = _catalog.Select(t => !Allowed(t) || Excluded(t.Id) ? t with { Broadcastable = false } : t).ToList();

            // najava na granici izmedju dve pesme (nikad usred pesme, nikad dve zaredom, nikad pri zatvaranju)
            if (!closing && Voice is { } voice && last is not null && !last.StartsWith(VoicePrefix, StringComparison.Ordinal) && SelectVoice(voice, now, decision) is { } voiceItem)
            {
                return voiceItem;
            }

            TrackInfo? chosen = null;
            var tier = Core.Rotation.MatchTier.Program;
            if (PreferredNext is { } preferred)
            {
                PreferredNext = null;
                chosen = available.FirstOrDefault(t => t.Id == preferred && t.Broadcastable);
            }

            if (chosen is null)
            {
                var pick = _rotation.Pick(available, _state, decision.Program, new DateTimeOffset(now), _options.Rotation);
                if (pick is null)
                {
                    return null;
                }

                chosen = pick.Track;
                tier = pick.Tier;
            }

            var record = _records[chosen.Id];
            var reservation = _state.Record(chosen, new DateTimeOffset(now));
            return new PlannedItem
            {
                TrackId = chosen.Id,
                Title = record.Title,
                Artist = record.Artist,
                Source = (byte)((int)decision.Source + 1),
                Part = decision.Part is { } part ? (byte)((int)part + 1) : (byte)0,
                Tier = (byte)((int)tier + 1),
                Program = decision.Program.Name,
                Path = record.Path,
                Tag = new PlanTag(record, reservation, decision.Program.Name, closing) { PreviousTrackId = last, Transition = decision.Program.Transition },
            };
        }
    }

    private PlannedItem? SelectVoice(VoiceLibrary voice, DateTime now, ScheduleDecision decision)
    {
        var part = decision.Part ?? DayPart.Morning;
        var open = _schedule.Days.TryGetValue(now.DayOfWeek, out var day) ? day.Times.Open : new TimeOnly(7, 0);
        var boundary = new Core.Voice.VoiceBoundary
        {
            NowLocal = now,
            Part = part,
            OpenedAtLocal = now.Date + open.ToTimeSpan(),
            ClosesAtLocal = decision.ClosesAtLocal ?? now.AddHours(1),
            OverrideActive = _override is not null,
        };
        var choice = _voiceScheduler.OnBoundary(boundary, voice.Broadcastable(), VoiceRules);
        if (choice.Message is not { } message || voice.Find(message.Id) is not { } entry)
        {
            return null;
        }

        _voiceScheduler.RecordPlayed(message, now, VoiceRules, part); // planirana = pustena (plan je kratak)
        return new PlannedItem
        {
            TrackId = VoicePrefix + entry.Id,
            Title = "Najava",
            Artist = entry.Text,
            Source = (byte)((int)decision.Source + 1),
            Part = (byte)((int)part + 1),
            Program = decision.Program?.Name,
            Path = voice.PathOf(entry),
            Tag = new VoiceTag(entry),
        };
    }

    /// <summary>Najava: ide posle prirodnog kraja pesme (Break), glasnija od muzike za 1,5 LU, bez preklapanja sa sledecom.</summary>
    private QueuedTrack? OpenVoice(PlannedItem item, VoiceEntry entry)
    {
        if (Voice is not { } voice || !voice.CanBroadcast(entry))
        {
            return null;
        }

        try
        {
            var source = Decoders.Open(voice.PathOf(entry), _format);
            var settings = _options.Loudness with { TargetLufs = _options.Loudness.TargetLufs + VoiceLibrary.LouderThanMusicLu };
            var gain = TrackGain.ToLinear(TrackGain.ComputeDb(entry.Lufs, entry.PeakDbfs, settings));
            return new QueuedTrack(item.TrackId, source, gain)
            {
                Break = true,
                TransitionSeconds = 0.05,
                PlaybackId = item.PlaybackId,
                Path = voice.PathOf(entry),
                Info = new NowPlaying(item.TrackId, "Najava", entry.Text, item.Program ?? string.Empty, null, Resumed: false, entry.DurationSeconds),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            _log($"Najava {entry.Id} ne moze da se pusti: {ex.Message}. Muzika ide dalje.");
            return null;
        }
    }

    public QueuedTrack? Open(PlannedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Tag is VoiceTag voiceTag)
        {
            return OpenVoice(item, voiceTag.Entry);
        }

        if (item.Tag is not PlanTag tag)
        {
            return null;
        }

        // Fajl se otvara VAN brave reditelja (radna nit): zaglavljen fajl ne sme da zaustavi raspored, stanje,
        // komande i koordinatora; zastoj tada otkriva koordinator (T2, proba diska) i radna nit se napusta.
        var queued = TryOpen(tag.Record, startSeconds: 0);
        if (queued is null)
        {
            return null;
        }

        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var decision = ScheduleEngine.Evaluate(_schedule, _override, _clock.NowLocal);
            DjCue? cue = null;
            if (_options.DjMix && _options.SmartCrossfade && tag.PreviousTrackId is { } previous && _records.TryGetValue(previous, out var before))
            {
                cue = DjCuePlanner.Plan(tag.Transition, before, tag.Record, _format);
            }

            return queued with
            {
                Cue = cue,
                PlaybackId = item.PlaybackId,
                Path = tag.Record.Path,
                Info = new NowPlaying(item.TrackId, tag.Record.Title, tag.Record.Artist, tag.Program ?? string.Empty, decision.Part, Resumed: false, tag.Record.DurationSeconds),
            };
        }
    }

    public void Release(PlannedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Tag is PlanTag tag)
        {
            AudioThreadGuard.Touch(AudioViolation.Lock);
            lock (_sync)
            {
                _state.Unrecord(tag.Reservation);
            }
        }
    }

    public bool IsValid(PlannedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Tag is VoiceTag voiceTag)
        {
            return Voice is { } voice && voice.Find(voiceTag.Entry.Id) is { } current && voice.CanBroadcast(current);
        }

        AudioThreadGuard.Touch(AudioViolation.Lock);
        lock (_sync)
        {
            var now = _clock.NowLocal;
            var nowUtc = now.ToUniversalTime();
            var info = _catalog.FirstOrDefault(t => t.Id == item.TrackId);
            if (info is not { Broadcastable: true, Blocked: false } || _unplayable.Contains(item.TrackId)
                || (_quarantine.TryGetValue(item.TrackId, out var until) && until > nowUtc))
            {
                return false;
            }

            var decision = ScheduleEngine.Evaluate(_schedule, _override, now);
            if (!decision.ShouldPlay)
            {
                return true; // stisavanje posle zatvaranja: plan iz poslednjeg programa vazi
            }

            return item.Tag is not PlanTag tag || tag.Closing || decision.Program?.Name == tag.Program;
        }
    }

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
                MarkUnplayable(id);
                return null;
            }

            BeforeOpen?.Invoke(record.Path);
            var source = Decoders.Open(record.Path, _format, record.DecoderId);
            try
            {
                var total = record.ExactFrames > 0 && record.SampleRate > 0
                    ? (long)Math.Round(record.ExactFrames * (double)_format.SampleRate / record.SampleRate)
                    : source.LengthFrames ?? 0;
                long skip = 0;

                // Smart Crossfade (spec. 5.2, signal 1): tisina na pocetku i kraju se ne pusta. Bez analize: cela pesma.
                var smart = _options.SmartCrossfade && record.EdgesVersion > 0 && record.SampleRate > 0 && record.AudioEndFrame is not null && total > 0;
                if (smart)
                {
                    total = Math.Min(total, (long)Math.Round(record.AudioEndFrame!.Value * (double)_format.SampleRate / record.SampleRate));
                    if (startSeconds <= 0 && record.AudioStartFrame is long audioStart && audioStart > 0)
                    {
                        skip = Math.Min(total - 1, (long)Math.Round(audioStart * (double)_format.SampleRate / record.SampleRate));
                    }
                }

                if (startSeconds > 0)
                {
                    skip = _format.FramesFromSeconds(startSeconds);
                }

                if (skip > 0)
                {
                    source.ResumeAt(skip);
                }

                ISampleSource shaped = total > 0 ? new ExactLengthSource(source, Math.Max(1, total - skip)) : source;
                var gain = TrackGain.ToLinear(TrackGain.ComputeDb(record.Lufs, record.PeakDbfs, _options.Loudness));

                // signal 2: pesma koja se sama stisava pretapa se tokom svog stisavanja (3-10 s); nagao kraj: obicno pretapanje
                return new QueuedTrack(id, shaped, gain) { TransitionSeconds = smart ? record.OutroSeconds : null, StartSkipFrames = skip };
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
            MarkUnplayable(id);
            return null;
        }
    }

    private void MarkUnplayable(string id)
    {
        lock (_sync)
        {
            _unplayable.Add(id);
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
