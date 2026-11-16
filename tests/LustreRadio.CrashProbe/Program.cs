using LustreRadio.Data;
using LustreRadio.Playout;

// Proba pada procesa (plan faze B, B-221 i B-222). Pisar ne radi; pesme "sviraju" (zapisi idu u crnu kutiju);
// zatim proces pada (Environment.FailFast), bez ikakvog zatvaranja. Test posle toga pokrece oporavak.
// Upotreba: LustreRadio.CrashProbe <folder> <broj pesama> [kapacitet]
//          LustreRadio.CrashProbe memorija   (B-195: memorija 3 napustene + 1 zaglavljene radne niti, u posebnom procesu)
if (args[0] == "memorija")
{
    MemoryProbe.Run();
    return;
}

var root = args[0];
var plays = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var capacity = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : LustreRadio.Core.History.BlackBox.DefaultCapacity;
var db = LustreDatabase.Open(Path.Combine(root, "lustre.db"));
var history = PlaybackHistory.Open(root, db, options: new PlaybackHistoryOptions { Capacity = capacity, StartScribe = false });
for (var i = 0; i < plays; i++)
{
    var pid = Guid.CreateVersion7();
    history.Prepared(pid, 1000 + i, 1, 1, 1, "Proba");
    history.Started(pid, 1000 + i, i * 1000L);
    if (i < plays - 1)
    {
        history.Ended(pid, 1000 + i, (i * 1000L) + 999, 999);
    }
}

Console.WriteLine($"PROBA: {plays} pesama, upisano do {history.Box.Reserved}, izgubljeno {history.Box.LostCount}; pad.");
Console.Out.Flush();
Environment.FailFast("LustreRadio.CrashProbe: namerni pad procesa (test)");


internal static class MemoryProbe
{
    private sealed class Blocking(LustreRadio.Audio.AudioFormat format) : LustreRadio.Audio.ISampleSource
    {
        private readonly float[] _state = new float[256 * 1024]; // oko 1 MB stanja "dekodera" (lazni dekoder)

        public LustreRadio.Audio.AudioFormat Format => format;

        public long? LengthFrames => 44100L * 60;

        public int Read(Span<float> buffer)
        {
            _state[0]++;
            Thread.Sleep(Timeout.Infinite); // zaglavljen poziv
            return 0;
        }

        public void Dispose()
        {
        }
    }

    private sealed class Probe : LustreRadio.Audio.IDiskProbe
    {
        public LustreRadio.Audio.DiskProbeOutcome Probe(string? stuckFile, TimeSpan timeout) => LustreRadio.Audio.DiskProbeOutcome.DiskWorks;
    }

    private sealed class Inspector : LustreRadio.Audio.IThreadInspector
    {
        public long CpuTicks(int nativeThreadId) => 0;

        public LustreRadio.Audio.WorkerThreadState Inspect(int nativeThreadId, long cpuTicksAtStart, TimeSpan callDuration, LustreRadio.Audio.StallOptions options) => LustreRadio.Audio.WorkerThreadState.Blocked;
    }

    public static void Run()
    {
        var format = LustreRadio.Audio.AudioFormat.Stereo44100;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        var before = process.PrivateMemorySize64;
        var threadsBefore = process.Threads.Count;
        var stall = new LustreRadio.Audio.StallOptions { BlockedCallSeconds = 0.2, ProbeSeconds = 0.1, ProbeRepeatSeconds = 0.2 };
        var restart = new ManualResetEventSlim();
        var coordinator = new LustreRadio.Audio.Coordinator(format, () => new LustreRadio.Audio.QueuedTrack("1", new Blocking(format)) { PlaybackId = Guid.NewGuid() },
            new LustreRadio.Audio.CoordinatorOptions { Stall = stall }, threaded: true)
        {
            DiskProbe = new Probe(),
            ThreadInspector = new Inspector(),
        };
        coordinator.RestartRequested += _ => restart.Set();
        coordinator.Start();
        restart.Wait(TimeSpan.FromSeconds(60));
        Thread.Sleep(500);
        GC.Collect();
        process.Refresh();
        var after = process.PrivateMemorySize64;
        Console.WriteLine(System.FormattableString.Invariant(
            $"MEMORIJA | napustenih niti {coordinator.Abandoned} + 1 zaglavljena aktivna | privatna pre {before / 1048576.0:F1} MB | posle {after / 1048576.0:F1} MB | razlika {(after - before) / 1048576.0:F1} MB | niti pre {threadsBefore}, posle {process.Threads.Count}"));
        Console.Out.Flush();
        Environment.Exit(0);
    }
}
