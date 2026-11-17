using LustreRadio.Analysis;
using Xunit;

namespace LustreRadio.Analysis.Tests;

/// <summary>FINAL, korak 1 (Smart Crossfade): tisina na krajevima i stisavanje na kraju pesme, na vestackim signalima.</summary>
public sealed class EdgeAnalyzerTests
{
    private const int Rate = 44100;

    /// <summary>Signal od delova: (sekunde, amplituda na pocetku, amplituda na kraju); amplituda 0 = tisina.</summary>
    private static EdgeResult? Analyze(params (double Seconds, double From, double To)[] parts)
    {
        var analyzer = new EdgeAnalyzer(Rate);
        var buffer = new float[4096 * 2];
        long n = 0;
        foreach (var (seconds, from, to) in parts)
        {
            var total = (int)Math.Round(seconds * Rate);
            var done = 0;
            while (done < total)
            {
                var count = Math.Min(4096, total - done);
                for (var i = 0; i < count; i++)
                {
                    var amplitude = from + ((to - from) * (done + i) / total);
                    var value = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * n++ / Rate));
                    buffer[2 * i] = value;
                    buffer[(2 * i) + 1] = value;
                }

                analyzer.Add(buffer.AsSpan(0, count * 2));
                done += count;
            }
        }

        return analyzer.Result();
    }

    [Fact] // tisina na pocetku i kraju: granice tacne do 10 ms (+ 10 ms rezerve)
    public void SilenceAtBothEnds_IsFoundWithin20ms()
    {
        var r = Analyze((2, 0, 0), (20, 0.5, 0.5), (3, 0, 0))!;
        Assert.InRange(r.AudioStartFrame, (long)(1.98 * Rate), 2 * Rate);
        Assert.InRange(r.AudioEndFrame, 22 * Rate, (long)(22.02 * Rate));
        Assert.Null(r.OutroSeconds); // nagao kraj
    }

    [Fact] // tih ali cujan zvuk (-50 dBFS) se ne sece
    public void QuietButAudible_IsNotTrimmed()
    {
        var quiet = Math.Pow(10, -50 / 20.0);
        var r = Analyze((3, quiet, quiet), (20, 0.5, 0.5), (3, quiet, quiet))!;
        Assert.InRange(r.AudioStartFrame, 0, Rate / 100);
        Assert.InRange(r.AudioEndFrame, (long)(25.99 * Rate), 26 * Rate);
    }

    [Fact]
    public void AllSilence_HasNoResult() => Assert.Null(Analyze((10, 0, 0)));

    [Fact] // pesma koja se stisava 8 s: prepoznato stisavanje, pretapanje 3-8 s
    public void FadeOut_IsDetected()
    {
        var r = Analyze((30, 0.5, 0.5), (8, 0.5, 0))!;
        Assert.NotNull(r.OutroSeconds);
        Assert.InRange(r.OutroSeconds!.Value, EdgeAnalyzer.MinOutroSeconds, 8);
    }

    [Fact] // kratak pad na kraju (1 s) nije stisavanje
    public void ShortDrop_IsNotAnOutro() => Assert.Null(Analyze((30, 0.5, 0.5), (1, 0.5, 0))!.OutroSeconds);
}
