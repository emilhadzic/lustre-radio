using LustreRadio.Audio;
using Xunit;

namespace LustreRadio.Audio.Tests;

/// <summary>Testovi T-AUD-01, 02, 03 i 05 iz specifikacije (deo 19), na vestackim signalima.</summary>
public class StandardCrossfadeTests
{
    private static readonly AudioFormat Format = Signal.Format;

    [Fact] // T-AUD-01
    public void FiftyTracks_PlayInOrder_WithoutUnplannedSilence()
    {
        const int Count = 50;
        const double TrackSeconds = 2.0, CrossfadeSeconds = 0.5;
        var tracks = Enumerable.Range(0, Count)
            .Select(i => Signal.Tone($"t{i}", 300 + (40 * i), TrackSeconds))
            .ToArray();
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(tracks), CrossfadeSeconds);

        var sink = Signal.Render(sequencer);
        var left = Signal.Left(sink.Samples);

        var trackFrames = Format.FramesFromSeconds(TrackSeconds);
        var crossfadeFrames = Format.FramesFromSeconds(CrossfadeSeconds);
        var step = trackFrames - crossfadeFrames;
        Assert.Equal((Count * trackFrames) - ((Count - 1) * crossfadeFrames), sink.Frames);

        // nema neplanirane tisine: svaki prozor od 20 ms ima zvuk
        var window = Format.SampleRate / 50;
        for (var start = 0; start + window <= left.Length; start += window)
        {
            Assert.True(Signal.Rms(left.AsSpan(start, window)) > 0.1, $"Tisina na frejmu {start}.");
        }

        // u sredini svake pesme cuje se bas ta pesma, a ne susedne
        var probe = Format.SampleRate / 10;
        for (var i = 0; i < Count; i++)
        {
            var middle = (int)((i * step) + (trackFrames / 2) - (probe / 2));
            var slice = left.AsSpan(middle, probe);
            Assert.InRange(Signal.ToneAmplitude(slice, 300 + (40 * i), Format.SampleRate), 0.24, 0.26);
            if (i > 0)
            {
                Assert.True(Signal.ToneAmplitude(slice, 300 + (40 * (i - 1)), Format.SampleRate) < 0.005);
            }

            if (i < Count - 1)
            {
                Assert.True(Signal.ToneAmplitude(slice, 300 + (40 * (i + 1)), Format.SampleRate) < 0.005);
            }
        }

        // zapis dogadjaja: svaka pesma krece tacno gde plan kaze
        Assert.Equal(Count, sequencer.Events.Count);
        for (var i = 0; i < Count; i++)
        {
            Assert.Equal($"t{i}", sequencer.Events[i].Id);
            Assert.Equal(i * step, sequencer.Events[i].StartFrame);
            Assert.False(sequencer.Events[i].Faulted || sequencer.Events[i].EndedEarly);
        }
    }

    [Fact] // T-AUD-02
    public void FullScaleTracks_NeverExceedCeiling()
    {
        var sequencer = new CrossfadeSequencer(
            Format,
            Signal.Queue(Signal.Tone("a", 400, 2, amplitude: 1f), Signal.Tone("b", 1000, 2, amplitude: 1f), Signal.Tone("c", 250, 2, amplitude: 1f, gain: 2f)),
            crossfadeSeconds: 1.0);
        var limiter = new Limiter(Format);
        var sink = new MemorySink();

        OfflineRenderer.Render(sequencer, limiter, sink);

        Assert.True(Signal.Peak(sink.Samples) <= limiter.Ceiling, $"Vrh {Signal.Peak(sink.Samples)} prelazi plafon {limiter.Ceiling}.");
        Assert.True(Signal.Peak(sink.Samples) > limiter.Ceiling * 0.9, "Limiter je previse stisao signal.");
    }

    [Fact] // T-AUD-03
    public void Crossfade_KeepsConstantPower()
    {
        var sequencer = new CrossfadeSequencer(
            Format,
            Signal.Queue(Signal.Tone("a", 400, 3), Signal.Tone("b", 1000, 3)),
            crossfadeSeconds: 1.0);

        var left = Signal.Left(Signal.Render(sequencer).Samples);

        var expected = 0.25 / Math.Sqrt(2);
        var window = Format.SampleRate / 20; // 50 ms: ceo broj perioda oba tona
        for (var start = 0; start + window <= left.Length; start += window)
        {
            var db = 20 * Math.Log10(Signal.Rms(left.AsSpan(start, window)) / expected);
            Assert.InRange(db, -1.0, 1.0);
        }
    }

    [Fact]
    public void Limiter_IsTransparent_BelowCeiling()
    {
        Func<CrossfadeSequencer> make = () => new CrossfadeSequencer(
            Format,
            Signal.Queue(Signal.Tone("a", 400, 1), Signal.Tone("b", 1000, 1)),
            crossfadeSeconds: 0.3);

        var with = Signal.Render(make(), limiter: true);
        var without = Signal.Render(make(), limiter: false);

        Assert.Equal(without.Frames, with.Frames);
        Assert.True(without.Samples.SequenceEqual(with.Samples), "Limiter je promenio signal koji je ispod plafona.");
    }

    [Fact] // T-AUD-05
    public void TruncatedFile_NextTrackStartsImmediately()
    {
        var full = Format.FramesFromSeconds(3);
        var broken = new QueuedTrack("broken", new TruncatedSource(new ToneSource(Format, 400, 0.25f, full), Format.FramesFromSeconds(1)));
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(broken, Signal.Tone("next", 1000, 2)), crossfadeSeconds: 1.0);

        var left = Signal.Left(Signal.Render(sequencer).Samples);

        Assert.True(Signal.LongestSilenceFrames(left, Format.SampleRate) <= Format.SampleRate / 5, "Tisina duza od 0,2 s.");
        Assert.True(sequencer.Events[0].EndedEarly);
        Assert.Equal(Format.FramesFromSeconds(1), sequencer.Events[1].StartFrame);
        Assert.Equal(Format.FramesFromSeconds(3), left.Length);
    }

    [Fact] // T-AUD-05
    public void ReadError_MidTrack_NextTrackStartsImmediately()
    {
        var broken = new QueuedTrack("broken", new ThrowingSource(new ToneSource(Format, 400, 0.25f, Format.FramesFromSeconds(3)), Format.FramesFromSeconds(1)));
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(broken, Signal.Tone("next", 1000, 2)), crossfadeSeconds: 1.0);

        var left = Signal.Left(Signal.Render(sequencer).Samples);

        Assert.True(Signal.LongestSilenceFrames(left, Format.SampleRate) <= Format.SampleRate / 5, "Tisina duza od 0,2 s.");
        Assert.True(sequencer.Events[0].Faulted);
        Assert.Equal("next", sequencer.Events[1].Id);
    }

    [Fact] // T-AUD-05
    public void ReadError_InIncomingTrack_DuringCrossfade_IsReplaced()
    {
        var broken = new QueuedTrack("broken", new ThrowingSource(new ToneSource(Format, 700, 0.25f, Format.FramesFromSeconds(3)), 0));
        var sequencer = new CrossfadeSequencer(
            Format,
            Signal.Queue(Signal.Tone("a", 400, 3), broken, Signal.Tone("c", 1000, 3)),
            crossfadeSeconds: 2.0);

        var left = Signal.Left(Signal.Render(sequencer).Samples);

        Assert.True(Signal.LongestSilenceFrames(left, Format.SampleRate) <= Format.SampleRate / 5, "Tisina duza od 0,2 s.");
        Assert.Equal(["a", "broken", "c"], sequencer.Events.Select(e => e.Id));
        Assert.True(sequencer.Events[1].Faulted);
        Assert.False(sequencer.Events[2].Faulted);
    }

    [Fact]
    public void ShortReads_DoNotChangeOutput()
    {
        Func<bool, CrossfadeSequencer> make = shortReads => new CrossfadeSequencer(
            Format,
            Signal.Queue(
                Wrap(new ToneSource(Format, 400, 0.25f, Format.FramesFromSeconds(1)), "a", shortReads),
                Wrap(new ToneSource(Format, 1000, 0.25f, Format.FramesFromSeconds(1)), "b", shortReads)),
            crossfadeSeconds: 0.4);

        var normal = Signal.Render(make(false));
        var shortRead = Signal.Render(make(true));

        Assert.True(normal.Samples.SequenceEqual(shortRead.Samples));

        static QueuedTrack Wrap(ISampleSource source, string id, bool shortReads) =>
            new(id, shortReads ? new ShortReadSource(source, 37) : source);
    }

    [Fact]
    public void BlockSize_DoesNotChangeOutput()
    {
        Func<CrossfadeSequencer> make = () => new CrossfadeSequencer(
            Format,
            Signal.Queue(Signal.Tone("a", 400, 1), Signal.Tone("b", 1000, 1), Signal.Tone("c", 600, 1)),
            crossfadeSeconds: 0.4);

        var small = new MemorySink();
        var large = new MemorySink();
        OfflineRenderer.Render(make(), new Limiter(Format), small, blockFrames: 64);
        OfflineRenderer.Render(make(), new Limiter(Format), large, blockFrames: 8192);

        Assert.True(small.Samples.SequenceEqual(large.Samples));
    }

    [Fact]
    public void LastTrack_PlaysToItsEnd_WithoutFadeOut()
    {
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(Signal.Tone("only", 400, 1)), crossfadeSeconds: 0.5);

        var left = Signal.Left(Signal.Render(sequencer).Samples);

        Assert.Equal(Format.FramesFromSeconds(1), left.Length);
        var tail = left.AsSpan(left.Length - (Format.SampleRate / 20));
        Assert.InRange(Signal.Rms(tail), 0.17, 0.18);
    }

    [Fact]
    public void TrackGain_IsApplied()
    {
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(Signal.Tone("a", 400, 1, amplitude: 0.5f, gain: 0.5f)), crossfadeSeconds: 0);

        var left = Signal.Left(Signal.Render(sequencer).Samples);

        Assert.InRange(Signal.ToneAmplitude(left.AsSpan(4410, 4410), 400, Format.SampleRate), 0.249, 0.251);
    }

    [Fact]
    public void SteadyPlayback_DoesNotAllocateMemory()
    {
        var sequencer = new CrossfadeSequencer(Format, Signal.Queue(Signal.Tone("a", 400, 30)), crossfadeSeconds: 1);
        var limiter = new Limiter(Format);
        var buffer = new float[2048];
        sequencer.Read(buffer); // ucitavanje pesme sme da zauzme memoriju
        limiter.Process(buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++)
        {
            sequencer.Read(buffer);
            limiter.Process(buffer);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
