using NAudio.CoreAudioApi;

namespace LustreRadio.Audio;

public enum OutputState
{
    Starting,
    Playing,

    /// <summary>Nema uredjaja ili je otkazao; pokusava se ponovo.</summary>
    Disconnected,

    /// <summary>Sav zvuk je pusten.</summary>
    Finished,
}

/// <summary>
/// Izlaz na zvucni uredjaj koji prezivljava nestanak uredjaja (npr. Bluetooth zvucnik):
/// kad uredjaj nestane, muzika zastaje; kad se vrati ili se pojavi drugi podrazumevani
/// uredjaj, nastavlja tacno odakle je stala. Prati i promenu podrazumevanog uredjaja.
/// </summary>
public sealed class ResilientOutput : IDisposable
{
    private readonly IOutputBackend _backend;
    private readonly OutputPuller _puller;
    private readonly AudioFormat _format;
    private readonly int _latencyMs;
    private readonly TimeSpan _retryInterval;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEventSlim _finished = new();
    private readonly object _logLock = new();
    private readonly List<string> _log = [];
    private readonly DateTime _started = DateTime.UtcNow;

    private Thread? _thread;
    private IOutputSession? _session;
    private volatile bool _sessionDead;
    private volatile bool _disposed;
    private bool _everOpened;
    private long _underrunFrames;
    private int _reconnects;
    private volatile OutputState _state = OutputState.Starting;

    public ResilientOutput(FloatRingBuffer ring, RenderPump pump, AudioFormat format, int latencyMs = 200, TimeSpan? retryInterval = null)
        : this(new WasapiBackend(), ring, pump, format, latencyMs, retryInterval ?? TimeSpan.FromSeconds(5))
    {
    }

    internal ResilientOutput(IOutputBackend backend, FloatRingBuffer ring, RenderPump pump, AudioFormat format, int latencyMs, TimeSpan retryInterval)
    {
        _backend = backend;
        _puller = new OutputPuller(ring, pump);
        _format = format;
        _latencyMs = latencyMs;
        _retryInterval = retryInterval;
        _backend.DevicesChanged += () => _signal.Set();
    }

    public OutputState State => _state;

    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Broj frejmova tisine poslatih zato sto zvuk nije stigao na vreme. Cilj: 0.</summary>
    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    /// <summary>Koliko puta je uredjaj ponovo otvoren posle prvog otvaranja.</summary>
    public int Reconnects => Volatile.Read(ref _reconnects);

    /// <summary>Promena stanja (stanje, opis). Poziva se sa nadzorne niti.</summary>
    public event Action<OutputState, string>? StateChanged;

    /// <summary>Podrazumevani uredjaj i njegov sample rate (engine radi u tom rate-u).</summary>
    public static (AudioFormat Format, string DeviceName) QueryDefaultDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (new AudioFormat(device.AudioClient.MixFormat.SampleRate, 2), device.FriendlyName);
    }

    public IReadOnlyList<string> LogSnapshot()
    {
        lock (_logLock)
        {
            return [.. _log];
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Vec pokrenuto.");
        }

        _thread = new Thread(Monitor) { IsBackground = true, Name = "LustreRadio.Output" };
        _thread.Start();
    }

    public bool WaitUntilFinished(TimeSpan timeout) => _finished.Wait(timeout);

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        _thread?.Join(3000);
        CloseSession();
        _backend.Dispose();
        _signal.Dispose();
        _finished.Dispose();
    }

    private void Monitor()
    {
        while (!_disposed)
        {
            try
            {
                Step();
            }
            catch (Exception ex)
            {
                Note("Greska nadzora izlaza: " + ex.GetType().Name + ": " + ex.Message);
            }

            _signal.WaitOne(_retryInterval);
        }
    }

    private void Step()
    {
        if (_state == OutputState.Finished)
        {
            return;
        }

        if (_session is not null && !_sessionDead)
        {
            // radi: proveri da li je podrazumevani uredjaj i dalje isti
            var current = _backend.CurrentDefaultDeviceId();
            if (current is null || current == _session.DeviceId)
            {
                return;
            }

            Note("Podrazumevani uredjaj je promenjen.");
        }

        CloseSession();
        if (_puller.Finished)
        {
            SetState(OutputState.Finished, "kraj");
            _finished.Set();
            return;
        }

        IOutputSession? session = null;
        try
        {
            session = _backend.Open(_puller, _format, _latencyMs, frames => Interlocked.Add(ref _underrunFrames, frames));
            var opened = session;
            opened.Stopped += error => OnSessionStopped(opened, error);
            _sessionDead = false;
            _session = opened;
            opened.Play();

            if (_everOpened)
            {
                Interlocked.Increment(ref _reconnects);
            }

            _everOpened = true;
            DeviceName = opened.DeviceName;
            SetState(OutputState.Playing, opened.DeviceName);
        }
        catch (Exception ex)
        {
            _session = null;
            session?.Dispose();
            SetState(OutputState.Disconnected, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void OnSessionStopped(IOutputSession session, Exception? error)
    {
        if (!ReferenceEquals(session, _session))
        {
            return; // stara veza koju smo sami zatvorili
        }

        if (error is null && _puller.Finished)
        {
            SetState(OutputState.Finished, "kraj");
            _finished.Set();
            return;
        }

        Note("Uredjaj je stao: " + (error is null ? "bez greske" : error.GetType().Name + ": " + error.Message));
        _sessionDead = true;
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // gasenje
        }
    }

    private void CloseSession()
    {
        var session = _session;
        _session = null;
        _sessionDead = false;
        session?.Dispose();
    }

    private void SetState(OutputState state, string detail)
    {
        if (_state == state && state == OutputState.Disconnected)
        {
            return; // ne ponavljaj isti zapis na svaki pokusaj
        }

        _state = state;
        Note(state + ": " + detail);
        StateChanged?.Invoke(state, detail);
    }

    private void Note(string text)
    {
        lock (_logLock)
        {
            _log.Add($"{(DateTime.UtcNow - _started).TotalSeconds,7:F1} s  {text}");
        }
    }
}
