using LustreRadio.Core.Security;
using LustreRadio.Core.Time;
using LustreRadio.Data;
using LustreRadio.Ipc;
using LustreRadio.Playout;
using Xunit;

namespace LustreRadio.Playout.Tests;

/// <summary>T-IPC-05 i pravila komandi: audio proces ne veruje interfejsu.</summary>
public sealed class RadioCommandTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "lustre-cmd-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly LustreDatabase _db;
    private readonly FakeRadio _radio = new();
    private readonly FakeMonotonic _clock = new();
    private readonly RadioCommandHandler _handler;

    public RadioCommandTests()
    {
        _db = LustreDatabase.Open(_path);
        _handler = new RadioCommandHandler(_radio, _db, new AdminSessions(_clock, () => _db.Settings.Get(RadioCommandHandler.PinKey)));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // privremeni fajl
            }
        }
    }

    private IpcMessage Send(string name, Dictionary<string, string>? args = null, string? session = null) =>
        _handler.Handle(new IpcMessage { Type = "cmd", Id = Guid.NewGuid().ToString("N"), Name = name, Args = args, Session = session });

    private string LoginAsAdmin()
    {
        Assert.True(Send("set-pin", new() { ["pin"] = "4821" }).Ok);
        return Send("login", new() { ["pin"] = "4821" }).Data!["session"];
    }

    [Fact]
    public void Language_IsSetPerScreen_AndOnlyKnownValuesAreAccepted()
    {
        var session = LoginAsAdmin();
        Assert.Equal("sr", RadioCommandHandler.Language(_db, RadioCommandHandler.StaffLanguageKey));

        Assert.True(Send("set-language", new() { ["screen"] = "staff", ["lang"] = "en" }, session).Ok);
        Assert.Equal("en", RadioCommandHandler.Language(_db, RadioCommandHandler.StaffLanguageKey));
        Assert.Equal("sr", RadioCommandHandler.Language(_db, RadioCommandHandler.AdminLanguageKey));

        Assert.Equal("bad-argument", Send("set-language", new() { ["screen"] = "staff", ["lang"] = "de" }, session).Error);
        Assert.Equal("bad-argument", Send("set-language", new() { ["screen"] = "kasa", ["lang"] = "en" }, session).Error);
        Assert.Equal("en", RadioCommandHandler.Language(_db, RadioCommandHandler.StaffLanguageKey));
    }

    [Fact]
    public void CollectiveLicense_IsRecordedOnlyForAdmin_WithWhoAndWhen()
    {
        var args = new Dictionary<string, string> { ["organizations"] = "SOKOJ, OFPS" };
        Assert.Equal("admin-required", Send("declare-collective-license", args).Error);
        Assert.Null(_db.Settings.GetJson<LustreRadio.Core.Licensing.CollectiveLicense>(LibraryIndexer.CollectiveLicenseKey));

        var session = LoginAsAdmin();
        _radio.Calls.Clear();
        Assert.Equal("bad-argument", Send("declare-collective-license", new() { ["organizations"] = " " }, session).Error);
        Assert.True(Send("declare-collective-license", args, session).Ok);

        var saved = _db.Settings.GetJson<LustreRadio.Core.Licensing.CollectiveLicense>(LibraryIndexer.CollectiveLicenseKey)!;
        Assert.Equal("SOKOJ, OFPS", saved.Organizations);
        Assert.Equal("admin (PIN)", saved.DeclaredBy);
        Assert.True((DateTimeOffset.Now - saved.DeclaredAt).Duration() < TimeSpan.FromMinutes(1));
        Assert.Equal(["rescan"], _radio.Calls); // pesme se odmah ponovo proveravaju
    }

    [Fact]
    public void MusicFolders_MustExist_OtherwiseNothingChanges()
    {
        var session = LoginAsAdmin();
        _radio.Calls.Clear();
        var real = Path.GetTempPath();
        var missing = Path.Combine(real, "nema-" + Guid.NewGuid().ToString("N"));

        var refused = Send("set-music-folders", new() { ["folder0"] = real, ["folder1"] = missing }, session);
        Assert.Equal("folder-not-found", refused.Error);
        Assert.Equal(missing, refused.Data!["folder"]);
        Assert.Equal("folder-not-found", Send("set-music-folders", new() { ["folder0"] = "muzika" }, session).Error);
        Assert.Empty(_radio.Calls);

        Assert.True(Send("set-music-folders", new() { ["folder0"] = real }, session).Ok);
        Assert.Equal(["folders:" + real], _radio.Calls);
    }

    [Fact]
    public void StaffCommands_WorkWithoutPin()
    {
        Assert.True(Send("pause").Ok);
        Assert.True(Send("play").Ok);
        Assert.True(Send("next").Ok);
        Assert.True(Send("get-state").Ok);
        Assert.Equal(["pause", "resume", "next:"], _radio.Calls);
    }

    [Theory] // T-IPC-05
    [InlineData("rescan")]
    [InlineData("set-music-folders")]
    [InlineData("set-volume-range")]
    [InlineData("set-language")]
    [InlineData("touch")]
    public void AdminCommands_AreRefusedWithoutValidSession(string command)
    {
        var args = new Dictionary<string, string> { ["folder0"] = @"D:\Muzika", ["min"] = "0", ["max"] = "1" };

        Assert.Equal("admin-required", Send(command, args).Error);
        Assert.Equal("admin-required", Send(command, args, session: "izmisljena-sesija").Error);
        Assert.Empty(_radio.Calls);

        var session = LoginAsAdmin();
        Assert.True(Send(command, args, session).Ok);

        _clock.Elapsed += TimeSpan.FromMinutes(4); // sesija istekla
        Assert.Equal("admin-required", Send(command, args, session).Error);
    }

    [Fact]
    public void Volume_IsClampedToAdminRange_ByAudioProcess()
    {
        var session = LoginAsAdmin();
        Assert.True(Send("set-volume-range", new() { ["min"] = "0.3", ["max"] = "0.7" }, session).Ok);
        _radio.Calls.Clear();

        Assert.Equal("0.7", Send("set-volume", new() { ["value"] = "1.0" }).Data!["value"]);
        Assert.Equal("0.3", Send("set-volume", new() { ["value"] = "0.0" }).Data!["value"]);
        Assert.Equal("0.5", Send("set-volume", new() { ["value"] = "0.5" }).Data!["value"]);
        Assert.Equal(["volume:0.7", "volume:0.3", "volume:0.5"], _radio.Calls);

        foreach (var bad in new[] { "abc", "NaN", "" })
        {
            Assert.Equal("bad-argument", Send("set-volume", new() { ["value"] = bad }).Error);
        }

        Assert.Equal("bad-argument", Send("set-volume").Error);
        Assert.Equal("bad-argument", Send("set-volume-range", new() { ["min"] = "0.8", ["max"] = "0.2" }, session).Error);
        Assert.Equal((0.3, 0.7), RadioCommandHandler.VolumeRange(_db));
    }

    [Fact]
    public void Next_WithStaleTrackId_DoesNotSkip()
    {
        _radio.CurrentTrack = "7";
        Assert.Equal("true", Send("next", new() { ["track"] = "7" }).Data!["skipped"]);
        _radio.CurrentTrack = "8"; // pesma se u medjuvremenu promenila
        Assert.Equal("false", Send("next", new() { ["track"] = "7" }).Data!["skipped"]);
    }

    [Fact]
    public void Pin_FirstSetupIsFree_LaterChangeNeedsAdmin_AndIsStoredHashed()
    {
        Assert.Equal("no-pin", Send("login", new() { ["pin"] = "4821" }).Error);
        Assert.Equal("bad-pin", Send("set-pin", new() { ["pin"] = "12" }).Error);
        Assert.Equal("bad-pin", Send("set-pin").Error);

        Assert.True(Send("set-pin", new() { ["pin"] = "4821" }).Ok);
        var stored = _db.Settings.Get(RadioCommandHandler.PinKey)!;
        Assert.DoesNotContain("4821", stored);

        // promena bez prijave nije dozvoljena
        Assert.Equal("admin-required", Send("set-pin", new() { ["pin"] = "9999" }).Error);
        Assert.Equal(stored, _db.Settings.Get(RadioCommandHandler.PinKey));

        var session = Send("login", new() { ["pin"] = "4821" }).Data!["session"];
        Assert.True(Send("set-pin", new() { ["pin"] = "9999" }, session).Ok);

        // stara sesija i stari PIN vise ne vaze
        Assert.Equal("admin-required", Send("rescan", session: session).Error);
        Assert.Equal("wrong-pin", Send("login", new() { ["pin"] = "4821" }).Error);
        Assert.True(Send("login", new() { ["pin"] = "9999" }).Ok);
    }

    [Fact]
    public void WrongPinFiveTimes_LocksOut_AndReportsWaitTime()
    {
        Assert.True(Send("set-pin", new() { ["pin"] = "4821" }).Ok);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("wrong-pin", Send("login", new() { ["pin"] = "0000" }).Error);
        }

        var locked = Send("login", new() { ["pin"] = "0000" });
        Assert.Equal("locked-out", locked.Error);
        Assert.Equal("300", locked.Data!["retryAfterSeconds"]);
        Assert.Equal("locked-out", Send("login", new() { ["pin"] = "4821" }).Error);

        _clock.Elapsed += TimeSpan.FromMinutes(5);
        Assert.True(Send("login", new() { ["pin"] = "4821" }).Ok);
    }

    [Fact]
    public void UnknownCommand_IsRefused()
    {
        Assert.Equal("unknown-command", Send("format-disk").Error);
        Assert.Empty(_radio.Calls);
    }

    [Fact]
    public void Logout_EndsSession()
    {
        var session = LoginAsAdmin();
        Assert.True(Send("logout", session: session).Ok);
        Assert.Equal("admin-required", Send("rescan", session: session).Error);
    }

    private sealed class FakeMonotonic : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class FakeRadio : IRadioControl
    {
        private double _volume = 1.0;

        public List<string> Calls { get; } = [];

        public string? CurrentTrack { get; set; }

        public RadioState GetState() => new() { Volume = _volume, TrackId = CurrentTrack };

        public void Pause() => Calls.Add("pause");

        public void Resume() => Calls.Add("resume");

        public bool Next(string? expectedTrackId)
        {
            Calls.Add("next:" + expectedTrackId);
            return expectedTrackId is null || expectedTrackId == CurrentTrack;
        }

        public void SetVolume(double volume)
        {
            _volume = volume;
            Calls.Add("volume:" + volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Rescan() => Calls.Add("rescan");

        public void StateChanged() => Calls.Add("state-changed");

        public void SetMusicFolders(IReadOnlyList<string> folders) => Calls.Add("folders:" + string.Join(";", folders));
    }
}
