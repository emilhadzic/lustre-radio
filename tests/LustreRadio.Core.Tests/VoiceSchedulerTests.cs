using LustreRadio.Core.Scheduling;
using LustreRadio.Core.Voice;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>T-VOI-03 i pravila umetanja glasovnih poruka (spec. deo 15), simulacijom.</summary>
public class VoiceSchedulerTests
{
    private static readonly VoiceRules Rules = new();

    private static List<VoiceMessage> Messages(int idents, int longs)
    {
        var list = new List<VoiceMessage>();
        for (var i = 0; i < idents; i++)
        {
            list.Add(new VoiceMessage
            {
                Id = $"ident{i}", Category = VoiceCategory.Ident, Broadcastable = true, DurationSeconds = 4,
                Language = i % 2 == 0 ? VoiceLanguage.Serbian : VoiceLanguage.English,
                Voice = i % 4 < 2 ? VoiceKind.Female : VoiceKind.Male,
            });
        }

        for (var i = 0; i < longs; i++)
        {
            list.Add(new VoiceMessage
            {
                Id = $"long{i}", Category = i % 2 == 0 ? VoiceCategory.Long : VoiceCategory.Welcome, Broadcastable = true, DurationSeconds = 25,
                Language = i % 2 == 0 ? VoiceLanguage.Serbian : VoiceLanguage.English,
                Voice = i % 4 < 2 ? VoiceKind.Female : VoiceKind.Male,
            });
        }

        return list;
    }

    private sealed record Play(VoiceMessage Message, DateTime At, int BoundaryIndex);

    /// <summary>Simulira dane: granica izmedju pesama na svaka 4 minuta, 07:00-23:30.</summary>
    private static List<Play> Simulate(
        IReadOnlyList<VoiceMessage> messages, VoiceRules rules, int days, int seed = 3,
        Func<DateTime, bool>? strongPair = null, Func<DateTime, bool>? overrideActive = null, Func<VoiceMessage, bool>? fails = null)
    {
        var scheduler = new VoiceScheduler(new Random(seed));
        var plays = new List<Play>();
        var index = 0;
        for (var day = 0; day < days; day++)
        {
            var open = new DateTime(2026, 11, 16, 7, 0, 0).AddDays(day);
            var close = open.Date.AddHours(23.5);
            for (var now = open.AddMinutes(4); now < close; now = now.AddMinutes(4), index++)
            {
                var part = now.Hour < 11 ? DayPart.Morning : now.Hour < 17 ? DayPart.Afternoon : DayPart.Evening;
                var decision = scheduler.OnBoundary(
                    new VoiceBoundary
                    {
                        NowLocal = now, Part = part, OpenedAtLocal = open, ClosesAtLocal = close,
                        StrongDjPair = strongPair?.Invoke(now) ?? false,
                        OverrideActive = overrideActive?.Invoke(now) ?? false,
                    },
                    messages,
                    rules);

                if (decision.Message is { } message)
                {
                    if (fails?.Invoke(message) == true)
                    {
                        scheduler.RecordFailed(message);
                        continue;
                    }

                    scheduler.RecordPlayed(message, now, rules, part);
                    plays.Add(new Play(message, now, index));
                }
            }
        }

        return plays;
    }

    [Fact] // T-VOI-03
    public void ThirtyDays_AllRulesHold()
    {
        var plays = Simulate(Messages(idents: 12, longs: 10), Rules, days: 30);

        var idents = plays.Where(p => p.Message.Category == VoiceCategory.Ident).ToList();
        var longs = plays.Where(p => p.Message.Category != VoiceCategory.Ident).ToList();

        // kolicina po danu: ident na 45-60 min, duza na 2-3 h, u 16,5 h
        foreach (var day in plays.GroupBy(p => p.At.Date))
        {
            Assert.InRange(day.Count(p => p.Message.Category == VoiceCategory.Ident), 9, 22);
            Assert.InRange(day.Count(p => p.Message.Category != VoiceCategory.Ident), 4, 8);
        }

        for (var i = 0; i < plays.Count; i++)
        {
            var p = plays[i];
            var open = p.At.Date.AddHours(7);
            var close = p.At.Date.AddHours(23.5);
            Assert.True(p.At - open >= Rules.QuietAfterOpen, "poruka odmah posle otvaranja");
            Assert.True(close - p.At.AddSeconds(p.Message.DurationSeconds) >= Rules.QuietBeforeClose, "poruka pred zatvaranje");
            Assert.True(plays.Count(q => q.At > p.At.AddHours(-1) && q.At <= p.At) <= Rules.MaxPerHour, "vise od 2 poruke na sat");
            if (i > 0)
            {
                Assert.True(p.BoundaryIndex - plays[i - 1].BoundaryIndex >= 2, "dve poruke zaredom");
            }
        }

        // razmaci istog tipa unutar dana
        foreach (var day in idents.GroupBy(p => p.At.Date))
        {
            var times = day.Select(p => p.At).ToList();
            for (var i = 1; i < times.Count; i++)
            {
                Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMinutes(45), $"identi na {times[i] - times[i - 1]}");
            }
        }

        foreach (var day in longs.GroupBy(p => p.At.Date))
        {
            var times = day.Select(p => p.At).ToList();
            for (var i = 1; i < times.Count; i++)
            {
                Assert.True(times[i] - times[i - 1] >= TimeSpan.FromHours(2), $"duze poruke na {times[i] - times[i - 1]}");
            }
        }

        // ista poruka: 12 identa i 10 duzih je dovoljno da se zeljeni razmaci postuju
        var last = new Dictionary<string, DateTime>();
        foreach (var p in plays)
        {
            if (last.TryGetValue(p.Message.Id, out var previous))
            {
                var gap = p.Message.Category == VoiceCategory.Ident ? Rules.IdentRepeatGap : Rules.LongRepeatGap;
                Assert.True(p.At - previous >= gap, $"{p.Message.Id} ponovljena posle {p.At - previous}");
            }

            last[p.Message.Id] = p.At;
        }

        // nasumicnost: koriste se sve poruke, oba jezika i oba glasa
        Assert.Equal(22, plays.Select(p => p.Message.Id).Distinct().Count());
        var serbian = plays.Count(p => p.Message.Language == VoiceLanguage.Serbian) / (double)plays.Count;
        Assert.InRange(serbian, 0.45, 0.70);
        Assert.InRange(plays.Count(p => p.Message.Voice == VoiceKind.Female) / (double)plays.Count, 0.35, 0.65);
    }

    [Fact]
    public void FewMessages_StillPlay_NeverSameTwiceInARow()
    {
        var plays = Simulate(Messages(idents: 2, longs: 2), Rules, days: 5);

        Assert.True(plays.Count > 60);
        var identIds = plays.Where(p => p.Message.Category == VoiceCategory.Ident).Select(p => p.Message.Id).ToList();
        for (var i = 1; i < identIds.Count; i++)
        {
            Assert.NotEqual(identIds[i - 1], identIds[i]);
        }
    }

    [Fact] // T-LIC-01 za glas: neodobrena ili iskljucena poruka nikad ne ide
    public void NotBroadcastableOrDisabledMessages_AreNeverChosen()
    {
        var messages = Messages(4, 4);
        messages.AddRange(Messages(4, 4).Select(m => m with { Id = "neodobrena-" + m.Id, Broadcastable = false }));
        messages.AddRange(Messages(4, 4).Select(m => m with { Id = "iskljucena-" + m.Id, Enabled = false }));

        var plays = Simulate(messages, Rules, days: 10);

        Assert.NotEmpty(plays);
        Assert.All(plays, p => Assert.False(p.Message.Id.Contains('-')));
    }

    [Fact]
    public void OnlyUnapprovedMessages_NothingPlays()
    {
        var messages = Messages(4, 4).Select(m => m with { Broadcastable = false }).ToList();
        Assert.Empty(Simulate(messages, Rules, days: 3));
    }

    [Fact]
    public void Disabled_NothingPlays()
    {
        Assert.Empty(Simulate(Messages(4, 4), Rules with { Enabled = false }, days: 3));
    }

    [Fact] // T-VOI-02 (logika): poruka se pomera najvise dva puta zbog DJ para
    public void StrongDjPair_DefersAtMostTwice()
    {
        var scheduler = new VoiceScheduler(new Random(1));
        var open = new DateTime(2026, 11, 16, 7, 0, 0);
        var close = open.Date.AddHours(23.5);
        var messages = Messages(4, 0);
        VoiceBoundary At(DateTime now, bool strong) => new()
        {
            NowLocal = now, Part = DayPart.Morning, OpenedAtLocal = open, ClosesAtLocal = close, StrongDjPair = strong,
        };

        // prvi ident dospeva najkasnije u 08:00
        Assert.Null(scheduler.OnBoundary(At(open.AddMinutes(20), true), messages, Rules).Message);

        var first = scheduler.OnBoundary(At(open.AddMinutes(64), true), messages, Rules);
        var second = scheduler.OnBoundary(At(open.AddMinutes(68), true), messages, Rules);
        var third = scheduler.OnBoundary(At(open.AddMinutes(72), true), messages, Rules);

        Assert.True(first.Deferred);
        Assert.True(second.Deferred);
        Assert.False(third.Deferred);
        Assert.NotNull(third.Message);
    }

    [Fact]
    public void WeakPair_PlaysImmediately_WhenDue()
    {
        var plays = Simulate(Messages(8, 0), Rules, days: 1, strongPair: _ => false);
        Assert.InRange(plays.Count, 14, 22);
    }

    [Fact]
    public void DuringOverride_NoMessages_ThenResumes()
    {
        var plays = Simulate(
            Messages(8, 6), Rules, days: 2,
            overrideActive: now => now.Hour is >= 12 and < 16);

        Assert.DoesNotContain(plays, p => p.At.Hour is >= 12 and < 16);
        Assert.Contains(plays, p => p.At.Hour is >= 16 and < 18);
    }

    [Fact]
    public void AllowedParts_AreRespected()
    {
        var messages = Messages(6, 0).Select(m => m with { AllowedParts = [DayPart.Evening] }).ToList();
        var plays = Simulate(messages, Rules, days: 3);

        Assert.NotEmpty(plays);
        Assert.All(plays, p => Assert.True(p.At.Hour >= 17));
    }

    [Fact]
    public void SeasonalMessage_HasPriorityWhileValid_OncePerItsGap()
    {
        var messages = Messages(8, 8);
        messages.Add(new VoiceMessage
        {
            Id = "sezonska", Category = VoiceCategory.Seasonal, Language = VoiceLanguage.Serbian, Voice = VoiceKind.Female,
            Broadcastable = true, DurationSeconds = 20,
            ValidFrom = new DateOnly(2026, 11, 18), ValidTo = new DateOnly(2026, 11, 20),
        });

        var plays = Simulate(messages, Rules, days: 7);
        var seasonal = plays.Where(p => p.Message.Id == "sezonska").ToList();

        Assert.Equal(3, seasonal.Count);
        Assert.Equal([18, 19, 20], seasonal.Select(p => p.At.Day));

        // prva duza poruka svakog od ta tri dana je sezonska
        foreach (var day in new[] { 18, 19, 20 })
        {
            var firstLong = plays.First(p => p.At.Day == day && p.Message.Category != VoiceCategory.Ident);
            Assert.Equal("sezonska", firstLong.Message.Id);
        }
    }

    [Fact] // T-VOI-04 (logika): neispravan fajl se danas vise ne bira
    public void FailedMessage_IsSkippedForTheRestOfTheDay()
    {
        var plays = Simulate(Messages(6, 0), Rules, days: 2, fails: m => m.Id == "ident0");

        Assert.NotEmpty(plays);
        Assert.DoesNotContain(plays, p => p.Message.Id == "ident0");
    }
}
