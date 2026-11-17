using LustreRadio.Audio;
using LustreRadio.Core.History;
using LustreRadio.Core.Scheduling;
using LustreRadio.Data;
using LustreRadio.Presentation;
using LustreRadio.Ipc;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>Faza B, korak B4: planer na koordinatoru, plan i mala biblioteka, istorija kroz kartice.</summary>
public sealed class PlannerTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateTime Monday = new(2026, 11, 16);
    private const int Rate = 44100;
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private int[] Tones(int count, double seconds, bool verify = true, int start = 300)
    {
        var tones = Enumerable.Range(0, count).Select(i => start + (i * 100)).ToArray();
        foreach (var hz in tones)
        {
            _env.AddTone(hz, seconds);
        }

        _env.Index();
        if (verify)
        {
            foreach (var hz in tones)
            {
                _env.Verify(hz);
            }
        }

        return tones;
    }

    private (RadioDirector Director, RenderClock Clock, RadioSession Session) Start(DateTime at, DirectorOptions? options = null, PlaybackSnapshot? snapshot = null)
    {
        var (director, clock) = _env.Director(at, options);
        var session = TestEnvironment.Session(director, clock, snapshot);
        return (director, clock, session);
    }

    [Theory] // B-130, B-135, B-136: plan ima 0, 1, 2, 3, 3 stavke; jedna se ponavlja; dve nikad ista zaredom
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    public void B130_PlanSizeFollowsLibrary(int allowed, int expected)
    {
        Tones(4, 3, verify: false);
        foreach (var hz in Enumerable.Range(0, allowed).Select(i => 300 + (i * 100)))
        {
            _env.Verify(hz);
        }

        var (_, _, session) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session, 0.1);
        Assert.Equal(expected, session.Plan.Items.Count);
        if (allowed < 3)
        {
            Assert.NotNull(session.Plan.ShortReason);
        }

        TestEnvironment.Render(session, 20);
        var started = session.Sequencer.Events.Select(e => e.Id).ToList();
        output.WriteLine($"{allowed} dozvoljenih: plan {session.Plan.Items.Count}; pustene: {string.Join(",", started)}");
        if (allowed == 2)
        {
            Assert.All(started.Zip(started.Skip(1)), p => Assert.NotEqual(p.First, p.Second));
        }

        if (allowed == 1)
        {
            Assert.True(started.Distinct().Count() == 1 && started.Count >= 3);
        }

        // B-136: bez plana nema reda "Sledeca"
        var state = new RadioState { OnAir = true, Title = "x", NextTitle = session.Plan.Items.FirstOrDefault()?.Title, NextArtist = session.Plan.Items.FirstOrDefault()?.Artist };
        var view = StaffView.From(state, LinkStatus.Connected, Monday.AddHours(10), Monday.AddHours(10).ToUniversalTime());
        Assert.Equal(expected == 0, string.IsNullOrEmpty(view.NextLine));
    }

    [Fact] // B-20: do 3 razlicite pesme u planu; "Sledeca" na ekranu je stvarno sledeca
    public void B20_UpToThreeDistinct_NextOnScreenIsReallyNext()
    {
        Tones(10, 3);
        var (_, _, session) = Start(Monday.AddHours(10));
        var mismatches = 0;
        for (var i = 0; i < 40; i++)
        {
            TestEnvironment.Render(session, 0.5);
            var plan = session.Plan.Items;
            Assert.True(plan.Count <= 3);
            Assert.Equal(plan.Count, plan.Select(p => p.TrackId).Distinct().Count());
            if (plan.Count > 0)
            {
                var promised = plan[0].TrackId;
                var before = session.Sequencer.Events.Count;
                while (session.Sequencer.Events.Count == before)
                {
                    TestEnvironment.Render(session, 0.1);
                }

                mismatches += session.Sequencer.Events[^1].Id == promised ? 0 : 1;
            }
        }

        Assert.Equal(0, mismatches);
    }

    [Fact] // B-131: 10 pesama, 9 bez licence -> 1 stavka; razlog "licenca"
    public void B131_NineWithoutLicense_OneItem_ReasonLicense()
    {
        Tones(10, 3, verify: false);
        _env.Verify(300);
        var (_, _, session) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session, 0.1);
        Assert.Single(session.Plan.Items);
        Assert.Equal("licenca", session.Plan.ShortReason);
    }

    [Fact] // B-133, B-27: sve osim tekuce povucene dok svira -> plan prazan; tekuca se zavrsi; povucena nikad ne krene
    public void B133_B27_AllButCurrentRevoked_PlanEmpties_RevokedNeverStart()
    {
        var tones = Tones(5, 6);
        var (director, _, session) = Start(Monday.AddHours(10), new DirectorOptions { CrossfadeSeconds = 1, Coordinator = new CoordinatorOptions { ValidateEverySeconds = 0 } });
        TestEnvironment.Render(session, 1);
        var current = session.Sequencer.Events[^1].Id;
        foreach (var track in _env.Db.Tracks.GetAll().Where(t => TestEnvironment.IdOf(t) != current))
        {
            _env.Db.Tracks.SetBlocked(track.Id, true);
        }

        director.ReloadCatalog();
        var revoked = _env.Db.Tracks.GetAll().Where(t => t.Blocked).Select(TestEnvironment.IdOf).ToHashSet();
        TestEnvironment.Render(session, 20);
        output.WriteLine($"plan: {session.Plan.Items.Count} ({session.Plan.ShortReason}); pustene: {string.Join(",", session.Sequencer.Events.Select(e => e.Id))}");
        Assert.DoesNotContain(session.Sequencer.Events.Skip(1), e => revoked.Contains(e.Id));
        Assert.All(session.Plan.Items, p => Assert.DoesNotContain(p.TrackId, revoked));
        _ = tones;
    }

    [Fact] // B-134: nijedna stavka plana nikad nije pesma koja ne prolazi kapiju licenci (nasumicne izmene)
    public void B134_PlanNeverContainsUnlicensed_RandomChanges()
    {
        Tones(8, 2);
        var (director, _, session) = Start(Monday.AddHours(10), new DirectorOptions { CrossfadeSeconds = 0.5, Coordinator = new CoordinatorOptions { ValidateEverySeconds = 0 } });
        var random = new Random(134);
        var tracks = _env.Db.Tracks.GetAll();
        var violations = 0;
        var startedRevoked = 0;
        session.Sequencer.Coordinator.TrackStarted += t =>
        {
            if (_env.Db.Tracks.Get(long.Parse(t.Id, System.Globalization.CultureInfo.InvariantCulture))!.Blocked)
            {
                startedRevoked++;
            }
        };
        for (var step = 0; step < 2000; step++)
        {
            var t = tracks[random.Next(tracks.Count)];
            _env.Db.Tracks.SetBlocked(t.Id, random.Next(3) == 0);
            director.ReloadCatalog();
            TestEnvironment.Render(session, 0.05);
            var blocked = _env.Db.Tracks.GetAll().Where(x => x.Blocked).Select(TestEnvironment.IdOf).ToHashSet();
            violations += session.Plan.Items.Count(p => blocked.Contains(p.TrackId));
        }

        output.WriteLine($"2.000 koraka: stavki bez dozvole u planu {violations}; pokrenutih bez dozvole {startedRevoked}");
        Assert.Equal(0, violations);
        Assert.Equal(0, startedRevoked);
    }

    [Fact] // B-22: rucna promena programa: od sledece pesme; odbacene stavke bez traga u rotaciji
    public void B22_ManualProgramChange_FromNextTrack_DiscardedWithoutTrace()
    {
        Tones(12, 6);
        var (director, _, session) = Start(Monday.AddHours(10), new DirectorOptions { CrossfadeSeconds = 1, Coordinator = new CoordinatorOptions { ValidateEverySeconds = 0 } });
        TestEnvironment.Render(session, 2);
        var discarded = session.Plan.Items.Select(p => p.TrackId).ToList();
        var schedule = director.Schedule;
        var evening = schedule.Days[DayOfWeek.Friday].Evening;
        director.Override = new ManualOverride { Program = evening with { Name = "Rucno" }, StartsLocal = Monday, EndsLocal = Monday.AddHours(23) };
        TestEnvironment.Render(session, 30);
        var rows = _env.Plays(session);
        var afterFirst = rows.Skip(1).ToList();
        output.WriteLine(string.Join(" | ", rows.Select(r => $"{r.TrackId}:{r.Program}")));
        Assert.All(afterFirst, r => Assert.Equal("Rucno", r.Program));
        Assert.Equal("Ponedeljak - jutro", rows[0].Program);
        Assert.NotEmpty(discarded);
    }

    [Fact] // B-23, B-24: granica termina u 11:00; pauza preko granice
    public void B23_B24_DayPartBoundary_AndPauseAcrossIt()
    {
        Tones(12, 4);
        var (_, clock, session) = Start(Monday.AddHours(10).AddMinutes(59).AddSeconds(40), new DirectorOptions { CrossfadeSeconds = 1, Coordinator = new CoordinatorOptions { ValidateEverySeconds = 0 } });
        TestEnvironment.Render(session, 10);
        clock.Shift = TimeSpan.FromSeconds(30); // pauza: vreme prolazi, zvuk se ne pravi
        TestEnvironment.Render(session, 15);
        var rows = _env.Plays(session);
        output.WriteLine(string.Join(" | ", rows.Select(r => $"{r.StartedUtc.ToLocalTime():HH:mm:ss}:{r.Program}")));
        Assert.Equal("Ponedeljak - jutro", rows[0].Program);
        Assert.Equal("Ponedeljak - popodne", rows[^1].Program);
        var firstAfternoon = rows.ToList().FindIndex(r => r.Program == "Ponedeljak - popodne");
        Assert.All(rows.Skip(firstAfternoon), r => Assert.Equal("Ponedeljak - popodne", r.Program));
    }

    [Fact] // B-25: fajl obrisan izmedju izbora i pripreme -> stavka ispada, muzika ide dalje, bez traga u rotaciji
    public void B25_FileDeletedAfterSelection_ItemDropped_MusicContinues()
    {
        Tones(6, 4);
        var (_, _, session) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session, 0.5);
        var victim = session.Plan.Items[^1];
        var record = _env.Db.Tracks.Get(long.Parse(victim.TrackId, System.Globalization.CultureInfo.InvariantCulture))!;
        File.Delete(record.Path);
        var audio = TestEnvironment.Render(session, 30);
        Assert.DoesNotContain(session.Sequencer.Events, e => e.Id == victim.TrackId);
        Assert.True(TestEnvironment.LongestSilenceFrames(audio) <= Rate / 10);
    }

    [Fact] // B-26: fajl izmenjen posle izbora, pre otvaranja -> ne pusta se
    public void B26_FileChangedBeforeOpen_NotPlayed()
    {
        Tones(6, 4);
        var (_, _, session) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session, 0.5);
        var victim = session.Plan.Items[^1];
        var record = _env.Db.Tracks.Get(long.Parse(victim.TrackId, System.Globalization.CultureInfo.InvariantCulture))!;
        TestEnvironment.WriteTone(record.Path, 2500, 4);
        var audio = TestEnvironment.Render(session, 30);
        Assert.DoesNotContain(session.Sequencer.Events, e => e.Id == victim.TrackId);
        Assert.True(TestEnvironment.Tone(audio, 2500) < 0.0005);
    }

    [Fact] // B-28: zatvaranje u 23:30: stisavanje, posle toga nista novo
    public void B28_ClosingAt2330_FadesAndStops()
    {
        Tones(8, 6);
        var (_, _, session) = Start(Monday.AddHours(23).AddMinutes(29).AddSeconds(30));
        var audio = TestEnvironment.Render(session, 120);
        output.WriteLine($"zvuk {audio.Length / (double)Rate:F1} s; kraj emitovanja: {session.Ended}");
        Assert.True(session.Ended);
        Assert.InRange(audio.Length / (double)Rate, 30, 90);
    }

    [Fact] // B-29: 30 simuliranih dana (ubrzano): razmak za pesmu i izvodjaca se postuje kroz planer
    public void B29_ThirtyDays_RotationRulesHold()
    {
        Tones(40, 2);
        var settings = new Core.Rotation.RotationSettings { TrackGap = TimeSpan.FromHours(1), ArtistGap = TimeSpan.FromMinutes(5) };
        var (director, clock) = _env.Director(Monday.AddHours(9), new DirectorOptions { Rotation = settings });
        var last = new Dictionary<string, DateTime>();
        var violations = 0;
        var before = new List<string>();
        long frames = 0;
        clock.Frames = () => frames;
        for (var i = 0; i < 30 * 12 * 15; i++) // 30 dana x 12 h x 15 pesama na sat
        {
            frames += (long)(240 * 44100.0);
            var item = director.Select(before);
            if (item is null)
            {
                continue;
            }

            var now = clock.NowLocal;
            if (last.TryGetValue(item.TrackId, out var prev) && now - prev < settings.TrackGap)
            {
                violations++;
            }

            last[item.TrackId] = now;
            before.Clear();
            before.Add(item.TrackId);
        }

        output.WriteLine($"izbora {last.Count} razlicitih; krsenja razmaka {violations}");
        Assert.Equal(0, violations);
    }

    [Fact] // B-30: ponovno pokretanje usred pesme: ista pesma i ista sledeca
    public void B30_RestartMidTrack_SameTrackAndSameNext()
    {
        Tones(8, 10);
        var (director1, _, session1) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session1, 4);
        session1.Tick(saveSnapshot: true);
        var snapshot = director1.LoadSnapshot()!;
        Assert.NotNull(snapshot.NextTrackId);
        _ = _env.Plays(session1);

        var (_, _, session2) = Start(Monday.AddHours(10).AddSeconds(20), snapshot: snapshot);
        TestEnvironment.Render(session2, 15);
        output.WriteLine($"snimak: {snapshot.TrackId} pa {snapshot.NextTrackId}; posle: {string.Join(",", session2.Sequencer.Events.Select(e => e.Id))}");
        Assert.Equal(snapshot.TrackId, session2.Sequencer.Events[0].Id);
        Assert.Equal(snapshot.NextTrackId, session2.Sequencer.Events[1].Id);
    }

    [Fact] // B-80, B-81, B-82: 100 kratkih pesama; pisar stoji dok sviraju; po povratku sve, po redu, bez duplikata
    public void B80_B82_HundredShortTracks_ScribeStopped_AllRowsInOrder()
    {
        Tones(20, 1.5);
        var (_, _, session) = Start(Monday.AddHours(10), new DirectorOptions { CrossfadeSeconds = 0.3 });
        while (session.Sequencer.Events.Count < 101)
        {
            TestEnvironment.Render(session, 1);
        }

        var started = session.Sequencer.Events.Take(100).Select(e => e.PlaybackId.ToString()).ToList();
        var rows = _env.Plays(session); // tek ovde radi pisar
        var ids = rows.Select(r => r.PlaybackId ?? string.Empty).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(started, id => Assert.Contains(id, ids));
        Assert.True(rows.Count >= 100);
        Assert.Equal(started, ids.Where(started.Contains).ToList()); // isti redosled
    }

    [Fact] // B-83: 40 pritisaka na Sledeca za 90 s: svaki pocetak jednom; svaki PREKINUTO – Sledeca jednom
    public void B83_FortySkips_EachStartAndEachSkipOnce()
    {
        Tones(30, 20);
        var (_, _, session) = Start(Monday.AddHours(10));
        for (var i = 0; i < 40; i++)
        {
            TestEnvironment.Render(session, 2.25);
            session.Skip();
        }

        TestEnvironment.Render(session, 3);
        var rows = _env.Plays(session);
        var skipped = rows.Count(r => r.StopReason == "sledeca");
        output.WriteLine($"redova {rows.Count}; Sledeca {skipped}");
        Assert.Equal(rows.Count, rows.Select(r => r.PlaybackId).Distinct().Count());
        Assert.InRange(skipped, 35, 40);
        Assert.Equal(session.Sequencer.Events.Count, rows.Count);
    }

    [Fact] // B-86: dnevnik ne potvrdi "pripremljena"; pesma krene posle 2 s; pad -> pesma u istoriji iz crne kutije
    public void B86_PreparedNotConfirmed_StartsAfterTwoSeconds_CrashRecoveredFromBlackBox()
    {
        Tones(6, 4);
        var root = Path.Combine(Path.GetTempPath(), "lustre-b86-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var history = PlaybackHistory.Open(root, _env.Db, options: new PlaybackHistoryOptions { StartScribe = false });
        var clock = new RenderClock(Monday.AddHours(10));
        var director = new RadioDirector(_env.Db, clock, TestEnvironment.Format, new Random(1), new DirectorOptions { CrossfadeSeconds = 1 }, history: history);
        var coordinator = new Coordinator(TestEnvironment.Format, director, new CoordinatorOptions { DurableWaitSeconds = 2 }, history);
        var sequencer = new CrossfadeSequencer(TestEnvironment.Format, coordinator, 1);
        clock.Frames = () => sequencer.OutputFrames;
        var buffer = new float[1024 * 2];
        while (sequencer.OutputFrames < 12 * Rate)
        {
            sequencer.Read(buffer);
        }

        var firstStart = sequencer.Events[0].StartFrame / (double)Rate;
        output.WriteLine($"prva pesma krenula na {firstStart:F2} s (pisar ne radi)");
        Assert.InRange(firstStart, 1.99, 2.1);
        history.Abandon(); // pad procesa: nista vise

        using var again = PlaybackHistory.Open(root, _env.Db);
        var rows = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Where(r => r.PlaybackId is not null).ToList();
        output.WriteLine(string.Join(" | ", rows.Select(r => r.Status)));
        // svaka pustena pesma je u istoriji (oporavak iz crne kutije); pripremljena sledeca koja nije krenula je NEPOTVRDJENO (a)
        Assert.Equal(sequencer.Events.Count - 1, rows.Count(r => r.Status == "odsvirana"));
        Assert.Single(rows, r => r.Status == "nepotvrdjena-b");
        Assert.InRange(rows.Count(r => r.Status == "nepotvrdjena-a"), 0, 1);
        Assert.All(sequencer.Events, e => Assert.Contains(rows, r => r.PlaybackId == e.PlaybackId.ToString()));
    }

    [Fact] // B-199: zapis "pretapanje" je jedan red sa jednim rid i poljima pid_a i pid_b
    public void B199_CrossfadeIsOneLineWithBothIds()
    {
        Tones(8, 4);
        var (_, _, session) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session, 30);
        _ = _env.Plays(session);
        var records = _env.History.Journal.ReadAll().Records;
        var crossfades = records.Where(r => r.Kind == JournalKind.Crossfade).ToList();
        var transitions = session.Sequencer.Events.Count - 1;
        output.WriteLine($"pretapanja u dnevniku {crossfades.Count}; prelaza {transitions}");
        Assert.Equal(transitions, crossfades.Count);
        Assert.All(crossfades, c => Assert.True(c.PlaybackId != Guid.Empty && c.PlaybackIdA != Guid.Empty && c.PlaybackId != c.PlaybackIdA));
        Assert.Equal(records.Count, records.Select(r => r.Rid).Distinct().Count());
        var lines = File.ReadAllLines(_env.History.Journal.Path).Where(l => l.Contains("\"pretapanje\"", StringComparison.Ordinal)).ToList();
        Assert.All(lines, l => Assert.True(l.Contains("\"pid_a\"", StringComparison.Ordinal) && l.Contains("\"pid_b\"", StringComparison.Ordinal)));
    }

    [Fact] // B-210: koordinator ne oslobadja kartice (sve zauzete) -> nema nove pesme, nema pada; nit za mesanje salje tisinu
    public void B210_AllTicketsTaken_NoNewTrack_NoCrash_ThenRecovers()
    {
        Tones(6, 2);
        var (_, _, session) = Start(Monday.AddHours(10));
        TestEnvironment.Render(session, 0.5);
        var tickets = session.Sequencer.Coordinator.Tickets;
        var held = new List<long>();
        long h;
        while ((h = tickets.Plan(Guid.Empty, 0)) >= 0)
        {
            held.Add(h);
        }

        var before = session.Sequencer.Events.Count;
        TestEnvironment.Render(session, 10);
        var during = session.Sequencer.Events.Count - before;
        foreach (var x in held)
        {
            tickets.MarkWithdrawn(x);
            tickets.Release(x);
        }

        TestEnvironment.Render(session, 10);
        output.WriteLine($"zauzetih kartica {held.Count}; novih pesama dok su zauzete {during}; posle {session.Sequencer.Events.Count - before - during}");
        Assert.True(during <= 1); // najvise ona koja je vec bila objavljena
        Assert.True(session.Sequencer.Events.Count - before - during >= 2);
    }

    [Fact] // B-220 (smanjeno): ceo put kartica -> crna kutija -> dnevnik -> baza -> oporavak; padovi u nasumicnim trenucima
    public void B220_FullPath_RandomCrashes_OneRidPerCrossfade_NoDuplicates()
    {
        Tones(30, 2.5);
        var root = Path.Combine(Path.GetTempPath(), "lustre-b220-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var random = new Random(220);
        var started = new List<string>();
        var crossfadesSeen = 0;
        for (var segment = 0; segment < 25; segment++)
        {
            var history = PlaybackHistory.Open(root, _env.Db, options: new PlaybackHistoryOptions { StartScribe = random.Next(2) == 0 });
            var clock = new RenderClock(Monday.AddHours(10).AddMinutes(segment * 5));
            var director = new RadioDirector(_env.Db, clock, TestEnvironment.Format, new Random(segment), new DirectorOptions { CrossfadeSeconds = 1 }, history: history);
            var session = new RadioSession(director, TestEnvironment.Format);
            clock.Frames = () => session.Sequencer.OutputFrames;
            TestEnvironment.Render(session, 20 + (random.NextDouble() * 60));
            started.AddRange(session.Sequencer.Events.Select(e => e.PlaybackId.ToString()));
            crossfadesSeen += Math.Max(0, session.Sequencer.Events.Count - 1);
            if (random.Next(4) == 0)
            {
                session.End();
                history.Shutdown(); // uredno gasenje
            }
            else
            {
                history.Abandon(); // pad procesa
            }
        }

        using (PlaybackHistory.Open(root, _env.Db))
        {
        }

        var journal = new PlayJournal(Path.Combine(root, PlaybackHistory.JournalFolder)).ReadAll().Records;
        var rows = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Where(r => r.PlaybackId is not null).ToList();
        var crossfades = journal.Where(r => r.Kind == JournalKind.Crossfade).ToList();
        output.WriteLine($"pesama {started.Count}; pretapanja vidjeno {crossfadesSeen}, u dnevniku {crossfades.Count}; redova {rows.Count}; statusi: {string.Join(", ", rows.GroupBy(r => r.Status).Select(g => g.Key + "=" + g.Count()))}");
        Assert.Equal(journal.Count, journal.Select(r => r.Rid).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.PlaybackId).Distinct().Count());
        Assert.All(crossfades, c => Assert.True(c.PlaybackId != Guid.Empty && c.PlaybackIdA != Guid.Empty));
        Assert.DoesNotContain(rows, r => r.Status == "prekinuta" && r.StopReason is null);
        var aggregates = PlaybackHistory.Aggregate(journal);
        Assert.DoesNotContain(aggregates.Values, a => a.Terminal is null); // posle oporavka sve ima zavrsni zapis
    }
}
