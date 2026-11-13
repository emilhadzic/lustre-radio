using LustreRadio.Audio;

namespace LustreRadio.Audio.Tests;

/// <summary>
/// Probni fajlovi u testdata/audio napravljeni su iz poznatog signala:
/// "chirp" od 4 s, 200 -> 4000 Hz, amplituda 0,5; desni kanal je upola tisi od levog.
/// Zato se za svaki dekodirani fajl tacno zna sta mora da izadje.
/// </summary>
internal static class AudioFixtures
{
    public const double Seconds = 4.0;
    public static readonly AudioFormat Target = AudioFormat.Stereo44100;

    public static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "audio", file);

    public static float[] ExpectedLeft(int sampleRate = 44100)
    {
        var n = (int)Math.Round(Seconds * sampleRate);
        var result = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / sampleRate;
            result[i] = (float)(0.5 * Math.Sin(2 * Math.PI * ((200.0 * t) + ((4000.0 - 200.0) * t * t / (2 * Seconds)))));
        }

        return result;
    }

    public static float[] ReadAll(ISampleSource source, long maxFrames = long.MaxValue)
    {
        var sink = new MemorySink();
        var buffer = new float[4096];
        long total = 0;
        while (total < maxFrames)
        {
            var want = (int)Math.Min(buffer.Length / 2, maxFrames - total);
            var frames = source.Read(buffer.AsSpan(0, want * 2));
            if (frames == 0)
            {
                break;
            }

            sink.Write(buffer.AsSpan(0, frames * 2));
            total += frames;
        }

        return sink.Samples.ToArray();
    }

    public static float[] Channel(float[] interleaved, int channel)
    {
        var result = new float[interleaved.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = interleaved[(i * 2) + channel];
        }

        return result;
    }

    /// <summary>
    /// Trazi pomak (lag) za koji je actual[actualStart + lag + i] najslicniji reference[referenceStart + i].
    /// Vraca pomak u frejmovima i slicnost (1 = isto).
    /// </summary>
    public static (int Lag, double Correlation) BestLag(float[] actual, int actualStart, float[] reference, int referenceStart, int length, int maxLag)
    {
        var bestLag = 0;
        var best = double.NegativeInfinity;
        double referenceEnergy = 0;
        for (var i = 0; i < length; i++)
        {
            referenceEnergy += (double)reference[referenceStart + i] * reference[referenceStart + i];
        }

        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            var offset = actualStart + lag;
            if (offset < 0 || offset + length > actual.Length)
            {
                continue;
            }

            double dot = 0, energy = 0;
            for (var i = 0; i < length; i++)
            {
                var a = actual[offset + i];
                dot += (double)a * reference[referenceStart + i];
                energy += (double)a * a;
            }

            var correlation = dot / Math.Sqrt(Math.Max(1e-12, energy * referenceEnergy));
            if (correlation > best)
            {
                best = correlation;
                bestLag = lag;
            }
        }

        return (bestLag, best);
    }

    /// <summary>Pravi WAV fajl (16-bit PCM ili 32-bit float) iz poznatog signala.</summary>
    public static string WriteWav(bool floatFormat, int sampleRate = 44100)
    {
        var left = ExpectedLeft(sampleRate);
        var path = Path.Combine(Path.GetTempPath(), $"lustre-{Guid.NewGuid():N}.wav");
        using var writer = new BinaryWriter(File.Create(path));
        var bytesPerSample = floatFormat ? 4 : 2;
        var dataBytes = left.Length * 2 * bytesPerSample;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)(floatFormat ? 3 : 1));
        writer.Write((short)2);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2 * bytesPerSample);
        writer.Write((short)(2 * bytesPerSample));
        writer.Write((short)(bytesPerSample * 8));
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in left)
        {
            if (floatFormat)
            {
                writer.Write(sample);
                writer.Write(sample * 0.5f);
            }
            else
            {
                writer.Write((short)Math.Round(sample * 32767));
                writer.Write((short)Math.Round(sample * 0.5 * 32767));
            }
        }

        return path;
    }
}
