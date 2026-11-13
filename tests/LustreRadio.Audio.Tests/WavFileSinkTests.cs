using System.Text;
using LustreRadio.Audio;
using Xunit;

namespace LustreRadio.Audio.Tests;

public class WavFileSinkTests
{
    [Fact]
    public void WritesValidFloatWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lustre-{Guid.NewGuid():N}.wav");
        try
        {
            float[] samples = [0f, 0.5f, -0.5f, 1f, -1f, 0.25f];
            using (var sink = new WavFileSink(path, AudioFormat.Stereo44100))
            {
                sink.Write(samples);
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + (samples.Length * 4), bytes.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(3, BitConverter.ToInt16(bytes, 20));
            Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
            Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(32, BitConverter.ToInt16(bytes, 34));
            Assert.Equal(samples.Length * 4, BitConverter.ToInt32(bytes, 40));
            for (var i = 0; i < samples.Length; i++)
            {
                Assert.Equal(samples[i], BitConverter.ToSingle(bytes, 44 + (i * 4)));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
