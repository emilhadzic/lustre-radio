using LustreRadio.Audio;
using LustreRadio.Core.History;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Audio.Tests;

/// <summary>Faza B, korak B4: kartice – objavljivanje bez brave (B-209), predaja vlasnistva (B-218), ponovna upotreba (B-219).</summary>
public sealed class TicketTests(ITestOutputHelper output)
{
    [Fact] // B-209: 10 miliona objava izmedju niti za mesanje i koordinatora, nasumicni zastoji
    public void B209_TenMillionPublications_NoTornOrLostData()
    {
        var tickets = new PlayTickets();
        const int cycles = 3_400_000; // 3 objave po ciklusu = 10,2 miliona
        long slot = -1;
        var errors = 0;
        long done = 0;
        var mixer = new Thread(() =>
        {
            var random = new Random(1);
            for (long n = 0; n < cycles; n++)
            {
                long h;
                var spins = 0;
                while ((h = Interlocked.Exchange(ref slot, -1)) < 0)
                {
                    if (++spins % 64 == 0)
                    {
                        Thread.Yield();
                    }
                }

                var track = tickets.TrackOf(h);
                tickets.WriteStart(h, track * 10, n % 2 == 0, Guid.Empty, track - 1);
                if (random.Next(1000) == 0)
                {
                    Thread.Sleep(0);
                }

                tickets.WriteTransition(h, (track * 10) + 1);
                tickets.WriteOutcome(h, (track * 10) + 2, n % 3 == 0 ? StopReason.Skip : StopReason.None, (int)(track % 1000));
            }
        });
        mixer.Start();
        var events = new List<TicketEvent>();
        var open = new List<long>();
        long published = 0;
        while (done < cycles)
        {
            if (published < cycles && Interlocked.Read(ref slot) < 0 && open.Count < 8)
            {
                var h = tickets.Plan(Guid.NewGuid(), published + 1);
                if (h >= 0)
                {
                    tickets.MarkHanded(h);
                    open.Add(h);
                    Interlocked.Exchange(ref slot, h);
                    published++;
                }
            }

            for (var i = open.Count - 1; i >= 0; i--)
            {
                var h = open[i];
                events.Clear();
                var finished = tickets.StateOf(h) == PlayTickets.Done;
                tickets.Collect(h, events);
                foreach (var e in events)
                {
                    var expected = (e.Track * 10) + (e.Flag == PlayTickets.FlagStart ? 0 : e.Flag == PlayTickets.FlagTransition ? 1 : 2);
                    if (e.Frame != expected || (e.Flag == PlayTickets.FlagOutcome && e.PositionMs != (int)(e.Track % 1000)) || (e.Flag == PlayTickets.FlagStart && e.PeerTrack != e.Track - 1))
                    {
                        errors++;
                    }
                }

                if (finished)
                {
                    tickets.Collect(h, events);
                    tickets.Release(h);
                    open.RemoveAt(i);
                    done++;
                }
            }
        }

        mixer.Join();
        output.WriteLine($"ciklusa {done}, gresaka {errors}");
        Assert.Equal(0, errors);
        Assert.Equal(0, tickets.InUse);
    }

    [Fact] // B-218: predaja vlasnistva uz povlacenje u isto vreme: od preuzimanja i povlacenja uspe tacno jedno
    public void B218_HandoffAndRetractRace_ExactlyOneWins()
    {
        var tickets = new PlayTickets();
        const int cycles = 2_000_000;
        long slot = -1;
        var taken = 0L;
        var retracted = 0L;
        var stop = false;
        var mixer = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var h = Interlocked.Exchange(ref slot, -1);
                if (h >= 0)
                {
                    Interlocked.Increment(ref taken);
                    tickets.WriteStart(h, 1, false, Guid.Empty, 0);
                    tickets.WriteOutcome(h, 2, StopReason.None, 0);
                    tickets.WriteOutcome(h, 3, StopReason.Skip, 0); // posle ZAVRSENA: nema dejstva
                }
            }
        });
        mixer.Start();
        var both = 0;
        for (var n = 0; n < cycles; n++)
        {
            var h = tickets.Plan(Guid.Empty, n);
            tickets.MarkHanded(h);
            Interlocked.Exchange(ref slot, h);
            if (n % 3 == 0)
            {
                Thread.SpinWait(n % 50);
            }

            var withdrawn = Interlocked.CompareExchange(ref slot, -1, h) == h;
            if (withdrawn)
            {
                retracted++;
                tickets.MarkWithdrawn(h);
            }

            // cekaj da kartica bude ZAVRSENA (ili povucena), pa oslobodi
            while (tickets.StateOf(h) != PlayTickets.Done)
            {
                Thread.SpinWait(10);
            }

            var events = new List<TicketEvent>();
            tickets.Collect(h, events);
            if (withdrawn && events.Count > 0)
            {
                both++; // i povucena i preuzeta
            }

            if (!withdrawn && events.Any(e => e.Flag == PlayTickets.FlagOutcome && e.Frame != 2))
            {
                both++; // upis posle ZAVRSENA je imao dejstvo
            }

            tickets.Release(h);
        }

        Volatile.Write(ref stop, true);
        mixer.Join();
        output.WriteLine($"preuzeto {taken}, povuceno {retracted}, oba ili upis posle kraja: {both}");
        Assert.Equal(cycles, taken + retracted);
        Assert.Equal(0, both);
    }

    [Fact] // B-219: ponovna upotreba: svaka kartica milion puta, dok stare rucke postoje -> svaka stara rucka odbacena
    public void B219_ReuseWithStaleHandles_StaleHandlesHaveNoEffect()
    {
        var tickets = new PlayTickets();
        var stale = new List<long>();
        var leaks = 0;
        for (var n = 0; n < 16_000_000; n++)
        {
            var h = tickets.Plan(Guid.Empty, n);
            tickets.MarkHanded(h);
            if (stale.Count > 0)
            {
                var old = stale[n % stale.Count];
                tickets.WriteStart(old, -1, false, Guid.Empty, 0); // stara rucka: bez dejstva
                tickets.WriteOutcome(old, -1, StopReason.Skip, 0);
                if (tickets.StateOf(old) != -1)
                {
                    leaks++;
                }
            }

            tickets.WriteStart(h, n, false, Guid.Empty, 0);
            tickets.WriteOutcome(h, n, StopReason.None, 0);
            var events = new List<TicketEvent>();
            tickets.Collect(h, events);
            if (events.Count != 2 || events.Any(e => e.Frame != n))
            {
                leaks++;
            }

            tickets.Release(h);
            if (n % 1000 == 0)
            {
                stale.Add(h);
            }
        }

        output.WriteLine($"starih rucki: {stale.Count}; pogodaka: {leaks}");
        Assert.Equal(0, leaks);
        Assert.Equal(0, tickets.InUse);
    }

    private sealed class BlockingPlanner(Queue<ScriptedSource> sources) : IPlanner
    {
        public ManualResetEventSlim Gate { get; } = new(true);

        public int Calls { get; private set; }

        public (int Size, string? Reason) Capacity() => (Math.Min(3, sources.Count), null);

        public PlannedItem? Select(IReadOnlyList<string> before)
        {
            Calls++;
            Gate.Wait(); // koordinator stoji (zaglavljen u planeru)
            lock (sources)
            {
                return sources.Count == 0 ? null : new PlannedItem { TrackId = (100 - sources.Count).ToString(System.Globalization.CultureInfo.InvariantCulture), Tag = sources.Dequeue() };
            }
        }

        public QueuedTrack? Open(PlannedItem item) => new(item.TrackId, (ScriptedSource)item.Tag!) { PlaybackId = item.PlaybackId };

        public void Release(PlannedItem item)
        {
        }

        public bool IsValid(PlannedItem item) => true;
    }

    [Fact] // B-210: koordinator stoji dok sviraju kratke pesme: nema pada; nit za mesanje ne ceka; posle povratka istorija tacna
    public void B210_CoordinatorStalls_MixerNeverWaits_HistoryCatchesUp()
    {
        var format = AudioFormat.Stereo44100;
        var sources = new Queue<ScriptedSource>(Enumerable.Range(0, 24).Select(_ => new ScriptedSource(format, 44100 * 2, _ => 0.2f)));
        var planner = new BlockingPlanner(sources);
        var events = new RecordingEvents();
        using var coordinator = new Coordinator(format, planner, new CoordinatorOptions { DurableWaitSeconds = 0 }, events, threaded: true);
        coordinator.Start();
        var sequencer = new CrossfadeSequencer(format, coordinator, 0.5);
        var buffer = new float[1024 * 2];
        var slowest = TimeSpan.Zero;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (sequencer.Events.Count < 3 && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            sequencer.Read(buffer);
            Thread.Sleep(5);
        }

        planner.Gate.Reset(); // koordinator ce stati pri sledecem izboru
        var startedBefore = sequencer.Events.Count;
        for (var i = 0; i < 2000; i++) // oko 46 s zvuka, bez cekanja na stvarno vreme
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            sequencer.Read(buffer);
            slowest = t.Elapsed > slowest ? t.Elapsed : slowest;
        }

        var startedDuring = sequencer.Events.Count - startedBefore;
        var recordsDuring = events.Records.Count;
        planner.Gate.Set();
        watch.Restart();
        while (sequencer.Events.Count < startedBefore + startedDuring + 3 && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            sequencer.Read(buffer);
            Thread.Sleep(5);
        }

        Thread.Sleep(300);
        var started = sequencer.Events.Select(e => e.PlaybackId).ToList();
        var recorded = events.Records.Where(r => r.Kind is JournalKind.Started or JournalKind.Crossfade).Select(r => r.PlaybackId).ToList();
        output.WriteLine($"dok koordinator stoji: novih pesama {startedDuring}, najduzi poziv niti za mesanje {slowest.TotalMilliseconds:F2} ms, tisina {sequencer.StarvedFrames / 44100.0:F1} s; zapisa pre povratka {recordsDuring}, posle {events.Records.Count}");
        Assert.True(startedDuring <= 2);
        Assert.True(slowest < TimeSpan.FromMilliseconds(50));
        Assert.True(sequencer.StarvedFrames > 0);
        Assert.Equal(started.Take(started.Count - 1), recorded.Take(started.Count - 1)); // svaka pesma koja je krenula, istim redom
        coordinator.DiscardPending(StopReason.Shutdown);
    }
}
