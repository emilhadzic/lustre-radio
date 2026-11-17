using LustreRadio.Core.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Audio.Tests;

/// <summary>Faza B, korak B5: nit zvucnog uredjaja (WASAPI) preko laznog uredjaja.</summary>
public sealed class OutputThreadTests(ITestOutputHelper output)
{
    [Fact] // B-02c: nit uredjaja uzima zvuk iz izlaznog bafera: 0 bajtova, 0 prijava cuvara
    public void B02c_DeviceThread_ZeroBytes_NoViolations()
    {
        var format = AudioFormat.Stereo44100;
        var sequencer = new CrossfadeSequencer(format, Signal.Queue(Signal.Tone("a", 400, 900)), crossfadeSeconds: 1);
        var ring = new FloatRingBuffer(65536);
        using var pump = new RenderPump(sequencer, new Limiter(format), ring);
        var puller = new OutputPuller(ring, pump);
        long underrun = 0;
        Action<int> report = frames => Interlocked.Add(ref underrun, frames); // isti oblik kao u ResilientOutput
        long allocated = -1, violations = -1, read = 0;
        pump.Start();
        var thread = new Thread(() =>
        {
            AudioThreadGuard.MarkCurrentThread();
            var buffer = new float[480 * 2]; // 10 ms, kao uredjaj
            for (var i = 0; i < 2000; i++)
            {
                Pull(buffer); // zagrevanje
            }

            var guard = AudioThreadGuard.CurrentThreadCount;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 60_000; i++) // 10 minuta zvuka
            {
                read += Pull(buffer);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            violations = AudioThreadGuard.CurrentThreadCount - guard;

            int Pull(float[] b)
            {
                var n = puller.Read(b);
                if (n < b.Length)
                {
                    report((b.Length - n) / 2);
                    Thread.Yield();
                }

                return n;
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)));
        output.WriteLine($"procitano {read / 2 / 44100.0:F0} s zvuka, prazno {underrun / 44100.0:F1} s, zauzeto {allocated} B, cuvar {violations}");
        Assert.Equal(0, allocated);
        Assert.Equal(0, violations);
    }
}
