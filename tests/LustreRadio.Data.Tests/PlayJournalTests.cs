using System.Text;
using LustreRadio.Core.History;
using LustreRadio.Data;
using Xunit;

namespace LustreRadio.Data.Tests;

/// <summary>Faza B, korak B2: dnevnik emitovanja (emitovanje.jsonl).</summary>
public sealed class PlayJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lustre-journal-" + Guid.NewGuid().ToString("N"));

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

    private static readonly Guid Run = Guid.CreateVersion7();

    private static JournalRecord Rec(long seq, JournalKind kind = JournalKind.Started) => new()
    {
        Run = Run, Seq = seq, Kind = kind, PlaybackId = Guid.CreateVersion7(), Track = seq, Frame = seq * 100,
        TimeTicks = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc).AddSeconds(seq).Ticks,
    };

    [Fact]
    public void EveryKind_RoundTrips_WithItsRid()
    {
        using var journal = new PlayJournal(_folder);
        var a = Guid.CreateVersion7();
        var records = new List<JournalRecord>
        {
            Rec(1, JournalKind.Prepared) with { Source = 1, Part = 2, Tier = 1, Program = "Ponedeljak - jutro" },
            Rec(2, JournalKind.Started),
            Rec(3, JournalKind.Crossfade) with { PlaybackIdA = a, TrackA = 9 },
            Rec(4, JournalKind.Ended) with { PositionMs = 180_000 },
            Rec(5, JournalKind.Stopped) with { Reason = StopReason.Skip, PositionMs = 1200 },
            Rec(6, JournalKind.Discarded) with { Reason = StopReason.NotLoaded },
            Rec(7, JournalKind.Shutdown) with { PlaybackId = Guid.Empty, Reason = StopReason.Shutdown },
            Rec(8, JournalKind.ProgramCrash) with { PlaybackId = Guid.Empty, PlaybackIdA = Guid.CreateVersion7(), Frame = DateTime.UtcNow.Ticks },
            Rec(9, JournalKind.Unconfirmed) with { Unconfirmed = UnconfirmedKind.TailUnknown },
            Rec(10, JournalKind.Lost) with { PlaybackId = Guid.Empty, Track = 300, Frame = DateTime.UtcNow.Ticks },
        };
        journal.Append(records);
        var back = journal.ReadAll();
        Assert.Equal(0, back.InvalidLines);
        Assert.Equal(records.Count, back.Records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            Assert.Equal(records[i].Rid, back.Records[i].Rid);
            Assert.Equal(records[i].Kind, back.Records[i].Kind);
            Assert.Equal(records[i].PlaybackId, back.Records[i].PlaybackId);
            Assert.Equal(records[i].PlaybackIdA, back.Records[i].PlaybackIdA);
            Assert.Equal(records[i].Track, back.Records[i].Track);
            Assert.Equal(records[i].Reason, back.Records[i].Reason);
            Assert.Equal(records[i].Unconfirmed, back.Records[i].Unconfirmed);
            Assert.Equal(records[i].TimeTicks, back.Records[i].TimeTicks);
        }

        var text = File.ReadAllText(journal.Path);
        Assert.Contains("\"vrsta\":\"pretapanje\",\"pid_a\":", text, StringComparison.Ordinal);
        Assert.Contains("\"pid_b\":", text, StringComparison.Ordinal);
        Assert.Contains("\"program\":\"Ponedeljak - jutro\"", text, StringComparison.Ordinal);
    }

    [Fact] // B-36: poslednji red presecen na svakom bajtu
    public void B36_LastLineCutAtEveryByte_IsSkipped_AndFileIsTruncated()
    {
        string path;
        byte[] full;
        int twoLines;
        using (var journal = new PlayJournal(_folder))
        {
            journal.Append([Rec(1), Rec(2)]);
            twoLines = (int)journal.Length;
            journal.Append([Rec(3)]);
            path = journal.Path;
        }

        full = File.ReadAllBytes(path);
        for (var cut = 0; cut < full.Length - twoLines; cut++)
        {
            File.WriteAllBytes(path, full[..(twoLines + cut)]);
            using var journal = new PlayJournal(_folder);
            var read = journal.ReadAll();
            Assert.Equal(new long[] { 1, 2 }, read.Records.Select(r => r.Seq).ToArray());
            Assert.Equal(cut > 0, read.TornTailTruncated);
            Assert.Equal(0, read.InvalidLines);
            Assert.Equal(twoLines, new FileInfo(path).Length);
        }
    }

    [Fact] // B-37: neispravan red u sredini se preskace, broji i ne zaustavlja citanje
    public void B37_InvalidMiddleLine_IsSkippedAndCounted()
    {
        string path;
        using (var journal = new PlayJournal(_folder))
        {
            journal.Append([Rec(1), Rec(2), Rec(3)]);
            path = journal.Path;
        }

        var lines = File.ReadAllLines(path);
        lines[1] = lines[1].Replace("\"pocetak\"", "\"pocetaK\"", StringComparison.Ordinal); // izmena bez novog kontrolnog zbira
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.AppendAllText(path, "ovo nije json\n");
        File.AppendAllText(path, Encoding.UTF8.GetString(Line(Rec(4))));

        using var again = new PlayJournal(_folder);
        var read = again.ReadAll();
        Assert.Equal(new long[] { 1, 3, 4 }, read.Records.Select(r => r.Seq).ToArray());
        Assert.Equal(2, read.InvalidLines);
        Assert.False(read.TornTailTruncated);
    }

    [Fact] // B-41: svaki upis trazi potvrdu diska pre nego sto se vrati
    public void B41_EveryAppend_RequestsDiskConfirmation()
    {
        using var journal = new PlayJournal(_folder);
        for (var i = 1; i <= 5; i++)
        {
            journal.Append([Rec(i)]);
        }

        Assert.Equal(5, journal.Flushes);
        Assert.Equal(5, journal.LinesWritten);
    }

    [Fact] // B-40: arhiviranje preko 1 MB; arhive starije od 90 dana se brisu
    public void B40_LargeFileIsArchived_OldArchivesDeleted()
    {
        using var journal = new PlayJournal(_folder);
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(journal.ArchiveIfLarge(now)); // prazan
        var seq = 1L;
        while (journal.Length <= PlayJournal.RotateBytes)
        {
            journal.Append(Enumerable.Range(0, 500).Select(_ => Rec(seq++)).ToList());
        }

        var archived = journal.ArchiveIfLarge(now);
        Assert.NotNull(archived);
        Assert.True(File.Exists(archived));
        Assert.False(File.Exists(journal.Path));
        File.SetLastWriteTimeUtc(archived!, now.AddDays(-91));
        journal.Append([Rec(seq)]);
        Assert.Null(journal.ArchiveIfLarge(now));
        Assert.False(File.Exists(archived));
        Assert.Single(journal.ReadAll().Records);
    }

    private static byte[] Line(JournalRecord record)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        PlayJournal.WriteLine(buffer, record);
        return buffer.WrittenSpan.ToArray();
    }
}
