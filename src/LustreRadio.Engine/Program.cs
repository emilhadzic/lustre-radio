using LustreRadio.Shared;

namespace LustreRadio.Engine;

/// <summary>
/// Faza M0: samo kostur. Upise jedan red u log i zavrsi.
/// Zvuk, raspored i baza dolaze u fazama M1-M3.
/// </summary>
public static class Program
{
    public static int Main()
    {
        using var log = LogSetup.Create("engine");
        log.Information(
            "LUSTRE RADIO Engine {Version}: kostur (faza M0), bez funkcija. Sesija {SessionId}.",
            typeof(Program).Assembly.GetName().Version,
            System.Diagnostics.Process.GetCurrentProcess().SessionId);
        return 0;
    }
}
