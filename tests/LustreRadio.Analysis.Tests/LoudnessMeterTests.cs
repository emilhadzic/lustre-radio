using LustreRadio.Analysis;
using Xunit;

namespace LustreRadio.Analysis.Tests;

/// <summary>
/// Signali su napravljeni po opisu iz EBU Tech 3341 (sinus 1000 Hz, stereo, zadati nivoi i trajanja).
/// To NISU zvanicni EBU fajlovi, ali su isti signali; ocekivane vrednosti i tolerancija ±0,1 LU su iz tog dokumenta.
/// </summary>
public sealed class LoudnessMeterTests
{
    private const double Tolerance = 0.1;

    private static void AddSine(LoudnessMeter meter, double seconds, double dbfs, double frequency = 1000, bool rightSilent = false, int chunkFrames = 4096)
    {
        var amplitude = Math.Pow(10, dbfs / 20);
        var total = (int)Math.Round(seconds * meter.SampleRate);
        var buffer = new float[chunkFrames * 2];
        var done = 0;
        while (done < total)
        {
            var n = Math.Min(chunkFrames, total - done);
            for (var i = 0; i < n; i++)
            {
                var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (done + i) / meter.SampleRate));
                buffer[2 * i] = value;
                buffer[(2 * i) + 1] = rightSilent ? 0 : value;
            }

            meter.Add(buffer.AsSpan(0, n * 2));
            done += n;
        }
    }

    [Theory] // Tech 3341, slucajevi 1 i 2
    [InlineData(48000, -23.0)]
    [InlineData(44100, -23.0)]
    [InlineData(48000, -33.0)]
    [InlineData(44100, -33.0)]
    [InlineData(96000, -23.0)]
    [InlineData(22050, -23.0)]
    public void SteadySine_ReadsItsLevel(int rate, double level)
    {
        var meter = new LoudnessMeter(rate);
        AddSine(meter, 20, level);
        Assert.Equal(level, meter.IntegratedLufs()!.Value, Tolerance);
    }

    [Theory] // Tech 3341, slucaj 3: tihi delovi ne ulaze u racun (relativni prag)
    [InlineData(48000)]
    [InlineData(44100)]
    public void QuietPartsAreGatedOut(int rate)
    {
        var meter = new LoudnessMeter(rate);
        AddSine(meter, 10, -36);
        AddSine(meter, 60, -23);
        AddSine(meter, 10, -36);
        Assert.Equal(-23.0, meter.IntegratedLufs()!.Value, Tolerance);
    }

    [Fact] // Tech 3341, slucaj 4: i apsolutni prag (−70)
    public void NearSilenceIsGatedOut()
    {
        var meter = new LoudnessMeter(48000);
        AddSine(meter, 10, -72);
        AddSine(meter, 10, -36);
        AddSine(meter, 60, -23);
        AddSine(meter, 10, -36);
        AddSine(meter, 10, -72);
        Assert.Equal(-23.0, meter.IntegratedLufs()!.Value, Tolerance);
    }

    [Fact] // Tech 3341, slucaj 5: delovi blizu praga ulaze u racun
    public void PartsAboveTheRelativeGateAreAveraged()
    {
        var meter = new LoudnessMeter(48000);
        AddSine(meter, 20, -26);
        AddSine(meter, 20.1, -20);
        AddSine(meter, 20, -26);
        Assert.Equal(-23.0, meter.IntegratedLufs()!.Value, Tolerance);
    }

    [Fact]
    public void OneChannelOnly_IsThreeDecibelsQuieter()
    {
        var meter = new LoudnessMeter(48000);
        AddSine(meter, 20, -20, rightSilent: true);
        Assert.Equal(-23.01, meter.IntegratedLufs()!.Value, Tolerance);
    }

    [Fact]
    public void LowBass_CountsLessThanMidrange_AndTrebleMore()
    {
        double At(double frequency)
        {
            var meter = new LoudnessMeter(48000);
            AddSine(meter, 10, -23, frequency);
            return meter.IntegratedLufs()!.Value;
        }

        Assert.True(At(40) < At(1000) - 3, $"40 Hz: {At(40):F2}");
        Assert.InRange(At(10000) - At(1000), 2.5, 4.5); // visoka polica: oko +4 dB minus 0,7
    }

    [Theory]
    [InlineData(1)]
    [InlineData(333)]
    [InlineData(4801)]
    public void ResultDoesNotDependOnChunkSize(int chunkFrames)
    {
        var whole = new LoudnessMeter(44100);
        AddSine(whole, 5, -18, 440);
        var chunked = new LoudnessMeter(44100);
        AddSine(chunked, 5, -18, 440, chunkFrames: chunkFrames);
        Assert.Equal(whole.IntegratedLufs()!.Value, chunked.IntegratedLufs()!.Value, 9);
    }

    [Fact]
    public void SilenceAndTooShortSound_HaveNoLoudness()
    {
        var silent = new LoudnessMeter(48000);
        silent.Add(new float[48000 * 2 * 5]);
        Assert.Null(silent.IntegratedLufs());

        var brief = new LoudnessMeter(48000);
        AddSine(brief, 0.3, -10);
        Assert.Null(brief.IntegratedLufs());

        Assert.Null(new LoudnessMeter(48000).IntegratedLufs());
    }

    [Fact]
    public void At48k_FiltersMatchTheStandard()
    {
        var shelf = KWeighting.Shelf(48000);
        Assert.Equal(1.53512485958697, shelf.B0, 9);
        Assert.Equal(-2.69169618940638, shelf.B1, 9);
        Assert.Equal(1.19839281085285, shelf.B2, 9);
        Assert.Equal(-1.69065929318241, shelf.A1, 9);
        Assert.Equal(0.73248077421585, shelf.A2, 9);

        var high = KWeighting.HighPass(48000);
        Assert.Equal(-1.99004745483398, high.A1, 9);
        Assert.Equal(0.99007225036621, high.A2, 9);
    }
}
