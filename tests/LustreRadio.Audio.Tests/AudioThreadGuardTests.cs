using LustreRadio.Audio;
using LustreRadio.Core.Diagnostics;
using Xunit;

namespace LustreRadio.Audio.Tests;

[CollectionDefinition(nameof(AudioThreadGuardCollection), DisableParallelization = true)]
public sealed class AudioThreadGuardCollection;

/// <summary>
/// Faza B0: cuvar audio-niti samo broji. Ovi testovi proveravaju da broji tacno ono sto treba,
/// i da ne menja ponasanje. Brojaci su zajednicki za proces, pa testovi rade sami (bez paralelnog rada).
/// </summary>
[Collection(nameof(AudioThreadGuardCollection))]
public class AudioThreadGuardTests
{
    [Fact]
    public void Touch_OnOrdinaryThread_IsNotCounted()
    {
        var before = AudioThreadGuard.Total;
        RunOnNewThread(() =>
        {
            AudioThreadGuard.Touch(AudioViolation.Decode);
            AudioThreadGuard.Touch(AudioViolation.Database);
        });

        Assert.Equal(before, AudioThreadGuard.Total);
    }

    [Fact]
    public void Touch_OnMarkedThread_CountsEachKind()
    {
        var file = AudioThreadGuard.Count(AudioViolation.FileAccess);
        var decode = AudioThreadGuard.Count(AudioViolation.Decode);
        var db = AudioThreadGuard.Count(AudioViolation.Database);
        var log = AudioThreadGuard.Count(AudioViolation.Log);
        var lck = AudioThreadGuard.Count(AudioViolation.Lock);

        RunOnNewThread(() =>
        {
            AudioThreadGuard.MarkCurrentThread();
            AudioThreadGuard.Touch(AudioViolation.FileAccess);
            AudioThreadGuard.Touch(AudioViolation.Decode);
            AudioThreadGuard.Touch(AudioViolation.Decode);
            AudioThreadGuard.Touch(AudioViolation.Database);
            AudioThreadGuard.Touch(AudioViolation.Log);
            AudioThreadGuard.Touch(AudioViolation.Lock);
            AudioThreadGuard.UnmarkCurrentThread();
            AudioThreadGuard.Touch(AudioViolation.Decode); // posle skidanja oznake se ne broji
        });

        Assert.Equal(file + 1, AudioThreadGuard.Count(AudioViolation.FileAccess));
        Assert.Equal(decode + 2, AudioThreadGuard.Count(AudioViolation.Decode));
        Assert.Equal(db + 1, AudioThreadGuard.Count(AudioViolation.Database));
        Assert.Equal(log + 1, AudioThreadGuard.Count(AudioViolation.Log));
        Assert.Equal(lck + 1, AudioThreadGuard.Count(AudioViolation.Lock));
    }

    [Fact]
    public void RenderPump_MarksItsThread_AndRecordsSteps()
    {
        var provider = new ProbeProvider(blocks: 20);
        var ring = new FloatRingBuffer(65536);
        var steps = AudioMetrics.Steps;
        var decode = AudioThreadGuard.Count(AudioViolation.Decode);

        using (var pump = new RenderPump(provider, limiter: null, ring, blockFrames: 256))
        {
            pump.Start();
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (!pump.Finished && DateTime.UtcNow < limit)
            {
                var drain = new float[4096];
                ring.Read(drain);
                pump.Signal();
                Thread.Sleep(5);
            }

            Assert.True(pump.Finished);
        }

        Assert.True(provider.SawAudioThread, "nit za pripremu zvuka mora biti obelezena");
        Assert.Equal(decode + 20, AudioThreadGuard.Count(AudioViolation.Decode));
        Assert.True(AudioMetrics.Steps >= steps + 20);
        Assert.True(AudioMetrics.MaxStepMilliseconds >= 0);
    }

    [Fact]
    public void DecodersOpenAndRead_OnMarkedThread_AreCounted_OnOrdinaryThread_AreNot()
    {
        var path = AudioFixtures.WriteWav(floatFormat: false);
        try
        {
            var file = AudioThreadGuard.Count(AudioViolation.FileAccess);
            var decode = AudioThreadGuard.Count(AudioViolation.Decode);
            using (var source = Decoders.Open(path, AudioFixtures.Target))
            {
                source.Read(new float[1024]);
            }

            Assert.Equal(file, AudioThreadGuard.Count(AudioViolation.FileAccess));
            Assert.Equal(decode, AudioThreadGuard.Count(AudioViolation.Decode));

            RunOnNewThread(() =>
            {
                AudioThreadGuard.MarkCurrentThread();
                using var source = Decoders.Open(path, AudioFixtures.Target);
                source.Read(new float[1024]);
            });

            Assert.Equal(file + 1, AudioThreadGuard.Count(AudioViolation.FileAccess));
            Assert.True(AudioThreadGuard.Count(AudioViolation.Decode) >= decode + 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Metrics_RecordStep_KeepsMaximumAndThresholds()
    {
        var over50 = AudioMetrics.StepsOver50ms;
        var allocated = AudioMetrics.RenderAllocatedBytes;
        var tick = System.Diagnostics.Stopwatch.Frequency / 1000; // 1 ms
        AudioMetrics.RecordStep(tick * 60, allocatedBytes: 128);
        Assert.True(AudioMetrics.MaxStepMilliseconds >= 59.9);
        Assert.Equal(over50 + 1, AudioMetrics.StepsOver50ms);
        Assert.Equal(allocated + 128, AudioMetrics.RenderAllocatedBytes);
    }

    private static void RunOnNewThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("Greska na probnoj niti.", error);
        }
    }

    /// <summary>Daje tisinu zadat broj blokova i pri svakom citanju prijavljuje "dekodiranje".</summary>
    private sealed class ProbeProvider(int blocks) : IFrameProvider
    {
        private int _left = blocks;

        public bool SawAudioThread { get; private set; }

        public AudioFormat Format => Signal.Format;

        public int Read(Span<float> buffer)
        {
            if (_left == 0)
            {
                return 0;
            }

            _left--;
            SawAudioThread |= AudioThreadGuard.IsAudioThread;
            AudioThreadGuard.Touch(AudioViolation.Decode);
            buffer.Clear();
            return buffer.Length / 2;
        }
    }
}
