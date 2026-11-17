using System.Reflection;
using System.Reflection.Emit;
using LustreRadio.Audio;
using LustreRadio.Core.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>Faza B, korak B5: audio-nit bez brava i bez zauzimanja memorije.</summary>
public sealed class AudioThreadTests(ITestOutputHelper output)
{
    [Fact] // B-01 i B-02a: pravi radio (koordinator i radna nit na svojim nitima); cuvar 0; tacno 0 bajtova u delu "merenje"
    public void B01_B02a_RealRadio_NoViolations_ZeroBytesWhileMeasuring()
    {
        using var env = new TestEnvironment();
        for (var i = 0; i < 12; i++)
        {
            env.AddTone(300 + (i * 100), 6 + (i % 3));
        }

        env.Index();
        foreach (var t in env.Db.Tracks.GetAll())
        {
            env.Verify(int.Parse(t.Title.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        var (director, clock) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        var session = new RadioSession(director, TestEnvironment.Format, realtime: true);
        clock.Frames = () => session.Sequencer.OutputFrames;
        var pause = new PauseGate(session.Output);
        var volume = new VolumeControl(pause, 0.8);
        var limiter = new Limiter(TestEnvironment.Format);
        var block = new float[1024 * 2];
        long violations = -1, allocated = -1;
        var startedBefore = 0;
        Exception? failure = null;

        void Play(double seconds, ref long blockNo)
        {
            var blocks = (long)(seconds * 44100 / 1024);
            for (var i = 0; i < blocks; i++, blockNo++)
            {
                var frames = volume.Read(block);
                limiter.Process(block.AsSpan(0, frames * 2));
                switch (blockNo % 1500)
                {
                    case 300: session.Skip(); break;
                    case 600: pause.Pause(); break;
                    case 700: pause.Resume(); break;
                    case 900: volume.SetVolume(0.4); break;
                    case 1200: volume.SetVolume(0.9); break;
                }

                if (blockNo % 4 == 0)
                {
                    Thread.Sleep(1); // koordinator stize da dopuni deckove
                }
            }
        }

        var thread = new Thread(() =>
        {
            try
            {
                AudioThreadGuard.MarkCurrentThread();
                long n = 0;
                Play(60, ref n); // zagrevanje
                Thread.Sleep(2000);
                startedBefore = session.Sequencer.Events.Count; // cita se kopija, ne dira audio-nit
                var guard = AudioThreadGuard.CurrentThreadCount;
                var before = GC.GetAllocatedBytesForCurrentThread();
                Play(600, ref n); // merenje: 10 minuta zvuka
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                violations = AudioThreadGuard.CurrentThreadCount - guard;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true, Name = "test audio-nit" };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "audio-nit nije zavrsila");
        var startedDuring = session.Sequencer.Events.Count - startedBefore;
        session.End(Core.History.StopReason.Shutdown);
        Assert.Null(failure);
        output.WriteLine($"pesama {startedDuring}, pretapanja {session.Sequencer.Events.Count - 1}, tisina {session.Sequencer.StarvedFrames / 44100.0:F1} s, cuvar {violations}, zauzeto {allocated} B");
        Assert.Equal(0, violations);
        Assert.Equal(0, allocated);
        Assert.True(startedDuring >= 30, $"premalo prelaza: {startedDuring}");
    }

    [Fact] // B-03: u prevedenom kodu klasa kroz koje prolazi audio-nit nema brava
    public void B03_AudioThreadClasses_HaveNoLocks()
    {
        var audio = typeof(Limiter).Assembly;
        var types = new[]
        {
            typeof(CrossfadeSequencer), typeof(Deck), typeof(DeckFeed), typeof(BufferedSource), typeof(HandoffSlot), typeof(PlayTickets),
            typeof(Limiter), typeof(FloatRingBuffer), typeof(FadeCurve), typeof(PauseGate), typeof(VolumeControl), typeof(ClosingFader),
            audio.GetType("LustreRadio.Audio.OutputPuller", throwOnError: true)!,
        };
        string[] forbidden = ["System.Threading.Monitor", "System.Threading.Lock", "System.Threading.SpinLock", "System.Threading.Mutex",
            "System.Threading.SemaphoreSlim", "System.Threading.Semaphore", "System.Threading.ReaderWriterLockSlim"];
        var found = new List<string>();
        foreach (var type in types.SelectMany(t => new[] { t }.Concat(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))))
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                if (type == typeof(CrossfadeSequencer) && method.Name == "get_Events")
                {
                    continue; // spisak za testove i dijagnostiku: cita ga druga nit, audio-nit ga ne poziva
                }

                foreach (var called in CalledMethods(method))
                {
                    if (called.DeclaringType?.FullName is { } owner && forbidden.Contains(owner))
                    {
                        found.Add($"{type.Name}.{method.Name} -> {owner}.{called.Name}");
                    }
                }
            }
        }

        output.WriteLine($"pregledano klasa: {types.Length}; nadjeno: {found.Count}");
        Assert.Empty(found);
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[i++]);
            }

            var op = OpCodesByValue[value];
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, i) * 4),
                _ => 4,
            };
            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase? target = null;
                try
                {
                    target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                    // generican poziv koji ne moze da se razresi bez konkretnih tipova
                }

                if (target is not null)
                {
                    yield return target;
                }
            }

            i += size;
        }
    }
}
