namespace LustreRadio.Presentation;

/// <summary>Svi tekstovi interfejsa. Svaki kljuc mora imati srpski i engleski tekst (T-L10N-01).</summary>
public enum T
{
    AppName,
    Connecting,
    VersionMismatch,
    NothingPlaying,
    UnknownArtist,
    Play,
    Pause,
    Next,
    Volume,
    Admin,
    StatusPlaying,
    StatusPaused,
    StatusClosed,
    StatusClosedOpensAt,
    StatusNoTracks,
    StatusNoSpeaker,
    StatusStarting,
    StatusSafeMode,
    ClosesAt,
    InHoursMinutes,
    InMinutes,
    PartMorning,
    PartAfternoon,
    PartEvening,
    Program,
    PinEnter,
    PinSetNew,
    PinRepeat,
    PinMismatch,
    PinWrong,
    PinLocked,
    PinRules,
    PinChanged,
    Ok,
    Cancel,
    Delete,
    Lock,
    Back,
    AdminTitle,
    SectionLibrary,
    SectionVolume,
    SectionLanguage,
    SectionPin,
    SectionSystem,
    MusicFolders,
    NoFolders,
    AddFolder,
    RemoveFolder,
    Rescan,
    Scanning,
    TracksSummary,
    FolderNotFound,
    VolumeMin,
    VolumeMax,
    VolumeRangeHelp,
    LanguageStaff,
    LanguageAdmin,
    Serbian,
    English,
    ChangePin,
    CommandFailed,
    SessionExpired,
    CheckEngine,
    CheckSpeaker,
    CheckTracks,
    CheckPin,
    CheckAutostart,
    CheckDisk,
    CheckSafeMode,
    CheckSchedule,
    CheckOk,
    CheckEngineDown,
    CheckSpeakerMissing,
    CheckNoTracks,
    CheckPinMissing,
    CheckAutostartMissing,
    CheckAutostartWrong,
    CheckDiskLow,
    CheckDiskFree,
    CheckSafeModeOn,
    CheckDatabase,
    CheckDatabaseNotUpgraded,
    CheckHistory,
    CheckHistoryNoBlackBox,
    CheckHistoryNotSaved,
    CheckHistoryLost,
    CheckHistoryLowSpace,
    CheckHistoryInvalidLines,
    DatabaseNotUpgradedBlocked,
    CheckOpenUntil,
    CheckClosedUntil,
    CheckVersion,
    Refresh,
    SectionLicense,
    LicenseExplain,
    LicenseDeclare,
    LicenseDeclared,
    LicenseMissing,
}

public static class Texts
{
    public const string Serbian = "sr";
    public const string English = "en";

    private static readonly Dictionary<T, (string Sr, string En)> Table = new()
    {
        [T.AppName] = ("LUSTRE RADIO", "LUSTRE RADIO"),
        [T.Connecting] = ("Povezivanje…", "Connecting…"),
        [T.VersionMismatch] = ("Program i audio deo nisu iste verzije. Ponovo instalirajte LUSTRE RADIO.", "The app and the audio part are different versions. Reinstall LUSTRE RADIO."),
        [T.NothingPlaying] = ("Trenutno ništa ne svira", "Nothing is playing"),
        [T.UnknownArtist] = ("Nepoznat izvođač", "Unknown artist"),
        [T.Play] = ("Pusti", "Play"),
        [T.Pause] = ("Pauza", "Pause"),
        [T.Next] = ("Sledeća", "Next"),
        [T.Volume] = ("Glasnoća", "Volume"),
        [T.Admin] = ("Admin", "Admin"),
        [T.StatusPlaying] = ("Svira", "On air"),
        [T.StatusPaused] = ("Pauza – muzika se sama nastavlja posle 15 minuta", "Paused – music resumes by itself after 15 minutes"),
        [T.StatusClosed] = ("Van radnog vremena", "Outside opening hours"),
        [T.StatusClosedOpensAt] = ("Van radnog vremena – muzika kreće {0} u {1}", "Outside opening hours – music starts {0} at {1}"),
        [T.StatusNoTracks] = ("Nema pesama koje smeju da se puštaju", "No tracks are cleared for playing"),
        [T.StatusNoSpeaker] = ("Zvučnik nije povezan – čeka se", "Speaker not connected – waiting"),
        [T.StatusStarting] = ("Pokretanje…", "Starting…"),
        [T.StatusSafeMode] = ("Sigurni režim", "Safe mode"),
        [T.ClosesAt] = ("Gašenje u {0} ({1})", "Closes at {0} ({1})"),
        [T.InHoursMinutes] = ("za {0} h {1} min", "in {0} h {1} min"),
        [T.InMinutes] = ("za {0} min", "in {0} min"),
        [T.PartMorning] = ("Jutro", "Morning"),
        [T.PartAfternoon] = ("Popodne", "Afternoon"),
        [T.PartEvening] = ("Veče", "Evening"),
        [T.Program] = ("Program", "Programme"),
        [T.PinEnter] = ("Unesite PIN", "Enter PIN"),
        [T.PinSetNew] = ("Postavite novi PIN", "Set a new PIN"),
        [T.PinRepeat] = ("Ponovite novi PIN", "Repeat the new PIN"),
        [T.PinMismatch] = ("PIN-ovi se ne poklapaju. Pokušajte ponovo.", "The PINs do not match. Try again."),
        [T.PinWrong] = ("Pogrešan PIN", "Wrong PIN"),
        [T.PinLocked] = ("Previše pogrešnih pokušaja. Pokušajte za {0} min.", "Too many wrong attempts. Try again in {0} min."),
        [T.PinRules] = ("4 do 8 cifara", "4 to 8 digits"),
        [T.PinChanged] = ("PIN je promenjen", "PIN changed"),
        [T.Ok] = ("Potvrdi", "OK"),
        [T.Cancel] = ("Odustani", "Cancel"),
        [T.Delete] = ("Obriši", "Delete"),
        [T.Lock] = ("Zaključaj", "Lock"),
        [T.Back] = ("Nazad", "Back"),
        [T.AdminTitle] = ("Podešavanja", "Settings"),
        [T.SectionLibrary] = ("Muzika", "Music"),
        [T.SectionVolume] = ("Glasnoća", "Volume"),
        [T.SectionLanguage] = ("Jezik", "Language"),
        [T.SectionPin] = ("PIN", "PIN"),
        [T.SectionSystem] = ("Provera sistema", "System check"),
        [T.MusicFolders] = ("Folderi sa muzikom", "Music folders"),
        [T.NoFolders] = ("Nijedan folder nije dodat", "No folder added yet"),
        [T.AddFolder] = ("Dodaj folder", "Add folder"),
        [T.RemoveFolder] = ("Ukloni", "Remove"),
        [T.Rescan] = ("Skeniraj ponovo", "Scan again"),
        [T.Scanning] = ("Skeniranje u toku…", "Scanning…"),
        [T.TracksSummary] = ("Pesama u biblioteci: {0}; sme da se pušta: {1}", "Tracks in library: {0}; cleared for playing: {1}"),
        [T.FolderNotFound] = ("Folder ne postoji: {0}", "Folder not found: {0}"),
        [T.VolumeMin] = ("Najtiše što zaposleni mogu", "Quietest staff can set"),
        [T.VolumeMax] = ("Najglasnije što zaposleni mogu", "Loudest staff can set"),
        [T.VolumeRangeHelp] = ("Zaposleni menjaju glasnoću samo između ove dve granice.", "Staff can change the volume only between these two limits."),
        [T.LanguageStaff] = ("Ekran za zaposlene", "Staff screen"),
        [T.LanguageAdmin] = ("Podešavanja", "Settings"),
        [T.Serbian] = ("Srpski", "Serbian"),
        [T.English] = ("Engleski", "English"),
        [T.ChangePin] = ("Promeni PIN", "Change PIN"),
        [T.CommandFailed] = ("Nije uspelo. Pokušajte ponovo.", "That did not work. Try again."),
        [T.SessionExpired] = ("Podešavanja su zaključana. Unesite PIN ponovo.", "Settings are locked. Enter the PIN again."),
        [T.CheckEngine] = ("Audio deo programa", "Audio part of the app"),
        [T.CheckSpeaker] = ("Zvučnik", "Speaker"),
        [T.CheckTracks] = ("Muzika", "Music"),
        [T.CheckPin] = ("PIN za podešavanja", "Settings PIN"),
        [T.CheckAutostart] = ("Automatsko pokretanje", "Automatic start"),
        [T.CheckDisk] = ("Slobodan prostor na disku", "Free disk space"),
        [T.CheckSafeMode] = ("Režim rada", "Running mode"),
        [T.CheckSchedule] = ("Radno vreme", "Opening hours"),
        [T.CheckOk] = ("U redu", "OK"),
        [T.CheckEngineDown] = ("Ne odgovara", "Not responding"),
        [T.CheckSpeakerMissing] = ("Nije povezan", "Not connected"),
        [T.CheckNoTracks] = ("Nema pesama koje smeju da se puštaju", "No tracks cleared for playing"),
        [T.CheckPinMissing] = ("Nije postavljen", "Not set"),
        [T.CheckAutostartMissing] = ("Nije podešeno (radi tek posle instalacije)", "Not set up (works only after installation)"),
        [T.CheckAutostartWrong] = ("Podešavanje nije ispravno: {0}", "Setting is wrong: {0}"),
        [T.CheckDiskLow] = ("Malo prostora: {0} GB", "Low space: {0} GB"),
        [T.CheckDiskFree] = ("{0} GB slobodno", "{0} GB free"),
        [T.CheckSafeModeOn] = ("Sigurni režim (program je više puta pao)", "Safe mode (the app crashed several times)"),
        [T.CheckDatabase] = ("Baza", "Database"),
        [T.CheckHistory] = ("Istorija", "History"),
        [T.CheckHistoryNoBlackBox] = ("Crna kutija nedostupna: pad programa može izbrisati istoriju", "Black box unavailable: a program crash may erase history"),
        [T.CheckHistoryNotSaved] = ("Istorija se ne čuva na disk", "History is not being saved to disk"),
        [T.CheckHistoryLost] = ("Istorija izgubljena: {0} događaja od {1} do {2}", "History lost: {0} events from {1} to {2}"),
        [T.CheckHistoryLowSpace] = ("Na disku je manje od 200 MB; istorija uskoro neće moći da se čuva", "Less than 200 MB free on disk; history will soon not be saved"),
        [T.CheckHistoryInvalidLines] = ("Dnevnik emitovanja ima {0} neispravnih redova (preskočeni)", "The broadcast journal has {0} invalid lines (skipped)"),
        [T.CheckDatabaseNotUpgraded] = ("Baza nije nadograđena (šema 1). Radio svira. Izmene podešavanja i biblioteke su isključene. Razlog: {0}", "Database not upgraded (schema 1). The radio plays. Changes to settings and library are disabled. Reason: {0}"),
        [T.DatabaseNotUpgradedBlocked] = ("Baza nije nadograđena; izmene nisu moguće.", "Database not upgraded; changes are not possible."),
        [T.CheckOpenUntil] = ("Otvoreno do {0}", "Open until {0}"),
        [T.CheckClosedUntil] = ("Zatvoreno; otvara se {0} u {1}", "Closed; opens {0} at {1}"),
        [T.CheckVersion] = ("Verzija {0}", "Version {0}"),
        [T.Refresh] = ("Proveri ponovo", "Check again"),
        [T.SectionLicense] = ("Licenca", "Licence"),
        [T.LicenseExplain] = (
            "Muzika sme da se pušta tek kad vlasnik izjavi da plaća naknadu organizacijama SOKOJ i OFPS. Program beleži datum izjave. Za tačnost izjave odgovara vlasnik.",
            "Music may play only after the owner declares that fees are paid to SOKOJ and OFPS. The app records the date. The owner is responsible for the declaration being true."),
        [T.LicenseDeclare] = ("Izjavljujem da plaćam SOKOJ i OFPS", "I declare that I pay SOKOJ and OFPS"),
        [T.LicenseDeclared] = ("Izjava je data: {0}, {1}", "Declaration recorded: {0}, {1}"),
        [T.LicenseMissing] = ("Izjava još nije data – pesme ne smeju u etar.", "No declaration yet – tracks may not be played."),
    };

    private static readonly string[] DaysSr = ["nedelja", "ponedeljak", "utorak", "sreda", "četvrtak", "petak", "subota"];
    private static readonly string[] DaysEn = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] MonthsSr = ["januar", "februar", "mart", "april", "maj", "jun", "jul", "avgust", "septembar", "oktobar", "novembar", "decembar"];
    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static IReadOnlyCollection<T> Keys => Table.Keys;

    public static string Normalize(string? language) => language == English ? English : Serbian;

    public static string Get(string? language, T key)
    {
        var (sr, en) = Table[key];
        return Normalize(language) == English ? en : sr;
    }

    public static string Format(string? language, T key, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(language, key), args);

    public static string DayName(string? language, DayOfWeek day) =>
        (Normalize(language) == English ? DaysEn : DaysSr)[(int)day];

    /// <summary>"Subota, 14. novembar 2026." / "Saturday, 14 November 2026"</summary>
    public static string LongDate(string? language, DateTime date)
    {
        if (Normalize(language) == English)
        {
            return $"{DaysEn[(int)date.DayOfWeek]}, {date.Day} {MonthsEn[date.Month - 1]} {date.Year}";
        }

        var day = DaysSr[(int)date.DayOfWeek];
        return $"{char.ToUpperInvariant(day[0])}{day[1..]}, {date.Day}. {MonthsSr[date.Month - 1]} {date.Year}.";
    }

    /// <summary>"danas" se ne koristi: uvek naziv dana, da nema zabune oko ponoci.</summary>
    public static string DayForSentence(string? language, DateTime date)
    {
        if (Normalize(language) == English)
        {
            return "on " + DaysEn[(int)date.DayOfWeek];
        }

        return date.DayOfWeek switch
        {
            DayOfWeek.Sunday => "u nedelju",
            DayOfWeek.Monday => "u ponedeljak",
            DayOfWeek.Tuesday => "u utorak",
            DayOfWeek.Wednesday => "u sredu",
            DayOfWeek.Thursday => "u četvrtak",
            DayOfWeek.Friday => "u petak",
            _ => "u subotu",
        };
    }

    public static string Time(DateTime time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
