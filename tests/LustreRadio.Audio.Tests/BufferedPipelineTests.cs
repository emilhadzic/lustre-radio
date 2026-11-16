using LustreRadio.Audio;
using LustreRadio.Core.History;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Audio.Tests;

/// <summary>
/// Izvor za oglede (B3): zadat signal, zadata duzina, oponasani zastoji (virtuelni sat), greska ili prerani kraj,
/// ogranicena brzina dekodiranja. Implementira <see cref="IVirtualStall"/>; pravi dekoderi ne.
/// </summary>
internal sealed class ScriptedSource : ISampleSource, IVirtualStall
{
    private readonly Func<long, float> _signal;
    private readonly List<(long At, long Frames)> _stalls = [];
    private long _stallStart = -1;
    private long _clock;
    private long _firstClock = -1;

    public ScriptedSource(AudioFormat format, long lengthFrames, Func<long, float> signal)
    {
        Format = format;
        LengthFrames = lengthFrames;
        _signal = signal;
    }

    public AudioFormat Format { get; }

    public long? LengthFrames { get; }

    public long Position { get; private set; }

    public long? FailAt { get; init; }

    public long? EndAt { get; init; }

    /// <summary>Brzina dekodiranja u odnosu na slusanje (0 = neograniceno).</summary>
    public double Speed { get; init; }

    public bool Disposed { get; private set; }

    public ScriptedSource StallAt(long position, long frames)
    {
        _stalls.Add((position, frames));
        return this;
    }

    public bool IsStalled(long clockFrames)
    {
        _clock = clockFrames;
        if (_firstClock < 0)
        {
            _firstClock = clockFrames;
        }

        if (_stalls.Count > 0 && Position >= _stalls[0].At)
        {
            if (_stallStart < 0)
            {
                _stallStart = clockFrames;
            }

            if (clockFrames < _stallStart + _stalls[0].Frames)
            {
                return true;
            }

            _stalls.RemoveAt(0);
            _stallStart = -1;
        }

        return Speed > 0 && Position >= Allowed;
    }

    private long Allowed => (long)((_clock - _firstClock) * Speed) + 4096;

    public int Read(Span<float> buffer)
    {
        if (FailAt is long fail && Position >= fail)
        {
            throw new InvalidDataException("neispravni podaci (oponasano)");
        }

        var limit = Math.Min(LengthFrames!.Value, EndAt ?? long.MaxValue);
        if (FailAt is long f2)
        {
            limit = Math.Min(limit, f2);
        }

        if (_stalls.Count > 0 && Position < _stalls[0].At)
        {
            limit = Math.Min(limit, _stalls[0].At);
        }

        if (Speed > 0)
        {
            limit = Math.Min(limit, Math.Max(Position, Allowed));
        }

        var frames = (int)Math.Min(buffer.Length / 2, limit - Position);
        if (frames <= 0)
        {
            if (FailAt is long f3 && Position >= f3)
            {
                throw new InvalidDataException("neispravni podaci (oponasano)");
            }

            return Position >= Math.Min(LengthFrames!.Value, EndAt ?? long.MaxValue) ? 0 : 0;
        }

        for (var i = 0; i < frames; i++)
        {
            var v = _signal(Position + i);
            buffer[i * 2] = v;
            buffer[(i * 2) + 1] = v;
        }

        Position += frames;
        return frames;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Belezi dogadjaje istorije (za proveru statusa i duplikata).</summary>
internal sealed class RecordingEvents : IPlaybackEvents
{
    public List<JournalRecord> Records { get; } = [];

    public void Prepared(Guid playbackId, long trackId, byte source, byte part, byte tier, string? program) =>
        Records.Add(new JournalRecord { Kind = JournalKind.Prepared, PlaybackId = playbackId, Track = trackId });

    public void Started(Guid playbackId, long trackId, long frame) =>
        Records.Add(new JournalRecord { Kind = JournalKind.Started, PlaybackId = playbackId, Track = trackId, Frame = frame });

    public void Crossfade(Guid playbackIdB, long trackIdB, Guid playbackIdA, long trackIdA, long frame) =>
        Records.Add(new JournalRecord { Kind = JournalKind.Crossfade, PlaybackId = playbackIdB, Track = trackIdB, PlaybackIdA = playbackIdA, TrackA = trackIdA, Frame = frame });

    public void Ended(Guid playbackId, long trackId, long frame, int positionMs) =>
        Records.Add(new JournalRecord { Kind = JournalKind.Ended, PlaybackId = playbackId, Track = trackId, Frame = frame, PositionMs = positionMs });

    public void Stopped(Guid playbackId, long trackId, StopReason reason, long frame, int positionMs) =>
        Records.Add(new JournalRecord { Kind = JournalKind.Stopped, PlaybackId = playbackId, Track = trackId, Reason = reason, Frame = frame, PositionMs = positionMs });

    public void Discarded(Guid playbackId, long trackId, StopReason reason) =>
        Records.Add(new JournalRecord { Kind = JournalKind.Discarded, PlaybackId = playbackId, Track = trackId, Reason = reason });

    /// <summary>Konacni ishod po pesmi (broj pesme -> ishodi).</summary>
    public Dictionary<long, List<PlaybackOutcome>> Outcomes()
    {
        var map = new Dictionary<Guid, PlaybackAggregate>();
        foreach (var r in Records)
        {
            if (r.Kind == JournalKind.Crossfade)
            {
                Get(r.PlaybackIdA).Apply(r, asA: true);
            }

            Get(r.PlaybackId).Apply(r, asA: false);
        }

        return map.Values.Select(a => a.Outcome()).Where(o => o is not null).GroupBy(o => o!.TrackId).ToDictionary(g => g.Key, g => g.Select(o => o!).ToList());

        PlaybackAggregate Get(Guid id)
        {
            if (!map.TryGetValue(id, out var a))
            {
                a = new PlaybackAggregate(id);
                map[id] = a;
            }

            return a;
        }
    }
}

/// <summary>Faza B, korak B3 (prvi deo): baferi deckova, koordinator, radna nit, pravilo "nema povratka".</summary>
public sealed class BufferedPipelineTests(ITestOutputHelper output)
{
    private static readonly AudioFormat Format = AudioFormat.Stereo44100;
    private const int Rate = 44100;

    private static long F(double seconds) => (long)Math.Round(seconds * Rate);

    private sealed class Harness
    {
        private readonly Queue<(string Id, ScriptedSource Source, long AvailableFrom)> _queue = new();

        public Harness(double crossfade = 6)
        {
            Sequencer = new CrossfadeSequencer(Format, Next, crossfade, history: Events);
        }

        public RecordingEvents Events { get; } = new();

        public CrossfadeSequencer Sequencer { get; }

        public List<ScriptedSource> Sources { get; } = [];

        public ScriptedSource Add(string id, double seconds, Func<long, float>? signal = null, Func<ScriptedSource, ScriptedSource>? configure = null, double speed = 0, long? failAt = null, long? endAt = null, double availableFrom = 0)
        {
            var source = new ScriptedSource(Format, F(seconds), signal ?? (i => 0.25f * MathF.Sin(i * 0.05f))) { Speed = speed, FailAt = failAt, EndAt = endAt };
            source = configure?.Invoke(source) ?? source;
            _queue.Enqueue((id, source, F(availableFrom)));
            Sources.Add(source);
            return source;
        }

        private QueuedTrack? Next()
        {
            if (_queue.Count == 0 || Sequencer.OutputFrames < _queue.Peek().AvailableFrom)
            {
                return null; // jos nema sledece (reditelj nije imao pesmu): pokusava se ponovo na 1 s
            }

            var (id, s, _) = _queue.Dequeue();
            return new QueuedTrack(id, s) { PlaybackId = Guid.CreateVersion7() };
        }

        /// <summary>Kao audio-nit: blokovi od 1024 frejma; vraca levi kanal.</summary>
        public float[] Render(double seconds, int block = 1024)
        {
            var result = new List<float>();
            var buffer = new float[block * 2];
            var target = F(seconds);
            while (result.Count < target)
            {
                var n = Sequencer.Read(buffer);
                if (n == 0)
                {
                    break;
                }

                for (var i = 0; i < n; i++)
                {
                    result.Add(buffer[i * 2]);
                }
            }

            return [.. result];
        }
    }

    private static int LongestZeroRun(float[] audio, int from = 0)
    {
        int longest = 0, run = 0;
        for (var i = from; i < audio.Length; i++)
        {
            run = audio[i] == 0 ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    private static float Tone(long i, double hz) => 0.25f * MathF.Sin((float)(2 * Math.PI * hz * i / Rate));

    [Fact] // B-04: izlaz jednak sadasnjem (faza A), uzorak po uzorak, 50 vestackih pesama
    public void B04_OutputIdenticalToPhaseA_SampleBySample()
    {
        var random = new Random(4);
        var specs = Enumerable.Range(0, 50).Select(i => (Id: $"p{i}", Seconds: 3 + (random.NextDouble() * 9), Hz: 200 + (i * 37), Gain: 0.5f + ((float)random.NextDouble() * 0.5f))).ToList();
        Func<QueuedTrack?> Queue()
        {
            var q = new Queue<(string Id, double Seconds, int Hz, float Gain)>(specs);
            return () => q.Count == 0 ? null : q.Dequeue() is var s ? new QueuedTrack(s.Id, new ScriptedSource(Format, F(s.Seconds), i => Tone(i, s.Hz)), s.Gain) : null;
        }

        var legacy = new LegacyCrossfadeSequencer(Format, Queue(), 6);
        var current = new CrossfadeSequencer(Format, Queue(), 6);
        var a = Render(legacy);
        var b = Render(current);
        output.WriteLine($"faza A: {a.Length / 2} frejmova; B3: {b.Length / 2} frejmova");
        for (var i = 0; i < Math.Max(legacy.Events.Count, current.Events.Count); i++)
        {
            var la = i < legacy.Events.Count ? $"{legacy.Events[i].Id}@{legacy.Events[i].StartFrame}" : "-";
            var lb = i < current.Events.Count ? $"{current.Events[i].Id}@{current.Events[i].StartFrame}" : "-";
            if (la != lb)
            {
                output.WriteLine($"razlika u dogadjaju {i}: faza A {la}, B3 {lb} (trajanja: {string.Join(", ", specs.Skip(Math.Max(0, i - 2)).Take(4).Select(s => s.Id + "=" + s.Seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))})");
                break;
            }
        }

        Assert.Equal(a.Length, b.Length);
        var firstDiff = -1;
        for (var i = 0; i < a.Length && firstDiff < 0; i++)
        {
            if (a[i] != b[i])
            {
                firstDiff = i;
            }
        }

        Assert.True(firstDiff < 0, $"prva razlika na uzorku {firstDiff}");
        Assert.Equal(0, current.StarvedFrames);

        static float[] Render(IFrameProvider p)
        {
            var all = new List<float>();
            var buffer = new float[2048];
            int n;
            while ((n = p.Read(buffer)) > 0)
            {
                all.AddRange(buffer.AsSpan(0, n * 2).ToArray());
            }

            return [.. all];
        }
    }

    [Fact] // B-10: bafer decka, dve niti, 10 miliona uzoraka
    public void B10_DeckFeed_TwoThreads_TenMillionFrames_InOrder()
    {
        var feed = new DeckFeed(F(5) is var c ? (int)c : 0);
        const long total = 10_000_000;
        var writer = new Thread(() =>
        {
            var chunk = new float[4096 * 2];
            long next = 0;
            while (next < total)
            {
                var n = (int)Math.Min(4096, total - next);
                for (var i = 0; i < n; i++)
                {
                    chunk[i * 2] = (next + i) % 1_000_000;
                    chunk[(i * 2) + 1] = -((next + i) % 1_000_000);
                }

                var written = 0;
                while (written < n)
                {
                    written += feed.Write(chunk.AsSpan(written * 2, (n - written) * 2));
                }

                next += n;
            }

            feed.MarkEnded(StopReason.None);
        });
        writer.Start();
        long expected = 0;
        var read = new float[1000 * 2];
        var errors = 0;
        while (!(feed.Ended && feed.Available == 0))
        {
            var n = feed.Read(read);
            for (var i = 0; i < n; i++)
            {
                if (read[i * 2] != expected % 1_000_000 || read[(i * 2) + 1] != -(expected % 1_000_000))
                {
                    errors++;
                }

                expected++;
            }
        }

        writer.Join();
        Assert.Equal(total, expected);
        Assert.Equal(0, errors);
    }

    [Theory] // B-11: dekoder stane 3 s usred pesme -> bez tisine
    [InlineData(3.0)]
    [InlineData(3.5)]
    public void B11_DecoderStallsUnderFourSeconds_NoSilence(double stall)
    {
        var h = new Harness();
        h.Add("a", 30, configure: s => s.StallAt(F(12), F(stall)));
        h.Add("b", 30);
        var audio = h.Render(50);
        output.WriteLine($"tisina: {h.Sequencer.StarvedFrames} frejmova");
        Assert.Equal(0, h.Sequencer.StarvedFrames);
        Assert.True(LongestZeroRun(audio) < 10);
    }

    [Fact] // B-12 (i B-143 deo): dekoder javi gresku usred pesme -> prelaz <= 2 s na pripremljenu sledecu
    public void B12_DecoderErrorMidTrack_NextWithinTwoSeconds()
    {
        var h = new Harness();
        h.Add("1", 30, signal: _ => 0.5f, failAt: F(10));
        h.Add("2", 30, signal: _ => -0.5f);
        var audio = h.Render(25);
        var lastA = Array.FindLastIndex(audio, v => v > 0.01f);
        var firstB = Array.FindIndex(audio, v => v < -0.01f);
        output.WriteLine($"poslednji uzorak A: {lastA / (double)Rate:F3} s; prvi uzorak B: {firstB / (double)Rate:F3} s");
        Assert.InRange(lastA / (double)Rate, 9.9, 10.01);
        Assert.InRange((firstB - lastA) / (double)Rate, 0, 2.0);
        var outcomes = h.Events.Outcomes();
        Assert.Equal(StopReason.FileError, outcomes[1].Single().Reason);
    }

    [Fact] // B-13: otvaranje sledece pesme traje 4 s -> bez tisine
    public void B13_OpeningNextTakesFourSeconds_NoSilence()
    {
        var h = new Harness();
        h.Add("a", 20);
        h.Add("b", 20, configure: s => s.StallAt(0, F(4)));
        h.Add("c", 20);
        h.Render(45);
        Assert.Equal(0, h.Sequencer.StarvedFrames);
        Assert.Equal(3, h.Sequencer.Events.Count);
    }

    [Fact] // B-14: pesma kraca od 5 s i kraca od pretapanja
    public void B14_ShortTracks_AllPlayed_NoLongSilence()
    {
        var h = new Harness();
        h.Add("a", 10);
        h.Add("b", 4);
        h.Add("c", 2);
        h.Add("d", 1);
        h.Add("e", 10);
        var audio = h.Render(40);
        Assert.Equal(5, h.Sequencer.Events.Count);
        Assert.True(LongestZeroRun(audio) < Rate / 10);
    }

    [Fact] // B-15 (i B-21 deo): Sledeca x20 brzo
    public void B15_SkipTwentyTimesFast_EachStartsOnce_NoCrash()
    {
        var h = new Harness();
        for (var i = 0; i < 25; i++)
        {
            h.Add($"{i}", 30);
        }

        h.Render(1);
        for (var i = 0; i < 20; i++)
        {
            h.Sequencer.RequestSkip();
            h.Render(0.2);
        }

        h.Render(5);
        var starts = h.Events.Records.Where(r => r.Kind is JournalKind.Started or JournalKind.Crossfade).Select(r => r.PlaybackId).ToList();
        Assert.Equal(starts.Count, starts.Distinct().Count());
        Assert.True(h.Sequencer.Events.Count >= 10, $"pesama: {h.Sequencer.Events.Count}");
        Assert.Equal(0, h.Sequencer.StarvedFrames);
    }

    [Fact] // B-17: pauza (nista se ne cita) pa nastavak: isti zvuk kao bez pauze
    public void B17_PauseThenResume_SameAudioAsWithoutPause()
    {
        var plain = new Harness();
        plain.Add("a", 20, signal: i => Tone(i, 440));
        plain.Add("b", 20, signal: i => Tone(i, 660));
        var expected = plain.Render(30);

        var paused = new Harness();
        paused.Add("a", 20, signal: i => Tone(i, 440));
        paused.Add("b", 20, signal: i => Tone(i, 660));
        var first = paused.Render(8);
        // pauza: niko ne cita sekvencer (pauza traje koliko god; ovde nije vazno koliko)
        var rest = paused.Render(22);
        var joined = first.Concat(rest).ToArray();
        var n = Math.Min(expected.Length, joined.Length);
        Assert.True(n > F(29));
        Assert.Equal(expected[..n], joined[..n]);
    }

    [Fact] // B-70: dekoder daje tacno 2,2x brzinu slusanja, 30 pretapanja
    public void B70_DecoderAt2_2x_ThirtyCrossfades_NoStarvation()
    {
        var h = new Harness();
        for (var i = 0; i < 31; i++)
        {
            h.Add($"{i}", 12, speed: 2.2);
        }

        h.Render(3); // prva pesma: priprema do punog bafera (5 s pri 2,2x = 2,3 s)
        var initial = h.Sequencer.StarvedFrames;
        h.Render((31 * 6) + 30);
        output.WriteLine($"pocetna tisina {initial / (double)Rate:F2} s; posle toga {(h.Sequencer.StarvedFrames - initial) / (double)Rate:F3} s; pesama {h.Sequencer.Events.Count}");
        Assert.Equal(initial, h.Sequencer.StarvedFrames);
        Assert.Equal(31, h.Sequencer.Events.Count);
    }

    [Theory] // B-71: zastoj 3,5 s na pocetku, u sredini i pred kraj pretapanja -> bez tisine
    [InlineData(0.2)]
    [InlineData(3.0)]
    [InlineData(5.5)]
    public void B71_StallDuringCrossfade_NoSilence(double into)
    {
        var h = new Harness();
        h.Add("a", 20);
        h.Add("b", 20, configure: s => s.StallAt(F(5 + into), F(3.5)));
        h.Add("c", 20);
        h.Render(50);
        Assert.Equal(0, h.Sequencer.StarvedFrames);
    }

    [Fact] // B-72, B-141, B-142: zastoj 8 s tokom pretapanja (oba decka); nijedan uzorak A posle preuzimanja; B nastavlja sa svog mesta
    public void B72_EightSecondStallInCrossfade_NoReturnToA_BContinues()
    {
        var h = new Harness();
        h.Add("1", 20, signal: _ => 0.5f, configure: s => s.StallAt(F(14.5), F(12))); // A: stalna vrednost; radna nit stoji 12 s
        var b = h.Add("2", 20, signal: i => (i % 2 == 0) ? 0.25f : -0.25f);         // B: srednja vrednost 0
        h.Add("3", 20, signal: _ => 0f);
        var audio = h.Render(60);
        Assert.Equal(1, h.Sequencer.Takeovers);
        var stopped = h.Events.Records.Single(r => r.Kind == JournalKind.Stopped && r.Track == 1);
        Assert.Equal(StopReason.DecoderStall, stopped.Reason);
        var takeover = (int)stopped.Frame;

        // posle preuzimanja: zvuk je samo B (srednja vrednost svakog para uzoraka ~0); A bi dala +0,5
        var leak = 0;
        for (var i = takeover + 2; i + 1 < Math.Min(audio.Length, takeover + F(20)); i += 2)
        {
            if (audio[i] != 0 && audio[i + 1] != 0 && Math.Abs(audio[i] + audio[i + 1]) > 0.02f)
            {
                leak++;
            }
        }

        output.WriteLine($"preuzimanje na {takeover / (double)Rate:F2} s; tisina {h.Sequencer.StarvedFrames / (double)Rate:F2} s");
        Assert.Equal(0, leak);
        Assert.True(h.Sequencer.StarvedFrames > 0);
        Assert.Equal(F(20), b.Position); // B dekodirana do kraja: nije preskocena
        var outcomes = h.Events.Outcomes();
        Assert.Single(outcomes[2]);
        Assert.Equal(PlaybackStatus.Played, outcomes[2][0].Status);
    }

    [Theory] // B-73: dekoder daje 1,0x i 0,8x brzinu slusanja -> definisano ponasanje, bez pada
    [InlineData(1.0)]
    [InlineData(0.8)]
    public void B73_SlowDecoder_DefinedBehaviour_NoCrash(double speed)
    {
        var h = new Harness();
        for (var i = 0; i < 4; i++)
        {
            h.Add($"{i}", 15, speed: speed);
        }

        var audio = h.Render(90);
        output.WriteLine($"{speed}x: tisina {h.Sequencer.StarvedFrames / (double)Rate:F1} s od {audio.Length / (double)Rate:F1} s; pesama {h.Sequencer.Events.Count}");
        Assert.True(h.Sequencer.Events.Count >= 3);
    }

    [Fact] // B-74, B-140: presusi samo deck koji izlazi -> pretapanje gotovo odmah; B na punoj glasnoci za 50 ms; bez klika
    public void B74_OutgoingRunsDry_TakeoverWithin50ms_NoClick()
    {
        var h = new Harness();
        h.Add("1", 20, signal: _ => 0f, configure: s => s.StallAt(F(16), F(30))); // A tiha: svaki skok dolazi od B
        h.Add("2", 30, signal: i => Tone(i, 300));
        h.Add("3", 30);
        var audio = h.Render(40);
        Assert.Equal(1, h.Sequencer.Takeovers);
        var takeover = (int)h.Events.Records.Single(r => r.Kind == JournalKind.Stopped && r.Track == 1).Frame;
        var maxJump = 0f;
        for (var i = takeover - F(0.2); i < takeover + F(0.2); i++)
        {
            maxJump = Math.Max(maxJump, Math.Abs(audio[i + 1] - audio[i]));
        }

        var tone = 2 * Math.PI * 300 / Rate * 0.25 * 1.5; // najveca promena cistog tona, uz rezervu
        output.WriteLine($"najveci skok oko preuzimanja: {maxJump:F4} (ton: {tone:F4})");
        Assert.True(maxJump <= tone, "klik pri preuzimanju");
        var after = audio.AsSpan(takeover + (int)F(0.06), (int)F(0.5)).ToArray().Max(Math.Abs);
        Assert.InRange(after, 0.23f, 0.26f); // B na punoj glasnoci posle 50 ms
    }

    [Fact] // B-75 (oblik): dekoder pesme B stoji tokom pretapanja -> A se stisava po rasporedu; glasnoca A nikad ne raste
    public void B75_IncomingDecoderStallsInCrossfade_AFadesOnSchedule_NeverRises()
    {
        var h = new Harness();
        h.Add("1", 20, signal: _ => 0.5f);
        h.Add("2", 30, signal: _ => 0f, configure: s => s.StallAt(F(5), F(3)));
        h.Add("3", 30, signal: _ => 0f);
        var audio = h.Render(30);
        var start = (int)F(14.06);
        for (var i = start + 1; i < (int)F(20); i++)
        {
            Assert.True(audio[i] <= audio[i - 1] + 1e-6f, $"glasnoca A porasla na {i / (double)Rate:F3} s");
        }

        Assert.Equal(0, h.Sequencer.Takeovers);
        Assert.True(audio[(int)F(19.99)] < 0.01f);
    }

    [Fact] // B-76: sledeca nije spremna u trenutku pretapanja (kasni 2 s) -> skraceno pretapanje
    public void B76_NextLateTwoSeconds_ShortenedCrossfade()
    {
        var h = new Harness();
        h.Add("1", 20, signal: _ => 0.5f);
        h.Add("2", 20, signal: _ => -0.5f, availableFrom: 16);
        var audio = h.Render(40);
        var firstB = h.Events.Records.Single(r => r.Kind == JournalKind.Crossfade).Frame / (double)Rate; // pocetak (skracenog) pretapanja
        output.WriteLine($"B krece na {firstB:F2} s; zakasnelih prelaza {h.Sequencer.LateTransitions}; tisina {h.Sequencer.StarvedFrames / (double)Rate:F2} s");
        Assert.InRange(firstB, 15.9, 17.2); // ponovni pokusaj na 1 s
        Assert.True(audio.Length > F(20));
        Assert.Equal(1, h.Sequencer.LateTransitions);
        Assert.Equal(0, h.Sequencer.StarvedFrames);
        Assert.Equal(PlaybackStatus.Played, h.Events.Outcomes()[1].Single().Status);
    }

    [Fact] // B-76: sledeca nije spremna ni do kraja pesme A (radna nit zauzeta) -> A do kraja, tisina, B sa ulazom od 50 ms
    public void B76_NextNotReadyUntilAEnds_SilenceThenFadeIn()
    {
        var h = new Harness();
        h.Add("1", 4.5, signal: _ => 0.5f);                                   // cela pesma A je u baferu
        h.Add("2", 20, signal: _ => -0.5f, configure: s => s.StallAt(0, F(8))); // otvaranje B traje 8 s
        var audio = h.Render(20);
        var lastA = Array.FindLastIndex(audio, v => v > 0.01f) / (double)Rate;
        var firstB = Array.FindIndex(audio, v => v < -0.01f) / (double)Rate;
        output.WriteLine($"A do {lastA:F2} s; B od {firstB:F2} s; zakasnelih prelaza {h.Sequencer.LateTransitions}");
        Assert.InRange(lastA, 4.4, 4.51);
        Assert.InRange(firstB, 7.9, 8.2);
        Assert.Equal(1, h.Sequencer.LateTransitions);
        var rampEnd = (int)(firstB * Rate) + (int)F(0.06);
        Assert.InRange(audio[rampEnd], -0.51f, -0.49f);
        Assert.True(audio[(int)(firstB * Rate)] > -0.2f); // ulaz od 50 ms, ne skok
    }

    [Fact] // B-77: sledeca kraca od 6 s; kraca od 5 s; Sledeca pa odmah prirodno pretapanje
    public void B77_ShortNextAndSkipThenCrossfade_NoSilence()
    {
        var h = new Harness();
        h.Add("1", 20);
        h.Add("2", 5.5);
        h.Add("3", 4.5);
        h.Add("4", 7);
        h.Add("5", 20);
        h.Render(2);
        h.Sequencer.RequestSkip();
        var audio = h.Render(40);
        Assert.Equal(0, h.Sequencer.StarvedFrames);
        Assert.True(LongestZeroRun(audio) < Rate / 20);
    }

    [Fact] // B-143 (oblik): dekoder pesme B javi gresku tokom pretapanja -> krece C na njeno mesto; B upisana kao PREKINUTO sa razlogom
    public void B143_BFailsDuringCrossfade_CTakesItsPlace()
    {
        var h = new Harness();
        h.Add("1", 20, signal: _ => 0.5f);
        h.Add("2", 20, signal: _ => 0.3f, failAt: F(2));
        h.Add("3", 20, signal: _ => -0.5f);
        var audio = h.Render(40);
        var o = h.Events.Outcomes();
        Assert.Equal(PlaybackStatus.Played, o[1].Single().Status);
        Assert.Equal((PlaybackStatus.Stopped, StopReason.FileError), (o[2].Single().Status, o[2].Single().Reason));
        Assert.Contains(audio, v => v < -0.4f);
    }

    [Fact] // B-146: istorija za preuzimanje: A jedan red PREKINUTO, B jedan red, bez duplikata
    public void B146_History_OneRowEach_NoDuplicates()
    {
        var h = new Harness();
        h.Add("1", 20, configure: s => s.StallAt(F(16), F(10)));
        h.Add("2", 20);
        h.Add("3", 20);
        h.Render(50);
        var o = h.Events.Outcomes();
        Assert.Single(o[1]);
        Assert.Single(o[2]);
        Assert.Equal(StopReason.DecoderStall, o[1][0].Reason);
        Assert.True(o[1][0].TransitionReached);
        var terminal = h.Events.Records.Where(r => r.IsTerminal).GroupBy(r => r.PlaybackId).ToList();
        Assert.All(terminal, g => Assert.Single(g));
    }

    [Fact] // B-147: zvuk u kratkim naletima posle praznog bafera: nastavak tek sa 1 s zalihe
    public void B147_ShortBursts_ResumeOnlyWithOneSecondReserve()
    {
        var h = new Harness();
        var a = h.Add("1", 60, signal: _ => 0.5f, configure: s => s.StallAt(F(10), F(10)).StallAt(F(10.2), F(1)).StallAt(F(10.4), F(1)));
        var audio = h.Render(40);
        // svaki nastavak zvuka posle tisine traje bar 1 s (N2), nema "seckanja" kraceg od praga
        var runs = new List<int>();
        int run = 0;
        for (var i = (int)F(5); i < audio.Length; i++)
        {
            if (audio[i] != 0)
            {
                run++;
            }
            else if (run > 0)
            {
                runs.Add(run);
                run = 0;
            }
        }

        output.WriteLine("naleti zvuka (s): " + string.Join(", ", runs.Select(r => (r / (double)Rate).ToString("F2", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.All(runs, r => Assert.True(r >= F(0.95), $"nalet od {r / (double)Rate:F2} s"));
        _ = a;
    }

    [Fact] // B-148: 1.000 nasumicnih rasporeda zastoja
    public void B148_ThousandRandomStallSchedules_NoReturnToA_AGainNeverRises()
    {
        var random = new Random(148);
        var failures = new List<string>();
        for (var run = 0; run < 1000; run++)
        {
            var h = new Harness();
            var stallA = random.NextDouble() < 0.7;
            var stallB = random.NextDouble() < 0.7;
            h.Add("1", 20, signal: _ => 0.5f, configure: s => stallA ? s.StallAt(F(12 + (random.NextDouble() * 8)), F(random.NextDouble() * 12)) : s);
            h.Add("2", 20, signal: _ => 0f, configure: s => stallB ? s.StallAt(F(random.NextDouble() * 10), F(random.NextDouble() * 12)) : s);
            h.Add("3", 20, signal: _ => 0f);
            var audio = h.Render(45);
            var released = h.Events.Records.FirstOrDefault(r => r.Track == 1 && r.IsTerminal);
            // zapis kraja nosi pocetak poslednjeg dela (najvise 4.096 frejmova) u kome je A jos svirala
            var end = released.Kind == JournalKind.None ? audio.Length : (int)Math.Min(audio.Length, released.Frame + 4096);

            // posle kraja/preuzimanja pesme A: 0 uzoraka A (ostale pesme su tisina)
            for (var i = end + 1; i < audio.Length; i++)
            {
                if (audio[i] != 0)
                {
                    failures.Add($"{run}: uzorak A posle kraja na {i}");
                    break;
                }
            }

            // tokom pretapanja glasnoca A samo opada (A se cuje samo kao 0,5 x kriva)
            var xfade = h.Events.Records.FirstOrDefault(r => r.Kind == JournalKind.Crossfade);
            if (xfade.Kind == JournalKind.Crossfade)
            {
                for (var i = (int)(xfade.Frame + F(0.06)); i < end && i < audio.Length; i++)
                {
                    if (audio[i] > audio[i - 1] + 1e-6f)
                    {
                        failures.Add($"{run}: glasnoca A porasla na {i}");
                        break;
                    }
                }
            }
        }

        Assert.Empty(failures);
    }
}
