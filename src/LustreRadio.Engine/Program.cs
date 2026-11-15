using System.Diagnostics;
using LustreRadio.Core;
using LustreRadio.Core.Supervision;
using LustreRadio.Ipc;
using LustreRadio.Playout;
using LustreRadio.Shared;

namespace LustreRadio.Engine;

/// <summary>
/// Audio proces: obican program bez prozora u korisnickoj sesiji (nikad Windows servis).
/// Izlazni kodovi: 0 uredno, 1 greska, 2 pogresna sesija (sesija 0), 3 vec radi druga instanca.
/// Argumenti: --smoke (sklopi i izadji), --safe (sigurni rezim, posle vise padova).
/// </summary>
public static class Program
{
    public const int ExitWrongSession = 2;
    public const int ExitAlreadyRunning = 3;

    public static int Main(string[] args)
    {
        using var log = LogSetup.Create("engine");
        var smoke = args.Contains("--smoke");
        var safe = args.Contains("--safe");
        var dataRoot = smoke
            ? Path.Combine(Path.GetTempPath(), "LustreRadio-smoke-" + Environment.ProcessId)
            : AppPaths.DefaultDataRoot;

        try
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            log.Information(
                "LUSTRE RADIO Engine {Version}; sesija {SessionId}; podaci: {DataRoot}; sigurni rezim: {Safe}",
                typeof(Program).Assembly.GetName().Version, sessionId, dataRoot, safe);

            if (!smoke && !SessionGuard.MayRun(sessionId))
            {
                log.Fatal("Audio proces je pokrenut u sesiji 0 (kao servis). Tu nema pouzdanog zvuka; izlazim.");
                return ExitWrongSession;
            }

            using var instance = smoke ? null : SingleInstance.TryAcquire(InstanceNames.Engine);
            if (!smoke && instance is null)
            {
                log.Warning("Audio proces vec radi; ova instanca izlazi.");
                return ExitAlreadyRunning;
            }

            using var host = new EngineHost(new EngineHostOptions
            {
                DataRoot = dataRoot,
                Log = message => log.Information("{Message}", message),
                PipeName = smoke ? null : IpcProtocol.DefaultPipeName,
                SafeMode = safe,
            });

            if (smoke)
            {
                log.Information("Biblioteka: {Summary}", host.Index());
                log.Information("Smoke provera prosla (sema baze {Schema}).", host.Database.SchemaVersion);
                return 0;
            }

            host.TrackStarted += playing => log.Information("Svira: {Artist} - {Title} [{Program}]", playing.Artist, playing.Title, playing.Program);
            host.OutputStateChanged += (state, detail) => log.Information("Izlaz: {State} {Detail}", state, detail);
            host.Start();
            host.IndexInBackground();

            using var exit = new ManualResetEventSlim();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();
            exit.Wait();
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
