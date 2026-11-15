using System.Globalization;
using System.Security;
using LustreRadio.Core.Fallback;
using LustreRadio.Core.Time;

namespace LustreRadio.Core.Supervision;

/// <summary>Imena sistemskih brava: svaki program postoji najvise u jednom primerku.</summary>
public static class InstanceNames
{
    public const string Engine = @"Global\LustreRadio.Engine";
    public const string Watchdog = @"Global\LustreRadio.Watchdog";
    public const string Ui = @"Global\LustreRadio.UI";
}

/// <summary>
/// Sistemska brava (imenovani mutex). Windows je sam oslobadja kad proces umre,
/// pa posle pada ne ostaje "zaglavljena". Mora se drzati dok program radi.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly Thread _owner;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _owner = Thread.CurrentThread;
    }

    /// <summary>Vraca bravu ako je slobodna, inace null (program vec radi).</summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstance(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex); // prethodni vlasnik je pao; brava je sada nasa
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>Da li neko trenutno drzi bravu.</summary>
    public static bool IsHeld(string name)
    {
        using var probe = TryAcquire(name);
        return probe is null;
    }

    public void Dispose()
    {
        if (Thread.CurrentThread == _owner)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // vec oslobodjena
            }
        }

        _mutex.Dispose();
    }
}

public static class SessionGuard
{
    /// <summary>
    /// Audio proces sme da radi samo u prijavljenoj korisnickoj sesiji. Sesija 0 je sesija
    /// Windows servisa, gde pristup zvuku nije pouzdan (spec. 2.1).
    /// </summary>
    public static bool MayRun(int sessionId) => sessionId != 0;
}

/// <summary>
/// Namerno zaustavljanje: dok oznaka vazi, nadzor ne pokrece nista. Oznaka vazi samo do
/// sledeceg paljenja racunara (pamti vreme paljenja), ili dok se ne ukloni komandom "Pokreni".
/// </summary>
public static class StopFlag
{
    public const string FileName = "stop.flag";

    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, FileName);

    public static void Set(string dataRoot, DateTime bootTimeUtc)
    {
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(PathIn(dataRoot), bootTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    public static void Clear(string dataRoot)
    {
        try
        {
            File.Delete(PathIn(dataRoot));
        }
        catch (IOException)
        {
            // nije bitno
        }
    }

    public static bool IsSet(string dataRoot, DateTime bootTimeUtc)
    {
        try
        {
            var path = PathIn(dataRoot);
            if (!File.Exists(path))
            {
                return false;
            }

            return DateTime.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stored)
                   && (stored.ToUniversalTime() - bootTimeUtc.ToUniversalTime()).Duration() < TimeSpan.FromMinutes(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Kada je racunar upaljen (iz broja milisekundi od paljenja).</summary>
    public static DateTime BootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Sta je nadzor video pri jednoj proveri.</summary>
public sealed record EngineObservation
{
    public bool ProcessRunning { get; init; }

    /// <summary>Audio proces je odgovorio na upit o stanju.</summary>
    public bool Responded { get; init; }

    public bool OnAir { get; init; }

    public bool Paused { get; init; }

    /// <summary>Zvucni izlaz je u stanju "Playing" (uredjaj povezan).</summary>
    public bool OutputPlaying { get; init; }

    /// <summary>Brojac pripremljenog zvuka; dok muzika ide, mora da raste.</summary>
    public long FramesRendered { get; init; }

    public bool StopRequested { get; init; }
}

public enum SupervisorAction
{
    None,
    Start,

    /// <summary>Zaustavi bez ponovnog pokretanja (namerno zaustavljanje).</summary>
    Kill,
    KillAndRestart,
}

public sealed record SupervisorDecision(SupervisorAction Action, bool SafeMode, string Reason);

public sealed record SupervisorSettings
{
    /// <summary>Provera pocinje na svakih ovoliko, bez obzira koliko je prethodna trajala (spec. 2.2).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Najduze trajanje jedne provere; posle toga se racuna kao neuspela.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Pauza pre ponovnog pokretanja posle PADA (da se ne vrti u krug). Ne vazi posle prekidanja zamrznutog procesa.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Bez odgovora ovoliko dugo (i najmanje MinFailedPolls uzastopnih neuspelih provera) = zamrznut.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Koliko uzastopnih provera mora da ne uspe pre nego sto se proces proglasi zamrznutim.</summary>
    public int MinFailedPolls { get; init; } = 5;

    /// <summary>Koliko se ceka PRVI odgovor posle pokretanja (pokretanje na sporom racunaru traje).</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>"Svira", a brojac zvuka stoji ovoliko dugo = zamrznut zvuk.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Posle ovoliko stabilnog rada sigurni rezim se ukida.</summary>
    public TimeSpan SafeModeProbation { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ako izmedju dve provere prodje vise od ovoga, nadzor sam nije radio (racunar zauzet ili se budio).
    /// To vreme se ne racuna kao cutanje audio procesa: inace bi nadzor ubio zdrav proces.
    /// </summary>
    public TimeSpan SelfStallGap { get; init; } = TimeSpan.FromSeconds(6);
}

public sealed class SupervisorPolicy(IMonotonicClock clock, SupervisorSettings settings)
{
    private readonly CrashLoopDetector _crashes = new();
    private TimeSpan _lastStart = TimeSpan.MinValue;
    private TimeSpan _lastResponse;
    private TimeSpan _lastProgress;
    private TimeSpan _healthySince;
    private long _lastFrames = -1;
    private bool _startedByUs;
    private bool _wasRunning;
    private bool _awaitingFirstResponse;
    private bool _evaluatedBefore;
    private TimeSpan _lastEvaluate;
    private int _failedPolls;

    public bool SafeMode { get; private set; }

    public SupervisorDecision Evaluate(EngineObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var now = clock.Elapsed;

        if (_evaluatedBefore && now - _lastEvaluate > settings.SelfStallGap)
        {
            // nadzor sam nije radio: brojanje cutanja i stajanja zvuka krece iznova
            _lastResponse = now;
            _lastProgress = now;
            _failedPolls = 0;
        }

        _evaluatedBefore = true;
        _lastEvaluate = now;

        if (observation.StopRequested)
        {
            _wasRunning = false;
            _startedByUs = false;
            return observation.ProcessRunning
                ? new(SupervisorAction.Kill, SafeMode, "Namerno zaustavljanje.")
                : new(SupervisorAction.None, SafeMode, "Zaustavljeno po zahtevu.");
        }

        if (!observation.ProcessRunning)
        {
            if (_wasRunning)
            {
                // radio je, a vise ga nema: pad
                _wasRunning = false;
                RecordCrash(now);
            }

            if (_lastStart != TimeSpan.MinValue && now - _lastStart < settings.RestartDelay)
            {
                return new(SupervisorAction.None, SafeMode, "Pauza pre ponovnog pokretanja.");
            }

            MarkStarted(now);
            return new(SupervisorAction.Start, SafeMode, "Audio proces ne radi.");
        }

        if (!_wasRunning)
        {
            // prvi put ga vidimo (pokrenuli smo ga mi, ili ga preuzimamo posle pada nadzora)
            _wasRunning = true;
            if (!_startedByUs)
            {
                _lastStart = now;
                _lastResponse = now;
                _lastProgress = now;
                _healthySince = now;
                _lastFrames = -1;
                _failedPolls = 0;
                _awaitingFirstResponse = true;
            }
        }

        if (!observation.Responded)
        {
            _failedPolls++;
            var limit = _awaitingFirstResponse ? settings.StartupTimeout : settings.HeartbeatTimeout;
            if (now - _lastResponse >= limit && _failedPolls >= settings.MinFailedPolls)
            {
                return Restart(now, _awaitingFirstResponse ? "Audio proces se nije javio posle pokretanja." : "Audio proces ne odgovara.");
            }

            return new(SupervisorAction.None, SafeMode, "Ceka se odgovor.");
        }

        _failedPolls = 0;
        _awaitingFirstResponse = false;
        _lastResponse = now;
        var shouldProgress = observation.OnAir && !observation.Paused && observation.OutputPlaying;
        if (!shouldProgress || observation.FramesRendered != _lastFrames)
        {
            _lastFrames = observation.FramesRendered;
            _lastProgress = now;
        }
        else if (now - _lastProgress >= settings.StallTimeout)
        {
            return Restart(now, "Zvuk stoji iako bi trebalo da svira.");
        }

        if (SafeMode && now - _healthySince >= settings.SafeModeProbation)
        {
            SafeMode = false;
        }

        return new(SupervisorAction.None, SafeMode, "U redu.");
    }

    private SupervisorDecision Restart(TimeSpan now, string reason)
    {
        RecordCrash(now);
        MarkStarted(now);
        _wasRunning = false;
        return new(SupervisorAction.KillAndRestart, SafeMode, reason);
    }

    private void RecordCrash(TimeSpan now)
    {
        if (_crashes.RecordCrash(now))
        {
            SafeMode = true;
        }
    }

    private void MarkStarted(TimeSpan now)
    {
        _startedByUs = true;
        _lastStart = now;
        _lastResponse = now;
        _lastProgress = now;
        _healthySince = now;
        _lastFrames = -1;
        _failedPolls = 0;
        _awaitingFirstResponse = true;
    }
}

/// <summary>
/// Definicija zadatka u Task Scheduler-u (spec. 2.1). Vazno:
///  - "InteractiveToken" = "Run only when user is logged on" (inace nema zvuka);
///  - prioritet 4 umesto podrazumevanih 7; bez vremenskog ogranicenja (podrazumevano 72 h);
///  - ponavljanje na 5 minuta vraca nadzor ako nestane; nova instanca se ne pokrece ako vec radi.
/// </summary>
public static class WatchdogTask
{
    public const string TaskName = "LustreRadio Watchdog";

    public static string BuildXml(string userId, string watchdogPath)
    {
        var user = SecurityElement.Escape(userId);
        var path = SecurityElement.Escape(watchdogPath);
        var directory = SecurityElement.Escape(Path.GetDirectoryName(watchdogPath) ?? string.Empty);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>LUSTRE RADIO: pokrece i nadzire audio proces u korisnickoj sesiji.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Repetition>
                    <Interval>PT5M</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>3</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{path}</Command>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Proverava da zadatak (iz "schtasks /Query /XML") ima podesavanja koja zvuk zahteva.</summary>
    public static IReadOnlyList<string> Problems(string taskXml)
    {
        var problems = new List<string>();
        void Require(string fragment, string message)
        {
            if (!taskXml.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(message);
            }
        }

        Require("<LogonType>InteractiveToken</LogonType>", "Zadatak nije podesen na 'Run only when user is logged on'.");
        Require("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", "Zadatak ima vremensko ogranicenje.");
        Require("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", "Zadatak moze da pokrene vise instanci.");
        Require("<Priority>4</Priority>", "Zadatak nema normalan prioritet.");
        Require("<LogonTrigger>", "Zadatak se ne pokrece pri prijavi.");
        Require("<Interval>PT5M</Interval>", "Zadatak se ne ponavlja na 5 minuta.");
        if (taskXml.Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Zadatak trazi administratorska prava.");
        }

        return problems;
    }
}
