namespace LustreRadio.Audio;

/// <summary>
/// Nit koja unapred priprema zvuk (sequencer + limiter) i puni kruzni bafer.
/// Nit zvucnog uredjaja samo cita iz bafera, pa dekodiranje nikad ne radi u njoj.
/// </summary>
public sealed class RenderPump : IDisposable
{
    private readonly IFrameProvider _provider;
    private readonly Limiter? _limiter;
    private readonly FloatRingBuffer _ring;
    private readonly float[] _block;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _finished;
    private long _framesRendered;

    public RenderPump(IFrameProvider provider, Limiter? limiter, FloatRingBuffer ring, int blockFrames = 1024)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _limiter = limiter;
        if (blockFrames * 2 > ring.Capacity)
        {
            throw new ArgumentException("Blok je veci od kruznog bafera.", nameof(blockFrames));
        }

        _block = new float[blockFrames * 2];
    }

    /// <summary>Izvor vise nema sta da pusti i sve je predato baferu.</summary>
    public bool Finished => _finished;

    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Greska u pripremi zvuka (ne bi smela da se desi; belezi se).</summary>
    public event Action<Exception>? Error;

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Vec pokrenuto.");
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LustreRadio.Render",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Citalac javlja da je oslobodio mesto u baferu.</summary>
    public void Signal() => _wake.Set();

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(2000);
        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            while (!_stop)
            {
                if (_ring.FreeSpace < _block.Length)
                {
                    _wake.WaitOne(10);
                    continue;
                }

                var frames = _provider.Read(_block);
                if (frames == 0)
                {
                    break;
                }

                var span = _block.AsSpan(0, frames * 2);
                _limiter?.Process(span);
                _ring.Write(span);
                Interlocked.Add(ref _framesRendered, frames);
            }

            // isprazni kasnjenje limitera
            var remaining = _limiter?.LatencyFrames ?? 0;
            while (remaining > 0 && !_stop)
            {
                var frames = Math.Min(remaining, _block.Length / 2);
                if (_ring.FreeSpace < frames * 2)
                {
                    _wake.WaitOne(10);
                    continue;
                }

                var span = _block.AsSpan(0, frames * 2);
                span.Clear();
                _limiter!.Process(span);
                _ring.Write(span);
                remaining -= frames;
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
        finally
        {
            _finished = true;
        }
    }
}
