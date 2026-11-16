namespace LustreRadio.Core.History;

/// <summary>
/// Cuvar crne kutije (D8-1): posebna mala nit koja na svakih 5 s trazi od Windows-a da crnu kutiju stvarno upise na disk.
/// Ne zavisi od pisara; ako disk stoji, zaglavi se samo ona.
/// </summary>
public sealed class BlackBoxGuard : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly BlackBox _box;
    private readonly TimeSpan _interval;
    private readonly Action<string> _log;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private bool _failing;

    /// <summary>Poslednja greska pri upisu na disk (za dnevnik i test).</summary>
    public string? LastError { get; private set; }

    public BlackBoxGuard(BlackBox box, TimeSpan? interval = null, Action<string>? log = null)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _interval = interval ?? DefaultInterval;
        _log = log ?? (_ => { });
        _thread = new Thread(Run) { IsBackground = true, Name = "LustreRadio.CuvarCrneKutije", Priority = ThreadPriority.BelowNormal };
    }

    public void Start()
    {
        if (_box.Available)
        {
            _thread.Start();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(10));
        }

        _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.Wait(_interval))
        {
            try
            {
                _box.Flush();
                if (_failing)
                {
                    _failing = false;
                    _log("Crna kutija se ponovo upisuje na disk.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                LastError = ex.Message;
                if (!_failing)
                {
                    _failing = true;
                    _log("Upis crne kutije na disk nije uspeo: " + ex.Message);
                }
            }
        }
    }
}
