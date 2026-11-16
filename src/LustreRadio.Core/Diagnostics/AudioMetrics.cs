using System.Diagnostics;

namespace LustreRadio.Core.Diagnostics;

/// <summary>
/// Brojaci rada audio-niti: koraci pripreme zvuka, njihovo trajanje, memorija zauzeta na toj niti,
/// praznjenje izlaza. Upis je bez brave (Interlocked) i bez zauzimanja memorije.
/// </summary>
public static class AudioMetrics
{
    private static long _steps;
    private static long _maxStepTicks;
    private static long _over5ms;
    private static long _over20ms;
    private static long _over50ms;
    private static long _renderAllocatedBytes;
    private static long _stepsWithAllocation;
    private static long _underrunEvents;
    private static long _underrunFrames;

    public static long Steps => Interlocked.Read(ref _steps);

    public static double MaxStepMilliseconds => Interlocked.Read(ref _maxStepTicks) * 1000.0 / Stopwatch.Frequency;

    public static long StepsOver5ms => Interlocked.Read(ref _over5ms);

    public static long StepsOver20ms => Interlocked.Read(ref _over20ms);

    public static long StepsOver50ms => Interlocked.Read(ref _over50ms);

    /// <summary>Bajtovi memorije zauzeti na niti za pripremu zvuka, tokom koraka pripreme.</summary>
    public static long RenderAllocatedBytes => Interlocked.Read(ref _renderAllocatedBytes);

    public static long StepsWithAllocation => Interlocked.Read(ref _stepsWithAllocation);

    public static long UnderrunEvents => Interlocked.Read(ref _underrunEvents);

    public static long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    /// <summary>Jedan korak pripreme zvuka: trajanje (Stopwatch otkucaji) i memorija zauzeta tokom njega.</summary>
    public static void RecordStep(long elapsedTicks, long allocatedBytes)
    {
        Interlocked.Increment(ref _steps);
        long current;
        while (elapsedTicks > (current = Interlocked.Read(ref _maxStepTicks)))
        {
            if (Interlocked.CompareExchange(ref _maxStepTicks, elapsedTicks, current) == current)
            {
                break;
            }
        }

        var ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        if (ms > 5)
        {
            Interlocked.Increment(ref _over5ms);
        }

        if (ms > 20)
        {
            Interlocked.Increment(ref _over20ms);
        }

        if (ms > 50)
        {
            Interlocked.Increment(ref _over50ms);
        }

        if (allocatedBytes > 0)
        {
            Interlocked.Add(ref _renderAllocatedBytes, allocatedBytes);
            Interlocked.Increment(ref _stepsWithAllocation);
        }
    }

    /// <summary>Izlaz je trazio zvuk koga nije bilo.</summary>
    public static void RecordUnderrun(long frames)
    {
        Interlocked.Increment(ref _underrunEvents);
        Interlocked.Add(ref _underrunFrames, frames);
    }

    public static string Describe() =>
        $"koraci={Steps} najduzi={MaxStepMilliseconds:F1}ms >5ms={StepsOver5ms} >20ms={StepsOver20ms} >50ms={StepsOver50ms} " +
        $"memorija_na_niti={RenderAllocatedBytes}B ({StepsWithAllocation} koraka) prazan_izlaz={UnderrunEvents} ({UnderrunFrames} frejmova)";
}
