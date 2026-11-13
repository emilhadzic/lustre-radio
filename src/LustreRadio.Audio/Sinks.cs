using System.Runtime.InteropServices;
using System.Text;

namespace LustreRadio.Audio;

/// <summary>Cuva zvuk u memoriji (za testove i merenja).</summary>
public sealed class MemorySink : IAudioSink
{
    private float[] _data = new float[1 << 16];
    private int _count;

    public ReadOnlySpan<float> Samples => _data.AsSpan(0, _count);

    public int Frames => _count / 2;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (_count + interleaved.Length > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _count + interleaved.Length));
        }

        interleaved.CopyTo(_data.AsSpan(_count));
        _count += interleaved.Length;
    }

    public void Dispose()
    {
    }
}

/// <summary>Upisuje zvuk u WAV fajl (32-bit float). Zaglavlje se dovrsava pri zatvaranju.</summary>
public sealed class WavFileSink : IAudioSink
{
    private const int HeaderSize = 44;
    private readonly FileStream _stream;
    private readonly AudioFormat _format;
    private long _dataBytes;
    private bool _disposed;

    public WavFileSink(string path, AudioFormat format)
    {
        _format = format;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader(0);
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("WAV upis podrzava samo little-endian sisteme.");
        }

        var bytes = MemoryMarshal.AsBytes(interleaved);
        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Position = 0;
        WriteHeader(_dataBytes);
        _stream.Dispose();
    }

    private void WriteHeader(long dataBytes)
    {
        const short FormatIeeeFloat = 3;
        const short BitsPerSample = 32;
        var blockAlign = (short)(_format.Channels * (BitsPerSample / 8));

        using var writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)Math.Min(uint.MaxValue, (HeaderSize - 8) + dataBytes));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write(FormatIeeeFloat);
        writer.Write((short)_format.Channels);
        writer.Write(_format.SampleRate);
        writer.Write(_format.SampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)Math.Min(uint.MaxValue, dataBytes));
    }
}
