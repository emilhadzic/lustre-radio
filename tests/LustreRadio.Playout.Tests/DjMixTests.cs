using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>FINAL, korak 2: DJ Mix uzivo kroz pravi radio (skeniranje, analiza tempa, reditelj, prelazi).</summary>
public sealed class DjMixTests(ITestOutputHelper output)
{
    private const int Rate = 44100;

    /// <summary>Klik-traka (1 kHz, 15 ms po udarcu), kao u testovima analize tempa.</summary>
    private static float[] Clicks(double seconds, double bpm, double firstBeat, double jitterMs, int seed)
    {
        var frames = (int)(seconds * Rate);
        var data = new float[frames * 2];
        var random = new Random(seed);
        for (var t = firstBeat; t < seconds - 0.1; t += 60 / bpm)
        {
            var start = (int)Math.Round((t + ((random.NextDouble() - 0.5) * 2 * jitterMs / 1000)) * Rate);
            for (var i = 0; i < (int)(0.015 * Rate) && start + i < frames; i++)
            {
                if (start + i >= 0)
                {
                    var v = (float)(0.6 * Math.Exp(-i / (0.004 * Rate)) * Math.Sin(2 * Math.PI * 1000 * i / Rate));
                    data[2 * (start + i)] += v;
                    data[(2 * (start + i)) + 1] += v;
                }
            }
        }

        return data;
    }

    private static void WriteClicks(TestEnvironment env, int name, double seconds, double bpm, double firstBeat, double jitterMs = 0)
    {
        var data = Clicks(seconds, bpm, firstBeat, jitterMs, name);
        using var writer = new BinaryWriter(File.Create(Path.Combine(env.MusicFolder, $"ton-{name}.wav")));
        var frames = data.Length / 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + (frames * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        foreach (var v in data)
        {
            writer.Write((short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767));
        }
    }

    /// <summary>Dve pesme; vraca broj DJ prelaza i, za prvi prelaz, koliko je prvi udarac pesme B udaljen od najblizeg udarca pesme A (ms).</summary>
    private (long Dj, double? OffsetMs) Play((double Bpm, double FirstBeat, double Jitter) a, (double Bpm, double FirstBeat, double Jitter) b)
    {
        using var env = new TestEnvironment();
        WriteClicks(env, 1, 70, a.Bpm, a.FirstBeat, a.Jitter);
        WriteClicks(env, 2, 70, b.Bpm, b.FirstBeat, b.Jitter);
        env.Index();
        env.Verify(1);
        env.Verify(2);
        var records = env.Db.Tracks.GetAll().ToDictionary(t => t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var r in records.Values)
        {
            output.WriteLine($"{r.Title}: {r.Bpm:F3} BPM, stabilan {r.TempoStable}, prvi udarac {r.FirstBeatFrame / (double)Rate:F3} s, pocetak {r.AudioStartFrame / (double)Rate:F3} s");
        }

        var (director, clock) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        var session = TestEnvironment.Session(director, clock);
        TestEnvironment.Render(session, 100);
        var events = session.Sequencer.Events;
        if (session.Sequencer.DjTransitions == 0 || events.Count < 2)
        {
            return (session.Sequencer.DjTransitions, null);
        }

        // stvarni udarci iz sinteze (ne iz analize): A na firstBeat + k*T u fajlu; izlaz = pocetak pesme + (vreme u fajlu - preskocena tisina)
        var first = events[0];
        var second = events[1];
        var ra = records[first.Id];
        var rb = records[second.Id];
        var aIsFirst = ra.Title == "ton-1";
        var pa = aIsFirst ? a : b;
        var pb = aIsFirst ? b : a;
        var bBeat = second.StartFrame + ((pb.FirstBeat * Rate) - (rb.AudioStartFrame ?? 0));
        var best = double.MaxValue;
        for (var k = 0; k < 400; k++)
        {
            var aBeat = first.StartFrame + (((pa.FirstBeat + (k * 60 / pa.Bpm)) * Rate) - (ra.AudioStartFrame ?? 0));
            best = Math.Min(best, Math.Abs(aBeat - bBeat));
        }

        return (session.Sequencer.DjTransitions, best / Rate * 1000);
    }

    [Fact] // stabilan tempo, razlika 0,17 %: DJ prelaz, prvi udarac B tacno na udarcu A (do 10 ms)
    public void CloseTempo_DjTransition_BeatsAligned()
    {
        var (dj, offset) = Play((120.0, 0.4, 0), (120.2, 1.3, 0));
        output.WriteLine($"DJ prelaza {dj}; odstupanje udarca {offset:F1} ms");
        Assert.True(dj >= 1);
        Assert.NotNull(offset);
        Assert.True(offset <= 10, $"udarci se ne poklapaju: {offset:F1} ms");
    }

    [Fact] // razlika 4 % (preko 0,3 %): bez DJ Mix-a, Smart
    public void DifferentTempo_NoDj()
    {
        var (dj, _) = Play((120, 0.4, 0), (125, 0.4, 0));
        Assert.Equal(0, dj);
    }

    [Fact] // svirano uzivo (udarci setaju +-60 ms): bez DJ Mix-a, Smart
    public void LooseTiming_NoDj()
    {
        var (dj, _) = Play((110, 0.4, 60), (110, 0.4, 60));
        Assert.Equal(0, dj);
    }
}
