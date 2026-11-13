using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LustreRadio.Audio;

/// <summary>NAudio adapter: uredjaj cita iz kruznog bafera. Ako bafer presusi, salje tisinu i broji prekid.</summary>
internal sealed class RingWaveProvider(FloatRingBuffer ring, RenderPump pump, AudioFormat format) : IWaveProvider
{
    private long _underrunFrames;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    public int Read(byte[] buffer, int offset, int count)
    {
        var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - (count % 8)));
        var read = ring.Read(floats);
        pump.Signal();

        if (read == 0 && pump.Finished)
        {
            return 0; // kraj: uredjaj zaustavlja reprodukciju
        }

        if (read < floats.Length)
        {
            floats[read..].Clear();
            if (!pump.Finished)
            {
                Interlocked.Add(ref _underrunFrames, (floats.Length - read) / 2);
            }
        }

        return floats.Length * sizeof(float);
    }
}

/// <summary>
/// Izlaz na stvarni zvucni uredjaj (WASAPI Shared).
/// STANJE: napisano, NIJE pokrenuto na uredjaju (GitHub runner nema zvucnu karticu).
/// Pracenje promene uredjaja i ponovno povezivanje dolaze u sledecem koraku.
/// </summary>
public sealed class WasapiPlayer : IDisposable
{
    private readonly RingWaveProvider _provider;
    private readonly int _latencyMs;
    private WasapiOut? _output;

    public WasapiPlayer(FloatRingBuffer ring, RenderPump pump, AudioFormat format, int latencyMs = 200)
    {
        _provider = new RingWaveProvider(ring, pump, format);
        _latencyMs = latencyMs;
    }

    /// <summary>Broj frejmova tisine poslatih zato sto zvuk nije stigao na vreme. Cilj: 0.</summary>
    public long UnderrunFrames => _provider.UnderrunFrames;

    /// <summary>Reprodukcija je stala (kraj ili greska uredjaja).</summary>
    public event Action<Exception?>? Stopped;

    /// <summary>Podrazumevani uredjaj i njegov sample rate (engine radi u tom rate-u, da nema resamplinga na izlazu).</summary>
    public static (AudioFormat Format, string DeviceName) QueryDefaultDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return (new AudioFormat(device.AudioClient.MixFormat.SampleRate, 2), device.FriendlyName);
    }

    public void Start()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, _latencyMs);
        _output.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
        _output.Init(_provider);
        _output.Play();
    }

    public void Dispose()
    {
        try
        {
            _output?.Stop();
        }
        catch (Exception)
        {
            // uredjaj je mozda vec nestao
        }

        _output?.Dispose();
        _output = null;
    }
}
