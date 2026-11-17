using LustreRadio.Core.Voice;
using LustreRadio.Data;
using Xunit;
using Xunit.Abstractions;

namespace LustreRadio.Playout.Tests;

/// <summary>FINAL, korak 3: najave kroz pravi radio. Kljucni rizici: najava ne prekida pesmu, muzika ide dalje, bez licence nema najave, najava ne ulazi u istoriju pesama.</summary>
public sealed class VoiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _voiceSource = Path.Combine(Path.GetTempPath(), "lustre-voice-" + Guid.NewGuid().ToString("N"));

    private static readonly VoiceLicense Complete = new()
    {
        Service = "ElevenLabs", ModelAndVoice = "Eleven v3, Lustre F", Plan = "Starter", PaymentProof = "test-001", GeneratedOn = "17.11.2026.", TermsSnapshot = "uslovi-2026-11-17.pdf",
    };

    public void Dispose()
    {
        if (Directory.Exists(_voiceSource))
        {
            Directory.Delete(_voiceSource, recursive: true);
        }
    }

    private (RadioSession Session, IReadOnlyList<PlayRow> Plays, IReadOnlyList<Audio.TrackEvent> Events) Play(VoiceLicense license, bool approve)
    {
        using var env = new TestEnvironment();
        foreach (var hz in new[] { 300, 500, 700 })
        {
            env.AddTone(hz, 15);
        }

        env.Index();
        foreach (var hz in new[] { 300, 500, 700 })
        {
            env.Verify(hz);
        }

        Directory.CreateDirectory(_voiceSource);
        var file = Path.Combine(_voiceSource, "najava.wav");
        TestEnvironment.WriteTone(file, 1500, 3);
        var library = new VoiceLibrary(env.Db, Path.Combine(_voiceSource, "voice"));
        var entry = library.Import(file, "Dobro dosli u Caffe Lustre", VoiceCategory.Ident, VoiceLanguage.Serbian, VoiceKind.Female, license, TestEnvironment.Format);
        Assert.Equal(approve, library.Approve(entry.Id, "test"));

        var (director, clock) = env.Director(new DateTime(2026, 11, 16, 10, 0, 0));
        director.Voice = library;
        var session = TestEnvironment.Session(director, clock);
        TestEnvironment.Render(session, 60);
        return (session, env.Plays(session), session.Sequencer.Events.ToList());
    }

    [Fact] // odobrena najava sa licencom: ide izmedju pesama, posle prirodnog kraja pesme; muzika se nastavlja; nije u istoriji pesama
    public void ApprovedVoice_PlaysBetweenSongs_NeverCutsASong_MusicContinues()
    {
        var (_, plays, events) = Play(Complete, approve: true);
        var i = events.ToList().FindIndex(e => e.Id.StartsWith(RadioDirector.VoicePrefix, StringComparison.Ordinal));
        output.WriteLine(string.Join(", ", events.Select(e => $"{e.Id}@{e.StartFrame / 44100.0:F1}s")));
        Assert.True(i > 0, "najava nije pustena");
        var song = events[i - 1];
        Assert.True(events[i].StartFrame - song.StartFrame >= (15 - 0.1) * 44100, "pesma pre najave je presecena");
        Assert.True(events.Count > i + 1 && !events[i + 1].Id.StartsWith(RadioDirector.VoicePrefix, StringComparison.Ordinal), "posle najave nije krenula pesma");
        Assert.DoesNotContain(plays, p => p.TrackId < 0);
    }

    [Fact] // licenca nije potpuna: najava ne moze da se odobri i nikad ne ide u eter (D10)
    public void IncompleteLicense_NeverOnAir()
    {
        var (_, _, events) = Play(Complete with { PaymentProof = string.Empty }, approve: false);
        Assert.DoesNotContain(events, e => e.Id.StartsWith(RadioDirector.VoicePrefix, StringComparison.Ordinal));
    }
}
