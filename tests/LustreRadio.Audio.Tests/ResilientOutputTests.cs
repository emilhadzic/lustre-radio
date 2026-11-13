using LustreRadio.Audio;
using Xunit;

namespace LustreRadio.Audio.Tests;

/// <summary>
/// T-AUD-09/10 na laznom uredjaju: nestanak, povratak i promena podrazumevanog uredjaja.
/// Dokazuje logiku ponovnog povezivanja; pravi uredjaj (i Bluetooth) proverava se posebno.
/// </summary>
public class ResilientOutputTests
{
    private static readonly AudioFormat Format = Signal.Format;
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(20);

    private static CrossfadeSequencer MakeSequencer() => new(
        Format,
        Signal.Queue(Signal.Tone("a", 400, 3), Signal.Tone("b", 1000, 3), Signal.Tone("c", 600, 3)),
        crossfadeSeconds: 1.0);

    private static float[] Offline()
    {
        return Signal.Render(MakeSequencer()).Samples.ToArray();
    }

    private static (ResilientOutput Output, RenderPump Pump, Limiter Limiter) Build(FakeBackend backend)
    {
        var ring = new FloatRingBuffer(16384);
        var limiter = new Limiter(Format);
        var pump = new RenderPump(MakeSequencer(), limiter, ring);
        var output = new ResilientOutput(backend, ring, pump, Format, 200, Retry);
        pump.Start();
        return (output, pump, limiter);
    }

    private static void AssertSameAudio(FakeBackend backend, Limiter limiter)
    {
        var expected = Offline();
        var actual = backend.Collected();
        var skip = limiter.LatencyFrames * 2;
        Assert.Equal(expected.Length, actual.Length - skip);
        Assert.True(expected.AsSpan().SequenceEqual(actual.AsSpan(skip)), "Zvuk se razlikuje: nesto je izgubljeno ili ponovljeno.");
    }

    [Fact]
    public void PlaysToEnd_OnOneDevice()
    {
        using var backend = new FakeBackend("slusalice");
        var (output, pump, limiter) = Build(backend);
        using (pump)
        using (output)
        {
            output.Start();
            Assert.True(output.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            Assert.Equal(OutputState.Finished, output.State);
            Assert.Equal(0, output.Reconnects);
            AssertSameAudio(backend, limiter);
        }
    }

    [Fact] // T-AUD-09: uredjaj nestane usred pesme, pa se vrati
    public void DeviceVanishesAndReturns_MusicContinuesWithoutLossOrRepeat()
    {
        using var backend = new FakeBackend("bt-zvucnik") { PauseAfterSamples = 100_000 };
        var (output, pump, limiter) = Build(backend);
        using (pump)
        using (output)
        {
            var states = new List<OutputState>();
            output.StateChanged += (state, _) =>
            {
                lock (states)
                {
                    states.Add(state);
                }
            };
            output.Start();

            Assert.True(backend.WaitForPause(TimeSpan.FromSeconds(10)));
            backend.RemoveDevice(new IOException("uredjaj je nestao (test)"));
            Assert.True(SpinWait.SpinUntil(() => output.State == OutputState.Disconnected, TimeSpan.FromSeconds(10)));
            Thread.Sleep(200); // nekoliko neuspelih pokusaja

            backend.PauseAfterSamples = long.MaxValue;
            backend.AddDevice("bt-zvucnik");

            Assert.True(output.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, output.Reconnects);
            lock (states)
            {
                Assert.Equal([OutputState.Playing, OutputState.Disconnected, OutputState.Playing, OutputState.Finished], states);
            }

            AssertSameAudio(backend, limiter);
        }
    }

    [Fact] // T-AUD-10: promenjen podrazumevani uredjaj
    public void DefaultDeviceChanges_OutputMovesToNewDevice()
    {
        using var backend = new FakeBackend("slusalice") { PauseAfterSamples = 100_000 };
        var (output, pump, limiter) = Build(backend);
        using (pump)
        using (output)
        {
            output.Start();
            Assert.True(backend.WaitForPause(TimeSpan.FromSeconds(10)));

            backend.PauseAfterSamples = long.MaxValue;
            backend.AddDevice("zvucnici");

            Assert.True(output.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, output.Reconnects);
            Assert.Equal("zvucnici", output.DeviceName);
            Assert.Equal(["slusalice", "zvucnici"], backend.OpenedDevices());
            AssertSameAudio(backend, limiter);
        }
    }

    [Fact]
    public void NoDeviceAtStart_WaitsAndStartsWhenDeviceAppears()
    {
        using var backend = new FakeBackend(null);
        var (output, pump, limiter) = Build(backend);
        using (pump)
        using (output)
        {
            output.Start();
            Assert.True(SpinWait.SpinUntil(() => output.State == OutputState.Disconnected, TimeSpan.FromSeconds(10)));
            Thread.Sleep(200);
            Assert.Empty(backend.Collected());

            backend.AddDevice("zvucnik");

            Assert.True(output.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, output.Reconnects);
            AssertSameAudio(backend, limiter);
        }
    }

    [Fact]
    public void OpenFailsRepeatedly_KeepsRetrying_ThenPlays()
    {
        using var backend = new FakeBackend("zvucnik") { FailOpens = 5 };
        var (output, pump, limiter) = Build(backend);
        using (pump)
        using (output)
        {
            output.Start();
            Assert.True(output.WaitUntilFinished(TimeSpan.FromSeconds(30)));
            Assert.True(backend.OpenAttempts >= 6);
            AssertSameAudio(backend, limiter);
        }
    }

    /// <summary>Lazni uredjaji: "sviraju" tako sto sakupljaju zvuk u memoriju.</summary>
    private sealed class FakeBackend(string? initialDevice) : IOutputBackend
    {
        private readonly object _lock = new();
        private readonly MemorySink _collected = new();
        private readonly List<string> _opened = [];
        private readonly ManualResetEventSlim _paused = new();
        private string? _default = initialDevice;
        private FakeSession? _active;
        private long _samples;

        public event Action? DevicesChanged;

        /// <summary>Uredjaj prestaje da trazi zvuk posle ovoliko uzoraka (da test stigne da reaguje).</summary>
        public long PauseAfterSamples { get; set; } = long.MaxValue;

        public int FailOpens { get; set; }

        public int OpenAttempts { get; private set; }

        public string? CurrentDefaultDeviceId()
        {
            lock (_lock)
            {
                return _default;
            }
        }

        public IOutputSession Open(OutputPuller puller, AudioFormat format, int latencyMs, Action<int> reportUnderrunFrames)
        {
            lock (_lock)
            {
                OpenAttempts++;
                if (FailOpens > 0)
                {
                    FailOpens--;
                    throw new InvalidOperationException("otvaranje nije uspelo (test)");
                }

                if (_default is null)
                {
                    throw new InvalidOperationException("nema uredjaja (test)");
                }

                _opened.Add(_default);
                _active = new FakeSession(this, _default, puller);
                return _active;
            }
        }

        public void RemoveDevice(Exception error)
        {
            FakeSession? session;
            lock (_lock)
            {
                _default = null;
                session = _active;
            }

            session?.Fail(error);
            DevicesChanged?.Invoke();
        }

        public void AddDevice(string id)
        {
            lock (_lock)
            {
                _default = id;
            }

            DevicesChanged?.Invoke();
        }

        public bool WaitForPause(TimeSpan timeout) => _paused.Wait(timeout);

        public float[] Collected()
        {
            lock (_lock)
            {
                return _collected.Samples.ToArray();
            }
        }

        public string[] OpenedDevices()
        {
            lock (_lock)
            {
                return [.. _opened];
            }
        }

        public void Dispose() => _paused.Dispose();

        private bool TryCollect(OutputPuller puller, float[] buffer)
        {
            lock (_lock)
            {
                if (_samples >= PauseAfterSamples)
                {
                    _paused.Set();
                    return false;
                }

                var read = puller.Read(buffer);
                _collected.Write(buffer.AsSpan(0, read));
                _samples += read;
                return read > 0;
            }
        }

        private sealed class FakeSession(FakeBackend owner, string deviceId, OutputPuller puller) : IOutputSession
        {
            private volatile bool _stop;
            private volatile Exception? _failure;
            private Thread? _thread;

            public string DeviceId { get; } = deviceId;

            public string DeviceName { get; } = deviceId;

            public event Action<Exception?>? Stopped;

            public void Play()
            {
                _thread = new Thread(Run) { IsBackground = true };
                _thread.Start();
            }

            public void Fail(Exception error) => _failure = error;

            public void Dispose()
            {
                _stop = true;
                _thread?.Join(2000);
            }

            private void Run()
            {
                var buffer = new float[512];
                while (!_stop)
                {
                    if (_failure is not null)
                    {
                        Stopped?.Invoke(_failure);
                        return;
                    }

                    if (!owner.TryCollect(puller, buffer))
                    {
                        if (puller.Finished)
                        {
                            Stopped?.Invoke(null);
                            return;
                        }

                        Thread.Sleep(1);
                    }
                }
            }
        }
    }
}
