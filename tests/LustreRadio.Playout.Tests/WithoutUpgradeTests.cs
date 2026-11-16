using System.Security.Cryptography;
using LustreRadio.Core.Security;
using LustreRadio.Core.Time;
using LustreRadio.Data;
using LustreRadio.Ipc;
using LustreRadio.Playout;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LustreRadio.Playout.Tests;

/// <summary>Faza B, korak B1: rad bez nadogradnje u Engine-u i komandama (plan v8, deo 2.3).</summary>
public sealed class WithoutUpgradeTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "db", "sema1-sqlite-3.46.1.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lustre-bez-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public WithoutUpgradeTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "lustre.db");
        File.Copy(Fixture, _path);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder
        }
    }

    [Fact(Skip = "rad bez nadogradnje je iskljucen (P2 ne prolazi; B1, 16.11.2026.); ceka odluku vlasnika")] // B-151
    public void B151_EveryBlockedCommand_IsRefused_WithMessage_AndNothingIsWritten()
    {
        using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES ($k, $v)";
            command.Parameters.AddWithValue("$k", RadioCommandHandler.PinKey);
            command.Parameters.AddWithValue("$v", PinVault.Hash("4821"));
            command.ExecuteNonQuery();
        }

        BlockBackups();
        var before = Sha(_path);
        var result = DatabaseStartup.Open(_path, Path.Combine(_root, EngineHost.BackupsFolder));
        Assert.Equal(DatabaseMode.NotUpgraded, result.Mode);
        using (var db = result.Database)
        {
            var handler = new RadioCommandHandler(new NullRadio(), db, new AdminSessions(new SystemMonotonicClock(), () => db.Settings.Get(RadioCommandHandler.PinKey)));
            IpcMessage Send(string name, Dictionary<string, string>? args = null, string? session = null) =>
                handler.Handle(new IpcMessage { Type = "cmd", Id = Guid.NewGuid().ToString("N"), Name = name, Args = args, Session = session });

            var login = Send("login", new() { ["pin"] = "4821" });
            Assert.True(login.Ok);
            var session = login.Data!["session"];

            var blocked = new (string Name, Dictionary<string, string> Args)[]
            {
                ("rescan", new()),
                ("set-music-folders", new() { ["folders"] = _root }),
                ("set-volume-range", new() { ["min"] = "0.1", ["max"] = "0.9" }),
                ("set-language", new() { ["screen"] = "staff", ["lang"] = "en" }),
                ("declare-collective-license", new() { ["organizations"] = "SOKOJ, OFPS" }),
                ("set-pin", new() { ["pin"] = "1234", ["current"] = "4821" }),
            };
            Assert.Equal(blocked.Select(b => b.Name).Order(), RadioCommandHandler.BlockedWithoutUpgrade.Order());
            foreach (var (name, args) in blocked)
            {
                var response = Send(name, args, session);
                Assert.False(response.Ok, name);
                Assert.Equal(RadioCommandHandler.DatabaseNotUpgradedError, response.Error);
            }

            // ostalo radi
            Assert.True(Send("pause").Ok);
            Assert.True(Send("play").Ok);
            Assert.True(Send("set-volume", new() { ["value"] = "0.5" }).Ok);
        }

        Assert.Equal(before, Sha(_path));
    }

    [Fact(Skip = "rad bez nadogradnje je iskljucen (P2 ne prolazi; B1, 16.11.2026.); ceka odluku vlasnika")] // B-150, kratka verzija na G (bez zvuka; verzija od 10 minuta sa zvukom nije pokrenuta)
    public void B150_EngineHost_WithoutUpgrade_ReportsMode_SkipsScan_FileUnchanged()
    {
        BlockBackups();
        var before = Sha(_path);
        var log = new List<string>();
        using (var host = new EngineHost(new EngineHostOptions { DataRoot = _root, Log = log.Add }))
        {
            Assert.Equal(DatabaseMode.NotUpgraded, host.DatabaseMode);
            var state = host.GetState();
            Assert.Equal(RadioState.DatabaseNotUpgraded, state.DatabaseMode);
            Assert.False(string.IsNullOrEmpty(state.DatabaseModeReason));
            host.IndexInBackground();
            Assert.Contains(log, l => l.Contains("skeniranje biblioteke je iskljuceno", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("sema 1 (NIJE nadogradjena", StringComparison.Ordinal));
            host.SetVolume(0.4);
        }

        Assert.Equal(before, Sha(_path));
    }

    [Fact] // Engine odbija pokretanje kad kopija ne moze da se napravi (rad bez nadogradnje je iskljucen)
    public void EngineHost_BackupImpossible_RefusesToStart_FileUnchanged()
    {
        BlockBackups();
        var before = Sha(_path);
        var error = Assert.Throws<DatabaseStartupException>(() => new EngineHost(new EngineHostOptions { DataRoot = _root }).Dispose());
        Assert.Contains("iskljucen", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Sha(_path));
    }

    [Fact] // B-169: rotacija dobija iste podatke pre i posle nadogradnje (stari zapisi ostaju i racunaju se)
    public void B169_HistoryForRotation_IsIdentical_BeforeAndAfterUpgrade()
    {
        var since = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<PlayRow> before;
        using (var old = LustreDatabase.Open(_path, readOnly: true))
        {
            before = old.History.GetSince(since);
        }

        var result = DatabaseStartup.Open(_path, Path.Combine(_root, EngineHost.BackupsFolder));
        using var db = result.Database;
        Assert.Equal(DatabaseMode.Normal, result.Mode);
        var after = db.History.GetSince(since);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before, after);
    }

    private void BlockBackups() => File.WriteAllText(Path.Combine(_root, EngineHost.BackupsFolder), "fajl umesto foldera: kopija ne moze da se napravi");

    private static string Sha(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class NullRadio : IRadioControl
    {
        public RadioState GetState() => new();

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public bool Next(string? expectedTrackId) => true;

        public void SetVolume(double volume)
        {
        }

        public void Rescan()
        {
        }

        public void SetMusicFolders(IReadOnlyList<string> folders)
        {
        }

        public void StateChanged()
        {
        }
    }
}
