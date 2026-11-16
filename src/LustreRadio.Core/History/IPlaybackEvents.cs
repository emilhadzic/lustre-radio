namespace LustreRadio.Core.History;

/// <summary>
/// Dogadjaji emitovanja koje prave reditelj i sekvencer (do B3/B4, kad ih preuzimaju koordinator i kartice).
/// Svaki poziv samo upisuje zapis u crnu kutiju: ne ceka, ne zakljucava, ne zauzima memoriju i ne dira bazu.
/// </summary>
public interface IPlaybackEvents
{
    /// <summary>Pesma je izabrana i otvorena (zapis <c>pripremljena</c>).</summary>
    void Prepared(Guid playbackId, long trackId, byte source, byte part, byte tier, string? program);

    /// <summary>Pesma krenula bez pretapanja.</summary>
    void Started(Guid playbackId, long trackId, long frame);

    /// <summary>Pretapanje pocelo: A je stigla do tacke prelaza, B je krenula. Jedan zapis.</summary>
    void Crossfade(Guid playbackIdB, long trackIdB, Guid playbackIdA, long trackIdA, long frame);

    /// <summary>Pesma (ili rep pesme A) odsvirana do poslednjeg uzorka.</summary>
    void Ended(Guid playbackId, long trackId, long frame, int positionMs);

    /// <summary>Pesma prekinuta, uz razlog.</summary>
    void Stopped(Guid playbackId, long trackId, StopReason reason, long frame, int positionMs);

    /// <summary>Stavka nije svirala (nije ucitana, ili gasenje pre pocetka).</summary>
    void Discarded(Guid playbackId, long trackId, StopReason reason);
}
