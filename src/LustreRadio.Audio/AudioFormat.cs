namespace LustreRadio.Audio;

/// <summary>Format zvuka unutar engine-a: float uzorci, isprepletani kanali (L, R, L, R...).</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public static AudioFormat Stereo44100 => new(44100, 2);

    public static AudioFormat Stereo48000 => new(48000, 2);

    public long FramesFromSeconds(double seconds) => (long)Math.Round(seconds * SampleRate);
}
