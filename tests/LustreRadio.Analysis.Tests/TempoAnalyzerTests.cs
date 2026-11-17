using LustreRadio.Analysis;
using Xunit;

namespace LustreRadio.Analysis.Tests;

/// <summary>FINAL, korak 2 (DJ Mix): tempo, mreza udaraca i stabilnost na klik-trakama (spec. 6.3: tempo +-0,1 %, udarac +-10 ms).</summary>
public sealed class TempoAnalyzerTests
{
    private const int Rate = 44100;

    /// <summary>Klik-traka: kratak udarac (1 kHz, 15 ms) na svakom udarcu; tempo moze da klizi; udarci mogu da "setaju".</summary>
    public static float[] Clicks(double seconds, double bpmFrom, double bpmTo, double firstBeat, double jitterMs = 0, int seed = 1)
    {
        var frames = (int)(seconds * Rate);
        var data = new float[frames * 2];
        var random = new Random(seed);
        var t = firstBeat;
        while (t < seconds - 0.1)
        {
            var at = t + ((random.NextDouble() - 0.5) * 2 * jitterMs / 1000);
            var start = (int)Math.Round(at * Rate);
            for (var i = 0; i < (int)(0.015 * Rate) && start + i < frames; i++)
            {
                if (start + i < 0)
                {
                    continue;
                }

                var v = (float)(0.6 * Math.Exp(-i / (0.004 * Rate)) * Math.Sin(2 * Math.PI * 1000 * i / Rate));
                data[2 * (start + i)] += v;
                data[(2 * (start + i)) + 1] += v;
            }

            var bpm = bpmFrom + ((bpmTo - bpmFrom) * t / seconds);
            t += 60 / bpm;
        }

        return data;
    }

    private static TempoResult? Analyze(float[] data)
    {
        var analyzer = new TempoAnalyzer(Rate);
        for (var i = 0; i < data.Length; i += 8192)
        {
            analyzer.Add(data.AsSpan(i, Math.Min(8192, data.Length - i)));
        }

        return analyzer.Result();
    }

    [Theory]
    [InlineData(120.0, 0.5)]
    [InlineData(97.3, 1.25)]
    [InlineData(128.0, 2.0)]
    [InlineData(84.0, 0.1)]
    public void StableClickTrack_TempoWithin0_1Percent_FirstBeatWithin10ms(double bpm, double firstBeat)
    {
        var r = Analyze(Clicks(180, bpm, bpm, firstBeat))!;
        Assert.True(r.Stable);
        Assert.InRange(r.Bpm, bpm * 0.999, bpm * 1.001);
        var period = 60.0 / bpm;
        var offset = ((r.FirstBeatFrame / (double)Rate) - firstBeat) % period;
        offset = Math.Min(Math.Abs(offset), period - Math.Abs(offset)); // isti udarac ili ceo broj udaraca
        Assert.True(offset <= 0.010, $"prvi udarac odstupa {offset * 1000:F1} ms");
    }

    [Fact] // tempo koji klizi 118 -> 124 BPM: nije stabilan (nikad DJ Mix)
    public void DriftingTempo_IsNotStable() => Assert.False(Analyze(Clicks(180, 118, 124, 0.5))?.Stable ?? false);

    [Fact] // udarci koji "setaju" +-60 ms (svirano uzivo): nije stabilan
    public void LooseTiming_IsNotStable() => Assert.False(Analyze(Clicks(180, 110, 110, 0.5, jitterMs: 60))?.Stable ?? false);

    [Fact] // bez ritma: nema rezultata
    public void SteadyTone_HasNoTempo()
    {
        var data = new float[60 * Rate * 2];
        for (var i = 0; i < data.Length / 2; i++)
        {
            data[2 * i] = data[(2 * i) + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        }

        Assert.False(Analyze(data)?.Stable ?? false);
    }
}
