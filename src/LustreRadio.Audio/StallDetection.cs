using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LustreRadio.Audio;

/// <summary>Pragovi za zastoje (plan faze B, 2.7). Sve vrednosti su PRIVREMENE dok ih ogledi B-120 i B-223 ne opravdaju.</summary>
public sealed record StallOptions
{
    /// <summary>T – rok za sporu pesmu (prazan deck koji svira).</summary>
    public double SlowTrackSeconds { get; init; } = 1;

    /// <summary>T2 – rok za blokiran poziv, meren od pocetka poziva.</summary>
    public double BlockedCallSeconds { get; init; } = 3;

    /// <summary>P – rok za probu diska.</summary>
    public double ProbeSeconds { get; init; } = 1;

    /// <summary>Proba se ponavlja na ovoliko, najvise jedna u isto vreme.</summary>
    public double ProbeRepeatSeconds { get; init; } = 2;

    /// <summary>D – donja granica procesorskog vremena radne niti ("bez procesora"), privremeno 20%.</summary>
    public double LowCpuShare { get; init; } = 0.20;

    /// <summary>G – gornja granica ("vrti se u krug"), privremeno 50%.</summary>
    public double HighCpuShare { get; init; } = 0.50;

    /// <summary>Najvise napustenih radnih niti u jednom procesu Engine-a.</summary>
    public int MaxAbandoned { get; init; } = 3;
}

/// <summary>Ishod probe diska.</summary>
public enum DiskProbeOutcome
{
    /// <summary>Drugi fajl sa istog volumena procitan mimo kesa u roku P. Jedini ishod koji dozvoljava prelaz na sledecu pesmu.</summary>
    DiskWorks,

    /// <summary>Citanje nije zavrseno u roku P: opsti zastoj; ceka se.</summary>
    NotResponding,

    /// <summary>Probu nije moguce pouzdano izvesti: ceka se.</summary>
    Undetermined,
}

/// <summary>Proba diska: razlikuje "zaglavljen je ovaj fajl" od "zaglavljen je disk ili sistem".</summary>
public interface IDiskProbe
{
    /// <summary>Moze da traje do <paramref name="timeout"/> (poziva se sa posebne niti, nikad sa audio-niti ni koordinatora).</summary>
    DiskProbeOutcome Probe(string? stuckFile, TimeSpan timeout);
}

/// <summary>Sta radna nit radi u trenutku T2 (odluka D7-5).</summary>
public enum WorkerThreadState
{
    /// <summary>Spremna da radi, a ne dobija procesor (ili tek malo, a ne ceka nista): opsti zastoj, ne preskace se.</summary>
    NoCpu,

    /// <summary>Ceka (disk, bravu, sistem), malo procesora: odlucuje proba diska.</summary>
    Blocked,

    /// <summary>Trosi vise od G procesorskog vremena, a poziv se ne zavrsava: dekoder se vrti u krug.</summary>
    Spinning,

    /// <summary>Izmedju, ili podatak ne moze da se dobije: ceka se.</summary>
    Undetermined,
}

/// <summary>Cita stanje radne niti od Windows-a.</summary>
public interface IThreadInspector
{
    /// <summary>Procesorsko vreme niti do sada (100-ns jedinice), ili -1.</summary>
    long CpuTicks(int nativeThreadId);

    WorkerThreadState Inspect(int nativeThreadId, long cpuTicksAtStart, TimeSpan callDuration, StallOptions options);
}

/// <summary>Stanje niti i procesorsko vreme preko Windows-a (ProcessThread, GetThreadTimes).</summary>
public sealed class WindowsThreadInspector : IThreadInspector
{
    private const int ThreadQueryLimitedInformation = 0x0800;

    public long CpuTicks(int nativeThreadId)
    {
        var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)nativeThreadId);
        if (handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            return GetThreadTimes(handle, out _, out _, out var kernel, out var user) ? kernel + user : -1;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public WorkerThreadState Inspect(int nativeThreadId, long cpuTicksAtStart, TimeSpan callDuration, StallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = CpuTicks(nativeThreadId);
        if (now < 0 || cpuTicksAtStart < 0 || callDuration <= TimeSpan.Zero)
        {
            return WorkerThreadState.Undetermined;
        }

        var share = (now - cpuTicksAtStart) / (double)callDuration.Ticks;
        System.Diagnostics.ThreadState? state = null;
        ThreadWaitReason? reason = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessThread thread in process.Threads)
            {
                if (thread.Id == nativeThreadId)
                {
                    state = thread.ThreadState;
                    reason = state == System.Diagnostics.ThreadState.Wait ? thread.WaitReason : null;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return WorkerThreadState.Undetermined;
        }

        return Classify(state, reason, share, options);
    }

    /// <summary>Tabela iz D7-5 (izdvojena radi testa).</summary>
    public static WorkerThreadState Classify(System.Diagnostics.ThreadState? state, ThreadWaitReason? reason, double cpuShare, StallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (state is null)
        {
            return WorkerThreadState.Undetermined;
        }

        if (cpuShare > options.HighCpuShare)
        {
            return WorkerThreadState.Spinning;
        }

        if (state == System.Diagnostics.ThreadState.Ready || (cpuShare < options.LowCpuShare && state != System.Diagnostics.ThreadState.Wait))
        {
            return WorkerThreadState.NoCpu;
        }

        if (state == System.Diagnostics.ThreadState.Wait && cpuShare < options.LowCpuShare)
        {
            _ = reason;
            return WorkerThreadState.Blocked;
        }

        return WorkerThreadState.Undetermined;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(int access, bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Zahtev za kontrolisano ponovno pokretanje Engine-a (posle treceg zaglavljenog poziva; D7-4).</summary>
public sealed record RestartRequest(bool Immediate, string Reason, IReadOnlyList<string> StuckTracks);

internal static class NativeThread
{
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
