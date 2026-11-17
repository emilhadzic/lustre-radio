using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>FINAL, korak 1: Smart Crossfade kroz pravi radio (skeniranje, analiza, reditelj, prelazi).</summary>
public sealed class SmartCrossfadeTests(ITestOutputHelper output)
{
    /// <summary>Ton sa tisinom na pocetku i kraju; naslov "ton-HZ" (kao TestEnvironment.AddTone).</summary>
    private static void ToneWithSilence(TestEnvironment env, int hz, double lead, double tone, double trail)
    {
        var frames = (int)Math.Round((lead + tone + trail) * 44100);
        var from = (int)Math.Round(lead * 44100);
        var to = from + (int)Math.Round(tone * 44100);
        using var writer = new BinaryWriter(File.Create(Path.Combine(env.MusicFolder, $"ton-{hz}.wav")));
        writer.Write("RIFF"u8);
        writer.Write(36 + (frames * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(44100);
        writer.Write(44100 * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var sample = i >= from && i < to ? (short)Math.Round(0.25 * 32767 * Math.Sin(2 * Math.PI * hz * i / 44100.0)) : (short)0;
            writer.Write(sample);
            writer.Write(sample);
        }
    }

    /// <summary>Najduza tisina (s) izmedju prvog i poslednjeg zvuka u izlazu.</summary>
    private static double LongestGap(float[] left)
    {
        var first = Array.FindIndex(left, v => Math.Abs(v) > 0.001f);
        var last = Array.FindLastIndex(left, v => Math.Abs(v) > 0.001f);
        int run = 0, longest = 0;
        for (var i = first; i <= last; i++)
        {
            // sinus prolazi kroz nulu: tisina je tek kad je ceo prozor od 2 ms tih
            run = Math.Abs(left[i]) <= 0.001f ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest / 44100.0;
    }

    private (double Gap, int Tracks) Play(bool smart)
    {
        using var env = new TestEnvironment();
        foreach (var hz in new[] { 300, 500, 700 })
        {
            ToneWithSilence(env, hz, lead: 3, tone: 8, trail: 3);
        }

        env.Index();
        foreach (var hz in new[] { 300, 500, 700 })
        {
            env.Verify(hz);
        }

        var (director, clock) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0), new DirectorOptions { CrossfadeSeconds = 1, SmartCrossfade = smart });
        var session = TestEnvironment.Session(director, clock);
        var left = TestEnvironment.Render(session, 40);
        return (LongestGap(left), session.Sequencer.Events.Count);
    }

    [Fact] // tisina na krajevima se ne pusta: izmedju pesama nema rupe; bez Smart-a (kontrola) rupa postoji
    public void SilenceAtEnds_NoGapBetweenSongs()
    {
        var (smartGap, smartTracks) = Play(smart: true);
        var (plainGap, _) = Play(smart: false);
        output.WriteLine($"najduza tisina izmedju pesama: Smart {smartGap:F3} s ({smartTracks} pesama), obicno pretapanje {plainGap:F3} s");
        Assert.True(smartTracks >= 3);
        Assert.True(smartGap < 0.05, $"Smart: tisina {smartGap:F3} s");
        Assert.True(plainGap > 4, "kontrola: bez Smart-a test mora da vidi rupu");
    }

    [Fact] // pesma bez tisine i bez stisavanja: pusta se cela, obicno pretapanje (isto kao pre Smart-a)
    public void AbruptEnd_WholeTrackKept_PlainTransition()
    {
        using var env = new TestEnvironment();
        var path = Path.Combine(env.MusicFolder, "ton-400.wav");
        TestEnvironment.WriteTone(path, 400, 30);
        env.Index();
        var plain = env.Db.Tracks.GetAll().Single();
        Assert.Equal(Analysis.EdgeAnalyzer.Version, plain.EdgesVersion);
        Assert.Null(plain.OutroSeconds); // nagao kraj -> obicno pretapanje
        Assert.Equal(0, plain.AudioStartFrame);
        Assert.Equal(plain.ExactFrames, plain.AudioEndFrame);
    }
}
