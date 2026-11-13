using LustreRadio.Core.Loudness;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>T-AUD-07: pojacanje po pesmi je ograniceno.</summary>
public class TrackGainTests
{
    private static readonly LoudnessSettings Settings = new();

    [Fact]
    public void LoudTrack_IsCutExactlyToTarget()
    {
        Assert.Equal(-9.0, TrackGain.ComputeDb(-9.0, -0.2, Settings), 6);
    }

    [Fact]
    public void QuietTrack_BoostIsLimited()
    {
        Assert.Equal(6.0, TrackGain.ComputeDb(-30.0, -20.0, Settings), 6);
    }

    [Fact]
    public void QuietTrackWithHighPeak_IsLimitedByPeak()
    {
        // jazz snimak: tih u proseku (-23 LUFS), ali sa vrhovima blizu maksimuma
        Assert.Equal(-0.5, TrackGain.ComputeDb(-23.0, -0.5, Settings), 6);
    }

    [Fact]
    public void UnmeasuredTrack_GetsDefaultGain()
    {
        Assert.Equal(-6.0, TrackGain.ComputeDb(null, null, Settings), 6);
        Assert.Equal(-6.0, TrackGain.ComputeDb(double.NegativeInfinity, null, Settings), 6);
    }

    [Fact]
    public void ForAnyTrack_BoostAndPeakLimitsHold()
    {
        for (var lufs = -40.0; lufs <= 0.0; lufs += 0.5)
        {
            for (var peak = -30.0; peak <= 0.0; peak += 0.5)
            {
                var gain = TrackGain.ComputeDb(lufs, peak, Settings);
                Assert.True(gain <= Settings.MaxBoostDb + 1e-9, $"pojacanje {gain} za {lufs} LUFS");
                Assert.True(peak + gain <= Settings.PeakCeilingDb + 1e-9, $"vrh {peak + gain} dBFS");
                Assert.True(lufs + gain <= Settings.TargetLufs + 1e-9, $"glasnoca {lufs + gain} LUFS");
            }
        }
    }

    [Fact]
    public void ToLinear_ConvertsDecibels()
    {
        Assert.Equal(1f, TrackGain.ToLinear(0), 6);
        Assert.Equal(0.5012f, TrackGain.ToLinear(-6), 3);
    }
}
