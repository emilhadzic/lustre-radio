using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LustreRadio.Shared;

namespace LustreRadio.UI;

/// <summary>
/// Faza M0: samo kostur. Prazan prozor sa nazivom; interfejs dolazi u fazi M5.
/// Argument --smoke: otvori i odmah zatvori prozor (za automatsku proveru).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var log = LogSetup.Create("ui");
        log.Information(
            "LUSTRE RADIO UI {Version}: kostur (faza M0), bez funkcija.",
            typeof(Program).Assembly.GetName().Version);

        var app = new Application();
        var window = new Window
        {
            Title = "LUSTRE RADIO",
            Width = 800,
            Height = 480,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16)),
            Content = new TextBlock
            {
                Text = "LUSTRE RADIO\nTemelj projekta (M0). Interfejs još nije napravljen.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x4B)),
                FontSize = 24,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        if (args.Contains("--smoke"))
        {
            window.Loaded += (_, _) => window.Close();
        }

        return app.Run(window);
    }
}
