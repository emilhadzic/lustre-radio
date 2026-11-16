using System.Diagnostics;
using System.Globalization;
using LustreRadio.Audio;
using LustreRadio.Core.History;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Audio.Tests;

/// <summary>
/// Ogledi za pragove (plan faze B, 2.7: B-120, B-126, B-223). Rezultat je TABELA, ne prolaz ili pad.
/// Pokrecu se u posebnom poslu ("ogledi"), ne u obicnom testiranju. Vrednosti biraju vlasnik na kontrolnoj tacki 1.
/// </summary>
[Trait("Category", "Ogled")]
public sealed class ThresholdExperiments(ITestOutputHelper output)
{
    private static readonly AudioFormat Format = AudioFormat.Stereo44100;
    private const int Rate = 44100;

    private static long F(double s) => (long)Math.Round(s * Rate);

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record Outcome(int Skipped, int WrongErrors, double Silence, bool Stuck, bool Choppy);

    /// <summary>Jedan scenario u virtuelnom vremenu. Ispravna pesma je "1"; vraca sta se desilo.</summary>
    private static Outcome Run(string scenario, double t, double n2, double t2, int seed)
    {
        var stall = new StallOptions { SlowTrackSeconds = t, BlockedCallSeconds = t2 };
        var probe = new FakeProbe(DiskProbeOutcome.DiskWorks);
        var events = new RecordingEvents();
        var queue = new Queue<ScriptedSource>();
        var random = new Random(seed);
        var jitter = random.NextDouble() * 0.3;

        // scenariji (grupe I, II, III iz plana 2.7)
        ScriptedSource first = scenario switch
        {
            var s when s.StartsWith("fajl-", StringComparison.Ordinal) => new ScriptedSource(Format, F(40), _ => 0.3f).StallAt(F(12 + jitter), F(double.Parse(s[5..], CultureInfo.InvariantCulture))),
            "disk-povremeno" => Periodic(),
            var s when s.StartsWith("disk-", StringComparison.Ordinal) => new ScriptedSource(Format, F(40), _ => 0.3f).StallAt(F(12 + jitter), F(double.Parse(s[5..], CultureInfo.InvariantCulture))),
            "zauvek" => new ScriptedSource(Format, F(40), _ => 0.3f).StallAt(F(12 + jitter), F(10_000)),
            "greska" => new ScriptedSource(Format, F(40), _ => 0.3f) { FailAt = F(12 + jitter) },
            var s when s.StartsWith("brzina-", StringComparison.Ordinal) => new ScriptedSource(Format, F(30), _ => 0.3f) { Speed = double.Parse(s[7..], CultureInfo.InvariantCulture) },
            _ => throw new ArgumentException(scenario),
        };

        // opsti zastoj diska: proba ne javlja "disk radi"; zastoj jednog fajla: disk radi
        probe.Outcome = scenario.StartsWith("disk-", StringComparison.Ordinal) ? DiskProbeOutcome.NotResponding : DiskProbeOutcome.DiskWorks;
        queue.Enqueue(first);
        queue.Enqueue(new ScriptedSource(Format, F(40), _ => -0.3f));
        var coordinator = new Coordinator(Format, () => queue.Count > 0 ? new QueuedTrack(queue.Count == 2 ? "1" : "2", queue.Dequeue()) { PlaybackId = Guid.NewGuid() } : null, new CoordinatorOptions { Stall = stall, ResumeSeconds = n2 }, events)
        {
            DiskProbe = probe,
            ThreadInspector = new FakeInspector(WorkerThreadState.Blocked),
        };
        var sequencer = new CrossfadeSequencer(Format, coordinator, 6, history: events);
        var buffer = new float[1024 * 2];
        var audio = new List<float>();
        while (audio.Count < F(90))
        {
            var n = sequencer.Read(buffer);
            if (n == 0)
            {
                break;
            }

            for (var i = 0; i < n; i++)
            {
                audio.Add(buffer[i * 2]);
            }
        }

        var o = events.Outcomes();
        var firstOutcome = o.TryGetValue(1, out var list) ? list.Single() : null;
        var skipped = firstOutcome is { Status: PlaybackStatus.Stopped, Reason: StopReason.DecoderStall } ? 1 : 0;
        var wrongErrors = firstOutcome is { Status: PlaybackStatus.Stopped, Reason: StopReason.FileError } && scenario != "greska" ? 1 : 0;

        // tisina posle greske ili zastoja (najduza neprekidna tisina posle 5. sekunde)
        int longest = 0, run = 0, bursts = 0, burst = 0;
        for (var i = (int)F(5); i < audio.Count; i++)
        {
            if (audio[i] == 0)
            {
                run++;
                if (burst > 0 && burst < F(0.25))
                {
                    bursts++;
                }

                burst = 0;
            }
            else
            {
                run = 0;
                burst++;
            }

            longest = Math.Max(longest, run);
        }

        var stuck = !o.ContainsKey(2) && scenario == "zauvek";
        return new Outcome(skipped, wrongErrors, longest / (double)Rate, stuck, bursts > 0);

        ScriptedSource Periodic()
        {
            var s = new ScriptedSource(Format, F(40), _ => 0.3f);
            for (var k = 0; k < 10; k++)
            {
                s.StallAt(F(10 + (k * 2.5)), F(2));
            }

            return s;
        }
    }

    [Fact] // B-120 i B-126: matrica T, N2, T2 (P se ne meri u virtuelnom vremenu); svaki scenario 5 puta
    public void B120_ThresholdMatrix()
    {
        double[] ts = [0.2, 0.5, 1, 1.5, 2];
        double[] n2s = [0.25, 0.5, 1, 2];
        double[] t2s = [1, 2, 3, 5];
        string[] scenarios = ["fajl-0.5", "fajl-1", "fajl-2", "fajl-3", "fajl-5", "fajl-10", "disk-1", "disk-4", "disk-8", "disk-20", "disk-povremeno", "zauvek", "greska", "brzina-0.8", "brzina-0.95", "brzina-1.1", "brzina-1.5"];
        output.WriteLine("OGLED B-120 | T | N2 | T2 | U1 opsti zastoj 0 preskocenih | U2 greska tisina <= 2 s | U3 bez seckanja (>=1,1x) | U4 0 pogresnih gresaka | U5 zauvek -> sledeca | U6 fajl <= 2 s 0 preskocenih | preskoceno fajl > 2 s (od 15) | najduza tisina greska (s)");
        foreach (var t in ts)
        {
            foreach (var n2 in n2s)
            {
                foreach (var t2 in t2s)
                {
                    bool u1 = true, u2 = true, u3 = true, u4 = true, u5 = true, u6 = true;
                    var skippedLong = 0;
                    var errorSilence = 0.0;
                    foreach (var scenario in scenarios)
                    {
                        for (var rep = 0; rep < 5; rep++)
                        {
                            var r = Run(scenario, t, n2, t2, rep);
                            u1 &= !scenario.StartsWith("disk-", StringComparison.Ordinal) || r.Skipped == 0;
                            if (scenario == "greska")
                            {
                                u2 &= r.Silence <= 2;
                                errorSilence = Math.Max(errorSilence, r.Silence);
                            }

                            u3 &= scenario is not ("brzina-1.1" or "brzina-1.5") || !r.Choppy;
                            u4 &= r.WrongErrors == 0;
                            u5 &= !r.Stuck;
                            if (scenario is "fajl-0.5" or "fajl-1" or "fajl-2")
                            {
                                u6 &= r.Skipped == 0;
                            }

                            if (scenario is "fajl-3" or "fajl-5" or "fajl-10")
                            {
                                skippedLong += r.Skipped;
                            }
                        }
                    }

                    output.WriteLine($"OGLED B-120 | {N(t)} | {N(n2)} | {N(t2)} | {Y(u1)} | {Y(u2)} | {Y(u3)} | {Y(u4)} | {Y(u5)} | {Y(u6)} | {skippedLong} | {N(errorSilence)}");
                }
            }
        }

        static string Y(bool ok) => ok ? "da" : "NE";
    }

    /// <summary>Jedno merenje stanja radne niti u zadatom scenariju (prave niti, pravi Windows podaci).</summary>
    private static (System.Diagnostics.ThreadState? State, double Share) Measure(string scenario, double seconds)
    {
        var inspector = new WindowsThreadInspector();
        var stop = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        var nativeId = 0;
        var hogs = new List<Thread>();
        var worker = new Thread(() =>
        {
            nativeId = (int)GetCurrentThreadId();
            gate.Set();
            switch (scenario)
            {
                case "S1-bez-procesora":
                case "S2-povremeno":
                case "S3-vrti-se":
                    while (!stop.IsSet)
                    {
                        Thread.SpinWait(200);
                    }

                    break;
                default: // S4/S5 blokirana: ceka dogadjaj koji ne dolazi
                    stop.Wait();
                    break;
            }
        })
        { IsBackground = true, Priority = scenario is "S1-bez-procesora" or "S2-povremeno" ? ThreadPriority.Lowest : ThreadPriority.Normal };
        worker.Start();
        gate.Wait();
        if (scenario is "S1-bez-procesora" or "S2-povremeno")
        {
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                var hog = new Thread(() =>
                {
                    var on = true;
                    var watch = Stopwatch.StartNew();
                    while (!stop.IsSet)
                    {
                        if (scenario == "S2-povremeno" && watch.ElapsedMilliseconds % 1000 > 900)
                        {
                            Thread.Sleep(1);
                            on = !on;
                            continue;
                        }

                        Thread.SpinWait(500);
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.Highest };
                hog.Start();
                hogs.Add(hog);
            }
        }

        var start = inspector.CpuTicks(nativeId);
        var clock = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var now = inspector.CpuTicks(nativeId);
        var share = (now - start) / (double)clock.Elapsed.Ticks;
        System.Diagnostics.ThreadState? state = null;
        using (var process = Process.GetCurrentProcess())
        {
            foreach (ProcessThread thread in process.Threads)
            {
                if (thread.Id == nativeId)
                {
                    state = thread.ThreadState;
                    break;
                }
            }
        }

        stop.Set();
        worker.Join(2000);
        hogs.ForEach(h => h.Join(2000));
        return (state, share);
    }

    [Fact] // B-223 (deo na G): granice stanja niti D i G; 13 parova; merenja pravih niti (S1–S5), 5 ponavljanja
    public void B223_ThreadStateThresholds()
    {
        string[] scenarios = ["S1-bez-procesora", "S2-povremeno", "S3-vrti-se", "S4-blokirana"];
        var samples = new List<(string Scenario, System.Diagnostics.ThreadState? State, double Share)>();
        foreach (var scenario in scenarios)
        {
            for (var rep = 0; rep < 5; rep++)
            {
                var (state, share) = Measure(scenario, 3);
                samples.Add((scenario, state, share));
                output.WriteLine($"MERENJE B-223 | {scenario} | ponavljanje {rep + 1} | stanje {state} | procesor {share:P0}");
            }
        }

        // S6 (podatak ne moze da se dobije) je uvek "neodredjeno" (Classify bez stanja)
        double[] lows = [0.10, 0.20, 0.30, 0.40];
        double[] highs = [0.40, 0.50, 0.70, 0.90];
        output.WriteLine("OGLED B-223 | D | G | S1/S2 pogresno (moglo bi da preskoci) | S1/S2 kao 'vrti se' (K3) | S3 otkriveno (K2) | S4 kao 'blokirana' | ispunjava K1-K3");
        foreach (var d in lows)
        {
            foreach (var g in highs.Where(g => g > d))
            {
                var options = new StallOptions { LowCpuShare = d, HighCpuShare = g };
                var cls = samples.Select(s => (s.Scenario, Class: WindowsThreadInspector.Classify(s.State, null, s.Share, options))).ToList();
                var noCpu = cls.Where(c => c.Scenario is "S1-bez-procesora" or "S2-povremeno").ToList();
                var wrong = noCpu.Count(c => c.Class is WorkerThreadState.Blocked or WorkerThreadState.Spinning);
                var asSpin = noCpu.Count(c => c.Class == WorkerThreadState.Spinning);
                var spinFound = cls.Count(c => c.Scenario == "S3-vrti-se" && c.Class == WorkerThreadState.Spinning);
                var blocked = cls.Count(c => c.Scenario == "S4-blokirana" && c.Class == WorkerThreadState.Blocked);
                var ok = wrong == 0 && asSpin == 0 && spinFound == 5;
                output.WriteLine($"OGLED B-223 | {d:P0} | {g:P0} | {wrong}/10 | {asSpin}/10 | {spinFound}/5 | {blocked}/5 | {(ok ? "da" : "NE")}");
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
