using LustreRadio.Ipc;

namespace LustreRadio.Presentation;

/// <summary>
/// Veza interfejsa sa audio procesom. Sama se ponovo povezuje (na 1 s, pa redje do 5 s; spec. 2.6).
/// Nikad ne pokrece audio proces: to radi samo nadzor.
/// </summary>
public interface IRadio
{
    event Action? Changed;

    RadioState? State { get; }

    LinkStatus Status { get; }

    string? AdminSession { get; set; }

    Task<IpcMessage?> SendAsync(string name, Dictionary<string, string>? args = null, bool admin = false);
}

public sealed class RadioLink : IRadio, IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private IpcClient? _client;
    private Task? _loop;
    private RadioState? _state;
    private LinkStatus _status = LinkStatus.Connecting;

    public RadioLink(string pipeName = IpcProtocol.DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>Stanje ili veza su se promenili. Stize sa pozadinske niti.</summary>
    public event Action? Changed;

    /// <summary>Poslednje poznato stanje; ostaje i kad veza pukne (prikazuje se zasivljeno).</summary>
    public RadioState? State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public LinkStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>Admin sesija koju je izdao audio proces; null kad je zakljucano.</summary>
    public string? AdminSession { get; set; }

    /// <summary>Pauza pre sledeceg pokusaja: 1, 2, 3, 4, 5, 5, 5… sekundi.</summary>
    public static TimeSpan RetryDelay(int failedAttempts) => TimeSpan.FromSeconds(Math.Clamp(failedAttempts, 1, 5));

    public void Start() => _loop ??= Task.Run(RunAsync);

    /// <summary>
    /// Salje komandu. Vraca null ako veze nema ili odgovor nije stigao na vreme
    /// (komanda se tada ne ponavlja sama: zaposleni vidi stvarno stanje i odlucuje).
    /// </summary>
    public async Task<IpcMessage?> SendAsync(string name, Dictionary<string, string>? args = null, bool admin = false)
    {
        IpcClient? client;
        lock (_sync)
        {
            client = _status == LinkStatus.Connected ? _client : null;
        }

        if (client is null)
        {
            return null;
        }

        try
        {
            var response = await client.SendAsync(name, args, admin ? AdminSession : null).ConfigureAwait(false);
            if (response is { Ok: false, Error: "admin-required" })
            {
                AdminSession = null;
            }

            return response;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // gasenje
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            IpcClient? client = null;
            try
            {
                var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client = await IpcClient.ConnectAsync(_pipeName, TimeSpan.FromSeconds(2), OnState, cancel: _stop.Token).ConfigureAwait(false);
                client.Disconnected += () => dropped.TrySetResult();
                lock (_sync)
                {
                    _client = client;
                    _status = LinkStatus.Connected;
                    if (client.LastState is { } first)
                    {
                        _state = first;
                    }
                }

                failures = 0;
                Changed?.Invoke();
                if (client.IsConnected)
                {
                    await dropped.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
            catch (IpcProtocolException)
            {
                failures = 5;
                SetStatus(LinkStatus.VersionMismatch);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException)
            {
                // audio proces ne radi ili se upravo pokrece
            }
            finally
            {
                lock (_sync)
                {
                    _client = null;
                }

                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (_stop.IsCancellationRequested)
            {
                break;
            }

            AdminSession = null; // posle prekida veze admin se ponovo prijavljuje
            if (Status != LinkStatus.VersionMismatch)
            {
                SetStatus(LinkStatus.Connecting);
            }

            failures++;
            try
            {
                await Task.Delay(RetryDelay(failures), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnState(RadioState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        Changed?.Invoke();
    }

    private void SetStatus(LinkStatus status)
    {
        bool changed;
        lock (_sync)
        {
            changed = _status != status;
            _status = status;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
