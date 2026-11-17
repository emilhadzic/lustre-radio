using System.Diagnostics;
using LustreRadio.Core.History;
using LustreRadio.Data;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

[CollectionDefinition(nameof(HistoryCollection), DisableParallelization = true)]
public sealed class HistoryCollection;

/// <summary>Faza B, korak B2: istorija emitovanja – pisar, dnevnik, statusi, oporavak.</summary>
[Collection(nameof(HistoryCollection))]
public sealed class HistoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lustre-history-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private LustreDatabase? _db;

    private LustreDatabase Db => _db ??= LustreDatabase.Open(Path.Combine(Directory.CreateDirectory(_root).FullName, "lustre.db"));

    public void Dispose()
    {
        _db?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder
        }
        catch (UnauthorizedAccessException)
        {
            // privremeni folder
        }
    }

    private PlaybackHistory Open(bool scribe = false, HistoryFaults? faults = null, int capacity = BlackBox.DefaultCapacity) =>
        PlaybackHistory.Open(_root, Db, m => { lock (_log) { _log.Add(m); } }, new PlaybackHistoryOptions
        {
            StartScribe = scribe, Faults = faults, Capacity = capacity, ScribePoll = TimeSpan.FromMilliseconds(20),
        });

    private static Guid Play(IPlaybackEvents h, long track, JournalKind end = JournalKind.Ended, StopReason reason = StopReason.None)
    {
        var pid = Guid.CreateVersion7();
        h.Prepared(pid, track, 1, 1, 1, "Ponedeljak - jutro");
        h.Started(pid, track, 0);
        if (end == JournalKind.Ended)
        {
            h.Ended(pid, track, 1000, 1000);
        }
        else if (end == JournalKind.Stopped)
        {
            h.Stopped(pid, track, reason, 500, 500);
        }

        return pid;
    }

    private IReadOnlyList<PlayRow> Rows() => Db.History.GetSince(DateTime.MinValue.ToUniversalTime());

    private PlayRow Row(Guid pid) => Rows().Single(r => r.PlaybackId == pid.ToString());

    /// <summary>Svi zapisi dnevnika: arhivirani (po redu) pa tekuci fajl.</summary>
    private List<JournalRecord> JournalRecords()
    {
        var folder = Path.Combine(_root, PlaybackHistory.JournalFolder);
        var result = new List<JournalRecord>();
        var archive = Path.Combine(folder, PlayJournal.ArchiveFolder);
        if (Directory.Exists(archive))
        {
            foreach (var file in Directory.EnumerateFiles(archive, "*.jsonl").Order())
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (PlayJournal.TryParse(System.Text.Encoding.UTF8.GetBytes(line), out var record))
                    {
                        result.Add(record);
                    }
                }
            }
        }

        result.AddRange(new PlayJournal(folder).ReadAll().Records);
        return result;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
            {
                Assert.Fail("nije se desilo na vreme: " + what);
            }

            Thread.Sleep(20);
        }
    }

    [Fact] // B-160: po jedan slucaj svakog statusa i razloga
    public void B160_EachStatusAndReason_EndsUpInTheDatabase()
    {
        var reasons = new[] { StopReason.Skip, StopReason.FileError, StopReason.FileUnavailable, StopReason.UnexpectedEnd, StopReason.DecoderStall, StopReason.Closing };
        var pids = new Dictionary<Guid, (string Status, string? Reason)>();
        var h = Open();
        pids[Play(h, 1)] = ("odsvirana", null);
        foreach (var reason in reasons)
        {
            pids[Play(h, 10 + (int)reason, JournalKind.Stopped, reason)] = ("prekinuta", JournalRecord.ReasonName(reason));
        }

        // nepotvrdjene (a), (b), (c): pad posle pripremljena / pocetak / pretapanje
        var a = Guid.CreateVersion7();
        h.Prepared(a, 30, 1, 1, 1, null);
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        h.Prepared(c, 31, 1, 1, 1, null);
        h.Started(c, 31, 0);
        h.Prepared(b, 32, 1, 1, 1, null);
        h.Crossfade(b, 32, c, 31, 100);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        h.Abandon();

        using var again = Open();
        pids[a] = ("nepotvrdjena-a", null);
        pids[b] = ("nepotvrdjena-b", null);
        pids[c] = ("nepotvrdjena-c", null);
        foreach (var (pid, (status, reason)) in pids)
        {
            var row = Row(pid);
            Assert.Equal(status, row.Status);
            Assert.Equal(reason, row.StopReason);
        }

        Assert.True(Row(c).TransitionReached);
        Assert.False(Row(b).TransitionReached);
        Assert.Equal("Ponedeljak - jutro", Rows().First(r => r.Status == "odsvirana").Program);
        Assert.Equal("Morning", Rows().First(r => r.Status == "odsvirana").Part);
    }

    [Fact] // B-160 kroz pravi radio: kraj, Sledeca, zatvaranje, pretapanje
    public void B160_ThroughTheRealRadio_EndedSkipAndClosing()
    {
        using var env = new TestEnvironment();
        for (var i = 0; i < 8; i++)
        {
            env.AddTone(300 + (i * 200), 3);
        }

        env.Index();
        foreach (var t in env.Db.Tracks.GetAll())
        {
            env.Verify(int.Parse(t.Title.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        var (director, clock) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        var session = TestEnvironment.Session(director, clock);
        TestEnvironment.Render(session, 1.0);
        session.Skip();
        TestEnvironment.Render(session, 9);
        var rows = env.Plays(session);
        foreach (var r in rows)
        {
            output.WriteLine($"{r.TrackId} {r.Status} {r.StopReason} prelaz={r.TransitionReached}");
        }

        Assert.Equal(session.Sequencer.Events.Count, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.PlaybackId).Distinct().Count());
        Assert.Equal("prekinuta", rows[0].Status);
        Assert.Equal("sledeca", rows[0].StopReason);
        Assert.Contains(rows, r => r.Status == "odsvirana" && r.TransitionReached);
        Assert.Equal("prekinuta", rows[^1].Status);
        Assert.Equal("zatvaranje", rows[^1].StopReason);
        Assert.DoesNotContain(rows, r => r.Status.StartsWith("nepotvrdjena", StringComparison.Ordinal));
    }

    [Fact] // B-161: pad posle pocetka, bez ishoda
    public void B161_CrashAfterStart_IsUnconfirmedB_NeverStoppedOrPlayed()
    {
        var h = Open();
        var pid = Play(h, 5, JournalKind.None);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        h.Abandon();
        using var again = Open();
        var row = Row(pid);
        Assert.Equal("nepotvrdjena-b", row.Status);
        Assert.Null(row.StopReason);
        Assert.Single(JournalRecords(), r => r.Kind == JournalKind.ProgramCrash);
    }

    [Fact] // B-162, B-168: cetiri odvojena broja; svaki upit vraca status; "odsvirane" samo POTVRDJENO
    public void B162_B168_CountsAreSeparate_AndEveryRowHasAStatus()
    {
        var start = DateTime.UtcNow.AddMinutes(-1);
        Db.History.Add(new PlayRow { TrackId = 1, StartedUtc = DateTime.UtcNow, Source = "WeeklySlot" }); // stari zapis
        var h = Open();
        Play(h, 2);
        Play(h, 3);
        Play(h, 4, JournalKind.Stopped, StopReason.Skip);
        Play(h, 5, JournalKind.None);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        h.Abandon();
        using var again = Open();

        var counts = Db.History.CountByStatus(start, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(new HistoryCounts(Played: 2, Stopped: 1, Unconfirmed: 1, Legacy: 1), counts);
        var rows = Rows();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Status)));

        // rotacija cita sve redove (GetSince), dakle sva cetiri statusa
        Assert.Equal(4, rows.Select(r => r.Status.Split('-')[0]).Distinct().Count());
    }

    [Fact] // B-163: pet uzastopnih pokretanja posle jednog pada
    public void B163_FiveRestartsAfterOneCrash_ExactlyOneUnconfirmedRow()
    {
        var h = Open();
        var pid = Play(h, 7, JournalKind.None);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        h.Abandon();
        for (var i = 0; i < 5; i++)
        {
            using var again = Open();
            Assert.Equal(i == 0 ? 1 : 0, again.RecoveryResult.Unconfirmed);
            Assert.Equal(i == 0 ? 1 : 0, again.RecoveryResult.ProgramCrashes);
        }

        Assert.Single(Rows(), r => r.PlaybackId == pid.ToString());
        Assert.Equal("nepotvrdjena-b", Row(pid).Status);
        var records = JournalRecords();
        Assert.Single(records, r => r.Kind == JournalKind.Unconfirmed);
        Assert.Single(records, r => r.Kind == JournalKind.ProgramCrash);
        Assert.DoesNotContain(PlaybackHistory.Aggregate(records).Values, a => a.Terminal is null);
    }

    [Theory] // B-164: pad usred oporavka, na svakom koraku
    [InlineData("oporavak-1")]
    [InlineData("oporavak-2")]
    [InlineData("oporavak-3")]
    [InlineData("oporavak-4")]
    public void B164_CrashDuringRecovery_NextStartFinishesIt(string point)
    {
        var h = Open();
        var pid = Play(h, 8, JournalKind.None);
        var played = Play(h, 9);
        h.Abandon(); // nista nije stiglo u dnevnik: sve je u crnoj kutiji

        var faults = new HistoryFaults { AtPoint = p => { if (p == point) { throw new SimulatedCrash(p); } } };
        Assert.Throws<SimulatedCrash>(() => Open(faults: faults));

        using var again = Open();
        Assert.Equal("nepotvrdjena-b", Row(pid).Status);
        Assert.Equal("odsvirana", Row(played).Status);
        Assert.Equal(2, Rows().Count);
        var records = JournalRecords();
        Assert.Single(records, r => r.Kind == JournalKind.Unconfirmed);
        var crashedRuns = records.Where(r => r.Kind == JournalKind.ProgramCrash).Select(r => r.PlaybackIdA).ToList();
        Assert.Equal(crashedRuns.Count, crashedRuns.Distinct().Count()); // jedan prekid_programa po padu
        var runsWithoutShutdown = records.Where(r => r.Run != again.Run).Select(r => r.Run).Distinct()
            .Where(run => !records.Any(r => r.Run == run && r.Kind == JournalKind.Shutdown)).ToList();
        Assert.Equal(runsWithoutShutdown.OrderBy(x => x), crashedRuns.OrderBy(x => x));
        output.WriteLine($"{point}: prekida programa {crashedRuns.Count}");
    }

    [Fact] // B-165: ista pesma posle NEPOTVRDJENO dobija nov playback_id; stari red se ne menja
    public void B165_SameTrackAgain_NewPlaybackId_OldRowUnchanged()
    {
        var h = Open();
        var first = Play(h, 11, JournalKind.None);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        h.Abandon();
        using var again = Open();
        var second = Play(again, 11);
        Assert.True(again.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(first, second);
        Assert.Equal("nepotvrdjena-b", Row(first).Status);
        Assert.Equal("odsvirana", Row(second).Status);
        Assert.Equal(2, Rows().Count(r => r.TrackId == 11));
    }

    [Fact] // B-166: uredno gasenje sa pripremljenom pesmom
    public void B166_OrderlyShutdown_WithPreparedTrack_NoUnconfirmed()
    {
        using (var h = Open(scribe: true))
        {
            var pid = Guid.CreateVersion7();
            h.Prepared(pid, 12, 1, 1, 1, null);
            h.Discarded(pid, 12, StopReason.Shutdown);
        }

        using var again = Open();
        Assert.Equal(0, again.RecoveryResult.Unconfirmed);
        Assert.Equal(0, again.RecoveryResult.ProgramCrashes);
        Assert.Empty(Rows());
        Assert.Single(JournalRecords(), r => r.Kind == JournalKind.Shutdown);
    }

    [Fact] // B-166 kroz pravi radio: nastavak pesme koji nije stigao da krene se odbacuje
    public void B166_ResumeNotStarted_IsDiscarded_OnEnd()
    {
        using var env = new TestEnvironment();
        env.AddTone(440, 30);
        env.Index();
        env.Verify(440);
        var (director, clock) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        var snapshot = new PlaybackSnapshot { TrackId = TestEnvironment.IdOf(env.TrackOf(440)), PositionSeconds = 5, SavedUtc = new DateTime(2026, 11, 16, 10, 0, 0).ToUniversalTime() };
        var session = TestEnvironment.Session(director, clock, snapshot);
        Assert.True(session.Resumed);
        Assert.Empty(env.Plays(session));
        Assert.Contains(env.History.Journal.ReadAll().Records, r => r.Kind == JournalKind.Discarded);
    }

    [Fact] // B-207: identifikatori
    public void B207_Identifiers()
    {
        Db.History.Add(new PlayRow { TrackId = 1, StartedUtc = DateTime.UtcNow, Source = "WeeklySlot" });
        Db.History.Add(new PlayRow { TrackId = 2, StartedUtc = DateTime.UtcNow, Source = "WeeklySlot" }); // stari redovi: prazan playback_id
        var h = Open();
        var one = Play(h, 21);
        var two = Play(h, 21); // ista pesma dvaput: dva playback_id, dva reda
        var twice = Guid.CreateVersion7();
        h.Prepared(twice, 22, 1, 1, 1, null);
        h.Started(twice, 22, 0);
        h.Stopped(twice, 22, StopReason.Skip, 10, 10);
        h.Ended(twice, 22, 20, 20); // drugi zavrsni zapis: greska programa, vazi prvi
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        h.Abandon();

        // isti red dvaput u dnevniku (ponovljen upis): uzima se jednom
        var journalPath = Path.Combine(_root, PlaybackHistory.JournalFolder, PlayJournal.FileName);
        var lines = File.ReadAllLines(journalPath);
        File.AppendAllText(journalPath, lines[2] + "\n");
        for (var i = 0; i < 5; i++)
        {
            using var again = Open();
        }

        var records = JournalRecords();
        Assert.Equal(records.Count - 1, records.Select(r => r.Rid).Distinct().Count()); // jedini duplikat je onaj koji je test dodao
        Assert.NotEqual(one, two);
        Assert.Equal(2, Rows().Count(r => r.TrackId == 21));
        Assert.Equal("prekinuta", Row(twice).Status);
        Assert.Equal("sledeca", Row(twice).StopReason);
        Assert.Contains(_log, m => m.Contains("Nepravilnost", StringComparison.Ordinal));
        Assert.Equal(5, Rows().Count); // 2 stara + 3 nova
    }

    [Fact] // B-31: svaki upis u bazu traje 5 s; muzika (dogadjaji) ne ceka
    public void B31_SlowDatabase_EventsDoNotWait()
    {
        var faults = new HistoryFaults { BeforeDatabase = () => Thread.Sleep(5000) };
        using var h = Open(scribe: true, faults: faults);
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            var watch = Stopwatch.StartNew();
            Play(h, 100 + i);
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
        }

        WaitUntil(() => h.Journal.LinesWritten >= 18, TimeSpan.FromSeconds(5), "dnevnik");
        output.WriteLine($"najduzi poziv dogadjaja: {slowest.TotalMilliseconds:F2} ms");
        Assert.True(slowest < TimeSpan.FromMilliseconds(50));
        WaitUntil(() => Rows().Count == 6, TimeSpan.FromSeconds(60), "6 redova u bazi");
    }

    [Fact] // B-32 (skraceno: baza odbija 10 s umesto 2 min), 40 pesama
    public void B32_DatabaseRefuses_ThenRecovers_AllRowsArrive()
    {
        var refuse = true;
        var attempts = 0;
        var faults = new HistoryFaults { BeforeDatabase = () => { attempts++; if (Volatile.Read(ref refuse)) { throw new InvalidOperationException("baza odbija upis"); } } };
        using var h = Open(scribe: true, faults: faults);
        for (var i = 0; i < 40; i++)
        {
            Play(h, 200 + i);
        }

        WaitUntil(() => h.Journal.LinesWritten >= 120, TimeSpan.FromSeconds(5), "dnevnik");
        Thread.Sleep(10_000);
        Assert.Empty(Rows());
        Assert.True(h.Scribe.DatabaseFailing);
        Assert.True(attempts >= 5, $"pokusaja: {attempts}");
        Volatile.Write(ref refuse, false);
        WaitUntil(() => Rows().Count == 40, TimeSpan.FromSeconds(10), "40 redova");
        Assert.False(h.Scribe.DatabaseFailing);
    }

    [Fact] // B-33: skener upisuje 200 pesama dok se istorija upisuje
    public void B33_ScannerWritesWhileHistoryWrites()
    {
        using var env = new TestEnvironment();
        for (var i = 0; i < 200; i++)
        {
            TestEnvironment.WriteTone(Path.Combine(env.MusicFolder, $"ton-{1000 + i}.wav"), 1000 + i, 0.3);
        }

        env.History.Scribe.Start();
        var scan = new Thread(() => env.Index());
        scan.Start();
        for (var i = 0; i < 200; i++)
        {
            Play(env.History, 5000 + i);
        }

        scan.Join();
        WaitUntil(() => env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Count == 200, TimeSpan.FromSeconds(30), "200 redova");
        Assert.Equal(200, env.Db.Tracks.GetAll().Count);
    }

    [Fact] // B-34: 5.000 dogadjaja dok baza ne radi
    public void B34_FiveThousandEventsWhileDatabaseIsDown()
    {
        var down = true;
        var faults = new HistoryFaults { BeforeDatabase = () => { if (Volatile.Read(ref down)) { throw new InvalidOperationException("baza ne radi"); } } };
        using var h = Open(scribe: true, faults: faults);
        for (var i = 0; i < 1667; i++)
        {
            Play(h, i);
        }

        WaitUntil(() => h.Journal.LinesWritten >= 5001, TimeSpan.FromSeconds(20), "5.001 red u dnevniku");
        WaitUntil(() => h.Scribe.PendingDatabase == 1667, TimeSpan.FromSeconds(10), "1.667 ishoda ceka bazu");
        Assert.Empty(Rows());
        Volatile.Write(ref down, false);
        WaitUntil(() => Rows().Count == 1667, TimeSpan.FromSeconds(60), "1.667 redova");
        Assert.Equal(0, h.Box.LostCount);
    }

    [Fact] // B-35: isti dogadjaj dvaput -> u bazi jednom
    public void B35_SameOutcomeTwice_OneRow()
    {
        var h = Open();
        var pid = Play(h, 1);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        var outcome = PlaybackHistory.Aggregate(JournalRecords())[pid].Outcome()!;
        Assert.False(Db.History.InsertOutcome(outcome));
        h.Shutdown();
        Assert.Single(Rows());
    }

    [Theory] // B-38 (dnevnik ne moze da se pise), B-39 (disk pun)
    [InlineData("Access to the path is denied.")]
    [InlineData("There is not enough space on the disk.")]
    public void B38_B39_JournalCannotBeWritten_NothingConfirmed_ThenEverythingArrives(string error)
    {
        var broken = true;
        var faults = new HistoryFaults { Journal = new PlayJournalFaults { BeforeWrite = () => { if (Volatile.Read(ref broken)) { throw new IOException(error); } } } };
        using var h = Open(scribe: true, faults: faults);
        for (var i = 0; i < 20; i++)
        {
            Play(h, 300 + i);
        }

        WaitUntil(() => h.Scribe.JournalFailing, TimeSpan.FromSeconds(5), "stanje 'ne cuva se'");
        Assert.Equal(0, h.Box.Confirmed); // potvrdjeno_do se ne pomera pre potvrde diska
        Assert.Empty(Rows());
        Assert.Contains(_log, m => m.Contains("Istorija se ne cuva na disk", StringComparison.Ordinal));
        Volatile.Write(ref broken, false);
        WaitUntil(() => Rows().Count == 20, TimeSpan.FromSeconds(10), "20 redova posle oporavka diska");
        Assert.False(h.Scribe.JournalFailing);
        Assert.Equal(h.Box.Reserved, h.Box.Confirmed);
    }

    [Fact] // B-39: upozorenje ispod 200 MB
    public void B39_LowSpaceWarning_Below200MB()
    {
        long free = 100L * 1024 * 1024;
        using var h = Open(faults: new HistoryFaults { Journal = new PlayJournalFaults { FreeSpace = () => free } });
        h.Journal.Faults = new PlayJournalFaults { FreeSpace = () => free };
        Assert.True(h.LowSpace);
        free = 300L * 1024 * 1024;
        Assert.False(h.LowSpace);
    }

    [Fact] // B-40: fajl sa neupisanim zapisima (otvorena pesma) se ne arhivira
    public void B40_JournalWithOpenPlayback_IsNotArchived_UntilSettled()
    {
        var h = Open();
        for (var i = 0; i < 2500; i++)
        {
            Play(h, i); // 7.500 zapisa: staje u crnu kutiju; pisar ih upisuje tek ispod
        }

        var open = Guid.CreateVersion7();
        h.Prepared(open, 1, 1, 1, 1, null);
        h.Started(open, 1, 0);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(30)));
        Assert.True(h.Journal.Length > PlayJournal.RotateBytes);
        var archive = Path.Combine(_root, PlaybackHistory.JournalFolder, PlayJournal.ArchiveFolder);
        Assert.False(Directory.Exists(archive) && Directory.EnumerateFiles(archive).Any());

        h.Ended(open, 1, 10, 10);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(30)));
        Assert.Single(Directory.EnumerateFiles(archive));
        h.Shutdown();
        Assert.Equal(2501, Rows().Count);
    }

    [Theory] // B-42: pad u svakoj tacki toka jednog dogadjaja
    [InlineData("samo-crna-kutija")]
    [InlineData("posle-dnevnika")]
    [InlineData("posle-potvrde")]
    [InlineData("posle-baze")]
    public void B42_CrashAtEveryPointOfOneEvent(string point)
    {
        var faults = new HistoryFaults { AtPoint = p => { if (p == point) { throw new SimulatedCrash(p); } } };
        var h = Open(faults: faults);
        var pid = Play(h, 42);
        if (point != "samo-crna-kutija")
        {
            Assert.Throws<SimulatedCrash>(() => h.Scribe.DrainNow(TimeSpan.FromSeconds(10)));
        }

        h.Abandon();
        using var again = Open();
        Assert.Single(Rows());
        Assert.Equal("odsvirana", Row(pid).Status);
        var records = JournalRecords();
        Assert.Equal(records.Count, records.Select(r => r.Rid).Distinct().Count());
        Assert.Equal(3, records.Count(r => r.PlaybackId == pid));
    }

    [Fact] // B-211: pisar stoji dok nastane 10.500 dogadjaja; po povratku 10.000 iz crne kutije pa zapis gubitak
    public void B211_ScribeStopped_10500Events_ThenLostRecord()
    {
        var h = Open();
        for (var i = 0; i < 3500; i++)
        {
            Play(h, i);
        }

        Assert.Equal(500, h.Box.LostCount);
        Assert.True(h.Scribe.DrainNow(TimeSpan.FromSeconds(240))); // 3.333 upisa u bazu; G je pod opterecenjem drugih testova
        var records = JournalRecords();
        Assert.Equal(10_000, records.Count(r => r.Kind != JournalKind.Lost));
        var lost = Assert.Single(records, r => r.Kind == JournalKind.Lost);
        Assert.Equal(500, lost.Track);
        Assert.True(lost.Frame <= lost.TimeTicks);
        h.Shutdown();
    }

    [Fact] // B-212 (ceo put): 5.000 pesama; memorija istorije stalna
    public void B212_ManyPlays_OpenSetAndQueuesStayBounded()
    {
        using var h = Open(scribe: true);
        var maxOpen = 0;
        for (var i = 0; i < 5000; i++)
        {
            Play(h, i);
            if (i % 250 == 0)
            {
                maxOpen = Math.Max(maxOpen, h.Scribe.OpenPlaybacks);
            }

            if (i % 100 == 99)
            {
                // pravi radio pravi 3 zapisa na nekoliko minuta; ovde se samo ne dozvoljava da crna kutija pretekne pisara
                WaitUntil(() => h.Box.Reserved - h.Box.Confirmed < 1000, TimeSpan.FromSeconds(30), "pisar stize");
            }
        }

        WaitUntil(() => Rows().Count == 5000, TimeSpan.FromSeconds(120), "5.000 redova");
        output.WriteLine($"najvise otvorenih kod pisara: {maxOpen}");
        Assert.Equal(0, h.Scribe.OpenPlaybacks);
        Assert.Equal(0, h.Scribe.PendingDatabase);
        Assert.Equal(0, h.Box.LostCount);
        Assert.Equal(BlackBox.HeaderSize + ((long)BlackBox.DefaultCapacity * BlackBox.RecordSize), new FileInfo(Path.Combine(_root, PlaybackHistory.JournalFolder, BlackBox.FileName)).Length);
    }

    [Fact] // B-225: crna kutija ne moze da se napravi
    public void B225_BlackBoxUnavailable_HistoryStillWorks_AndIsFlagged()
    {
        Directory.CreateDirectory(Path.Combine(_root, PlaybackHistory.JournalFolder, BlackBox.FileName));
        using (var h = Open(scribe: true))
        {
            Assert.False(h.BlackBoxAvailable);
            Play(h, 1);
        }

        Assert.Single(Rows());
    }

    private static string ProbeExe()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var exe = Directory.EnumerateFiles(Path.Combine(tests, "LustreRadio.CrashProbe", "bin", config), "LustreRadio.CrashProbe.exe", SearchOption.AllDirectories).First();
        return exe;
    }

    private void RunProbe(int plays)
    {
        Db.Dispose();
        _db = null;
        var start = new ProcessStartInfo(ProbeExe(), [_root, plays.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000));
        output.WriteLine(text.Trim());
        output.WriteLine($"izlazni kod: 0x{process.ExitCode:X8}");
        Assert.NotEqual(0, process.ExitCode); // proces je pao
        Assert.Contains("PROBA:", text, StringComparison.Ordinal);
    }

    [Theory] // B-198: pad u cetiri tacke oko kraja pesme
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void B198_CrashAroundTrackEnd(int point)
    {
        var h = Open();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        h.Prepared(a, 1, 1, 1, 1, null);
        h.Started(a, 1, 0);
        switch (point)
        {
            case 1: // pretapanje potvrdjeno, kraj repa nije
                h.Prepared(b, 2, 1, 1, 1, null);
                h.Crossfade(b, 2, a, 1, 100);
                break;
            case 2: // pretapanje pocelo, zapis jos nije u crnoj kutiji (koordinator ga nije prepisao)
                h.Prepared(b, 2, 1, 1, 1, null);
                break;
            case 3: // pesma bez sledece zavrsila, kraj na disku
                h.Ended(a, 1, 200, 200);
                break;
            case 4: // pesma bez sledece zavrsila, kraj jos nije zapisan
                break;
        }

        h.Abandon();
        using var again = Open();
        var expected = point switch
        {
            1 => ("nepotvrdjena-c", "nepotvrdjena-b"),
            2 => ("nepotvrdjena-b", "nepotvrdjena-a"),
            3 => ("odsvirana", (string?)null),
            _ => ("nepotvrdjena-b", (string?)null),
        };
        Assert.Equal(expected.Item1, Row(a).Status);
        if (expected.Item2 is { } bStatus)
        {
            Assert.Equal(bStatus, Row(b).Status);
        }

        Assert.DoesNotContain(Rows(), r => r.Status == "prekinuta");
    }

    [Fact] // B-84, B-85, B-87: pad posle "pripremljena" (pesma krenula ili nije) -> NEPOTVRDJENO (a), jedan red; snimak stanja se ne koristi
    public void B84_B85_B87_CrashAfterPrepared_UnconfirmedA_OneRow()
    {
        var h = Open();
        var pid = Guid.CreateVersion7();
        h.Prepared(pid, 9, 1, 1, 1, null);
        h.Abandon();
        using var again = Open();
        Assert.Equal("nepotvrdjena-a", Row(pid).Status);
        Assert.Single(Rows(), r => r.PlaybackId == pid.ToString());
    }

    [Fact] // B-195: memorija 3 napustene + 1 zaglavljene radne niti, izmerena u POSEBNOM procesu (lazni dekoder)
    public void B195_MemoryOfStuckThreads_MeasuredInSeparateProcess()
    {
        var start = new ProcessStartInfo(ProbeExe(), ["memorija"]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000));
        output.WriteLine(text.Trim());
        Assert.Contains("MEMORIJA | napustenih niti 3", text, StringComparison.Ordinal);
    }

    [Fact] // B-221: pisar ne radi; 30 pesama; pravi pad procesa; oporavak iz crne kutije
    public void B221_ScribeNotRunning_ProcessCrash_All30RecoveredFromBlackBox()
    {
        RunProbe(30);
        Assert.False(File.Exists(Path.Combine(_root, PlaybackHistory.JournalFolder, PlayJournal.FileName)) && JournalRecords().Count > 0);
        using var h = Open();
        Assert.Equal(89, h.RecoveryResult.FromBlackBox); // 29 x 3 + 2
        var rows = Rows();
        Assert.Equal(30, rows.Count);
        Assert.Equal(29, rows.Count(r => r.Status == "odsvirana"));
        Assert.Single(rows, r => r.Status == "nepotvrdjena-b");
        Assert.Equal(Enumerable.Range(1000, 30).Select(i => (long)i), rows.Select(r => r.TrackId).Order());
    }

    [Fact] // B-222: pisar ne radi; crna kutija se napuni (10.000 + 300); pravi pad procesa
    public void B222_BlackBoxFull_ProcessCrash_10000Recovered_LostRecordWith300()
    {
        RunProbe(3434); // 3434 x 3 - 1 = 10.301 dogadjaj
        using var h = Open();
        Assert.Equal(10_000, h.RecoveryResult.FromBlackBox);
        Assert.Equal(301, h.RecoveryResult.Lost);
        var lost = Assert.Single(JournalRecords(), r => r.Kind == JournalKind.Lost);
        Assert.Equal(301, lost.Track);
        Assert.True(lost.Frame > 0 && lost.Frame <= lost.TimeTicks);
        var rows = Rows();
        output.WriteLine($"redova: {rows.Count}; odsvirano {rows.Count(r => r.Status == "odsvirana")}; nepotvrdjeno {rows.Count(r => r.Status.StartsWith("nepotvrdjena", StringComparison.Ordinal))}");
        Assert.Equal(3333, rows.Count(r => r.Status == "odsvirana")); // 3333 x 3 = 9.999 zapisa
        Assert.Single(rows, r => r.Status == "nepotvrdjena-a"); // 10.000. zapis: samo "pripremljena"
    }
}
