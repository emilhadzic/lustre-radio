using System.Globalization;
using LustreRadio.Audio;
using LustreRadio.Core.Licensing;
using LustreRadio.Core.Time;
using LustreRadio.Data;
using LustreRadio.Playout;
using Xunit;

namespace LustreRadio.Playout.Tests;

/// <summary>Sat koji tece zajedno sa pripremljenim zvukom: 1 s zvuka = 1 s vremena.</summary>
internal sealed class RenderClock(DateTime start) : IClock
{
    public Func<long>? Frames { get; set; }

    public DateTime NowLocal => start.AddSeconds((Frames?.Invoke() ?? 0) / 44100.0);
}

/// <summary>Privremena biblioteka: pravi WAV fajlove (tonove), bazu i skenira ih.</summary>
internal sealed class TestEnvironment : IDisposable
{
    public static readonly AudioFormat Format = AudioFormat.Stereo44100;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lustre-playout-" + Guid.NewGuid().ToString("N"));

    public TestEnvironment()
    {
        MusicFolder = Path.Combine(_root, "muzika");
        Directory.CreateDirectory(MusicFolder);
        Db = LustreDatabase.Open(Path.Combine(_root, "lustre.db"));
        History = PlaybackHistory.Open(_root, Db, options: new PlaybackHistoryOptions { StartScribe = false });
    }

    /// <summary>Istorija emitovanja (B2); pisar ne radi sam, nego ga test pokrece preko <see cref="Plays"/>.</summary>
    public PlaybackHistory History { get; }

    /// <summary>
    /// Zavrsava emitovanje (pesme u deckovima dobijaju PREKINUTO – zatvaranje), upisuje sve iz crne kutije i
    /// vraca istoriju iz baze. Od B2 red u bazi nastaje tek kad je ishod poznat.
    /// </summary>
    public IReadOnlyList<PlayRow> Plays(params RadioSession[] sessions)
    {
        foreach (var session in sessions)
        {
            session.End();
        }

        Assert.True(History.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        return Db.History.GetSince(DateTime.MinValue.ToUniversalTime());
    }

    public string MusicFolder { get; }

    public LustreDatabase Db { get; }

    /// <summary>Ton zadate frekvencije; naslov pesme je "ton-FREKVENCIJA".</summary>
    public string AddTone(int hz, double seconds, float amplitude = 0.25f)
    {
        var path = Path.Combine(MusicFolder, $"ton-{hz}.wav");
        WriteTone(path, hz, seconds, amplitude);
        return path;
    }

    public static void WriteTone(string path, int hz, double seconds, float amplitude = 0.25f)
    {
        var frames = (int)Math.Round(seconds * 44100);
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = frames * 4;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(44100);
        writer.Write(44100 * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)Math.Round(amplitude * 32767 * Math.Sin(2 * Math.PI * hz * i / 44100.0));
            writer.Write(sample);
            writer.Write(sample);
        }
    }

    public IndexSummary Index() => LibraryIndexer.Run(Db, [MusicFolder]);

    public TrackRecord TrackOf(int hz) => Db.Tracks.GetAll().Single(t => t.Title == $"ton-{hz}");

    public static string IdOf(TrackRecord track) => track.Id.ToString(CultureInfo.InvariantCulture);

    public void Verify(int hz)
    {
        var hash = TrackOf(hz).ContentHash;
        var license = Db.Licenses.EnsureImported(hash, "test");
        Db.Licenses.Save(
            license with
            {
                Status = LicenseStatus.UnderReview, Source = "test", SourceUrl = "https://example.com", Author = "test",
                LicenseType = "CC0 1.0", DownloadedOn = new DateOnly(2026, 11, 13), CommercialUse = Answer.Yes,
                PublicPerformance = Answer.Yes, Modifications = Answer.Yes, AttributionRequired = Answer.No, HasProof = true,
            },
            "test");
        Db.Licenses.Verify(hash, "test", DateTimeOffset.Now);
    }

    public (RadioDirector Director, RenderClock Clock) Director(DateTime startLocal, DirectorOptions? options = null, int seed = 1)
    {
        var clock = new RenderClock(startLocal);
        var director = new RadioDirector(Db, clock, Format, new Random(seed), options ?? new DirectorOptions { CrossfadeSeconds = 1 }, history: History);
        return (director, clock);
    }

    public static RadioSession Session(RadioDirector director, RenderClock clock, PlaybackSnapshot? snapshot = null)
    {
        var session = new RadioSession(director, Format, snapshot);
        clock.Frames = () => session.Sequencer.OutputFrames;
        return session;
    }

    /// <summary>Priprema zvuk bez uredjaja; vraca levi kanal.</summary>
    public static float[] Render(RadioSession session, double maxSeconds)
    {
        var sink = new MemorySink();
        OfflineRenderer.Render(session.Output, null, sink, maxFrames: Format.FramesFromSeconds(maxSeconds));
        var left = new float[sink.Frames];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = sink.Samples[i * 2];
        }

        return left;
    }

    public static double Rms(ReadOnlySpan<float> mono)
    {
        double sum = 0;
        foreach (var v in mono)
        {
            sum += (double)v * v;
        }

        return Math.Sqrt(sum / Math.Max(1, mono.Length));
    }

    /// <summary>Amplituda tona zadate frekvencije u odlomku.</summary>
    public static double Tone(ReadOnlySpan<float> mono, double hz)
    {
        var coefficient = 2.0 * Math.Cos(2.0 * Math.PI * hz / 44100.0);
        double s1 = 0, s2 = 0;
        foreach (var v in mono)
        {
            var s0 = v + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        return 2.0 * Math.Sqrt(Math.Max(0, (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2))) / Math.Max(1, mono.Length);
    }

    public static int LongestSilenceFrames(ReadOnlySpan<float> mono, double threshold = 0.005)
    {
        const int Window = 441;
        int longest = 0, run = 0;
        for (var start = 0; start + Window <= mono.Length; start += Window)
        {
            run = Rms(mono.Slice(start, Window)) < threshold ? run + Window : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    public void Dispose()
    {
        History.Shutdown();
        Db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder
        }
    }
}
