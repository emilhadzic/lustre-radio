using LustreRadio.Audio;
using LustreRadio.Library;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Library.Tests;

public sealed class ScannerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lustre-lib-" + Guid.NewGuid().ToString("N"));

    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "audio", file);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("chirp_44k_stereo.flac", AudioKind.Flac, 44100, 2, 176400)]
    [InlineData("chirp_96k_stereo.flac", AudioKind.Flac, 96000, 2, 384000)]
    [InlineData("chirp_44k_stereo.ogg", AudioKind.OggVorbis, 44100, 2, 176400)]
    [InlineData("chirp_44k_stereo_cbr.mp3", AudioKind.Mp3, 44100, 2, 0)]
    [InlineData("chirp_44k_mono.mp3", AudioKind.Mp3, 44100, 1, 0)]
    [InlineData("chirp_48k_stereo.mp3", AudioKind.Mp3, 48000, 2, 0)]
    [InlineData("chirp_44k_stereo.m4a", AudioKind.Mp4, 44100, 2, 0)]
    [InlineData("chirp_44k_stereo.aac", AudioKind.AacAdts, 44100, 2, 0)]
    public void Scan_MeasuresRealFile(string file, AudioKind kind, int rate, int channels, long exactFrames)
    {
        var scanned = TrackScanner.Scan(Fixture(file));
        output.WriteLine($"{file}: ok={scanned.DecodeOk} dekoder={scanned.DecoderId} frejmova={scanned.ExactFrames} trajanje={scanned.DurationSeconds:F3}s vrh={scanned.PeakDbfs:F2} dBFS");

        Assert.True(scanned.DecodeOk, scanned.DecodeError);
        Assert.Equal(kind, scanned.Kind);
        Assert.Equal(rate, scanned.SampleRate);
        Assert.Equal(channels, scanned.Channels);
        Assert.Equal(64, scanned.ContentHash.Length);
        Assert.Equal(new FileInfo(Fixture(file)).Length, scanned.SizeBytes);
        Assert.InRange(scanned.DurationSeconds, 3.95, 4.15);
        // Signal ima amplitudu 0,5 = -6 dBFS. Kompresovani formati vracaju nesto vise vrhove
        // (izmereno: AAC do -2,8 dBFS); zato se vrh meri na dekodiranom zvuku, a ne pretpostavlja.
        Assert.InRange(scanned.PeakDbfs!.Value, -7.0, -2.0);
        if (exactFrames > 0)
        {
            Assert.Equal(exactFrames, scanned.ExactFrames);
        }
    }

    [Fact]
    public void Scan_ReadsTags_OrFallsBackToFileName()
    {
        var tagged = TrackScanner.Scan(Fixture("chirp_44k_stereo_art.mp3"));
        Assert.Equal("Chirp", tagged.Title);
        Assert.Equal("Lustre Test", tagged.Artist);

        var plain = TrackScanner.Scan(Fixture("chirp_44k_stereo.ogg"));
        Assert.False(string.IsNullOrWhiteSpace(plain.Title));
    }

    [Fact]
    public void Scan_SameContent_SameHash_DifferentContent_DifferentHash()
    {
        Directory.CreateDirectory(_directory);
        var copy = Path.Combine(_directory, "kopija.mp3");
        File.Copy(Fixture("chirp_44k_stereo_cbr.mp3"), copy);

        var original = TrackScanner.Scan(Fixture("chirp_44k_stereo_cbr.mp3"));
        Assert.Equal(original.ContentHash, TrackScanner.Scan(copy).ContentHash);
        Assert.NotEqual(original.ContentHash, TrackScanner.Scan(Fixture("chirp_44k_stereo_vbr.mp3")).ContentHash);
        Assert.Equal(original.ContentHash, TrackScanner.HashFile(copy));
    }

    [Fact]
    public void Scan_BadFiles_ReportProblem_WithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        var garbage = Path.Combine(_directory, "smece.mp3");
        var bytes = new byte[40_000];
        new Random(3).NextBytes(bytes);
        bytes[0] = 0;
        File.WriteAllBytes(garbage, bytes);
        var empty = Path.Combine(_directory, "prazno.flac");
        File.WriteAllBytes(empty, []);

        foreach (var path in new[] { garbage, empty, Fixture("chirp_48k_stereo.opus"), Path.Combine(_directory, "nema.mp3") })
        {
            var scanned = TrackScanner.Scan(path);
            output.WriteLine($"{Path.GetFileName(path)}: {scanned.DecodeError}");
            Assert.False(scanned.DecodeOk);
            Assert.False(string.IsNullOrWhiteSpace(scanned.DecodeError));
            Assert.Equal(0, scanned.ExactFrames);
        }
    }

    [Fact]
    public void Scan_WrongExtension_StillDecodes()
    {
        Directory.CreateDirectory(_directory);
        var renamed = Path.Combine(_directory, "zapravo-flac.mp3");
        File.Copy(Fixture("chirp_44k_stereo.flac"), renamed);

        var scanned = TrackScanner.Scan(renamed);
        Assert.True(scanned.DecodeOk, scanned.DecodeError);
        Assert.Equal(AudioKind.Flac, scanned.Kind);
        Assert.Equal(176400, scanned.ExactFrames);
    }

    [Fact]
    public void Enumerate_FindsOnlyAudioFiles_InSubfolders()
    {
        var sub = Path.Combine(_directory, "jazz", "album");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_directory, "a.mp3"), "x");
        File.WriteAllText(Path.Combine(_directory, "B.FLAC"), "x");
        File.WriteAllText(Path.Combine(sub, "c.ogg"), "x");
        File.WriteAllText(Path.Combine(sub, "omot.jpg"), "x");
        File.WriteAllText(Path.Combine(_directory, "licenca.pdf"), "x");

        var files = LibraryFiles.Enumerate([_directory, Path.Combine(_directory, "ne-postoji")]);

        Assert.Equal(["a.mp3", "B.FLAC", "c.ogg"], files.Select(Path.GetFileName));
    }

    [Fact]
    public void Diff_SeparatesNewChangedMissingAndUnchanged()
    {
        Directory.CreateDirectory(_directory);
        string Make(string name, string content)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        var same = Make("ista.mp3", "aaaa");
        var changedSize = Make("veca.mp3", "aaaaaaaa");
        var changedTime = Make("novija.mp3", "aaaa");
        var added = Make("nova.mp3", "aaaa");
        var gone = Path.Combine(_directory, "nestala.mp3");

        KnownFile Known(string path, long? size = null, DateTime? time = null)
        {
            var info = new FileInfo(path);
            return new KnownFile(path, size ?? info.Length, time ?? info.LastWriteTimeUtc);
        }

        var diff = LibraryFiles.Diff(
            [Known(same), Known(changedSize, size: 4), Known(changedTime, time: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), new KnownFile(gone, 4, DateTime.UtcNow)],
            LibraryFiles.Enumerate([_directory]));

        Assert.Equal([added], diff.New);
        Assert.Equal([changedTime, changedSize], diff.Changed);
        Assert.Equal([gone], diff.Missing);
        Assert.Equal([same], diff.Unchanged);
    }
}
