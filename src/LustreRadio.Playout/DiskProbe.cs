using System.Runtime.InteropServices;
using System.Text;
using LustreRadio.Audio;
using Microsoft.Win32.SafeHandles;

namespace LustreRadio.Playout;

/// <summary>
/// Proba diska (plan faze B, 2.7): drugi fajl sa ISTOG volumena (isti GUID), procitan mimo Windows kesa
/// (<c>FILE_FLAG_NO_BUFFERING</c>), u roku P. Tri ishoda; samo "disk radi" dozvoljava prelaz na sledecu pesmu.
/// "Neodredjeno": nema druge pesme na volumenu, mrezni folder, volumen na vise diskova, upit ne uspe, citanje ne uspe.
/// </summary>
public sealed class VolumeDiskProbe : IDiskProbe
{
    public const int ProbeBytes = 64 * 1024;

    private readonly Func<IReadOnlyList<string>> _libraryFiles;
    private readonly Random _random = new();
    private readonly Dictionary<string, string?> _volumeOfFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private Thread? _stillRunning;

    public VolumeDiskProbe(Func<IReadOnlyList<string>> libraryFiles) => _libraryFiles = libraryFiles ?? throw new ArgumentNullException(nameof(libraryFiles));

    /// <summary>Samo za test (B-128): umesto stvarnog citanja.</summary>
    internal Func<string, bool>? ReadOverride { get; set; }

    /// <summary>Poslednji razlog za "neodredjeno" (za dnevnik i test).</summary>
    public string? LastReason { get; private set; }

    public DiskProbeOutcome Probe(string? stuckFile, TimeSpan timeout)
    {
        lock (_sync)
        {
            if (_stillRunning is { IsAlive: true })
            {
                LastReason = "prethodna proba jos stoji";
                return DiskProbeOutcome.NotResponding;
            }

            var candidate = Candidate(stuckFile, out var reason);
            if (candidate is null)
            {
                LastReason = reason;
                return DiskProbeOutcome.Undetermined;
            }

            var ok = false;
            string? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    ok = ReadOverride?.Invoke(candidate) ?? ReadUnbuffered(candidate, aligned: true, _random);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    error = ex.Message;
                }
            })
            { IsBackground = true, Name = "LustreRadio.ProbaDiska" };
            thread.Start();
            if (!thread.Join(timeout))
            {
                _stillRunning = thread;
                LastReason = "citanje nije zavrseno u roku";
                return DiskProbeOutcome.NotResponding;
            }

            LastReason = ok ? null : "citanje nije uspelo: " + error;
            return ok ? DiskProbeOutcome.DiskWorks : DiskProbeOutcome.Undetermined;
        }
    }

    /// <summary>Izbor probnog fajla: samo sa volumena zaglavljenog fajla; null + razlog ako proba nije moguca.</summary>
    public string? Candidate(string? stuckFile, out string reason)
    {
        if (string.IsNullOrEmpty(stuckFile))
        {
            reason = "nije poznat zaglavljeni fajl";
            return null;
        }

        if (stuckFile.StartsWith(@"\\", StringComparison.Ordinal) && !stuckFile.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            reason = "mrezni folder";
            return null;
        }

        var volume = VolumeOf(Path.GetDirectoryName(stuckFile) ?? stuckFile);
        if (volume is null)
        {
            reason = "volumen nije moguce utvrditi";
            return null;
        }

        var disks = DiskCount(volume);
        if (disks != 1)
        {
            reason = disks < 0 ? "upit za diskove nije uspeo" : "volumen je na vise diskova";
            return null;
        }

        var same = _libraryFiles()
            .Where(f => !string.Equals(f, stuckFile, StringComparison.OrdinalIgnoreCase) && string.Equals(VolumeOf(Path.GetDirectoryName(f) ?? f), volume, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (same.Count == 0)
        {
            reason = "na istom volumenu nema druge pesme";
            return null;
        }

        reason = string.Empty;
        return same[_random.Next(same.Count)];
    }

    private string? VolumeOf(string folder)
    {
        lock (_volumeOfFolder)
        {
            if (!_volumeOfFolder.TryGetValue(folder, out var volume))
            {
                volume = VolumeGuid(folder);
                _volumeOfFolder[folder] = volume;
            }

            return volume;
        }
    }

    /// <summary>GUID putanja volumena (<c>\\?\Volume{GUID}\</c>) za zadatu putanju, ili null.</summary>
    public static string? VolumeGuid(string path)
    {
        var mount = new StringBuilder(1024);
        if (!GetVolumePathNameW(path, mount, mount.Capacity))
        {
            return null;
        }

        var name = new StringBuilder(1024);
        return GetVolumeNameForVolumeMountPointW(mount.ToString(), name, name.Capacity) ? name.ToString() : null;
    }

    /// <summary>Broj fizickih diskova volumena (IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS); -1 ako upit ne uspe.</summary>
    public static int DiskCount(string volumeGuidPath)
    {
        using var handle = CreateFileW(volumeGuidPath.TrimEnd('\\'), 0, 0x7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return -1;
        }

        var buffer = new byte[8 + (24 * 4)];
        if (DeviceIoControl(handle, 0x00560000, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            return BitConverter.ToInt32(buffer, 0);
        }

        return Marshal.GetLastWin32Error() == 234 ? 2 : -1; // ERROR_MORE_DATA: vise diskova
    }

    /// <summary>
    /// Cita 64 KB mimo kesa sa nasumicnog mesta poravnatog na 4.096 bajtova, u poravnat bafer.
    /// <paramref name="aligned"/> = false: namerno neporavnato citanje (B-192: mora da vrati gresku).
    /// </summary>
    public static unsafe bool ReadUnbuffered(string path, bool aligned, Random? random = null)
    {
        const FileOptions noBuffering = (FileOptions)0x20000000;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, noBuffering);
        var length = RandomAccess.GetLength(handle);
        const int sector = 4096;
        var blocks = Math.Max(1, (length - ProbeBytes) / sector);
        var offset = aligned ? (random ?? Random.Shared).NextInt64(0, blocks) * sector : 1;
        var memory = (byte*)NativeMemory.AlignedAlloc(ProbeBytes + sector, sector);
        try
        {
            var span = aligned ? new Span<byte>(memory, ProbeBytes) : new Span<byte>(memory + 1, ProbeBytes - 1);
            _ = RandomAccess.Read(handle, span, offset);
            return true;
        }
        finally
        {
            NativeMemory.AlignedFree(memory);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
}
