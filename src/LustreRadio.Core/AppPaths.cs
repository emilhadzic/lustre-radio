namespace LustreRadio.Core;

/// <summary>
/// Mesta na disku gde LUSTRE RADIO drzi svoje podatke (spec. 2.5).
/// Muzika se ovde ne cuva; ona ostaje u folderima koje izabere administrator.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "LustreRadio";
    public const string DatabaseFileName = "lustre.db";

    public const string Logs = "logs";
    public const string Backups = "backups";
    public const string Licenses = "licenses";
    public const string Voice = "voice";
    public const string Fallback = "fallback";
    public const string Prepared = "prepared";
    public const string Incoming = "incoming";

    /// <summary>Svi podfolderi koje program pravi unutar korena podataka.</summary>
    public static IReadOnlyList<string> SubFolders { get; } =
        [Logs, Backups, Licenses, Voice, Fallback, Prepared, Incoming];

    /// <summary>Podrazumevani koren podataka: C:\ProgramData\LustreRadio na Windowsu.</summary>
    public static string DefaultDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName);

    public static string DatabasePath(string dataRoot) => Path.Combine(dataRoot, DatabaseFileName);

    public static string SubFolder(string dataRoot, string name)
    {
        if (!SubFolders.Contains(name))
        {
            throw new ArgumentException($"Nepoznat podfolder: {name}", nameof(name));
        }

        return Path.Combine(dataRoot, name);
    }
}
