using LustreRadio.Core.Rotation;
using LustreRadio.Core.Scheduling;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>Testovi T-ROT-01 do T-ROT-04 (spec. deo 19), simulacijom sa zadatim vremenom.</summary>
public class RotationEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 7, 0, 0, TimeSpan.FromHours(1));
    private static readonly SlotProgram Lounge = new() { Name = "test", Genres = ["lounge"], BpmMin = 60, BpmMax = 130 };

    private static List<TrackInfo> Library(int tracks, int artists, string genre = "lounge", string prefix = "t") =>
        Enumerable.Range(0, tracks)
            .Select(i => new TrackInfo { Id = $"{prefix}{i}", Artist = $"{prefix}-izvodjac{i % artists}", Genres = [genre], Broadcastable = true })
            .ToList();

    private static List<(PickResult Pick, DateTimeOffset At)> Simulate(
        IReadOnlyList<TrackInfo> library, SlotProgram program, RotationSettings settings, int days, int picksPerDay, int seed = 1)
    {
        var engine = new RotationEngine(new Random(seed));
        var state = new RotationState();
        var result = new List<(PickResult, DateTimeOffset)>();
        for (var day = 0; day < days; day++)
        {
            for (var i = 0; i < picksPerDay; i++)
            {
                var now = Start.AddDays(day).AddMinutes(4 * i);
                var pick = engine.Pick(library, state, program, now, settings);
                Assert.NotNull(pick);
                state.Record(pick.Track, now);
                result.Add((pick, now));
            }
        }

        return result;
    }

    [Fact] // T-ROT-01
    public void LargeLibrary_ThirtyDays_NoRepeatWithinSevenDays()
    {
        var settings = new RotationSettings();
        var plays = Simulate(Library(2000, 400), Lounge, settings, days: 30, picksPerDay: 250);

        var last = new Dictionary<string, DateTimeOffset>();
        foreach (var (pick, at) in plays)
        {
            if (last.TryGetValue(pick.Track.Id, out var previous))
            {
                Assert.True(at - previous >= settings.TrackGap, $"{pick.Track.Id} ponovljena posle {at - previous}.");
            }

            last[pick.Track.Id] = at;
            Assert.False(pick.TrackGapRelaxed);
            Assert.Equal(MatchTier.Program, pick.Tier);
        }

        Assert.Equal(7500, plays.Count);
    }

    [Fact] // T-ROT-02
    public void SmallLibrary_KeepsPlaying_SpreadsRepeats_NeverTwiceInARow()
    {
        var plays = Simulate(Library(30, 10), Lounge, new RotationSettings(), days: 4, picksPerDay: 250);

        var lastIndex = new Dictionary<string, int>();
        for (var i = 0; i < plays.Count; i++)
        {
            var id = plays[i].Pick.Track.Id;
            if (i > 0)
            {
                Assert.NotEqual(plays[i - 1].Pick.Track.Id, id);
            }

            if (lastIndex.TryGetValue(id, out var previous))
            {
                // 30 pesama: ista pesma se vraca tek posle bar 15 drugih (bira se medju najduze nepustanima)
                Assert.True(i - previous >= 15, $"{id} ponovljena posle samo {i - previous} pesama.");
            }

            lastIndex[id] = i;
        }

        Assert.Equal(30, lastIndex.Count);
        Assert.Contains(plays, p => p.Pick.TrackGapRelaxed);
    }

    [Fact] // T-ROT-03
    public void ArtistGap_IsRespected_WhenLibraryAllows()
    {
        var settings = new RotationSettings { TrackGap = TimeSpan.FromHours(3), ArtistGap = TimeSpan.FromHours(2) };
        var plays = Simulate(Library(80, 40), Lounge, settings, days: 2, picksPerDay: 250);

        var lastArtist = new Dictionary<string, DateTimeOffset>();
        foreach (var (pick, at) in plays)
        {
            Assert.False(pick.ArtistGapRelaxed);
            if (lastArtist.TryGetValue(pick.Track.Artist, out var previous))
            {
                Assert.True(at - previous >= settings.ArtistGap, $"{pick.Track.Artist} posle {at - previous}.");
            }

            lastArtist[pick.Track.Artist] = at;
        }
    }

    [Fact]
    public void TooFewTracksInGenre_UsesCompatibleGenres_BeforeRepeating()
    {
        var library = Library(2, 2, "bossa nova", "b");
        library.AddRange(Library(50, 25, "jazz", "j"));
        library.AddRange(Library(50, 25, "deep house", "h"));
        var program = new SlotProgram { Name = "brunch", Genres = ["bossa nova"] };

        var plays = Simulate(library, program, new RotationSettings(), days: 1, picksPerDay: 40);

        Assert.Equal(2, plays.Count(p => p.Pick.Tier == MatchTier.Program));
        Assert.Equal(38, plays.Count(p => p.Pick.Tier == MatchTier.Compatible));
        Assert.All(plays, p => Assert.False(p.Pick.TrackGapRelaxed));
        Assert.All(plays, p => Assert.DoesNotContain("deep house", p.Pick.Track.Genres));
        Assert.Equal(40, plays.Select(p => p.Pick.Track.Id).Distinct().Count());
    }

    [Fact] // deo T-LIC-01: rotacija nikad ne bira nedozvoljenu ili blokiranu pesmu
    public void NotBroadcastableOrBlockedTracks_AreNeverPicked()
    {
        var library = Library(10, 5);
        library.AddRange(Library(10, 5, prefix: "n").Select(t => t with { Broadcastable = false }));
        library.AddRange(Library(5, 5, prefix: "x").Select(t => t with { Blocked = true }));

        var plays = Simulate(library, Lounge, new RotationSettings(), days: 2, picksPerDay: 250);

        Assert.All(plays, p => Assert.StartsWith("t", p.Pick.Track.Id));
    }

    [Fact]
    public void EmptyLibrary_ReturnsNull_SingleTrack_IsRepeated()
    {
        var engine = new RotationEngine(new Random(1));
        Assert.Null(engine.Pick([], new RotationState(), Lounge, Start, new RotationSettings()));
        Assert.Null(engine.Pick(
            Library(3, 3).Select(t => t with { Broadcastable = false }).ToList(), new RotationState(), Lounge, Start, new RotationSettings()));

        var single = Library(1, 1);
        var state = new RotationState();
        for (var i = 0; i < 5; i++)
        {
            var pick = engine.Pick(single, state, Lounge, Start.AddMinutes(4 * i), new RotationSettings());
            Assert.Equal("t0", pick!.Track.Id);
            state.Record(pick.Track, Start.AddMinutes(4 * i));
        }
    }

    [Fact]
    public void BpmOutsideRange_IsOnlyCompatible_UnknownBpmIsAccepted()
    {
        var program = new SlotProgram { Name = "p", Genres = ["deep house"], BpmMin = 110, BpmMax = 124 };
        TrackInfo Track(double? bpm) => new() { Id = "x", Artist = "a", Genres = ["Deep House"], Bpm = bpm, Broadcastable = true };

        Assert.Equal(MatchTier.Program, GenreCompatibility.Classify(Track(120), program));
        Assert.Equal(MatchTier.Program, GenreCompatibility.Classify(Track(null), program));
        Assert.Equal(MatchTier.Compatible, GenreCompatibility.Classify(Track(128), program));
        Assert.Equal(MatchTier.Compatible, GenreCompatibility.Classify(Track(120) with { Genres = ["melodic house"] }, program));
        Assert.Equal(MatchTier.Any, GenreCompatibility.Classify(Track(120) with { Genres = ["smooth jazz"] }, program));
    }

    [Fact]
    public void AvoidsPairThatAlreadyPlayedInSameOrder_WhenThereIsAChoice()
    {
        var library = Library(3, 3);
        var settings = new RotationSettings { TrackGap = TimeSpan.Zero, ArtistGap = TimeSpan.Zero };
        for (var seed = 0; seed < 50; seed++)
        {
            var state = new RotationState();
            state.Record(library[0], Start);
            state.Record(library[1], Start.AddMinutes(4)); // t1 je vec isla posle t0
            state.Record(library[0], Start.AddMinutes(8));

            var pick = new RotationEngine(new Random(seed)).Pick(library, state, Lounge, Start.AddMinutes(12), settings);
            Assert.Equal("t2", pick!.Track.Id);
        }
    }

    [Fact] // T-ROT-04
    public void LibraryHealth_CountsMissingTracksPerSlot()
    {
        var health = LibraryHealth.Compute(DefaultSchedule.Create(), Library(100, 50), new RotationSettings());

        Assert.Equal(21, health.Count);

        var mondayAfternoon = health.Single(h => h.Day == DayOfWeek.Monday && h.Part == DayPart.Afternoon);
        Assert.Equal(100, mondayAfternoon.MatchingTracks);
        Assert.Equal(90, mondayAfternoon.PlaysPerOccurrence); // 6 h, pesme od 4 min
        Assert.Equal(0, mondayAfternoon.Missing);

        var mondayMorning = health.Single(h => h.Day == DayOfWeek.Monday && h.Part == DayPart.Morning);
        Assert.Equal(0, mondayMorning.MatchingTracks);
        Assert.Equal(100, mondayMorning.CompatibleTracks);
        Assert.Equal(60, mondayMorning.NeededForTargetGap);
        Assert.Equal(60, mondayMorning.Missing);

        var fridayEvening = health.Single(h => h.Day == DayOfWeek.Friday && h.Part == DayPart.Evening);
        Assert.Equal(98, fridayEvening.PlaysPerOccurrence); // 6,5 h
        Assert.Equal(0, fridayEvening.MatchingTracks + fridayEvening.CompatibleTracks);
    }
}
