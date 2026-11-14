using System.Diagnostics;
using System.IO.Pipes;
using LustreRadio.Ipc;
using Xunit;

namespace LustreRadio.Ipc.Tests;

/// <summary>
/// Druga strana koja se javi pa prestane da cita (zamrznut audio proces).
/// Greska pronadjena integracionim testom nadzora 14.11.2026: slanje je cekalo zauvek,
/// pa je i nadzor stao bas onda kada je trebalo da reaguje.
/// </summary>
public sealed class FrozenPeerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task Send_ToPeerThatStoppedReading_FailsWithinTimeout_AndNeverHangs(int bufferSize)
    {
        var name = "LustreRadio.Test." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, bufferSize, bufferSize);
        var accept = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            Assert.Equal("hello", (await IpcProtocol.ReadAsync(server, default))!.Type);
            await IpcProtocol.WriteAsync(server, new IpcMessage { Type = "hello", Ok = true, Protocol = IpcProtocol.Version }, default);
            // od sada: ni citanje ni pisanje, kao zamrznut proces
        });

        var client = await IpcClient.ConnectAsync(name, TimeSpan.FromSeconds(10));
        await accept;

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            var send = client.SendAsync("get-state", new Dictionary<string, string> { ["punjenje"] = new string('x', 4000) }, timeout: TimeSpan.FromSeconds(1));
            var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(8)));
            Assert.True(ReferenceEquals(finished, send), "slanje se zaglavilo");
            var error = await Record.ExceptionAsync(() => send);
            Assert.True(error is TimeoutException or IOException, "ocekivan istek roka ili prekid veze, dobijeno: " + error);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"trajalo {watch.Elapsed.TotalSeconds:F1} s");

        var dispose = client.DisposeAsync().AsTask();
        Assert.True(ReferenceEquals(await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(8))), dispose), "zatvaranje veze se zaglavilo");
    }
}
