using LustreRadio.Audio;
using Xunit;

namespace LustreRadio.Audio.Tests;

public class PlaybackControlsTests
{
    private static readonly AudioFormat Format = Signal.Format;

    private static CrossfadeSequencer Tones() => new(
        Format, Signal.Queue(Signal.Tone("a", 400, 4), Signal.Tone("b", 1000, 4)), crossfadeSeconds: 1);

    private static float[] Pull(IFrameProvider provider, double seconds)
    {
        var sink = new MemorySink();
        var buffer = new float[1024];
        var remaining = Format.FramesFromSeconds(seconds);
        while (remaining > 0)
        {
            var want = (int)Math.Min(512, remaining);
            var read = provider.Read(buffer.AsSpan(0, want * 2));
            if (read == 0)
            {
                break;
            }

            sink.Write(buffer.AsSpan(0, read * 2));
            remaining -= read;
        }

        return sink.Samples.ToArray();
    }

    [Fact]
    public void Pause_SilencesOutput_AndResumeContinuesExactlyWhereItStopped()
    {
        var reference = Pull(Tones(), 6);

        var gate = new PauseGate(Tones(), fadeSeconds: 0.1);
        var before = Pull(gate, 2);
        gate.Pause();
        var fading = Pull(gate, 0.1);          // stisavanje
        var silent = Pull(gate, 3);            // pauza: tisina, izvor se ne trosi
        gate.Resume();
        var after = Pull(gate, 3.9);

        Assert.True(gate.IsPaused is false);
        Assert.True(before.AsSpan().SequenceEqual(reference.AsSpan(0, before.Length)));
        Assert.Equal(0f, Signal.Peak(silent));
        Assert.True(Signal.Rms(Signal.Left(fading)) < Signal.Rms(Signal.Left(before.AsSpan(before.Length - 8820))));

        // posle nastavka muzika je na istom mestu: kad se ulazak zavrsi, uzorci su isti kao bez pauze
        var resumedAt = before.Length + fading.Length;
        var settle = (int)Format.FramesFromSeconds(0.2) * 2;
        Assert.True(after.AsSpan(settle).SequenceEqual(reference.AsSpan(resumedAt + settle, after.Length - settle)), "muzika nije nastavila sa istog mesta");
    }

    [Fact]
    public void Pause_HasNoClicks()
    {
        var gate = new PauseGate(Tones(), fadeSeconds: 0.1);
        Pull(gate, 1);
        gate.Pause();
        var fade = Signal.Left(Pull(gate, 0.3));
        gate.Resume();
        var back = Signal.Left(Pull(gate, 0.3));

        foreach (var segment in new[] { fade, back })
        {
            for (var i = 1; i < segment.Length; i++)
            {
                // ton od 400 Hz amplitude 0,25 menja se najvise ~0,015 po uzorku
                Assert.True(Math.Abs(segment[i] - segment[i - 1]) < 0.03, $"skok na uzorku {i}");
            }
        }
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.25)]
    [InlineData(0.0, 0.0)]
    public void Volume_ScalesOutput(double volume, double expectedGain)
    {
        var loud = Signal.Left(Pull(Tones(), 1));
        var scaled = Signal.Left(Pull(new VolumeControl(Tones(), volume), 1));

        Assert.Equal(Signal.Rms(loud) * expectedGain, Signal.Rms(scaled), 4);
    }

    [Fact]
    public void VolumeChange_IsSmooth_AndReachesTarget()
    {
        var control = new VolumeControl(Tones(), 1.0);
        Pull(control, 0.5);
        control.SetVolume(0.5);
        var transition = Signal.Left(Pull(control, 0.5));
        var settled = Signal.Left(Pull(control, 0.5));

        for (var i = 1; i < transition.Length; i++)
        {
            Assert.True(Math.Abs(transition[i] - transition[i - 1]) < 0.03, $"skok na uzorku {i}");
        }

        Assert.InRange(Signal.ToneAmplitude(settled.AsSpan(0, 4410), 400, Format.SampleRate), 0.0615, 0.0635); // 0,25 x 0,25
    }
}
