using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LustreRadio.Audio;
using LustreRadio.Core.Diagnostics;
using LustreRadio.Core.Licensing;
using LustreRadio.Core.Time;
using LustreRadio.Data;
using LustreRadio.Playout;
using Microsoft.Win32;

namespace LustreRadio.Sim;

/// <summary>
/// Simulacija slabog racunara (G-sim). Pokrece ISTI kod radija kao Engine (reditelj, prelazi, limiter,
/// nit za pripremu zvuka), ali sa laznim zvucnim uredjajem koji trosi zvuk stvarnom brzinom.
/// Rezultat je uvek "G-sim": NIJE test na Celeronu N4000 niti sa Bluetooth zvucnikom.
///
/// Argumenti:
///   --all --seconds N --out folder     pokrece sve scenarije, svaki u svom procesu sa ogranicenjima
///   --child --scenario ime --seconds N jedan scenario (interno)
///   --burn                             trosi sve procesore (opterecenje, interno)
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--burn"))
            {
                Burn();
                return 0;
            }

            var seconds = int.Parse(Arg(args, "--seconds") ?? "180", CultureInfo.InvariantCulture);
            if (args.Contains("--child"))
            {
                return Child.Run(Arg(args, "--scenario") ?? "bez-ogranicenja", seconds);
            }

            if (args.Contains("--long"))
            {
                return Launcher.RunLong(seconds, Arg(args, "--out") ?? "sim-rezultati");
            }

            return Launcher.Run(seconds, Arg(args, "--out") ?? "sim-rezultati");
        }
        catch (Exception ex)
        {
            Console.WriteLine("SIM-GRESKA " + ex);
            return 2;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Burn()
    {
        for (var i = 0; i < Environment.ProcessorCount; i++)
        {
            new Thread(() =>
            {
                var x = 0.0;
                while (true)
                {
                    x = Math.Sqrt(x + 1.0);
                }
            })
            { IsBackground = false }.Start();
        }
    }
}

/// <summary>Scenario simulacije. Faktor = udeo jednog jezgra ovog racunara koji odgovara jednom jezgru N4000 (procena).</summary>
internal sealed record Scenario(string Name, double? CpuFactor, bool TwoCores, bool Load, long? MemoryLimitBytes, string Description, int TrackSeconds = 45, int TrackCount = 10)
{
    // PassMark po niti: N4000 oko 1.037 (cpubenchmark.net). GitHub racunar nije izmeren istim merilom;
    // pretpostavka je oko 2.600, pa je faktor oko 0,4. To je PROCENA; model procesora se upisuje u rezultat.
    public const double EstimatedN4000Factor = 0.4;

    public static readonly Scenario[] All =
    [
        new("bez-ogranicenja", null, false, false, null, "GitHub racunar bez ogranicenja"),
        new("procena-n4000", EstimatedN4000Factor, true, false, 256L << 20, "2 jezgra, procesor ogranicen na procenu N4000, memorija 256 MB"),
        new("duplo-strozije", EstimatedN4000Factor / 2, true, false, 256L << 20, "2 jezgra, polovina procene N4000, memorija 256 MB"),
        new("procena-n4000-opterecen", EstimatedN4000Factor, true, true, 256L << 20, "kao procena N4000, uz programe koji trose sve procesore"),
    ];

    /// <summary>B6: jedna duza simulacija sa opterecenjem i brojanjem pretapanja (kratke pesme, mnogo prelaza).</summary>
    public static readonly Scenario Long = new("dugo-opterecen", EstimatedN4000Factor, true, true, 256L << 20,
        "kao procena N4000, uz programe koji trose sve procesore; pesme od 12 s, pretapanje 6 s", TrackSeconds: 12, TrackCount: 20);

}

/// <summary>Rezultat jednog scenarija.</summary>
internal sealed class SimResult
{
    public string Scenario { get; set; } = "";
    public string Location { get; set; } = "G-sim";
    public int Seconds { get; set; }
    public string CpuModel { get; set; } = "";
    public int Processors { get; set; }
    public double? CpuCapPercentOfMachine { get; set; }
    public int TracksStarted { get; set; }
    public long DeviceUnderrunEvents { get; set; }
    public long DeviceUnderrunFrames { get; set; }
    public long RenderSteps { get; set; }
    public double RenderMaxStepMs { get; set; }
    public long RenderStepsOver5ms { get; set; }
    public long RenderStepsOver20ms { get; set; }
    public long RenderStepsOver50ms { get; set; }
    public long RenderAllocatedBytes { get; set; }
    public long RenderStepsWithAllocation { get; set; }
    public long GuardFileAccess { get; set; }
    public long GuardDecode { get; set; }
    public long GuardDatabase { get; set; }
    public long GuardLog { get; set; }
    public long GuardLock { get; set; }
    public double RingMinFillSeconds { get; set; }
    public double PrivateMemoryPeakMb { get; set; }
    public int Gc0 { get; set; }
    public int Gc1 { get; set; }
    public int Gc2 { get; set; }
    public double CpuSeconds { get; set; }

    /// <summary>Izmerena brzina jedne niti posle ogranicenja, u odnosu na brzinu pre ogranicenja (1,0 = bez promene).</summary>
    public double MeasuredSpeedRatio { get; set; }
    public string? Error { get; set; }
}

internal static class Child
{
    public static int Run(string scenarioName, int seconds)
    {
        var root = Path.Combine(Path.GetTempPath(), "lustre-sim-" + Guid.NewGuid().ToString("N"));
        var music = Path.Combine(root, "muzika");
        var data = Path.Combine(root, "podaci");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(data);
        var result = new SimResult { Scenario = scenarioName, Seconds = seconds, Processors = Environment.ProcessorCount, CpuModel = CpuModel() };

        try
        {
            var scenario = Scenario.All.Append(Scenario.Long).FirstOrDefault(x => x.Name == scenarioName) ?? Scenario.All[0];
            Library.Generate(music, count: scenario.TrackCount, seconds: scenario.TrackSeconds);
            using var db = LustreDatabase.Open(Path.Combine(data, "lustre.db"));
            LibraryIndexer.Run(db, [music]);
            db.Settings.SetJson(LibraryIndexer.CollectiveLicenseKey, new CollectiveLicense { Organizations = "SIM", DeclaredBy = "simulacija", DeclaredAt = DateTimeOffset.Now });
            LibraryIndexer.ApplyCollectiveLicense(db);

            var format = AudioFormat.Stereo48000;
            var director = new RadioDirector(db, new SystemClock(), format, new Random(7));
            director.Schedule = EngineHost.AlwaysOpenSchedule(director.Schedule);
            var started = 0;
            director.TrackStarted += _ => Interlocked.Increment(ref started);
            if (director.BroadcastableCount == 0)
            {
                throw new InvalidOperationException("U simulaciji nema pesama koje smeju da se puste.");
            }

            var before = Calibrate();
            Console.WriteLine("READY");
            if (Console.ReadLine() != "GO")
            {
                throw new InvalidOperationException("Nije stigao znak za pocetak.");
            }

            // provera da je ogranicenje stvarno primenjeno: ista racunska petlja pre i posle
            result.MeasuredSpeedRatio = Math.Round(Calibrate() / (double)before, 3);

            var session = new RadioSession(director, format, realtime: true);
            var ring = new FloatRingBuffer(65536);
            var volume = new VolumeControl(new PauseGate(session.Output), 1.0);
            using var pump = new RenderPump(volume, new Limiter(format), ring);
            var device = new FakeDevice(ring, pump, format);
            using var process = Process.GetCurrentProcess();
            var peak = 0L;

            pump.Start();
            device.Start();
            var clock = Stopwatch.StartNew();
            var tick = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(1000);
                tick++;
                session.Tick(saveSnapshot: tick % 5 == 0);
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
            }

            device.Stop();
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"G-SIM-DECK | {result.Scenario} | pesama {session.Sequencer.Events.Count} | prazan bafer decka {session.Sequencer.StarvedFrames / (double)format.SampleRate:F2} s | najniza popunjenost decka koji svira {(session.Coordinator.MinMixingFillFrames == long.MaxValue ? 0 : session.Coordinator.MinMixingFillFrames) / (double)format.SampleRate:F2} s | preuzimanja {session.Sequencer.Takeovers} | zakasnelih prelaza {session.Sequencer.LateTransitions} | napustenih niti {session.Coordinator.Abandoned}"));
            result.TracksStarted = started;
            result.DeviceUnderrunEvents = device.UnderrunEvents;
            result.DeviceUnderrunFrames = device.UnderrunFrames;
            result.RingMinFillSeconds = device.MinFillFrames / (double)format.SampleRate;
            result.PrivateMemoryPeakMb = Math.Round(peak / 1048576.0, 1);
            result.CpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 1);
        }
        catch (Exception ex)
        {
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }

        result.RenderSteps = AudioMetrics.Steps;
        result.RenderMaxStepMs = Math.Round(AudioMetrics.MaxStepMilliseconds, 2);
        result.RenderStepsOver5ms = AudioMetrics.StepsOver5ms;
        result.RenderStepsOver20ms = AudioMetrics.StepsOver20ms;
        result.RenderStepsOver50ms = AudioMetrics.StepsOver50ms;
        result.RenderAllocatedBytes = AudioMetrics.RenderAllocatedBytes;
        result.RenderStepsWithAllocation = AudioMetrics.StepsWithAllocation;
        result.GuardFileAccess = AudioThreadGuard.Count(AudioViolation.FileAccess);
        result.GuardDecode = AudioThreadGuard.Count(AudioViolation.Decode);
        result.GuardDatabase = AudioThreadGuard.Count(AudioViolation.Database);
        result.GuardLog = AudioThreadGuard.Count(AudioViolation.Log);
        result.GuardLock = AudioThreadGuard.Count(AudioViolation.Lock);
        result.Gc0 = GC.CollectionCount(0);
        result.Gc1 = GC.CollectionCount(1);
        result.Gc2 = GC.CollectionCount(2);
        Console.WriteLine("SIM-REZULTAT " + JsonSerializer.Serialize(result));
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder; nije bitno
        }

        return result.Error is null ? 0 : 1;
    }

    /// <summary>Koliko krugova jednostavne racunske petlje jedna nit uradi za 2 s (stvarnog vremena).</summary>
    private static long Calibrate()
    {
        var clock = Stopwatch.StartNew();
        long rounds = 0;
        var x = 1.0;
        while (clock.ElapsedMilliseconds < 2000)
        {
            for (var i = 0; i < 10_000; i++)
            {
                x = Math.Sqrt(x + i);
            }

            rounds++;
        }

        return x > 0 ? rounds : rounds + 1;
    }

    private static string CpuModel()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "nepoznat";
    }
}

/// <summary>
/// Lazni zvucni uredjaj: kao WASAPI u deljenom rezimu, ima bafer od 200 ms i na svakih ~10 ms
/// pusti onoliko zvuka koliko je proteklo vremena. Ako zvuka nema, to je prekid (prazan izlaz).
/// </summary>
internal sealed class FakeDevice(FloatRingBuffer ring, RenderPump pump, AudioFormat format)
{
    private const int BufferMs = 200;
    private readonly int _bufferFrames = format.SampleRate * BufferMs / 1000;
    private Thread? _thread;
    private volatile bool _stop;
    private long _underrunEvents;
    private long _underrunFrames;

    public long UnderrunEvents => Interlocked.Read(ref _underrunEvents);

    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    public long MinFillFrames { get; private set; } = long.MaxValue;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Sim.Device", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
    }

    private void Run()
    {
        AudioThreadGuard.MarkCurrentThread();
        NativeMethods.TimeBeginPeriod(1);
        try
        {
            var scratch = new float[_bufferFrames * 2];

            // kao pravi uredjaj: krece kad je bafer pun
            var buffered = 0L;
            while (!_stop && buffered < _bufferFrames)
            {
                buffered += ring.Read(scratch.AsSpan(0, (int)(_bufferFrames - buffered) * 2)) / 2;
                pump.Signal();
                Thread.Sleep(5);
            }

            var clock = Stopwatch.StartNew();
            long played = 0;
            while (!_stop)
            {
                Thread.Sleep(10);
                var due = (long)(clock.Elapsed.TotalSeconds * format.SampleRate) - played;
                if (due <= 0)
                {
                    continue;
                }

                if (buffered >= due)
                {
                    buffered -= due;
                }
                else
                {
                    Interlocked.Increment(ref _underrunEvents);
                    Interlocked.Add(ref _underrunFrames, due - buffered);
                    AudioMetrics.RecordUnderrun(due - buffered);
                    buffered = 0;
                }

                played += due;
                var want = (int)(_bufferFrames - buffered);
                if (want > 0)
                {
                    buffered += ring.Read(scratch.AsSpan(0, want * 2)) / 2;
                    pump.Signal();
                }

                if (clock.Elapsed.TotalSeconds > 2)
                {
                    MinFillFrames = Math.Min(MinFillFrames, buffered + (ring.AvailableToRead / 2));
                }
            }
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
        }
    }
}

/// <summary>Probne pesme: tonovi sa malo suma, naizmenicno 44,1 i 48 kHz (da radi i preracunavanje frekvencije).</summary>
internal static class Library
{
    public static void Generate(string folder, int count, int seconds)
    {
        var random = new Random(11);
        for (var i = 0; i < count; i++)
        {
            var rate = i % 2 == 0 ? 44100 : 48000;
            var frequency = 180.0 + (i * 70);
            var frames = rate * seconds;
            var path = Path.Combine(folder, $"sim-{i:D2}.wav");
            using var writer = new BinaryWriter(File.Create(path));
            var dataBytes = frames * 4;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(rate);
            writer.Write(rate * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            for (var n = 0; n < frames; n++)
            {
                var t = n / (double)rate;
                var envelope = Math.Min(1.0, Math.Min(t, seconds - t) / 2.0);
                var value = ((0.35 * Math.Sin(2 * Math.PI * frequency * t)) + (0.05 * ((random.NextDouble() * 2) - 1))) * envelope;
                var sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
                writer.Write(sample);
                writer.Write(sample);
            }
        }
    }
}

internal static class Launcher
{
    public static int Run(int seconds, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Nepoznata putanja programa.");
        var results = new List<SimResult>();
        foreach (var scenario in Scenario.All)
        {
            Console.WriteLine($"== G-sim scenario {scenario.Name}: {scenario.Description} ({seconds} s)");
            results.Add(RunOne(self, scenario, seconds));
        }

        File.WriteAllText(Path.Combine(outFolder, "g-sim-b0.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var r in results)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"G-SIM | {r.Scenario} | pesama {r.TracksStarted} | prazan izlaz {r.DeviceUnderrunEvents} ({r.DeviceUnderrunFrames / 48.0:F0} ms) | najduzi korak {r.RenderMaxStepMs} ms (>20 ms: {r.RenderStepsOver20ms}, >50 ms: {r.RenderStepsOver50ms}) | " +
                $"najmanja zaliha {r.RingMinFillSeconds:F3} s | memorija na audio-niti {r.RenderAllocatedBytes} B | cuvar: fajl {r.GuardFileAccess}, dekoder {r.GuardDecode}, baza {r.GuardDatabase}, dnevnik {r.GuardLog}, brava {r.GuardLock} | " +
                $"privatna memorija vrh {r.PrivateMemoryPeakMb} MB | procesor {r.CpuSeconds} s | izmerena brzina {r.MeasuredSpeedRatio:F2}x | {r.CpuModel} | {(r.Error is null ? "zavrseno" : "GRESKA: " + r.Error)}"));
        }

        return results.All(r => r.Error is null) ? 0 : 1;
    }

    /// <summary>
    /// B6: jedan duzi scenario sa opterecenjem. Kriterijumi (nivo "procena N4000", plan deo 7): prazan izlaz 0, nijedan korak
    /// niti za mesanje duzi od 50 ms, najmanje 200 pretapanja, privatna memorija Sim do 80 MB, bez greske. G-sim, nije test na hardveru.
    /// </summary>
    public static int RunLong(int seconds, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Nepoznata putanja programa.");
        var s = Scenario.Long;
        Console.WriteLine($"== G-sim scenario {s.Name}: {s.Description} ({seconds} s)");
        var r = RunOne(self, s, seconds);
        File.WriteAllText(Path.Combine(outFolder, "g-sim-b6-dugo.json"), JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        var crossfades = Math.Max(0, r.TracksStarted - 1);
        var checks = new (string Name, bool Ok)[]
        {
            ("bez greske", r.Error is null),
            ($"prazan izlaz {r.DeviceUnderrunEvents} (dozvoljeno 0)", r.DeviceUnderrunEvents == 0),
            ($"koraka duzih od 50 ms: {r.RenderStepsOver50ms} (najduzi {r.RenderMaxStepMs} ms)", r.RenderStepsOver50ms == 0),
            ($"pretapanja {crossfades} (najmanje 200)", crossfades >= 200),
            ($"privatna memorija vrh {r.PrivateMemoryPeakMb} MB (do 80)", r.PrivateMemoryPeakMb <= 80),
            ($"memorija na audio-niti {r.RenderAllocatedBytes} B, brava {r.GuardLock}", r.RenderAllocatedBytes == 0 && r.GuardLock == 0),
        };
        foreach (var (name, ok) in checks)
        {
            Console.WriteLine($"G-SIM-B6 | {s.Name} | {name} | {(ok ? "PASS" : "FAIL")}");
        }

        return checks.All(c => c.Ok) ? 0 : 1;
    }

    private static SimResult RunOne(string self, Scenario scenario, int seconds)
    {
        var info = new ProcessStartInfo(self)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("--child");
        info.ArgumentList.Add("--scenario");
        info.ArgumentList.Add(scenario.Name);
        info.ArgumentList.Add("--seconds");
        info.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));

        using var child = Process.Start(info) ?? throw new InvalidOperationException("Simulacija nije pokrenuta.");
        using var job = new JobLimits();
        Process? burner = null;
        try
        {
            string? line;
            while ((line = child.StandardOutput.ReadLine()) is not null && line != "READY")
            {
                Console.WriteLine("   " + line);
                if (line.StartsWith("SIM-REZULTAT ", StringComparison.Ordinal))
                {
                    return JsonSerializer.Deserialize<SimResult>(line["SIM-REZULTAT ".Length..])!;
                }
            }

            double? capPercent = null;
            if (scenario.CpuFactor is { } factor)
            {
                var cores = scenario.TwoCores ? Math.Min(2, Environment.ProcessorCount) : Environment.ProcessorCount;
                capPercent = Math.Max(1.0, factor * cores / Environment.ProcessorCount * 100.0);
            }

            job.Apply(capPercent, scenario.TwoCores, scenario.MemoryLimitBytes);
            job.Assign(child);
            if (scenario.Load)
            {
                burner = Process.Start(new ProcessStartInfo(self, "--burn") { UseShellExecute = false, CreateNoWindow = true });
            }

            child.StandardInput.WriteLine("GO");
            child.StandardInput.Flush();
            var limit = DateTime.UtcNow.AddSeconds(seconds + 120);
            while ((line = child.StandardOutput.ReadLine()) is not null)
            {
                if (line.StartsWith("SIM-REZULTAT ", StringComparison.Ordinal))
                {
                    var result = JsonSerializer.Deserialize<SimResult>(line["SIM-REZULTAT ".Length..])!;
                    result.CpuCapPercentOfMachine = capPercent is null ? null : Math.Round(capPercent.Value, 1);
                    return result;
                }

                Console.WriteLine("   " + line);
                if (DateTime.UtcNow > limit)
                {
                    break;
                }
            }

            return new SimResult { Scenario = scenario.Name, Seconds = seconds, Error = "Simulacija se zavrsila bez rezultata." };
        }
        finally
        {
            if (burner is { HasExited: false })
            {
                burner.Kill(entireProcessTree: true);
            }

            burner?.Dispose();
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }
}

/// <summary>Windows Job Object: tvrdo ogranicenje procesora, broj jezgara i memorija procesa.</summary>
internal sealed class JobLimits : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint LimitAffinity = 0x10;
    private const uint LimitProcessMemory = 0x100;
    private const uint LimitKillOnJobClose = 0x2000;
    private const uint CpuRateEnable = 0x1;
    private const uint CpuRateHardCap = 0x4;

    private readonly IntPtr _job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);

    public void Apply(double? cpuPercentOfMachine, bool twoCores, long? memoryLimitBytes)
    {
        if (_job == IntPtr.Zero)
        {
            throw new InvalidOperationException("Job Object nije napravljen: " + Marshal.GetLastWin32Error());
        }

        var limits = new NativeMethods.JobExtendedLimits();
        limits.Basic.LimitFlags = LimitKillOnJobClose;
        if (twoCores && Environment.ProcessorCount > 2)
        {
            limits.Basic.LimitFlags |= LimitAffinity;
            limits.Basic.Affinity = 0b11;
        }

        if (memoryLimitBytes is { } memory)
        {
            limits.Basic.LimitFlags |= LimitProcessMemory;
            limits.ProcessMemoryLimit = (UIntPtr)(ulong)memory;
        }

        Set(JobObjectExtendedLimitInformation, limits);
        if (cpuPercentOfMachine is { } percent)
        {
            Set(JobObjectCpuRateControlInformation, new NativeMethods.JobCpuRate { ControlFlags = CpuRateEnable | CpuRateHardCap, CpuRate = (uint)Math.Round(percent * 100) });
        }
    }

    public void Assign(Process process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_job, process.Handle))
        {
            throw new InvalidOperationException("Proces nije dodeljen Job Object-u: " + Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_job);
        }
    }

    private void Set<T>(int infoClass, T value)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buffer, false);
            if (!NativeMethods.SetInformationJobObject(_job, infoClass, buffer, (uint)size))
            {
                throw new InvalidOperationException($"Ogranicenje {infoClass} nije postavljeno: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobCpuRate
    {
        public uint ControlFlags;
        public uint CpuRate;
    }
}
