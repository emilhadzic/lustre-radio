using LustreRadio.Core.History;
using LustreRadio.Data;

namespace LustreRadio.Playout;

/// <summary>Kvarovi koje test ubacuje u tok jednog dogadjaja (B-42) i oporavak (B-164). Samo za testove.</summary>
internal sealed class HistoryFaults
{
    /// <summary>Poziva se u imenovanim tackama; izuzetak <see cref="SimulatedCrash"/> oponasa pad procesa.</summary>
    public Action<string>? AtPoint { get; set; }

    public PlayJournalFaults? Journal { get; set; }

    /// <summary>Pre svakog upisa u bazu (spora baza, baza odbija upis).</summary>
    public Action? BeforeDatabase { get; set; }
}

/// <summary>Oponasan pad procesa u testu: nit se zaustavlja na tom mestu i nista posle toga se ne radi.</summary>
internal sealed class SimulatedCrash : Exception
{
    public SimulatedCrash(string point)
        : base("oponasan pad: " + point)
    {
    }

    public SimulatedCrash()
    {
    }

    public SimulatedCrash(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Pisar (plan faze B, 2.2): jedini citalac crne kutije. Cita zapise posle <c>potvrdjeno_do</c>, upisuje ih u
/// <c>emitovanje.jsonl</c> sa istim rid, ceka potvrdu diska, tek onda pomera <c>potvrdjeno_do</c>; zatim upisuje
/// konacne ishode u bazu ("ubaci ili preskoci"). Radi na svojoj niti, sniženog prioriteta; nije na putu muzike.
/// </summary>
public sealed class Scribe : IDisposable
{
    public const int MaxBatch = 512;

    /// <summary>Najvise ishoda koji cekaju bazu; ostali se upisuju iz dnevnika pri sledecem pokretanju.</summary>
    public const int MaxPendingDatabase = 50_000;

    private static readonly TimeSpan[] JournalRetry =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly BlackBox _box;
    private readonly PlayJournal _journal;
    private readonly LustreDatabase _db;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly object _work = new();
    private readonly Dictionary<Guid, PlaybackAggregate> _open = [];
    private readonly Queue<PlaybackOutcome> _pending = new();
    private readonly List<JournalRecord> _batch = new(MaxBatch);
    private readonly ManualResetEventSlim _stop = new();
    private readonly TimeSpan _poll;
    private Thread? _thread;
    private int _journalFailures;
    private DateTime _nextJournalTry;
    private DateTime _nextDatabaseTry;
    private volatile bool _journalFailing;
    private volatile bool _databaseFailing;
    private volatile bool _abandoned;
    private long _droppedPending;

    internal Scribe(BlackBox box, PlayJournal journal, LustreDatabase db, Action<string> log, Func<DateTime> utcNow, TimeSpan poll, HistoryFaults? faults)
    {
        _box = box;
        _journal = journal;
        _db = db;
        _log = log;
        _utcNow = utcNow;
        _poll = poll;
        Faults = faults;
    }

    internal HistoryFaults? Faults { get; }

    /// <summary>Dnevnik trenutno ne moze da se pise ("Istorija se ne cuva na disk").</summary>
    public bool JournalFailing => _journalFailing;

    public bool DatabaseFailing => _databaseFailing;

    public int OpenPlaybacks
    {
        get
        {
            lock (_work)
            {
                return _open.Count;
            }
        }
    }

    public int PendingDatabase
    {
        get
        {
            lock (_work)
            {
                return _pending.Count;
            }
        }
    }

    public bool Running => _thread is { IsAlive: true };

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.Pisar", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Radi dok sve iz crne kutije nije u dnevniku i svi ishodi u bazi, ili dok ne istekne rok.</summary>
    public bool DrainNow(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_work)
            {
                ProcessOnce(force: true);
                if (_box.Confirmed >= _box.Reserved && _pending.Count == 0)
                {
                    return true;
                }
            }

            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>Zaustavlja nit (posle <see cref="DrainNow"/> pri urednom gasenju).</summary>
    public void Stop()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    /// <summary>Samo za test: nit staje bez ikakvog daljeg upisa (oponasan pad).</summary>
    internal void Abandon()
    {
        _abandoned = true;
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.Wait(_poll))
        {
            try
            {
                lock (_work)
                {
                    ProcessOnce(force: false);
                }
            }
            catch (SimulatedCrash)
            {
                _abandoned = true;
                return;
            }
            catch (Exception ex)
            {
                _log("Greska pisara: " + ex.Message);
            }
        }
    }

    private void ProcessOnce(bool force)
    {
        if (_abandoned)
        {
            throw new SimulatedCrash("pisar je stao");
        }

        var now = _utcNow();
        if (force || !_journalFailing || now >= _nextJournalTry)
        {
            WriteJournal(now);
        }

        ReportLost(now);

        if (force || !_databaseFailing || now >= _nextDatabaseTry)
        {
            WriteDatabase(now);
        }

        if (_open.Count == 0 && _pending.Count == 0 && _box.Confirmed >= _box.Reserved && !_journalFailing && _journal.Length > PlayJournal.RotateBytes)
        {
            try
            {
                if (_journal.ArchiveIfLarge(now) is { } archived)
                {
                    _log("Dnevnik emitovanja arhiviran: " + archived);
                }
            }
            catch (IOException ex)
            {
                _log("Arhiviranje dnevnika nije uspelo: " + ex.Message);
            }
        }
    }

    private void WriteJournal(DateTime now)
    {
        while (true)
        {
            _batch.Clear();
            for (var seq = _box.Confirmed + 1; _batch.Count < MaxBatch && _box.TryRead(seq, out var record); seq++)
            {
                _batch.Add(record);
            }

            if (_batch.Count == 0)
            {
                return;
            }

            try
            {
                if (Faults?.Journal is { } jf)
                {
                    _journal.Faults = jf;
                }

                _journal.Append(_batch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_journalFailing)
                {
                    _log("Istorija se ne cuva na disk: " + ex.Message);
                }

                _journalFailing = true;
                _nextJournalTry = now + JournalRetry[Math.Min(_journalFailures, JournalRetry.Length - 1)];
                _journalFailures++;
                return;
            }

            if (_journalFailing)
            {
                _log("Dnevnik emitovanja se ponovo pise.");
            }

            _journalFailing = false;
            _journalFailures = 0;
            Faults?.AtPoint?.Invoke("posle-dnevnika");
            _box.Confirm(_batch[^1].Seq);
            Faults?.AtPoint?.Invoke("posle-potvrde");
            foreach (var record in _batch)
            {
                Track(record);
            }
        }
    }

    private void Track(in JournalRecord record)
    {
        switch (record.Kind)
        {
            case JournalKind.Crossfade:
                Aggregate(record.PlaybackIdA).Apply(record, asA: true);
                Aggregate(record.PlaybackId).Apply(record, asA: false);
                Settle(record.PlaybackIdA);
                Settle(record.PlaybackId);
                break;
            case JournalKind.Prepared or JournalKind.Started or JournalKind.Ended or JournalKind.Stopped or JournalKind.Discarded or JournalKind.Unconfirmed:
                if (record.PlaybackId == Guid.Empty)
                {
                    break;
                }

                // Zavrsni zapis za stavku koju pisar nije video kako je pripremljena (oporavak) – bazu za nju puni oporavak.
                if (record.Kind != JournalKind.Prepared && !_open.ContainsKey(record.PlaybackId))
                {
                    break;
                }

                Aggregate(record.PlaybackId).Apply(record, asA: false);
                Settle(record.PlaybackId);
                break;
        }
    }

    private PlaybackAggregate Aggregate(Guid playbackId)
    {
        if (!_open.TryGetValue(playbackId, out var aggregate))
        {
            aggregate = new PlaybackAggregate(playbackId);
            _open[playbackId] = aggregate;
        }

        return aggregate;
    }

    private void Settle(Guid playbackId)
    {
        if (!_open.TryGetValue(playbackId, out var aggregate) || aggregate.Terminal is null)
        {
            return;
        }

        _open.Remove(playbackId);
        if (aggregate.ExtraTerminals > 0)
        {
            _log($"Nepravilnost u istoriji: {aggregate.ExtraTerminals} visak zavrsnih zapisa za {playbackId}; vazi prvi.");
        }

        if (aggregate.Outcome() is { } outcome)
        {
            if (_pending.Count >= MaxPendingDatabase)
            {
                _droppedPending++;
                return;
            }

            _pending.Enqueue(outcome);
        }
    }

    private void ReportLost(DateTime now)
    {
        var lost = _box.LostCount;
        var reported = _box.LostReported;
        if (lost <= reported)
        {
            return;
        }

        var record = new JournalRecord
        {
            Kind = JournalKind.Lost,
            Track = lost - reported,
            Frame = (_box.LostFirstUtc ?? now).Ticks,
            TimeTicks = (_box.LostLastUtc ?? now).Ticks,
        };
        if (_box.TryAppend(ref record))
        {
            _box.MarkLostReported(lost);
            _log($"Istorija izgubljena: {lost - reported} dogadjaja (crna kutija je bila puna).");
        }
    }

    private void WriteDatabase(DateTime now)
    {
        while (_pending.Count > 0)
        {
            try
            {
                Faults?.BeforeDatabase?.Invoke();
                _db.History.InsertOutcome(_pending.Peek());
            }
            catch (Exception ex) when (ex is not SimulatedCrash)
            {
                if (!_databaseFailing)
                {
                    _log("Upis istorije u bazu nije uspeo (pokusava se ponovo): " + ex.Message);
                }

                _databaseFailing = true;
                _nextDatabaseTry = now + TimeSpan.FromSeconds(1);
                return;
            }

            _pending.Dequeue();
            Faults?.AtPoint?.Invoke("posle-baze");
            if (_databaseFailing)
            {
                _databaseFailing = false;
                _log("Upis istorije u bazu ponovo radi.");
            }
        }

        if (_droppedPending > 0)
        {
            _log($"{_droppedPending} ishoda nije stalo u red za bazu; upisuju se iz dnevnika pri sledecem pokretanju.");
            _droppedPending = 0;
        }
    }
}
