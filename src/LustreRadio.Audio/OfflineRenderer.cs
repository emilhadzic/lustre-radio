namespace LustreRadio.Audio;

/// <summary>
/// Pusta engine u odrediste najbrze sto moze (bez zvucne kartice i bez cekanja).
/// Osnova automatskih testova zvuka. Kasnjenje limitera se poravnava, pa je
/// izlaz iste duzine i na istom mestu kao zvuk pre limitera.
/// </summary>
public static class OfflineRenderer
{
    public static long Render(IFrameProvider provider, Limiter? limiter, IAudioSink sink, int blockFrames = 1024, long maxFrames = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(sink);

        var buffer = new float[blockFrames * 2];
        var skip = limiter?.LatencyFrames ?? 0;
        long written = 0;

        void Emit(Span<float> span)
        {
            var frames = span.Length / 2;
            var drop = Math.Min(skip, frames);
            skip -= drop;
            if (frames > drop)
            {
                sink.Write(span[(drop * 2)..]);
                written += frames - drop;
            }
        }

        while (written < maxFrames)
        {
            var frames = provider.Read(buffer);
            if (frames == 0)
            {
                break;
            }

            var span = buffer.AsSpan(0, frames * 2);
            limiter?.Process(span);
            Emit(span);
        }

        if (limiter is not null)
        {
            var remaining = limiter.LatencyFrames;
            while (remaining > 0)
            {
                var frames = Math.Min(remaining, blockFrames);
                var span = buffer.AsSpan(0, frames * 2);
                span.Clear();
                limiter.Process(span);
                Emit(span);
                remaining -= frames;
            }
        }

        return written;
    }
}
