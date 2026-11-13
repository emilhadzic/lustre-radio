using System.Diagnostics;

namespace LustreRadio.Core.Time;

/// <summary>Zidni sat (lokalno vreme racunara). Koristi ga samo raspored.</summary>
public interface IClock
{
    DateTime NowLocal { get; }
}

/// <summary>Stoperica koja ne zavisi od pomeranja sistemskog sata. Koristi se za sva trajanja.</summary>
public interface IMonotonicClock
{
    TimeSpan Elapsed { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime NowLocal => DateTime.Now;
}

public sealed class SystemMonotonicClock : IMonotonicClock
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);
}

/// <summary>Sta je straza sata primetila izmedju dve provere.</summary>
public enum ClockJump
{
    None,

    /// <summary>Zidni sat je otisao unazad u odnosu na stopericu (spec. T-SCH-09).</summary>
    Backward,

    /// <summary>Zidni sat je skocio unapred vise nego sto je stvarno proslo.</summary>
    Forward,
}

/// <summary>
/// Poredi zidni sat sa stopericom i javlja kad se sistemsko vreme pomeri
/// (rucna izmena, sinhronizacija, prazna baterija sata). Promena letnjeg/zimskog
/// vremena je takodje skok od jednog sata i prijavljuje se.
/// </summary>
public sealed class ClockGuard(IClock clock, IMonotonicClock monotonic, TimeSpan tolerance)
{
    private DateTime _lastWall = clock.NowLocal;
    private TimeSpan _lastMonotonic = monotonic.Elapsed;

    /// <summary>Koliko se zidni sat pomerio mimo stvarno proteklog vremena pri poslednjoj proveri.</summary>
    public TimeSpan LastDrift { get; private set; }

    public ClockJump Check()
    {
        var wall = clock.NowLocal;
        var mono = monotonic.Elapsed;
        LastDrift = (wall - _lastWall) - (mono - _lastMonotonic);
        _lastWall = wall;
        _lastMonotonic = mono;

        if (LastDrift < -tolerance)
        {
            return ClockJump.Backward;
        }

        return LastDrift > tolerance ? ClockJump.Forward : ClockJump.None;
    }
}
