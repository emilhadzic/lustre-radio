using System.IO;
using LustreRadio.Core;
using Serilog;
using Serilog.Core;

namespace LustreRadio.Shared;

/// <summary>
/// Zajednicko podesavanje logova za Engine, UI i Watchdog.
/// Jedan fajl po programu i danu, cuva se 14 fajlova, najvise 10 MB po fajlu.
/// </summary>
internal static class LogSetup
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static Logger Create(string processName)
    {
        var directory = ResolveLogDirectory();
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(directory, processName + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate: Template)
            .CreateLogger();
    }

    /// <summary>
    /// Prvo ProgramData; ako nalog tamo nema pravo upisa, folder korisnika; na kraju TEMP.
    /// Program ne sme da padne zato sto ne moze da pise log.
    /// </summary>
    internal static string ResolveLogDirectory()
    {
        string[] candidates =
        [
            AppPaths.SubFolder(AppPaths.DefaultDataRoot, AppPaths.Logs),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppPaths.AppFolderName,
                AppPaths.Logs),
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, $".write-test-{Environment.ProcessId}");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // probaj sledece mesto
            }
        }

        return Path.GetTempPath();
    }
}
