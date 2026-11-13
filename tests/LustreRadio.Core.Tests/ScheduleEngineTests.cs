using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Time;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>Testovi T-SCH-01 do T-SCH-10 (spec. deo 19). Vreme je zadato, nista ne ceka pravi sat.</summary>
public class ScheduleEngineTests
{
    private static readonly WeeklySchedule Schedule = DefaultSchedule.Create();

    // 16.11.2026. je ponedeljak
    private static DateTime Mon(int hour, int minute = 0, int second = 0) => new(2026, 11, 16, hour, minute, second);

    private static SlotProgram Special(string name) => new() { Name = name, Genres = ["lounge"] };

    [Fact] // T-SCH-01
    public void At0600_Waits_Until0700()
    {
        var d = ScheduleEngine.Evaluate(Schedule, null, Mon(6));
        Assert.False(d.ShouldPlay);
        Assert.Equal(ProgramSource.Closed, d.Source);
        Assert.Equal(Mon(7), d.NextChangeLocal);
    }

    [Fact] // T-SCH-02
    public void At1000_PlaysMorningProgram()
    {
        var d = ScheduleEngine.Evaluate(Schedule, null, Mon(10));
        Assert.True(d.ShouldPlay);
        Assert.Equal(DayPart.Morning, d.Part);
        Assert.Equal("Ponedeljak - jutro", d.Program!.Name);
        Assert.Equal(ProgramSource.WeeklySlot, d.Source);
        Assert.Equal(Mon(11), d.NextChangeLocal);
        Assert.Equal(Mon(23, 30), d.ClosesAtLocal);
    }

    [Fact] // T-SCH-03
    public void At2345_DoesNotPlay_NextOpeningIsTomorrow()
    {
        var d = ScheduleEngine.Evaluate(Schedule, null, Mon(23, 45));
        Assert.False(d.ShouldPlay);
        Assert.Equal(Mon(7).AddDays(1), d.NextChangeLocal);
    }

    [Fact] // T-SCH-04
    public void ClosesExactlyAt2330()
    {
        Assert.True(ScheduleEngine.Evaluate(Schedule, null, Mon(23, 29, 59)).ShouldPlay);
        Assert.False(ScheduleEngine.Evaluate(Schedule, null, Mon(23, 30, 0)).ShouldPlay);
        Assert.True(ScheduleEngine.Evaluate(Schedule, null, Mon(7, 0, 0)).ShouldPlay);
        Assert.False(ScheduleEngine.Evaluate(Schedule, null, Mon(6, 59, 59)).ShouldPlay);
    }

    [Fact] // T-SCH-05
    public void All21Slots_ResolveToTheirOwnProgram()
    {
        string[] days = ["Ponedeljak", "Utorak", "Sreda", "Cetvrtak", "Petak", "Subota", "Nedelja"];
        (int Hour, DayPart Part, string Suffix)[] parts =
        [
            (8, DayPart.Morning, "jutro"), (12, DayPart.Afternoon, "popodne"), (18, DayPart.Evening, "vece"),
        ];
        var names = new HashSet<string>();

        for (var i = 0; i < 7; i++)
        {
            foreach (var (hour, part, suffix) in parts)
            {
                var d = ScheduleEngine.Evaluate(Schedule, null, Mon(hour).AddDays(i));
                Assert.True(d.ShouldPlay);
                Assert.Equal(part, d.Part);
                Assert.Equal($"{days[i]} - {suffix}", d.Program!.Name);
                names.Add(d.Program.Name);
            }
        }

        Assert.Equal(21, names.Count);
    }

    [Fact]
    public void PartBoundaries_SwitchExactlyOnTime()
    {
        Assert.Equal(DayPart.Morning, ScheduleEngine.Evaluate(Schedule, null, Mon(10, 59, 59)).Part);
        Assert.Equal(DayPart.Afternoon, ScheduleEngine.Evaluate(Schedule, null, Mon(11)).Part);
        Assert.Equal(DayPart.Afternoon, ScheduleEngine.Evaluate(Schedule, null, Mon(16, 59, 59)).Part);
        Assert.Equal(DayPart.Evening, ScheduleEngine.Evaluate(Schedule, null, Mon(17)).Part);
        Assert.Equal(Mon(23, 30), ScheduleEngine.Evaluate(Schedule, null, Mon(17)).NextChangeLocal);
    }

    [Fact] // T-SCH-06
    public void Holiday_TakesPriorityOverWeeklySlot()
    {
        var holiday = new DaySchedule { Morning = Special("Praznik"), Afternoon = Special("Praznik"), Evening = Special("Praznik") };
        var schedule = Schedule with
        {
            Exceptions = [new ScheduleException { Name = "Dan lokala", From = new(2026, 11, 18), To = new(2026, 11, 18), Schedule = holiday }],
        };

        var onHoliday = ScheduleEngine.Evaluate(schedule, null, Mon(12).AddDays(2));
        Assert.Equal(ProgramSource.Exception, onHoliday.Source);
        Assert.Equal("Praznik", onHoliday.Program!.Name);
        Assert.Equal("Dan lokala", onHoliday.ExceptionName);

        var dayAfter = ScheduleEngine.Evaluate(schedule, null, Mon(12).AddDays(3));
        Assert.Equal(ProgramSource.WeeklySlot, dayAfter.Source);
        Assert.Equal("Cetvrtak - popodne", dayAfter.Program!.Name);
    }

    [Fact]
    public void ClosedDay_NeverPlays_AndPointsToNextOpenDay()
    {
        var closed = new DaySchedule { Morning = Special("x"), Afternoon = Special("x"), Evening = Special("x"), Closed = true };
        var schedule = Schedule with
        {
            Exceptions = [new ScheduleException { Name = "Zatvoreno", From = new(2026, 11, 17), To = new(2026, 11, 18), Schedule = closed }],
        };

        for (var hour = 0; hour < 24; hour++)
        {
            Assert.False(ScheduleEngine.Evaluate(schedule, null, Mon(hour).AddDays(1)).ShouldPlay);
        }

        Assert.Equal(Mon(7).AddDays(3), ScheduleEngine.Evaluate(schedule, null, Mon(23, 45)).NextChangeLocal);
    }

    [Fact]
    public void OverlappingExceptions_LaterOneWins()
    {
        var a = new DaySchedule { Morning = Special("A"), Afternoon = Special("A"), Evening = Special("A") };
        var b = new DaySchedule { Morning = Special("B"), Afternoon = Special("B"), Evening = Special("B") };
        var schedule = Schedule with
        {
            Exceptions =
            [
                new ScheduleException { Name = "sezona", From = new(2026, 11, 1), To = new(2026, 11, 30), Schedule = a },
                new ScheduleException { Name = "dogadjaj", From = new(2026, 11, 16), To = new(2026, 11, 16), Schedule = b },
            ],
        };

        Assert.Equal("B", ScheduleEngine.Evaluate(schedule, null, Mon(12)).Program!.Name);
        Assert.Equal("A", ScheduleEngine.Evaluate(schedule, null, Mon(12).AddDays(1)).Program!.Name);
    }

    [Fact] // T-SCH-07
    public void Override_AppliesForItsDuration_ThenScheduleReturns()
    {
        var manual = new ManualOverride { Program = Special("Rucno"), StartsLocal = Mon(10), EndsLocal = Mon(10, 30) };

        var before = ScheduleEngine.Evaluate(Schedule, manual, Mon(9, 50));
        Assert.Equal(ProgramSource.WeeklySlot, before.Source);
        Assert.Equal(Mon(10), before.NextChangeLocal);

        var during = ScheduleEngine.Evaluate(Schedule, manual, Mon(10, 15));
        Assert.Equal(ProgramSource.Override, during.Source);
        Assert.Equal("Rucno", during.Program!.Name);
        Assert.Equal(Mon(10, 30), during.NextChangeLocal);

        var after = ScheduleEngine.Evaluate(Schedule, manual, Mon(10, 30));
        Assert.Equal(ProgramSource.WeeklySlot, after.Source);
        Assert.Equal("Ponedeljak - jutro", after.Program!.Name);
    }

    [Fact]
    public void Override_UntilNextChange_EndsWithDayPart()
    {
        var manual = ScheduleEngine.OverrideUntilNextChange(Schedule, Special("Rucno"), Mon(10, 15));
        Assert.Equal(Mon(11), manual.EndsLocal);
        Assert.Equal(ProgramSource.Override, ScheduleEngine.Evaluate(Schedule, manual, Mon(10, 59)).Source);
        Assert.Equal("Ponedeljak - popodne", ScheduleEngine.Evaluate(Schedule, manual, Mon(11)).Program!.Name);
    }

    [Fact]
    public void Override_DoesNotOpenOutsideWorkingHours_AndStopsAtClosing()
    {
        var manual = new ManualOverride { Program = Special("Rucno"), StartsLocal = Mon(22), EndsLocal = Mon(23, 59) };
        Assert.Equal(Mon(23, 30), ScheduleEngine.Evaluate(Schedule, manual, Mon(23)).NextChangeLocal);
        Assert.False(ScheduleEngine.Evaluate(Schedule, manual, Mon(23, 40)).ShouldPlay);
    }

    [Theory] // T-SCH-08: noci prelaska na letnje i zimsko vreme (Evropa 2026: 29.3. i 25.10.)
    [InlineData(2026, 3, 28)]
    [InlineData(2026, 10, 24)]
    public void DaylightSavingNights_OneStartAndOneStopPerDay(int year, int month, int day)
    {
        var start = new DateTime(year, month, day, 0, 0, 0);
        var starts = new Dictionary<DateOnly, int>();
        var stops = new Dictionary<DateOnly, int>();
        var playing = false;

        for (var minute = 0; minute < 3 * 24 * 60; minute++)
        {
            var now = start.AddMinutes(minute);
            var should = ScheduleEngine.Evaluate(Schedule, null, now).ShouldPlay;
            var date = DateOnly.FromDateTime(now);
            if (should && !playing)
            {
                starts[date] = starts.GetValueOrDefault(date) + 1;
            }
            else if (!should && playing)
            {
                stops[date] = stops.GetValueOrDefault(date) + 1;
            }

            playing = should;
        }

        Assert.Equal(3, starts.Count);
        Assert.All(starts.Values, count => Assert.Equal(1, count));
        Assert.Equal(3, stops.Count);
        Assert.All(stops.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void DefaultSchedule_IsValid_AndHasAllSevenDays()
    {
        Assert.Empty(ScheduleValidator.Validate(Schedule));
        Assert.Equal(7, Schedule.Days.Count);
    }

    [Fact]
    public void Validator_ReportsBrokenTimesAndFadeOut()
    {
        var monday = Schedule.Days[DayOfWeek.Monday];
        var broken = Schedule with
        {
            FadeOutSeconds = 5,
            Days = new Dictionary<DayOfWeek, DaySchedule>(Schedule.Days)
            {
                [DayOfWeek.Monday] = monday with { Times = monday.Times with { EveningStart = new TimeOnly(9, 0) } },
            },
        };

        var errors = ScheduleValidator.Validate(broken);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void CopyDay_CopiesWholeDay()
    {
        var copied = Schedule.WithDayCopied(DayOfWeek.Friday, DayOfWeek.Monday);
        Assert.Equal("Petak - vece", ScheduleEngine.Evaluate(copied, null, Mon(18)).Program!.Name);
        Assert.Equal("Ponedeljak - vece", ScheduleEngine.Evaluate(Schedule, null, Mon(18)).Program!.Name);
    }

    [Fact] // T-SCH-09 i T-SCH-10
    public void ClockGuard_DetectsWallClockJumps_ButNotNormalTime()
    {
        var wall = new FakeClock { NowLocal = Mon(10) };
        var mono = new FakeMonotonic();
        var guard = new ClockGuard(wall, mono, TimeSpan.FromMinutes(2));

        wall.NowLocal = wall.NowLocal.AddSeconds(30);
        mono.Elapsed += TimeSpan.FromSeconds(30);
        Assert.Equal(ClockJump.None, guard.Check());

        wall.NowLocal = wall.NowLocal.AddHours(-3); // sat vracen unazad
        mono.Elapsed += TimeSpan.FromSeconds(30);
        Assert.Equal(ClockJump.Backward, guard.Check());
        Assert.True(guard.LastDrift < TimeSpan.FromHours(-2.9));

        wall.NowLocal = wall.NowLocal.AddHours(1).AddSeconds(30); // npr. prelazak na letnje vreme
        mono.Elapsed += TimeSpan.FromSeconds(30);
        Assert.Equal(ClockJump.Forward, guard.Check());

        wall.NowLocal = wall.NowLocal.AddSeconds(30);
        mono.Elapsed += TimeSpan.FromSeconds(30);
        Assert.Equal(ClockJump.None, guard.Check());
    }

    private sealed class FakeClock : IClock
    {
        public DateTime NowLocal { get; set; }
    }

    private sealed class FakeMonotonic : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }
}
