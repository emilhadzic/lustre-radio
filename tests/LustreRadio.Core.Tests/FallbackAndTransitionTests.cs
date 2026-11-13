using LustreRadio.Core.Fallback;
using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Transitions;
using Xunit;

namespace LustreRadio.Core.Tests;

public class FallbackAndTransitionTests
{
    private static readonly DjMixSettings Dj = new();

    private static TrackAnalysisSummary Track(double? bpm = 120, bool edges = true, bool grid = true, bool stable = true, double confidence = 0.95) =>
        new() { Bpm = bpm, HasEdges = edges, HasBeatGrid = grid, TempoStable = stable, BeatConfidence = confidence };

    [Theory] // odluka D6: tri slucaja po razlici tempa
    [InlineData(120.0, 120.0, true, TransitionKind.DjMixLive)]
    [InlineData(120.0, 120.3, true, TransitionKind.DjMixLive)]    // 0,25 %
    [InlineData(120.0, 120.5, true, TransitionKind.DjMixPrepared)] // 0,42 %
    [InlineData(120.0, 122.0, true, TransitionKind.DjMixPrepared)] // 1,67 %
    [InlineData(120.0, 122.0, false, TransitionKind.Smart)]        // pripremljen prelaz ne postoji
    [InlineData(120.0, 123.0, true, TransitionKind.Smart)]         // 2,5 % > 2 %
    [InlineData(120.0, 126.0, true, TransitionKind.Smart)]
    public void DjMix_FollowsTempoDifference(double bpmA, double bpmB, bool prepared, TransitionKind expected)
    {
        Assert.Equal(expected, TransitionSelector.Select(TransitionMode.DjMix, Track(bpmA), Track(bpmB), Dj, prepared).Kind);
    }

    [Fact]
    public void PreparedTolerance_CannotExceedThreePercent()
    {
        var loose = new DjMixSettings { PreparedTolerance = 0.10 };
        Assert.Equal(TransitionKind.DjMixPrepared, TransitionSelector.Select(TransitionMode.DjMix, Track(120), Track(123.5), loose, true).Kind);
        Assert.Equal(TransitionKind.Smart, TransitionSelector.Select(TransitionMode.DjMix, Track(120), Track(124), loose, true).Kind);
    }

    [Fact] // T-TRN-05: svaka nesigurnost -> Smart
    public void DjMix_FallsBackToSmart_WhenRhythmIsUncertain()
    {
        var ok = Track();
        TrackAnalysisSummary[] uncertain =
        [
            Track(bpm: null), Track(grid: false), Track(stable: false), Track(confidence: 0.5),
        ];

        foreach (var track in uncertain)
        {
            Assert.Equal(TransitionKind.Smart, TransitionSelector.Select(TransitionMode.DjMix, ok, track, Dj, true).Kind);
            Assert.Equal(TransitionKind.Smart, TransitionSelector.Select(TransitionMode.DjMix, track, ok, Dj, true).Kind);
        }
    }

    [Fact] // T-TRN-06: nedostaju podaci -> nizi rezim, nikad "nista"
    public void MissingEdgeData_FallsBackToStandard()
    {
        foreach (var mode in new[] { TransitionMode.Smart, TransitionMode.DjMix })
        {
            Assert.Equal(TransitionKind.Standard, TransitionSelector.Select(mode, Track(edges: false), Track(), Dj, true).Kind);
            Assert.Equal(TransitionKind.Standard, TransitionSelector.Select(mode, Track(), null, Dj, true).Kind);
            Assert.Equal(TransitionKind.Standard, TransitionSelector.Select(mode, null, null, Dj, true).Kind);
        }
    }

    [Fact]
    public void RequestedModes_AreHonoredWhenDataExists()
    {
        Assert.Equal(TransitionKind.Standard, TransitionSelector.Select(TransitionMode.Standard, Track(), Track(), Dj, true).Kind);
        Assert.Equal(TransitionKind.Smart, TransitionSelector.Select(TransitionMode.Smart, Track(), Track(), Dj, true).Kind);
        Assert.Equal(TransitionKind.Smart, TransitionSelector.Select(TransitionMode.Smart, Track(bpm: null, grid: false), Track(), Dj, true).Kind);
    }

    [Theory] // spec. deo 10
    [InlineData(true, true, true, true, PlaybackSource.Rotation)]
    [InlineData(true, false, true, true, PlaybackSource.SimpleFromMix)]
    [InlineData(false, true, true, true, PlaybackSource.CachedList)]
    [InlineData(false, false, true, true, PlaybackSource.CachedList)]
    [InlineData(false, false, false, true, PlaybackSource.EmergencyFolder)]
    [InlineData(false, false, false, false, PlaybackSource.Nothing)]
    public void PlaybackSource_DegradesStepByStep(bool db, bool rotation, bool cached, bool folder, PlaybackSource expected)
    {
        var health = new SourceHealth { DatabaseOk = db, RotationOk = rotation, CachedListAvailable = cached, EmergencyFolderAvailable = folder };
        Assert.Equal(expected, PlaybackSourceSelector.Choose(health));
    }

    [Fact]
    public void TrackLeavesRotation_AfterThreeFaults_AndSuccessResetsCounter()
    {
        var tracker = new TrackFaultTracker();
        Assert.False(tracker.RecordFault("a"));
        Assert.False(tracker.RecordFault("a"));
        Assert.False(tracker.IsQuarantined("a"));
        Assert.True(tracker.RecordFault("a"));
        Assert.True(tracker.IsQuarantined("a"));
        Assert.False(tracker.IsQuarantined("b"));

        tracker.RecordFault("b");
        tracker.RecordFault("b");
        tracker.RecordSuccess("b");
        Assert.False(tracker.RecordFault("b"));
        Assert.Equal(1, tracker.FaultsOf("b"));

        tracker.Release("a");
        Assert.False(tracker.IsQuarantined("a"));
    }

    [Fact]
    public void ThreeCrashesInTenMinutes_TriggerSafeMode_SlowCrashesDoNot()
    {
        var fast = new CrashLoopDetector();
        Assert.False(fast.RecordCrash(TimeSpan.FromMinutes(1)));
        Assert.False(fast.RecordCrash(TimeSpan.FromMinutes(5)));
        Assert.True(fast.RecordCrash(TimeSpan.FromMinutes(9)));

        var slow = new CrashLoopDetector();
        Assert.False(slow.RecordCrash(TimeSpan.FromMinutes(0)));
        Assert.False(slow.RecordCrash(TimeSpan.FromMinutes(8)));
        Assert.False(slow.RecordCrash(TimeSpan.FromMinutes(16)));
        Assert.False(slow.RecordCrash(TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void FallbackEntry_MatchesOnlyUnchangedFile()
    {
        var written = new DateTime(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);
        var entry = new FallbackEntry { Path = @"D:\Muzika\a.mp3", ContentHash = "ab", SizeBytes = 1000, LastWriteUtc = written };

        Assert.True(entry.MatchesFile(1000, written));
        Assert.False(entry.MatchesFile(1001, written));
        Assert.False(entry.MatchesFile(1000, written.AddSeconds(1)));
    }
}
