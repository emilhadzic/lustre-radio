using LustreRadio.Core.Licensing;
using LustreRadio.Core.Scheduling;
using LustreRadio.Data;
using LustreRadio.Playout;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>
/// Ceo lanac bez zvucnog uredjaja: folder -> skener -> baza -> licence -> raspored -> rotacija -> prelazi.
/// Vreme tece zajedno sa pripremljenim zvukom, pa se sati "odsviraju" za sekunde.
/// </summary>
public sealed class PlayoutTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 44100;
    private static readonly DateTime Monday = new(2026, 11, 16);
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private int[] AddTones(int count, double seconds, bool verify = true)
    {
        var tones = Enumerable.Range(0, count).Select(i => 300 + (100 * i)).ToArray();
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

    [Fact]
    public void Indexer_AddsTracks_AsUnverified_AndFollowsChangesOnDisk()
    {
        _env.AddTone(300, 2);
        _env.AddTone(400, 2);
        var third = _env.AddTone(500, 2);

        var first = _env.Index();
        Assert.Equal(new IndexSummary(3, 3, 0, 0, 0), first);
        var tracks = _env.Db.Tracks.GetAll();
        Assert.All(tracks, t => Assert.True(t.DecodeOk));
        Assert.All(tracks, t => Assert.Equal(2 * Rate, t.ExactFrames));
        Assert.All(tracks, t => Assert.Equal(LicenseStatus.Imported, _env.Db.Licenses.Get(t.ContentHash)!.Status));

        Assert.Equal(new IndexSummary(3, 0, 0, 0, 0), _env.Index());

        File.Delete(third);
        Assert.Equal(1, _env.Index().MarkedMissing);
        Assert.True(_env.TrackOf(500).Missing);
        Assert.Equal(0, _env.Index().MarkedMissing);

        var oldHash = _env.TrackOf(400).ContentHash;
        TestEnvironment.WriteTone(Path.Combine(_env.MusicFolder, "ton-400.wav"), 450, 3);
        var afterChange = _env.Index();
        Assert.Equal(1, afterChange.Rescanned);
        var changed = _env.TrackOf(400);
        Assert.NotEqual(oldHash, changed.ContentHash);
        Assert.Equal(3 * Rate, changed.ExactFrames);
        Assert.Equal(LicenseStatus.Imported, _env.Db.Licenses.Get(changed.ContentHash)!.Status);

        TestEnvironment.WriteTone(third, 500, 2);
        _env.Index();
        Assert.False(_env.TrackOf(500).Missing);
        Assert.Equal(3, _env.Db.Tracks.GetAll().Count);
    }

    [Fact] // T-LIC-01 kroz ceo lanac
    public void NothingVerified_RadioStaysSilent()
    {
        AddTones(4, 3, verify: false);
        var (director, clock) = _env.Director(Monday.AddHours(10));
        var session = TestEnvironment.Session(director, clock);

        Assert.Equal(0, director.BroadcastableCount);
        Assert.Empty(TestEnvironment.Render(session, 30));
        Assert.Empty(_env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()));
    }

    [Fact] // T-LIC-01 kroz ceo lanac
    public void OnlyVerifiedTracksAreEverHeard()
    {
        var tones = AddTones(6, 3, verify: false);
        int[] verified = [tones[0], tones[2], tones[4]];
        foreach (var hz in verified)
        {
            _env.Verify(hz);
        }

        var (director, clock) = _env.Director(Monday.AddHours(10));
        var session = TestEnvironment.Session(director, clock);
        var audio = TestEnvironment.Render(session, 40);

        var allowedIds = verified.Select(hz => _env.TrackOf(hz).Id).ToHashSet();
        var history = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime());
        Assert.True(history.Count >= 10);
        Assert.All(history, h => Assert.Contains(h.TrackId, allowedIds));

        foreach (var hz in tones)
        {
            var level = TestEnvironment.Tone(audio, hz);
            output.WriteLine($"{hz} Hz: {level:F4}");
            if (verified.Contains(hz))
            {
                Assert.True(level > 0.01, $"proverena pesma {hz} Hz se ne cuje");
            }
            else
            {
                Assert.True(level < 0.0005, $"NEPROVERENA pesma {hz} Hz se cuje ({level})");
            }
        }
    }

    [Fact]
    public void DuringWorkingHours_PlaysContinuously_AndWritesHistory()
    {
        AddTones(8, 3);
        var (director, clock) = _env.Director(Monday.AddHours(10));
        var session = TestEnvironment.Session(director, clock);
        var started = new List<NowPlaying>();
        director.TrackStarted += started.Add;

        var audio = TestEnvironment.Render(session, 20);

        Assert.InRange(audio.Length, 20 * Rate, (20 * Rate) + 1024);
        Assert.True(TestEnvironment.LongestSilenceFrames(audio) <= Rate / 10, "tisina duza od 0,1 s");

        var history = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime());
        Assert.Equal(session.Sequencer.Events.Count, history.Count);
        Assert.Equal(history.Count, started.Count);
        Assert.All(history, h =>
        {
            Assert.Equal("Ponedeljak - jutro", h.Program);
            Assert.Equal("Morning", h.Part);
            Assert.Equal("WeeklySlot", h.Source);
        });
        Assert.Equal(history.Count, history.Select(h => h.TrackId).Distinct().Count()); // 8 pesama, nijedna dvaput u 20 s
    }

    [Fact] // T-SCH-01 kroz ceo lanac
    public void BeforeOpening_RadioIsSilent()
    {
        AddTones(4, 3);
        var (director, clock) = _env.Director(Monday.AddHours(6));
        var session = TestEnvironment.Session(director, clock);

        Assert.False(director.ShouldPlayNow());
        Assert.Empty(TestEnvironment.Render(session, 10));
    }

    [Fact] // T-SCH-04 kroz ceo lanac
    public void AtClosingTime_FadesOutOver20Seconds_AndStops()
    {
        AddTones(8, 4);
        var (director, clock) = _env.Director(Monday.AddHours(23).AddMinutes(29).AddSeconds(50));
        var session = TestEnvironment.Session(director, clock);

        var audio = TestEnvironment.Render(session, 120);

        // 10 s do 23:30 + 20 s stisavanja
        Assert.InRange(audio.Length / (double)Rate, 29.9, 30.2);
        Assert.True(session.Ended);
        var loud = TestEnvironment.Rms(audio.AsSpan(5 * Rate, Rate / 2));
        var fading = TestEnvironment.Rms(audio.AsSpan(22 * Rate, Rate / 2));
        var tail = TestEnvironment.Rms(audio.AsSpan(audio.Length - (Rate / 5)));
        output.WriteLine($"glasno={loud:F4} u stisavanju={fading:F4} kraj={tail:F5}");
        Assert.True(loud > 0.05);
        Assert.True(fading < loud * 0.8 && fading > tail);
        Assert.True(tail < 0.005);
        Assert.True(TestEnvironment.LongestSilenceFrames(audio.AsSpan(0, 25 * Rate)) <= Rate / 10);

        // posle zatvaranja nista vise ne krece
        Assert.False(director.ShouldPlayNow());
        Assert.Null(director.NextTrack());
    }

    [Fact]
    public void DayPartChange_NextTracksUseNewProgram_WithoutInterruption()
    {
        AddTones(10, 3);
        var (director, clock) = _env.Director(Monday.AddHours(10).AddMinutes(59).AddSeconds(50));
        var session = TestEnvironment.Session(director, clock);

        var audio = TestEnvironment.Render(session, 25);

        var programs = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Select(h => h.Program).ToList();
        Assert.Contains("Ponedeljak - jutro", programs);
        Assert.Contains("Ponedeljak - popodne", programs);
        Assert.True(programs.IndexOf("Ponedeljak - popodne") > programs.LastIndexOf("Ponedeljak - jutro"));
        Assert.True(TestEnvironment.LongestSilenceFrames(audio) <= Rate / 10);
    }

    [Fact] // nastavak posle restarta
    public void AfterRestart_ResumesSameTrack_FromSavedPosition()
    {
        AddTones(6, 8);
        var (director1, clock1) = _env.Director(Monday.AddHours(10));
        var session1 = TestEnvironment.Session(director1, clock1);
        TestEnvironment.Render(session1, 10);
        session1.Tick(saveSnapshot: true);

        var snapshot = director1.LoadSnapshot()!;
        var historyBefore = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Count;
        output.WriteLine($"snimak: pesma {snapshot.TrackId}, pozicija {snapshot.PositionSeconds:F2} s");
        Assert.Equal(2, historyBefore);
        Assert.InRange(snapshot.PositionSeconds, 2.9, 3.1); // druga pesma je krenula na 7 s

        // "restart" 20 sekundi kasnije: novi reditelj nad istom bazom
        var (director2, clock2) = _env.Director(Monday.AddHours(10).AddSeconds(30), seed: 9);
        var session2 = TestEnvironment.Session(director2, clock2, director2.LoadSnapshot());

        Assert.True(session2.Resumed);
        Assert.Equal(historyBefore, _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Count);

        var audio = TestEnvironment.Render(session2, 12);
        var events = session2.Sequencer.Events;
        Assert.Equal(snapshot.TrackId, events[0].Id);

        // preostalo 5 s pesme, prelaz 1 s: sledeca krece na 4 s
        Assert.InRange(events[1].StartFrame / (double)Rate, 3.9, 4.1);
        var resumedHz = int.Parse(_env.Db.Tracks.Get(long.Parse(snapshot.TrackId))!.Title.Split('-')[1]);
        Assert.True(TestEnvironment.Tone(audio.AsSpan(1 * Rate, Rate), resumedHz) > 0.05);

        // ulazak zvuka 3 s: pocetak je tisi od ostatka
        Assert.True(TestEnvironment.Rms(audio.AsSpan(0, Rate / 10)) < TestEnvironment.Rms(audio.AsSpan(3 * Rate, Rate / 2)) * 0.5);
    }

    [Fact]
    public void OldSnapshot_IsIgnored_NewTrackStarts()
    {
        AddTones(6, 8);
        var (director1, clock1) = _env.Director(Monday.AddHours(10));
        var session1 = TestEnvironment.Session(director1, clock1);
        TestEnvironment.Render(session1, 10);
        session1.Tick(saveSnapshot: true);

        var (director2, clock2) = _env.Director(Monday.AddHours(10).AddMinutes(40));
        var session2 = TestEnvironment.Session(director2, clock2, director2.LoadSnapshot());

        Assert.False(session2.Resumed);
        Assert.NotEmpty(TestEnvironment.Render(session2, 5));
    }

    [Fact] // brza provera pre pustanja: licenca vazi samo za fajl koji je skeniran
    public void FileReplacedAfterVerification_IsNotPlayed()
    {
        var tones = AddTones(4, 3);
        var replaced = _env.TrackOf(tones[1]);
        TestEnvironment.WriteTone(replaced.Path, 2500, 5); // zamenjen drugim zvukom, bez ponovnog skeniranja

        var (director, clock) = _env.Director(Monday.AddHours(10));
        var session = TestEnvironment.Session(director, clock);
        var audio = TestEnvironment.Render(session, 40);

        Assert.DoesNotContain(_env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()), h => h.TrackId == replaced.Id);
        Assert.True(TestEnvironment.Tone(audio, 2500) < 0.0005, "zamenjeni fajl se cuje");
        Assert.True(TestEnvironment.LongestSilenceFrames(audio) <= Rate / 10);
    }

    [Fact] // T-AUD-05 kroz ceo lanac: pokvaren fajl ne zaustavlja muziku
    public void FileThatCannotBeDecoded_IsSkipped_AndFaultIsRecorded()
    {
        var tones = AddTones(4, 3);
        var broken = _env.TrackOf(tones[2]);
        var garbage = new byte[broken.SizeBytes];
        new Random(4).NextBytes(garbage);
        File.WriteAllBytes(broken.Path, garbage);
        File.SetLastWriteTimeUtc(broken.Path, broken.LastWriteUtc); // ista velicina i datum: brza provera ga propusta

        var (director, clock) = _env.Director(Monday.AddHours(10));
        var session = TestEnvironment.Session(director, clock);
        var audio = TestEnvironment.Render(session, 40);

        Assert.True(_env.Db.Tracks.Get(broken.Id)!.FaultCount >= 1);
        Assert.DoesNotContain(_env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()), h => h.TrackId == broken.Id);
        Assert.InRange(audio.Length, 40 * Rate, (40 * Rate) + 1024);
        Assert.True(TestEnvironment.LongestSilenceFrames(audio) <= Rate / 10);
    }

    [Fact]
    public void AfterRestart_RotationRemembersWhatWasPlayed()
    {
        var tones = AddTones(10, 3);
        var (director1, clock1) = _env.Director(Monday.AddHours(10));
        TestEnvironment.Render(TestEnvironment.Session(director1, clock1), 15);
        var played = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Select(h => h.TrackId).ToHashSet();
        Assert.InRange(played.Count, 6, 9);

        var (director2, clock2) = _env.Director(Monday.AddHours(10).AddMinutes(30), seed: 5);
        clock2.Frames = () => 0;
        var remaining = tones.Length - played.Count;
        for (var i = 0; i < remaining; i++)
        {
            using var next = director2.NextTrack()!.Source;
        }

        var all = _env.Db.History.GetSince(DateTime.MinValue.ToUniversalTime()).Select(h => h.TrackId).ToList();
        Assert.Equal(tones.Length, all.Distinct().Count()); // prvo su odsvirane sve koje jos nisu isle
    }

    [Fact]
    public void Skip_StartsNextTrackAlmostImmediately()
    {
        AddTones(5, 8);
        var (director, clock) = _env.Director(Monday.AddHours(10));
        var session = TestEnvironment.Session(director, clock);

        TestEnvironment.Render(session, 2);
        session.Skip();
        TestEnvironment.Render(session, 3);

        Assert.InRange(session.Sequencer.Events[1].StartFrame / (double)Rate, 2.0, 2.1);
    }

    [Fact]
    public void ProbeHelpers_OpenAllDay_AndMarkTracksForTrial()
    {
        AddTones(3, 2, verify: false);
        var open = EngineHost.AlwaysOpenSchedule(DefaultSchedule.Create());
        Assert.True(ScheduleEngine.Evaluate(open, null, Monday.AddHours(3)).ShouldPlay);
        Assert.Equal("Ponedeljak - vece", ScheduleEngine.Evaluate(open, null, Monday.AddHours(23).AddMinutes(45)).Program!.Name);

        Assert.Equal(3, ProbeLicensing.MarkAllVerified(_env.Db));
        Assert.Equal(0, ProbeLicensing.MarkAllVerified(_env.Db));
        var (director, _) = _env.Director(Monday.AddHours(10));
        Assert.Equal(3, director.BroadcastableCount);
        Assert.All(_env.Db.Licenses.GetAll().Values, l => Assert.Equal(ProbeLicensing.Marker, l.LicenseType));
    }
}
