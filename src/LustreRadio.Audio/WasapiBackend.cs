using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LustreRadio.Audio;

/// <summary>Pravi zvucni uredjaji preko WASAPI Shared.</summary>
internal sealed class WasapiBackend : IOutputBackend, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public WasapiBackend()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public event Action? DevicesChanged;

    public string? CurrentDefaultDeviceId()
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IOutputSession Open(OutputPuller puller, AudioFormat format, int latencyMs, Action<int> reportUnderrunFrames)
    {
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new Session(device, new PullWaveProvider(puller, format, reportUnderrunFrames), latencyMs);
    }

    public void Dispose()
    {
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception)
        {
            // gasenje
        }

        _enumerator.Dispose();
    }

    // Obavestenja stizu na sistemskim nitima: ovde se samo javlja da nesto treba proveriti.
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render)
        {
            DevicesChanged?.Invoke();
        }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // promene glasnoce i slicno: nije bitno
    }

    private sealed class PullWaveProvider(OutputPuller puller, AudioFormat format, Action<int> reportUnderrunFrames) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - (count % 8)));
            var read = puller.Read(floats);
            if (read == 0 && puller.Finished)
            {
                return 0; // kraj: uredjaj zaustavlja reprodukciju
            }

            if (read < floats.Length)
            {
                floats[read..].Clear();
                reportUnderrunFrames((floats.Length - read) / 2);
            }

            return floats.Length * sizeof(float);
        }
    }

    private sealed class Session : IOutputSession
    {
        private readonly WasapiOut _output;
        private readonly IWaveProvider _provider;

        public Session(MMDevice device, IWaveProvider provider, int latencyMs)
        {
            DeviceId = device.ID;
            DeviceName = device.FriendlyName;
            _provider = provider;
            _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latencyMs);
            _output.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
        }

        public string DeviceId { get; }

        public string DeviceName { get; }

        public event Action<Exception?>? Stopped;

        public void Play()
        {
            _output.Init(_provider);
            _output.Play();
        }

        public void Dispose()
        {
            try
            {
                _output.Stop();
            }
            catch (Exception)
            {
                // uredjaj je mozda vec nestao
            }

            try
            {
                _output.Dispose();
            }
            catch (Exception)
            {
                // isto
            }
        }
    }
}
