using System.Text.RegularExpressions;
using LustreRadio.Ipc;
using LustreRadio.Presentation;
using Xunit;

namespace LustreRadio.Presentation.Tests;

public sealed class TextsTests
{
    public static IEnumerable<object[]> AllKeys => Enum.GetValues<T>().Select(k => new object[] { k });

    [Theory] // T-L10N-01
    [MemberData(nameof(AllKeys))]
    public void EveryText_ExistsInBothLanguages_WithSamePlaceholders(T key)
    {
        Assert.Contains(key, Texts.Keys);
        var sr = Texts.Get("sr", key);
        var en = Texts.Get("en", key);
        Assert.False(string.IsNullOrWhiteSpace(sr), $"{key}: nema srpskog teksta");
        Assert.False(string.IsNullOrWhiteSpace(en), $"{key}: nema engleskog teksta");

        static string[] Holes(string text) => Regex.Matches(text, @"\{\d\}").Select(m => m.Value).Order().ToArray();
        Assert.Equal(Holes(sr), Holes(en));

        // srpski je latinica
        Assert.DoesNotContain(sr, c => c is >= 'Ѐ' and <= 'ӿ');
    }

    [Fact]
    public void UnknownLanguage_FallsBackToSerbian()
    {
        Assert.Equal(Texts.Get("sr", T.Next), Texts.Get(null, T.Next));
        Assert.Equal(Texts.Get("sr", T.Next), Texts.Get("de", T.Next));
        Assert.NotEqual(Texts.Get("sr", T.Next), Texts.Get("en", T.Next));
    }

    [Fact]
    public void Dates_AreWrittenOutInBothLanguages()
    {
        var saturday = new DateTime(2026, 11, 14, 12, 0, 0);
        Assert.Equal("Subota, 14. novembar 2026.", Texts.LongDate("sr", saturday));
        Assert.Equal("Saturday, 14 November 2026", Texts.LongDate("en", saturday));
        Assert.Equal("u subotu", Texts.DayForSentence("sr", saturday));
        Assert.Equal("on Sunday", Texts.DayForSentence("en", saturday.AddDays(1)));
        Assert.Equal("07:00", Texts.Time(new DateTime(2026, 11, 15, 7, 0, 0)));
    }
}

public sealed class StaffViewTests
{
    private static readonly DateTime NowLocal = new(2026, 11, 14, 20, 18, 0);
    private static readonly DateTime NowUtc = new(2026, 11, 14, 19, 18, 0, DateTimeKind.Utc);

    private static RadioState Playing() => new()
    {
        OnAir = true,
        TrackId = "7",
        Title = "Night Drive",
        Artist = "Lustre Band",
        PositionSeconds = 60,
        DurationSeconds = 240,
        Program = "Deep House",
        DayPart = "Evening",
        Volume = 0.6,
        VolumeMin = 0.2,
        VolumeMax = 0.9,
        Output = "Playing",
        Device = "Zvucnik",
        BroadcastableTracks = 120,
        AsOfUtc = NowUtc,
        NowLocal = NowLocal,
        ClosesAtLocal = new DateTime(2026, 11, 14, 23, 30, 0),
    };

    [Fact]
    public void Playing_ShowsTrackProgramAndClosingTime()
    {
        var view = StaffView.From(Playing(), LinkStatus.Connected, NowLocal, NowUtc);

        Assert.Equal("Night Drive", view.Title);
        Assert.Equal("Lustre Band", view.Artist);
        Assert.Equal("Veče · Deep House", view.ProgramLine);
        Assert.Equal("Gašenje u 23:30 (za 3 h 12 min)", view.ClosesLine);
        Assert.Equal("Svira", view.StatusLine);
        Assert.Equal(Health.Good, view.Health);
        Assert.Equal(0.25, view.Progress, 3);
        Assert.Equal("1:00", view.PositionText);
        Assert.Equal("4:00", view.DurationText);
        Assert.True(view.Live && view.CanPlayPause && view.CanNext);
        Assert.False(view.ShowPlay);
        Assert.Equal("Subota, 14. novembar 2026.", view.DateLine);
    }

    [Fact]
    public void English_IsUsedWhenStaffLanguageIsEnglish()
    {
        var view = StaffView.From(Playing() with { StaffLanguage = "en" }, LinkStatus.Connected, NowLocal, NowUtc);

        Assert.Equal("Evening · Deep House", view.ProgramLine);
        Assert.Equal("Closes at 23:30 (in 3 h 12 min)", view.ClosesLine);
        Assert.Equal("On air", view.StatusLine);
        Assert.Equal("Saturday, 14 November 2026", view.DateLine);
    }

    [Fact]
    public void Position_MovesBetweenMessages_ButNeverPastTheEnd_AndNotWhilePaused()
    {
        var state = Playing();
        Assert.Equal("1:04", StaffView.From(state, LinkStatus.Connected, NowLocal, NowUtc.AddSeconds(4)).PositionText);

        // poruke kasne minut: traka ide najvise 10 s unapred
        Assert.Equal("1:10", StaffView.From(state, LinkStatus.Connected, NowLocal, NowUtc.AddMinutes(1)).PositionText);

        var nearEnd = state with { PositionSeconds = 238 };
        var atEnd = StaffView.From(nearEnd, LinkStatus.Connected, NowLocal, NowUtc.AddSeconds(8));
        Assert.Equal("4:00", atEnd.PositionText);
        Assert.Equal(1.0, atEnd.Progress, 6);

        var paused = StaffView.From(state with { Paused = true }, LinkStatus.Connected, NowLocal, NowUtc.AddSeconds(4));
        Assert.Equal("1:00", paused.PositionText);
        Assert.True(paused.ShowPlay);
        Assert.True(paused.CanPlayPause);
        Assert.False(paused.CanNext);
        Assert.Equal(Health.Attention, paused.Health);
    }

    [Fact]
    public void Disconnected_KeepsLastTrackGreyedOut_AndDisablesControls()
    {
        var view = StaffView.From(Playing(), LinkStatus.Connecting, NowLocal, NowUtc.AddSeconds(30));

        Assert.Equal("Night Drive", view.Title);
        Assert.False(view.Live);
        Assert.False(view.CanPlayPause);
        Assert.False(view.CanNext);
        Assert.Equal("Povezivanje…", view.StatusLine);
        Assert.Equal("1:00", view.PositionText); // traka stoji dok nema veze
        Assert.Equal(string.Empty, view.ClosesLine); // bez veze se ne tvrdi nista o vremenu gasenja
    }

    [Fact]
    public void NeverConnected_ShowsConnecting_WithoutCrashing()
    {
        var view = StaffView.From(null, LinkStatus.Connecting, NowLocal, NowUtc);

        Assert.False(view.Live);
        Assert.Equal("Trenutno ništa ne svira", view.Title);
        Assert.Equal(string.Empty, view.Artist);
        Assert.Equal(0, view.Progress);
    }

    [Fact]
    public void Closed_SaysWhenMusicStartsAgain()
    {
        var closed = new RadioState { OnAir = false, OpensAtLocal = new DateTime(2026, 11, 15, 7, 0, 0), BroadcastableTracks = 10, NowLocal = NowLocal };
        var view = StaffView.From(closed, LinkStatus.Connected, NowLocal, NowUtc);

        Assert.Equal("Van radnog vremena – muzika kreće u nedelju u 07:00", view.StatusLine);
        Assert.Equal(Health.Good, view.Health);
        Assert.False(view.CanPlayPause);
        Assert.Equal(string.Empty, view.ClosesLine);
    }

    [Fact]
    public void Problems_AreNamedPlainly()
    {
        var noTracks = new RadioState { OnAir = false, BroadcastableTracks = 0, NowLocal = NowLocal };
        Assert.Equal(Health.Problem, StaffView.From(noTracks, LinkStatus.Connected, NowLocal, NowUtc).Health);
        Assert.Equal("Nema pesama koje smeju da se puštaju", StaffView.From(noTracks, LinkStatus.Connected, NowLocal, NowUtc).StatusLine);

        var noSpeaker = StaffView.From(Playing() with { Output = "Disconnected" }, LinkStatus.Connected, NowLocal, NowUtc.AddSeconds(5));
        Assert.Equal(Health.Problem, noSpeaker.Health);
        Assert.Equal("1:00", noSpeaker.PositionText);

        Assert.Equal(Health.Problem, StaffView.From(null, LinkStatus.VersionMismatch, NowLocal, NowUtc).Health);
    }

    [Fact]
    public void MissingTags_GetReadableFallbacks_AndLongTracksShowHours()
    {
        var view = StaffView.From(Playing() with { Title = " ", Artist = null, DurationSeconds = 4000, PositionSeconds = 3725 }, LinkStatus.Connected, NowLocal, NowUtc);

        Assert.Equal("Trenutno ništa ne svira", view.Title);
        Assert.Equal("Nepoznat izvođač", view.Artist);
        Assert.Equal("1:02:05", view.PositionText);
    }

    [Fact]
    public void LastMinutes_AreCountedInMinutes()
    {
        var state = Playing() with { NowLocal = new DateTime(2026, 11, 14, 23, 15, 30) };
        Assert.Equal("Gašenje u 23:30 (za 15 min)", StaffView.From(state, LinkStatus.Connected, NowLocal, NowUtc).ClosesLine);
    }
}

public sealed class PinEntryTests
{
    [Fact]
    public void AcceptsOnlyDigits_FourToEight()
    {
        var pin = new PinEntry();
        Assert.False(pin.Add('a'));
        foreach (var c in "123")
        {
            Assert.True(pin.Add(c));
        }

        Assert.False(pin.CanSubmit);
        Assert.True(pin.Add('4'));
        Assert.True(pin.CanSubmit);
        Assert.Equal("●●●●", pin.Masked);

        foreach (var c in "5678")
        {
            Assert.True(pin.Add(c));
        }

        Assert.False(pin.Add('9'));
        Assert.Equal("12345678", pin.Digits);

        pin.Backspace();
        Assert.Equal("1234567", pin.Digits);
        pin.Clear();
        Assert.Equal(string.Empty, pin.Digits);
        pin.Backspace();
        Assert.Equal(string.Empty, pin.Masked);
    }

    [Fact]
    public void NewPin_MustBeRepeatedExactly()
    {
        var flow = new NewPinFlow();
        Assert.Equal(NewPinStep.First, flow.Submit("12"));
        Assert.Equal(NewPinStep.Repeat, flow.Submit("4821"));
        Assert.Equal(NewPinStep.Mismatch, flow.Submit("4822"));
        Assert.Null(flow.Pin);
        Assert.Equal(NewPinStep.First, flow.Step);

        Assert.Equal(NewPinStep.Repeat, flow.Submit("4821"));
        Assert.Equal(NewPinStep.Done, flow.Submit("4821"));
        Assert.Equal("4821", flow.Pin);
    }
}

public sealed class SystemCheckTests
{
    private static readonly RadioState Healthy = new()
    {
        OnAir = true,
        Output = "Playing",
        Device = "Zvucnik",
        BroadcastableTracks = 50,
        TotalTracks = 60,
        PinIsSet = true,
        EngineVersion = "0.1.0",
        ClosesAtLocal = new DateTime(2026, 11, 14, 23, 30, 0),
    };

    [Fact]
    public void HealthySystem_HasNoWarnings()
    {
        var lines = SystemCheck.Evaluate(Healthy, LinkStatus.Connected, new LocalProbes { AutostartProblems = [], FreeDiskGb = 80 }, "sr");

        Assert.All(lines, l => Assert.Equal(Health.Good, l.Health));
        Assert.Equal(9, lines.Count); // faza B, B1: dodat red "Baza" (ranije 8 redova)
        Assert.Contains(lines, l => l.Detail == "Pesama u biblioteci: 60; sme da se pušta: 50");
        Assert.Contains(lines, l => l.Detail == "Otvoreno do 23:30");
    }

    [Fact]
    public void EachProblem_IsReportedOnItsOwnLine()
    {
        var broken = Healthy with { Output = "Disconnected", BroadcastableTracks = 0, PinIsSet = false, SafeMode = true };
        var lines = SystemCheck.Evaluate(broken, LinkStatus.Connected, new LocalProbes { AutostartProblems = ["Priority"], FreeDiskGb = 1.5 }, "en");

        Assert.Equal(Health.Problem, lines.Single(l => l.Name == "Speaker").Health);
        Assert.Equal(Health.Problem, lines.Single(l => l.Name == "Music").Health);
        Assert.Equal(Health.Problem, lines.Single(l => l.Name == "Settings PIN").Health);
        Assert.Equal(Health.Attention, lines.Single(l => l.Name == "Running mode").Health);
        Assert.Equal("Setting is wrong: Priority", lines.Single(l => l.Name == "Automatic start").Detail);
        Assert.Equal("Low space: 1.5 GB", lines.Single(l => l.Name == "Free disk space").Detail);
    }

    [Fact] // faza B, B1: rad bez nadogradnje se vidi kao problem, sa razlogom
    public void DatabaseNotUpgraded_IsShownAsProblem_WithReason()
    {
        var state = Healthy with { DatabaseMode = RadioState.DatabaseNotUpgraded, DatabaseModeReason = "kopija nije uspela" };
        var lines = SystemCheck.Evaluate(state, LinkStatus.Connected, new LocalProbes { AutostartProblems = [], FreeDiskGb = 80 }, "sr");
        var line = lines.Single(l => l.Name == "Baza");
        Assert.Equal(Health.Problem, line.Health);
        Assert.Contains("Baza nije nadograđena", line.Detail, StringComparison.Ordinal);
        Assert.Contains("kopija nije uspela", line.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutEngine_OnlyLocalChecksAreShown_AndNothingIsInvented()
    {
        var lines = SystemCheck.Evaluate(Healthy, LinkStatus.Connecting, new LocalProbes(), "sr");

        Assert.Equal(2, lines.Count);
        Assert.Equal(Health.Problem, lines[0].Health);
        Assert.Equal("Ne odgovara", lines[0].Detail);
        Assert.Equal(Health.Attention, lines[1].Health); // automatsko pokretanje nije podeseno
    }
}

public sealed class RadioLinkTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void RetryDelay_StartsAtOneSecond_AndStopsAtFive()
    {
        Assert.Equal([1, 1, 2, 3, 4, 5, 5, 5], new[] { 0, 1, 2, 3, 4, 5, 6, 60 }.Select(n => (int)RadioLink.RetryDelay(n).TotalSeconds));
    }

    [Fact]
    public async Task Link_Connects_Reconnects_AndGetsFreshStateEachTime()
    {
        var pipe = "LustreRadio.Test." + Guid.NewGuid().ToString("N");
        var handler = new Handler { Title = "prva" };
        await using var link = new RadioLink(pipe);
        link.Start();

        // audio proces jos ne radi: veza ceka, ne baca gresku
        await Task.Delay(300);
        Assert.Equal(LinkStatus.Connecting, link.Status);
        Assert.Null(await link.SendAsync("pause"));

        var server = new IpcServer(pipe, handler);
        server.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => link.Status == LinkStatus.Connected && link.State?.Title == "prva", Limit));
            Assert.True((await link.SendAsync("pause"))!.Ok);

            link.AdminSession = "sesija";
            server.Dispose();
            Assert.True(SpinWait.SpinUntil(() => link.Status == LinkStatus.Connecting, Limit));
            Assert.Equal("prva", link.State?.Title); // poslednji prikaz ostaje
            Assert.Null(link.AdminSession);          // posle prekida admin se ponovo prijavljuje
            Assert.Null(await link.SendAsync("pause"));

            handler.Title = "druga";
            server = new IpcServer(pipe, handler);
            server.Start();
            Assert.True(SpinWait.SpinUntil(() => link.Status == LinkStatus.Connected && link.State?.Title == "druga", Limit));
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ExpiredAdminSession_IsForgotten()
    {
        var pipe = "LustreRadio.Test." + Guid.NewGuid().ToString("N");
        using var server = new IpcServer(pipe, new Handler());
        server.Start();
        await using var link = new RadioLink(pipe);
        link.Start();
        Assert.True(SpinWait.SpinUntil(() => link.Status == LinkStatus.Connected, Limit));

        link.AdminSession = "istekla";
        var response = await link.SendAsync("admin-thing", admin: true);

        Assert.Equal("admin-required", response!.Error);
        Assert.Null(link.AdminSession);
    }

    private sealed class Handler : IIpcHandler
    {
        public string Title { get; set; } = "pesma";

        public RadioState CurrentState() => new() { Title = Title };

        public IpcMessage Handle(IpcMessage command) => command.Name == "admin-thing"
            ? IpcMessage.Failure(command.Id, "admin-required")
            : IpcMessage.Success(command.Id);
    }
}
