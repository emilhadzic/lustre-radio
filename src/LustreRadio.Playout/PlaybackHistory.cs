using LustreRadio.Core.History;
using LustreRadio.Data;

namespace LustreRadio.Playout;

public sealed record PlaybackHistoryOptions
{
    public int Capacity { get; init; } = BlackBox.DefaultCapacity;

    /// <summary>Koliko cesto pisar gleda crnu kutiju.</summary>
    public TimeSpan ScribePoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan GuardInterval { get; init; } = BlackBoxGuard.DefaultInterval;

    /// <summary>Samo za test: pisar se ne pokrece ("pisar ne radi").</summary>
    internal bool StartScribe { get; init; } = true;

    internal HistoryFaults? Faults { get; init; }

    internal Func<DateTime>? UtcNow { get; init; }
}

/// <summary>Sta je oporavak pri pokretanju zatekao i uradio (za dnevnik programa i testove).</summary>
public sealed record HistoryRecovery
{
    public int FromBlackBox { get; init; }

    public int InvalidBlackBoxRecords { get; init; }

    public int JournalRecords { get; init; }

    public int InvalidJournalLines { get; init; }

    public bool TornTailTruncated { get; init; }

    public int ProgramCrashes { get; init; }

    public int Unconfirmed { get; init; }

    public long Lost { get; init; }

    public int DatabaseRows { get; init; }

    public string? Archived { get; init; }

    public DateTime? LostFromUtc { get; init; }

    public DateTime? LostToUtc { get; init; }

    public bool DatabaseComplete { get; init; }
}

/// <summary>
/// Istorija emitovanja (plan faze B, korak B2): crna kutija + cuvar + pisar + dnevnik + oporavak pri pokretanju.
/// Reditelj i sekvencer javljaju dogadjaje preko <see cref="IPlaybackEvents"/>; svaki poziv samo upisuje zapis
/// u crnu kutiju. Upis u dnevnik i bazu radi pisar, na svojoj niti.
/// </summary>
public sealed class PlaybackHistory : IPlaybackEvents, IDisposable
{
    public const string JournalFolder = "journal";

    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly BlackBoxGuard _guard;
    private readonly BlackBox? _fileBoxKeptForNextStart;
    private readonly LustreDatabase _db;
    private bool _closed;

    private PlaybackHistory(LustreDatabase db, BlackBox box, PlayJournal journal, Scribe scribe, BlackBoxGuard guard, Action<string> log, Func<DateTime> utcNow, HistoryRecovery recovery, BlackBox? keptBox)
    {
        _db = db;
        Box = box;
        Journal = journal;
        Scribe = scribe;
        _guard = guard;
        _log = log;
        _utcNow = utcNow;
        RecoveryResult = recovery;
        _fileBoxKeptForNextStart = keptBox;
    }

    public BlackBox Box { get; }

    public PlayJournal Journal { get; }

    public Scribe Scribe { get; }

    public Guid Run => Box.Run;

    /// <summary>Crna kutija je fajl mapiran u memoriju (inace crveno "Crna kutija nedostupna").</summary>
    public bool BlackBoxAvailable => Box.Available;

    /// <summary>Upozorenje "manje od 200 MB na disku".</summary>
    public bool LowSpace
    {
        get
        {
            var free = Journal.FreeSpace();
            return free >= 0 && free < PlayJournal.LowSpaceBytes;
        }
    }

    public static PlaybackHistory Open(string dataRoot, LustreDatabase db, Action<string>? log = null, PlaybackHistoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        options ??= new PlaybackHistoryOptions();
        var say = log ?? (_ => { });
        var utcNow = options.UtcNow ?? (() => DateTime.UtcNow);
        var folder = Path.Combine(dataRoot, JournalFolder);
        var journal = new PlayJournal(folder) { Faults = options.Faults?.Journal };
        var box = BlackBox.Open(Path.Combine(folder, BlackBox.FileName), options.Capacity, say);
        BlackBox? kept = null;
        var recovery = Recover(box, journal, say, options.Faults, out var ready);
        if (!ready)
        {
            // Dnevnik ne moze da se pise: stari sadrzaj crne kutije se cuva za sledece pokretanje; radi se sa redom u memoriji.
            kept = box;
            box = BlackBox.InMemory(options.Capacity, "dnevnik nije mogao da preuzme staru crnu kutiju");
        }

        var scribe = new Scribe(box, journal, db, say, utcNow, options.ScribePoll, options.Faults);
        var history = new PlaybackHistory(db, box, journal, scribe, new BlackBoxGuard(box, options.GuardInterval, say), say, utcNow, recovery, kept);
        history.AppendRecoveryRecords(recovery);
        recovery = history.RecoveryResult;
        if (options.StartScribe)
        {
            scribe.Start();
        }

        history._guard.Start();
        say($"Istorija: crna kutija {(box.Available ? "u fajlu" : "NEDOSTUPNA (" + box.UnavailableReason + ")")}; iz crne kutije {recovery.FromBlackBox}, " +
            $"prekida programa {recovery.ProgramCrashes}, nepotvrdjenih {recovery.Unconfirmed}, izgubljeno {recovery.Lost}, neispravnih redova {recovery.InvalidJournalLines}.");
        return history;
    }

    public void Prepared(Guid playbackId, long trackId, byte source, byte part, byte tier, string? program)
    {
        var record = new JournalRecord { Kind = JournalKind.Prepared, PlaybackId = playbackId, Track = trackId, Source = source, Part = part, Tier = tier, Program = program, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
    }

    public void Started(Guid playbackId, long trackId, long frame)
    {
        var record = new JournalRecord { Kind = JournalKind.Started, PlaybackId = playbackId, Track = trackId, Frame = frame, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
    }

    public void Crossfade(Guid playbackIdB, long trackIdB, Guid playbackIdA, long trackIdA, long frame)
    {
        var record = new JournalRecord { Kind = JournalKind.Crossfade, PlaybackId = playbackIdB, Track = trackIdB, PlaybackIdA = playbackIdA, TrackA = trackIdA, Frame = frame, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
    }

    public void Ended(Guid playbackId, long trackId, long frame, int positionMs)
    {
        var record = new JournalRecord { Kind = JournalKind.Ended, PlaybackId = playbackId, Track = trackId, Frame = frame, PositionMs = positionMs, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
    }

    public void Stopped(Guid playbackId, long trackId, StopReason reason, long frame, int positionMs)
    {
        var record = new JournalRecord { Kind = JournalKind.Stopped, PlaybackId = playbackId, Track = trackId, Reason = reason, Frame = frame, PositionMs = positionMs, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
    }

    /// <summary>Karantin do ponoci (upisuje se u dnevnik, pa vazi i posle ponovnog pokretanja).</summary>
    public void Quarantine(long trackId, DateTime untilUtc)
    {
        var record = new JournalRecord { Kind = JournalKind.Quarantine, Track = trackId, Frame = untilUtc.Ticks, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
        lock (_quarantine)
        {
            _quarantine[trackId] = untilUtc;
        }
    }

    /// <summary>Pesme u karantinu u ovom trenutku (iz dnevnika i iz ovog pokretanja).</summary>
    public IReadOnlyDictionary<long, DateTime> ActiveQuarantine(DateTime nowUtc)
    {
        lock (_quarantine)
        {
            return _quarantine.Where(q => q.Value > nowUtc).ToDictionary(q => q.Key, q => q.Value);
        }
    }

    private readonly Dictionary<long, DateTime> _quarantine = [];

    public void Discarded(Guid playbackId, long trackId, StopReason reason)
    {
        var record = new JournalRecord { Kind = JournalKind.Discarded, PlaybackId = playbackId, Track = trackId, Reason = reason, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
    }

    /// <summary>Uredno gasenje: zapis <c>gasenje</c>, pisar upisuje sve, zatim se sve zatvara.</summary>
    public void Shutdown(StopReason reason = StopReason.Shutdown)
    {
        if (_closed)
        {
            return;
        }

        var record = new JournalRecord { Kind = JournalKind.Shutdown, Reason = reason, TimeTicks = _utcNow().Ticks };
        Box.TryAppend(ref record);
        if (!Scribe.DrainNow(TimeSpan.FromSeconds(5)))
        {
            _log("Pri gasenju nije sve iz crne kutije stiglo u dnevnik; ostatak se oporavlja pri sledecem pokretanju.");
        }

        Close();
    }

    public void Dispose() => Shutdown();

    /// <summary>Samo za test: oponasa pad procesa (niti staju, nista se vise ne upisuje, fajlovi ostaju kakvi jesu).</summary>
    internal void Abandon()
    {
        _closed = true;
        Scribe.Abandon();
        _guard.Dispose();
        Journal.Dispose();
    }

    private void Close()
    {
        _closed = true;
        Scribe.Dispose();
        _guard.Dispose();
        Journal.Dispose();
        Box.Dispose();
        _fileBoxKeptForNextStart?.Dispose();
    }

    private static HistoryRecovery Recover(BlackBox box, PlayJournal journal, Action<string> log, HistoryFaults? faults, out bool ready)
    {
        var newRun = Guid.CreateVersion7();
        var found = box.ReadForRecovery();
        var read = journal.ReadAll();
        if (read.TornTailTruncated)
        {
            log("Dnevnik emitovanja: presecen poslednji red je preskocen; fajl je skracen do poslednjeg ispravnog reda.");
        }

        if (read.InvalidLines > 0)
        {
            log($"Dnevnik emitovanja: {read.InvalidLines} neispravnih redova je preskoceno.");
        }

        // 1. Sve ispravno iz crne kutije posle potvrdjeno_do ide u dnevnik, sa istim rid (ponovljen rid se uzima jednom).
        var known = new HashSet<(Guid, long)>(read.Records.Select(r => (r.Run, r.Seq)));
        var missing = found.Records.Where(r => !known.Contains((r.Run, r.Seq))).ToList();
        if (missing.Count > 0)
        {
            try
            {
                journal.Append(missing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log("Dnevnik emitovanja ne moze da se pise; stara crna kutija se cuva za sledece pokretanje: " + ex.Message);
                ready = false;
                return new HistoryRecovery { FromBlackBox = 0, InvalidJournalLines = read.InvalidLines, JournalRecords = read.Records.Count };
            }
        }

        faults?.AtPoint?.Invoke("oporavak-1");
        var all = new List<JournalRecord>(read.Records.Count + missing.Count);
        all.AddRange(read.Records);
        all.AddRange(missing);

        // Ponovljeni redovi (isti rid) se uzimaju jednom; redosled po pokretanju pa po rednom broju.
        all = [.. all.GroupBy(r => (r.Run, r.Seq)).Select(g => g.First()).OrderBy(r => r.Run).ThenBy(r => r.Seq)];
        box.Reset(newRun);
        faults?.AtPoint?.Invoke("oporavak-2");
        ready = true;
        return new HistoryRecovery
        {
            FromBlackBox = missing.Count,
            InvalidBlackBoxRecords = found.InvalidRecords,
            JournalRecords = all.Count,
            InvalidJournalLines = read.InvalidLines,
            TornTailTruncated = read.TornTailTruncated,
            Lost = found.UnreportedLost,
            LostFromUtc = found.LostFromUtc,
            LostToUtc = found.LostToUtc,
        };
    }

    /// <summary>Koraci oporavka posle praznjenja crne kutije: prekid programa, nepotvrdjeni, gubitak, upis u bazu.</summary>
    private void AppendRecoveryRecords(HistoryRecovery start)
    {
        var all = Journal.ReadAll().Records;
        var now = _utcNow();
        foreach (var q in all.Where(r => r.Kind == JournalKind.Quarantine && r.Frame > now.Ticks))
        {
            _quarantine[q.Track] = new DateTime(q.Frame, DateTimeKind.Utc);
        }

        var generated = new List<JournalRecord>();

        // 2. prekid_programa za svako ranije pokretanje bez zapisa gasenje (i bez vec upisanog prekid_programa za njega)
        var runs = all.Where(r => r.Run != Run).GroupBy(r => r.Run).ToDictionary(g => g.Key, g => g.Max(r => r.TimeTicks));
        var handled = new HashSet<Guid>(all.Where(r => r.Kind == JournalKind.Shutdown).Select(r => r.Run));
        handled.UnionWith(all.Where(r => r.Kind == JournalKind.ProgramCrash).Select(r => r.PlaybackIdA));
        foreach (var (run, last) in runs.Where(r => !handled.Contains(r.Key)).OrderBy(r => r.Value))
        {
            generated.Add(new JournalRecord { Kind = JournalKind.ProgramCrash, PlaybackIdA = run, Frame = last, TimeTicks = now.Ticks });
        }

        // 3. nepotvrdjeno za svaku stavku iz ranijih pokretanja bez zavrsnog zapisa
        var aggregates = Aggregate(all);
        var irregular = aggregates.Values.Count(a => a.ExtraTerminals > 0);
        if (irregular > 0)
        {
            _log($"Nepravilnost u istoriji: {irregular} stavki ima vise zavrsnih zapisa; vazi prvi po redosledu rid.");
        }

        foreach (var aggregate in aggregates.Values.Where(a => a.Terminal is null && a.Run != Run))
        {
            generated.Add(new JournalRecord
            {
                Kind = JournalKind.Unconfirmed,
                PlaybackId = aggregate.PlaybackId,
                Track = aggregate.TrackId,
                Unconfirmed = aggregate.PendingKind,
                Frame = aggregate.LastUtc.Ticks,
                TimeTicks = now.Ticks,
            });
        }

        // 4. gubitak iz zaglavlja stare crne kutije
        if (start.Lost > 0)
        {
            generated.Add(new JournalRecord { Kind = JournalKind.Lost, Track = start.Lost, Frame = (start.LostFromUtc ?? now).Ticks, TimeTicks = (start.LostToUtc ?? now).Ticks });
        }

        for (var i = 0; i < generated.Count; i++)
        {
            var record = generated[i];
            Box.TryAppend(ref record);
            generated[i] = record;
        }

        Scribe.Faults?.AtPoint?.Invoke("oporavak-3");
        var journalDone = generated.Count == 0 || Scribe.DrainNow(TimeSpan.FromSeconds(10));
        foreach (var record in generated.Where(r => r.Kind == JournalKind.Unconfirmed))
        {
            aggregates[record.PlaybackId].Apply(record, asA: false);
        }

        // 5. u bazu: svaka stavka sa zavrsnim zapisom, a bez reda u bazi ("ubaci ili preskoci")
        var inserted = 0;
        var complete = journalDone;
        try
        {
            var existing = _db.History.PlaybackIds();
            foreach (var aggregate in aggregates.Values)
            {
                if (aggregate.Outcome() is { } outcome && !existing.Contains(outcome.PlaybackId.ToString()))
                {
                    Scribe.Faults?.BeforeDatabase?.Invoke();
                    if (_db.History.InsertOutcome(outcome))
                    {
                        inserted++;
                    }

                    Scribe.Faults?.AtPoint?.Invoke("oporavak-4");
                }
            }
        }
        catch (Exception ex) when (ex is not SimulatedCrash)
        {
            complete = false;
            _log("Upis istorije u bazu pri pokretanju nije zavrsen; ponavlja se pri sledecem pokretanju: " + ex.Message);
        }

        string? archived = null;
        if (complete && Box.Available)
        {
            try
            {
                archived = Journal.ArchiveIfLarge(now);
                if (archived is not null)
                {
                    // aktivan karantin mora da ostane u tekucem dnevniku
                    foreach (var (track, until) in _quarantine.Where(q => q.Value > now).ToList())
                    {
                        var record = new JournalRecord { Kind = JournalKind.Quarantine, Track = track, Frame = until.Ticks, TimeTicks = now.Ticks };
                        Box.TryAppend(ref record);
                    }
                }
            }
            catch (IOException ex)
            {
                _log("Arhiviranje dnevnika nije uspelo: " + ex.Message);
            }
        }

        RecoveryResult = start with
        {
            ProgramCrashes = generated.Count(r => r.Kind == JournalKind.ProgramCrash),
            Unconfirmed = generated.Count(r => r.Kind == JournalKind.Unconfirmed),
            DatabaseRows = inserted,
            DatabaseComplete = complete,
            Archived = archived,
        };
    }

    /// <summary>Rezultat celog oporavka (posle upisa u bazu).</summary>
    public HistoryRecovery RecoveryResult { get; private set; } = new();

    internal static Dictionary<Guid, PlaybackAggregate> Aggregate(IEnumerable<JournalRecord> records)
    {
        var map = new Dictionary<Guid, PlaybackAggregate>();
        foreach (var record in records)
        {
            if (record.Kind == JournalKind.Crossfade)
            {
                Get(map, record.PlaybackIdA).Apply(record, asA: true);
                Get(map, record.PlaybackId).Apply(record, asA: false);
            }
            else if (record.PlaybackId != Guid.Empty && record.Kind is not (JournalKind.ProgramCrash or JournalKind.Lost or JournalKind.Shutdown))
            {
                Get(map, record.PlaybackId).Apply(record, asA: false);
            }
        }

        return map;

        static PlaybackAggregate Get(Dictionary<Guid, PlaybackAggregate> map, Guid id)
        {
            if (!map.TryGetValue(id, out var aggregate))
            {
                aggregate = new PlaybackAggregate(id);
                map[id] = aggregate;
            }

            return aggregate;
        }
    }
}
