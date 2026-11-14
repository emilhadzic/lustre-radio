using LustreRadio.Core.Security;
using LustreRadio.Core.Time;
using Xunit;

namespace LustreRadio.Core.Tests;

/// <summary>T-UI-02 na nivou logike: PIN, zakljucavanje, istek sesije.</summary>
public class AdminAccessTests
{
    private sealed class FakeMonotonic : IMonotonicClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_MustBeFourToEightDigits(string? pin, bool valid)
    {
        Assert.Equal(valid, PinVault.IsValidPin(pin));
    }

    [Fact]
    public void Pin_IsStoredAsSaltedHash_NeverAsText()
    {
        var first = PinVault.Hash("4821");
        var second = PinVault.Hash("4821");

        Assert.DoesNotContain("4821", first);
        Assert.NotEqual(first, second); // razlicita so
        Assert.True(PinVault.Verify("4821", first));
        Assert.True(PinVault.Verify("4821", second));
        Assert.False(PinVault.Verify("4822", first));
        Assert.False(PinVault.Verify("", first));
        Assert.False(PinVault.Verify("4821", "nije-hes"));
        Assert.False(PinVault.Verify("4821", null));
        Assert.Throws<ArgumentException>(() => PinVault.Hash("12"));
    }

    [Fact]
    public void Login_WorksWithCorrectPin_AndSessionExpiresAfterThreeIdleMinutes()
    {
        var clock = new FakeMonotonic();
        var hash = PinVault.Hash("4821");
        var sessions = new AdminSessions(clock, () => hash);

        var login = sessions.Login("4821");
        Assert.Equal(LoginOutcome.Ok, login.Outcome);
        Assert.True(sessions.Validate(login.Token));
        Assert.False(sessions.Validate("izmisljen-token"));
        Assert.False(sessions.Validate(null));

        // aktivnost produzava sesiju
        clock.Elapsed += TimeSpan.FromMinutes(2);
        Assert.True(sessions.Validate(login.Token));
        clock.Elapsed += TimeSpan.FromMinutes(2);
        Assert.True(sessions.Validate(login.Token));

        // tri minuta bez aktivnosti
        clock.Elapsed += TimeSpan.FromMinutes(3);
        Assert.False(sessions.Validate(login.Token));

        var again = sessions.Login("4821");
        sessions.Logout(again.Token);
        Assert.False(sessions.Validate(again.Token));
    }

    [Fact]
    public void FiveWrongPins_LockInputForFiveMinutes_EvenForCorrectPin()
    {
        var clock = new FakeMonotonic();
        var hash = PinVault.Hash("4821");
        var sessions = new AdminSessions(clock, () => hash);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(LoginOutcome.WrongPin, sessions.Login("0000").Outcome);
        }

        var fifth = sessions.Login("0000");
        Assert.Equal(LoginOutcome.LockedOut, fifth.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(5), fifth.RetryAfter);

        clock.Elapsed += TimeSpan.FromMinutes(4);
        var during = sessions.Login("4821");
        Assert.Equal(LoginOutcome.LockedOut, during.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), during.RetryAfter);

        clock.Elapsed += TimeSpan.FromMinutes(1);
        Assert.Equal(LoginOutcome.Ok, sessions.Login("4821").Outcome);
    }

    [Fact]
    public void CorrectPin_ResetsFailureCounter_AndNoPinMeansNoLogin()
    {
        var clock = new FakeMonotonic();
        string? hash = null;
        var sessions = new AdminSessions(clock, () => hash);

        Assert.Equal(LoginOutcome.NoPinSet, sessions.Login("4821").Outcome);

        hash = PinVault.Hash("4821");
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(LoginOutcome.WrongPin, sessions.Login("1111").Outcome);
            }

            Assert.Equal(LoginOutcome.Ok, sessions.Login("4821").Outcome);
        }

        var token = sessions.Login("4821").Token;
        sessions.InvalidateAll();
        Assert.False(sessions.Validate(token));
    }
}
