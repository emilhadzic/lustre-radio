namespace LustreRadio.Audio;

/// <summary>
/// Limiter sa pogledom unapred (stereo povezan). Sluzi kao osigurac: dok je signal ispod
/// plafona, zvuk prolazi nepromenjen (samo zakasnjen za LatencyFrames).
/// Garancija: nijedan izlazni uzorak ne prelazi plafon.
/// </summary>
public sealed class Limiter
{
    private readonly int _lookahead;
    private readonly float _ceiling;
    private readonly double _releaseCoefficient;

    private readonly float[] _delay;
    private int _delayPos;

    // klizni minimum preko poslednjih (_lookahead + 1) vrednosti
    private readonly long[] _dequeIndex;
    private readonly double[] _dequeValue;
    private int _dequeHead;
    private int _dequeCount;

    // pokretni prosek preko poslednjih _lookahead minimuma
    private readonly double[] _box;
    private int _boxPos;
    private double _boxSum;

    private double _released = 1.0;
    private long _frame;

    public Limiter(AudioFormat format, double ceilingDb = -1.0, double lookaheadSeconds = 0.005, double releaseSeconds = 0.2)
    {
        if (format.Channels != 2)
        {
            throw new ArgumentException("Limiter radi samo u stereo formatu.", nameof(format));
        }

        _lookahead = Math.Max(1, (int)Math.Round(lookaheadSeconds * format.SampleRate));
        _ceiling = (float)Math.Pow(10.0, ceilingDb / 20.0);
        _releaseCoefficient = Math.Exp(-1.0 / (releaseSeconds * format.SampleRate));

        _delay = new float[_lookahead * 2];
        _dequeIndex = new long[_lookahead + 2];
        _dequeValue = new double[_lookahead + 2];
        _box = new double[_lookahead];
        Reset();
    }

    public int LatencyFrames => _lookahead;

    public float Ceiling => _ceiling;

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Fill(_box, 1.0);
        _boxSum = _lookahead;
        _boxPos = 0;
        _delayPos = 0;
        _dequeHead = 0;
        _dequeCount = 0;
        _released = 1.0;
        _frame = 0;
    }

    /// <summary>Obradjuje bafer na mestu. Izlaz kasni za LatencyFrames.</summary>
    public void Process(Span<float> interleaved)
    {
        var capacity = _dequeIndex.Length;
        var frames = interleaved.Length / 2;

        for (var i = 0; i < frames; i++)
        {
            var left = interleaved[i * 2];
            var right = interleaved[(i * 2) + 1];
            var peak = Math.Max(Math.Abs(left), Math.Abs(right));

            // 1) potrebno pojacanje za ovaj uzorak, sa sporim oporavkom
            var required = peak > _ceiling ? _ceiling / (double)peak : 1.0;
            var recovering = 1.0 - ((1.0 - _released) * _releaseCoefficient);
            if (recovering > 0.999999)
            {
                recovering = 1.0;
            }

            _released = Math.Min(required, recovering);

            // 2) klizni minimum preko prozora [n - L, n]
            while (_dequeCount > 0 && _dequeValue[(_dequeHead + _dequeCount - 1) % capacity] >= _released)
            {
                _dequeCount--;
            }

            var tail = (_dequeHead + _dequeCount) % capacity;
            _dequeIndex[tail] = _frame;
            _dequeValue[tail] = _released;
            _dequeCount++;

            while (_dequeIndex[_dequeHead] < _frame - _lookahead)
            {
                _dequeHead = (_dequeHead + 1) % capacity;
                _dequeCount--;
            }

            var minimum = _dequeValue[_dequeHead];

            // 3) pokretni prosek: gladak silazak pre vrha
            _boxSum += minimum - _box[_boxPos];
            _box[_boxPos] = minimum;
            if (++_boxPos == _lookahead)
            {
                _boxPos = 0;
                var exact = 0.0;
                foreach (var value in _box)
                {
                    exact += value;
                }

                _boxSum = exact; // ponistava nagomilanu gresku zaokruzivanja (rad 24/7)
            }

            var gain = Math.Min(1.0, _boxSum / _lookahead);

            // 4) zakasnjen signal puta pojacanje
            var delayedLeft = _delay[_delayPos * 2];
            var delayedRight = _delay[(_delayPos * 2) + 1];
            _delay[_delayPos * 2] = left;
            _delay[(_delayPos * 2) + 1] = right;
            if (++_delayPos == _lookahead)
            {
                _delayPos = 0;
            }

            interleaved[i * 2] = Math.Clamp((float)(delayedLeft * gain), -_ceiling, _ceiling);
            interleaved[(i * 2) + 1] = Math.Clamp((float)(delayedRight * gain), -_ceiling, _ceiling);
            _frame++;
        }
    }
}
