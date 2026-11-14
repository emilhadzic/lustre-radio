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

    [Fact] // T-WD-02
    public void NoResponseFor30Seconds_KillsAndRestarts()
    {
        var (policy, clock) = Create();
        policy.Evaluate(Down);
        clock.Elapsed += TimeSpan.FromSeconds(5);
        policy.Evaluate(Idle);

        var frozen = new EngineObservation { ProcessRunning = true, Responded = false };
        for (var i = 0; i < 5; i++)
        {
            clock.Elapsed += TimeSpan.FromSeconds(5);
            Assert.Equal(SupervisorAction.None, policy.Evaluate(frozen).Action);
        }

        clock.Elapsed += TimeSpan.FromSeconds(5);
        Assert.Equal(SupervisorAction.KillAndRestart, policy.Evaluate(frozen).Action);
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
        clock.Elapsed += TimeSpan.FromSeconds(20);
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

    [Theory] // T-SES-01
    [InlineData(1, null, true)]
    [InlineData(2, null, true)]
    [InlineData(0, null, false)]
    [InlineData(0, "0", false)]
    [InlineData(0, "1", true)]
    public void SessionZero_IsRefused_UnlessTestOverride(int sessionId, string? variable, bool mayRun)
    {
        Assert.Equal(mayRun, SessionGuard.MayRun(sessionId, variable));
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
