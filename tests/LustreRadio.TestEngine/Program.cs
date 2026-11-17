using LustreRadio.Core;
using LustreRadio.Core.Licensing;
using LustreRadio.Playout;

namespace LustreRadio.TestEngine;

/// <summary>
/// Probni Engine za test oporavka sa pravim nadzorom (B6, B-196 skraceno). Radi kao pravi Engine, uz dve razlike:
/// <list type="bullet">
/// <item>ako u folderu podataka postoji <c>probni-engine.txt</c>: napravi probnu muziku, folder, izjavu o licenci i radno vreme ceo dan;</item>
/// <item>ako pri pokretanju postoji <c>zaglavi-sve.flag</c>: obrise ga i u OVOM procesu se svako otvaranje fajla zaglavi zauvek.</item>
/// </list>
/// Otvoreni i zaglavljeni fajlovi se upisuju u <c>otvoreni.txt</c> i <c>zaglavljeni.txt</c>.
/// </summary>
public static class Program
{
    private static readonly object Sync = new();

    public static int Main(string[] args)
    {
        var root = AppPaths.DefaultDataRoot;
        Directory.CreateDirectory(root);
        var setup = File.Exists(Path.Combine(root, "probni-engine.txt"));
        var flag = Path.Combine(root, "zaglavi-sve.flag");
        var stallAll = File.Exists(flag);
        if (stallAll)
        {
            File.Delete(flag);
        }

        var music = Path.Combine(root, "probna-muzika");
        if (setup && !Directory.Exists(music))
        {
            Directory.CreateDirectory(music);
            for (var i = 0; i < 8; i++)
            {
                WriteTone(Path.Combine(music, $"ton-{300 + (i * 100)}.wav"), 300 + (i * 100), 20);
            }
        }

        var run = Environment.ProcessId;
        void Note(string file, string path)
        {
            lock (Sync)
            {
                File.AppendAllText(Path.Combine(root, file), $"{run} {Path.GetFileNameWithoutExtension(path)}{Environment.NewLine}");
            }
        }

        return LustreRadio.Engine.Program.Run(
            args,
            options => !setup ? options : options with
            {
                MusicFolders = [music],
                AlwaysOpen = true,
                BeforeOpenFile = path =>
                {
                    Note("otvoreni.txt", path);
                    if (stallAll)
                    {
                        Note("zaglavljeni.txt", path);
                        Thread.Sleep(Timeout.Infinite); // "dekoder" koji se nikad ne vraca
                    }
                },
            },
            host =>
            {
                if (!setup)
                {
                    return;
                }

                host.Index();
                if (host.Database.Settings.GetJson<CollectiveLicense>(LibraryIndexer.CollectiveLicenseKey) is null)
                {
                    host.Database.Settings.SetJson(LibraryIndexer.CollectiveLicenseKey, new CollectiveLicense
                    {
                        Organizations = "SOKOJ, OFPS", DeclaredBy = "probni Engine", DeclaredAt = DateTimeOffset.Now,
                    });
                }

                LibraryIndexer.ApplyCollectiveLicense(host.Database);
            });
    }

    private static void WriteTone(string path, int hz, double seconds)
    {
        var frames = (int)Math.Round(seconds * 44100);
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = frames * 4;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(44100);
        writer.Write(44100 * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)Math.Round(0.25 * 32767 * Math.Sin(2 * Math.PI * hz * i / 44100.0));
            writer.Write(sample);
            writer.Write(sample);
        }
    }
}
