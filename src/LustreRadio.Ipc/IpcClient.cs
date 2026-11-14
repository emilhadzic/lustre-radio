using System.Collections.Concurrent;
using System.IO.Pipes;

namespace LustreRadio.Ipc;

/// <summary>
/// Strana interfejsa. Ne cuva stanje radija: prikazuje poslednje stanje koje je stiglo.
/// Kad veza pukne, javlja to (Disconnected); ponovno povezivanje radi onaj ko ga koristi.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Action<RadioState>? _onState;
    private Task? _readLoop;
    private int _disconnected;

    private IpcClient(NamedPipeClientStream pipe, Action<RadioState>? onState)
    {
        _pipe = pipe;
        _onState = onState;
    }

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;

    public RadioState? LastState { get; private set; }

    public event Action? Disconnected;

    /// <summary>Povezuje se i proverava verziju protokola. Baca IpcProtocolException ako se verzije ne slazu.</summary>
    public static async Task<IpcClient> ConnectAsync(
        string pipeName, TimeSpan timeout, Action<RadioState>? onState = null, int protocol = IpcProtocol.Version, CancellationToken cancel = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout);
            await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
            await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "hello", Protocol = protocol }, limit.Token).ConfigureAwait(false);
            var ack = await IpcProtocol.ReadAsync(pipe, limit.Token).ConfigureAwait(false);
            if (ack is not { Type: "hello", Ok: true })
            {
                throw new IpcProtocolException(
                    $"Audio proces koristi drugu verziju protokola ({ack?.Protocol?.ToString() ?? "nepoznata"}), a interfejs {protocol}.");
            }

            var client = new IpcClient(pipe, onState);
            client._readLoop = Task.Run(client.ReadLoopAsync);
            return client;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Salje komandu i ceka odgovor. Isti id poslat ponovo ne izvrsava komandu drugi put.</summary>
    public async Task<IpcMessage> SendAsync(
        string name, Dictionary<string, string>? args = null, string? session = null, TimeSpan? timeout = null, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        try
        {
            if (!IsConnected)
            {
                throw new IOException("Veza sa audio procesom je prekinuta.");
            }

            var limit = timeout ?? TimeSpan.FromSeconds(3);
            var frame = IpcProtocol.Encode(new IpcMessage { Type = "cmd", Id = id, Name = name, Args = args, Session = session });

            // Rok vazi i za samo slanje: ako je druga strana zamrznuta, upis u cev moze da ceka zauvek.
            using var sendLimit = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            sendLimit.CancelAfter(limit);
            try
            {
                await _writeLock.WaitAsync(sendLimit.Token).ConfigureAwait(false);
                try
                {
                    await _pipe.WriteAsync(frame, sendLimit.Token).ConfigureAwait(false);
                    await _pipe.FlushAsync(sendLimit.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (!_closed.IsCancellationRequested)
            {
                // poruka je mozda poslata napola; veza vise nije upotrebljiva
                MarkDisconnected(notify: true);
                throw new TimeoutException("Audio proces ne prima poruke.");
            }

            return await waiter.Task.WaitAsync(limit).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new IOException("Veza sa audio procesom je prekinuta.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        MarkDisconnected(notify: false);
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // gasenje
            }
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _closed.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var message = await IpcProtocol.ReadAsync(_pipe, _closed.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message.Type == "res" && message.Id is not null && _pending.TryGetValue(message.Id, out var waiter))
                {
                    waiter.TrySetResult(message);
                }
                else if (message is { Type: "state", State: not null })
                {
                    LastState = message.State;
                    _onState?.Invoke(message.State);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // veza je pukla
        }
        finally
        {
            MarkDisconnected(notify: true);
        }
    }

    private void MarkDisconnected(bool notify)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // vec zatvoreno
        }

        foreach (var waiter in _pending.Values)
        {
            waiter.TrySetException(new IOException("Veza sa audio procesom je prekinuta."));
        }

        if (notify)
        {
            Disconnected?.Invoke();
        }
    }
}
