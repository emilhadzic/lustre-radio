using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Unicode;

namespace LustreRadio.Core.History;

/// <summary>
/// Crna kutija (plan faze B, 2.2, odluka D8-1): red izmedju onih koji prave dogadjaje istorije i pisara, u fajlu
/// mapiranom u memoriju (<c>journal\crna_kutija.bin</c>), stalne velicine. Ono sto je upisano pripada Windows-u, pa
/// prezivljava pad procesa. Nestanak struje ili pad Windows-a pre upisa na disk: ne (cuvar upisuje na 5 s).
/// <para>Bez brave i bez zauzimanja memorije: mesto za zapis se rezervise atomskim brojacem
/// (<c>Interlocked.CompareExchange</c>), kontrolni zbir se upisuje poslednji. Kad je puna, novi dogadjaj se ne upisuje,
/// nego se broji kao izgubljen (broj i vreme od–do su u zaglavlju, u istom fajlu).</para>
/// <para>Ako fajl ne moze da se napravi ili mapira, isti red radi u obicnoj memoriji (<see cref="Available"/> = false).</para>
/// </summary>
public sealed unsafe class BlackBox : IDisposable
{
    public const int DefaultCapacity = 10_000;
    public const int RecordSize = 128;
    public const int HeaderSize = 4096;
    public const string FileName = "crna_kutija.bin";

    private const uint Magic = 0x4B42524C; // "LRBK"
    private const int Version = 1;
    private const int CrcOffset = RecordSize - 4;

    // Zaglavlje (pomaci u bajtovima)
    private const int HMagic = 0;
    private const int HVersion = 4;
    private const int HCapacity = 8;
    private const int HRun = 16;
    private const int HReserved = 32;
    private const int HConfirmed = 40;
    private const int HLostCount = 48;
    private const int HLostFirst = 56;
    private const int HLostLast = 64;
    private const int HLostReported = 72;
    private const int HLastWritten = 80;

    private readonly FileStream? _file;
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte* _base;
    private readonly long _size;
    private long _flushes;
    private bool _disposed;

    private BlackBox(int capacity, FileStream? file, MemoryMappedFile? map, MemoryMappedViewAccessor? view, byte* basePointer, string? unavailableReason)
    {
        Capacity = capacity;
        _file = file;
        _map = map;
        _view = view;
        _base = basePointer;
        _size = HeaderSize + ((long)capacity * RecordSize);
        UnavailableReason = unavailableReason;
    }

    public int Capacity { get; }

    /// <summary>true: fajl mapiran u memoriju; false: obicna memorija (pad procesa brise sadrzaj).</summary>
    public bool Available => _map is not null;

    public string? UnavailableReason { get; }

    public Guid Run => ReadGuid(HRun);

    /// <summary>Poslednji dodeljen redni broj (rid) u ovom pokretanju.</summary>
    public long Reserved => Volatile.Read(ref *(long*)(_base + HReserved));

    /// <summary>Dokle je pisar potvrdio da je u dnevniku na disku (<c>potvrdjeno_do</c>).</summary>
    public long Confirmed => Volatile.Read(ref *(long*)(_base + HConfirmed));

    public long LostCount => Volatile.Read(ref *(long*)(_base + HLostCount));

    public long LostReported => Volatile.Read(ref *(long*)(_base + HLostReported));

    public DateTime? LostFirstUtc => Ticks(HLostFirst);

    public DateTime? LostLastUtc => Ticks(HLostLast);

    /// <summary>Koliko puta je trazen upis na disk (cuvar).</summary>
    public long Flushes => Interlocked.Read(ref _flushes);

    /// <summary>Otvara (ili pravi) crnu kutiju; kad to ne uspe, vraca crnu kutiju u memoriji sa razlogom.</summary>
    public static BlackBox Open(string path, int capacity = DefaultCapacity, Action<string>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        var size = HeaderSize + ((long)capacity * RecordSize);
        FileStream? file = null;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length != size)
            {
                if (file.Length != 0)
                {
                    // Druga velicina (drugi kapacitet): stari fajl se ne brise, nego cuva pored.
                    file.Dispose();
                    var keep = path + ".stari-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Move(path, keep);
                    log?.Invoke("Crna kutija drugacije velicine sacuvana kao " + keep);
                    file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                }

                file.SetLength(size);
            }

            map = MemoryMappedFile.CreateFromFile(file, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            view = map.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += view.PointerOffset;
            return new BlackBox(capacity, file, map, view, pointer, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            view?.Dispose();
            map?.Dispose();
            file?.Dispose();
            log?.Invoke("Crna kutija nedostupna (" + ex.Message + "); istorija ide kroz red u memoriji.");
            return InMemory(capacity, ex.Message);
        }
    }

    /// <summary>Red u obicnoj memoriji (rezerva kad fajl nije dostupan).</summary>
    public static BlackBox InMemory(int capacity, string reason)
    {
        var size = HeaderSize + ((long)capacity * RecordSize);
        var pointer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        var box = new BlackBox(capacity, null, null, null, pointer, reason);
        box.Reset(Guid.NewGuid());
        return box;
    }

    /// <summary>Stanje zateceno pri pokretanju: ispravni zapisi posle <c>potvrdjeno_do</c> i podaci o gubitku.</summary>
    public RecoveredBox ReadForRecovery()
    {
        if (*(uint*)(_base + HMagic) != Magic || *(int*)(_base + HVersion) != Version || *(int*)(_base + HCapacity) != Capacity)
        {
            return new RecoveredBox(Guid.Empty, [], 0, null, null, 0);
        }

        var run = Run;
        var reserved = Reserved;
        var confirmed = Confirmed;
        var records = new List<JournalRecord>();
        var invalid = 0;
        for (var seq = Math.Max(confirmed + 1, reserved - Capacity + 1); seq <= reserved; seq++)
        {
            if (TryRead(seq, out var record))
            {
                records.Add(record);
            }
            else
            {
                invalid++;
            }
        }

        var unreported = LostCount - LostReported;
        return new RecoveredBox(run, records, Math.Max(0, unreported), LostFirstUtc, LostLastUtc, invalid);
    }

    /// <summary>Prazni crnu kutiju i zapocinje novo pokretanje (posle oporavka).</summary>
    public void Reset(Guid run)
    {
        new Span<byte>(_base, checked((int)_size)).Clear();
        *(uint*)(_base + HMagic) = Magic;
        *(int*)(_base + HVersion) = Version;
        *(int*)(_base + HCapacity) = Capacity;
        WriteGuid(HRun, run);
        Flush();
    }

    /// <summary>
    /// Upisuje zapis i dodeljuje mu redni broj (rid). Ne ceka, ne zakljucava, ne zauzima memoriju.
    /// Vraca false ako je crna kutija puna: dogadjaj se broji kao izgubljen.
    /// </summary>
    public bool TryAppend(ref JournalRecord record)
    {
        var reservedPointer = (long*)(_base + HReserved);
        long seq;
        while (true)
        {
            var reserved = Volatile.Read(ref *reservedPointer);
            var confirmed = Volatile.Read(ref *(long*)(_base + HConfirmed));
            if (reserved - confirmed >= Capacity)
            {
                CountLost(record.TimeTicks != 0 ? record.TimeTicks : DateTime.UtcNow.Ticks);
                return false;
            }

            if (Interlocked.CompareExchange(ref *reservedPointer, reserved + 1, reserved) == reserved)
            {
                seq = reserved + 1;
                break;
            }
        }

        record.Run = Run;
        record.Seq = seq;
        Span<byte> buffer = stackalloc byte[RecordSize];
        Serialize(ref record, buffer);
        var slot = Slot(seq);
        Volatile.Write(ref *(uint*)(slot + CrcOffset), 0u);
        buffer[..CrcOffset].CopyTo(new Span<byte>(slot, CrcOffset));
        Volatile.Write(ref *(uint*)(slot + CrcOffset), BinaryPrimitives.ReadUInt32LittleEndian(buffer[CrcOffset..]));
        Volatile.Write(ref *(long*)(_base + HLastWritten), record.TimeTicks);
        return true;
    }

    /// <summary>Cita zapis sa zadatim rednim brojem; false ako jos nije upisan do kraja ili je ostecen.</summary>
    public bool TryRead(long seq, out JournalRecord record)
    {
        record = default;
        if (seq <= 0)
        {
            return false;
        }

        Span<byte> copy = stackalloc byte[RecordSize];
        var slot = Slot(seq);
        var crc = Volatile.Read(ref *(uint*)(slot + CrcOffset));
        new ReadOnlySpan<byte>(slot, CrcOffset).CopyTo(copy);
        if (crc == 0 || Crc32.Compute(copy[..CrcOffset]) != crc)
        {
            return false;
        }

        Deserialize(copy, out record);
        return record.Seq == seq && record.Run == Run;
    }

    /// <summary>Pisar: sve do <paramref name="seq"/> je potvrdjeno u dnevniku na disku.</summary>
    public void Confirm(long seq)
    {
        var pointer = (long*)(_base + HConfirmed);
        if (seq > Volatile.Read(ref *pointer))
        {
            Volatile.Write(ref *pointer, seq);
        }
    }

    /// <summary>Zapis <c>gubitak</c> za ovoliko dogadjaja je upisan (ili ce biti upisan).</summary>
    public void MarkLostReported(long count) => Volatile.Write(ref *(long*)(_base + HLostReported), count);

    /// <summary>Trazi od Windows-a upis na disk (FlushViewOfFile + FlushFileBuffers).</summary>
    public void Flush()
    {
        if (_view is null || _disposed)
        {
            return;
        }

        _view.Flush();
        _file!.Flush(flushToDisk: true);
        Interlocked.Increment(ref _flushes);
    }

    /// <summary>Samo za test (B-226): upisuje samo prvih <paramref name="bytes"/> bajtova zapisa, kao pad tokom upisa.</summary>
    internal long WriteTorn(ref JournalRecord record, int bytes)
    {
        var seq = Interlocked.Increment(ref *(long*)(_base + HReserved));
        record.Run = Run;
        record.Seq = seq;
        Span<byte> buffer = stackalloc byte[RecordSize];
        Serialize(ref record, buffer);
        var slot = Slot(seq);
        *(uint*)(slot + CrcOffset) = 0;
        buffer[..Math.Min(bytes, RecordSize)].CopyTo(new Span<byte>(slot, Math.Min(bytes, RecordSize)));
        return seq;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_view is not null)
        {
            Flush();
            _disposed = true;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _map!.Dispose();
            _file!.Dispose();
        }
        else
        {
            _disposed = true;
            NativeMemory.Free(_base);
        }
    }

    private byte* Slot(long seq) => _base + HeaderSize + (((seq - 1) % Capacity) * RecordSize);

    private void CountLost(long ticks)
    {
        Interlocked.Increment(ref *(long*)(_base + HLostCount));
        Interlocked.CompareExchange(ref *(long*)(_base + HLostFirst), ticks, 0);
        Interlocked.Exchange(ref *(long*)(_base + HLostLast), ticks);
    }

    private DateTime? Ticks(int offset)
    {
        var ticks = Volatile.Read(ref *(long*)(_base + offset));
        return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }

    private Guid ReadGuid(int offset) => new(new ReadOnlySpan<byte>(_base + offset, 16));

    private void WriteGuid(int offset, Guid value) => _ = value.TryWriteBytes(new Span<byte>(_base + offset, 16));

    private static void Serialize(ref JournalRecord r, Span<byte> b)
    {
        b.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(b, r.Seq);
        _ = r.Run.TryWriteBytes(b[8..24]);
        b[24] = (byte)r.Kind;
        b[25] = (byte)r.Reason;
        b[26] = (byte)r.Unconfirmed;
        b[27] = r.Source;
        b[28] = r.Part;
        b[29] = r.Tier;
        _ = r.PlaybackId.TryWriteBytes(b[32..48]);
        _ = r.PlaybackIdA.TryWriteBytes(b[48..64]);
        BinaryPrimitives.WriteInt64LittleEndian(b[64..], r.Track);
        BinaryPrimitives.WriteInt64LittleEndian(b[72..], r.TrackA);
        BinaryPrimitives.WriteInt64LittleEndian(b[80..], r.Frame);
        BinaryPrimitives.WriteInt64LittleEndian(b[88..], r.TimeTicks);
        BinaryPrimitives.WriteInt32LittleEndian(b[96..], r.PositionMs);
        if (r.Program is { Length: > 0 } program)
        {
            // Kraci naziv ako ne staje (bez zauzimanja memorije; uvek cela slova).
            _ = Utf8.FromUtf16(program, b.Slice(100, JournalRecord.ProgramBytes), out _, out _, replaceInvalidSequences: true, isFinalBlock: false);
        }

        var crc = Crc32.Compute(b[..CrcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(b[CrcOffset..], crc == 0 ? 1u : crc);
        if (crc == 0)
        {
            // 0 znaci "nije upisano"; takav zapis bi se odbacio. Verovatnoca 1 : 4 milijarde; tada se menja jedan bajt dopune.
            b[30] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(b[CrcOffset..], Crc32.Compute(b[..CrcOffset]));
        }
    }

    private static void Deserialize(ReadOnlySpan<byte> b, out JournalRecord r)
    {
        r = default;
        r.Seq = BinaryPrimitives.ReadInt64LittleEndian(b);
        r.Run = new Guid(b[8..24]);
        r.Kind = (JournalKind)b[24];
        r.Reason = (StopReason)b[25];
        r.Unconfirmed = (UnconfirmedKind)b[26];
        r.Source = b[27];
        r.Part = b[28];
        r.Tier = b[29];
        r.PlaybackId = new Guid(b[32..48]);
        r.PlaybackIdA = new Guid(b[48..64]);
        r.Track = BinaryPrimitives.ReadInt64LittleEndian(b[64..]);
        r.TrackA = BinaryPrimitives.ReadInt64LittleEndian(b[72..]);
        r.Frame = BinaryPrimitives.ReadInt64LittleEndian(b[80..]);
        r.TimeTicks = BinaryPrimitives.ReadInt64LittleEndian(b[88..]);
        r.PositionMs = BinaryPrimitives.ReadInt32LittleEndian(b[96..]);
        var program = b.Slice(100, JournalRecord.ProgramBytes);
        var end = program.IndexOf((byte)0);
        r.Program = (end < 0 ? program : program[..end]) is { Length: > 0 } p ? System.Text.Encoding.UTF8.GetString(p) : null;
    }
}

/// <summary>Sta je zateceno u crnoj kutiji pri pokretanju.</summary>
public sealed record RecoveredBox(Guid Run, IReadOnlyList<JournalRecord> Records, long UnreportedLost, DateTime? LostFromUtc, DateTime? LostToUtc, int InvalidRecords);
