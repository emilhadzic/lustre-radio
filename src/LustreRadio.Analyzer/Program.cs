using System.Globalization;
using LustreRadio.Analysis;
using LustreRadio.Audio;

namespace LustreRadio.Analyzer;

/// <summary>
/// Prenosni alat za analizu. Za sada meri glasnocu (LUFS) i vrh svakog fajla istim dekoderom kao radio.
/// Upotreba: LustreRadio.Analyzer.exe &lt;fajl ili folder&gt; [...]. Bez argumenata samo ispisuje uputstvo.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("LUSTRE RADIO Analyzer: LustreRadio.Analyzer.exe <fajl ili folder> [...]");
            return 0;
        }

        var files = new List<string>();
        foreach (var arg in args)
        {
            if (Directory.Exists(arg))
            {
                files.AddRange(Directory.EnumerateFiles(arg, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                files.Add(arg);
            }
        }

        var failed = 0;
        Console.WriteLine("LUFS\tvrh dBFS\tsekundi\tdekoder\tfajl");
        foreach (var file in files)
        {
            try
            {
                int rate;
                string decoder;
                using (var probe = Decoders.Open(file, AudioFormat.Stereo44100))
                {
                    rate = probe.SourceSampleRate;
                    decoder = probe.DecoderId;
                }

                using var source = Decoders.Open(file, new AudioFormat(rate, 2), decoder);
                var meter = new LoudnessMeter(rate);
                var buffer = new float[8192];
                long frames = 0;
                var peak = 0f;
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    frames += read;
                    var block = buffer.AsSpan(0, read * 2);
                    meter.Add(block);
                    foreach (var sample in block)
                    {
                        peak = Math.Max(peak, Math.Abs(sample));
                    }
                }

                var lufs = meter.IntegratedLufs();
                Console.WriteLine(string.Join('\t',
                    lufs?.ToString("F2", CultureInfo.InvariantCulture) ?? "-",
                    peak > 0 ? (20 * Math.Log10(peak)).ToString("F2", CultureInfo.InvariantCulture) : "-",
                    (frames / (double)rate).ToString("F1", CultureInfo.InvariantCulture),
                    decoder,
                    file));
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"-\t-\t-\tGRESKA\t{file}\t{ex.GetType().Name}: {ex.Message}");
            }
        }

        return failed == 0 ? 0 : 1;
    }
}
