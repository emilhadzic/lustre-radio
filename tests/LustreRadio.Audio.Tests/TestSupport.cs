using LustreRadio.Audio;

namespace LustreRadio.Audio.Tests;

internal static class Signal
{
    public static readonly AudioFormat Format = AudioFormat.Stereo44100;

    /// <summary>Levi kanal iz isprepletanog stereo signala.</summary>
    public static float[] Left(ReadOnlySpan<float> interleaved)
    {
        var left = new float[interleaved.Length / 2];
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = interleaved[i * 2];
        }

        return left;
    }

    public static double Rms(ReadOnlySpan<float> mono)
    {
        double sum = 0;
        foreach (var v in mono)
        {
            sum += (double)v * v;
        }

        return Math.Sqrt(sum / Math.Max(1, mono.Length));
    }

    /// <summary>Amplituda tona zadate frekvencije u prozoru (Goertzel).</summary>
    public static double ToneAmplitude(ReadOnlySpan<float> mono, double frequencyHz, int sampleRate)
    {
        var w = 2.0 * Math.PI * frequencyHz / sampleRate;
        var coefficient = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var v in mono)
        {
            var s0 = v + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return 2.0 * Math.Sqrt(Math.Max(0, power)) / mono.Length;
    }

    /// <summary>Najduzi neprekidan tihi deo, u frejmovima (prozori od 10 ms).</summary>
    public static int LongestSilenceFrames(ReadOnlySpan<float> mono, int sampleRate, double threshold = 0.01)
    {
        var window = sampleRate / 100;
        int longest = 0, run = 0;
        for (var start = 0; start + window <= mono.Length; start += window)
        {
            if (Rms(mono.Slice(start, window)) < threshold)
            {
                run += window;
                longest = Math.Max(longest, run);
            }
            else
            {
                run = 0;
            }
        }

        return longest;
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var v in samples)
        {
            peak = Math.Max(peak, Math.Abs(v));
        }

        return peak;
    }

    public static Func<QueuedTrack?> Queue(params QueuedTrack[] tracks)
    {
        var index = 0;
        return () => index < tracks.Length ? tracks[index++] : null;
    }

    public static QueuedTrack Tone(string id, double hz, double seconds, float amplitude = 0.25f, float gain = 1f) =>
        new(id, new ToneSource(Format, hz, amplitude, Format.FramesFromSeconds(seconds)), gain);

    public static MemorySink Render(CrossfadeSequencer sequencer, bool limiter = true)
    {
        var sink = new MemorySink();
        OfflineRenderer.Render(sequencer, limiter ? new Limiter(Format) : null, sink);
        return sink;
    }
}

/// <summary>Izvor koji najavi punu duzinu, a isporuci manje (ostecen ili odsecen fajl).</summary>
internal sealed class TruncatedSource(ISampleSource inner, long deliverFrames) : ISampleSource
{
    private long _delivered;

    public AudioFormat Format => inner.Format;

    public long? LengthFrames => inner.LengthFrames;

    public int Read(Span<float> buffer)
    {
        var frames = (int)Math.Min(buffer.Length / 2, deliverFrames - _delivered);
        if (frames <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer[..(frames * 2)]);
        _delivered += read;
        return read;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Izvor koji baci gresku posle zadatog broja frejmova (fajl pukne usred pesme).</summary>
internal sealed class ThrowingSource(ISampleSource inner, long throwAfterFrames) : ISampleSource
{
    private long _delivered;

    public AudioFormat Format => inner.Format;

    public long? LengthFrames => inner.LengthFrames;

    public int Read(Span<float> buffer)
    {
        if (_delivered >= throwAfterFrames)
        {
            throw new IOException("Namerna greska citanja (test).");
        }

        var frames = (int)Math.Min(buffer.Length / 2, throwAfterFrames - _delivered);
        var read = inner.Read(buffer[..(frames * 2)]);
        _delivered += read;
        return read;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Izvor koji vraca najvise nekoliko frejmova po pozivu (kratka citanja dekodera).</summary>
internal sealed class ShortReadSource(ISampleSource inner, int maxFramesPerRead) : ISampleSource
{
    public AudioFormat Format => inner.Format;

    public long? LengthFrames => inner.LengthFrames;

    public int Read(Span<float> buffer) =>
        inner.Read(buffer[..(Math.Min(buffer.Length / 2, maxFramesPerRead) * 2)]);

    public void Dispose() => inner.Dispose();
}
