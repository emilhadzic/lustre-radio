using LustreRadio.Core.History;

namespace LustreRadio.Audio;

/// <summary>
/// Kartice (plan faze B, 2.2, odluke D7-3 i D8-3): 16 unapred napravljenih kartica izmedju niti za mesanje i koordinatora.
/// <para>Svako polje ima tacno jednog pisca i upisuje se najvise jednom dok je kartica u upotrebi. Uz svaku grupu polja je
/// zastavica: pisac prvo upise podatke, pa zastavicu (release, <c>Volatile.Write</c>); citalac prvo procita zastavicu
/// (acquire, <c>Volatile.Read</c>), pa podatke. Nit za mesanje ne ceka, ne zakljucava i ne zauzima memoriju.</para>
/// <para>Rucka = redni broj kartice (nizih 32 bita) + generacija (visih 32 bita). Generacija se povecava pri svakom
/// oslobadjanju, pa stara rucka ne moze da pogodi novu stavku (ABA).</para>
/// </summary>
public sealed class PlayTickets
{
    public const int Count = 16;

    public const int Free = 0;
    public const int Planned = 1;
    public const int Handed = 2;
    public const int Done = 3;

    public const int FlagStart = 1;
    public const int FlagTransition = 2;
    public const int FlagOutcome = 4;

    private readonly Ticket[] _tickets = new Ticket[Count];
    private long _eventSeq;

    public PlayTickets()
    {
        for (var i = 0; i < Count; i++)
        {
            _tickets[i] = new Ticket();
        }
    }

    public static int IndexOf(long handle) => (int)(handle & 0xFFFFFFFF);

    public static int GenerationOf(long handle) => (int)(handle >> 32);

    public static long Handle(int index, int generation) => ((long)generation << 32) | (uint)index;

    public int InUse
    {
        get
        {
            var n = 0;
            foreach (var t in _tickets)
            {
                n += Volatile.Read(ref t.State) != Free ? 1 : 0;
            }

            return n;
        }
    }

    // ---------------- koordinator ----------------

    /// <summary>Koordinator: SLOBODNA -> U PLANU (CompareExchange). Vraca rucku ili -1 ako nijedna nije slobodna.</summary>
    public long Plan(Guid playbackId, long trackNumber)
    {
        for (var i = 0; i < Count; i++)
        {
            var t = _tickets[i];
            if (Interlocked.CompareExchange(ref t.State, Planned, Free) == Free)
            {
                t.PlaybackId = playbackId;
                t.TrackNumber = trackNumber;
                t.Flags = 0;
                t.StartKind = 0;
                t.PeerPlaybackId = Guid.Empty;
                t.PeerTrack = 0;
                t.Emitted = 0;
                return Handle(i, t.Generation);
            }
        }

        return -1;
    }

    /// <summary>Koordinator: U PLANU -> PREDATA, neposredno pre objave u mestu za predaju (release).</summary>
    public void MarkHanded(long handle)
    {
        var t = Get(handle);
        if (t is not null)
        {
            Volatile.Write(ref t.State, Handed);
        }
    }

    /// <summary>Koordinator: posle uspesnog povlacenja (nit za mesanje karticu nikad nije dobila) -> ZAVRSENA.</summary>
    public void MarkWithdrawn(long handle)
    {
        var t = Get(handle);
        if (t is not null)
        {
            Volatile.Write(ref t.State, Done);
        }
    }

    /// <summary>Koordinator: ZAVRSENA -> SLOBODNA (generacija + 1). Samo kad su svi dogadjaji kartice prepisani.</summary>
    public bool Release(long handle)
    {
        var t = Get(handle);
        if (t is null || Volatile.Read(ref t.State) != Done)
        {
            return false;
        }

        t.Generation++;
        Volatile.Write(ref t.State, Free);
        return true;
    }

    public int StateOf(long handle) => Get(handle) is { } t ? Volatile.Read(ref t.State) : -1;

    /// <summary>Koordinator: novi dogadjaji kartice (acquire), u redosledu u kom ih je nit za mesanje upisala.</summary>
    public void Collect(long handle, List<TicketEvent> into)
    {
        var t = Get(handle);
        if (t is null)
        {
            return;
        }

        var flags = Volatile.Read(ref t.Flags);
        var fresh = flags & ~t.Emitted;
        if ((fresh & FlagStart) != 0)
        {
            into.Add(new TicketEvent(handle, FlagStart, t.StartSeq, t.PlaybackId, t.TrackNumber, t.StartFrame, t.StartKind == 2 ? JournalKind.Crossfade : JournalKind.Started, StopReason.None, 0, t.PeerPlaybackId, t.PeerTrack));
        }

        if ((fresh & FlagTransition) != 0)
        {
            into.Add(new TicketEvent(handle, FlagTransition, t.TransitionSeq, t.PlaybackId, t.TrackNumber, t.TransitionFrame, JournalKind.None, StopReason.None, 0, Guid.Empty, 0));
        }

        if ((fresh & FlagOutcome) != 0)
        {
            into.Add(new TicketEvent(handle, FlagOutcome, t.OutcomeSeq, t.PlaybackId, t.TrackNumber, t.OutcomeFrame, (StopReason)t.OutcomeReason == StopReason.None ? JournalKind.Ended : JournalKind.Stopped, (StopReason)t.OutcomeReason, t.OutcomePositionMs, Guid.Empty, 0));
        }

        t.Emitted |= fresh;
    }

    // ---------------- nit za mesanje ----------------

    /// <summary>Nit za mesanje: pesma krenula. Kod pretapanja i <c>playback_id</c> i broj pesme A (D8-2).</summary>
    public void WriteStart(long handle, long frame, bool crossfade, Guid peerPlaybackId, long peerTrack)
    {
        var t = Get(handle);
        if (t is null || Volatile.Read(ref t.State) != Handed)
        {
            return;
        }

        t.StartFrame = frame;
        t.StartKind = crossfade ? 2 : 1;
        t.PeerPlaybackId = peerPlaybackId;
        t.PeerTrack = peerTrack;
        t.StartSeq = ++_eventSeq;
        Volatile.Write(ref t.Flags, t.Flags | FlagStart);
    }

    /// <summary>Nit za mesanje: pretapanje pocelo, ova pesma je A (stigla do tacke prelaza).</summary>
    public void WriteTransition(long handle, long frame)
    {
        var t = Get(handle);
        if (t is null || Volatile.Read(ref t.State) != Handed)
        {
            return;
        }

        t.TransitionFrame = frame;
        t.TransitionSeq = ++_eventSeq;
        Volatile.Write(ref t.Flags, t.Flags | FlagTransition);
    }

    /// <summary>Nit za mesanje: ishod (poslednji upis u karticu), pa PREDATA -> ZAVRSENA (release). Posle ovoga je ne dira.</summary>
    public void WriteOutcome(long handle, long frame, StopReason reason, int positionMs)
    {
        var t = Get(handle);
        if (t is null || Volatile.Read(ref t.State) != Handed)
        {
            return;
        }

        t.OutcomeFrame = frame;
        t.OutcomeReason = (int)reason;
        t.OutcomePositionMs = positionMs;
        t.OutcomeSeq = ++_eventSeq;
        Volatile.Write(ref t.Flags, t.Flags | FlagOutcome);
        Volatile.Write(ref t.State, Done);
    }

    public Guid PlaybackIdOf(long handle) => Get(handle)?.PlaybackId ?? Guid.Empty;

    public long TrackOf(long handle) => Get(handle)?.TrackNumber ?? 0;

    private Ticket? Get(long handle)
    {
        if (handle < 0)
        {
            return null;
        }

        var index = IndexOf(handle);
        if (index is < 0 or >= Count)
        {
            return null;
        }

        var t = _tickets[index];
        return t.Generation == GenerationOf(handle) ? t : null; // stara rucka se odbacuje
    }

    private sealed class Ticket
    {
        public int State;
        public int Generation;
        public int Flags;
        public int Emitted;
        public Guid PlaybackId;
        public long TrackNumber;
        public long StartFrame;
        public int StartKind;
        public long StartSeq;
        public Guid PeerPlaybackId;
        public long PeerTrack;
        public long TransitionFrame;
        public long TransitionSeq;
        public long OutcomeFrame;
        public int OutcomeReason;
        public int OutcomePositionMs;
        public long OutcomeSeq;
    }
}

/// <summary>Jedan dogadjaj procitan iz kartice (koordinator ga upisuje u crnu kutiju).</summary>
public readonly record struct TicketEvent(long Handle, int Flag, long Seq, Guid PlaybackId, long Track, long Frame, JournalKind Kind, StopReason Reason, int PositionMs, Guid PeerPlaybackId, long PeerTrack);
