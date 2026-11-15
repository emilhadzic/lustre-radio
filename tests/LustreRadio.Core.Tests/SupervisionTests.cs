using LustreRadio.Core.Supervision;
using LustreRadio.Core.Time;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>Logika nadzora (T-WD-02, T-WD-03, T-SES-01, 03, 04, 05) bez pravih procesa.</summary>
public class SupervisionTests
{
    private sealed class FakeMonotonic : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private static readonly EngineObservation Down = new();
    private static readonly EngineObservation Idle = new() { ProcessRunning = true, Responded = true };

    private static EngineObservation Playing(long frames) =>
        new() { ProcessRunning = true, Responded = true, OnAir = true, OutputPlaying = true, FramesRendered = frames };

    private static (SupervisorPolicy Policy, FakeMonotonic Clock) Create()
    {
        var clock = new FakeMonotonic { Elapsed = TimeSpan.FromHours(1) };
        return (new SupervisorPolicy(clock, new SupervisorSettings()), clock);
    }

    [Fact]
    public void EngineNotRunning_IsStarted_Once_ThenLeftAlone()
    {
        var (policy, clock) = Create();

        Assert.Equal(SupervisorAction.Start, policy.Evaluate(Down).Action);

        // proces jos nije stigao da se pojavi: ne pokreci drugi
        clock.Elapsed += TimeSpan.FromSeconds(1);
        Assert.Equal(SupervisorAction.None, policy.Evaluate(Down).Action);

        clock.Elapsed += TimeSpan.FromSeconds(1);
        Assert.Equal(SupervisorAction.None, policy.Evaluate(Idle).Action);
        for (var i = 0; i < 100; i++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(5);
            Assert.Equal(SupervisorAction.None, policy.Evaluate(Idle).Action);
        }
    }

    [Fact] // T-WD-01 (logika)
    public void EngineCrashes_IsRestartedAfterShortDelay()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        policy.Evaluate(Playing(1000));

        clock.Elapsed += TimeSpan.FromMinutes(20);
        policy.Evaluate(Playing(2000));
        clock.Elapsed += TimeSpan.FromSeconds(5);

        var decision = policy.Evaluate(Down);
        Assert.Equal(SupervisorAction.Start, decision.Action);
        Assert.False(decision.SafeMode);
    }

    private static readonly EngineObservation Frozen = new() { ProcessRunning = true, Responded = false };

    /// <summary>
    /// Simulira petlju nadzora: provera pocinje na svake 2 s; uspesna zavrsava odmah,
    /// neuspesna posle <paramref name="failedPollSeconds"/>. Vraca trenutak odluke o prekidu, ili null.
    /// </summary>
    private static TimeSpan? RunLoop(SupervisorPolicy policy, FakeMonotonic clock, TimeSpan origin, double seconds, Func<TimeSpan, bool> responds, double failedPollSeconds = 1.5)
    {
        var interval = new SupervisorSettings().PollInterval;
        for (var start = clock.Elapsed - origin; start.TotalSeconds < seconds; start += interval)
        {
            var ok = responds(start);
            clock.Elapsed = origin + start + (ok ? TimeSpan.Zero : TimeSpan.FromSeconds(failedPollSeconds));
            var decision = policy.Evaluate(ok ? Idle : Frozen);
            if (decision.Action == SupervisorAction.KillAndRestart)
            {
                return clock.Elapsed - origin;
            }

            Assert.Equal(SupervisorAction.None, decision.Action);
        }

        return null;
    }

    private static (SupervisorPolicy Policy, FakeMonotonic Clock, TimeSpan Origin) CreateRunning()
    {
        var (policy, clock) = Create();
        var origin = clock.Elapsed;
        Assert.Equal(SupervisorAction.None, policy.Evaluate(Idle).Action); // preuzet proces koji odgovara
        return (policy, clock, origin);
    }

    public static IEnumerable<object[]> FreezeMoments =>
        from tenth in Enumerable.Range(0, 20)
        from pollSeconds in new[] { 0.0, 0.7, 1.5 }
        select new object[] { 100 + (tenth / 10.0), pollSeconds };

    [Theory] // T-WD-02: rok od 30 s, za svaki trenutak zamrzavanja unutar ciklusa i svako trajanje neuspele provere
    [MemberData(nameof(FreezeMoments))]
    public void FrozenEngine_IsDetectedWithin24Seconds_ButNeverBefore20SecondsOfSilence(double freezeAt, double failedPollSeconds)
    {
        var (policy, clock, origin) = CreateRunning();
        var freeze = TimeSpan.FromSeconds(freezeAt);
        var lastAnswer = TimeSpan.Zero;

        var detected = RunLoop(policy, clock, origin, 200, start =>
        {
            if (start < freeze)
            {
                lastAnswer = start;
                return true;
            }

            return false;
        }, failedPollSeconds);

        Assert.NotNull(detected);
        var sinceFreeze = (detected.Value - freeze).TotalSeconds;
        var sinceLastAnswer = (detected.Value - lastAnswer).TotalSeconds;
        Assert.True(sinceFreeze <= 24.0, $"otkriveno {sinceFreeze:F1} s posle zamrzavanja");
        Assert.True(sinceLastAnswer >= 20.0, $"prekinut posle samo {sinceLastAnswer:F1} s cutanja");
    }

    [Theory] // kratko cutanje nije kvar
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(17)]
    public void ShortSilence_IsNotAFault_AndCountingStartsOverAfterAnAnswer(double silentSeconds)
    {
        var (policy, clock, origin) = CreateRunning();
        var detected = RunLoop(policy, clock, origin, 600, start =>
        {
            var phase = start.TotalSeconds % 40; // svakih 40 s po jedno cutanje
            return phase >= silentSeconds;
        });

        Assert.Null(detected);
    }

    [Fact] // opterecen racunar: odgovori stizu samo povremeno
    public void IntermittentAnswers_NeverCauseAKill()
    {
        var (policy, clock, origin) = CreateRunning();
        var n = 0;
        Assert.Null(RunLoop(policy, clock, origin, 3600, _ => n++ % 4 == 0)); // tri od cetiri provere ne uspeju
    }

    [Fact] // i dugo cutanje nije kvar dok nema 5 uzastopnih neuspelih provera
    public void TwentySecondsOfSilence_WithFewerThanFiveFailedPolls_IsNotAFault()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Idle);
        for (var i = 0; i < 4; i++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(5.5); // provere razvucene, ukupno 22 s
            Assert.Equal(SupervisorAction.None, policy.Evaluate(Frozen).Action);
        }

        clock.Elapsed += TimeSpan.FromSeconds(5.5);
        Assert.Equal(SupervisorAction.KillAndRestart, policy.Evaluate(Frozen).Action);
    }

    [Theory] // budjenje racunara ili zauzet sistem: dok nadzor sam nije radio, vreme se ne racuna
    [InlineData(7)]
    [InlineData(40)]
    [InlineData(3600)]
    public void WhenWatchdogItselfWasStalled_HealthyEngineIsNotKilled(double stalledSeconds)
    {
        var (policy, clock, origin) = CreateRunning();
        Assert.Null(RunLoop(policy, clock, origin, 20, _ => true));

        // nadzor (i ceo racunar) stoji; po povratku audio procesu treba jos 10 s da proradi
        clock.Elapsed += TimeSpan.FromSeconds(stalledSeconds);
        var resumed = clock.Elapsed - origin;
        Assert.Null(RunLoop(policy, clock, origin, resumed.TotalSeconds + 120, start => start >= resumed + TimeSpan.FromSeconds(10)));
    }

    [Fact] // ...ali zaista zamrznut proces se i posle takvog zastoja otkriva
    public void AfterWatchdogStall_FrozenEngineIsStillDetected()
    {
        var (policy, clock, origin) = CreateRunning();
        clock.Elapsed += TimeSpan.FromSeconds(40);
        var resumed = clock.Elapsed - origin;

        var detected = RunLoop(policy, clock, origin, resumed.TotalSeconds + 120, _ => false);

        Assert.NotNull(detected);
        Assert.InRange((detected.Value - resumed).TotalSeconds, 20.0, 26.0);
    }

    [Fact] // pokretanje na sporom racunaru: prvi odgovor se ceka do 60 s
    public void AfterStart_FirstAnswerMayTakeUpToAMinute()
    {
        var (policy, clock) = Create();
        Assert.Equal(SupervisorAction.Start, policy.Evaluate(Down).Action);
        var origin = clock.Elapsed;

        var detected = RunLoop(policy, clock, origin, 300, _ => false);

        Assert.NotNull(detected);
        Assert.InRange(detected.Value.TotalSeconds, 60.0, 64.0);
    }

    [Fact]
    public void AfterStart_OnceEngineAnswers_NormalLimitApplies()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);
        var origin = clock.Elapsed;

        var detected = RunLoop(policy, clock, origin, 300, start => start.TotalSeconds is >= 30 and < 40);

        Assert.NotNull(detected);
        Assert.InRange(detected.Value.TotalSeconds, 58.0, 64.0); // poslednji odgovor u 38. s + 20 s + do 3,5 s
    }

    [Fact] // T-WD-02
    public void AudioStalledWhileOnAir_KillsAndRestarts()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        policy.Evaluate(Playing(1000));

        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Equal(SupervisorAction.None, policy.Evaluate(Playing(1000)).Action);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Equal(SupervisorAction.None, policy.Evaluate(Playing(1000)).Action);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Equal(SupervisorAction.KillAndRestart, policy.Evaluate(Playing(1000)).Action);
    }

    [Fact]
    public void Pause_Idle_AndDisconnectedSpeaker_AreNotFaults()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        policy.Evaluate(Playing(1000));

        EngineObservation[] harmless =
        [
            Playing(1000) with { Paused = true },
            Playing(1000) with { OutputPlaying = false },   // npr. Bluetooth zvucnik ugasen
            Idle,                                           // van radnog vremena
        ];

        foreach (var observation in harmless)
        {
            for (var i = 0; i < 200; i++)
            {
                clock.Elapsed += TimeSpan.FromSeconds(5);
                Assert.Equal(SupervisorAction.None, policy.Evaluate(observation).Action);
            }
        }

        // posle pauze brojac ponovo mora da raste
        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Equal(SupervisorAction.None, policy.Evaluate(Playing(1000)).Action);
        for (var i = 0; i < 7; i++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(2);
            Assert.Equal(SupervisorAction.None, policy.Evaluate(Playing(1000)).Action);
        }

        clock.Elapsed += TimeSpan.FromSeconds(2);
        Assert.Equal(SupervisorAction.KillAndRestart, policy.Evaluate(Playing(1000)).Action);
    }

    [Fact] // T-SES-05
    public void StopRequested_KillsWithoutRestart_UntilCleared()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        policy.Evaluate(Idle);

        Assert.Equal(SupervisorAction.Kill, policy.Evaluate(Idle with { StopRequested = true }).Action);
        for (var i = 0; i < 50; i++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(5);
            var decision = policy.Evaluate(Down with { StopRequested = true });
            Assert.Equal(SupervisorAction.None, decision.Action);
            Assert.False(decision.SafeMode);
        }

        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Equal(SupervisorAction.Start, policy.Evaluate(Down).Action);
    }

    [Fact] // T-SES-04: novi nadzor preuzima audio proces koji vec radi
    public void RunningEngine_IsAdopted_NotRestarted()
    {
        var (policy, clock) = Create();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(SupervisorAction.None, policy.Evaluate(Playing(1000 + i)).Action);
            clock.Elapsed += TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public void ThreeCrashesInTenMinutes_SwitchToSafeMode_WhichEndsAfterStableRun()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);

        for (var crash = 0; crash < 3; crash++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(10);
            policy.Evaluate(Idle);
            clock.Elapsed += TimeSpan.FromSeconds(10);
            var decision = policy.Evaluate(Down);
            Assert.Equal(SupervisorAction.Start, decision.Action);
            Assert.Equal(crash == 2, decision.SafeMode);
        }

        Assert.True(policy.SafeMode);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        policy.Evaluate(Idle);
        clock.Elapsed += TimeSpan.FromMinutes(9);
        Assert.True(policy.Evaluate(Idle).SafeMode);
        clock.Elapsed += TimeSpan.FromMinutes(2);
        Assert.False(policy.Evaluate(Idle).SafeMode);
    }

    [Theory] // T-SES-01: sesija 0 se odbija uvek; ne postoji prekidac koji to menja
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void SessionZero_IsAlwaysRefused(int sessionId, bool mayRun)
    {
        Assert.Equal(mayRun, SessionGuard.MayRun(sessionId));
    }

    [Fact] // T-WD-03 (osnova)
    public void SingleInstance_SecondAcquireFails_UntilFirstIsReleased()
    {
        var name = "LustreRadio.Test." + Guid.NewGuid().ToString("N");
        SingleInstance? second = null;
        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);

        // druga "instanca" na drugoj niti (isti proces sme da udje ponovo samo sa iste niti)
        var thread = new Thread(() => second = SingleInstance.TryAcquire(name));
        thread.Start();
        thread.Join();
        Assert.Null(second);

        var held = false;
        thread = new Thread(() => held = SingleInstance.IsHeld(name));
        thread.Start();
        thread.Join();
        Assert.True(held);

        first.Dispose();
        thread = new Thread(() =>
        {
            second = SingleInstance.TryAcquire(name);
            second?.Dispose();
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(second);
    }

    [Fact] // T-SES-05
    public void StopFlag_IsValidOnlyUntilNextBoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lustre-flag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var boot = new DateTime(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc);
            Assert.False(StopFlag.IsSet(directory, boot));

            StopFlag.Set(directory, boot);
            Assert.True(StopFlag.IsSet(directory, boot));
            Assert.True(StopFlag.IsSet(directory, boot.AddSeconds(30)));   // isto paljenje, sitna razlika u racunanju
            Assert.False(StopFlag.IsSet(directory, boot.AddHours(5)));     // racunar je u medjuvremenu ponovo upaljen

            StopFlag.Clear(directory);
            Assert.False(StopFlag.IsSet(directory, boot));

            File.WriteAllText(StopFlag.PathIn(directory), "smece");
            Assert.False(StopFlag.IsSet(directory, boot));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact] // T-SES-03
    public void TaskDefinition_HasSettingsThatAudioRequires()
    {
        var xml = WatchdogTask.BuildXml(@"KAFIC\radio", @"C:\Program Files\Lustre Radio\LustreRadio.Watchdog.exe");

        Assert.Empty(WatchdogTask.Problems(xml));
        Assert.Contains(@"<Command>C:\Program Files\Lustre Radio\LustreRadio.Watchdog.exe</Command>", xml);
        Assert.Contains(@"<WorkingDirectory>C:\Program Files\Lustre Radio</WorkingDirectory>", xml);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        System.Xml.Linq.XDocument.Parse(xml.Replace("encoding=\"UTF-16\"", "encoding=\"utf-8\""));

        // podrazumevana podesavanja Task Scheduler-a bi bila pogresna za zvuk
        var bad = xml.Replace("InteractiveToken", "Password").Replace("PT0S", "PT72H").Replace("<Priority>4", "<Priority>7");
        Assert.Equal(3, WatchdogTask.Problems(bad).Count);
        Assert.Contains("&amp;", WatchdogTask.BuildXml("a&b", @"C:\x\w.exe"));
    }
}
