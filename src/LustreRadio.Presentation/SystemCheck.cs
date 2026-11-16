using LustreRadio.Ipc;

namespace LustreRadio.Presentation;

/// <summary>Ono sto interfejs sam izmeri na racunaru (ostalo stize od audio procesa).</summary>
public sealed record LocalProbes
{
    /// <summary>null = zadatak ne postoji; prazno = ispravan; inace spisak problema.</summary>
    public IReadOnlyList<string>? AutostartProblems { get; init; }

    public double? FreeDiskGb { get; init; }
}

public sealed record CheckLine(string Name, string Detail, Health Health);

/// <summary>Ekran "Provera sistema": samo provere koje program zaista ume da uradi.</summary>
public static class SystemCheck
{
    public const double LowDiskGb = 5;

    public static IReadOnlyList<CheckLine> Evaluate(RadioState? state, LinkStatus link, LocalProbes probes, string? language)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var lines = new List<CheckLine>();
        string Name(T key) => Texts.Get(language, key);
        var ok = Texts.Get(language, T.CheckOk);
        var connected = link == LinkStatus.Connected && state is not null;

        lines.Add(connected
            ? new(Name(T.CheckEngine), ok + " · " + Texts.Format(language, T.CheckVersion, state!.EngineVersion ?? "?"), Health.Good)
            : new(Name(T.CheckEngine), Texts.Get(language, link == LinkStatus.VersionMismatch ? T.VersionMismatch : T.CheckEngineDown), Health.Problem));

        if (connected)
        {
            var s = state!;
            lines.Add(s.Output == "Disconnected" || (string.IsNullOrEmpty(s.Device) && s.OnAir)
                ? new(Name(T.CheckSpeaker), Texts.Get(language, T.CheckSpeakerMissing), Health.Problem)
                : new(Name(T.CheckSpeaker), string.IsNullOrEmpty(s.Device) ? ok : s.Device!, Health.Good));

            lines.Add(new(
                Name(T.CheckTracks),
                s.BroadcastableTracks == 0
                    ? Texts.Get(language, T.CheckNoTracks)
                    : Texts.Format(language, T.TracksSummary, s.TotalTracks, s.BroadcastableTracks),
                s.BroadcastableTracks == 0 ? Health.Problem : Health.Good));

            lines.Add(s.ClosesAtLocal is { } closes
                ? new(Name(T.CheckSchedule), Texts.Format(language, T.CheckOpenUntil, Texts.Time(closes)), Health.Good)
                : s.OpensAtLocal is { } opens
                    ? new(Name(T.CheckSchedule), Texts.Format(language, T.CheckClosedUntil, Texts.DayForSentence(language, opens), Texts.Time(opens)), Health.Good)
                    : new(Name(T.CheckSchedule), ok, Health.Good));

            lines.Add(s.PinIsSet
                ? new(Name(T.CheckPin), ok, Health.Good)
                : new(Name(T.CheckPin), Texts.Get(language, T.CheckPinMissing), Health.Problem));

            lines.Add(s.SafeMode
                ? new(Name(T.CheckSafeMode), Texts.Get(language, T.CheckSafeModeOn), Health.Attention)
                : new(Name(T.CheckSafeMode), ok, Health.Good));

            lines.Add(s.DatabaseMode == RadioState.DatabaseNotUpgraded
                ? new(Name(T.CheckDatabase), Texts.Format(language, T.CheckDatabaseNotUpgraded, s.DatabaseModeReason ?? "?"), Health.Problem)
                : new(Name(T.CheckDatabase), ok, Health.Good));
        }

        lines.Add(probes.AutostartProblems switch
        {
            null => new(Name(T.CheckAutostart), Texts.Get(language, T.CheckAutostartMissing), Health.Attention),
            { Count: 0 } => new(Name(T.CheckAutostart), ok, Health.Good),
            var problems => new(Name(T.CheckAutostart), Texts.Format(language, T.CheckAutostartWrong, string.Join("; ", problems)), Health.Problem),
        });

        if (probes.FreeDiskGb is { } free)
        {
            var text = free.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            lines.Add(free < LowDiskGb
                ? new(Name(T.CheckDisk), Texts.Format(language, T.CheckDiskLow, text), Health.Attention)
                : new(Name(T.CheckDisk), Texts.Format(language, T.CheckDiskFree, text), Health.Good));
        }

        return lines;
    }
}
