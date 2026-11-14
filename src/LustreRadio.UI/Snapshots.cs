using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LustreRadio.Ipc;
using LustreRadio.Presentation;

namespace LustreRadio.UI;

/// <summary>
/// Crta svaki ekran u sliku (sa izmisljenim stanjem, bez audio procesa) i proverava ono sto moze
/// automatski: svaka kontrola je najmanje 56 x 56 px i cela staje na ekran (deo T-TCH-01).
/// Ovo NIJE proba prstom na pravom monitoru.
/// </summary>
internal static class Snapshots
{
    private static readonly (int Width, int Height)[] Sizes = [(1024, 600), (1366, 768)];

    public static List<string> Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var problems = new List<string>();
        var now = DateTime.Now;
        var playing = new RadioState
        {
            OnAir = true,
            TrackId = "1",
            Title = "Midnight in Novi Pazar",
            Artist = "Lustre House Band",
            PositionSeconds = 83,
            DurationSeconds = 247,
            Program = "Deep House",
            DayPart = "Evening",
            Volume = 0.6,
            VolumeMin = 0.2,
            VolumeMax = 0.9,
            Output = "Playing",
            Device = "Bluetooth zvucnik",
            BroadcastableTracks = 412,
            TotalTracks = 430,
            PinIsSet = true,
            AsOfUtc = DateTime.UtcNow,
            NowLocal = now.Date.AddHours(20).AddMinutes(18),
            ClosesAtLocal = now.Date.AddHours(23).AddMinutes(30),
            MusicFolders = [@"D:\Muzika\Jutro", @"D:\Muzika\Popodne", @"D:\Muzika\Vece"],
            EngineVersion = "0.1.0",
            LicenseOrganizations = "SOKOJ, OFPS",
            LicenseDeclaredLocal = now.Date.AddHours(7).AddMinutes(33),
        };
        var english = playing with { StaffLanguage = "en", AdminLanguage = "en", Paused = true };
        var closed = playing with { OnAir = false, TrackId = null, ClosesAtLocal = null, OpensAtLocal = now.Date.AddDays(1).AddHours(7) };
        var fresh = playing with { PinIsSet = false, LicenseOrganizations = null, LicenseDeclaredLocal = null, MusicFolders = [], TotalTracks = 0, BroadcastableTracks = 0, OnAir = false, TrackId = null, ClosesAtLocal = null };
        var probes = new LocalProbes { AutostartProblems = [], FreeDiskGb = 71.4 };

        var scenes = new (string Name, RadioState? State, LinkStatus Link, Action<Shell>? Enter)[]
        {
            ("01-zaposleni-svira", playing, LinkStatus.Connected, null),
            ("02-zaposleni-pauza-en", english, LinkStatus.Connected, null),
            ("03-zaposleni-zatvoreno", closed, LinkStatus.Connected, null),
            ("04-zaposleni-nema-veze", playing, LinkStatus.Connecting, null),
            ("05-zaposleni-prvi-put", null, LinkStatus.Connecting, null),
            ("06-pin", playing, LinkStatus.Connected, s => s.OpenAdmin()),
            ("07-pin-prvi-put", fresh, LinkStatus.Connected, s => s.OpenAdmin()),
            ("08-admin-muzika", playing, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.Library)),
            ("09-admin-muzika-prazno", fresh, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.Library)),
            ("10-admin-licenca", fresh, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.License)),
            ("11-admin-glasnoca", playing, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.Volume)),
            ("12-admin-jezik-en", english, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.Language)),
            ("13-admin-pin", playing, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.Pin)),
            ("14-admin-provera", playing, LinkStatus.Connected, s => s.ShowAdmin(AdminSection.System)),
        };

        foreach (var (width, height) in Sizes)
        {
            foreach (var scene in scenes)
            {
                var radio = new FixedRadio(scene.State, scene.Link);
                var shell = new Shell(radio, () => Task.FromResult(probes));
                shell.SetProbes(probes);
                scene.Enter?.Invoke(shell);
                var name = $"{scene.Name}-{width}x{height}";
                for (var pass = 0; pass < 3; pass++)
                {
                    shell.Refresh();
                    shell.Measure(new Size(width, height));
                    shell.Arrange(new Rect(0, 0, width, height));
                    shell.UpdateLayout();
                }

                Check(shell, shell, name, insideScroll: false, problems);

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(shell);
                Save(new PngBitmapEncoder(), bitmap, Path.Combine(folder, name + ".png"));
                Save(new JpegBitmapEncoder { QualityLevel = 80 }, bitmap, Path.Combine(folder, name + ".jpg"));
            }
        }

        return problems;
    }

    private static void Save(BitmapEncoder encoder, BitmapSource bitmap, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Check(Shell root, DependencyObject node, string scene, bool insideScroll, List<string> problems)
    {
        if (node is UIElement { Visibility: not Visibility.Visible })
        {
            return;
        }

        if (node is FrameworkElement element and (Button or LevelBar))
        {
            var what = element is Button { Content: TextBlock label } ? $"dugme \"{label.Text}\"" : element.GetType().Name;
            if (element.ActualWidth < Theme.Touch - 0.5 || element.ActualHeight < Theme.Touch - 0.5)
            {
                problems.Add($"{scene}: {what} je {element.ActualWidth:0}x{element.ActualHeight:0} px (najmanje {Theme.Touch:0})");
            }

            if (element is Button { Content: TextBlock { Text.Length: 0 } })
            {
                problems.Add($"{scene}: dugme bez teksta");
            }

            if (!insideScroll)
            {
                var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > root.ActualWidth + 0.5 || bounds.Bottom > root.ActualHeight + 0.5)
                {
                    problems.Add($"{scene}: {what} ne staje na ekran ({bounds.Left:0},{bounds.Top:0} – {bounds.Right:0},{bounds.Bottom:0})");
                }
            }
        }

        var scroll = insideScroll || node is ScrollViewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            Check(root, VisualTreeHelper.GetChild(node, i), scene, scroll, problems);
        }
    }

    /// <summary>Nepromenljivo stanje za crtanje; komande se ne salju nikome.</summary>
    private sealed class FixedRadio(RadioState? state, LinkStatus status) : IRadio
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public RadioState? State { get; } = state;

        public LinkStatus Status { get; } = status;

        public string? AdminSession { get; set; } = "slika";

        public Task<IpcMessage?> SendAsync(string name, Dictionary<string, string>? args = null, bool admin = false) =>
            Task.FromResult<IpcMessage?>(null);
    }
}
