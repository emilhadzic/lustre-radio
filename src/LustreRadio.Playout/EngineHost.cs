using System.Globalization;
using LustreRadio.Audio;
using LustreRadio.Core.Security;
using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Time;
using LustreRadio.Data;
using LustreRadio.Ipc;

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

    /// <summary>Ime lokalnog kanala za interfejs. null = bez veze sa interfejsom.</summary>
    public string? PipeName { get; init; }

    /// <summary>Pauza se sama prekida posle ovog vremena (spec. deo 12: 15 minuta).</summary>
    public TimeSpan PauseAutoResume { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Sigurni rezim (posle vise padova): bez skeniranja biblioteke.</summary>
    public bool SafeMode { get; init; }
}

/// <summary>
/// Sklapa bazu, reditelja i zvucni izlaz u radio koji sam svira po rasporedu:
/// ceka radno vreme, pusta, stisava na zatvaranju, cuva stanje i nastavlja posle restarta.
/// STANJE: logika proverena offline testovima; rad sa pravim uredjajem proverava se probom.
/// </summary>
public sealed class EngineHost : IDisposable, IRadioControl
{
    public const string MusicFoldersKey = "music-folders";
    public const string VolumeKey = "volume";

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
    private PauseGate? _pauseGate;
    private VolumeControl? _volumeControl;
    private IpcServer? _server;
    private NowPlaying? _nowPlaying;
    private TimeSpan _pausedAt;
    private double _volume;
    private readonly object _scanLock = new();
    private bool _scanning;
    private bool _scanAgain;

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

        var (min, max) = RadioCommandHandler.VolumeRange(Database);
        _volume = Math.Clamp(
            double.TryParse(Database.Settings.Get(VolumeKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var saved) ? saved : max,
            min,
            max);
    }

    public LustreDatabase Database { get; }

    public event Action<NowPlaying>? TrackStarted;

    public event Action<OutputState, string>? OutputStateChanged;

    public int BroadcastableCount => _director?.BroadcastableCount ?? 0;

    public IReadOnlyList<string> MusicFolders =>
        _options.MusicFolders ?? Database.Settings.GetJson<List<string>>(MusicFoldersKey) ?? [];

    /// <summary>
    /// Skenira biblioteku u pozadini, da pokretanje programa ne ceka (veliko skeniranje traje minutima,
    /// a nadzor bi program koji ne odgovara smatrao zamrznutim). U sigurnom rezimu se ne skenira.
    /// </summary>
    public void IndexInBackground()
    {
        if (_options.SafeMode)
        {
            _log("Sigurni rezim: skeniranje biblioteke je preskoceno.");
            return;
        }

        Rescan();
    }

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

        _director.TrackStarted += playing =>
        {
            _nowPlaying = playing;
            TrackStarted?.Invoke(playing);
        };

        if (_options.PipeName is not null)
        {
            var sessions = new AdminSessions(_monotonic, () => Database.Settings.Get(RadioCommandHandler.PinKey));
            _server = new IpcServer(_options.PipeName, new RadioCommandHandler(this, Database, sessions, _log), _log);
            _server.Start();
            _log("Veza za interfejs: " + _options.PipeName);
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.Host" };
        _thread.Start();
    }

    /// <summary>Prelazak na sledecu pesmu.</summary>
    public void Skip() => Next(null);

    public bool Next(string? expectedTrackId)
    {
        lock (_sessionLock)
        {
            if (_session is null)
            {
                return false;
            }

            if (expectedTrackId is not null && (!_session.TryGetPosition(out var current, out _) || current != expectedTrackId))
            {
                return false; // ta pesma vise ne svira: komanda je zakasnela ili ponovljena
            }

            _session.Skip();
            return true;
        }
    }

    public void Pause()
    {
        lock (_sessionLock)
        {
            if (_pauseGate is { IsPaused: false })
            {
                _pauseGate.Pause();
                _pausedAt = _monotonic.Elapsed;
                _log("Pauza (sama se prekida posle " + (int)_options.PauseAutoResume.TotalMinutes + " min).");
            }
        }

        Publish();
    }

    public void Resume()
    {
        lock (_sessionLock)
        {
            if (_pauseGate is { IsPaused: true })
            {
                _pauseGate.Resume();
                _log("Nastavak posle pauze.");
            }
        }

        Publish();
    }

    public void SetVolume(double volume)
    {
        lock (_sessionLock)
        {
            _volume = Math.Clamp(volume, 0, 1);
            _volumeControl?.SetVolume(_volume);
            Database.Settings.Set(VolumeKey, _volume.ToString("R", CultureInfo.InvariantCulture));
        }

        Publish();
    }

    /// <summary>
    /// Pokrece skeniranje u pozadini i odmah se vraca: komanda interfejsa ne sme da ceka minute.
    /// Ako skeniranje vec traje, zapamti da treba jos jedno posle njega (npr. folderi su upravo promenjeni).
    /// </summary>
    public void Rescan()
    {
        lock (_scanLock)
        {
            if (_scanning)
            {
                _scanAgain = true;
                return;
            }

            _scanning = true;
        }

        Publish();
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _log($"Skeniranje: {Index()}");
                }
                catch (Exception ex)
                {
                    _log("Skeniranje nije uspelo: " + ex.Message);
                }

                lock (_scanLock)
                {
                    if (_scanAgain)
                    {
                        _scanAgain = false;
                        continue;
                    }

                    _scanning = false;
                    break;
                }
            }

            Publish();
        })
        {
            IsBackground = true,
            Name = "LustreRadio.Index",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public bool IsScanning
    {
        get
        {
            lock (_scanLock)
            {
                return _scanning;
            }
        }
    }

    public void SetMusicFolders(IReadOnlyList<string> folders)
    {
        Database.Settings.SetJson(MusicFoldersKey, folders.ToList());
        Rescan();
    }

    public void StateChanged() => Publish();

    public RadioState GetState()
    {
        lock (_sessionLock)
        {
            var (min, max) = RadioCommandHandler.VolumeRange(Database);
            var playing = _session is not null ? _nowPlaying : null;
            var decision = _director?.CurrentDecision();
            var license = Database.Settings.GetJson<Core.Licensing.CollectiveLicense>(LibraryIndexer.CollectiveLicenseKey);
            var position = 0.0;
            var trackId = playing?.TrackId;
            if (_session is not null && _session.TryGetPosition(out var id, out var seconds) && id == trackId)
            {
                position = seconds;
            }

            return new RadioState
            {
                OnAir = _session is not null,
                Paused = _pauseGate?.IsPaused ?? false,
                TrackId = trackId,
                Title = playing?.Title,
                Artist = playing?.Artist,
                PositionSeconds = position,
                DurationSeconds = playing?.DurationSeconds ?? 0,
                Program = playing?.Program,
                DayPart = playing?.Part?.ToString(),
                Volume = _volume,
                VolumeMin = min,
                VolumeMax = max,
                Output = _output?.State.ToString(),
                Device = _output?.DeviceName,
                BroadcastableTracks = BroadcastableCount,
                PinIsSet = !string.IsNullOrEmpty(Database.Settings.Get(RadioCommandHandler.PinKey)),
                AsOfUtc = DateTime.UtcNow,
                FramesRendered = _session?.Sequencer.OutputFrames ?? 0,
                ProcessId = Environment.ProcessId,
                SafeMode = _options.SafeMode,
                ClosesAtLocal = decision is { ShouldPlay: true } ? decision.ClosesAtLocal : null,
                OpensAtLocal = decision is { ShouldPlay: false } ? decision.NextChangeLocal : null,
                NowLocal = DateTime.Now,
                MusicFolders = MusicFolders.ToList(),
                TotalTracks = _director?.TotalCount ?? 0,
                Scanning = IsScanning,
                StaffLanguage = RadioCommandHandler.Language(Database, RadioCommandHandler.StaffLanguageKey),
                AdminLanguage = RadioCommandHandler.Language(Database, RadioCommandHandler.AdminLanguageKey),
                EngineVersion = typeof(EngineHost).Assembly.GetName().Version?.ToString(3),
                LicenseOrganizations = license?.Organizations,
                LicenseDeclaredLocal = license?.DeclaredAt.LocalDateTime,
            };
        }
    }

    private void Publish()
    {
        try
        {
            _server?.Broadcast(GetState());
        }
        catch (Exception ex)
        {
            _log("Slanje stanja interfejsu nije uspelo: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(5000);
        _server?.Dispose();
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

            Publish();
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

        if (_pauseGate is { IsPaused: true })
        {
            if (!director.ShouldPlayNow())
            {
                // radno vreme je isteklo tokom pauze: nema sta da se stisava
                EndSession("zatvaranje tokom pauze");
                return;
            }

            if (_monotonic.Elapsed - _pausedAt >= _options.PauseAutoResume)
            {
                _pauseGate.Resume();
                _log("Pauza je istekla; muzika se nastavlja sama.");
            }
        }

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
        var pauseGate = new PauseGate(session.Output);
        var volume = new VolumeControl(pauseGate, _volume);
        var pump = new RenderPump(volume, new Limiter(_format), ring);
        pump.Error += ex => _log("Greska pripreme zvuka: " + ex);
        var output = new ResilientOutput(ring, pump, _format);
        output.StateChanged += (state, detail) => OutputStateChanged?.Invoke(state, detail);

        _session = session;
        _pump = pump;
        _output = output;
        _pauseGate = pauseGate;
        _volumeControl = volume;
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
        _pauseGate = null;
        _volumeControl = null;
    }
}
