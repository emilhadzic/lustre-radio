using LustreRadio.Data;
using Xunit;

namespace LustreRadio.Playout.Tests;

/// <summary>FINAL, korak 4: nocna kopija baze. Kljucni rizik: kopija mora biti ispravna i ne sme da raste bez granice.</summary>
public sealed class NightlyBackupTests
{
    [Fact]
    public void Backup_IsVerified_OncePerDay_KeepsLast14()
    {
        using var env = new TestEnvironment();
        env.AddTone(300, 5);
        env.Index();
        var folder = Path.Combine(Path.GetTempPath(), "lustre-nightly-" + Guid.NewGuid().ToString("N"));
        var log = new List<string>();
        var backup = new NightlyBackup(env.Db, folder, log.Add);
        try
        {
            var day = new DateTime(2026, 11, 1, 3, 0, 0);
            Assert.False(backup.Due(day)); // pre 03:30
            for (var i = 0; i < 20; i++)
            {
                var night = day.AddDays(i).AddMinutes(45);
                Assert.True(backup.Due(night));
                var path = backup.Run(night);
                Assert.NotNull(path);
                Assert.True(LustreDatabase.VerifyFile(path));
                Assert.False(backup.Due(night.AddHours(5))); // isti dan: samo jednom
            }

            using (var copy = LustreDatabase.Open(Directory.GetFiles(folder).Max()!, readOnly: true))
            {
                Assert.Single(copy.Tracks.GetAll()); // kopija sadrzi podatke
            }

            Assert.Equal(14, Directory.GetFiles(folder, NightlyBackup.Prefix + "*.db").Length);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
