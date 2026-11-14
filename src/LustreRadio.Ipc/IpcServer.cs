using System.IO.Pipes;
using System.Threading.Channels;

namespace LustreRadio.Ipc;

/// <summary>
/// Strana audio procesa. Pravila koja stite muziku (spec. 2.6):
///  - nijedno slanje ne ceka klijenta: poruke idu u ogranicen red, a klijent koji ne cita se iskljucuje;
///  - komande se izvrsavaju jedna po jedna, van niti koja pusta zvuk;
///  - ponovljena komanda (isti ID) ne izvrsava se dvaput;
///  - neispravne poruke se odbijaju; previse komandi u sekundi se odbija.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private const int OutgoingQueueSize = 256;
    private const int MaxCommandsPerSecond = 50;
    private const int RememberedCommands = 256;

    private readonly string _pipeName;
    private readonly IIpcHandler _handler;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _clientsLock = new();
    private readonly List<Client> _clients = [];
    private readonly object _handlerLock = new();
    private readonly Dictionary<string, IpcMessage> _recent = [];
    private readonly Queue<string> _recentOrder = new();
    private long _seq;
    private Task? _acceptLoop;
    private int _disposed;

    public IpcServer(string pipeName, IIpcHandler handler, Action<string>? log = null)
    {
        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _log = log ?? (_ => { });
    }

    public int ClientCount
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Count;
            }
        }
    }

    public void Start() => _acceptLoop ??= Task.Run(AcceptLoopAsync);

    /// <summary>Salje stanje svim povezanim klijentima. Nikad ne blokira.</summary>
    public void Broadcast(RadioState state)
    {
        var frame = IpcProtocol.Encode(new IpcMessage { Type = "state", Seq = Interlocked.Increment(ref _seq), State = state });
        Client[] clients;
        lock (_clientsLock)
        {
            clients = [.. _clients];
        }

        foreach (var client in clients)
        {
            if (!client.Outgoing.Writer.TryWrite(frame))
            {
                _log("Klijent ne cita poruke; veza se prekida.");
                client.Close();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        Client[] clients;
        lock (_clientsLock)
        {
            clients = [.. _clients];
        }

        foreach (var client in clients)
        {
            client.Close();
        }

        try
        {
            _acceptLoop?.Wait(2000);
        }
        catch (AggregateException)
        {
            // gasenje
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 65536,
                    outBufferSize: 65536);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log("Greska pri prihvatanju veze: " + ex.Message);
                await Task.Delay(500).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        var client = new Client(pipe);
        try
        {
            // rukovanje: klijent mora prvi da se javi, u roku od 5 s, istom verzijom protokola
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                var hello = await IpcProtocol.ReadAsync(pipe, handshake.Token).ConfigureAwait(false);
                var ok = hello is { Type: "hello", Protocol: IpcProtocol.Version };
                await IpcProtocol.WriteAsync(
                    pipe,
                    new IpcMessage { Type = "hello", Protocol = IpcProtocol.Version, Ok = ok, Error = ok ? null : "protocol" },
                    handshake.Token).ConfigureAwait(false);
                if (!ok)
                {
                    return;
                }
            }

            lock (_clientsLock)
            {
                _clients.Add(client);
            }

            client.Outgoing.Writer.TryWrite(IpcProtocol.Encode(
                new IpcMessage { Type = "state", Seq = Interlocked.Increment(ref _seq), State = _handler.CurrentState() }));
            var writer = Task.Run(() => WriteLoopAsync(client));

            while (!client.Closed.IsCancellationRequested)
            {
                var message = await IpcProtocol.ReadAsync(pipe, client.Closed.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                var response = message.Type == "cmd" ? Execute(client, message) : IpcMessage.Failure(message.Id, "bad-message");
                if (!client.Outgoing.Writer.TryWrite(IpcProtocol.Encode(response)))
                {
                    break;
                }
            }

            client.Close();
            await writer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            // klijent je nestao, poslao neispravan okvir ili se server gasi
        }
        catch (Exception ex)
        {
            _log("Greska u vezi sa klijentom: " + ex);
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }

            client.Close();
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IpcMessage Execute(Client client, IpcMessage command)
    {
        if (string.IsNullOrEmpty(command.Id) || string.IsNullOrEmpty(command.Name))
        {
            return IpcMessage.Failure(command.Id, "bad-message");
        }

        if (!client.AllowCommand(MaxCommandsPerSecond))
        {
            return IpcMessage.Failure(command.Id, "rate-limit");
        }

        lock (_handlerLock)
        {
            if (_recent.TryGetValue(command.Id, out var previous))
            {
                return previous;
            }

            IpcMessage response;
            try
            {
                response = _handler.Handle(command) with { Type = "res", Id = command.Id };
            }
            catch (Exception ex)
            {
                _log($"Komanda '{command.Name}' je bacila gresku: {ex}");
                response = IpcMessage.Failure(command.Id, "internal-error");
            }

            _recent[command.Id] = response;
            _recentOrder.Enqueue(command.Id);
            if (_recentOrder.Count > RememberedCommands)
            {
                _recent.Remove(_recentOrder.Dequeue());
            }

            return response;
        }
    }

    private async Task WriteLoopAsync(Client client)
    {
        try
        {
            await foreach (var frame in client.Outgoing.Reader.ReadAllAsync(client.Closed.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(client.Closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await client.Pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                await client.Pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // klijent ne cita ili je nestao
        }
        finally
        {
            client.Close();
        }
    }

    private sealed class Client(NamedPipeServerStream pipe)
    {
        private readonly object _rateLock = new();
        private long _windowStart = Environment.TickCount64;
        private int _inWindow;
        private int _closed;

        public NamedPipeServerStream Pipe { get; } = pipe;

        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutgoingQueueSize) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        public CancellationTokenSource Closed { get; } = new();

        public bool AllowCommand(int perSecond)
        {
            lock (_rateLock)
            {
                var now = Environment.TickCount64;
                if (now - _windowStart >= 1000)
                {
                    _windowStart = now;
                    _inWindow = 0;
                }

                return ++_inWindow <= perSecond;
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Outgoing.Writer.TryComplete();
                try
                {
                    Closed.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // vec zatvoreno
                }
            }
        }
    }
}
