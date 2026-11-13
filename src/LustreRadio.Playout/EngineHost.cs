using LustreRadio.Audio;
using LustreRadio.Core.Licensing;
using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Time;
using LustreRadio.Data;

namespace LustreRadio.Playout;

public sealed record EngineHostOptions
{
    /// <summary>Folder sa bazom i ostalim podacima programa.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Folderi sa muzikom. Ako je null, citaju se iz podesavanja u bazi.</summary>
    public IReadOnlyList<string>? MusicFolders { get; init; }

    /// <summary>Samo za probu: radno vreme ceo dan, da moze da se slusa u bilo koje doba.</summary>
    public bool AlwaysOpen { get; init; }

    public DirectorOptions Director { get; init; } = new();

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Sklapa bazu, reditelja i zvucni izlaz u radio koji sam svira po rasporedu:
/// ceka radno vreme, pusta, stisava na zatvaranju, cuva stanje i nastavlja posle restarta.
/// STANJE: logika proverena offline testovima; rad sa pravim uredjajem proverava se probom.
/// </summary>
public sealed class EngineHost : IDisposable
{
    public const string MusicFoldersKey = "music-folders";

    private readonly EngineHostOptions _options;
    private readonly Action<string> _log;
    private readonly ManualResetEventSlim _stop = new();
    private readonly object _sessionLock = new();
    private readonly IMonotonicClock _monotonic = new SystemMonotonicClock();
    private RadioDirector? _director;
    private Thread? _thread;
    private RadioSession? _session;
    private RenderPump? _pump;
    private ResilientOutput? _output;
    private AudioFormat _format = AudioFormat.Stereo48000;
    private TimeSpan _notBefore;
    private int _ticks;

    public EngineHost(EngineHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? (_ => { });
        Directory.CreateDirectory(options.DataRoot);
        Database = LustreDatabase.Open(Path.Combine(options.DataRoot, Core.AppPaths.DatabaseFileName));
        if (!Database.QuickCheck())
        {
            _log("UPOZORENJE: provera baze nije prosla.");
        }
    }

    public LustreDatabase Database { get; }

    public event Action<NowPlaying>? TrackStarted;

    public event Action<OutputState, string>? OutputStateChanged;

    public int BroadcastableCount => _director?.BroadcastableCount ?? 0;

    public IReadOnlyList<string> MusicFolders =>
        _options.MusicFolders ?? Database.Settings.GetJson<List<string>>(MusicFoldersKey) ?? [];

    /// <summary>Skenira foldere sa muzikom i uskladjuje bazu.</summary>
    public IndexSummary Index()
    {
        var summary = LibraryIndexer.Run(Database, MusicFolders, _log);
        _director?.ReloadCatalog();
        return summary;
    }

    public void ReloadCatalog() => _director?.ReloadCatalog();

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Vec pokrenuto.");
        }

        try
        {
            (_format, var device) = ResilientOutput.QueryDefaultDevice();
            _log($"Zvucni uredjaj: {device}, {_format.SampleRate} Hz");
        }
        catch (Exception ex)
        {
            _log("Zvucni uredjaj trenutno nije dostupan (" + ex.Message + "); radim u 48000 Hz i cekam uredjaj.");
        }

        _director = new RadioDirector(Database, new SystemClock(), _format, new Random(), _options.Director, _log);
        if (_options.AlwaysOpen)
        {
            _director.Schedule = AlwaysOpenSchedule(_director.Schedule);
        }

        _director.TrackStarted += playing => TrackStarted?.Invoke(playing);
        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.Host" };
        _thread.Start();
    }

    /// <summary>Prelazak na sledecu pesmu.</summary>
    public void Skip()
    {
        lock (_sessionLock)
        {
            _session?.Skip();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(5000);
        lock (_sessionLock)
        {
            try
            {
                _session?.Tick(saveSnapshot: true);
            }
            catch (Exception ex)
            {
                _log("Snimak stanja pri gasenju nije uspeo: " + ex.Message);
            }

            EndSession("gasenje");
        }

        Database.Dispose();
        _stop.Dispose();
    }

    /// <summary>Isti programi po danima i periodima, ali otvoreno ceo dan (samo za probu).</summary>
    public static WeeklySchedule AlwaysOpenSchedule(WeeklySchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var days = schedule.Days.ToDictionary(
            d => d.Key,
            d => d.Value with { Times = d.Value.Times with { Open = TimeOnly.MinValue, Close = new TimeOnly(23, 59, 59) } });
        return schedule with { Days = days, Exceptions = [] };
    }

    private void Run()
    {
        while (!_stop.IsSet)
        {
            try
            {
                lock (_sessionLock)
                {
                    Step();
                }
            }
            catch (Exception ex)
            {
                _log("Greska u glavnoj petlji: " + ex);
            }

            _stop.Wait(TimeSpan.FromSeconds(1));
        }
    }

    private void Step()
    {
        var director = _director!;
        if (_session is null)
        {
            if (_monotonic.Elapsed >= _notBefore && director.ShouldPlayNow() && director.BroadcastableCount > 0)
            {
                StartSession(director);
            }

            return;
        }

        _ticks++;
        _session.Tick(saveSnapshot: _ticks % 5 == 0);
        if (_output!.State == OutputState.Finished)
        {
            EndSession("kraj emitovanja");
            _notBefore = _monotonic.Elapsed + TimeSpan.FromSeconds(10);
        }
    }

    private void StartSession(RadioDirector director)
    {
        var session = new RadioSession(director, _format, director.LoadSnapshot());
        var ring = new FloatRingBuffer(65536);
        var pump = new RenderPump(session.Output, new Limiter(_format), ring);
        pump.Error += ex => _log("Greska pripreme zvuka: " + ex);
        var output = new ResilientOutput(ring, pump, _format);
        output.StateChanged += (state, detail) => OutputStateChanged?.Invoke(state, detail);

        _session = session;
        _pump = pump;
        _output = output;
        _ticks = 0;
        _log(session.Resumed ? "Pocinje emitovanje (nastavak prekinute pesme)." : "Pocinje emitovanje.");
        pump.Start();
        output.Start();
    }

    private void EndSession(string reason)
    {
        if (_session is null)
        {
            return;
        }

        _log("Emitovanje zavrseno: " + reason + ".");
        _output?.Dispose();
        _pump?.Dispose();
        _output = null;
        _pump = null;
        _session = null;
    }
}

/// <summary>
/// SAMO ZA PROBU: oznacava sve pesme u probnoj bazi kao proverene, da bi mogle da se cuju.
/// Ovo NIJE provera licence i ne sme se koristiti za bazu koja radi u lokalu.
/// </summary>
public static class ProbeLicensing
{
    public const string Marker = "PROBA - nije provera licence";

    public static int MarkAllVerified(LustreDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var count = 0;
        foreach (var track in db.Tracks.GetAll().Where(t => t.DecodeOk && t.ContentHash.Length > 0))
        {
            var license = db.Licenses.EnsureImported(track.ContentHash, "proba");
            if (license.Status == LicenseStatus.Verified)
            {
                continue;
            }

            if (license.Status is LicenseStatus.Imported or LicenseStatus.Recheck or LicenseStatus.Rejected)
            {
                db.Licenses.Save(license with { Status = LicenseStatus.UnderReview }, "proba", Marker);
            }

            db.Licenses.Save(
                (db.Licenses.Get(track.ContentHash) ?? license) with
                {
                    Status = LicenseStatus.UnderReview,
                    Source = "PROBA",
                    SourceUrl = "proba://lokalni-fajl",
                    Author = string.IsNullOrWhiteSpace(track.Artist) ? "nepoznat" : track.Artist,
                    LicenseType = Marker,
                    DownloadedOn = DateOnly.FromDateTime(DateTime.Now),
                    CommercialUse = Answer.Yes,
                    PublicPerformance = Answer.Yes,
                    Modifications = Answer.Yes,
                    AttributionRequired = Answer.No,
                    HasProof = true,
                },
                "proba",
                Marker);
            db.Licenses.Verify(track.ContentHash, "proba", DateTimeOffset.Now);
            count++;
        }

        return count;
    }
}
