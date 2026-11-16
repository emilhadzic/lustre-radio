using LustreRadio.Core.History;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Core.Tests;

/// <summary>Faza B, korak B2: crna kutija (D8-1).</summary>
public sealed class BlackBoxTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lustre-bb-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, BlackBox.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder
        }
    }

    private static JournalRecord Play(JournalKind kind, int i) => new()
    {
        Kind = kind,
        PlaybackId = Guid.CreateVersion7(),
        Track = i,
        Frame = i * 10L,
        TimeTicks = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc).AddSeconds(i).Ticks,
        Program = "Ponedeljak - jutro",
    };

    [Fact]
    public void Record_RoundTrips_AllFields_AndRidComesFromTheBox()
    {
        using var box = BlackBox.Open(FilePath, 100);
        box.Reset(Guid.CreateVersion7());
        Assert.True(box.Available);
        var record = new JournalRecord
        {
            Kind = JournalKind.Crossfade, PlaybackId = Guid.CreateVersion7(), PlaybackIdA = Guid.CreateVersion7(), Track = 7, TrackA = 6,
            Frame = 123456, TimeTicks = DateTime.UtcNow.Ticks, PositionMs = 4321, Source = 1, Part = 2, Tier = 3, Program = "Četvrtak - popodne",
        };
        Assert.True(box.TryAppend(ref record));
        Assert.Equal(1, record.Seq);
        Assert.Equal(box.Run, record.Run);
        Assert.True(box.TryRead(1, out var back));
        Assert.Equal(record.PlaybackId, back.PlaybackId);
        Assert.Equal(record.PlaybackIdA, back.PlaybackIdA);
        Assert.Equal((7L, 6L, 123456L, 4321), (back.Track, back.TrackA, back.Frame, back.PositionMs));
        Assert.Equal("Četvrtak - popodne", back.Program);
        Assert.Equal(((byte)1, (byte)2, (byte)3), (back.Source, back.Part, back.Tier));
        Assert.False(box.TryRead(2, out _));
    }

    [Fact]
    public void LongProgramName_IsShortened_ToWholeLetters()
    {
        using var box = BlackBox.Open(FilePath, 10);
        box.Reset(Guid.CreateVersion7());
        var record = Play(JournalKind.Prepared, 1);
        record.Program = "Ččččččččččččččččččččččččč";
        Assert.True(box.TryAppend(ref record));
        Assert.True(box.TryRead(1, out var back));
        Assert.Equal(new string('Č', JournalRecord.ProgramBytes / 2), back.Program);
    }

    [Fact] // B-211 (deo u crnoj kutiji): pisar stoji, 10.500 dogadjaja
    public void B211_WhenFull_ExactlyTheExtraEventsAreCountedAsLost_NothingOverwritten()
    {
        using var box = BlackBox.Open(FilePath);
        box.Reset(Guid.CreateVersion7());
        var accepted = 0;
        for (var i = 1; i <= 10_500; i++)
        {
            var record = Play(JournalKind.Started, i);
            if (box.TryAppend(ref record))
            {
                accepted++;
            }
        }

        Assert.Equal(10_000, accepted);
        Assert.Equal(500, box.LostCount);
        Assert.Equal(new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc).AddSeconds(10_001), box.LostFirstUtc);
        Assert.Equal(new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc).AddSeconds(10_500), box.LostLastUtc);
        var recovered = box.ReadForRecovery();
        Assert.Equal(10_000, recovered.Records.Count);
        Assert.Equal(Enumerable.Range(1, 10_000).Select(i => (long)i), recovered.Records.Select(r => r.Track));
        Assert.Equal(500, recovered.UnreportedLost);
    }

    [Fact] // B-211: brojac gubitka je u fajlu i prezivljava zatvaranje (u fajlu, kao posle pada)
    public void LostCounter_IsInTheFile()
    {
        using (var box = BlackBox.Open(FilePath, 10))
        {
            box.Reset(Guid.CreateVersion7());
            for (var i = 1; i <= 13; i++)
            {
                var record = Play(JournalKind.Started, i);
                box.TryAppend(ref record);
            }
        }

        using var again = BlackBox.Open(FilePath, 10);
        var found = again.ReadForRecovery();
        Assert.Equal(10, found.Records.Count);
        Assert.Equal(3, found.UnreportedLost);
    }

    [Fact] // B-212 (deo u crnoj kutiji): 100.000 pesama; velicina stalna; upis ne zauzima memoriju
    public void B212_HundredThousandPlays_ConstantSize_ZeroAllocationPerEvent()
    {
        using var box = BlackBox.Open(FilePath);
        box.Reset(Guid.CreateVersion7());
        var size = new FileInfo(FilePath).Length;
        var record = Play(JournalKind.Started, 1);
        box.TryAppend(ref record);
        box.Confirm(1);
        long allocated = 0;
        for (var i = 0; i < 100_000; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var k = 0; k < 3; k++)
            {
                record.Kind = (JournalKind)(k + 2);
                if (!box.TryAppend(ref record))
                {
                    Assert.Fail("crna kutija se napunila iako pisar potvrdjuje");
                }
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            box.Confirm(box.Reserved);
        }

        output.WriteLine($"300.000 zapisa; zauzeto na niti: {allocated} B; velicina fajla {size} B");
        Assert.Equal(0, allocated);
        Assert.Equal(size, new FileInfo(FilePath).Length);
        Assert.Equal(300_001, box.Reserved);
        Assert.Equal(0, box.LostCount);
    }

    [Fact] // B-225: crna kutija ne moze da se napravi -> red u memoriji, uz oznaku
    public void B225_FileCannotBeCreated_InMemoryQueueIsUsed()
    {
        Directory.CreateDirectory(FilePath); // na mestu fajla je folder
        var messages = new List<string>();
        using var box = BlackBox.Open(FilePath, 100, messages.Add);
        Assert.False(box.Available);
        Assert.NotNull(box.UnavailableReason);
        Assert.Contains(messages, m => m.Contains("nedostupna", StringComparison.Ordinal));
        var record = Play(JournalKind.Started, 1);
        Assert.True(box.TryAppend(ref record));
        Assert.True(box.TryRead(1, out _));
    }

    [Fact] // B-226: zapis presecen u svakom bajtu se zanemaruje u celini; ostali se oporavljaju
    public void B226_TornRecordAtEveryByte_IsIgnoredWhole_OthersRecovered()
    {
        for (var bytes = 0; bytes <= BlackBox.RecordSize; bytes++)
        {
            using var box = BlackBox.InMemory(16, "test");
            var first = Play(JournalKind.Prepared, 1);
            box.TryAppend(ref first);
            var torn = Play(JournalKind.Started, 2);
            box.WriteTorn(ref torn, bytes);
            var third = Play(JournalKind.Ended, 3);
            box.TryAppend(ref third);

            var found = box.ReadForRecovery();
            var expected = bytes == BlackBox.RecordSize ? new long[] { 1, 2, 3 } : [1, 3];
            Assert.Equal(expected, found.Records.Select(r => r.Track).ToArray());
            Assert.Equal(bytes == BlackBox.RecordSize ? 0 : 1, found.InvalidRecords);
        }
    }

    [Fact] // vise pisaca odjednom: svaki zapis tacno jednom, rid bez rupa
    public void ConcurrentWriters_EveryRecordOnce_RidsWithoutGaps()
    {
        using var box = BlackBox.InMemory(50_000, "test");
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                var record = Play(JournalKind.Started, (t * 100_000) + i);
                box.TryAppend(ref record);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var found = box.ReadForRecovery();
        Assert.Equal(40_000, found.Records.Count);
        Assert.Equal(Enumerable.Range(1, 40_000).Select(i => (long)i), found.Records.Select(r => r.Seq));
        Assert.Equal(40_000, found.Records.Select(r => r.Track).Distinct().Count());
    }

    [Fact]
    public void Reset_StartsNewRun_AndForgetsOldRecords()
    {
        using var box = BlackBox.Open(FilePath, 10);
        box.Reset(Guid.CreateVersion7());
        var record = Play(JournalKind.Started, 1);
        box.TryAppend(ref record);
        var oldRun = box.Run;
        box.Reset(Guid.CreateVersion7());
        Assert.NotEqual(oldRun, box.Run);
        Assert.Empty(box.ReadForRecovery().Records);
        Assert.Equal(0, box.Reserved);
    }

    [Fact] // cuvar: trazi upis na disk na zadati razmak
    public void Guard_FlushesPeriodically()
    {
        using var box = BlackBox.Open(FilePath, 10);
        box.Reset(Guid.CreateVersion7());
        var before = box.Flushes;
        using (var guard = new BlackBoxGuard(box, TimeSpan.FromMilliseconds(50)))
        {
            guard.Start();
            Thread.Sleep(600);
        }

        Assert.True(box.Flushes - before >= 3, $"upisa na disk: {box.Flushes - before}");
    }

    [Fact]
    public void Outcome_FollowsTheFinalStatusTable()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var t = DateTime.UtcNow.Ticks;
        var crossfade = new JournalRecord { Kind = JournalKind.Crossfade, PlaybackId = b, PlaybackIdA = a, Track = 2, TrackA = 1, TimeTicks = t };

        // red 1: pretapanje + kraj = ODSVIRANA, prelaz dostignut
        var agg = new PlaybackAggregate(a);
        agg.Apply(new JournalRecord { Kind = JournalKind.Started, PlaybackId = a, Track = 1, TimeTicks = t }, false);
        agg.Apply(crossfade, true);
        Assert.Null(agg.Outcome()); // pretapanje nije zavrsni zapis
        Assert.Equal(UnconfirmedKind.TailUnknown, agg.PendingKind); // red 5: podvrsta (c)
        agg.Apply(new JournalRecord { Kind = JournalKind.Ended, PlaybackId = a, Track = 1, TimeTicks = t }, false);
        Assert.Equal((PlaybackStatus.Played, true), (agg.Outcome()!.Status, agg.Outcome()!.TransitionReached));

        // red 4: Sledeca tokom repa = PREKINUTO – Sledeca, prelaz dostignut
        var skipped = new PlaybackAggregate(a);
        skipped.Apply(crossfade, true);
        skipped.Apply(new JournalRecord { Kind = JournalKind.Stopped, Reason = StopReason.Skip, PlaybackId = a, Track = 1, TimeTicks = t }, false);
        Assert.Equal((PlaybackStatus.Stopped, StopReason.Skip, true), (skipped.Outcome()!.Status, skipped.Outcome()!.Reason, skipped.Outcome()!.TransitionReached));

        // B kao pesma koja je krenula: bez zavrsnog = podvrsta (b)
        var bAgg = new PlaybackAggregate(b);
        bAgg.Apply(crossfade, false);
        Assert.Equal(UnconfirmedKind.StartedEndUnknown, bAgg.PendingKind);

        // samo pripremljena = podvrsta (a); odbacena = nema reda u bazi
        var prepared = new PlaybackAggregate(Guid.CreateVersion7());
        prepared.Apply(new JournalRecord { Kind = JournalKind.Prepared, Track = 3, TimeTicks = t }, false);
        Assert.Equal(UnconfirmedKind.NotStarted, prepared.PendingKind);
        prepared.Apply(new JournalRecord { Kind = JournalKind.Discarded, Track = 3, TimeTicks = t }, false);
        Assert.Null(prepared.Outcome());

        // dva zavrsna zapisa: vazi prvi, drugi se broji kao nepravilnost
        var twice = new PlaybackAggregate(a);
        twice.Apply(new JournalRecord { Kind = JournalKind.Stopped, Reason = StopReason.Skip, TimeTicks = t }, false);
        twice.Apply(new JournalRecord { Kind = JournalKind.Ended, TimeTicks = t }, false);
        Assert.Equal((PlaybackStatus.Stopped, 1), (twice.Outcome()!.Status, twice.ExtraTerminals));
    }
}
