using System.Diagnostics;
using System.Security.Principal;
using LustreRadio.Core;
using LustreRadio.Core.Supervision;
using LustreRadio.Core.Time;
using LustreRadio.Ipc;
using LustreRadio.Shared;
using Serilog.Core;

namespace LustreRadio.Watchdog;

/// <summary>
/// Nadzor: jedini pokrece audio proces i vraca ga ako padne ili se zamrzne (spec. 2.2).
/// Ako nadzor padne, audio proces nastavlja da svira; novi nadzor ga preuzima.
/// Argumenti:
///   (bez)            nadzor (provera na 2 s; kvar posle 20 s cutanja i 5 uzastopnih neuspelih provera)
///   --stop           namerno zaustavljanje radija (do --start ili sledeceg paljenja racunara)
///   --start          ukida namerno zaustavljanje
///   --install-task   pravi zadatak u Task Scheduler-u za prijavljenog korisnika
///   --remove-task    brise taj zadatak
///   --check-task     proverava podesavanja zadatka
/// Izlazni kodovi: 0 uredno, 1 greska, 2 pogresna sesija, 3 nadzor vec radi.
/// </summary>
public static class Program
{
    private const string EngineProcessName = "LustreRadio.Engine";
    private static readonly SupervisorSettings Settings = new();

    public static int Main(string[] args)
    {
        using var log = LogSetup.Create("watchdog");
        var dataRoot = AppPaths.DefaultDataRoot;

        try
        {
            if (args.Contains("--install-task"))
            {
                return InstallTask(log);
            }

            if (args.Contains("--remove-task"))
            {
                return RunSchtasks(log, "/Delete", "/TN", WatchdogTask.TaskName, "/F").ExitCode;
            }

            if (args.Contains("--check-task"))
            {
                return CheckTask(log);
            }

            if (args.Contains("--stop"))
            {
                StopFlag.Set(dataRoot, StopFlag.BootTimeUtc());
                log.Information("Namerno zaustavljanje je zatrazeno.");
                return 0;
            }

            if (args.Contains("--start"))
            {
                StopFlag.Clear(dataRoot);
                log.Information("Namerno zaustavljanje je ukinuto.");
                return 0;
            }

            return Supervise(log, dataRoot);
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Nadzor je stao zbog greske.");
            return 1;
        }
    }

    private static int Supervise(Logger log, string dataRoot)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        if (!SessionGuard.MayRun(sessionId))
        {
            log.Fatal("Nadzor je pokrenut u sesiji 0. Zadatak mora biti 'Run only when user is logged on'; izlazim.");
            return 2;
        }

        using var instance = SingleInstance.TryAcquire(InstanceNames.Watchdog);
        if (instance is null)
        {
            return 3; // nadzor vec radi (npr. ponavljanje zadatka na 5 minuta)
        }

        var enginePath = Path.Combine(AppContext.BaseDirectory, EngineProcessName + ".exe");
        log.Information("Nadzor radi; sesija {SessionId}; audio proces: {EnginePath}; podaci: {DataRoot}", sessionId, enginePath, dataRoot);

        var clock = new SystemMonotonicClock();
        var policy = new SupervisorPolicy(clock, Settings);
        var cadence = Stopwatch.StartNew();
        IpcClient? client = null;
        using var exit = new ManualResetEventSlim();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();
        var lastReason = string.Empty;

        while (!exit.IsSet)
        {
            try
            {
                var engines = FindEngines(sessionId);
                var observation = new EngineObservation
                {
                    ProcessRunning = engines.Count > 0,
                    StopRequested = StopFlag.IsSet(dataRoot, StopFlag.BootTimeUtc()),
                };

                if (engines.Count > 0)
                {
                    (client, var state) = PollWithLimit(client, log);
                    if (state is not null)
                    {
                        observation = observation with
                        {
                            Responded = true,
                            OnAir = state.OnAir,
                            Paused = state.Paused,
                            OutputPlaying = state.Output == "Playing",
                            FramesRendered = state.FramesRendered,
                        };
                    }
                }

                var decision = policy.Evaluate(observation);
                if (decision.Action != SupervisorAction.None || decision.Reason != lastReason)
                {
                    log.Information("Odluka: {Action} ({Reason}); sigurni rezim: {SafeMode}", decision.Action, decision.Reason, decision.SafeMode);
                    lastReason = decision.Reason;
                }

                if (decision.Action is SupervisorAction.Kill or SupervisorAction.KillAndRestart)
                {
                    client = Drop(client);
                    KillAll(engines, log);
                    WaitForEngineGone(TimeSpan.FromSeconds(10));
                }

                if (decision.Action is SupervisorAction.Start or SupervisorAction.KillAndRestart)
                {
                    // dva uslova pre pokretanja: brava slobodna i nema procesa
                    if (!SingleInstance.IsHeld(InstanceNames.Engine) && FindEngines(sessionId).Count == 0)
                    {
                        StartEngine(enginePath, decision.SafeMode, log);
                    }
                    else
                    {
                        log.Warning("Audio proces jos postoji; pokretanje se preskace.");
                    }
                }

                foreach (var engine in engines)
                {
                    engine.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "Greska u petlji nadzora.");
            }

            // Fiksni raspored: sledeca provera pocinje na sledecem celom koraku od 2 s,
            // bez obzira koliko je ova trajala. (Ranije "proveri pa cekaj 5 s": razmak je rastao
            // bas kad audio proces ne odgovara, pa je otkrivanje kasnilo.)
            var step = Settings.PollInterval.Ticks;
            var next = TimeSpan.FromTicks(((cadence.Elapsed.Ticks / step) + 1) * step);
            exit.Wait(next - cadence.Elapsed);
        }

        Drop(client);
        return 0;
    }

    private static List<Process> FindEngines(int sessionId) =>
        Process.GetProcessesByName(EngineProcessName).Where(p =>
        {
            try
            {
                return p.SessionId == sessionId && !p.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }).ToList();

    /// <summary>
    /// Provera sa tvrdim rokom: sta god da se desi sa vezom, petlja nadzora ne sme da stane.
    /// Ako provera ne zavrsi na vreme, racuna se da audio proces nije odgovorio, a veza se
    /// zatvara u pozadini kad zaostala provera zavrsi.
    /// </summary>
    private static (IpcClient? Client, RadioState? State) PollWithLimit(IpcClient? client, Logger log)
    {
        _ = log;
        var poll = Task.Run(() => Poll(client));
        if (poll.Wait(Settings.PollTimeout))
        {
            return poll.Result;
        }

        poll.ContinueWith(
            finished =>
            {
                if (finished.Status == TaskStatus.RanToCompletion)
                {
                    Drop(finished.Result.Client);
                }
                else
                {
                    _ = finished.Exception;
                    Drop(client);
                }
            },
            TaskScheduler.Default);
        return (null, null);
    }

    private static (IpcClient? Client, RadioState? State) Poll(IpcClient? client)
    {
        try
        {
            if (client is not { IsConnected: true })
            {
                Drop(client);
                client = IpcClient.ConnectAsync(IpcProtocol.DefaultPipeName, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            }

            var response = client.SendAsync("get-state", timeout: TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            if (response.Ok != true)
            {
                return (client, null);
            }

            // stanje stize zasebnom porukom; kratko sacekaj prvu
            SpinWait.SpinUntil(() => client.LastState is not null, TimeSpan.FromMilliseconds(300));
            return (client, client.LastState);
        }
        catch (Exception)
        {
            return (Drop(client), null);
        }
    }

    /// <summary>Zatvara vezu u pozadini; nikad ne ceka (cekanje bi pomerilo raspored provera).</summary>
    private static IpcClient? Drop(IpcClient? client)
    {
        if (client is null)
        {
            return null;
        }

        try
        {
            client.DisposeAsync().AsTask().ContinueWith(closed => _ = closed.Exception, TaskScheduler.Default);
        }
        catch (Exception)
        {
            // veza je vec pukla
        }

        return null;
    }

    private static void KillAll(List<Process> engines, Logger log)
    {
        foreach (var engine in engines)
        {
            try
            {
                log.Warning("Prekidam audio proces {ProcessId}.", engine.Id);
                engine.Kill();
            }
            catch (Exception ex)
            {
                log.Warning("Prekid nije uspeo: {Message}", ex.Message);
            }
        }
    }

    private static void WaitForEngineGone(TimeSpan limit) =>
        SpinWait.SpinUntil(() => !SingleInstance.IsHeld(InstanceNames.Engine), limit);

    private static void StartEngine(string enginePath, bool safeMode, Logger log)
    {
        if (!File.Exists(enginePath))
        {
            log.Error("Audio proces nije pronadjen: {EnginePath}", enginePath);
            return;
        }

        var info = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(enginePath)!,
            CreateNoWindow = true,
        };
        if (safeMode)
        {
            info.ArgumentList.Add("--safe");
        }

        using var process = Process.Start(info);
        log.Information("Pokrenut audio proces {ProcessId}{Safe}.", process?.Id, safeMode ? " (sigurni rezim)" : string.Empty);
    }

    private static int InstallTask(Logger log)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var watchdogPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LustreRadio.Watchdog.exe");
        var xmlPath = Path.Combine(Path.GetTempPath(), "LustreRadio-task-" + Environment.ProcessId + ".xml");
        File.WriteAllText(xmlPath, WatchdogTask.BuildXml(user, watchdogPath), System.Text.Encoding.Unicode);
        try
        {
            var result = RunSchtasks(log, "/Create", "/TN", WatchdogTask.TaskName, "/XML", xmlPath, "/F");
            if (result.ExitCode != 0)
            {
                return result.ExitCode;
            }

            log.Information("Zadatak '{Task}' je napravljen za korisnika {User}.", WatchdogTask.TaskName, user);
            return CheckTask(log);
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    private static int CheckTask(Logger log)
    {
        var result = RunSchtasks(log, "/Query", "/TN", WatchdogTask.TaskName, "/XML");
        if (result.ExitCode != 0)
        {
            log.Error("Zadatak '{Task}' ne postoji.", WatchdogTask.TaskName);
            return 1;
        }

        var problems = WatchdogTask.Problems(result.Output);
        foreach (var problem in problems)
        {
            log.Error("Podesavanje zadatka: {Problem}", problem);
        }

        if (problems.Count == 0)
        {
            log.Information("Podesavanja zadatka su ispravna.");
        }

        return problems.Count == 0 ? 0 : 1;
    }

    private static (int ExitCode, string Output) RunSchtasks(Logger log, params string[] arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            log.Warning("schtasks {Arguments}: kod {ExitCode}: {Error}", string.Join(' ', arguments), process.ExitCode, error.Trim());
        }

        return (process.ExitCode, output);
    }
}
