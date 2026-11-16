using System.Runtime.InteropServices;
using LustreRadio.Audio;
using LustreRadio.Core.History;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>Faza B, korak B3: proba diska, razvrstavanje i provera gresaka, ponovno merenje.</summary>
public sealed class StallPlayoutTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lustre-probe-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // privremeni folder
        }
    }

    private string File(string name, int bytes = 512 * 1024)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact] // B-192: citanje mimo kesa je zaista ukljuceno: neporavnato citanje vraca gresku, poravnato uspeva
    public void B192_UnbufferedRead_AlignedWorks_UnalignedFails()
    {
        var path = File("a.bin");
        Assert.True(VolumeDiskProbe.ReadUnbuffered(path, aligned: true));
        var error = Record.Exception(() => VolumeDiskProbe.ReadUnbuffered(path, aligned: false));
        output.WriteLine("neporavnato: " + (error?.GetType().Name + ": " + error?.Message));
        Assert.NotNull(error);
    }

    [Fact] // B-191: probni fajl samo sa istog volumena; bez kandidata, mrezni folder -> neodredjeno
    public void B191_CandidateOnlyFromSameVolume_OtherwiseUndetermined()
    {
        var stuck = File("zaglavljen.bin");
        var other = File("drugi.bin");
        var volume = VolumeDiskProbe.VolumeGuid(_folder);
        output.WriteLine($"volumen privremenog foldera: {volume}; diskova: {(volume is null ? -1 : VolumeDiskProbe.DiskCount(volume))}");
        Assert.NotNull(volume);

        var otherVolumeFile = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName).FirstOrDefault(root => !string.Equals(VolumeDiskProbe.VolumeGuid(root), volume, StringComparison.OrdinalIgnoreCase));
        var probe = new VolumeDiskProbe(() => otherVolumeFile is null ? [stuck, other] : [stuck, other, Path.Combine(otherVolumeFile, "nepostojeci.mp3")]);
        var disks = VolumeDiskProbe.DiskCount(volume!);
        var candidate = probe.Candidate(stuck, out var reason);
        if (disks == 1)
        {
            Assert.Equal(other, candidate);
            Assert.Equal(DiskProbeOutcome.DiskWorks, probe.Probe(stuck, TimeSpan.FromSeconds(5)));
        }
        else
        {
            output.WriteLine($"upit za diskove: {disks} ({reason}) -> neodredjeno");
            Assert.Null(candidate);
        }

        var alone = new VolumeDiskProbe(() => [stuck]);
        Assert.Null(alone.Candidate(stuck, out reason));
        Assert.Equal(DiskProbeOutcome.Undetermined, alone.Probe(stuck, TimeSpan.FromSeconds(1)));
        output.WriteLine("bez kandidata: " + reason);

        Assert.Null(probe.Candidate(@"\\server\muzika\a.mp3", out reason));
        Assert.Equal("mrezni folder", reason);
        Assert.Null(probe.Candidate(null, out _));
    }

    [Fact] // B-128: dok disk (oponasano) stoji, proba ne javlja "disk radi"; druga proba dok prva stoji -> ne odgovara
    public void B128_BlockedDisk_ProbeNeverReportsDiskWorks()
    {
        var stuck = File("z.bin");
        File("d.bin");
        var gate = new ManualResetEventSlim();
        var probe = new VolumeDiskProbe(() => Directory.GetFiles(_folder)) { ReadOverride = _ => gate.Wait(TimeSpan.FromSeconds(30)) };
        if (VolumeDiskProbe.VolumeGuid(_folder) is not { } v || VolumeDiskProbe.DiskCount(v) != 1)
        {
            output.WriteLine("volumen nije na jednom disku; proba je neodredjena (i to je bezbedan smer)");
            Assert.Equal(DiskProbeOutcome.Undetermined, probe.Probe(stuck, TimeSpan.FromSeconds(1)));
            return;
        }

        Assert.Equal(DiskProbeOutcome.NotResponding, probe.Probe(stuck, TimeSpan.FromSeconds(1)));
        Assert.Equal(DiskProbeOutcome.NotResponding, probe.Probe(stuck, TimeSpan.FromSeconds(1)));
        gate.Set();
        Thread.Sleep(200);
        probe.ReadOverride = _ => true;
        Assert.Equal(DiskProbeOutcome.DiskWorks, probe.Probe(stuck, TimeSpan.FromSeconds(1)));
    }

    [Theory] // B-129: razvrstavanje prestanka dekodiranja
    [InlineData("neispravni-podaci", StopReason.FileError, DecodeFailureKind.ExplicitDecoderError)]
    [InlineData("mf-neispravan-tok", StopReason.FileError, DecodeFailureKind.ExplicitDecoderError)]
    [InlineData("mf-nepoznata", StopReason.FileError, DecodeFailureKind.UndeterminedDecoderError)]
    [InlineData("citanje", StopReason.FileUnavailable, DecodeFailureKind.ReadError)]
    [InlineData("pristup", StopReason.FileUnavailable, DecodeFailureKind.ReadError)]
    [InlineData("prerani-kraj", StopReason.UnexpectedEnd, DecodeFailureKind.UnexpectedEnd)]
    [InlineData("nepoznata", StopReason.FileError, DecodeFailureKind.UndeterminedDecoderError)]
    public void B129_FailureClassification(string error, StopReason reason, DecodeFailureKind expected)
    {
        Exception? ex = error switch
        {
            "neispravni-podaci" => new InvalidDataException(),
            "mf-neispravan-tok" => new COMException("MF", unchecked((int)0xC00D36CB)),
            "mf-nepoznata" => new COMException("MF", unchecked((int)0x80004005)),
            "citanje" => new IOException("uredjaj nije spreman"),
            "pristup" => new UnauthorizedAccessException(),
            "prerani-kraj" => null,
            _ => new InvalidOperationException(),
        };
        Assert.Equal(expected, DecodeFailures.Classify(reason, ex));
    }

    private sealed class FailingSource(long failAt, Exception error, long length) : ISampleSource
    {
        private long _position;

        public AudioFormat Format => AudioFormat.Stereo44100;

        public long? LengthFrames => length;

        public TimeSpan Delay { get; init; }

        public int Read(Span<float> buffer)
        {
            if (Delay > TimeSpan.Zero)
            {
                Thread.Sleep(Delay);
            }

            if (_position >= failAt)
            {
                throw error;
            }

            var frames = (int)Math.Min(buffer.Length / 2, Math.Min(failAt, length) - _position);
            if (frames <= 0)
            {
                return 0;
            }

            _position += frames;
            return frames;
        }

        public void Dispose()
        {
        }
    }

    [Fact] // B-189: potvrda eksplicitne greske
    public void B189_ConfirmExplicitError()
    {
        var format = AudioFormat.Stereo44100;
        var original = new InvalidDataException("los blok");
        var at = 10L * 44100;
        Assert.Equal(FaultCheck.Confirmed, DecodeFailures.Confirm(() => new FailingSource(at, new InvalidDataException(), 60 * 44100), () => true, original, at, format, TimeSpan.FromSeconds(10)));
        Assert.Equal(FaultCheck.NotRepeated, DecodeFailures.Confirm(() => new FailingSource(long.MaxValue, original, 60 * 44100), () => true, original, at, format, TimeSpan.FromSeconds(10)));
        Assert.Equal(FaultCheck.NotRepeated, DecodeFailures.Confirm(() => new FailingSource(at + (3 * 44100), new InvalidDataException(), 60 * 44100), () => true, original, at, format, TimeSpan.FromSeconds(10)));
        Assert.Equal(FaultCheck.FileChanged, DecodeFailures.Confirm(() => new FailingSource(at, original, 60 * 44100), () => false, original, at, format, TimeSpan.FromSeconds(10)));
        Assert.Equal(FaultCheck.CheckFailed, DecodeFailures.Confirm(() => new FailingSource(at, original, 60 * 44100) { Delay = TimeSpan.FromMilliseconds(50) }, () => true, original, at, format, TimeSpan.FromMilliseconds(200)));
    }

    [Fact] // B-189 kroz reditelja: samo potvrdjena greska ulazi u brojac gresaka
    public async Task B189_OnlyConfirmedErrorIsCounted()
    {
        using var env = new TestEnvironment();
        env.AddTone(440, 30);
        env.Index();
        env.Verify(440);
        var (director, _) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        var track = env.TrackOf(440);
        var id = TestEnvironment.IdOf(track);
        var queued = new QueuedTrack(id, new FailingSource(1, new InvalidDataException(), 1));
        var at = 10L * 44100;

        director.DecoderFactory = _ => new FailingSource(long.MaxValue, new InvalidDataException(), 30 * 44100);
        var check = await director.HandleDecodeFailure(new DecodeFailure(queued, StopReason.FileError, new InvalidDataException(), at));
        Assert.Equal(FaultCheck.NotRepeated, check);
        Assert.Equal(0, env.Db.Tracks.Get(track.Id)!.FaultCount);

        director.DecoderFactory = _ => new FailingSource(at, new InvalidDataException(), 30 * 44100);
        check = await director.HandleDecodeFailure(new DecodeFailure(queued, StopReason.FileError, new InvalidDataException(), at));
        Assert.Equal(FaultCheck.Confirmed, check);
        Assert.Equal(1, env.Db.Tracks.Get(track.Id)!.FaultCount);

        check = await director.HandleDecodeFailure(new DecodeFailure(queued, StopReason.FileError, new InvalidOperationException(), at));
        Assert.Null(check); // neodredjena greska se ne proverava i ne broji
        check = await director.HandleDecodeFailure(new DecodeFailure(queued, StopReason.FileUnavailable, new IOException(), at));
        Assert.Null(check);
        Assert.Equal(1, env.Db.Tracks.Get(track.Id)!.FaultCount);
    }

    [Fact] // B-190: neocekivan kraj: ne broji se; ponovno merenje ispravlja duzinu ili belezi da se nije ponovilo
    public async Task B190_UnexpectedEnd_RemeasureCorrectsLength()
    {
        using var env = new TestEnvironment();
        env.AddTone(440, 30);
        env.Index();
        env.Verify(440);
        var (director, _) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        var track = env.TrackOf(440);
        var queued = new QueuedTrack(TestEnvironment.IdOf(track), new FailingSource(1, new InvalidDataException(), 1));
        var shortEnd = 20L * 44100;

        director.DecoderFactory = _ => new FailingSource(long.MaxValue, new InvalidDataException(), 30 * 44100);
        await director.HandleDecodeFailure(new DecodeFailure(queued, StopReason.UnexpectedEnd, null, shortEnd));
        Assert.Equal(track.ExactFrames, env.Db.Tracks.Get(track.Id)!.ExactFrames); // nije se ponovilo: duzina ista

        director.DecoderFactory = _ => new FailingSource(long.MaxValue, new InvalidDataException(), shortEnd);
        await director.HandleDecodeFailure(new DecodeFailure(queued, StopReason.UnexpectedEnd, null, shortEnd));
        Assert.Equal(shortEnd, env.Db.Tracks.Get(track.Id)!.ExactFrames); // ponovilo se: duzina ispravljena
        Assert.Equal(0, env.Db.Tracks.Get(track.Id)!.FaultCount);
    }
}
