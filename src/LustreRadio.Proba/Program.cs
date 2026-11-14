using System.Text;
using LustreRadio.Audio;
using LustreRadio.Core.Licensing;
using LustreRadio.Playout;

namespace LustreRadio.Proba;

/// <summary>Probni radio u konzoli: skenira folder "muzika", svira po rasporedu, pamti stanje. Argument --auto: bez pitanja, 3 s.</summary>
public static class Program
{
    private static readonly object Sync = new();
    private static StreamWriter? _journal;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var auto = args.Contains("--auto");
        var root = AppContext.BaseDirectory;
        var music = Path.Combine(root, "muzika");
        var data = Path.Combine(root, "podaci");

        try
        {
            _journal = new StreamWriter(Path.Combine(root, "LustreRadio-Proba-dnevnik.txt"), append: true, new UTF8Encoding(true)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bez dnevnika, samo ekran
        }

        Say("==== LUSTRE RADIO proba " + typeof(Program).Assembly.GetName().Version + " ====");

        if (!Directory.Exists(music) && !auto)
        {
            Say("Nema foldera 'muzika' pored programa. Napravite ga, ubacite pesme i pokrenite ponovo.");
            Wait(auto);
            return 0;
        }

        using var host = new EngineHost(new EngineHostOptions { DataRoot = data, MusicFolders = [music], AlwaysOpen = true, Log = Say });

        Say("Skeniram folder 'muzika' (prvi put traje duze)...");
        var summary = host.Index();
        Say($"Fajlova: {summary.FilesOnDisk}; novih: {summary.Added}; ponovo skeniranih: {summary.Rescanned}; nestalih: {summary.MarkedMissing}; ne mogu da se dekodiraju: {summary.Undecodable}");

        var declaration = host.Database.Settings.GetJson<CollectiveLicense>(LibraryIndexer.CollectiveLicenseKey);
        if (declaration is null)
        {
            Say("Pesma sme da svira samo ako za nju postoji zabelezen osnov (licenca).");
            var answer = "n";
            if (!auto)
            {
                Console.WriteLine("Da li izjavljujete da je muzika koju pustate pokrivena vasim ugovorima");
                Console.Write("sa SOKOJ i OFPS? Izjava se belezi sa datumom. (d/n): ");
                answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "n";
            }

            Say("Odgovor: " + answer);
            if (answer == "d")
            {
                host.Database.Settings.SetJson(LibraryIndexer.CollectiveLicenseKey, new CollectiveLicense
                {
                    Organizations = "SOKOJ, OFPS",
                    DeclaredBy = "vlasnik (proba)",
                    DeclaredAt = DateTimeOffset.Now,
                });
            }
        }
        else
        {
            Say($"Vazi izjava o kolektivnoj licenci: {declaration.Organizations} ({declaration.DeclaredBy}, {declaration.DeclaredAt:yyyy-MM-dd}).");
        }

        var applied = LibraryIndexer.ApplyCollectiveLicense(host.Database);
        if (applied > 0)
        {
            Say($"Po izjavi o kolektivnoj licenci potvrdjeno pesama: {applied}.");
        }

        host.TrackStarted += playing => Say(
            (playing.Resumed ? "NASTAVAK: " : "SVIRA:    ") +
            (string.IsNullOrWhiteSpace(playing.Artist) ? string.Empty : playing.Artist + " - ") + playing.Title +
            $"   [{playing.Program}]");
        host.OutputStateChanged += (state, detail) => Say(state switch
        {
            OutputState.Playing => "[zvuk ide na: " + detail + "]",
            OutputState.Disconnected => "[ZVUCNI UREDJAJ NIJE POVEZAN - cekam da se vrati...]",
            _ => "[" + state + "]",
        });

        host.Start();
        Say($"Pesama koje smeju u eter: {host.BroadcastableCount}.");
        if (host.BroadcastableCount == 0)
        {
            Say("Nema nijedne pesme koja sme da svira, pa radio cuti. To je ispravno ponasanje.");
        }

        if (auto)
        {
            Thread.Sleep(3000);
            return 0;
        }

        Say("Tasteri: N = sledeca pesma, Q = kraj.");
        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.N)
            {
                Say("(sledeca pesma)");
                host.Skip();
            }
            else if (key == ConsoleKey.Q)
            {
                break;
            }
        }

        Say("Gasim. Ako ponovo pokrenete program u roku od 10 minuta, treba da nastavi istu pesmu.");
        return 0;
    }

    private static void Say(string text)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        lock (Sync)
        {
            Console.WriteLine(line);
            _journal?.WriteLine(DateTime.Now.ToString("yyyy-MM-dd ") + line);
        }
    }

    private static void Wait(bool auto)
    {
        if (!auto)
        {
            Console.WriteLine("Pritisnite Enter za kraj.");
            Console.ReadLine();
        }
    }
}
