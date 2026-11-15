using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using LustreRadio.Ipc;
using Xunit;

namespace LustreRadio.Ipc.Tests;

/// <summary>Testovi T-IPC-01 do 04 i 07 (spec. deo 19) preko pravog lokalnog kanala.</summary>
public sealed class IpcTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _pipe = "LustreRadio.Test." + Guid.NewGuid().ToString("N");
    private readonly FakeHandler _handler = new();
    private readonly IpcServer _server;

    public IpcTests()
    {
        _server = new IpcServer(_pipe, _handler);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private async Task<NamedPipeClientStream> RawConnectAsync(bool hello = true)
    {
        var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)Timeout.TotalMilliseconds);
        if (hello)
        {
            await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "hello", Protocol = IpcProtocol.Version }, default);
            Assert.True((await IpcProtocol.ReadAsync(pipe, default))!.Ok);
            Assert.Equal("state", (await IpcProtocol.ReadAsync(pipe, default))!.Type);
        }

        return pipe;
    }

    [Fact]
    public async Task Client_GetsSnapshotOnConnect_ThenUpdates_AndCommandResponses()
    {
        var states = new List<RadioState>();
        await using var client = await IpcClient.ConnectAsync(_pipe, Timeout, s =>
        {
            lock (states)
            {
                states.Add(s);
            }
        });

        Assert.True(SpinWait.SpinUntil(() => client.LastState is not null, Timeout));
        Assert.Equal("pocetno", client.LastState!.Title);

        var response = await client.SendAsync("echo", new() { ["x"] = "1" });
        Assert.True(response.Ok);
        Assert.Equal("1", response.Data!["x"]);

        _server.Broadcast(new RadioState { Title = "nova pesma", OnAir = true });
        Assert.True(SpinWait.SpinUntil(() => client.LastState!.Title == "nova pesma", Timeout));

        var failed = await client.SendAsync("fail");
        Assert.False(failed.Ok);
        Assert.Equal("nope", failed.Error);

        var crashed = await client.SendAsync("throw");
        Assert.Equal("internal-error", crashed.Error);
        Assert.True((await client.SendAsync("echo")).Ok); // server je preziveo gresku u komandi
    }

    [Fact] // T-IPC-01: interfejs koji ne cita poruke ne sme da zaustavi audio proces
    public async Task ClientThatNeverReads_IsDropped_AndServerNeverBlocks()
    {
        using var stuck = await RawConnectAsync();
        Assert.True(SpinWait.SpinUntil(() => _server.ClientCount == 1, Timeout));

        var big = new RadioState { Title = new string('x', 4000) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
        {
            _server.Broadcast(big);
        }

        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"slanje je trajalo {watch.Elapsed}");
        Assert.True(SpinWait.SpinUntil(() => _server.ClientCount == 0, Timeout), "klijent koji ne cita nije iskljucen");

        // novi klijent i dalje radi normalno
        await using var healthy = await IpcClient.ConnectAsync(_pipe, Timeout);
        Assert.True((await healthy.SendAsync("echo")).Ok);
    }

    [Fact] // T-IPC-02
    public async Task MalformedMessages_AreRejected_ConnectionSurvives()
    {
        using var pipe = await RawConnectAsync();
        var garbage = Encoding.UTF8.GetBytes("{ ovo nije json ]");
        var frame = new byte[4 + garbage.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, garbage.Length);
        garbage.CopyTo(frame, 4);

        for (var i = 0; i < 1000; i++)
        {
            await pipe.WriteAsync(frame);
            var reply = await IpcProtocol.ReadAsync(pipe, default);
            Assert.Equal("bad-message", reply!.Error);
        }

        // komanda bez ID-ja ili imena, pogresan tip
        await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "cmd", Name = "echo" }, default);
        Assert.Equal("bad-message", (await IpcProtocol.ReadAsync(pipe, default))!.Error);
        await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "nesto", Id = "1" }, default);
        Assert.Equal("bad-message", (await IpcProtocol.ReadAsync(pipe, default))!.Error);

        await IpcProtocol.WriteAsync(pipe, new IpcMessage { Type = "cmd", Id = "ok1", Name = "echo" }, default);
        Assert.True((await IpcProtocol.ReadAsync(pipe, default))!.Ok);
        Assert.Equal(1, _handler.Calls);
    }

    [Theory] // T-IPC-02: prevelika ili negativna duzina zatvara tu vezu, server ostaje
    [InlineData(int.MaxValue)]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task InvalidFrameLength_ClosesThatConnection_ServerKeepsRunning(int length)
    {
        using (var pipe = await RawConnectAsync())
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await pipe.WriteAsync(header);
            using var limit = new CancellationTokenSource(Timeout);
            Assert.Null(await IpcProtocol.ReadAsync(pipe, limit.Token)); // server je zatvorio vezu
        }

        await using var client = await IpcClient.ConnectAsync(_pipe, Timeout);
        Assert.True((await client.SendAsync("echo")).Ok);
    }

    [Fact] // T-IPC-03
    public async Task ReconnectHundredTimes_AlwaysGetsFreshSnapshot()
    {
        for (var i = 0; i < 100; i++)
        {
            _handler.Title = "stanje-" + i;
            await using var client = await IpcClient.ConnectAsync(_pipe, Timeout);
            Assert.True(SpinWait.SpinUntil(() => client.LastState is not null, Timeout));
            Assert.Equal("stanje-" + i, client.LastState!.Title);
        }

        Assert.True(SpinWait.SpinUntil(() => _server.ClientCount == 0, Timeout));
    }

    [Fact] // T-IPC-04
    public async Task SameCommandIdTwice_IsExecutedOnce()
    {
        await using var client = await IpcClient.ConnectAsync(_pipe, Timeout);

        var first = await client.SendAsync("count", id: "ista-komanda");
        var second = await client.SendAsync("count", id: "ista-komanda");
        var third = await client.SendAsync("count", id: "druga-komanda");

        Assert.Equal("1", first.Data!["n"]);
        Assert.Equal("1", second.Data!["n"]);
        Assert.Equal("2", third.Data!["n"]);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact] // T-IPC-07
    public async Task DifferentProtocolVersion_IsRefusedWithClearError()
    {
        var error = await Assert.ThrowsAsync<IpcProtocolException>(() => IpcClient.ConnectAsync(_pipe, Timeout, protocol: IpcProtocol.Version + 1));
        Assert.Contains("verziju protokola", error.Message);
        Assert.Equal(0, _handler.Calls);

        // klijent koji se ne predstavi ne moze da salje komande
        using var silent = await RawConnectAsync(hello: false);
        await IpcProtocol.WriteAsync(silent, new IpcMessage { Type = "cmd", Id = "x", Name = "echo" }, default);
        var reply = await IpcProtocol.ReadAsync(silent, default);
        Assert.False(reply!.Ok);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task TooManyCommandsPerSecond_AreRefused()
    {
        await using var client = await IpcClient.ConnectAsync(_pipe, Timeout);
        // 200 komandi odjednom: vise od granice (50 u sekundi), a manje od reda odgovora (256).
        // Sa 300 je test povremeno padao (15.11.2026.): odgovori su prepunili red, pa je server,
        // po drugom pravilu, prekinuo vezu sa klijentom koji ne stize da cita.
        var tasks = Enumerable.Range(0, 200).Select(_ => client.SendAsync("echo")).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Contains(results, r => r.Error == "rate-limit");
        Assert.True(results.Count(r => r.Ok == true) >= 50);
        Assert.True(_handler.Calls <= 150);
    }

    [Fact]
    public async Task ServerStops_ClientNoticesDisconnect_AndPendingCommandsFail()
    {
        var disconnected = new ManualResetEventSlim();
        var client = await IpcClient.ConnectAsync(_pipe, Timeout);
        client.Disconnected += disconnected.Set;

        _server.Dispose();

        Assert.True(disconnected.Wait(Timeout));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("echo"));
        await client.DisposeAsync();
    }

    private sealed class FakeHandler : IIpcHandler
    {
        private int _calls;
        private int _counter;

        public int Calls => Volatile.Read(ref _calls);

        public string Title { get; set; } = "pocetno";

        public RadioState CurrentState() => new() { Title = Title };

        public IpcMessage Handle(IpcMessage command)
        {
            Interlocked.Increment(ref _calls);
            return command.Name switch
            {
                "echo" => IpcMessage.Success(command.Id, command.Args),
                "count" => IpcMessage.Success(command.Id, new() { ["n"] = (++_counter).ToString() }),
                "fail" => IpcMessage.Failure(command.Id, "nope"),
                "throw" => throw new InvalidOperationException("namerna greska (test)"),
                _ => IpcMessage.Failure(command.Id, "unknown-command"),
            };
        }
    }
}
