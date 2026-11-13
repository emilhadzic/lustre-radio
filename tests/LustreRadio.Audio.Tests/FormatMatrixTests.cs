using LustreRadio.Audio;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Audio.Tests;

/// <summary>T-AUD-04 (matrica formata) i T-AUD-06 (dekodiranje od pocetka naspram premotavanja).</summary>
public class FormatMatrixTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    // fajl, ocekivani dekoder, bez gubitaka i bez promene sample rate-a, mono
    public static IEnumerable<object[]> Files =>
    [
        ["chirp_44k_stereo_cbr.mp3", Decoders.MediaFoundation, false, false],
        ["chirp_44k_stereo_vbr.mp3", Decoders.MediaFoundation, false, false],
        ["chirp_44k_stereo_art.mp3", Decoders.MediaFoundation, false, false],
        ["chirp_48k_stereo.mp3", Decoders.MediaFoundation, false, false],
        ["chirp_44k_mono.mp3", Decoders.MediaFoundation, false, true],
        ["chirp_44k_stereo.flac", Decoders.MediaFoundation, true, false],
        ["chirp_96k_stereo.flac", Decoders.MediaFoundation, false, false],
        ["chirp_44k_stereo.m4a", Decoders.MediaFoundation, false, false],
        ["chirp_44k_stereo.aac", Decoders.MediaFoundation, false, false],
        ["chirp_44k_stereo.ogg", Decoders.Vorbis, false, false],
    ];

    [Theory] // T-AUD-04
    [MemberData(nameof(Files))]
    public void File_DecodesToKnownSignal(string file, string decoder, bool exact, bool mono)
    {
        using var source = Decoders.Open(AudioFixtures.PathOf(file), AudioFixtures.Target);
        Assert.Equal(decoder, source.DecoderId);

        var samples = AudioFixtures.ReadAll(source);
        var left = AudioFixtures.Channel(samples, 0);
        var right = AudioFixtures.Channel(samples, 1);
        var expected = AudioFixtures.ExpectedLeft();

        var (lag, correlation) = AudioFixtures.BestLag(left, Rate, expected, Rate, Rate / 10, Rate / 10);
        output.WriteLine($"{file}: dekoder={source.DecoderId} frejmova={left.Length} (ocekivano {expected.Length}) " +
            $"najavljeno={source.LengthFrames} pomak={lag} slicnost={correlation:F5}");

        Assert.InRange(left.Length, expected.Length - (Rate / 10), expected.Length + (Rate / 5));
        if (exact)
        {
            Assert.Equal(expected.Length, left.Length);
            Assert.Equal(0, lag);
            Assert.True(correlation > 0.9999, $"slicnost {correlation}");
            Assert.Equal(expected.Length, source.LengthFrames);
        }
        else
        {
            Assert.True(correlation > 0.98, $"slicnost {correlation}");
        }

        var ratio = Signal.Rms(right.AsSpan(Rate, Rate)) / Signal.Rms(left.AsSpan(Rate, Rate));
        Assert.InRange(ratio, mono ? 0.99 : 0.45, mono ? 1.01 : 0.55);
    }

    [Theory] // T-AUD-06
    [MemberData(nameof(Files))]
    public void Seek_MatchesSequentialDecode(string file, string decoder, bool exact, bool mono)
    {
        Assert.NotNull(decoder);
        Assert.True(mono || !mono);
        var path = AudioFixtures.PathOf(file);
        float[] sequential;
        using (var source = Decoders.Open(path, AudioFixtures.Target))
        {
            sequential = AudioFixtures.Channel(AudioFixtures.ReadAll(source), 0);
        }

        const int Target = 2 * Rate, Skip = Rate / 20, Length = Rate / 10, MaxLag = Rate / 10;
        float[] afterSeek;
        using (var source = Decoders.Open(path, AudioFixtures.Target))
        {
            source.Seek(Target);
            afterSeek = AudioFixtures.Channel(AudioFixtures.ReadAll(source, Skip + Length + Rate), 0);
        }

        // gde se u sekvencijalnom dekodiranju nalazi ono sto je stiglo posle premotavanja
        var (lag, correlation) = AudioFixtures.BestLag(sequential, Target + Skip, afterSeek, Skip, Length, MaxLag);
        output.WriteLine($"{file}: pomak posle premotavanja={lag} frejmova ({lag * 1000.0 / Rate:F1} ms) slicnost={correlation:F5}");

        Assert.True(correlation > 0.98, $"slicnost {correlation}");
        if (exact)
        {
            Assert.Equal(0, lag);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wav_DecodesExactly(bool floatFormat)
    {
        var path = AudioFixtures.WriteWav(floatFormat);
        try
        {
            using var source = Decoders.Open(path, AudioFixtures.Target);
            Assert.Equal(Decoders.Wav, source.DecoderId);
            var left = AudioFixtures.Channel(AudioFixtures.ReadAll(source), 0);
            var expected = AudioFixtures.ExpectedLeft();

            Assert.Equal(expected.Length, left.Length);
            Assert.Equal(expected.Length, source.LengthFrames);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.True(Math.Abs(left[i] - expected[i]) < 1.0 / 16384, $"razlika na frejmu {i}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Wav_48k_IsResampledTo44k()
    {
        var path = AudioFixtures.WriteWav(floatFormat: false, sampleRate: 48000);
        try
        {
            using var source = Decoders.Open(path, AudioFixtures.Target);
            Assert.True(source.IsResampled);
            var left = AudioFixtures.Channel(AudioFixtures.ReadAll(source), 0);
            var expected = AudioFixtures.ExpectedLeft();
            var (lag, correlation) = AudioFixtures.BestLag(left, Rate, expected, Rate, Rate / 10, 500);
            output.WriteLine($"resampling 48k->44.1k: frejmova={left.Length} pomak={lag} slicnost={correlation:F5}");

            Assert.InRange(left.Length, expected.Length - 500, expected.Length + 500);
            Assert.InRange(lag, -100, 100);
            Assert.True(correlation > 0.999, $"slicnost {correlation}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mp3_AlsoDecodes_WithFallbackDecoder()
    {
        using var source = Decoders.Open(AudioFixtures.PathOf("chirp_44k_stereo_cbr.mp3"), AudioFixtures.Target, Decoders.NLayer);
        Assert.Equal(Decoders.NLayer, source.DecoderId);
        var left = AudioFixtures.Channel(AudioFixtures.ReadAll(source), 0);
        var (lag, correlation) = AudioFixtures.BestLag(left, Rate, AudioFixtures.ExpectedLeft(), Rate, Rate / 10, Rate / 10);
        output.WriteLine($"nlayer: frejmova={left.Length} pomak={lag} slicnost={correlation:F5}");
        Assert.True(correlation > 0.98, $"slicnost {correlation}");
    }

    [Fact]
    public void WrongExtension_IsDetectedByContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lustre-{Guid.NewGuid():N}.mp3");
        File.Copy(AudioFixtures.PathOf("chirp_44k_stereo.flac"), path);
        try
        {
            Assert.Equal(AudioKind.Flac, AudioFileSniffer.Detect(path));
            using var source = Decoders.Open(path, AudioFixtures.Target);
            var left = AudioFixtures.Channel(AudioFixtures.ReadAll(source), 0);
            Assert.Equal(AudioFixtures.ExpectedLeft().Length, left.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("chirp_44k_stereo_cbr.mp3", AudioKind.Mp3)]
    [InlineData("chirp_44k_stereo_art.mp3", AudioKind.Mp3)]
    [InlineData("chirp_44k_stereo.flac", AudioKind.Flac)]
    [InlineData("chirp_44k_stereo.m4a", AudioKind.Mp4)]
    [InlineData("chirp_44k_stereo.aac", AudioKind.AacAdts)]
    [InlineData("chirp_44k_stereo.ogg", AudioKind.OggVorbis)]
    [InlineData("chirp_48k_stereo.opus", AudioKind.OggOpus)]
    public void Sniffer_DetectsKind(string file, AudioKind kind)
    {
        Assert.Equal(kind, AudioFileSniffer.Detect(AudioFixtures.PathOf(file)));
    }

    [Fact]
    public void Garbage_ThrowsDecodeException_NotCrash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lustre-{Guid.NewGuid():N}.mp3");
        var bytes = new byte[50_000];
        new Random(7).NextBytes(bytes);
        bytes[0] = 0x00;
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Throws<AudioDecodeException>(() => Decoders.Open(path, AudioFixtures.Target).Dispose());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact] // Opus jos nije podrzan: mora da se javi jasnom greskom, ne padom
    public void Opus_IsReportedAsNotYetSupported()
    {
        Assert.Throws<AudioDecodeException>(() => Decoders.Open(AudioFixtures.PathOf("chirp_48k_stereo.opus"), AudioFixtures.Target).Dispose());
    }

    [Fact]
    public void TruncatedMp3_PlaysWhatExists_ThenNextTrackStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lustre-{Guid.NewGuid():N}.mp3");
        var bytes = File.ReadAllBytes(AudioFixtures.PathOf("chirp_44k_stereo_cbr.mp3"));
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        try
        {
            var tracks = Signal.Queue(
                new QueuedTrack("truncated", Decoders.Open(path, AudioFixtures.Target)),
                Signal.Tone("next", 1000, 2));
            var sequencer = new CrossfadeSequencer(AudioFixtures.Target, tracks, crossfadeSeconds: 0.5);

            var left = Signal.Left(Signal.Render(sequencer).Samples);
            output.WriteLine($"odsecen mp3: ukupno {left.Length} frejmova; ranije zavrsio={sequencer.Events[0].EndedEarly} greska={sequencer.Events[0].Faulted}");

            Assert.Equal(2, sequencer.Events.Count);
            Assert.InRange(left.Length, 3 * Rate, 5 * Rate);
            Assert.True(Signal.LongestSilenceFrames(left.AsSpan(Rate / 5).ToArray(), Rate) <= Rate / 5, "Tisina duza od 0,2 s.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RealFiles_CrossfadeTogether()
    {
        var tracks = Signal.Queue(
            new QueuedTrack("mp3", Decoders.Open(AudioFixtures.PathOf("chirp_44k_stereo_cbr.mp3"), AudioFixtures.Target)),
            new QueuedTrack("flac", Decoders.Open(AudioFixtures.PathOf("chirp_44k_stereo.flac"), AudioFixtures.Target)),
            new QueuedTrack("ogg", Decoders.Open(AudioFixtures.PathOf("chirp_44k_stereo.ogg"), AudioFixtures.Target)),
            new QueuedTrack("m4a", Decoders.Open(AudioFixtures.PathOf("chirp_44k_stereo.m4a"), AudioFixtures.Target)));
        var sequencer = new CrossfadeSequencer(AudioFixtures.Target, tracks, crossfadeSeconds: 1.0);

        var sink = Signal.Render(sequencer);
        var left = Signal.Left(sink.Samples);
        foreach (var e in sequencer.Events)
        {
            output.WriteLine($"{e.Id}: pocetak={e.StartFrame} ranije zavrsio={e.EndedEarly} greska={e.Faulted}");
        }

        Assert.Equal(4, sequencer.Events.Count);
        Assert.DoesNotContain(sequencer.Events, e => e.Faulted || e.FailedToLoad);
        Assert.InRange(left.Length, 12 * Rate, 14 * Rate);
        Assert.True(Signal.LongestSilenceFrames(left.AsSpan(Rate / 5, left.Length - (2 * Rate / 5)).ToArray(), Rate) <= Rate / 5, "Tisina duza od 0,2 s.");
    }
}
