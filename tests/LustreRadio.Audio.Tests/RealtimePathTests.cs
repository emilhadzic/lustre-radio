using LustreRadio.Audio;
using LustreRadio.Core.Loudness;
using Xunit;

namespace LustreRadio.Audio.Tests;

public class RealtimePathTests
{
    private static readonly AudioFormat Format = Signal.Format;

    [Fact]
    public void RingBuffer_WrapsAndKeepsOrder()
    {
        var ring = new FloatRingBuffer(8);
        Assert.Equal(8, ring.Capacity);
        Assert.Equal(6, ring.Write([1, 2, 3, 4, 5, 6]));
        var a = new float[4];
        Assert.Equal(4, ring.Read(a));
        Assert.Equal([1, 2, 3, 4], a);
        Assert.Equal(6, ring.Write([7, 8, 9, 10, 11, 12, 13, 14])); // staje samo 6
        var b = new float[16];
        Assert.Equal(8, ring.Read(b));
        Assert.Equal([5, 6, 7, 8, 9, 10, 11, 12], b[..8]);
        Assert.Equal(0, ring.Read(b));
    }

    [Fact]
    public void RingBuffer_TwoThreads_NoLossNoReorder()
    {
        const int Total = 2_000_000;
        var ring = new FloatRingBuffer(1024);
        var writer = new Thread(() =>
        {
            var chunk = new float[97];
            var next = 0;
            while (next < Total)
            {
                var n = Math.Min(chunk.Length, Total - next);
                for (var i = 0; i < n; i++)
                {
                    chunk[i] = next + i;
                }

                var written = ring.Write(chunk.AsSpan(0, n));
                next += written;
                if (written == 0)
                {
                    Thread.Yield();
                }
            }
        });
        writer.Start();

        var buffer = new float[61];
        var expected = 0;
        while (expected < Total)
        {
            var n = ring.Read(buffer);
            for (var i = 0; i < n; i++)
            {
                Assert.Equal(expected + i, (int)buffer[i]);
            }

            expected += n;
            if (n == 0)
            {
                Thread.Yield();
            }
        }

        writer.Join();
    }

    [Fact] // put kroz nit i kruzni bafer daje isti zvuk kao offline renderer
    public void RenderPump_ProducesSameAudioAsOfflineRenderer()
    {
        Func<CrossfadeSequencer> make = () => new CrossfadeSequencer(
            Format,
            Signal.Queue(Signal.Tone("a", 400, 2), Signal.Tone("b", 1000, 2), Signal.Tone("c", 600, 2, amplitude: 1f)),
            crossfadeSeconds: 0.7);

        var offline = Signal.Render(make());

        var ring = new FloatRingBuffer(16384);
        var limiter = new Limiter(Format);
        using var pump = new RenderPump(make(), limiter, ring);
        Exception? error = null;
        pump.Error += ex => error = ex;
        pump.Start();

        var collected = new MemorySink();
        var chunk = new float[882]; // kao da uredjaj trazi po 10 ms
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var n = ring.Read(chunk);
            pump.Signal();
            if (n > 0)
            {
                collected.Write(chunk.AsSpan(0, n));
            }
            else if (pump.Finished && ring.AvailableToRead == 0)
            {
                break;
            }
        }

        Assert.Null(error);
        Assert.True(pump.Finished);
        var skip = limiter.LatencyFrames * 2;
        Assert.Equal(offline.Samples.Length, collected.Samples.Length - skip);
        Assert.True(offline.Samples.SequenceEqual(collected.Samples[skip..]));
    }

    [Fact]
    public void LimitedSource_CutsAndReportsLength()
    {
        using var source = new LimitedSource(new ToneSource(Format, 400, 0.25f, Format.FramesFromSeconds(10)), Format.FramesFromSeconds(1));
        Assert.Equal(Format.FramesFromSeconds(1), source.LengthFrames);
        Assert.Equal(Format.FramesFromSeconds(1) * 2, AudioFixtures.ReadAll(source).Length);
    }

    [Theory] // T-AUD-07: posle pojacanja po pesmi vrh ostaje ispod granice i bez limitera
    [InlineData(-30.0, 0.05f)]
    [InlineData(-23.0, 0.95f)]
    [InlineData(-8.0, 1.0f)]
    public void TrackGain_KeepsPeakBelowCeiling_WithoutLimiter(double lufs, float peak)
    {
        var settings = new LoudnessSettings();
        var gainDb = TrackGain.ComputeDb(lufs, 20 * Math.Log10(peak), settings);
        var track = new QueuedTrack("t", new ToneSource(Format, 400, peak, Format.FramesFromSeconds(1)), TrackGain.ToLinear(gainDb));
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(track), crossfadeSeconds: 0);

        var sink = Signal.Render(sequencer, limiter: false);

        Assert.True(Signal.Peak(sink.Samples) <= TrackGain.ToLinear(settings.PeakCeilingDb) + 1e-4f, $"vrh {Signal.Peak(sink.Samples)}");
        Assert.True(gainDb <= settings.MaxBoostDb);
    }
}
