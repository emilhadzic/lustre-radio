using LustreRadio.Core;
using LustreRadio.Playout;
using LustreRadio.Shared;

namespace LustreRadio.Engine;

/// <summary>
/// Audio proces: obican program bez prozora u korisnickoj sesiji (nikad Windows servis).
/// Svira po rasporedu iz baze. Nadzor (Watchdog) i veza sa interfejsom dolaze u narednim fazama.
/// Argument --smoke: otvori privremenu bazu, proveri da sve moze da se sklopi i izadji.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        using var log = LogSetup.Create("engine");
        var smoke = args.Contains("--smoke");
        var dataRoot = smoke
            ? Path.Combine(Path.GetTempPath(), "LustreRadio-smoke-" + Environment.ProcessId)
            : AppPaths.DefaultDataRoot;

        try
        {
            log.Information(
                "LUSTRE RADIO Engine {Version}; sesija {SessionId}; podaci: {DataRoot}",
                typeof(Program).Assembly.GetName().Version,
                System.Diagnostics.Process.GetCurrentProcess().SessionId,
                dataRoot);

            using var host = new EngineHost(new EngineHostOptions { DataRoot = dataRoot, Log = message => log.Information("{Message}", message) });
            var summary = host.Index();
            log.Information("Biblioteka: {Summary}", summary);

            if (smoke)
            {
                log.Information("Smoke provera prosla (sema baze {Schema}).", host.Database.SchemaVersion);
                return 0;
            }

            host.TrackStarted += playing => log.Information("Svira: {Artist} - {Title} [{Program}]", playing.Artist, playing.Title, playing.Program);
            host.OutputStateChanged += (state, detail) => log.Information("Izlaz: {State} {Detail}", state, detail);
            host.Start();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Engine je stao zbog greske.");
            return 1;
        }
        finally
        {
            if (smoke && Directory.Exists(dataRoot))
            {
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch (IOException)
                {
                    // privremeni folder
                }
            }
        }
    }
}
