using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using LustreRadio.Core;
using LustreRadio.Core.Supervision;
using LustreRadio.Presentation;
using LustreRadio.Shared;

namespace LustreRadio.UI;

/// <summary>
/// Interfejs. Otvara se kad ga neko pokrene; nije "uvek na vrhu" i nista ne prikazuje preko kase.
/// Muzika ne zavisi od njega: ako se zatvori ili padne, audio proces nastavlja da svira.
/// Argumenti: --smoke (otvori i zatvori), --snapshot &lt;folder&gt; (slike ekrana i provera velicine kontrola).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var log = LogSetup.Create("ui");
        try
        {
            var snapshot = Array.IndexOf(args, "--snapshot");
            if (snapshot >= 0)
            {
                var folder = snapshot + 1 < args.Length ? args[snapshot + 1] : "snapshots";
                var problems = Snapshots.Run(folder);
                foreach (var problem in problems)
                {
                    log.Error("Provera ekrana: {Problem}", problem);
                    Console.Error.WriteLine(problem);
                }

                log.Information("Slike ekrana: {Folder}; problema: {Count}", folder, problems.Count);
                return problems.Count == 0 ? 0 : 1;
            }

            var smoke = args.Contains("--smoke");
            using var instance = smoke ? null : SingleInstance.TryAcquire(InstanceNames.Ui);
            if (!smoke && instance is null)
            {
                BringExistingToFront();
                return 0;
            }

            log.Information("LUSTRE RADIO UI {Version}", typeof(Program).Assembly.GetName().Version);
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // greska u prikazu ne sme da ugasi prozor; muzika ionako ne zavisi od njega
                log.Error(e.Exception, "Greska u interfejsu.");
                e.Handled = true;
            };

            var link = new RadioLink();
            var window = new Window
            {
                Title = "LUSTRE RADIO",
                Width = 1280,
                Height = 720,
                MinWidth = 1024,
                MinHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowState = smoke ? WindowState.Normal : WindowState.Maximized,
                Background = Theme.Black,
                Content = new Shell(link, Probes.RunAsync),
            };

            if (smoke)
            {
                window.Loaded += (_, _) => window.Close();
            }
            else
            {
                link.Start();
            }

            var code = app.Run(window);
            link.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            return code;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Interfejs je stao zbog greske.");
            return 1;
        }
    }

    private static void BringExistingToFront()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id != current.Id && other.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(other.MainWindowHandle);
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}

/// <summary>Provere koje interfejs radi sam na racunaru (ekran "Provera sistema").</summary>
internal static class Probes
{
    public static Task<LocalProbes> RunAsync() => Task.Run(() => new LocalProbes
    {
        AutostartProblems = Autostart(),
        FreeDiskGb = FreeDisk(),
    });

    private static IReadOnlyList<string>? Autostart()
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "/Query", "/TN", WatchdogTask.TaskName, "/XML" })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)!;
            var xml = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000) || process.ExitCode != 0)
            {
                return null;
            }

            return WatchdogTask.Problems(xml);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? FreeDisk()
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.DefaultDataRoot);
            return root is null ? null : Math.Round(new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d, 1);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
