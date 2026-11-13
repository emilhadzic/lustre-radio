using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LustreRadio.Audio;
using LustreRadio.Core.Loudness;
using Microsoft.Win32;

namespace LustreRadio.Bench;

/// <summary>
/// Prenosni program za merenje: brzina dekodiranja, brzina pripreme prelaza i stvarna reprodukcija.
/// Argument --auto: bez pitanja i cekanja (za automatsku proveru).
/// </summary>
public static class Program
{
    private static readonly StringBuilder Report = new();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var auto = args.Contains("--auto");

        Line("LUSTRE RADIO Bench " + typeof(Program).Assembly.GetName().Version);
        Line("Vreme: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Line();

        Step("1. Racunar", SystemInfo);
        Step("2. Brzina dekodiranja (koliko puta brze od slusanja)", DecodeSpeed);
        Step("3. Priprema prelaza bez uredjaja", RenderSpeed);
        Step("4. Stvarna reprodukcija", () => Playback(auto));

        var path = SaveReport();
        Console.WriteLine();
        Console.WriteLine("Izvestaj je sacuvan: " + path);
        if (!auto)
        {
            Console.WriteLine("Pritisnite Enter za kraj.");
            Console.ReadLine();
        }

        return 0;
    }

    private static void Line(string text = "")
    {
        Console.WriteLine(text);
        Report.AppendLine(text);
    }

    private static void Step(string title, Action action)
    {
        Line("== " + title + " ==");
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Line("GRESKA: " + ex.GetType().Name + ": " + ex.Message);
        }

        Line();
    }

    private static void SystemInfo()
    {
        Line("Windows: " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
        string? cpu = null;
        try
        {
            cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        }
        catch (Exception)
        {
            // nije bitno
        }

        Line("Procesor: " + (cpu?.Trim() ?? "nepoznat") + ", logickih jezgara: " + Environment.ProcessorCount);
        Line($"Memorija ukupno: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024:F1} GB");

        var sw = Stopwatch.StartNew();
        double acc = 0;
        for (var i = 0; i < 20_000_000; i++)
        {
            acc += Math.Sin(i * 1e-3);
        }

        sw.Stop();
        Line($"Indeks brzine procesora (manje je brze): {sw.ElapsedMilliseconds} ms [{acc:F1}]");
    }

    private static void DecodeSpeed()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "proba");
        if (!Directory.Exists(directory))
        {
            Line("Nema foldera sa probnim fajlovima.");
            return;
        }

        var buffer = new float[8192];
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            try
            {
                const int Rounds = 10;
                long frames = 0;
                var decoder = string.Empty;
                var sw = Stopwatch.StartNew();
                for (var round = 0; round < Rounds; round++)
                {
                    using var source = Decoders.Open(file, AudioFormat.Stereo44100);
                    decoder = source.DecoderId;
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        frames += read;
                    }
                }

                sw.Stop();
                var audioSeconds = frames / 44100.0;
                Line($"{name,-28} {decoder,-10} {audioSeconds / sw.Elapsed.TotalSeconds,7:F0}x");
            }
            catch (Exception ex)
            {
                Line($"{name,-28} NE RADI: {ex.Message}");
            }
        }
    }

    private static void RenderSpeed()
    {
        var format = AudioFormat.Stereo44100;
        var index = 0;
        QueuedTrack? Next() => index < 100
            ? new QueuedTrack($"t{index}", new ToneSource(format, 200 + (10 * index++), 0.25f, format.FramesFromSeconds(10)))
            : null;

        var sequencer = new CrossfadeSequencer(format, Next, crossfadeSeconds: 3);
        var limiter = new Limiter(format);
        using var sink = new CountingSink();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var frames = OfflineRenderer.Render(sequencer, limiter, sink);
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        var seconds = frames / (double)format.SampleRate;
        Line($"100 pesama, prelazi 3 s: {seconds:F0} s zvuka za {sw.Elapsed.TotalSeconds:F2} s ({seconds / sw.Elapsed.TotalSeconds:F0}x)");
        Line($"Zauzeto memorije tokom pripreme: {allocated / 1024.0:F0} KB");
        using var process = Process.GetCurrentProcess();
        Line($"Memorija programa: radni skup {process.WorkingSet64 / 1024.0 / 1024:F0} MB, privatno {process.PrivateMemorySize64 / 1024.0 / 1024:F0} MB");
    }

    private static void Playback(bool auto)
    {
        AudioFormat format;
        try
        {
            (format, var deviceName) = ResilientOutput.QueryDefaultDevice();
            Line($"Uredjaj: {deviceName}, {format.SampleRate} Hz");
        }
        catch (Exception ex)
        {
            Line("Nema dostupnog zvucnog uredjaja: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        var musicDirectory = Path.Combine(AppContext.BaseDirectory, "muzika");
        var files = Directory.Exists(musicDirectory)
            ? Directory.EnumerateFiles(musicDirectory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        Func<QueuedTrack?> next;
        double crossfade, expectedSeconds;
        var settings = new LoudnessSettings();
        if (files.Count > 0)
        {
            double partSeconds = 25;
            if (!auto)
            {
                Console.Write("Koliko sekundi svake pesme da pustim? (Enter = 25, 0 = cele pesme): ");
                if (double.TryParse(Console.ReadLine(), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) && typed >= 0)
                {
                    partSeconds = typed;
                }
            }

            var wholeTracks = partSeconds == 0;
            crossfade = 5;
            expectedSeconds = wholeTracks ? double.NaN : (files.Count * partSeconds) - ((files.Count - 1) * crossfade);
            Line(wholeTracks
                ? $"Pustam cele pesme ({files.Count}) iz foldera 'muzika', prelazi {crossfade:F0} s."
                : $"Pustam prvih {partSeconds:F0} s svake od {files.Count} pesama iz foldera 'muzika', prelazi {crossfade:F0} s.");
            var i = 0;
            next = () =>
            {
                while (i < files.Count)
                {
                    var file = files[i++];
                    try
                    {
                        var source = Decoders.Open(file, format);
                        Line($"  {Path.GetFileName(file)}: dekoder {source.DecoderId}, {source.SourceSampleRate} Hz, {source.SourceChannels} kan.");
                        return new QueuedTrack(
                            Path.GetFileName(file),
                            wholeTracks ? source : new LimitedSource(source, format.FramesFromSeconds(partSeconds)),
                            TrackGain.ToLinear(TrackGain.ComputeDb(null, null, settings)));
                    }
                    catch (Exception ex)
                    {
                        Line($"  {Path.GetFileName(file)}: NE MOZE DA SE OTVORI: {ex.Message}");
                    }
                }

                return null;
            };
        }
        else
        {
            double[] tones = [220.00, 277.18, 329.63, 440.00, 329.63, 277.18];
            crossfade = 2;
            expectedSeconds = (tones.Length * 5) - ((tones.Length - 1) * crossfade);
            Line($"Pustam {tones.Length} tihih tonova po 5 s, prelazi {crossfade:F0} s (ukupno oko {expectedSeconds:F0} s).");
            Line("Treba da se cuje neprekidan zvuk koji se glatko pretapa iz tona u ton, bez pucketanja i bez tisine.");
            var i = 0;
            next = () => i < tones.Length
                ? new QueuedTrack($"ton {tones[i]:F0} Hz", new ToneSource(format, tones[i++], 0.1f, format.FramesFromSeconds(5)))
                : null;
        }

        var sequencer = new CrossfadeSequencer(format, next, crossfade);
        var ring = new FloatRingBuffer(65536);
        using var pump = new RenderPump(sequencer, new Limiter(format), ring);
        using var output = new ResilientOutput(ring, pump, format);
        Exception? pumpError = null;
        pump.Error += ex => pumpError = ex;
        output.StateChanged += (state, detail) => Console.WriteLine(state switch
        {
            OutputState.Playing => "  [zvuk ide na: " + detail + "]",
            OutputState.Disconnected => "  [ZVUCNI UREDJAJ NIJE POVEZAN - cekam da se vrati...]",
            _ => "  [" + state + "]",
        });

        if (!auto)
        {
            Console.WriteLine("Proba ponovnog povezivanja: dok muzika svira, izvucite slusalice ili ugasite zvucnik,");
            Console.WriteLine("sacekajte desetak sekundi i vratite ih. Muzika treba sama da nastavi.");
            Console.WriteLine("Za prekid pritisnite taster Q.");
        }

        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        pump.Start();
        output.Start();

        var finished = false;
        var interrupted = false;
        while (!finished)
        {
            finished = output.WaitUntilFinished(TimeSpan.FromMilliseconds(200));
            if (auto && sw.Elapsed.TotalSeconds > 60)
            {
                break;
            }

            if (!auto && !Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
            {
                interrupted = true;
                break;
            }
        }

        sw.Stop();
        process.Refresh();
        var cpuPercent = (process.TotalProcessorTime - cpuBefore).TotalSeconds / sw.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;

        Line($"Trajanje: {sw.Elapsed.TotalSeconds:F1} s" + (double.IsNaN(expectedSeconds) ? string.Empty : $" (plan bez zastoja: {expectedSeconds:F0} s)") +
            $"; zavrseno samo: {(finished ? "da" : interrupted ? "NE (prekinuto tasterom Q)" : "NE (isteklo vreme)")}");
        Line($"Prekidi zvuka (frejmova tisine zbog kasnjenja): {output.UnderrunFrames}");
        Line($"Ponovna povezivanja uredjaja: {output.Reconnects}");
        Line($"Procesor tokom reprodukcije: {cpuPercent:F2} % (od svih jezgara)");
        Line($"Memorija programa: radni skup {process.WorkingSet64 / 1024.0 / 1024:F0} MB, privatno {process.PrivateMemorySize64 / 1024.0 / 1024:F0} MB");
        Line("Dnevnik izlaza:");
        foreach (var entry in output.LogSnapshot())
        {
            Line("  " + entry);
        }

        if (pumpError is not null)
        {
            Line("Greska pripreme zvuka: " + pumpError);
        }

        Line("Pesme:");
        foreach (var e in sequencer.Events)
        {
            Line($"  {e.StartFrame / (double)format.SampleRate,6:F1} s  {e.Id}" +
                (e.Faulted ? "  [GRESKA CITANJA]" : string.Empty) +
                (e.EndedEarly ? "  [ZAVRSIO RANIJE]" : string.Empty) +
                (e.FailedToLoad ? "  [NIJE UCITAN]" : string.Empty));
        }

        if (!auto)
        {
            Console.Write("Da li ste culi glatke prelaze, bez pucketanja i bez tisine? (d/n): ");
            var answer = Console.ReadLine()?.Trim();
            Report.AppendLine("Ocena slusanjem (d/n): " + (string.IsNullOrEmpty(answer) ? "bez odgovora" : answer));
            Console.Write("Ako ste izvlacili slusalice ili gasili zvucnik: da li je muzika sama nastavila? (d/n, Enter = nisam probao): ");
            var reconnect = Console.ReadLine()?.Trim();
            Report.AppendLine("Ponovno povezivanje po oceni (d/n): " + (string.IsNullOrEmpty(reconnect) ? "nije probano" : reconnect));
        }
    }

    private static string SaveReport()
    {
        const string FileName = "LustreRadio-Bench-izvestaj.txt";
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                var path = Path.Combine(directory, FileName);
                File.WriteAllText(path, Report.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // probaj sledece mesto
            }
        }

        return "(nije sacuvan)";
    }

    private sealed class CountingSink : IAudioSink
    {
        public void Write(ReadOnlySpan<float> interleaved)
        {
        }

        public void Dispose()
        {
        }
    }
}
