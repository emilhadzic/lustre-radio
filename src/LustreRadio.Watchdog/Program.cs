using LustreRadio.Shared;

namespace LustreRadio.Watchdog;

/// <summary>
/// Faza M0: samo kostur. Upise jedan red u log i zavrsi.
/// Nadzor dolazi u fazi M4.
/// </summary>
public static class Program
{
    public static int Main()
    {
        using var log = LogSetup.Create("watchdog");
        log.Information(
            "LUSTRE RADIO Watchdog {Version}: kostur (faza M0), bez funkcija. Sesija {SessionId}.",
            typeof(Program).Assembly.GetName().Version,
            System.Diagnostics.Process.GetCurrentProcess().SessionId);
        return 0;
    }
}
